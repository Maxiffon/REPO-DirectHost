// Offline tests for REPO DirectHost: the REAL Photon client (game's Photon3Unity3D.dll) talks to our server
// over loopback UDP through a lossy proxy.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using ExitGames.Client.Photon;
using RepoDirectHost.Net;
using RepoDirectHost.Net.Enet;

internal static class Program
{
    private static int Main(string[] args)
    {
        string test = args.Length > 0 ? args[0] : "all";
        if (test == "player")
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            bool pl = LiveTest.Player(args[1], args.Length > 2 ? double.Parse(args[2], inv) : 0, args.Length > 3 ? int.Parse(args[3]) : 900,
                args.Length > 4 ? args[4] : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"r2modmanPlus-local\REPO\profiles\Default\BepInEx\directhost-views.txt"));
            Console.WriteLine(pl ? "RESULT: PASS" : "RESULT: FAIL");
            return pl ? 0 : 1;
        }
        if (test == "stun")
        {
            var all = RepoDirectHost.Game.Stun.QueryAll();
            foreach (var f in all) Console.WriteLine($"  {f.ip}  seen by {f.servers}");
            Console.WriteLine(all.Count > 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return all.Count > 0 ? 0 : 1;
        }
        if (test == "hoststun")
        {
            // hoststun [punchPort] — the host's punch socket exactly as in the game (UdpServer loop + AddSocket + StartStun):
            // does it learn its own public endpoint (the "pe=" the host publishes)?
            int pp = args.Length > 1 ? int.Parse(args[1]) : 47823;
            var photon = new RepoDirectHost.Server.PhotonServer(UdpServer.Now, (c, r) => { });
            var udp = new UdpServer(photon, IPAddress.Any, (47821, RepoDirectHost.Server.Role.Master), (47822, RepoDirectHost.Server.Role.Game)) { Tick = photon.Tick, OnError = e => Console.WriteLine("  ERROR " + e) };
            var ph = new PunchHost(new TunnelHost(udp), pp) { Log = s => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {s}") };
            udp.AddSocket(ph.Sock, ph.OnDatagram, ph.Tick);
            Console.WriteLine($"== host punch socket STUN on local port {ph.Port}");
            ph.StartStun();
            udp.Start();
            Thread.Sleep(4000);
            var mapped = ph.Mapped();
            udp.Dispose();
            Console.WriteLine("  mapped: " + (mapped.Count > 0 ? string.Join(", ", mapped) : "NOTHING"));
            Console.WriteLine(mapped.Count > 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return mapped.Count > 0 ? 0 : 1;
        }
        if (test == "nat")
        {
            // nat — one UDP socket asks every STUN server: the same public port for all = "cone" NAT (punch-through
            // works from this PC), a different port per server = symmetric NAT (works only towards open/cone peers)
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            s.ReceiveTimeout = 300;
            Console.WriteLine($"== NAT check from local port {((IPEndPoint)s.LocalEndPoint).Port}");
            var servers = RepoDirectHost.Net.StunCodec.Resolve();
            var tids = new List<(byte[] tid, string name)>();
            for (int round = 0; round < 3; round++)
                foreach (var (ep, name) in servers) { var req = RepoDirectHost.Net.StunCodec.Request(out var tid); tids.Add((tid, name)); s.SendTo(req, ep); }
            var seen = new Dictionary<string, string>();
            var buf = new byte[512];
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = s.ReceiveFrom(buf, ref from); } catch (System.Net.Sockets.SocketException) { continue; }
                foreach (var t in tids) { var m = RepoDirectHost.Net.StunCodec.Parse(buf, n, t.tid); if (m != null) seen[t.name] = m.ToString(); }
            }
            foreach (var kv in seen) Console.WriteLine($"  {kv.Key,-24} sees {kv.Value}");
            foreach (var g in seen.Values.GroupBy(v => v.Substring(0, v.LastIndexOf(':'))))
                Console.WriteLine($"  {g.Key}: {(g.Distinct().Count() == 1 ? "same port for every server -> cone NAT" : "different ports -> symmetric NAT")} ({string.Join(", ", g.Distinct())})");
            return seen.Count > 0 ? 0 : 1;
        }
        if (test == "liveprobe")
        {
            // liveprobe ip1,ip2,... [masterPort] [gamePort] — probes a running game's server like a client would
            var ips = args[1].Split(',').Select(IPAddress.Parse).ToList();
            int m = args.Length > 2 ? int.Parse(args[2]) : 7777, g = args.Length > 3 ? int.Parse(args[3]) : 7778;
            Console.WriteLine($"== LIVE probe of [{string.Join(", ", ips)}] on UDP {m}+{g}");
            var rtt = Probe.Measure(ips, m, g, 0, 1500, _ => false, s => Console.WriteLine("  " + s));
            for (int i = 0; i < rtt.Length; i++) Console.WriteLine($"  {ips[i]}: {(rtt[i] >= 0 ? rtt[i] + " ms" : "no answer")}");
            bool any = rtt.Any(x => x >= 0);
            Console.WriteLine(any ? "RESULT: PASS" : "RESULT: FAIL");
            return any ? 0 : 1;
        }
        if (test == "stay")
        {
            bool st = LiveTest.Stay(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0, args.Length > 3 ? int.Parse(args[3]) : 300);
            Console.WriteLine(st ? "RESULT: PASS" : "RESULT: FAIL");
            return st ? 0 : 1;
        }
        if (test == "live")
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            bool live = LiveTest.Run(args[1], args.Length > 2 ? double.Parse(args[2], inv) : 0, args.Length > 3 ? args[3] : "127.0.0.1", args.Length > 4 ? int.Parse(args[4]) : 7777);
            Console.WriteLine(live ? "RESULT: PASS" : "RESULT: FAIL");
            return live ? 0 : 1;
        }
        double loss = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0.10;
        bool ok = true;
        if (test == "enet" || test == "all") ok &= EnetTransportTest.Run(loss);
        if (test == "room" || test == "all") ok &= RoomTest.Run(loss);
        if (test == "probe" || test == "all") ok &= TunnelTest.ProbeRun();
        if (test == "tunnel" || test == "all") ok &= TunnelTest.Run(loss, args.Length > 2 ? int.Parse(args[2]) : 25);
        if (test == "punch" || test == "all") { ok &= PunchTest.SharedPort(); ok &= PunchTest.Run(loss, withSteam: true); ok &= PunchTest.Run(loss, withSteam: false); }
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }
}

/// <summary>UDP proxy between clients and server that drops a share of datagrams in both directions (multi-client).</summary>
internal sealed class LossyProxy : IDisposable
{
    private readonly UdpClient _front;
    private readonly IPEndPoint _server;
    private readonly ConcurrentDictionary<string, UdpClient> _backs = new ConcurrentDictionary<string, UdpClient>();
    private readonly Random _rnd = new Random(12345);
    private readonly double _loss;
    private volatile bool _run = true;
    public int Dropped, Forwarded;

    public LossyProxy(int listenPort, int serverPort, double loss)
    {
        _loss = loss;
        _front = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);
        new Thread(PumpFront) { IsBackground = true }.Start();
    }

    private bool Drop() { lock (_rnd) return _rnd.NextDouble() < _loss; }

    private void PumpFront()
    {
        while (_run)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var data = _front.Receive(ref ep);
                var back = _backs.GetOrAdd(ep.ToString(), _ =>
                {
                    var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var client = ep;
                    new Thread(() => PumpBack(b, client)) { IsBackground = true }.Start();
                    return b;
                });
                if (Drop()) { Interlocked.Increment(ref Dropped); continue; }
                Interlocked.Increment(ref Forwarded);
                back.Send(data, data.Length, _server);
            }
            catch { if (!_run) return; }
        }
    }

    private void PumpBack(UdpClient back, IPEndPoint client)
    {
        while (_run)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var data = back.Receive(ref ep);
                if (Drop()) { Interlocked.Increment(ref Dropped); continue; }
                Interlocked.Increment(ref Forwarded);
                _front.Send(data, data.Length, client);
            }
            catch { if (!_run) return; }
        }
    }

    public void Dispose() { _run = false; _front.Close(); foreach (var b in _backs.Values) b.Close(); }
}
internal sealed class ClientListener : IPhotonPeerListener
{
    public volatile StatusCode LastStatus;
    public readonly List<EventData> Events = new List<EventData>();
    public readonly List<string> Debug = new List<string>();
    public void DebugReturn(DebugLevel level, string message) { if (level <= DebugLevel.WARNING) lock (Debug) Debug.Add(level + ": " + message); }
    public void OnOperationResponse(OperationResponse operationResponse) { }
    public void OnStatusChanged(StatusCode statusCode) { LastStatus = statusCode; }
    public void OnEvent(EventData eventData)
    {
        var copy = new EventData { Code = eventData.Code };
        foreach (var kv in eventData.Parameters) copy.Parameters.Add(kv.Key, kv.Value is byte[] b ? (byte[])b.Clone() : kv.Value);
        lock (Events) Events.Add(copy);
    }
}

internal static class EnetTransportTest
{
    private const int ReliableEvents = 60, UnseqEvents = 100;

    private sealed class Handler : IEnetHandler
    {
        public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();
        public int OpRequestBytes;
        public Func<byte, int, byte[]> MakeEvent;
        public void OnConnected(EnetConnection c) => Log.Enqueue("connected peer " + c.PeerId);
        public void OnDisconnected(EnetConnection c, string reason) => Log.Enqueue("disconnected: " + reason);
        public void OnMessage(EnetConnection c, byte channel, byte[] data, byte mode)
        {
            if (data.Length < 2 || data[0] != 0xF3) return;
            int type = data[1] & 0x7F;
            if (type == 0) { c.SendReliable(0, new byte[] { 0xF3, 1 }); Log.Enqueue("init -> init response"); return; }
            if (type == 2)
            {
                OpRequestBytes = data.Length;
                Log.Enqueue($"op request {data.Length} B on ch{channel}");
                for (int k = 0; k < ReliableEvents; k++) c.SendReliable(0, MakeEvent(1, Size(k)));       // reliable, incl. big ones
                for (int k = 0; k < UnseqEvents; k++) c.SendUnsequenced(1, MakeEvent(2, 40 + k));        // voice-like
            }
        }
    }

    public static int Size(int k) => k % 5 == 0 ? 2500 + k * 50 : 50 + k * 7;   // every 5th > 1200 B (fragmented)

    public static bool Run(double loss)
    {
        Console.WriteLine($"== ENet transport test: real Photon client <-> our server, {loss:P0} loss each way");
        var p16 = new Protocol16();
        var ser = typeof(Protocol16).GetMethod("SerializeEventData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Func<byte, int, byte[]> makeEvent = (code, size) =>
        {
            var e = new EventData { Code = code };
            var payload = new byte[size];
            for (int i = 0; i < size; i++) payload[i] = (byte)(i * 31 + size);
            e.Parameters.Add(245, payload);
            var sb = new StreamBuffer(0);
            ser.Invoke(p16, new object[] { sb, e, false });
            var body = sb.ToArray();
            var msg = new byte[body.Length + 2];
            msg[0] = 0xF3; msg[1] = 4;
            Buffer.BlockCopy(body, 0, msg, 2, body.Length);
            return msg;
        };

        var handler = new Handler { MakeEvent = makeEvent };
        using var server = new UdpServer(50555, handler, IPAddress.Loopback);
        server.Log = s => handler.Log.Enqueue(s);
        server.Start();
        using var proxy = new LossyProxy(50556, 50555, loss);

        var listener = new ClientListener();
        var peer = new PhotonPeer(listener, ConnectionProtocol.Udp) { SerializationProtocolType = SerializationProtocol.GpBinaryV16 };
        var sw = Stopwatch.StartNew();
        peer.Connect("127.0.0.1:50556", "test");
        bool sentOp = false; long connectedAt = -1, doneAt = -1;
        while (sw.ElapsedMilliseconds < 30000)
        {
            peer.Service();
            if (listener.LastStatus == StatusCode.Connect && !sentOp)
            {
                connectedAt = sw.ElapsedMilliseconds;
                var big = new byte[3000];
                for (int i = 0; i < big.Length; i++) big[i] = (byte)i;
                peer.SendOperation(200, new Dictionary<byte, object> { { 1, big } }, SendOptions.SendReliable);
                sentOp = true;
            }
            int reliableGot; lock (listener.Events) reliableGot = listener.Events.Count(e => e.Code == 1);
            if (reliableGot >= ReliableEvents) { doneAt = sw.ElapsedMilliseconds; for (int i = 0; i < 50; i++) { peer.Service(); Thread.Sleep(5); } break; }
            Thread.Sleep(5);
        }

        List<EventData> evs; lock (listener.Events) evs = listener.Events.ToList();
        var rel = evs.Where(e => e.Code == 1).ToList();
        bool orderOk = rel.Count == ReliableEvents;
        for (int k = 0; k < rel.Count && orderOk; k++)
        {
            var b = rel[k].Parameters[245] as byte[];
            if (b == null || b.Length != Size(k)) { orderOk = false; Console.WriteLine($"  event #{k}: size {b?.Length} expected {Size(k)}"); break; }
            for (int i = 0; i < b.Length; i++) if (b[i] != (byte)(i * 31 + b.Length)) { orderOk = false; Console.WriteLine($"  event #{k}: corrupted at {i}"); break; }
        }
        int unseq = evs.Count(e => e.Code == 2);
        EnetConnection conn = server.Host.Connections.FirstOrDefault();

        while (handler.Log.TryDequeue(out var l)) Console.WriteLine("  server: " + l);
        lock (listener.Debug) foreach (var d in listener.Debug.Take(10)) Console.WriteLine("  client: " + d);
        Console.WriteLine($"  connected after {connectedAt} ms; op request at server: {handler.OpRequestBytes} B (sent 3000 B param, fragmented by client)");
        Console.WriteLine($"  reliable events: {rel.Count}/{ReliableEvents}, in order & intact: {orderOk}, all delivered after {doneAt} ms");
        Console.WriteLine($"  unsequenced events: {unseq}/{UnseqEvents} (loss expected ~{loss:P0}, never resent)");
        Console.WriteLine($"  proxy: dropped {proxy.Dropped}, forwarded {proxy.Forwarded}");
        if (conn != null) Console.WriteLine($"  server conn: rtt {conn.Rtt}±{conn.RttVar} ms, datagrams sent {conn.DatagramsSent}, resent cmds {conn.CommandsResent}, fragmented msgs {conn.MessagesFragmented}, unacked now {conn.Unacked}");
        Console.WriteLine($"  client: rtt {peer.RoundTripTime} ms, state {peer.PeerState}");
        peer.Disconnect();
        for (int i = 0; i < 20; i++) { peer.Service(); Thread.Sleep(5); }
        return connectedAt >= 0 && orderOk && handler.OpRequestBytes > 3000 && unseq > 0;
    }
}
