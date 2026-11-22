// iRacing telemetry (irsdk) reader - frame rate and the sim's own CPU/GPU load.
//
// iRacing publishes live telemetry in a read-only shared memory file ("Local\IRSDKMemMapFileName"), the
// official SDK interface every overlay uses. Race Mode only maps it for reading: no hooks, no injection,
// no handle to the sim. It's opened only while the sim runs and closed as soon as it exits.
//
// Layout (irsdk_defines.h):
//   header  int ver@0, status@4 (bit 0 = connected), tickRate@8, sessionInfoUpdate@12, sessionInfoLen@16,
//           sessionInfoOffset@20, numVars@24, varHeaderOffset@28, numBuf@32, bufLen@36, pad[2],
//           varBuf[4]@48, 16 bytes each: tickCount@+0, bufOffset@+4, pad[2]
//   var header (144 bytes each, at varHeaderOffset + i*144): type@0, offset@4, count@8, countAsTime@12,
//           name char[32]@16, desc char[64]@48, unit char[32]@112
//   types: 0 char, 1 bool, 2 int, 3 bitfield, 4 float, 5 double
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace RaceMode
{
    public class IrsdkSample
    {
        public bool Connected;
        public double Fps = double.NaN, CpuFg = double.NaN, CpuBg = double.NaN, Gpu = double.NaN;
        public string Context = "";   // drive | replay | other ("" when not connected)
    }

    public sealed class Irsdk : IDisposable
    {
        public static string MapName = @"Local\IRSDKMemMapFileName";
        public static bool ForceOpen;            // tests: read a fake map even though no sim is running

        const int HeaderSize = 112, VarHeaderSize = 144, MaxBufs = 4;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        class Var { public int Type, Offset; public string Unit; public bool Percent; }

        MemoryMappedFile _mmf;
        MemoryMappedViewAccessor _view;
        int _pid = -1, _numVars = -1, _varHeaderOffset = -1;
        readonly Dictionary<string, Var> _vars = new Dictionary<string, Var>(StringComparer.Ordinal);
        bool _announced;

        static readonly string[] Wanted = { "FrameRate", "CpuUsageFG", "CpuUsageBG", "GpuUsage", "IsOnTrack", "IsReplayPlaying" };

        // Called from the sampler thread once per sample. Returns null when there's nothing to read.
        // logEvent receives one-off notes (connected, units); it must not throw.
        public IrsdkSample Read(bool simRunning, int simPid, Action<string> logEvent)
        {
            if (!simRunning && !ForceOpen) { Close(); return null; }
            if (simPid != _pid) Close();                    // sim restarted: its map is a new one
            _pid = simPid;
            try
            {
                if (_view == null && !Open()) return null;
                var s = new IrsdkSample();
                int status = _view.ReadInt32(4);
                if ((status & 1) == 0) return s;            // loading or between sessions: no values yet

                int numVars = _view.ReadInt32(24), varHeaderOffset = _view.ReadInt32(28);
                if (numVars != _numVars || varHeaderOffset != _varHeaderOffset) Resolve(numVars, varHeaderOffset, logEvent);

                int numBuf = Math.Max(1, Math.Min(MaxBufs, _view.ReadInt32(32)));
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    // Newest buffer = highest tickCount. iRacing writes the buffers round-robin at 60 Hz.
                    int best = 0, bestTick = int.MinValue;
                    for (int i = 0; i < numBuf; i++) { int t = _view.ReadInt32(48 + i * 16); if (t > bestTick) { bestTick = t; best = i; } }
                    if (bestTick < 0) continue;              // tick counts are never negative: a buffer is being rewritten
                    int bufOffset = _view.ReadInt32(48 + best * 16 + 4);
                    Thread.MemoryBarrier();                 // values must be read after the tick, and the re-check after them

                    s.Fps = Value("FrameRate", bufOffset);
                    s.CpuFg = Value("CpuUsageFG", bufOffset);
                    s.CpuBg = Value("CpuUsageBG", bufOffset);
                    s.Gpu = Value("GpuUsage", bufOffset);
                    double onTrack = Value("IsOnTrack", bufOffset), replay = Value("IsReplayPlaying", bufOffset);

                    // Torn read: the sim rewrote this buffer while we read it. Try again with the new newest one.
                    Thread.MemoryBarrier();
                    if (_view.ReadInt32(48 + best * 16) != bestTick) continue;

                    s.Connected = true;
                    s.Context = replay > 0 ? "replay" : onTrack > 0 ? "drive" : "other";
                    return s;
                }
                return new IrsdkSample();                   // kept changing under us: skip this sample
            }
            catch (Exception ex)
            {
                // The sim crashed or closed with the view open: drop it, reopen on a later sample.
                Close();
                if (logEvent != null) logEvent("iRacing telemetry read failed, will retry: " + ex.Message);
                return null;
            }
        }

        bool Open()
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _numVars = -1; _varHeaderOffset = -1; _vars.Clear(); _announced = false;
                return _view.Capacity >= HeaderSize;
            }
            catch { Close(); return false; }                // not there yet (sim still starting): try next sample
        }

        void Resolve(int numVars, int varHeaderOffset, Action<string> logEvent)
        {
            _vars.Clear();
            _numVars = numVars; _varHeaderOffset = varHeaderOffset;
            if (numVars <= 0 || numVars > 10000 || varHeaderOffset < HeaderSize) return;
            if ((long)varHeaderOffset + (long)numVars * VarHeaderSize > _view.Capacity) return;
            for (int i = 0; i < numVars; i++)
            {
                long h = varHeaderOffset + (long)i * VarHeaderSize;
                var name = Str(h + 16, 32);
                if (Array.IndexOf(Wanted, name) < 0) continue;
                var unit = Str(h + 112, 32);
                _vars[name] = new Var { Type = _view.ReadInt32(h), Offset = _view.ReadInt32(h + 4), Unit = unit, Percent = unit == "%" };
            }
            if (!_announced && logEvent != null)
            {
                _announced = true;
                var parts = new List<string>();
                foreach (var w in Wanted) { Var v; parts.Add(w + (_vars.TryGetValue(w, out v) ? " (" + (v.Unit.Length > 0 ? v.Unit : "no unit") + ")" : " missing")); }
                logEvent("iRacing telemetry connected: " + string.Join(", ", parts.ToArray()));
            }
        }

        double Value(string name, int bufOffset)
        {
            Var v;
            if (!_vars.TryGetValue(name, out v)) return double.NaN;
            long at = (long)bufOffset + v.Offset;
            if (at < 0 || at + 8 > _view.Capacity) return double.NaN;
            double d;
            switch (v.Type)
            {
                case 1: d = _view.ReadByte(at) != 0 ? 1 : 0; break;
                case 2: case 3: d = _view.ReadInt32(at); break;
                case 4: d = _view.ReadSingle(at); break;
                case 5: d = _view.ReadDouble(at); break;
                default: return double.NaN;
            }
            if (double.IsNaN(d) || double.IsInfinity(d)) return double.NaN;
            // iRacing marks these "%" but stores a fraction of the available time, and it can exceed 1.0
            // (CpuUsageFG has read 9.1 during a loading screen). So always scale, never guess.
            if (v.Percent) d *= 100.0;
            return d;
        }

        string Str(long at, int len)
        {
            var b = new byte[len];
            _view.ReadArray(at, b, 0, len);
            int n = Array.IndexOf(b, (byte)0); if (n < 0) n = len;
            return Encoding.ASCII.GetString(b, 0, n);
        }

        void Close()
        {
            try { if (_view != null) _view.Dispose(); } catch { }
            try { if (_mmf != null) _mmf.Dispose(); } catch { }
            _view = null; _mmf = null; _pid = -1;
        }

        public void Dispose() { Close(); }
    }
}
