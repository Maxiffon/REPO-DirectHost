// REPO DirectHost — datagram tunnel over a message link (Steam P2P), transport-agnostic.
//
// Client side: Photon (PUN + Voice peers) connects to two loopback UDP ports of TunnelClient, exactly as it would to
// the host. Every Photon socket (source endpoint) becomes a "flow"; datagrams travel through the link unchanged.
// Host side: TunnelHost feeds them into the same EnetHost instances that serve UDP, keyed per (link, flow), so the
// server cannot tell tunnelled players from direct ones. ENet keeps doing reliability/ordering end to end; the link
// must be UNRELIABLE (no second retransmit layer, no head-of-line blocking).
//
// Wire format of a link message:  up   = [flow lo][flow hi][role] + datagram
//                                 down = [flow lo][flow hi]        + datagram
//
// One client may use several links at once (Steam + the own NAT punch link). The host keys ENet sessions by the
// CLIENT (SteamID) and flow, not by the link, and always answers on the link the latest datagram came from — so the
// client can move its traffic from one link to another mid-game (relay -> direct, direct lost -> Steam) without
// the Photon connection noticing anything but a ping change.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using RepoDirectHost.Net.Enet;

namespace RepoDirectHost.Net
{
    internal sealed class TunnelClient : IDisposable
    {
        private readonly Socket[] _socks = new Socket[2];          // [0] = master (role 0), [1] = game (role 1)
        private readonly Dictionary<EndPoint, ushort>[] _bySrc = { new Dictionary<EndPoint, ushort>(), new Dictionary<EndPoint, ushort>() };
        private readonly Dictionary<ushort, (Socket sock, EndPoint ep)> _flows = new Dictionary<ushort, (Socket, EndPoint)>();
        private ushort _nextFlow = 1;
        private readonly Thread _thread;
        private volatile bool _run = true;

        public int MasterPort { get; }
        public int GamePort { get; }
        /// <summary>Sends one link message (called on the tunnel thread).</summary>
        public volatile Action<byte[], int> SendUp;
        /// <summary>Polls the link (called on the tunnel thread every loop); the link hands messages to <see cref="Deliver"/>.</summary>
        public volatile Func<int> PollLink;
        public Action<Exception> OnError;
        public long Up, Down;

        private readonly Socket _extra;
        private readonly Action<byte[], int, EndPoint> _onExtra;

        /// <param name="extra">optional link socket (punch link) served by the same thread; datagrams go to <paramref name="onExtra"/>.</param>
        public TunnelClient(byte masterRole, byte gameRole, Socket extra = null, Action<byte[], int, EndPoint> onExtra = null)
        {
            _roles = new[] { masterRole, gameRole };
            _extra = extra; _onExtra = onExtra;
            _nextFlow = (ushort)new Random().Next(1, 60000);     // flows of an older tunnel to the same host never collide
            for (int i = 0; i < 2; i++)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try { s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
                s.ReceiveBufferSize = 1 << 20;
                s.SendBufferSize = 1 << 20;
                s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _socks[i] = s;
            }
            MasterPort = ((IPEndPoint)_socks[0].LocalEndPoint).Port;
            GamePort = ((IPEndPoint)_socks[1].LocalEndPoint).Port;
            HighResTimer.Enable();
            _thread = new Thread(Loop) { IsBackground = true, Name = "DirectHost tunnel", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private readonly byte[] _roles;

        private void Loop()
        {
            var buf = new byte[65536 + 3];
            var xbuf = new byte[65536 + 8];
            var read = new List<Socket>(3);
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    read.Clear(); read.Add(_socks[0]); read.Add(_socks[1]);
                    if (_extra != null) read.Add(_extra);
                    Socket.Select(read, null, null, 1000);
                    if (_extra != null && read.Contains(_extra))
                    {
                        int guard = 0;
                        while (_run && _extra.Available > 0 && guard++ < 256)
                        {
                            EndPoint ep = any;
                            int n;
                            try { n = _extra.ReceiveFrom(xbuf, ref ep); }
                            catch (SocketException) { break; }
                            _onExtra?.Invoke(xbuf, n, ep);
                        }
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        var sock = _socks[i];
                        if (!read.Contains(sock)) continue;
                        int guard = 0;
                        while (_run && sock.Available > 0 && guard++ < 256)
                        {
                            EndPoint ep = any;
                            int n;
                            try { n = sock.ReceiveFrom(buf, 3, buf.Length - 3, SocketFlags.None, ref ep); }
                            catch (SocketException) { break; }
                            if (!_bySrc[i].TryGetValue(ep, out ushort flow))
                            {
                                flow = _nextFlow++;
                                if (_nextFlow == 0) _nextFlow = 1;
                                if (_flows.TryGetValue(flow, out var old)) _bySrc[old.sock == _socks[0] ? 0 : 1].Remove(old.ep);
                                _bySrc[i][ep] = flow;
                                _flows[flow] = (sock, ep);
                            }
                            buf[0] = (byte)flow; buf[1] = (byte)(flow >> 8); buf[2] = _roles[i];
                            Up++;
                            SendUp?.Invoke(buf, n + 3);
                        }
                    }
                    PollLink?.Invoke();
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { if (!_run) return; OnError?.Invoke(e); Thread.Sleep(1); }
            }
        }

        /// <summary>Downstream link message -> the Photon socket of its flow. Call on the tunnel thread (from PollLink).</summary>
        public void Deliver(byte[] msg, int len) => Deliver(msg, 0, len);

        public void Deliver(byte[] msg, int off, int len)
        {
            if (len < 3) return;
            ushort flow = (ushort)(msg[off] | msg[off + 1] << 8);
            if (!_flows.TryGetValue(flow, out var f)) return;
            Down++;
            try { f.sock.SendTo(msg, off + 2, len - 2, SocketFlags.None, f.ep); } catch { }
        }

        public void Dispose()
        {
            _run = false;
            foreach (var s in _socks) { try { s.Close(); } catch { } }
            try { if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(500); } catch { }
        }
    }

    /// <summary>Host side of the tunnel. All methods run on the UdpServer thread.</summary>
    internal sealed class TunnelHost
    {
        private readonly UdpServer _udp;
        private readonly Dictionary<(string link, ushort flow), (string key, Action<byte[]> send)> _peers = new Dictionary<(string, ushort), (string, Action<byte[]>)>();
        private readonly byte[] _scratch = new byte[65536];
        public long Up, Down;
        /// <summary>Client -> link its latest datagram came through ("steam:..." / "punch:...").</summary>
        public readonly Dictionary<string, string> LastLink = new Dictionary<string, string>();

        public TunnelHost(UdpServer udp) { _udp = udp; }

        public void OnUp(string link, byte[] msg, int len, Action<byte[], int> sendDown) => OnUp(link, link, msg, 0, len, sendDown);

        /// <param name="client">who sent it (SteamID): ENet sessions are keyed by client + flow, so a client may switch links</param>
        /// <param name="link">the link it came through; answers to this datagram go back through <paramref name="sendDown"/></param>
        /// <param name="downHeader">free bytes the link wants in front of each down message (filled in by the link)</param>
        public void OnUp(string client, string link, byte[] msg, int off, int len, Action<byte[], int> sendDown, int downHeader = 0)
        {
            if (len < 4) return;
            ushort flow = (ushort)(msg[off] | msg[off + 1] << 8);
            var host = _udp.HostFor(msg[off + 2]);
            if (host == null) return;
            if (!_peers.TryGetValue((link, flow), out var peer))
            {
                byte lo = (byte)flow, hi = (byte)(flow >> 8);
                int h = downHeader;
                peer = ($"{client}/{flow}", data =>
                {
                    var m = new byte[h + data.Length + 2];
                    m[h] = lo; m[h + 1] = hi;
                    Buffer.BlockCopy(data, 0, m, h + 2, data.Length);
                    Down++;
                    sendDown(m, m.Length);
                });
                _peers[(link, flow)] = peer;
            }
            if (!LastLink.TryGetValue(client, out var last) || last != link) LastLink[client] = link;
            Up++;
            int n = len - 3;
            Buffer.BlockCopy(msg, off + 3, _scratch, 0, n);
            host.OnDatagram(peer.key, _scratch, n, peer.send);
        }

        /// <summary>The link is gone: forget its flows (their ENet sessions time out like silent UDP peers).</summary>
        public void DropLink(string link)
        {
            var dead = new List<(string, ushort)>();
            foreach (var k in _peers.Keys) if (k.link == link) dead.Add(k);
            foreach (var k in dead) _peers.Remove(k);
        }
    }

    /// <summary>1 ms Windows timer resolution while tunnels/servers run, so 1 ms waits really are ~1 ms (not 15.6).</summary>
    internal static class HighResTimer
    {
        private static int _on;
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

        public static void Enable()
        {
            if (Interlocked.Exchange(ref _on, 1) == 1) return;
            try { timeBeginPeriod(1); } catch { }
        }
    }
}
