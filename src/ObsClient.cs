using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RaceMode
{
    // What OBS reported last. Frame counters are OBS's running totals since its output started.
    public class ObsStats
    {
        public bool Connected, Streaming, Reconnecting;
        public string Status = "OBS stats are off";       // plain line for the UI
        public double Cpu = double.NaN, MemoryMB = double.NaN, ActiveFps = double.NaN, Congestion = double.NaN, Kbps = double.NaN;
        public long RenderSkipped, RenderTotal, EncodeSkipped, EncodeTotal, NetDropped, NetTotal;
        public DateTime Time;
        public ObsStats Clone() { return (ObsStats)MemberwiseClone(); }
    }

    // OBS Studio integration, part 2: reads stream stats over OBS's remote control (obs-websocket v5),
    // on 127.0.0.1 only. One background thread, only while stats are switched on (Stream mode).
    // Login is OBS's challenge-response (the password itself is never sent), read from OBS's own settings file.
    public static partial class Obs
    {
        static readonly object _lock = new object();
        static ObsStats _latest = new ObsStats();
        static Thread _thread;
        static volatile bool _run;
        public static string Host = "127.0.0.1";       // tests point this and Port at a fake server
        public static int PortOverride;                  // 0 = the port in OBS's settings
        public static Func<string> PasswordSource = ReadPassword;
        public static Func<bool> IsRunning = Running;

        public static ObsStats Latest { get { lock (_lock) return _latest.Clone(); } }
        static void Set(ObsStats s) { s.Time = DateTime.Now; lock (_lock) _latest = s; }
        static void SetStatus(string status) { var s = new ObsStats { Status = status }; Set(s); }

        public static bool Active { get { return _run; } }

        public static void Start()
        {
            if (_run) return;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "RaceMode.Obs", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public static void Stop()
        {
            _run = false;
            SetStatus("OBS stats are off");
        }

        static void Loop()
        {
            while (_run)
            {
                int wait = 5000;
                try
                {
                    if (!IsRunning()) { SetStatus("OBS isn't open"); }
                    else
                    {
                        var ws = ReadSettings();
                        if (PortOverride == 0 && !ws.Enabled) SetStatus("OBS's remote control is off, so Race Mode can't see stream stats");
                        else { Session(PortOverride != 0 ? PortOverride : ws.Port); wait = 3000; }
                    }
                }
                catch (ObsAuthException ex) { SetStatus(ex.Message); wait = 30000; }
                catch (Exception ex) { SetStatus("Couldn't reach OBS (" + Short(ex) + ")"); }
                for (int i = 0; i < wait / 250 && _run; i++) Thread.Sleep(250);
            }
        }

        static string Short(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex.Message; }

        class ObsAuthException : Exception { public ObsAuthException(string m) : base(m) { } }

        // One connection: hello -> identify -> poll GetStats + GetStreamStatus every 2 s until OBS goes away or stats are switched off.
        static void Session(int port)
        {
            using (var ws = new ClientWebSocket())
            {
                ws.Options.AddSubProtocol("obswebsocket.json");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var cts = new CancellationTokenSource(5000))
                    ws.ConnectAsync(new Uri("ws://" + Host + ":" + port.ToString(CultureInfo.InvariantCulture)), cts.Token).Wait();

                var hello = Receive(ws, 5000);
                if (Op(hello) != 0) throw new IOException("unexpected first message");
                var d = Dict(hello, "d");
                var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 0 } };
                var auth = Dict(d, "authentication");
                if (auth != null)
                {
                    var pw = PasswordSource();
                    if (string.IsNullOrEmpty(pw)) throw new ObsAuthException("OBS asks for a password, and Race Mode couldn't find it in OBS's settings");
                    identify["authentication"] = AuthString(pw, Str(auth, "salt"), Str(auth, "challenge"));
                }
                Send(ws, new Dictionary<string, object> { { "op", 1 }, { "d", identify } });
                var identified = Receive(ws, 5000);
                if (identified == null || Op(identified) != 2) throw new ObsAuthException("OBS didn't accept Race Mode's login. If you changed OBS's remote control password, restart Race Mode");

                long lastBytes = -1; DateTime lastAt = DateTime.MinValue; int id = 0;
                while (_run && ws.State == WebSocketState.Open)
                {
                    var stats = Request(ws, "GetStats", ++id);
                    var stream = Request(ws, "GetStreamStatus", ++id);
                    var s = new ObsStats { Connected = true };
                    if (stats != null)
                    {
                        s.Cpu = Num(stats, "cpuUsage"); s.MemoryMB = Num(stats, "memoryUsage"); s.ActiveFps = Num(stats, "activeFps");
                        s.RenderSkipped = (long)Num0(stats, "renderSkippedFrames"); s.RenderTotal = (long)Num0(stats, "renderTotalFrames");
                        s.EncodeSkipped = (long)Num0(stats, "outputSkippedFrames"); s.EncodeTotal = (long)Num0(stats, "outputTotalFrames");
                    }
                    if (stream != null)
                    {
                        s.Streaming = Bool(stream, "outputActive"); s.Reconnecting = Bool(stream, "outputReconnecting");
                        s.Congestion = Num(stream, "outputCongestion");
                        s.NetDropped = (long)Num0(stream, "outputSkippedFrames"); s.NetTotal = (long)Num0(stream, "outputTotalFrames");
                        var bytes = (long)Num0(stream, "outputBytes");
                        var now = DateTime.UtcNow;
                        if (s.Streaming && lastBytes >= 0 && bytes >= lastBytes && now > lastAt)
                            s.Kbps = (bytes - lastBytes) * 8.0 / 1000.0 / (now - lastAt).TotalSeconds;
                        lastBytes = s.Streaming ? bytes : -1; lastAt = now;
                    }
                    s.Status = !s.Streaming ? "OBS is connected, not streaming" : s.Reconnecting ? "OBS is reconnecting to the stream" : "Streaming";
                    Set(s);
                    for (int i = 0; i < 8 && _run; i++) Thread.Sleep(250);
                }
                if (ws.State == WebSocketState.Open)
                    try { using (var c = new CancellationTokenSource(2000)) ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", c.Token).Wait(); } catch { }
            }
        }

        // obs-websocket v5: secret = base64(sha256(password + salt)); auth = base64(sha256(secret + challenge)).
        public static string AuthString(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        static Dictionary<string, object> Request(ClientWebSocket ws, string type, int id)
        {
            var rid = type + "-" + id.ToString(CultureInfo.InvariantCulture);
            Send(ws, new Dictionary<string, object> { { "op", 6 }, { "d", new Dictionary<string, object> { { "requestType", type }, { "requestId", rid } } } });
            for (int tries = 0; tries < 10; tries++)
            {
                var msg = Receive(ws, 5000);
                if (msg == null) return null;
                if (Op(msg) != 7) continue;                       // an event or something else: skip
                var d = Dict(msg, "d");
                if (d == null || Str(d, "requestId") != rid) continue;
                var st = Dict(d, "requestStatus");
                if (st == null || !Bool(st, "result")) return null;
                return Dict(d, "responseData");
            }
            return null;
        }

        static void Send(ClientWebSocket ws, object msg)
        {
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(msg));
            using (var cts = new CancellationTokenSource(5000))
                ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).Wait();
        }

        const int MaxMessage = 256 * 1024;   // OBS's replies are a few hundred bytes; anything huge is refused

        static Dictionary<string, object> Receive(ClientWebSocket ws, int timeoutMs)
        {
            var buf = new byte[8192];
            using (var ms = new MemoryStream())
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                while (true)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result;
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > MaxMessage) throw new IOException("OBS sent a message that's too big");
                    if (r.EndOfMessage) break;
                }
                try { return new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(ms.ToArray())) as Dictionary<string, object>; }
                catch { return new Dictionary<string, object>(); }   // not JSON: ignored by the caller (no op)
            }
        }

        static int Op(Dictionary<string, object> m) { object v; return m != null && m.TryGetValue("op", out v) && v is int ? (int)v : -1; }
        static Dictionary<string, object> Dict(Dictionary<string, object> m, string k) { object v; return m != null && m.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static string Str(Dictionary<string, object> m, string k) { object v; return m != null && m.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static bool Bool(Dictionary<string, object> m, string k) { object v; return m != null && m.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> m, string k)
        {
            object v;
            if (m == null || !m.TryGetValue(k, out v) || v == null) return double.NaN;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return double.NaN; }
        }
        static double Num0(Dictionary<string, object> m, string k) { var d = Num(m, k); return double.IsNaN(d) || d < 0 ? 0 : d; }
    }
}
