// NAT punch link test with real Photon Realtime clients:
//  withSteam = true:  the joiner starts on a slow lossy fake "Steam relay" link, the punch link comes up mid-game and
//                     the traffic moves to it, then the host forgets the punch link and the traffic falls back to the
//                     relay — the Photon session must survive both switches with every reliable event in order.
//  withSteam = false: the joiner connects through the punch link only (host knocks first, like a CGNAT host).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using RepoDirectHost.Net;
using RepoDirectHost.Server;
using Hashtable = ExitGames.Client.Photon.Hashtable;

internal static class PunchTest
{
    private const string AppId = "11111111-2222-3333-4444-555555555555";
    private static readonly PropertyInfo GsAddr = typeof(LoadBalancingClient).GetProperty(nameof(LoadBalancingClient.GameServerAddress));
    private const ulong HostId = 76561198000000001UL, JoinerId = 76561198000000002UL;

    private sealed class FakeLink
    {
        private readonly ConcurrentQueue<(long at, byte[] data)> _q = new ConcurrentQueue<(long, byte[])>();
        private readonly Random _rnd; private readonly double _loss; private readonly int _delayMs;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public int Sent;
        public FakeLink(double loss, int delayMs, int seed) { _loss = loss; _delayMs = delayMs; _rnd = new Random(seed); }
        public void Send(byte[] b, int n)
        {
            Interlocked.Increment(ref Sent);
            lock (_rnd) if (_rnd.NextDouble() < _loss) return;
            var copy = new byte[n]; Buffer.BlockCopy(b, 0, copy, 0, n);
            _q.Enqueue((_sw.ElapsedMilliseconds + _delayMs, copy));
        }
        public int Drain(Action<byte[]> deliver)
        {
            int k = 0;
            while (_q.TryPeek(out var m) && m.at <= _sw.ElapsedMilliseconds && _q.TryDequeue(out m)) { deliver(m.data); k++; }
            return k;
        }
    }

    private static bool Check(string what, bool ok) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); return ok; }

    private static LoadBalancingClient Client(string user, int masterPort, int gamePort, List<EventData> events)
    {
        var c = new LoadBalancingClient(ConnectionProtocol.Udp) { AuthValues = new AuthenticationValues(user) };
        c.EventReceived += e =>
        {
            var copy = new EventData { Code = e.Code };
            foreach (var kv in e.Parameters) copy.Parameters.Add(kv.Key, kv.Value is byte[] b ? (byte[])b.Clone() : kv.Value);
            lock (events) events.Add(copy);
        };
        c.StateChanged += (a, b) =>
        {
            var gs = c.GameServerAddress;
            if (gs != null && gs.StartsWith("@:")) GsAddr.SetValue(c, "127.0.0.1:" + gamePort);
        };
        c.ConnectUsingSettings(new AppSettings { AppIdRealtime = AppId, Server = "127.0.0.1", Port = masterPort, UseNameServer = false, Protocol = ConnectionProtocol.Udp, AppVersion = "test", AuthMode = AuthModeOption.Auth });
        return c;
    }

    private static bool Pump(LoadBalancingClient[] cs, Func<bool> until, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var c in cs) c.Service();
            if (until()) return true;
            Thread.Sleep(2);
        }
        return until();
    }

    /// <summary>100 reliable events each way (codes a/b), all must arrive in order.</summary>
    private static bool Stream(LoadBalancingClient host, LoadBalancingClient joiner, List<EventData> hostEv, List<EventData> joinEv, byte a, byte b, int count)
    {
        for (int k = 0; k < count; k++)
        {
            host.OpRaiseEvent(a, new object[] { k, new byte[50 + k * 10] }, RaiseEventOptions.Default, SendOptions.SendReliable);
            joiner.OpRaiseEvent(b, new object[] { k }, RaiseEventOptions.Default, SendOptions.SendReliable);
            Pump(new[] { host, joiner }, () => false, 10);
        }
        bool all = Pump(new[] { host, joiner }, () =>
        {
            int x, y; lock (joinEv) x = joinEv.Count(e => e.Code == a); lock (hostEv) y = hostEv.Count(e => e.Code == b);
            return x == count && y == count;
        }, 20000);
        List<int> o1, o2;
        lock (joinEv) o1 = joinEv.Where(e => e.Code == a).Select(e => (int)((object[])e.Parameters[245])[0]).ToList();
        lock (hostEv) o2 = hostEv.Where(e => e.Code == b).Select(e => (int)((object[])e.Parameters[245])[0]).ToList();
        return all && o1.SequenceEqual(Enumerable.Range(0, count)) && o2.SequenceEqual(Enumerable.Range(0, count));
    }

    /// <summary>
    /// Another program holds a port on one address (like the old relay on the VPN adapter's IP): the punch socket must
    /// not share it (it would lose every datagram to that address), the server listeners must say so.
    /// </summary>
    public static bool SharedPort()
    {
        Console.WriteLine("== Shared port: another program bound to 127.0.0.1:<port> first");
        bool ok = true;
        const int P = 47831;
        using var other = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        other.Bind(new IPEndPoint(IPAddress.Loopback, P));
        using (var s = PunchWire.Bind(new[] { P, P + 1 }, out int bound))
            ok &= Check($"punch socket skipped the shared port (took {bound})", bound == P + 1);
        var photon = new PhotonServer(UdpServer.Now, (c, r) => { });
        using (var udp = new UdpServer(photon, IPAddress.Any, (P, Role.Master), (P + 2, Role.Game)))
            ok &= Check($"server warns about the shared listener port ({udp.BindWarnings.Count} warning)", udp.BindWarnings.Count == 1 && udp.BindWarnings[0].Contains("UDP " + P));
        return ok;
    }

    public static bool Run(double loss, bool withSteam)
    {
        Console.WriteLine(withSteam
            ? $"== Punch test: joiner moves fake Steam relay ({loss:P0} loss, 40 ms each way) -> own punch link -> back, mid-game"
            : "== Punch test: joiner connects through the own punch link only (host knocks first)");
        var log = new ConcurrentQueue<string>();
        UdpServer udp = null;
        int mPort = withSteam ? 47801 : 47811, gPort = mPort + 1, pPort = mPort + 2, cPort = mPort + 3;
        var photon = new PhotonServer(UdpServer.Now, (c, r) => udp.Hosts.First(h => h.Role == c.Role).Close(c, r, true))
        { GameServerAddress = "@:" + gPort, Log = s => log.Enqueue(s), Warn = s => log.Enqueue("WARN " + s) };
        udp = new UdpServer(photon, IPAddress.Loopback, (mPort, Role.Master), (gPort, Role.Game)) { OnError = e => log.Enqueue("ERROR " + e), Tick = photon.Tick, SelectMicros = 1000 };
        var th = new TunnelHost(udp);
        var ph = new PunchHost(th, pPort) { Tag = HostId, Log = s => log.Enqueue(s) };
        udp.AddSocket(ph.Sock, ph.OnDatagram, ph.Tick);

        var up = new FakeLink(loss, 40, 1);
        var down = new FakeLink(loss, 40, 2);
        if (withSteam) udp.PollExternal = () => up.Drain(m => th.OnUp(JoinerId.ToString(), "steam:1", m, 0, m.Length, down.Send));
        udp.Start();

        // client: punch socket + tunnel; "steam" = the fake link. Host address list holds a dead address too.
        var pc = new PunchClient(JoinerId, HostId, new[] { new IPEndPoint(IPAddress.Loopback, 9) }, new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback }, pPort, cPort) { Log = s => log.Enqueue("client " + s) };
        bool usePunch = !withSteam;
        using var tc = new TunnelClient(Role.Master, Role.Game, pc.Sock, pc.OnDatagram) { OnError = e => log.Enqueue("TUNNEL ERROR " + e) };
        pc.Deliver = (b, o, n) => tc.Deliver(b, o, n);
        tc.SendUp = (b, n) => { if (usePunch && pc.Alive) pc.Send(b, n); else if (withSteam) up.Send(b, n); };
        tc.PollLink = () => { pc.Tick(); return withSteam ? down.Drain(m => tc.Deliver(m, m.Length)) : 0; };

        // what GameHooks does with the client's lobby member data: endpoints (a dead one first) + key
        void Publish() => udp.Post(() => ph.SetClient(JoinerId, (byte[])pc.Key.Clone(), new List<IPEndPoint> { new IPEndPoint(IPAddress.Loopback, 9), new IPEndPoint(IPAddress.Loopback, pc.LocalPort) }));
        if (!withSteam) Publish();

        bool ok = true;
        var hostEv = new List<EventData>(); var joinEv = new List<EventData>();
        LoadBalancingClient host = null, joiner = null;
        try
        {
            host = Client(HostId.ToString(), mPort, gPort, hostEv);
            bool req = false;
            ok &= Check("host (direct) joined the room", Pump(new[] { host }, () =>
            {
                if (!req && host.State == ClientState.ConnectedToMasterServer)
                {
                    req = true;
                    host.OpJoinOrCreateRoom(new EnterRoomParams { RoomName = "punchroom", RoomOptions = new RoomOptions { MaxPlayers = 6, IsVisible = false } });
                }
                return host.State == ClientState.Joined;
            }, 15000));
            var big = new byte[5000]; for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);
            host.OpRaiseEvent(202, new Hashtable { { (byte)0, "Level" }, { (byte)5, new object[] { big } } }, new RaiseEventOptions { CachingOption = EventCaching.AddToRoomCache }, SendOptions.SendReliable);
            Pump(new[] { host }, () => false, 200);

            if (!withSteam)
                ok &= Check("punch link up before connecting (host knocked, client answered)", Pump(new[] { host }, () => pc.Alive, 5000));

            var sw = Stopwatch.StartNew();
            joiner = Client(JoinerId.ToString(), tc.MasterPort, tc.GamePort, joinEv);
            bool jreq = false;
            bool joined = Pump(new[] { host, joiner }, () =>
            {
                if (!jreq && joiner.State == ClientState.ConnectedToMasterServer) { jreq = true; joiner.OpJoinRoom(new EnterRoomParams { RoomName = "punchroom" }); }
                return joiner.State == ClientState.Joined;
            }, 20000);
            ok &= Check($"joiner joined the room through the {(withSteam ? "fake relay" : "punch link")} in {sw.ElapsedMilliseconds} ms", joined);
            if (!joined) { Console.WriteLine("  joiner state " + joiner.State); return false; }
            ok &= Check("joiner got the cached 5000 B event intact", Pump(new[] { host, joiner }, () =>
            {
                lock (joinEv) return joinEv.Any(e => e.Code == 202 && e.Parameters.TryGetValue(245, out var d) && d is Hashtable h && h[(byte)5] is object[] a && a[0] is byte[] b && b.SequenceEqual(big));
            }, 10000));

            if (withSteam)
            {
                Pump(new[] { host, joiner }, () => false, 2500);
                int rttRelay = joiner.LoadBalancingPeer.RoundTripTime;

                // the client publishes its member data; the host knocks; the link comes up; traffic moves over
                Publish();
                ok &= Check("punch link came up mid-game", Pump(new[] { host, joiner }, () => pc.Alive, 5000));
                usePunch = true;
                ok &= Check("100 reliable events each way in order while moving relay -> punch", Stream(host, joiner, hostEv, joinEv, 1, 2, 100));
                Pump(new[] { host, joiner }, () => false, 2500);
                int rttPunch = joiner.LoadBalancingPeer.RoundTripTime;
                string last = null; udp.Post(() => th.LastLink.TryGetValue(JoinerId.ToString(), out last)); Thread.Sleep(50);
                ok &= Check($"traffic moved to the punch link (host answers on '{last}'; Photon RTT {rttRelay} -> {rttPunch} ms)", last == "punch:" + JoinerId && rttPunch + 40 < rttRelay);

                // the punch link dies (host forgets it): the client notices within ~4 s and falls back to the relay
                udp.Post(() => ph.RemoveClient(JoinerId));
                ok &= Check("client noticed the dead punch link", Pump(new[] { host, joiner }, () => !pc.Alive, 8000));
                ok &= Check("50 reliable events each way in order after falling back to the relay", Stream(host, joiner, hostEv, joinEv, 3, 4, 50));

                // the player re-enters the lobby: the client starts over and publishes again, the host knocks again
                string before = pc.MemberValue();
                pc.Rearm();
                ok &= Check("rearm publishes a new attempt number (host sees changed member data)", Pump(new[] { host, joiner }, () => { var v = pc.MemberValue(); return v != null && v != before; }, 4000));
                Publish();
                ok &= Check("punch link back up after the rejoin", Pump(new[] { host, joiner }, () => pc.Alive, 5000));
                ok &= Check("30 reliable events each way in order after moving back to the punch link", Stream(host, joiner, hostEv, joinEv, 5, 6, 30));
                ok &= Check("joiner never left the room during all three switches", joiner.State == ClientState.Joined);
            }
            else
            {
                ok &= Check("100 reliable events each way in order over the punch link", Stream(host, joiner, hostEv, joinEv, 1, 2, 100));
                Pump(new[] { host, joiner }, () => false, 1500);
                Console.WriteLine($"  RTT: host {host.LoadBalancingPeer.RoundTripTime} ms, joiner through the punch link {joiner.LoadBalancingPeer.RoundTripTime} ms, link RTT {pc.Rtt} ms; client sent {pc.Sent}, received {pc.Received} frames");
                ok &= Check("punch link adds little", joiner.LoadBalancingPeer.RoundTripTime - host.LoadBalancingPeer.RoundTripTime < 25);
            }
            int sessions = 0; udp.Post(() => sessions = udp.HostFor(Role.Game).Connections.Count(c => c.Key.StartsWith(JoinerId + "/"))); Thread.Sleep(50);
            ok &= Check($"exactly one game-server session for the joiner on the host ({sessions})", sessions == 1);

            joiner.Disconnect(); host.Disconnect();
            Pump(new[] { host, joiner }, () => false, 300);
        }
        finally
        {
            udp.Dispose();
            pc.Dispose();
            foreach (var l in log) if (l.StartsWith("ERROR") || l.StartsWith("TUNNEL") || l.StartsWith("WARN") || l.Contains("punch:")) Console.WriteLine("  log: " + l);
        }
        return ok;
    }
}
