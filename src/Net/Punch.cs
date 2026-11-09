// REPO DirectHost — own NAT punch-through: a direct UDP link between the host and a client when neither the host's
// address nor Steam's own punch-through (ICE) works — e.g. a host behind the provider's CGNAT and a client with a
// forwarded port, or two "cone" NATs that Steam leaves on its relay anyway.
//
// Signalling goes through Steam lobby data (no extra server):
//   host   publishes "pp=<punch port>" in the lobby candidates (GameHooks.HostCandidates);
//   client binds one UDP socket (7779/7778/7777 first: those are the ports friends usually forward), learns its
//          public endpoint(s) with STUN from that socket and publishes them + a random key as lobby MEMBER data;
//   host   sends HELLO from its punch socket to every published endpoint (this opens the host's own NAT/CGNAT
//          towards the client); at the same time the client sends PINGs to the host's public addresses (opens the
//          client's NAT towards the host). Whoever gets through first is answered at the endpoint the packet really
//          came from, so the link works for every NAT pair except symmetric <-> symmetric.
// After that the client pings once a second (RTT, NAT keep-alive) and tunnel frames flow both ways:
//   HELLO "DHPUNCH0" key(8) hostTag(8)          host -> client      24 B
//   PING  "DHPUNCH1" key(8) clientId(8) seq(4)  client -> host      28 B
//   PONG  "DHPUNCH2" key(8) hostTag(8) seq(4)   host -> client      28 B
//   DATA  0xD7 + tunnel frame (see Tunnel.cs)   both ways, only between endpoints that completed PING/PONG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RepoDirectHost.Net
{
    internal static class PunchWire
    {
        public const byte Data = 0xD7;
        public const int HelloLen = 24, PingLen = 28, PongLen = 28;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DHPUNCH");

        /// <summary>'0' hello, '1' ping, '2' pong, or 0.</summary>
        public static char Type(byte[] b, int n)
        {
            if (n < 8) return '\0';
            for (int i = 0; i < 7; i++) if (b[i] != Magic[i]) return '\0';
            return (char)b[7];
        }

        private static int Head(byte[] b, char t, byte[] key)
        {
            Buffer.BlockCopy(Magic, 0, b, 0, 7);
            b[7] = (byte)t;
            Buffer.BlockCopy(key, 0, b, 8, 8);
            return 16;
        }

        public static int Hello(byte[] b, byte[] key, ulong hostTag) { Head(b, '0', key); PutU64(b, 16, hostTag); return HelloLen; }
        public static int Ping(byte[] b, byte[] key, ulong clientId, uint seq) { Head(b, '1', key); PutU64(b, 16, clientId); PutU32(b, 24, seq); return PingLen; }
        public static int Pong(byte[] b, byte[] key, ulong hostTag, uint seq) { Head(b, '2', key); PutU64(b, 16, hostTag); PutU32(b, 24, seq); return PongLen; }

        public static bool KeyIs(byte[] b, byte[] key)
        {
            for (int i = 0; i < 8; i++) if (b[8 + i] != key[i]) return false;
            return true;
        }

        public static void PutU64(byte[] b, int at, ulong v) { for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i)); }
        public static ulong GetU64(byte[] b, int at) { ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)b[at + i] << (8 * i); return v; }
        public static void PutU32(byte[] b, int at, uint v) { for (int i = 0; i < 4; i++) b[at + i] = (byte)(v >> (8 * i)); }
        public static uint GetU32(byte[] b, int at) { uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)b[at + i] << (8 * i); return v; }

        public static string Hex(byte[] k) { var sb = new StringBuilder(); foreach (var x in k) sb.Append(x.ToString("x2")); return sb.ToString(); }
        public static byte[] FromHex(string s)
        {
            if (s == null || s.Length != 16) return null;
            var k = new byte[8];
            for (int i = 0; i < 8; i++) if (!byte.TryParse(s.Substring(2 * i, 2), System.Globalization.NumberStyles.HexNumber, null, out k[i])) return null;
            return k;
        }

        /// <summary>
        /// A UDP socket for the server/punch links. Exclusive: Windows then refuses the bind when another program holds
        /// the port on any address — otherwise the bind succeeds and that program silently receives every datagram sent
        /// to its address (e.g. an old relay on the VPN adapter's IP got all STUN answers of the host's punch socket).
        /// </summary>
        public static Socket NewSocket(bool exclusive)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }   // ignore ICMP port-unreachable
            if (exclusive) try { s.ExclusiveAddressUse = true; } catch { }
            s.ReceiveBufferSize = 1 << 20;
            s.SendBufferSize = 1 << 20;
            return s;
        }

        /// <summary>First free (not shared) port of <paramref name="ports"/>, else an ephemeral one.</summary>
        public static Socket Bind(IEnumerable<int> ports, out int bound)
        {
            foreach (int p in ports)
            {
                var s = NewSocket(true);
                try { s.Bind(new IPEndPoint(IPAddress.Any, p)); bound = p; return s; }
                catch (SocketException) { s.Close(); }
            }
            var e = NewSocket(true);
            e.Bind(new IPEndPoint(IPAddress.Any, 0));
            bound = ((IPEndPoint)e.LocalEndPoint).Port;
            return e;
        }

        public static bool TryParseEndpoint(string s, out IPEndPoint ep)
        {
            ep = null;
            int i = s.LastIndexOf(':');
            if (i <= 0 || !IPAddress.TryParse(s.Substring(0, i), out var a) || !int.TryParse(s.Substring(i + 1), out int p) || p <= 0 || p > 65535) return false;
            ep = new IPEndPoint(a, p);
            return true;
        }
    }

    /// <summary>STUN binding request/response (RFC 5389), shared by the host's address discovery and the punch client.</summary>
    internal static class StunCodec
    {
        public static readonly string[] Servers =
        {
            "stun.sipnet.ru:3478",          // Russia
            "stun.nextcloud.com:3478",      // Germany (Hetzner)
            "stun.cloudflare.com:3478",
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
        };

        public static byte[] Request(out byte[] tid)
        {
            var req = new byte[20];
            req[1] = 0x01;                                                     // Binding Request
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;        // magic cookie
            tid = new byte[12];
            lock (Rnd) Rnd.NextBytes(tid);
            Buffer.BlockCopy(tid, 0, req, 8, 12);
            return req;
        }
        private static readonly Random Rnd = new Random();

        public static bool IsResponse(byte[] r, int n) => n >= 20 && r[0] == 0x01 && r[1] == 0x01 && r[4] == 0x21 && r[5] == 0x12 && r[6] == 0xA4 && r[7] == 0x42;

        /// <summary>The mapped (public) IPv4 endpoint from a Binding Success Response to <paramref name="tid"/>, or null.</summary>
        public static IPEndPoint Parse(byte[] r, int n, byte[] tid)
        {
            if (!IsResponse(r, n)) return null;
            for (int i = 0; i < 12; i++) if (r[8 + i] != tid[i]) return null;
            int len = (r[2] << 8) | r[3];
            int p = 20;
            while (p + 4 <= 20 + len && p + 4 <= n)
            {
                int type = (r[p] << 8) | r[p + 1];
                int alen = (r[p + 2] << 8) | r[p + 3];
                int v = p + 4;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && v + 8 <= n && r[v + 1] == 0x01)
                {
                    bool x = type == 0x0020;
                    int port = (r[v + 2] << 8) | r[v + 3];
                    if (x) port ^= 0x2112;
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = x ? (byte)(r[v + 4 + i] ^ r[4 + i]) : r[v + 4 + i];
                    return new IPEndPoint(new IPAddress(b), port);
                }
                p = v + alen + ((4 - alen % 4) % 4);
            }
            return null;
        }

        /// <summary>Resolves the STUN servers (IPv4), in parallel; slow or failing names are skipped.</summary>
        public static List<(IPEndPoint ep, string name)> Resolve(int timeoutMs = 2000)
        {
            var found = new List<(IPEndPoint, string)>();
            var threads = new List<Thread>();
            foreach (var s in Servers)
            {
                var server = s;
                var t = new Thread(() =>
                {
                    try
                    {
                        var parts = server.Split(':');
                        var a = Array.Find(Dns.GetHostAddresses(parts[0]), x => x.AddressFamily == AddressFamily.InterNetwork);
                        if (a != null) lock (found) found.Add((new IPEndPoint(a, int.Parse(parts[1])), parts[0]));
                    }
                    catch { }
                }) { IsBackground = true, Name = "DirectHost DNS " + server };
                t.Start();
                threads.Add(t);
            }
            var sw = Stopwatch.StartNew();
            foreach (var t in threads) t.Join(Math.Max(0, timeoutMs - (int)sw.ElapsedMilliseconds));
            lock (found) return new List<(IPEndPoint, string)>(found);
        }
    }

    /// <summary>Host side of the punch link. Every method runs on the UdpServer thread (socket registered with AddSocket).</summary>
    internal sealed class PunchHost : IDisposable
    {
        public readonly Socket Sock;
        public readonly int Port;
        public ulong Tag;
        public Action<string> Log;

        private sealed class Client
        {
            public ulong Id; public string Name, Link; public byte[] Key;
            public List<IPEndPoint> Cands = new List<IPEndPoint>();
            public long BurstUntil, NextBurst, LastSeen;
            public IPEndPoint Ep; public bool Up;
            public Action<byte[], int> Down;
        }

        private const int BurstMs = 12000, BurstEveryMs = 200, SilentMs = 30000;
        private readonly TunnelHost _tunnel;
        private readonly Dictionary<ulong, Client> _clients = new Dictionary<ulong, Client>();
        private readonly Dictionary<IPEndPoint, Client> _byEp = new Dictionary<IPEndPoint, Client>();
        private readonly byte[] _out = new byte[64];
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public int Links => _byEp.Count;

        public PunchHost(TunnelHost tunnel, params int[] ports)
        {
            _tunnel = tunnel;
            Sock = PunchWire.Bind(ports, out Port);
        }

        // ---- the punch socket's own public endpoint(s), asked from STUN through this very socket: published as "pe=",
        // so clients behind a port-restricted NAT can open their side towards exactly this endpoint. Asked again every
        // 30 s — that also keeps the mapping of an idle socket alive in the host's NAT.
        private volatile List<(IPEndPoint ep, string name)> _stunServers;
        private readonly List<(byte[] tid, long at)> _stunPending = new List<(byte[], long)>();
        private readonly List<IPEndPoint> _mapped = new List<IPEndPoint>();
        private long _nextStun;

        /// <summary>Starts resolving the STUN servers (call once, any thread).</summary>
        public void StartStun()
        {
            new Thread(() => { _stunServers = StunCodec.Resolve(); }) { IsBackground = true, Name = "DirectHost punch DNS" }.Start();
        }

        /// <summary>Public endpoints of the punch socket seen by STUN (thread-safe copy).</summary>
        public List<IPEndPoint> Mapped() { lock (_mapped) return new List<IPEndPoint>(_mapped); }

        private void StunTick(long now)
        {
            var servers = _stunServers;
            if (servers == null || now < _nextStun) return;
            bool first = _nextStun == 0;
            if (first) _stunStart = now;
            _nextStun = now + (first ? 400 : 30000);      // a second round right away (UDP may lose the first)
            _stunPending.RemoveAll(p => now - p.at > 5000);
            foreach (var (ep, _) in servers)
            {
                var req = StunCodec.Request(out var tid);
                _stunPending.Add((tid, now));
                try { Sock.SendTo(req, ep); } catch { }
            }
            if (!first) lock (_mapped)
            {
                if (_mapped.Count > 0 && now - _mappedAt > 90000) _mapped.Clear();   // outdated
                if (_mapped.Count == 0 && !_noStunLogged && now - _stunStart > 5000)
                {
                    _noStunLogged = true;
                    Log?.Invoke($"punch: no STUN answer on UDP {Port} yet — friends can still reach this host through Steam");
                }
            }
        }
        private long _mappedAt, _stunStart;
        private bool _noStunLogged;

        private void OnStun(byte[] b, int n)
        {
            for (int i = 0; i < _stunPending.Count; i++)
            {
                var m = StunCodec.Parse(b, n, _stunPending[i].tid);
                if (m == null) continue;
                _stunPending.RemoveAt(i);
                _mappedAt = _sw.ElapsedMilliseconds;
                lock (_mapped)
                    if (!_mapped.Contains(m))
                    {
                        _mapped.RemoveAll(x => x.Address.Equals(m.Address));      // one (latest) port per public IP
                        _mapped.Add(m);
                        Log?.Invoke($"punch: public endpoint of the punch socket {m}");
                    }
                return;
            }
        }

        /// <summary>A lobby member published (or changed) its punch data: start knocking at its endpoints.</summary>
        public void SetClient(ulong id, byte[] key, List<IPEndPoint> cands)
        {
            if (!_clients.TryGetValue(id, out var c))
            {
                c = new Client { Id = id, Name = id.ToString(), Link = "punch:" + id };
                var cc = c;
                c.Down = (m, n) =>
                {
                    m[0] = PunchWire.Data;
                    var ep = cc.Ep;
                    if (ep != null) try { Sock.SendTo(m, 0, n, SocketFlags.None, ep); } catch { }
                };
                _clients[id] = c;
            }
            bool same = c.Key != null && PunchWire.KeyIs(Prefix(c.Key), key);
            c.Key = key;
            c.Cands = cands;
            long now = _sw.ElapsedMilliseconds;
            if (same && c.Up && now - c.LastSeen < 5000) return;      // link alive: the client only refreshed its endpoints
            c.Up = false;
            c.BurstUntil = now + BurstMs;
            c.NextBurst = now;
            Log?.Invoke($"punch: knocking at {id} ({(cands.Count > 0 ? string.Join(", ", cands) : "no public endpoint known, waiting for its pings")})");
        }

        private static byte[] Prefix(byte[] key) { var b = new byte[16]; Buffer.BlockCopy(key, 0, b, 8, 8); return b; }

        public void RemoveClient(ulong id)
        {
            if (!_clients.TryGetValue(id, out var c)) return;
            _clients.Remove(id);
            if (c.Ep != null) _byEp.Remove(c.Ep);
            _tunnel.DropLink(c.Link);
        }

        public void OnDatagram(byte[] b, int n, EndPoint from)
        {
            var ep = (IPEndPoint)from;
            if (n >= 4 && b[0] == PunchWire.Data)
            {
                if (!_byEp.TryGetValue(ep, out var c)) return;
                c.LastSeen = _sw.ElapsedMilliseconds;
                _tunnel.OnUp(c.Name, c.Link, b, 1, n - 1, c.Down, 1);
                return;
            }
            if (StunCodec.IsResponse(b, n)) { OnStun(b, n); return; }
            if (n != PunchWire.PingLen || PunchWire.Type(b, n) != '1') return;
            ulong id = PunchWire.GetU64(b, 16);
            if (!_clients.TryGetValue(id, out var cl) || !PunchWire.KeyIs(b, cl.Key)) return;
            if (cl.Ep == null || !cl.Ep.Equals(ep))
            {
                if (cl.Ep != null) _byEp.Remove(cl.Ep);
                Log?.Invoke($"punch: direct link to {id} is {(cl.Ep == null ? "up" : "moved")} via {ep}");
                cl.Ep = new IPEndPoint(ep.Address, ep.Port);
                _byEp[cl.Ep] = cl;
            }
            cl.Up = true;
            cl.LastSeen = _sw.ElapsedMilliseconds;
            int len = PunchWire.Pong(_out, cl.Key, Tag, PunchWire.GetU32(b, 24));
            try { Sock.SendTo(_out, 0, len, SocketFlags.None, ep); } catch { }
        }

        public void Tick()
        {
            long now = _sw.ElapsedMilliseconds;
            StunTick(now);
            foreach (var c in _clients.Values)
            {
                if (c.Up && now - c.LastSeen > SilentMs) { c.Up = false; Log?.Invoke($"punch: link to {c.Id} went silent"); }
                if (c.Up || now >= c.BurstUntil || now < c.NextBurst) continue;
                c.NextBurst = now + BurstEveryMs;
                int len = PunchWire.Hello(_out, c.Key, Tag);
                foreach (var ep in c.Cands) try { Sock.SendTo(_out, 0, len, SocketFlags.None, ep); } catch { }
            }
        }

        public void Dispose() { try { Sock.Close(); } catch { } }
    }

    /// <summary>
    /// Client side of the punch link. Receive/Tick/Send run on the tunnel thread (TunnelClient's extra socket);
    /// MemberValue and Retarget may be called from the main thread.
    /// </summary>
    internal sealed class PunchClient : IDisposable
    {
        public readonly Socket Sock;
        public readonly int LocalPort;
        public readonly byte[] Key = new byte[8];
        public readonly ulong Me, HostTag;
        public Action<string> Log;
        /// <summary>Down frames for the tunnel: (buffer, offset, length).</summary>
        public Action<byte[], int, int> Deliver;

        /// <summary>Smoothed RTT of the link in ms (-1 until the first answer).</summary>
        public volatile int Rtt = -1;
        public long Sent, Received;

        private const int BurstMs = 12000, PingEveryMs = 200, KeepAliveMs = 1000, DeadMs = 4000, RetryMs = 15000;
        private readonly object _lock = new object();
        private List<IPEndPoint> _targets = new List<IPEndPoint>();
        private readonly List<IPEndPoint> _mapped = new List<IPEndPoint>();
        private readonly List<(byte[] tid, string name)> _stun = new List<(byte[], string)>();
        private volatile IPEndPoint _hostEp;
        private long _lastPong = -100000, _burstUntil, _nextPing, _retryAt, _stunAt;
        private bool _lostLogged = true, _everUp;
        private uint _seq;
        private readonly long[] _sentAt = new long[16];
        private readonly byte[] _out = new byte[65536 + 8];
        private readonly byte[] _ctl = new byte[64];
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private volatile bool _disposed;
        private int _attempt;      // published as "n=": a new value makes the host knock again
        private int _retries;      // automatic retries since the last (re)start
        private int _rearm;        // Rearm() request, handled on the tunnel thread

        /// <param name="hostEps">the host punch socket's public endpoints (STUN-mapped)</param>
        /// <param name="hostIps">the host's public addresses: also tried at <paramref name="hostPort"/> (forwarded / port-preserving NAT)</param>
        public PunchClient(ulong me, ulong hostTag, IEnumerable<IPEndPoint> hostEps, IEnumerable<IPAddress> hostIps, int hostPort, params int[] preferredPorts)
        {
            Me = me; HostTag = hostTag;
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(Key);
            Sock = PunchWire.Bind(preferredPorts, out LocalPort);
            Retarget(hostEps, hostIps, hostPort);
            StartStun();
        }

        public bool Alive => _hostEp != null && _sw.ElapsedMilliseconds - Interlocked.Read(ref _lastPong) < DeadMs;
        public IPEndPoint HostEp => _hostEp;

        /// <summary>Where the client's own pings go: opens the client's NAT towards the host before the host's knock arrives.</summary>
        public void Retarget(IEnumerable<IPEndPoint> hostEps, IEnumerable<IPAddress> hostIps, int hostPort)
        {
            var t = new List<IPEndPoint>();
            if (hostEps != null) foreach (var e in hostEps) if (!IsLocal(e.Address) && !t.Contains(e)) t.Add(e);
            if (hostPort > 0 && hostIps != null)
                foreach (var a in hostIps)
                {
                    var e = new IPEndPoint(a, hostPort);
                    if (!IsLocal(a) && !t.Contains(e)) t.Add(e);
                }
            if (t.Count > 12) t.RemoveRange(12, t.Count - 12);
            lock (_lock) _targets = t;
            Interlocked.Exchange(ref _burstUntil, _sw.ElapsedMilliseconds + BurstMs);
        }

        /// <summary>
        /// Any thread: start the punch over unless the link is up — fresh STUN (the public port may have changed), a new
        /// attempt number (so the host knocks again once it is published) and a new retry budget. For a rejoin of the
        /// same host's lobby and for host endpoints that changed mid-game.
        /// </summary>
        public void Rearm() => Interlocked.Exchange(ref _rearm, 1);

        private void Restart(long now)
        {
            _retryAt = now + RetryMs;
            lock (_lock) { _mapped.Clear(); _stun.Clear(); }
            _attempt++;
            StartStun();
            Interlocked.Exchange(ref _burstUntil, now + BurstMs);
        }

        private static bool IsLocal(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 169 && b[1] == 254);
        }

        private void StartStun()
        {
            Interlocked.Exchange(ref _stunAt, _sw.ElapsedMilliseconds);
            new Thread(() =>
            {
                var servers = StunCodec.Resolve();
                for (int round = 0; round < 2 && !_disposed; round++)
                {
                    foreach (var (ep, name) in servers)
                    {
                        var req = StunCodec.Request(out var tid);
                        lock (_lock) _stun.Add((tid, name));
                        try { Sock.SendTo(req, ep); } catch { }
                    }
                    Thread.Sleep(400);
                }
            }) { IsBackground = true, Name = "DirectHost punch STUN" }.Start();
        }

        /// <summary>
        /// Lobby member data "v=1;k=key;n=attempt;ep=ip:port,..." — null while STUN is still running (at most ~1.5 s).
        /// Besides each STUN-mapped endpoint, ip:localPort is offered for every public IP (a forwarded port keeps its number).
        /// </summary>
        public string MemberValue()
        {
            List<IPEndPoint> eps;
            lock (_lock)
            {
                long age = _sw.ElapsedMilliseconds - Interlocked.Read(ref _stunAt);
                if (_mapped.Count == 0 ? age < 1500 : age < 400) return null;
                eps = new List<IPEndPoint>(_mapped);
                foreach (var m in _mapped)
                {
                    var same = new IPEndPoint(m.Address, LocalPort);
                    if (!eps.Contains(same)) eps.Add(same);
                }
            }
            if (eps.Count > 8) eps.RemoveRange(8, eps.Count - 8);
            return $"v=1;k={PunchWire.Hex(Key)};n={_attempt};ep={string.Join(",", eps)}";
        }

        public void OnDatagram(byte[] b, int n, EndPoint from)
        {
            var ep = (IPEndPoint)from;
            if (n >= 3 && b[0] == PunchWire.Data)
            {
                var h = _hostEp;
                if (h == null || !h.Equals(ep)) return;
                Received++;
                Deliver?.Invoke(b, 1, n - 1);
                return;
            }
            if (StunCodec.IsResponse(b, n))
            {
                lock (_lock)
                    for (int i = 0; i < _stun.Count; i++)
                    {
                        var m = StunCodec.Parse(b, n, _stun[i].tid);
                        if (m == null) continue;
                        if (!_mapped.Contains(m)) { _mapped.Add(m); Log?.Invoke($"punch: my public endpoint {m} (seen by {_stun[i].name})"); }
                        _stun.RemoveAt(i);
                        break;
                    }
                return;
            }
            char t = PunchWire.Type(b, n);
            long now = _sw.ElapsedMilliseconds;
            if (t == '0' && n == PunchWire.HelloLen && PunchWire.KeyIs(b, Key) && PunchWire.GetU64(b, 16) == HostTag)
            {
                lock (_lock) if (!_targets.Contains(ep)) { var t2 = new List<IPEndPoint>(_targets) { new IPEndPoint(ep.Address, ep.Port) }; _targets = t2; }
                if (!Alive) SendPing(ep, now);              // answer the knock right away, at the endpoint it came from
                return;
            }
            if (t == '2' && n == PunchWire.PongLen && PunchWire.KeyIs(b, Key) && PunchWire.GetU64(b, 16) == HostTag)
            {
                uint seq = PunchWire.GetU32(b, 24);
                long sent = _sentAt[seq & 15];
                int sample = sent > 0 && seq + 16 > _seq ? (int)(now - sent) : -1;
                var h = _hostEp;
                if (h == null || (!h.Equals(ep) && !Alive))
                {
                    _hostEp = new IPEndPoint(ep.Address, ep.Port);
                    Rtt = sample;
                    Log?.Invoke($"punch: direct link to the host is up via {ep}{(sample >= 0 ? $" (ping {sample} ms)" : "")}");
                }
                else if (!h.Equals(ep)) return;
                else if (sample >= 0) Rtt = Rtt < 0 ? sample : (Rtt * 7 + sample) / 8;
                Interlocked.Exchange(ref _lastPong, now);
                _lostLogged = false;
                _everUp = true;
            }
        }

        private void SendPing(IPEndPoint ep, long now)
        {
            uint seq = ++_seq;
            _sentAt[seq & 15] = now;
            int len = PunchWire.Ping(_ctl, Key, Me, seq);
            try { Sock.SendTo(_ctl, 0, len, SocketFlags.None, ep); } catch { }
        }

        public void Tick()
        {
            long now = _sw.ElapsedMilliseconds;
            if (Interlocked.Exchange(ref _rearm, 0) == 1 && !Alive)
            {
                _retries = 0;
                Restart(now);
                Log?.Invoke("punch: starting over (new lobby or new host addresses)");
            }
            if (now < _nextPing) return;
            if (Alive)
            {
                _nextPing = now + KeepAliveMs;
                SendPing(_hostEp, now);
                return;
            }
            _nextPing = now + PingEveryMs;
            if (!_lostLogged && _hostEp != null) { _lostLogged = true; Log?.Invoke("punch: direct link to the host lost — traffic goes through Steam meanwhile"); _retryAt = now + 3000; }
            if (now < Interlocked.Read(ref _burstUntil))
            {
                List<IPEndPoint> t; lock (_lock) t = _targets;
                foreach (var ep in t) SendPing(ep, now);
                var h = _hostEp;
                if (h != null && !t.Contains(h)) SendPing(h, now);
            }
            else if (now >= _retryAt && _retries < (_everUp ? 20 : 2))
            {
                // nothing got through (or the link died): ask STUN again — the public port may have changed — and
                // republish, so the host knocks again
                _retries++;
                Restart(now);
            }
        }

        /// <summary>Tunnel thread: one tunnel frame (flow + role + datagram) to the host.</summary>
        public void Send(byte[] frame, int n)
        {
            var h = _hostEp;
            if (h == null || n + 1 > _out.Length) return;
            _out[0] = PunchWire.Data;
            Buffer.BlockCopy(frame, 0, _out, 1, n);
            Sent++;
            try { Sock.SendTo(_out, 0, n + 1, SocketFlags.None, h); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            try { Sock.Close(); } catch { }
        }
    }
}
