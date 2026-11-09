// Tunnel + probe tests. Real Photon Realtime clients: the host connects to the server directly, the joiner goes
// through TunnelClient -> fake message link (stand-in for Steam: unreliable, lossy, delayed) -> TunnelHost -> the
// same EnetHost instances. Also checks the reachability probe (pick the answering address, tag check, dead ports).
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

internal static class TunnelTest
{
    private const string AppId = "11111111-2222-3333-4444-555555555555";
    private static readonly PropertyInfo GsAddr = typeof(LoadBalancingClient).GetProperty(nameof(LoadBalancingClient.GameServerAddress));

    /// <summary>Unreliable message link with loss and one-way delay, like a Steam P2P connection.</summary>
    private sealed class FakeLink
    {
        private readonly ConcurrentQueue<(long at, byte[] data)> _q = new ConcurrentQueue<(long, byte[])>();
        private readonly Random _rnd;
        private readonly double _loss; private readonly int _delayMs;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public int Sent, Dropped;
        public FakeLink(double loss, int delayMs, int seed) { _loss = loss; _delayMs = delayMs; _rnd = new Random(seed); }
        public void Send(byte[] b, int n)
        {
            Interlocked.Increment(ref Sent);
            lock (_rnd) if (_rnd.NextDouble() < _loss) { Interlocked.Increment(ref Dropped); return; }
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
            var gs = c.GameServerAddress;     // same rewrite the mod does: "@:7778" -> this client's game port
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

    public static bool Run(double loss, int delayMs)
    {
        Console.WriteLine($"== Tunnel test: joiner through TunnelClient -> fake Steam link ({loss:P0} loss, {delayMs} ms each way) -> TunnelHost");
        var log = new ConcurrentQueue<string>();
        UdpServer udp = null;
        var photon = new PhotonServer(UdpServer.Now, (c, r) => udp.Hosts.First(h => h.Role == c.Role).Close(c, r, true))
        { GameServerAddress = "@:47778", Log = s => log.Enqueue(s), Warn = s => log.Enqueue("WARN " + s) };
        udp = new UdpServer(photon, IPAddress.Loopback, (47777, Role.Master), (47778, Role.Game)) { OnError = e => log.Enqueue("ERROR " + e), Tick = photon.Tick, SelectMicros = 1000 };

        var up = new FakeLink(loss, delayMs, 1);
        var down = new FakeLink(loss, delayMs, 2);
        var th = new TunnelHost(udp);
        udp.PollExternal = () => up.Drain(m => th.OnUp("fake", m, m.Length, down.Send));
        udp.Start();
        using var tc = new TunnelClient(Role.Master, Role.Game) { OnError = e => log.Enqueue("TUNNEL ERROR " + e) };
        tc.SendUp = up.Send;
        tc.PollLink = () => down.Drain(m => tc.Deliver(m, m.Length));

        bool ok = true;
        try
        {
            var hostEv = new List<EventData>(); var joinEv = new List<EventData>();
            var host = Client("76561198000000001", 47777, 47778, hostEv);
            bool req = false;
            ok &= Check("host (direct) joined the room", Pump(new[] { host }, () =>
            {
                if (!req && host.State == ClientState.ConnectedToMasterServer)
                {
                    req = true;
                    host.OpJoinOrCreateRoom(new EnterRoomParams { RoomName = "tunnelroom", RoomOptions = new RoomOptions { MaxPlayers = 6, IsVisible = false } });
                }
                return host.State == ClientState.Joined;
            }, 15000));
            var big = new byte[5000]; for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 13);
            host.OpRaiseEvent(202, new Hashtable { { (byte)0, "Level" }, { (byte)5, new object[] { big } } }, new RaiseEventOptions { CachingOption = EventCaching.AddToRoomCache }, SendOptions.SendReliable);
            Pump(new[] { host }, () => false, 200);

            var sw = Stopwatch.StartNew();
            var joiner = Client("76561198000000002", tc.MasterPort, tc.GamePort, joinEv);
            bool jreq = false;
            bool joined = Pump(new[] { host, joiner }, () =>
            {
                if (!jreq && joiner.State == ClientState.ConnectedToMasterServer) { jreq = true; joiner.OpJoinRoom(new EnterRoomParams { RoomName = "tunnelroom" }); }
                return joiner.State == ClientState.Joined;
            }, 20000);
            ok &= Check($"joiner (tunnel) connected to master, moved to game server, joined the room in {sw.ElapsedMilliseconds} ms", joined);
            if (!joined) { Console.WriteLine("  joiner state " + joiner.State); return false; }

            ok &= Check("joiner got the cached 5000 B event intact (fragmented through the tunnel)", Pump(new[] { host, joiner }, () =>
            {
                lock (joinEv) return joinEv.Any(e => e.Code == 202 && e.Parameters.TryGetValue(245, out var d) && d is Hashtable h && h[(byte)5] is object[] a && a[0] is byte[] b && b.Length == 5000 && b.SequenceEqual(big));
            }, 10000));

            // reliable stream both ways + unreliable voice-like frames
            for (int k = 0; k < 100; k++)
            {
                host.OpRaiseEvent(1, new object[] { k, new byte[50 + k * 20] }, RaiseEventOptions.Default, SendOptions.SendReliable);
                joiner.OpRaiseEvent(2, new object[] { k }, RaiseEventOptions.Default, SendOptions.SendReliable);
                host.OpRaiseEvent(203, new object[] { k }, RaiseEventOptions.Default, SendOptions.SendUnreliable);
                Pump(new[] { host, joiner }, () => false, 5);
            }
            bool streams = Pump(new[] { host, joiner }, () =>
            {
                int a, b; lock (joinEv) a = joinEv.Count(e => e.Code == 1); lock (hostEv) b = hostEv.Count(e => e.Code == 2);
                return a == 100 && b == 100;
            }, 15000);
            List<int> order; lock (joinEv) order = joinEv.Where(e => e.Code == 1).Select(e => (int)((object[])e.Parameters[245])[0]).ToList();
            int voice; lock (joinEv) voice = joinEv.Count(e => e.Code == 203);
            ok &= Check($"100 reliable events each way, in order ({order.Count}/100 host->joiner)", streams && order.SequenceEqual(Enumerable.Range(0, 100)));
            if (!streams)
            {
                int back; lock (hostEv) back = hostEv.Count(e => e.Code == 2);
                Console.WriteLine($"  joiner: state {joiner.State}, cause {joiner.DisconnectedCause}, sent-count allowance {joiner.LoadBalancingPeer.SentCountAllowance}, timeout {joiner.LoadBalancingPeer.DisconnectTimeout} ms; joiner->host {back}/100; host: state {host.State}, cause {host.DisconnectedCause}");
                foreach (var l in log) if (l.Contains("left") || l.Contains("timeout") || l.Contains("disconnect")) Console.WriteLine("  server: " + l);
                udp.Post(() =>
                {
                    foreach (var h in udp.Hosts) foreach (var c in h.Connections) Console.WriteLine($"  server conn role {h.Role} {c.Key}: {c.DebugUnacked(h.Now)}");
                });
                Thread.Sleep(100);
                lock (joinEv) Console.WriteLine("  joiner got codes: " + string.Join(" ", joinEv.Where(e => e.Code == 1).Select(e => (int)((object[])e.Parameters[245])[0])));
            }
            ok &= Check($"unreliable frames pass and are never resent ({voice}/100, loss expected ~{loss:P0} each way)", voice > 0 && voice <= 100);

            Pump(new[] { host, joiner }, () => false, 1500);
            int rttJ = joiner.LoadBalancingPeer.RoundTripTime, rttH = host.LoadBalancingPeer.RoundTripTime;
            Console.WriteLine($"  RTT: host direct {rttH} ms, joiner through the tunnel {rttJ} ms (link adds {2 * delayMs} ms by design)");
            ok &= Check($"tunnel adds little on top of the link ({rttJ - rttH - 2 * delayMs} ms)", rttJ - rttH - 2 * delayMs < 25);
            long stray = 0; udp.Post(() => stray = udp.Hosts.SelectMany(h => h.Connections).Sum(c => c.AcksRejected)); Thread.Sleep(50);
            Console.WriteLine($"  link: up {up.Sent} msgs ({up.Dropped} dropped), down {down.Sent} ({down.Dropped} dropped); tunnel up {th.Up} down {th.Down}; stray acks rejected {stray}");

            joiner.Disconnect(); host.Disconnect();
            Pump(new[] { host, joiner }, () => false, 300);
        }
        finally
        {
            udp.Dispose();
            while (log.TryDequeue(out var l)) if (l.StartsWith("ERROR") || l.StartsWith("TUNNEL") || l.StartsWith("WARN")) Console.WriteLine("  server: " + l);
        }
        return ok;
    }

    public static bool ProbeRun()
    {
        Console.WriteLine("== Probe test: pick the address that answers on both ports, check the host tag");
        UdpServer udp = null;
        var photon = new PhotonServer(UdpServer.Now, (c, r) => { }) { GameServerAddress = "@:47788" };
        udp = new UdpServer(photon, IPAddress.Loopback, (47787, Role.Master), (47788, Role.Game)) { ProbeTag = 76561198000000042UL };
        udp.Start();
        bool ok = true;
        try
        {
            var dead = IPAddress.Parse("127.0.0.2");     // nothing listens there (server is bound to 127.0.0.1)
            var live = IPAddress.Loopback;
            var sw = Stopwatch.StartNew();
            var r = Probe.FindFastest(new[] { dead, live }, 47787, 47788, 76561198000000042UL, 700);
            ok &= Check($"answering address chosen: {r?.ToString() ?? "none"} after {sw.ElapsedMilliseconds} ms", r != null && r.Address.Equals(live) && sw.ElapsedMilliseconds < 300);
            var wrong = Probe.FindFastest(new[] { live }, 47787, 47788, 12345UL, 400);
            ok &= Check("another host's server (wrong SteamID tag) is rejected", wrong == null);
            var lines = new List<string>();
            var half = Probe.FindFastest(new[] { live }, 47787, 47799, 0, 400, s => lines.Add(s));
            ok &= Check("only one port reachable -> not usable, reported: " + string.Join(" | ", lines), half == null && lines.Count > 0);
            var rtt = Probe.Measure(new[] { live, dead }, 47787, 47788, 0, 700, i => i == 0);
            ok &= Check($"measure stops early on a preferred address (rtt [{string.Join(",", rtt)}])", rtt[0] >= 0 && rtt[1] == -1);
            ok &= Check($"server answered {udp.ProbesAnswered} probes and created no ENet sessions", udp.ProbesAnswered >= 4 && udp.Hosts.All(h => h.Count == 0));
        }
        finally { udp.Dispose(); }
        return ok;
    }
}
