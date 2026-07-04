using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Media;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using NAudio.Lame;
using NAudio.Wave;

namespace ETS2TunnelRadio
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // A tunnel = a line segment (entry -> exit) on the map's X/Z plane, plus a half-width in metres.
    public class Tunnel
    {
        public string Name { get; set; } = "Tunnel";
        public double Ax { get; set; }
        public double Az { get; set; }
        public double Bx { get; set; }
        public double Bz { get; set; }
        public double HalfWidth { get; set; } = 12.0;
    }

    // Ten distinct "no signal" textures, each modelled on a real interference type:
    // FM dropout = rising high hiss; AM = crackle/buzz; electrical = whine/clicks;
    // weak reception = whistle/twitter; lightning = pops. All loops carry slow (~0.1-0.3 Hz)
    // amplitude drift so a 7-second loop never reads as a loop.
    internal static class NoiseBank
    {
        public const int Rate = 44100;
        public static readonly string[] Names = {
            "FM hiss", "Weak FM twitter", "AM crackle", "Electric buzz", "Heterodyne warble",
            "Pink hiss", "Seek pulse", "Distant station", "Storm static", "Digital grit",
        };
        public static short[][] Pcm;    // mono 16-bit
        public static byte[][] Wav;

        public static void Build()
        {
            const double secs = 7.0;
            int n = (int)(Rate * secs);
            Pcm = new short[Names.Length][];
            Wav = new byte[Names.Length][];
            for (int v = 0; v < Names.Length; v++)
            {
                var rnd = new Random(777 + v);
                var s = new double[n];
                switch (v)
                {
                    case 0: FmHiss(s, rnd); break;
                    case 1: WeakFm(s, rnd); break;
                    case 2: AmCrackle(s, rnd); break;
                    case 3: ElectricBuzz(s, rnd); break;
                    case 4: Heterodyne(s, rnd); break;
                    case 5: PinkHiss(s, rnd); break;
                    case 6: SeekPulse(s, rnd); break;
                    case 7: DistantStation(s, rnd); break;
                    case 8: StormStatic(s, rnd); break;
                    default: DigitalGrit(s, rnd); break;
                }
                SlowDrift(s, rnd);      // anti-loop amplitude drift
                Pcm[v] = Quantize(s, 0.40);
                Wav[v] = ToWav(Pcm[v]);
            }
        }

        // 0: classic FM inter-station hiss — bright white noise, slight high tilt
        static void FmHiss(double[] s, Random r)
        {
            double prev = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double w = r.NextDouble() * 2 - 1;
                s[i] = (w - prev) * 0.5 + w * 0.5;
                prev = w;
            }
        }

        // 1: weak FM — hiss with brief band-passed "twitter" chirps poking through
        static void WeakFm(double[] s, Random r)
        {
            FmHiss(s, r);
            double b1 = 0, b2 = 0;
            int next = Rate / 2;
            for (int i = 0; i < s.Length; i++)
            {
                if (i == next)
                {
                    int len = Rate / 8 + r.Next(Rate / 6);
                    double f = 900 + r.NextDouble() * 1400;
                    for (int j = 0; j < len && i + j < s.Length; j++)
                    {
                        double env = Math.Sin(Math.PI * j / len);
                        s[i + j] = s[i + j] * 0.55 + Math.Sin(2 * Math.PI * f * j / Rate + 6 * Math.Sin(2 * Math.PI * 4 * j / Rate)) * env * 0.28;
                    }
                    next = i + len + Rate / 3 + r.Next(Rate);
                }
                b1 += 0.02 * (s[i] - b1); b2 += 0.3 * (b1 - b2);
            }
        }

        // 2: AM band — warm noise floor + random lightning crackle pops
        static void AmCrackle(double[] s, Random r)
        {
            double lp = 0;
            for (int i = 0; i < s.Length; i++)
            {
                lp += 0.12 * ((r.NextDouble() * 2 - 1) - lp);   // darker floor
                s[i] = lp * 2.2;
            }
            int pops = 90;
            for (int p = 0; p < pops; p++)
            {
                int at = r.Next(s.Length - 400);
                double a = 0.5 + r.NextDouble() * 0.5;
                int len = 30 + r.Next(300);
                for (int j = 0; j < len && at + j < s.Length; j++)
                    s[at + j] += (r.NextDouble() * 2 - 1) * a * Math.Exp(-4.0 * j / len);
            }
        }

        // 3: electrical interference — 50 Hz buzz + harmonics over faint hiss
        static void ElectricBuzz(double[] s, Random r)
        {
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                double buzz = 0;
                for (int h = 1; h <= 7; h += 2)
                    buzz += Math.Sin(2 * Math.PI * 50 * h * t) / h;
                double saw = 2 * (t * 100 % 1) - 1;
                s[i] = buzz * 0.30 + saw * 0.10 + (r.NextDouble() * 2 - 1) * 0.22;
            }
        }

        // 4: heterodyne — wandering whistle riding on hiss (two carriers beating)
        static void Heterodyne(double[] s, Random r)
        {
            double phase = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                double f = 700 + 500 * Math.Sin(2 * Math.PI * 0.13 * t) + 220 * Math.Sin(2 * Math.PI * 0.71 * t);
                phase += 2 * Math.PI * f / Rate;
                s[i] = Math.Sin(phase) * 0.20 + (r.NextDouble() * 2 - 1) * 0.5;
            }
        }

        // 5: pink (1/f) hiss — softer, deeper "empty band" noise
        static void PinkHiss(double[] s, Random r)
        {
            double p0 = 0, p1 = 0, p2 = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double w = r.NextDouble() * 2 - 1;
                p0 = 0.997 * p0 + 0.030 * w;
                p1 = 0.985 * p1 + 0.045 * w;
                p2 = 0.950 * p2 + 0.075 * w;
                s[i] = (p0 + p1 + p2 + w * 0.05) * 2.4;
            }
        }

        // 6: radio seek — hiss chopped by a slow shh..shh scanning pulse + tone blips
        static void SeekPulse(double[] s, Random r)
        {
            FmHiss(s, r);
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                double g = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 0.65 * t);
                s[i] *= 0.35 + 0.65 * g * g;
            }
            for (int b = 0; b < 6; b++)
            {
                int at = r.Next(s.Length - Rate / 6);
                double f = 400 + r.NextDouble() * 800;
                for (int j = 0; j < Rate / 10 && at + j < s.Length; j++)
                    s[at + j] += Math.Sin(2 * Math.PI * f * j / Rate) * 0.14 * Math.Sin(Math.PI * j / (Rate / 10.0));
            }
        }

        // 7: distant station bleeding through — muffled speech-band babble under hiss
        static void DistantStation(double[] s, Random r)
        {
            double lp = 0, bp = 0, env = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                double w = r.NextDouble() * 2 - 1;
                lp += 0.06 * (w - lp);                                     // muffle
                double syll = Math.Max(0, Math.Sin(2 * Math.PI * 3.3 * t) + 0.3 * Math.Sin(2 * Math.PI * 1.1 * t) - 0.4);
                env += 0.004 * (syll - env);
                bp += 0.25 * (lp - bp);
                s[i] = (lp - bp) * 9.0 * env + w * 0.34;                   // babble + hiss bed
            }
        }

        // 8: storm — white noise with dense bursty crackle clusters
        static void StormStatic(double[] s, Random r)
        {
            for (int i = 0; i < s.Length; i++) s[i] = (r.NextDouble() * 2 - 1) * 0.42;
            for (int c = 0; c < 10; c++)
            {
                int at = r.Next(s.Length - Rate / 2);
                int len = Rate / 8 + r.Next(Rate / 3);
                for (int j = 0; j < len && at + j < s.Length; j++)
                    if (r.NextDouble() < 0.10)
                    {
                        int pl = 20 + r.Next(120);
                        double a = 0.5 + r.NextDouble() * 0.5;
                        for (int k = 0; k < pl && at + j + k < s.Length; k++)
                            s[at + j + k] += (r.NextDouble() * 2 - 1) * a * Math.Exp(-5.0 * k / pl);
                        j += pl;
                    }
            }
        }

        // 9: digital grit — sample-hold quantized noise, GSM-ish granular texture
        static void DigitalGrit(double[] s, Random r)
        {
            double hold = 0;
            int per = Rate / 5500, cnt = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (cnt-- <= 0) { hold = Math.Round((r.NextDouble() * 2 - 1) * 6) / 6; cnt = per + r.Next(4); }
                double t = (double)i / Rate;
                double gate = Math.Sin(2 * Math.PI * 8.35 * t) > -0.6 ? 1 : 0.25;   // GSM-like frame buzz
                s[i] = hold * 0.5 * gate + (r.NextDouble() * 2 - 1) * 0.18;
            }
        }

        static void SlowDrift(double[] s, Random r)
        {
            double ph = r.NextDouble() * Math.PI * 2;
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / Rate;
                s[i] *= 0.80 + 0.20 * Math.Sin(2 * Math.PI * (1.0 / 7.0) * t + ph)  // exact loop period
                             * Math.Sin(2 * Math.PI * (3.0 / 7.0) * t);
            }
        }

        static short[] Quantize(double[] s, double amp)
        {
            var pcm = new short[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                double x = s[i] * amp;
                if (x > 1) x = 1; else if (x < -1) x = -1;
                pcm[i] = (short)(x * short.MaxValue);
            }
            return pcm;
        }

        static byte[] ToWav(short[] pcm)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int dataBytes = pcm.Length * 2;
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(Rate);
            bw.Write(Rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            foreach (short v in pcm) bw.Write(v);
            bw.Flush();
            return ms.ToArray();
        }
    }

    // Local "Tunnel Radio FM" station: relays a real internet stream and crossfades to
    // static inside tunnels. The GAME's own radio plays http://127.0.0.1:17771/tunnelradio,
    // so the in-game radio UI/volume apply. Raw TCP + hand-rolled HTTP (no urlacl issues).
    internal class RadioProxy
    {
        public const int Port = 17771;
        readonly Func<bool> _tunnelState;       // predicted state (buffer-compensated)
        readonly Func<int> _variant;
        readonly Func<string> _sourceUrl;       // legacy /tunnelradio relay URL
        readonly Func<int, string> _stationUrl; // /s/<idx> -> original station URL
        public volatile string Status = "off";
        public volatile int CurrentStation = -1;   // /s/<idx> of the live connection, -1 = none
        long _lastActiveTicks;                     // UTC ticks of the last streamed frame
        int _connSeq;                              // the game overlaps connections on station switch;
        volatile int _newestConn;                  // only the newest one may clear shared state
        public bool ClientLive => (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc)).TotalSeconds < 2.5;
        TcpListener _listener;
        volatile bool _run;

        public RadioProxy(Func<bool> tunnelState, Func<int> variant, Func<string> sourceUrl, Func<int, string> stationUrl)
        { _tunnelState = tunnelState; _variant = variant; _sourceUrl = sourceUrl; _stationUrl = stationUrl; }

        internal static void Dbg(string msg)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ETR-debug.log"), $"[{DateTime.Now:HH:mm:ss}] PROXY {msg}\r\n"); } catch { }
        }

        public void Start()
        {
            if (_run) return;
            _run = true;
            _listener = new TcpListener(System.Net.IPAddress.Loopback, Port);
            _listener.Start();
            Status = "waiting for the game to tune in...";
            new Thread(AcceptLoop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            _run = false;
            try { _listener?.Stop(); } catch { }
            Status = "off";
        }

        void AcceptLoop()
        {
            while (_run)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            int myConn = Interlocked.Increment(ref _connSeq);
            try
            {
                using (client)
                using (var net = client.GetStream())
                {
                    var buf = new byte[4096];
                    net.ReadTimeout = 3000;
                    int got = 0;
                    try { got = net.Read(buf, 0, buf.Length); } catch { }
                    string reqLine = got > 0 ? Encoding.ASCII.GetString(buf, 0, got).Split('\r', '\n')[0] : "";
                    // "GET /s/3 HTTP/1.1" -> relay station 3; "/tunnelradio" -> legacy textbox URL
                    string url = _sourceUrl();
                    int station = -1;
                    var m = System.Text.RegularExpressions.Regex.Match(reqLine, @"GET\s+/s/(\d+)");
                    if (m.Success)
                    {
                        station = int.Parse(m.Groups[1].Value);
                        string su = _stationUrl(station);
                        if (!string.IsNullOrEmpty(su)) url = su;
                    }
                    _newestConn = myConn;
                    CurrentStation = station;
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\nicy-name: Tunnel Radio FM\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                    net.Write(head, 0, head.Length);
                    Status = "game connected — streaming";
                    Dbg("serving " + reqLine + " -> " + url + " (conn " + myConn + ")");
                    StreamAudio(net, url, myConn);
                }
            }
            catch (Exception ex) { Dbg("serve failed: " + ex); }
            if (_newestConn == myConn)   // an overlapping newer connection owns the state now
            {
                CurrentStation = -1;
                Interlocked.Exchange(ref _lastActiveTicks, 0);
                if (_run) Status = "waiting for the game to tune in...";
            }
        }

        // Mp3Frame needs a stream with a readable Position; network streams have neither.
        // Classic NAudio wrapper: full-count reads + tracked position, no seeking.
        class ReadFullyStream : Stream
        {
            readonly Stream _src;
            long _pos;
            public ReadFullyStream(Stream src) { _src = src; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _pos;
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = 0;
                while (read < count)
                {
                    int n = _src.Read(buffer, offset + read, count - read);
                    if (n == 0) break;
                    read += n;
                }
                _pos += read;
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // Relay decoder: background thread pulls MP3 frames over plain HTTP (icecast-friendly,
        // no Media Foundation) into a PCM ring buffer; the mix loop below never blocks on it.
        class RelaySource
        {
            readonly string _url;
            readonly float[] _ring = new float[44100 * 2 * 8];   // 8 s stereo @44.1k
            long _w, _r;
            volatile bool _run = true;
            public volatile string State = "connecting";

            public RelaySource(string url)
            {
                _url = url;
                new Thread(Pump) { IsBackground = true }.Start();
            }

            public void Stop() { _run = false; }

            public int Read(float[] dst, int count)
            {
                int n = (int)Math.Min(count, _w - _r);
                for (int i = 0; i < n; i++) dst[i] = _ring[(_r + i) % _ring.Length];
                _r += n;
                return n;
            }

            void Push(float l, float r)
            {
                if (_w - _r >= _ring.Length - 2) { _r = _w - _ring.Length / 2; }  // overwrite oldest
                _ring[_w % _ring.Length] = l; _w++;
                _ring[_w % _ring.Length] = r; _w++;
            }

            void Pump()
            {
                while (_run)
                {
                    IMp3FrameDecompressor dec = null;
                    try
                    {
                        var req = System.Net.WebRequest.CreateHttp(_url);
                        req.AllowAutoRedirect = true;
                        req.UserAgent = "ETS2TunnelRadio/0.2";
                        req.Timeout = 8000;
                        using var resp = req.GetResponse();
                        using var rs = resp.GetResponseStream();
                        var buffered = new ReadFullyStream(new BufferedStream(rs, 64 * 1024));
                        var pcm = new byte[65536];
                        State = "relaying";
                        while (_run)
                        {
                            var frame = Mp3Frame.LoadFromStream(buffered);
                            if (frame == null) break;
                            if (dec == null)
                            {
                                var fmt = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate);
                                dec = new AcmMp3FrameDecompressor(fmt);
                            }
                            int bytes = dec.DecompressFrame(frame, pcm, 0);
                            var wf = dec.OutputFormat;   // 16-bit PCM at stream rate
                            int ch = wf.Channels, sr = wf.SampleRate;
                            int samples = bytes / 2 / ch;
                            double step = sr / 44100.0;
                            for (double pos = 0; pos < samples; pos += step)
                            {
                                int idx = (int)pos * ch * 2;
                                float l = BitConverter.ToInt16(pcm, idx) / 32768f;
                                float r = ch == 2 ? BitConverter.ToInt16(pcm, idx + 2) / 32768f : l;
                                Push(l, r);
                            }
                        }
                    }
                    catch (Exception ex) { RadioProxy.Dbg("relay: " + ex.Message); }
                    finally { try { dec?.Dispose(); } catch { } }
                    if (!_run) break;
                    State = "relay lost — retrying";
                    Thread.Sleep(3000);
                }
            }
        }

        void StreamAudio(Stream net, string url, int myConn)
        {
            const int rate = 44100, frame = rate / 10;      // 100 ms frames, stereo
            var mix = new short[frame * 2];
            var srcBuf = new float[frame * 2];
            double xfade = 0;                                // 0 = radio, 1 = static
            long staticPos = 0;
            var lame = new LameMP3FileWriter(net, new WaveFormat(rate, 16, 2), 192);
            var relay = new RelaySource(url);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long sent = 0;
            try
            {
                while (_run && net.CanWrite)
                {
                    bool tun = _tunnelState();
                    double target = tun ? 1 : 0;
                    var pcmStatic = NoiseBank.Pcm[Math.Max(0, Math.Min(NoiseBank.Pcm.Length - 1, _variant()))];

                    int got = relay.Read(srcBuf, srcBuf.Length);
                    if (_newestConn == myConn) Status = "game connected — " + relay.State;
                    Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);
                    for (int i = 0; i < frame; i++)
                    {
                        xfade += (target - xfade) * 0.0012;               // ~0.3 s crossfade
                        double st = pcmStatic[staticPos % pcmStatic.Length] / 32768.0;
                        staticPos++;
                        double l = (i * 2 < got ? srcBuf[i * 2] : 0) * (1 - xfade) + st * xfade;
                        double r = (i * 2 + 1 < got ? srcBuf[i * 2 + 1] : 0) * (1 - xfade) + st * xfade;
                        if (l > 1) l = 1; else if (l < -1) l = -1;
                        if (r > 1) r = 1; else if (r < -1) r = -1;
                        mix[i * 2] = (short)(l * 32767);
                        mix[i * 2 + 1] = (short)(r * 32767);
                    }
                    var bytes = new byte[mix.Length * 2];
                    Buffer.BlockCopy(mix, 0, bytes, 0, bytes.Length);
                    try { lame.Write(bytes, 0, bytes.Length); }
                    catch (Exception ex) { Dbg("lame.Write failed: " + ex.Message); break; }   // client gone

                    sent += frame;
                    double ahead = (double)sent / rate - sw.Elapsed.TotalSeconds;
                    if (ahead > 0.4) Thread.Sleep((int)((ahead - 0.2) * 1000));   // pace to realtime
                }
            }
            finally { relay.Stop(); }
        }
    }

    internal class MainForm : Form
    {
        // ---- SCS telemetry shared-memory offsets (RenCloud scs-sdk-plugin) ----
        const string MapName = "Local\\SCSTelemetry";
        const int OFF_ACTIVE = 0;      // bool
        const int OFF_PLUGREV = 40;    // u32, RenCloud plugin revision
        const int OFF_SPEED = 704;     // float, m/s
        const int OFF_X = 2200;        // double
        const int OFF_Y = 2208;        // double (altitude)
        const int OFF_Z = 2216;        // double

        MemoryMappedFile _mmf;
        MemoryMappedViewAccessor _acc;

        readonly List<Tunnel> _tunnels = new List<Tunnel>();   // generated DB (tunnels.json, replaced on regeneration)
        readonly List<Tunnel> _manual = new List<Tunnel>();    // hand-tagged (manual-tunnels.json, survives regeneration)
        double _x, _y, _z, _speedKmh;
        double _vx, _vz;               // smoothed velocity (m/s) for stream-delay prediction
        bool _telemetryOk;
        double _lastX, _lastZ, _spdSmooth;
        bool _haveLast;
        readonly System.Diagnostics.Stopwatch _spdWatch = System.Diagnostics.Stopwatch.StartNew();
        bool _inTunnel;
        int _enterHits, _exitHits;   // debounce counters
        volatile bool _proxyTunnel;  // buffer-compensated state for the proxy
        volatile int _variantIdx;
        int _lastVariant = -1;
        readonly Random _pick = new Random();

        SoundPlayer _radio, _static;
        string _audioState = "";     // "" | "radio" | "static"
        RadioProxy _proxy;

        // pending tag
        bool _haveEntry;
        double _entX, _entZ;

        // ---- UI ----
        Label _lblStatus, _lblPos, _lblVer, _lblState, _lblProxy;
        string _gameVersion = "";      // from game.log.txt / running exe
        string _dbVersion = "";        // from tunnels.meta.json
        uint _pluginRev;
        int _verRefresh;
        ListBox _lstTunnels;
        TextBox _txtName, _txtStream;
        NumericUpDown _numWidth, _numDelay;
        CheckBox _chkForce, _chkTone, _chkProxy;
        ComboBox _cmbVariant;
        Button _btnEntry, _btnExit, _btnDelete, _btnStation, _btnRestore;
        System.Windows.Forms.Timer _timer;

        string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ETS2TunnelRadio");
        string DbFile => Path.Combine(DataDir, "tunnels.json");
        string ManualFile => Path.Combine(DataDir, "manual-tunnels.json");
        string SettingsFile => Path.Combine(DataDir, "settings.json");
        string StationsFile => Path.Combine(DataDir, "stations.json");
        static string GameDocs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Euro Truck Simulator 2");

        public class Station { public string Url { get; set; } public string Name { get; set; } }
        readonly List<Station> _stations = new List<Station>();   // originals, index-aligned with the rewritten list

        public MainForm()
        {
            Text = "ETS2 Tunnel Radio";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 582);
            Font = new Font("Segoe UI", 9F);

            _lblStatus = new Label { Left = 12, Top = 10, Width = 446, Height = 18, ForeColor = Color.Firebrick, Text = "Telemetry: waiting for game + plugin..." };
            _lblPos = new Label { Left = 12, Top = 32, Width = 446, Height = 18, ForeColor = Color.DimGray, Text = "X: --   Z: --   alt: --   speed: --" };
            _lblVer = new Label { Left = 12, Top = 52, Width = 446, Height = 16, ForeColor = Color.DimGray, Text = "Game: detecting..." };

            _lblState = new Label { Left = 12, Top = 72, Width = 446, Height = 42, Font = new Font("Segoe UI", 18F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "UNAVAILABLE", ForeColor = Color.Gray };

            var tabs = new TabControl { Left = 12, Top = 120, Width = 446, Height = 452 };   // Edit tab needs the height
            var pageRadio = new TabPage("Radio");
            var pageEdit = new TabPage("Edit");
            tabs.TabPages.Add(pageRadio);
            tabs.TabPages.Add(pageEdit);

            // ---------- Radio tab ----------
            // internal-only controls (not shown): tunnel logic reads them; defaults are final
            _chkForce = new CheckBox { Visible = false, Checked = false };
            _chkTone = new CheckBox { Visible = false, Checked = false };   // no test tone for end users
            _cmbVariant = new ComboBox { Visible = false, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbVariant.Items.Add("Random per tunnel");
            foreach (var nm in NoiseBank.Names) _cmbVariant.Items.Add(nm);
            _cmbVariant.SelectedIndex = 0;

            var grpProxy = new GroupBox { Left = 8, Top = 10, Width = 424, Height = 164, Text = "Game-radio mode: your in-game stations, tunnel-aware" };
            var lblSrc = new Label { Left = 10, Top = 24, Width = 90, Text = "Fallback relay:" };
            _txtStream = new TextBox { Left = 100, Top = 21, Width = 310, Text = "https://glzwizzlv.bynetcdn.com/glglz_mp3" };
            _chkProxy = new CheckBox { Left = 10, Top = 50, Width = 300, Text = "Run the local tunnel-radio engine (port 17771)" };
            var lblDelay = new Label { Left = 10, Top = 78, Width = 210, Text = "Game stream buffer compensation:" };
            _numDelay = new NumericUpDown { Left = 220, Top = 75, Width = 52, Minimum = 0, Maximum = 15, Value = 5 };
            var lblS = new Label { Left = 276, Top = 78, Width = 60, Text = "seconds" };
            _btnStation = new Button { Left = 10, Top = 104, Width = 218, Height = 28, Text = "Make ALL my stations tunnel-aware" };
            _btnRestore = new Button { Left = 234, Top = 104, Width = 120, Height = 28, Text = "Restore originals" };
            _lblProxy = new Label { Left = 10, Top = 140, Width = 404, Height = 16, ForeColor = Color.DimGray, Text = "engine: off" };
            grpProxy.Controls.AddRange(new Control[] { lblSrc, _txtStream, _chkProxy, lblDelay, _numDelay, lblS, _btnStation, _btnRestore, _lblProxy });

            var lblHelp = new Label
            {
                Left = 8, Top = 182, Width = 424, Height = 116, ForeColor = Color.FromArgb(0, 80, 160),
                Text = "Setup: click \"Make ALL my stations tunnel-aware\", restart the game, then\r\n" +
                       "tune the in-game radio to ANY of your stations — it plays through this\r\n" +
                       "engine and fades to static inside tunnels.\r\n" +
                       "Keep this app running while you play (or click Restore originals).\r\n" +
                       "Without the engine, the app plays static from your speakers in tunnels."
            };
            pageRadio.Controls.AddRange(new Control[] { grpProxy, lblHelp });

            // ---------- Edit tab (manual tunnels) ----------
            var grpTag = new GroupBox { Left = 8, Top = 10, Width = 424, Height = 400, Text = "Manual tunnels (drive in, tag Entry; drive out, tag Exit)" };
            var lblName = new Label { Left = 10, Top = 24, Width = 42, Text = "Name:" };
            _txtName = new TextBox { Left = 54, Top = 21, Width = 170, Text = "Tunnel 1" };
            var lblW = new Label { Left = 234, Top = 24, Width = 70, Text = "Half-width:" };
            _numWidth = new NumericUpDown { Left = 306, Top = 21, Width = 56, Minimum = 3, Maximum = 60, Value = 12 };
            var lblM = new Label { Left = 366, Top = 24, Width = 24, Text = "m" };
            _btnEntry = new Button { Left = 10, Top = 52, Width = 120, Height = 30, Text = "Tag Entry" };
            _btnExit = new Button { Left = 136, Top = 52, Width = 120, Height = 30, Text = "Tag Exit", Enabled = false };
            _btnDelete = new Button { Left = 262, Top = 52, Width = 100, Height = 30, Text = "Delete sel." };
            _lstTunnels = new ListBox { Left = 10, Top = 92, Width = 404, Height = 260 };
            var lblEditHint = new Label { Left = 10, Top = 360, Width = 404, Height = 32, ForeColor = Color.DimGray,
                Text = "Use this when auto-detection misses a tunnel: drive to its entrance, Tag Entry, drive out the far end, Tag Exit. Saved to manual-tunnels.json forever." };
            grpTag.Controls.AddRange(new Control[] { lblName, _txtName, lblW, _numWidth, lblM, _btnEntry, _btnExit, _btnDelete, _lstTunnels, lblEditHint });
            pageEdit.Controls.Add(grpTag);

            Controls.AddRange(new Control[] { _lblStatus, _lblPos, _lblVer, _lblState, tabs });

            _btnEntry.Click += (s, e) => TagEntry();
            _btnExit.Click += (s, e) => TagExit();
            _btnDelete.Click += (s, e) => DeleteSelected();
            _btnStation.Click += (s, e) => TakeOverStations();
            _btnRestore.Click += (s, e) => RestoreStations();
            _chkProxy.CheckedChanged += (s, e) => { if (_chkProxy.Checked) _proxy.Start(); else _proxy.Stop(); SaveSettings(); };
            _cmbVariant.SelectedIndexChanged += (s, e) =>
            {
                if (_cmbVariant.SelectedIndex > 0) SetVariant(_cmbVariant.SelectedIndex - 1, true);
                SaveSettings();
            };
            _txtStream.Leave += (s, e) => SaveSettings();
            _numDelay.ValueChanged += (s, e) => SaveSettings();

            Load += (s, e) => Init();
            FormClosed += (s, e) => Cleanup();
        }

        void Init()
        {
            NoiseBank.Build();
            BuildAudio();
            LoadDb();
            LoadSettings();
            LoadStations();
            _proxy = new RadioProxy(() => _proxyTunnel, () => _variantIdx, () => _txtStream.Text.Trim(),
                i => (i >= 0 && i < _stations.Count && _stations[i] != null) ? _stations[i].Url : null);
            if (_chkProxy.Checked) _proxy.Start();
            SetVariant(_pick.Next(NoiseBank.Names.Length), false);
            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        void SetVariant(int idx, bool restartIfPlaying)
        {
            _variantIdx = idx;
            _static = new SoundPlayer(new MemoryStream(NoiseBank.Wav[idx]));
            try { _static.Load(); } catch { }
            if (restartIfPlaying && _audioState == "static")
            {
                try { _static.PlayLooping(); } catch { }
            }
        }

        // ---------- telemetry ----------
        void Tick()
        {
            ReadTelemetry();

            bool inside;
            if (_chkForce.Checked) inside = true;
            else inside = _telemetryOk && IsInAnyTunnel(_x, _z);

            // simple debounce so portals don't flicker
            if (inside) { _enterHits++; _exitHits = 0; } else { _exitHits++; _enterHits = 0; }
            bool wasIn = _inTunnel;
            if (!_inTunnel && (_enterHits >= 2)) _inTunnel = true;
            if (_inTunnel && (_exitHits >= 3)) _inTunnel = false;

            if (_inTunnel && !wasIn && _cmbVariant.SelectedIndex == 0)
            {
                int v;
                do { v = _pick.Next(NoiseBank.Names.Length); } while (v == _lastVariant && NoiseBank.Names.Length > 1);
                _lastVariant = v;
                SetVariant(v, false);
            }

            // proxy uses a position predicted ahead by the game's stream buffer
            double lead = (double)_numDelay.Value;
            if (_chkForce.Checked) _proxyTunnel = true;
            else if (_telemetryOk)
            {
                double px = _x + _vx * lead, pz = _z + _vz * lead;
                _proxyTunnel = IsInAnyTunnel(px, pz) || _inTunnel && lead < 0.5;
            }
            else _proxyTunnel = false;

            UpdateStateLabel();
            _lblProxy.Text = "engine: " + _proxy.Status;
            if (_verRefresh-- <= 0) { _verRefresh = 100; DetectGameVersion(); UpdateVersionLabel(); }   // every ~10 s

            UpdateAudio(_inTunnel);
        }

        void DetectGameVersion()
        {
            try
            {
                // primary: the game's own log (full build number, rewritten every game start)
                string log = Path.Combine(GameDocs, "game.log.txt");
                if (File.Exists(log))
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var buf = new byte[Math.Min(fs.Length, 64 * 1024)];
                    fs.Read(buf, 0, buf.Length);
                    var m = System.Text.RegularExpressions.Regex.Match(Encoding.UTF8.GetString(buf), @"Loaded pack set version ([\d.]+)");
                    if (m.Success) { _gameVersion = m.Groups[1].Value; return; }
                }
                // fallback: version stamp of a running game exe
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("eurotrucks2"))
                {
                    try
                    {
                        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(p.MainModule.FileName);
                        if (!string.IsNullOrEmpty(vi.ProductVersion)) { _gameVersion = vi.ProductVersion.Split(' ')[0]; return; }
                    }
                    catch { }
                }
            }
            catch { }
        }

        static string MajorMinor(string v)
        {
            var parts = (v ?? "").Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : v ?? "";
        }

        void UpdateVersionLabel()
        {
            string game = string.IsNullOrEmpty(_gameVersion) ? "not detected" : _gameVersion;
            string db = string.IsNullOrEmpty(_dbVersion) ? "unknown" : _dbVersion;
            string plug = _pluginRev > 0 ? $"   ·   plugin r{_pluginRev}" : "";
            if (string.IsNullOrEmpty(_gameVersion) || string.IsNullOrEmpty(_dbVersion))
            {
                _lblVer.Text = $"Game: {game}   ·   tunnel DB: {db}{plug}";
                _lblVer.ForeColor = Color.DimGray;
            }
            else if (MajorMinor(_gameVersion) == MajorMinor(_dbVersion))
            {
                _lblVer.Text = $"Game {game}   ·   tunnel DB {db}  ✓{plug}";
                _lblVer.ForeColor = Color.DarkGreen;
            }
            else
            {
                _lblVer.Text = $"Game {game}  ⚠  tunnel DB built for {db} — tunnels may be off{plug}";
                _lblVer.ForeColor = Color.DarkOrange;
            }
        }

        void UpdateStateLabel()
        {
            bool proxyOn = _chkProxy.Checked;
            if (!_telemetryOk && !_chkForce.Checked)
            { _lblState.Text = "UNAVAILABLE"; _lblState.ForeColor = Color.Gray; return; }
            if (proxyOn && !_proxy.ClientLive)
            { _lblState.Text = "RADIO OFF"; _lblState.ForeColor = Color.Firebrick; return; }
            if (_inTunnel)
            { _lblState.Text = "STATIC  (no signal)"; _lblState.ForeColor = Color.DarkOrange; return; }
            string name = "RADIO";
            if (proxyOn)
            {
                int i = _proxy.CurrentStation;
                if (i >= 0 && i < _stations.Count && !string.IsNullOrEmpty(_stations[i].Name)) name = _stations[i].Name;
                else name = "Tunnel Radio FM";
                if (name.Length > 16) name = name.Substring(0, 6) + "…";
            }
            _lblState.Text = name;
            _lblState.ForeColor = Color.SeaGreen;
        }

        void ReadTelemetry()
        {
            try
            {
                if (_acc == null)
                {
                    _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                    _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    _pluginRev = _acc.ReadUInt32(OFF_PLUGREV);
                }
                bool active = _acc.ReadByte(OFF_ACTIVE) != 0;
                double x = _acc.ReadDouble(OFF_X);
                double y = _acc.ReadDouble(OFF_Y);
                double z = _acc.ReadDouble(OFF_Z);
                _telemetryOk = active;
                _x = x; _y = y; _z = z;
                if (active)
                {
                    double dt = _spdWatch.Elapsed.TotalSeconds; _spdWatch.Restart();
                    if (_haveLast && dt > 0.001 && dt < 1.0)
                    {
                        double dx = x - _lastX, dz = z - _lastZ;
                        double d = Math.Sqrt(dx * dx + dz * dz);
                        double inst = d / dt * 3.6; // km/h
                        if (inst < 400)
                        {
                            _spdSmooth = _spdSmooth * 0.7 + inst * 0.3; // ignore teleports/jumps
                            _vx = _vx * 0.7 + (dx / dt) * 0.3;
                            _vz = _vz * 0.7 + (dz / dt) * 0.3;
                        }
                    }
                    _lastX = x; _lastZ = z; _haveLast = true;
                    _speedKmh = _spdSmooth;
                }
                else { _haveLast = false; _spdSmooth = 0; _speedKmh = 0; _vx = 0; _vz = 0; }

                _lblStatus.Text = active ? "Telemetry: LIVE" : "Telemetry: plugin found, waiting for a loaded save...";
                _lblStatus.ForeColor = active ? Color.Green : Color.DarkGoldenrod;
                _lblPos.Text = active
                    ? $"X: {_x:0}   Z: {_z:0}   alt: {_y:0}   speed: {_speedKmh:0} km/h"
                    : "X: --   Z: --   alt: --   speed: --";
            }
            catch
            {
                _telemetryOk = false;
                try { _acc?.Dispose(); } catch { }
                try { _mmf?.Dispose(); } catch { }
                _acc = null; _mmf = null;
                _lblStatus.Text = "Telemetry: waiting for game + plugin...";
                _lblStatus.ForeColor = Color.Firebrick;
                _lblPos.Text = "X: --   Z: --   alt: --   speed: --";
            }
        }

        // ---------- tunnel geometry ----------
        bool IsInAnyTunnel(double px, double pz)
        {
            foreach (var t in _tunnels)
                if (DistToSegment(px, pz, t.Ax, t.Az, t.Bx, t.Bz) <= t.HalfWidth)
                    return true;
            foreach (var t in _manual)
                if (DistToSegment(px, pz, t.Ax, t.Az, t.Bx, t.Bz) <= t.HalfWidth)
                    return true;
            return false;
        }

        static double DistToSegment(double px, double pz, double ax, double az, double bx, double bz)
        {
            double dx = bx - ax, dz = bz - az;
            double len2 = dx * dx + dz * dz;
            double tt = len2 <= 1e-9 ? 0 : ((px - ax) * dx + (pz - az) * dz) / len2;
            if (tt < 0) tt = 0; else if (tt > 1) tt = 1;
            double cx = ax + tt * dx, cz = az + tt * dz;
            double ex = px - cx, ez = pz - cz;
            return Math.Sqrt(ex * ex + ez * ez);
        }

        void TagEntry()
        {
            if (!_telemetryOk) { MessageBox.Show("No live telemetry yet — drive in-game first."); return; }
            _entX = _x; _entZ = _z; _haveEntry = true;
            _btnExit.Enabled = true;
            _lblStatus.Text = $"Entry tagged at X {_x:0}, Z {_z:0} — now drive out and Tag Exit.";
            _lblStatus.ForeColor = Color.Blue;
        }

        void TagExit()
        {
            if (!_haveEntry) return;
            var t = new Tunnel
            {
                Name = string.IsNullOrWhiteSpace(_txtName.Text) ? "Tunnel" : _txtName.Text.Trim(),
                Ax = _entX, Az = _entZ, Bx = _x, Bz = _z,
                HalfWidth = (double)_numWidth.Value
            };
            _manual.Add(t);
            _haveEntry = false;
            _btnExit.Enabled = false;
            RefreshList();
            SaveDb();
            _lblStatus.Text = $"Saved \"{t.Name}\" (length {Math.Sqrt((t.Bx-t.Ax)*(t.Bx-t.Ax)+(t.Bz-t.Az)*(t.Bz-t.Az)):0} m). Drive through it to test.";
            _lblStatus.ForeColor = Color.Green;
        }

        void DeleteSelected()
        {
            int i = _lstTunnels.SelectedIndex;
            if (i >= 0 && i < _manual.Count) { _manual.RemoveAt(i); RefreshList(); SaveDb(); }
        }

        void RefreshList()
        {
            // the listbox holds MANUAL tags only; the generated DB is thousands of segments
            _lstTunnels.Items.Clear();
            foreach (var t in _manual) _lstTunnels.Items.Add($"{t.Name}  ({t.Ax:0},{t.Az:0} -> {t.Bx:0},{t.Bz:0})  w{t.HalfWidth:0}");
            Text = $"ETS2 Tunnel Radio — {_tunnels.Count} auto + {_manual.Count} manual";
        }

        void LoadDb()
        {
            string dbg = Path.Combine(Path.GetTempPath(), "ETR-debug.log");
            try
            {
                // first run after unzip: install the bundled DB from next to the exe
                string exeDir = AppContext.BaseDirectory;
                if (!File.Exists(DbFile) && File.Exists(Path.Combine(exeDir, "tunnels.json")))
                {
                    Directory.CreateDirectory(DataDir);
                    File.Copy(Path.Combine(exeDir, "tunnels.json"), DbFile, false);
                    string m = Path.Combine(exeDir, "tunnels.meta.json");
                    if (File.Exists(m)) File.Copy(m, Path.Combine(DataDir, "tunnels.meta.json"), true);
                }
                File.AppendAllText(dbg, $"[{DateTime.Now:HH:mm:ss}] DataDir={DataDir}  DbFile={DbFile}  Exists={File.Exists(DbFile)}\r\n");
                if (File.Exists(DbFile))
                {
                    var list = JsonSerializer.Deserialize<List<Tunnel>>(File.ReadAllText(DbFile));
                    if (list != null) { _tunnels.Clear(); _tunnels.AddRange(list); }
                }
                if (File.Exists(ManualFile))
                {
                    var list = JsonSerializer.Deserialize<List<Tunnel>>(File.ReadAllText(ManualFile));
                    if (list != null) { _manual.Clear(); _manual.AddRange(list); }
                }
                string metaFile = Path.Combine(DataDir, "tunnels.meta.json");
                if (File.Exists(metaFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaFile));
                    if (doc.RootElement.TryGetProperty("gameVersion", out var gv)) _dbVersion = gv.GetString() ?? "";
                }
                File.AppendAllText(dbg, $"[{DateTime.Now:HH:mm:ss}] Loaded {_tunnels.Count} auto + {_manual.Count} manual tunnels OK (DB for game {_dbVersion})\r\n");
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(dbg, $"[{DateTime.Now:HH:mm:ss}] LOAD FAILED: {ex}\r\n"); } catch { }
            }
            RefreshList();
        }

        void SaveDb()
        {
            // only manual tags are saved; the generated DB (tunnels.json) is never written by the app
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(ManualFile, JsonSerializer.Serialize(_manual, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        class AppSettings
        {
            public string StreamUrl { get; set; }
            public bool ProxyOn { get; set; }
            public int DelaySec { get; set; } = 5;
            public int Variant { get; set; }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (s == null) return;
                if (!string.IsNullOrWhiteSpace(s.StreamUrl)) _txtStream.Text = s.StreamUrl;
                _numDelay.Value = Math.Max(_numDelay.Minimum, Math.Min(_numDelay.Maximum, s.DelaySec));
                if (s.Variant >= 0 && s.Variant < _cmbVariant.Items.Count) _cmbVariant.SelectedIndex = s.Variant;
                _chkProxy.Checked = s.ProxyOn;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings
                {
                    StreamUrl = _txtStream.Text.Trim(),
                    ProxyOn = _chkProxy.Checked,
                    DelaySec = (int)_numDelay.Value,
                    Variant = _cmbVariant.SelectedIndex,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // Rewrites ONLY the URL field of every stream_data entry to point at this app
        // (http://127.0.0.1:17771/s/<idx>), keeping names/genres/count/structure untouched —
        // the game list looks identical, but every station becomes tunnel-aware.
        void TakeOverStations()
        {
            try
            {
                string file = Path.Combine(GameDocs, "live_streams.sii");
                if (!File.Exists(file)) { MessageBox.Show("live_streams.sii not found in Documents\\Euro Truck Simulator 2.\nStart the game once, then retry."); return; }
                string txt = File.ReadAllText(file);
                string backup = Path.Combine(GameDocs, "live_streams.backup-tunnelradio.sii");
                if (!txt.Contains("127.0.0.1:" + RadioProxy.Port))
                    File.WriteAllText(backup, txt);   // only back up a clean (non-taken-over) list
                var found = new List<Station>();
                string outTxt = System.Text.RegularExpressions.Regex.Replace(txt,
                    @"(stream_data\[(\d+)\]\s*:\s*"")([^""|]+)(\|[^""]*"")",
                    m =>
                    {
                        int idx = int.Parse(m.Groups[2].Value);
                        while (found.Count <= idx) found.Add(null);
                        string orig = m.Groups[3].Value;
                        string rest = m.Groups[4].Value;            // "|Name|Genre|Lang|Bitrate|Fav""
                        var parts = rest.TrimEnd('"').Split('|');
                        string nm = parts.Length > 1 ? parts[1] : ("Station " + idx);
                        if (orig.Contains("127.0.0.1:" + RadioProxy.Port))
                        {
                            // already ours — keep the mapping we know
                            found[idx] = idx < _stations.Count ? _stations[idx] : new Station { Url = null, Name = nm };
                            return m.Value;
                        }
                        found[idx] = new Station { Url = orig, Name = nm };
                        return m.Groups[1].Value + $"http://127.0.0.1:{RadioProxy.Port}/s/{idx}" + rest;
                    });
                if (found.Count == 0) { MessageBox.Show("No stations found in live_streams.sii — nothing to do."); return; }
                _stations.Clear();
                _stations.AddRange(found);
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(StationsFile, JsonSerializer.Serialize(_stations, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(file, outTxt);
                if (!_chkProxy.Checked) _chkProxy.Checked = true;   // engine must run for the game's radio to work
                MessageBox.Show($"Done — {found.FindAll(u => u != null && u.Url != null).Count} stations now route through the tunnel engine.\n" +
                    "(Original list backed up; \"Restore originals\" puts it back.)\n\n" +
                    "Restart ETS2 and tune to ANY of your stations.\n" +
                    "IMPORTANT: keep this app running while you play.");
            }
            catch (Exception ex) { MessageBox.Show("Could not update live_streams.sii:\n" + ex.Message); }
        }

        void RestoreStations()
        {
            try
            {
                string file = Path.Combine(GameDocs, "live_streams.sii");
                string backup = Path.Combine(GameDocs, "live_streams.backup-tunnelradio.sii");
                if (!File.Exists(backup)) { MessageBox.Show("No backup found (live_streams.backup-tunnelradio.sii)."); return; }
                File.Copy(backup, file, true);
                MessageBox.Show("Original station list restored. Restart ETS2 to see it.");
            }
            catch (Exception ex) { MessageBox.Show("Could not restore:\n" + ex.Message); }
        }

        void LoadStations()
        {
            try
            {
                if (!File.Exists(StationsFile)) return;
                string json = File.ReadAllText(StationsFile);
                try
                {
                    var list = JsonSerializer.Deserialize<List<Station>>(json);
                    if (list != null && list.Exists(s => s != null && s.Url != null))
                    { _stations.Clear(); _stations.AddRange(list); return; }
                }
                catch { }
                // migrate from the old plain-URL format; recover names from the game's list
                var urls = JsonSerializer.Deserialize<List<string>>(json);
                if (urls == null) return;
                _stations.Clear();
                var names = new Dictionary<int, string>();
                try
                {
                    string sii = File.ReadAllText(Path.Combine(GameDocs, "live_streams.sii"));
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sii, @"stream_data\[(\d+)\]\s*:\s*""[^""|]*\|([^|""]*)"))
                        names[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
                }
                catch { }
                for (int i = 0; i < urls.Count; i++)
                    _stations.Add(new Station { Url = urls[i], Name = names.TryGetValue(i, out var n) ? n : ("Station " + i) });
                File.WriteAllText(StationsFile, JsonSerializer.Serialize(_stations, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ---------- audio ----------
        void BuildAudio()
        {
            _radio = new SoundPlayer(new MemoryStream(MakeToneWav(220, 0.18, 1.0)));
            try { _radio.Load(); } catch { }
        }

        void UpdateAudio(bool inTunnel)
        {
            // in game-radio mode the game plays our proxy stream; keep the local speakers quiet
            bool proxyMode = _chkProxy.Checked && !_chkForce.Checked;
            string want = proxyMode ? "silence" : inTunnel ? "static" : (_chkTone.Checked ? "radio" : "silence");
            if (want == _audioState) return;
            _audioState = want;
            try
            {
                _radio.Stop(); _static.Stop();
                if (want == "static") _static.PlayLooping();
                else if (want == "radio") _radio.PlayLooping();
            }
            catch { }
        }

        void Cleanup()
        {
            _timer?.Stop();
            _proxy?.Stop();
            try { _radio?.Stop(); _static?.Stop(); } catch { }
            try { _acc?.Dispose(); _mmf?.Dispose(); } catch { }
        }

        // 16-bit mono PCM WAV of a sine tone (freq chosen to loop seamlessly at 1s).
        static byte[] MakeToneWav(int freq, double amp, double seconds)
        {
            int rate = 44100; int n = (int)(rate * seconds);
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(Math.Sin(2 * Math.PI * freq * i / rate) * amp * short.MaxValue);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int dataBytes = pcm.Length * 2;
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(rate);
            bw.Write(rate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            foreach (short s in pcm) bw.Write(s);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
