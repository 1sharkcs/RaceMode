// Tests for ObsClient.cs (obs-websocket v5 login and stats) against a FAKE OBS server, and ObsDock.cs (the dock's rules).
// Nothing talks to the real OBS. Build next to RaceMode.exe:
//   csc -r:RaceMode.exe -r:System.Web.Extensions.dll ObsTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using RaceMode;

class ObsTests
{
    static int pass, fail;
    static void Check(string n, bool ok, string d) { if (ok) pass++; else fail++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok || d == null ? "" : "  -> " + d)); }

    // ---------------- a tiny fake obs-websocket server ----------------
    class FakeObs
    {
        public TcpListener L; public int Port; public string Password = "supersecretpassword"; public string Mode = "good";
        public volatile int Requests; public volatile bool SawGoodAuth; public long Bytes;
        public FakeObs() { L = new TcpListener(IPAddress.Loopback, 0); L.Start(); Port = ((IPEndPoint)L.LocalEndpoint).Port; new Thread(Run) { IsBackground = true }.Start(); }
        void Run()
        {
            while (true)
            {
                TcpClient c; try { c = L.AcceptTcpClient(); } catch { return; }
                new Thread(() => { try { Serve(c); } catch { } finally { c.Close(); } }) { IsBackground = true }.Start();
            }
        }
        void Serve(TcpClient c)
        {
            var s = c.GetStream();
            var head = ReadHead(s);
            string key = null;
            foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(18).Trim();
            string accept;
            using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\nSec-WebSocket-Protocol: obswebsocket.json\r\n\r\n";
            var rb = Encoding.ASCII.GetBytes(resp); s.Write(rb, 0, rb.Length);
            const string salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";
            if (Mode == "garbage") { SendText(s, "this is not json"); Thread.Sleep(500); return; }
            if (Mode == "huge") { SendText(s, "{\"op\":0,\"d\":{\"x\":\"" + new string('a', 400000) + "\"}}"); Thread.Sleep(500); return; }
            SendText(s, "{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.5.0\",\"rpcVersion\":1,\"authentication\":{\"challenge\":\"" + challenge + "\",\"salt\":\"" + salt + "\"}}}");
            var ident = Ser.DeserializeObject(ReadText(s)) as Dictionary<string, object>;
            var d = (Dictionary<string, object>)ident["d"];
            object a; d.TryGetValue("authentication", out a);
            if ((string)a != Obs.AuthString(Password, salt, challenge)) { SendClose(s, 4009); return; }
            SawGoodAuth = true;
            SendText(s, "{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
            while (true)
            {
                var req = Ser.DeserializeObject(ReadText(s)) as Dictionary<string, object>;
                var rd = (Dictionary<string, object>)req["d"];
                var type = (string)rd["requestType"]; var id = (string)rd["requestId"];
                Requests++;
                // an unrelated event first, which the client must skip
                SendText(s, "{\"op\":5,\"d\":{\"eventType\":\"Whatever\"}}");
                string data = type == "GetStats"
                    ? "{\"cpuUsage\":3.5,\"memoryUsage\":420.0,\"activeFps\":60.0,\"renderSkippedFrames\":2,\"renderTotalFrames\":1000,\"outputSkippedFrames\":1,\"outputTotalFrames\":1000}"
                    : "{\"outputActive\":true,\"outputReconnecting\":false,\"outputCongestion\":0.01,\"outputBytes\":" + Interlocked.Add(ref Bytes, 250000) + ",\"outputSkippedFrames\":5,\"outputTotalFrames\":2000}";
                SendText(s, "{\"op\":7,\"d\":{\"requestType\":\"" + type + "\",\"requestId\":\"" + id + "\",\"requestStatus\":{\"result\":true,\"code\":100},\"responseData\":" + data + "}}");
            }
        }
        static JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string ReadHead(NetworkStream s) { var sb = new StringBuilder(); while (!sb.ToString().EndsWith("\r\n\r\n")) sb.Append((char)s.ReadByte()); return sb.ToString(); }
        static void SendText(NetworkStream s, string text)
        {
            var p = Encoding.UTF8.GetBytes(text); var h = new List<byte> { 0x81 };
            if (p.Length < 126) h.Add((byte)p.Length);
            else if (p.Length < 65536) { h.Add(126); h.Add((byte)(p.Length >> 8)); h.Add((byte)p.Length); }
            else { h.Add(127); for (int i = 7; i >= 0; i--) h.Add((byte)((long)p.Length >> (8 * i))); }
            s.Write(h.ToArray(), 0, h.Count); s.Write(p, 0, p.Length);
        }
        static void SendClose(NetworkStream s, int code) { s.Write(new byte[] { 0x88, 2, (byte)(code >> 8), (byte)code }, 0, 4); Thread.Sleep(300); }
        static string ReadText(NetworkStream s)
        {
            while (true)
            {
                int b0 = s.ReadByte(), b1 = s.ReadByte(); if (b0 < 0 || b1 < 0) throw new IOException("closed");
                long len = b1 & 0x7F;
                if (len == 126) len = (s.ReadByte() << 8) | s.ReadByte();
                else if (len == 127) { len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | (uint)s.ReadByte(); }
                var mask = new byte[4]; if ((b1 & 0x80) != 0) Fill(s, mask);
                var p = new byte[len]; Fill(s, p);
                for (int i = 0; i < p.Length; i++) p[i] ^= mask[i % 4];
                int op = b0 & 0x0F;
                if (op == 8) throw new IOException("closed");
                if (op == 1) return Encoding.UTF8.GetString(p);
            }
        }
        static void Fill(NetworkStream s, byte[] b) { int n = 0; while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) throw new IOException("eof"); n += r; } }
    }

    static bool WaitFor(Func<bool> cond, int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { if (cond()) return true; Thread.Sleep(100); } return cond(); }

    static string Http(int port, string raw)
    {
        using (var c = new TcpClient()) { c.Connect(IPAddress.Loopback, port); var s = c.GetStream(); var b = Encoding.ASCII.GetBytes(raw); s.Write(b, 0, b.Length); using (var r = new StreamReader(s)) return r.ReadToEnd(); }
    }

    static void Main()
    {
        // 01 the login hash, against the example in obs-websocket's protocol documentation
        Check("01 login hash matches OBS's documented example",
            Obs.AuthString("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=") == "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=",
            Obs.AuthString("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="));

        // 02-06 good login, stats, bitrate
        var fake = new FakeObs();
        Obs.IsRunning = () => true; Obs.PortOverride = fake.Port; Obs.PasswordSource = () => "supersecretpassword";
        Obs.Start();
        bool got = WaitFor(() => Obs.Latest.Connected && Obs.Latest.Streaming, 8000);
        var o = Obs.Latest;
        Check("02 logs in with the challenge-response (password never sent)", fake.SawGoodAuth, null);
        Check("03 reads stats: streaming, OBS CPU, render/encode/network frames", got && o.Cpu == 3.5 && o.RenderSkipped == 2 && o.EncodeSkipped == 1 && o.NetDropped == 5 && o.NetTotal == 2000, o.Status);
        Check("04 skips unrelated events between replies", fake.Requests >= 2 && o.Connected, null);
        bool kb = WaitFor(() => !double.IsNaN(Obs.Latest.Kbps), 8000);
        Check("05 bitrate from bytes sent (~1000 kbps)", kb && Obs.Latest.Kbps > 700 && Obs.Latest.Kbps < 1400, Obs.Latest.Kbps.ToString());
        Obs.Stop();
        Thread.Sleep(1500);
        Check("06 stopping switches stats off", !Obs.Latest.Connected && !Obs.Active, Obs.Latest.Status);

        // 07 wrong password: refused with a plain message, no crash
        var wrong = new FakeObs { Password = "somethingelse" };
        Obs.PortOverride = wrong.Port; Obs.Start();
        WaitFor(() => Obs.Latest.Status.Contains("login") || Obs.Latest.Status.Contains("password"), 8000);
        Check("07 wrong password -> plain message, not connected", !Obs.Latest.Connected && Obs.Latest.Status.Contains("login"), Obs.Latest.Status);
        Obs.Stop(); Thread.Sleep(600);

        // 08-09 malformed replies
        var junk = new FakeObs { Mode = "garbage" };
        Obs.PortOverride = junk.Port; Obs.Start(); Thread.Sleep(2500);
        Check("08 non-JSON reply -> not connected, no crash", !Obs.Latest.Connected, Obs.Latest.Status);
        Obs.Stop(); Thread.Sleep(600);
        var huge = new FakeObs { Mode = "huge" };
        Obs.PortOverride = huge.Port; Obs.Start(); Thread.Sleep(2500);
        Check("09 oversized message -> refused", !Obs.Latest.Connected && Obs.Latest.Status.Contains("too big"), Obs.Latest.Status);
        Obs.Stop(); Thread.Sleep(600);

        // 10 OBS not running -> says so, never connects
        Obs.IsRunning = () => false; Obs.Start(); Thread.Sleep(800);
        Check("10 OBS closed -> 'OBS isn't open'", Obs.Latest.Status == "OBS isn't open", Obs.Latest.Status);
        Obs.Stop();

        // ---------------- the dock ----------------
        var token = ObsDock.NewToken();
        Check("11 dock key: 64 hex characters from a secure random source", token.Length == 64 && ObsDock.NewToken() != token, null);
        var listen = new TcpListener(IPAddress.Loopback, 0); listen.Start(); int dport = ((IPEndPoint)listen.LocalEndpoint).Port; listen.Stop();
        var switched = new List<bool>(); int shown = 0;
        var err = ObsDock.Start(dport, token, () => "{\"on\":true}", on => { lock (switched) switched.Add(on); }, () => Interlocked.Increment(ref shown));
        Check("12 dock listens on 127.0.0.1 only", err == null && ObsDock.Url.StartsWith("http://127.0.0.1:" + dport + "/dock?t="), err);
        string ok = Http(dport, "GET /stats.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n");
        Check("13 right key + local Host -> stats", ok.StartsWith("HTTP/1.1 200") && ok.EndsWith("{\"on\":true}") && ok.Contains("Cache-Control: no-store"), ok.Split('\n')[0]);
        string page = Http(dport, "GET /dock?t=" + token + " HTTP/1.1\r\nHost: localhost:" + dport + "\r\n\r\n");
        Check("14 dock page served with a strict content policy", page.StartsWith("HTTP/1.1 200") && page.Contains("Content-Security-Policy: default-src 'none'") && page.Contains("<title>Race Mode</title>"), page.Split('\n')[0]);
        Check("15 wrong key -> 403", Http(dport, "GET /stats.json?t=" + token.Substring(1) + "x HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n").StartsWith("HTTP/1.1 403"), null);
        Check("16 no key -> 403", Http(dport, "GET /stats.json HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n").StartsWith("HTTP/1.1 403"), null);
        Check("17 a web page's Host (DNS rebinding) -> 403", Http(dport, "GET /stats.json?t=" + token + " HTTP/1.1\r\nHost: evil.example:" + dport + "\r\n\r\n").StartsWith("HTTP/1.1 403"), null);
        Check("18 two Host headers -> 400", Http(dport, "GET /stats.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\nHost: evil.example\r\n\r\n").StartsWith("HTTP/1.1 400"), null);
        Check("19 POST without the marker header -> refused", Http(dport, "POST /stats.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\nContent-Length: 0\r\n\r\n").StartsWith("HTTP/1.1 403"), null);
        Check("20 other paths -> 404", Http(dport, "GET /config.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n").StartsWith("HTTP/1.1 404"), null);
        Check("21 path tricks -> 404", Http(dport, "GET /../config.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n").StartsWith("HTTP/1.1 404"), null);
        string big = "GET /stats.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\nX-Junk: " + new string('a', 9000) + "\r\n\r\n";
        string bigResp = ""; try { bigResp = Http(dport, big); } catch { }
        Check("22 oversized request -> no answer", bigResp == "", bigResp.Split('\n')[0]);
        // 23 a slow client can't block the dock: 4 idle connections, then a real request still... is refused or answered, never hangs
        var idle = new List<TcpClient>(); for (int i = 0; i < 6; i++) { var t = new TcpClient(); t.Connect(IPAddress.Loopback, dport); idle.Add(t); }
        Thread.Sleep(3600);   // the 2 s read timeout frees them
        string after = Http(dport, "GET /stats.json?t=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n\r\n");
        Check("23 idle connections time out; the dock keeps working", after.StartsWith("HTTP/1.1 200"), after.Split('\n')[0]);
        foreach (var t in idle) t.Close();

        // ---- commands: switch Race Mode on/off, bring the window to the front ----
        string H = " HTTP/1.1\r\nHost: 127.0.0.1:" + dport + "\r\n";
        Func<string, string> post = extra => Http(dport, "POST /switch?on=1&t=" + token + H + extra + "\r\n");
        Check("25 switch on from the dock (marker header) -> 200, Race Mode told 'on'", post("X-RaceMode: 1\r\n").StartsWith("HTTP/1.1 200") && switched.Count == 1 && switched[0], null);
        Check("26 no marker header (a plain web form) -> 403, nothing switched", post("").StartsWith("HTTP/1.1 403") && switched.Count == 1, null);
        Check("27 another site's Origin -> 403", post("X-RaceMode: 1\r\nOrigin: https://evil.example\r\n").StartsWith("HTTP/1.1 403") && switched.Count == 1, null);
        Check("28 our own Origin -> 200", post("X-RaceMode: 1\r\nOrigin: http://127.0.0.1:" + dport + "\r\n").StartsWith("HTTP/1.1 200") && switched.Count == 2, null);
        Check("29 a request body -> 400", post("X-RaceMode: 1\r\nContent-Length: 5\r\n").StartsWith("HTTP/1.1 400") && switched.Count == 2, null);
        Check("30 chunked body -> 400", post("X-RaceMode: 1\r\nTransfer-Encoding: chunked\r\n").StartsWith("HTTP/1.1 400") && switched.Count == 2, null);
        Check("31 wrong key -> 403", Http(dport, "POST /switch?on=1&t=" + token.Substring(1) + "x" + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 403") && switched.Count == 2, null);
        Check("32 off -> Race Mode told 'off'", Http(dport, "POST /switch?on=0&t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 200") && switched.Count == 3 && !switched[2], null);
        Check("33 no on= or both -> 400, nothing guessed", Http(dport, "POST /switch?t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 400")
            && Http(dport, "POST /switch?on=1&on=0&t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 400") && Http(dport, "POST /switch?xon=1&t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 400") && switched.Count == 3, null);
        Check("34 show window -> 200", Http(dport, "POST /show?t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 200") && shown == 1, null);
        Check("35 GET can't switch -> 404", Http(dport, "GET /switch?on=1&t=" + token + H + "\r\n").StartsWith("HTTP/1.1 404") && switched.Count == 3, null);
        Check("36 POST can't read or change anything else -> 404", Http(dport, "POST /stats.json?t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 404")
            && Http(dport, "POST /config?t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 404"), null);
        Check("37 other methods -> 405", Http(dport, "PUT /switch?on=1&t=" + token + H + "X-RaceMode: 1\r\n\r\n").StartsWith("HTTP/1.1 405")
            && Http(dport, "OPTIONS /switch?on=1&t=" + token + H + "\r\n").StartsWith("HTTP/1.1 405"), null);
        ObsDock.Stop();
        bool refused = false; try { Http(dport, "GET / HTTP/1.1\r\n\r\n"); } catch { refused = true; }
        Check("24 stopped -> nothing listens", refused, null);

        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
