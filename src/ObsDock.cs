using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RaceMode
{
    // The OBS dock: a tiny web page that OBS shows inside its window (Docks > Custom Browser Docks).
    // Served on 127.0.0.1 only, never the network. Every request needs the secret token in its URL, and the
    // Host header must be 127.0.0.1/localhost on our port, so a web page in a browser can't read it (DNS rebinding).
    // GET: the page and stats.json (read-only). POST: only two commands, "switch Race Mode on/off" and "show the
    // window". A POST also needs the X-RaceMode header (a web page can't add it without a CORS preflight, which
    // this server never answers) and, if the browser sends an Origin, our own origin. No settings can be changed.
    // Small requests, no bodies, at most 4 connections, 2 s timeouts. Runs only while Stream mode is on.
    public static class ObsDock
    {
        public const int DefaultPort = 47815;
        public static int Port = DefaultPort;
        static TcpListener _listener;
        static string _token = "";
        static Func<string> _json;
        static Action<bool> _switch;     // Race Mode on (true) / off (false), run on the UI thread by the caller
        static Action _show;             // bring the Race Mode window to the front
        static int _open;
        static byte[] _page;

        public static bool Running { get { return _listener != null; } }

        // .NET Framework sockets are inheritable: any program Race Mode starts (a race app, the copy started by an
        // update) would get its own handle to the dock's port and keep it open after Race Mode quits, so the next
        // Race Mode couldn't listen there. Every socket here is made private to this process.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetHandleInformation(IntPtr h, out uint flags);
        const uint HandleFlagInherit = 1;
        static void NoInherit(Socket s) { try { SetHandleInformation(s.Handle, HandleFlagInherit, 0); } catch { } }
        // For tests: whether the listening socket can be inherited by programs Race Mode starts.
        public static bool ListenerInheritable
        {
            get { var l = _listener; uint f; return l != null && GetHandleInformation(l.Server.Handle, out f) && (f & HandleFlagInherit) != 0; }
        }
        // How many ports after the saved one to try when it is taken.
        public const int PortsToTry = 10;
        public static string Url { get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/dock?t=" + _token; } }

        public static string NewToken()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        // A token this code made: exactly 64 lowercase hex characters.
        public static bool ValidToken(string t)
        {
            if (t == null || t.Length != 64) return false;
            foreach (var ch in t) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            return true;
        }

        // Returns null when listening, or why not (e.g. the port is taken).
        public static string Start(int port, string token, Func<string> json) { return Start(port, token, json, null, null); }

        public static string Start(int port, string token, Func<string> json, Action<bool> onSwitch, Action onShow)
        {
            _switch = onSwitch; _show = onShow;
            if (string.IsNullOrEmpty(token) || token.Length < 32) return "missing token";
            Stop();
            try
            {
                if (_page == null)
                    using (var s = typeof(ObsDock).Assembly.GetManifestResourceStream("dock.html"))
                    using (var ms = new MemoryStream()) { if (s != null) s.CopyTo(ms); _page = ms.ToArray(); }
                _token = token; _json = json;
                // the saved port first; if something holds it, the next few (the caller then makes a new link)
                TcpListener l = null; Exception first = null;
                for (int p = port; p < port + PortsToTry && p <= 65535 && l == null; p++)
                {
                    var tryL = new TcpListener(IPAddress.Loopback, p);
                    try { tryL.Server.ExclusiveAddressUse = true; NoInherit(tryL.Server); tryL.Start(8); NoInherit(tryL.Server); l = tryL; Port = p; }
                    catch (Exception ex) { if (first == null) first = ex; try { tryL.Stop(); } catch { } }
                }
                if (l == null) throw first ?? new IOException("no free port");
                _listener = l;
                var t = new Thread(() => Accept(l)) { IsBackground = true, Name = "RaceMode.Dock" };
                t.Start();
                return null;
            }
            catch (Exception ex) { _listener = null; return "The OBS dock couldn't start on port " + port + " (" + ex.Message + ")"; }
        }

        public static void Stop()
        {
            var l = _listener; _listener = null;
            if (l != null) try { l.Stop(); } catch { }
        }

        static void Accept(TcpListener l)
        {
            while (_listener == l)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { return; }
                NoInherit(c.Client);
                if (Interlocked.Increment(ref _open) > 4) { Interlocked.Decrement(ref _open); try { c.Close(); } catch { } continue; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch { } finally { Interlocked.Decrement(ref _open); try { c.Close(); } catch { } } });
            }
        }

        public static void Handle(TcpClient c)
        {
            c.ReceiveTimeout = 2000; c.SendTimeout = 2000;
            var stream = c.GetStream();
            var head = ReadHead(stream);
            if (head == null) return;
            int status; string type; byte[] body;
            Route(head, out status, out type, out body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : status == 403 ? " Forbidden" : status == 404 ? " Not Found" : status == 405 ? " Method Not Allowed" : status == 503 ? " Service Unavailable" : " Bad Request").Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\n");
            if (type.StartsWith("text/html", StringComparison.Ordinal))
                sb.Append("Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var h = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(h, 0, h.Length);
            stream.Write(body, 0, body.Length);
        }

        // The routing rules, separate from the socket so tests can check them directly.
        public static void Route(string head, out int status, out string type, out byte[] body)
        {
            type = "text/plain; charset=utf-8"; body = new byte[0];
            // a bare \n (one that isn't half of \r\n) can hide a header from us that another reader would see
            for (int i = 0; i < head.Length; i++)
                if (head[i] == '\n' && (i == 0 || head[i - 1] != '\r')) { status = 400; return; }
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var req = lines[0].Split(' ');
            if (req.Length != 3 || !req[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) { status = 400; return; }
            if (req[0] != "GET" && req[0] != "POST") { status = 405; return; }
            bool post = req[0] == "POST";
            string host = null, origin = null, marker = null, length = null;
            for (int i = 1; i < lines.Length; i++)
            {
                var k = lines[i].IndexOf(':');
                if (k <= 0) continue;
                var name = lines[i].Substring(0, k).Trim(); var val = lines[i].Substring(k + 1).Trim();
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) { if (host != null) { status = 400; return; } host = val; }   // two Host headers: refuse
                else if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase)) { if (origin != null) { status = 400; return; } origin = val; }   // two Origin headers: refuse
                else if (string.Equals(name, "X-RaceMode", StringComparison.OrdinalIgnoreCase)) marker = val;
                else if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) length = val;
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) { status = 400; return; }
            }
            var p = Port.ToString(CultureInfo.InvariantCulture);
            if (host != "127.0.0.1:" + p && host != "localhost:" + p) { status = 403; return; }
            if (post)
            {
                // Commands: no body, our marker header, and if the browser says where the page came from, it must be us.
                if (marker != "1") { status = 403; return; }
                if (origin != null && origin != "http://127.0.0.1:" + p && origin != "http://localhost:" + p) { status = 403; return; }
                if (length != null && length != "0") { status = 400; return; }
            }
            var target = req[1];
            var q = target.IndexOf('?');
            var path = q < 0 ? target : target.Substring(0, q);
            string t = null;
            if (q >= 0)
                foreach (var part in target.Substring(q + 1).Split('&'))
                    if (part.StartsWith("t=", StringComparison.Ordinal)) t = part.Substring(2);
            if (!TokenOk(t)) { status = 403; return; }
            if (post)
            {
                if (path == "/switch")
                {
                    // exactly one of on=1 / on=0; anything else is refused rather than guessed
                    int found = 0; bool on = false;
                    foreach (var part in target.Substring(q + 1).Split('&'))
                        if (part == "on=1") { on = true; found++; }
                        else if (part == "on=0") { on = false; found++; }
                    if (found != 1) { status = 400; return; }
                    if (_switch == null) { status = 503; return; }
                    _switch(on);
                }
                else if (path == "/show") { if (_show == null) { status = 503; return; } _show(); }
                else { status = 404; return; }
                status = 200; type = "application/json; charset=utf-8"; body = Encoding.UTF8.GetBytes("{\"ok\":true}"); return;
            }
            if (path == "/dock") { status = 200; type = "text/html; charset=utf-8"; body = _page ?? new byte[0]; return; }
            if (path == "/stats.json")
            {
                string json = "{}";
                try { if (_json != null) json = _json() ?? "{}"; } catch { }
                status = 200; type = "application/json; charset=utf-8"; body = Encoding.UTF8.GetBytes(json); return;
            }
            status = 404;
        }

        // Constant-time compare, so the token can't be guessed a character at a time.
        static bool TokenOk(string t)
        {
            if (t == null || _token.Length == 0 || t.Length != _token.Length) return false;
            int diff = 0;
            for (int i = 0; i < t.Length; i++) diff |= t[i] ^ _token[i];
            return diff == 0;
        }

        // Reads the request line and headers (up to 4 KB, all within 3 seconds, however slowly they arrive).
        // The dock never needs a request body.
        static string ReadHead(NetworkStream s)
        {
            var buf = new byte[4096]; int n = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(3000);
            while (n < buf.Length)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                int r;
                try { s.ReadTimeout = left; r = s.Read(buf, n, buf.Length - n); } catch { return null; }
                if (r <= 0) return null;
                n += r;
                var text = Encoding.ASCII.GetString(buf, 0, n);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
            }
            return null;   // too big
        }

        // For tests: set the token without starting the listener.
        public static void SetForTest(int port, string token, Func<string> json) { Port = port; _token = token; _json = json; }
        public static void SetForTest(int port, string token, Func<string> json, Action<bool> onSwitch, Action onShow) { SetForTest(port, token, json); _switch = onSwitch; _show = onShow; }
    }
}
