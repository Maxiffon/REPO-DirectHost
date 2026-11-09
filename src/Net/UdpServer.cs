// REPO DirectHost — UDP sockets + the single server thread that owns all server state.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RepoDirectHost.Net.Enet;

namespace RepoDirectHost.Net
{
    internal sealed class UdpServer : IDisposable
    {
        private sealed class Listener
        {
            public Socket Sock; public EnetHost Host; public int Port;
            public readonly Dictionary<EndPoint, (string key, Action<byte[]> send)> Peers = new Dictionary<EndPoint, (string, Action<byte[]>)>();
        }

        public Action<string> Log;
        public Action<Exception> OnError;
        /// <summary>Called on the server thread every loop iteration (~every 2 ms).</summary>
        public Action Tick;
        /// <summary>Extra transports (Steam tunnel): polled on the server thread every loop iteration, before Tick.</summary>
        public volatile Action PollExternal;
        /// <summary>Longest wait for a UDP datagram per loop iteration (lower it when an external transport is polled).</summary>
        public volatile int SelectMicros = 2000;
        /// <summary>Sent back in reachability probe answers (the host's SteamID), so clients can tell hosts apart.</summary>
        public ulong ProbeTag;
        public int ProbesAnswered;

        private readonly List<Listener> _listeners = new List<Listener>();
        private readonly Thread _thread;
        private volatile bool _run = true;
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Server time in ms (positive, monotonic, same for all connections).</summary>
        public static int Now() => (int)(Clock.ElapsedMilliseconds + 1000);

        public EnetHost Host => _listeners[0].Host;
        public IEnumerable<EnetHost> Hosts { get { foreach (var l in _listeners) yield return l.Host; } }

        public EnetHost HostFor(byte role)
        {
            foreach (var l in _listeners) if (l.Host.Role == role) return l.Host;
            return null;
        }

        /// <summary>Single socket (role 0).</summary>
        public UdpServer(int port, IEnetHandler handler, IPAddress bind = null) : this(handler, bind, (port, (byte)0)) { }

        /// <summary>Problems found while binding (shared ports), for the caller to log.</summary>
        public readonly List<string> BindWarnings = new List<string>();

        public UdpServer(IEnetHandler handler, IPAddress bind, params (int port, byte role)[] ports)
        {
            foreach (var (port, role) in ports)
            {
                var ep = new IPEndPoint(bind ?? IPAddress.Any, port);
                var s = PunchWire.NewSocket(true);
                try { s.Bind(ep); }
                catch (SocketException)
                {
                    // Another program holds this port on one specific address (e.g. a VPN adapter): an exclusive bind is
                    // refused. A shared bind still works, but datagrams to that address go to the other program.
                    s.Close();
                    s = PunchWire.NewSocket(false);
                    s.Bind(ep);
                    BindWarnings.Add($"UDP {port} is also used by another program (on one network address) — packets to that address go to it, not to the game. Close that program (an old relay, another P2P tool).");
                }
                _listeners.Add(new Listener { Sock = s, Port = port, Host = new EnetHost(handler, Now) { Role = role } });
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "DirectHost server", Priority = ThreadPriority.AboveNormal };
        }

        public void Start() { foreach (var l in _listeners) l.Host.Log = Log; _thread.Start(); }

        private readonly List<(Socket sock, Action<byte[], int, EndPoint> onDatagram, Action tick)> _extra = new List<(Socket, Action<byte[], int, EndPoint>, Action)>();

        /// <summary>
        /// An extra socket served by the server thread (the NAT punch link): its datagrams go to <paramref name="onDatagram"/>,
        /// <paramref name="tick"/> runs every loop iteration. Call before <see cref="Start"/>; the socket is closed on Dispose.
        /// </summary>
        public void AddSocket(Socket s, Action<byte[], int, EndPoint> onDatagram, Action tick)
        {
            if (_thread.IsAlive) throw new InvalidOperationException("AddSocket must be called before Start");
            _extra.Add((s, onDatagram, tick));
        }

        /// <summary>Run an action on the server thread (thread-safe).</summary>
        public void Post(Action a) => _actions.Enqueue(a);

        private void Loop()
        {
            var buf = new byte[65536];
            var read = new List<Socket>();
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (_run)
            {
                try
                {
                    read.Clear();
                    foreach (var l in _listeners) read.Add(l.Sock);
                    foreach (var x in _extra) read.Add(x.sock);
                    Socket.Select(read, null, null, SelectMicros);
                    foreach (var x in _extra)
                    {
                        if (!read.Contains(x.sock)) continue;
                        int guard = 0;
                        while (_run && x.sock.Available > 0 && guard++ < 256)
                        {
                            EndPoint ep = any;
                            int n;
                            try { n = x.sock.ReceiveFrom(buf, ref ep); }
                            catch (SocketException) { break; }
                            x.onDatagram(buf, n, ep);
                        }
                    }
                    foreach (var l in _listeners)
                    {
                        if (!read.Contains(l.Sock)) continue;
                        int guard = 0;
                        while (_run && l.Sock.Available > 0 && guard++ < 256)
                        {
                            EndPoint ep = any;
                            int n;
                            try { n = l.Sock.ReceiveFrom(buf, ref ep); }
                            catch (SocketException) { break; }
                            if (Probe.IsPing(buf, n))
                            {
                                try { l.Sock.SendTo(Probe.MakePong(buf, ProbeTag), ep); ProbesAnswered++; } catch { }
                                continue;
                            }
                            if (!l.Peers.TryGetValue(ep, out var peer))
                            {
                                var target = ep; var sock = l.Sock;
                                peer = ($"udp:{l.Port}:{ep}", data => { try { sock.SendTo(data, target); } catch { } });
                                if (l.Peers.Count > 4096) l.Peers.Clear();
                                l.Peers[ep] = peer;
                            }
                            l.Host.OnDatagram(peer.key, buf, n, peer.send);
                        }
                    }
                    while (_actions.TryDequeue(out var a)) a();
                    PollExternal?.Invoke();
                    foreach (var x in _extra) x.tick?.Invoke();
                    Tick?.Invoke();
                    foreach (var l in _listeners) l.Host.Tick();
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e) { if (!_run) return; OnError?.Invoke(e); }
            }
        }

        public void Dispose()
        {
            _run = false;
            foreach (var l in _listeners) { try { l.Sock.Close(); } catch { } }
            foreach (var x in _extra) { try { x.sock.Close(); } catch { } }
            try { if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(500); } catch { }
        }
    }
}
