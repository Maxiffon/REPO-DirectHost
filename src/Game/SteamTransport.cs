// REPO DirectHost — fallback path through Steam Networking (used only when no direct address of the host answers).
//
// What makes Steam paths slow in other mods, and what this does instead:
//  * ICE (Steam's NAT punch-through = a DIRECT UDP path, Steam only introduces the peers) is enabled explicitly
//    (P2P_Transport_ICE_Enable = Private|Public). Left at default, traffic may be forced through Valve relays
//    (SDR) — for Novosibirsk <-> Moscow that is a detour via European relays: 120-160 ms instead of ~50.
//    SDR remains the automatic fallback when NAT punching fails; Steam keeps measuring and switches to the
//    faster transport by itself.
//  * Messages are pumped on our own threads every ~1 ms (host: a Steam pump thread, client: the tunnel thread),
//    not in Steam's main-thread callback loop (16 ms ticks), and sent Unreliable|NoNagle (no 5 ms Nagle delay,
//    no second reliability layer under ENet).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using RepoDirectHost.Net;
using RepoDirectHost.Server;
using Steamworks;
using Steamworks.Data;

namespace RepoDirectHost.Game
{
    internal static class SteamTransport
    {
        /// <summary>Steam P2P virtual port of the DirectHost tunnel (same on host and clients).</summary>
        public const int VirtualPort = 7777;
        internal const SendType Fast = SendType.Unreliable | SendType.NoNagle;

        private static bool _configured;

        /// <summary>Main thread, once Steam is up: enable the direct (ICE) path, relay access, faster send rate, route logging.</summary>
        public static void Configure()
        {
            if (_configured) return;
            try { if (!SteamClient.IsValid) return; } catch { return; }
            _configured = true;
            try
            {
                const int ICE_Enable = 104, ICE_Private = 2, ICE_Public = 4;
                bool ok = SetConfigInt(ICE_Enable, ICE_Private | ICE_Public);
                Plugin.Log.LogInfo($"Steam networking: direct P2P (ICE) {(ok ? "enabled" : "could NOT be enabled")} (value {GetConfigInt(ICE_Enable)})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Steam networking: could not enable ICE: " + e.Message); }
            try
            {
                SteamNetworkingUtils.SendRateMin = 1 << 20;   // 1 MB/s from the start: level-load bursts are never paced/queued
                SteamNetworkingUtils.SendRateMax = 4 << 20;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Steam networking: send rate: " + e.Message); }
            try
            {
                SteamNetworkingUtils.OnDebugOutput += OnSteamDebug;
                SteamNetworkingUtils.DebugLevelP2PRendezvous = (int)NetDebugOutput.Msg;
                SteamNetworkingUtils.DebugLevel = NetDebugOutput.Msg;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Steam networking: debug output: " + e.Message); }
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch { }
        }

        // Facepunch keeps the config setters internal; the numbers are the documented Steamworks ESteamNetworkingConfigValue ids.
        private static MethodInfo _set, _get;
        private static Type _netConfig;
        private static bool SetConfigInt(int id, int value)
        {
            Resolve();
            return (bool)_set.Invoke(null, new object[] { Enum.ToObject(_netConfig, id), value });
        }
        private static int GetConfigInt(int id)
        {
            Resolve();
            return (int)_get.Invoke(null, new object[] { Enum.ToObject(_netConfig, id) });
        }
        private static void Resolve()
        {
            if (_set != null) return;
            var t = typeof(SteamNetworkingUtils);
            _netConfig = t.Assembly.GetType("Steamworks.NetConfig", true);
            _set = t.GetMethod("SetConfigInt", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("SetConfigInt");
            _get = t.GetMethod("GetConfigInt", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException("GetConfigInt");
        }

        private static void OnSteamDebug(NetDebugOutput level, string msg)
        {
            if (msg == null) return;
            if (msg.IndexOf("ICE not enabled by local user options", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Plugin.Log.LogWarning("Steam: the direct path is blocked by YOUR Steam settings -> Steam > Settings > In Game > Steam Networking: set it to \"Always\" (or \"Friends only\"). Until then Steam uses its relays (higher ping).");
                return;
            }
            if (level <= NetDebugOutput.Warning
                || msg.IndexOf("transport", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("ICE ", StringComparison.Ordinal) >= 0
                || msg.IndexOf("route", StringComparison.OrdinalIgnoreCase) >= 0)
                Plugin.Log.LogInfo("[Steam] " + msg.Trim());
        }

        /// <summary>"ping 48 ms, direct P2P" / "ping 150 ms, via Steam relay".</summary>
        public static string Describe(Connection c)
        {
            try
            {
                int ping = c.QuickStatus().Ping;
                bool relay = IsRelayed(c);
                return $"ping {ping} ms, {(relay ? "via Steam relay" : "direct P2P")}";
            }
            catch { return "status unavailable"; }
        }

        // SteamNetConnectionInfo_t (696 B): m_idPOPRelay @172, m_nFlags @440. Facepunch's struct drops m_nFlags, so read it raw.
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamNetworkingSockets_GetConnectionInfo(IntPtr self, uint conn, IntPtr info);
        private const int FlagRelayed = 16;

        /// <summary>True when the connection goes through a Valve relay (SDR) or TURN, false for a direct ICE path.</summary>
        public static bool IsRelayed(Connection c)
        {
            IntPtr buf = Marshal.AllocHGlobal(1024);
            try
            {
                for (int i = 0; i < 1024; i += 4) Marshal.WriteInt32(buf, i, 0);
                if (SteamAPI_ISteamNetworkingSockets_GetConnectionInfo(SteamAPI_SteamNetworkingSockets_SteamAPI_v012(), c.Id, buf))
                    return (Marshal.ReadInt32(buf, 440) & FlagRelayed) != 0 || Marshal.ReadInt32(buf, 172) != 0;
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            string d = c.DetailedStatus() ?? "";
            return d.IndexOf("relay", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================= host
        internal sealed class HostSocket : SocketManager
        {
            public UdpServer Udp;
            public TunnelHost Tunnel;
            /// <summary>Main thread: may this SteamID use the tunnel (is it in our lobby)?</summary>
            public Func<SteamId, bool> Allow;
            private readonly List<(Connection c, SteamId who, DateTime until)> _pending = new List<(Connection, SteamId, DateTime)>();
            // Steam calls take Steam's global networking lock, which Steam's own service thread can hold for
            // ~0.8 s while it sets up a new P2P route. So Steam is only ever touched from this pump thread;
            // the UdpServer thread (serving everyone) just exchanges byte arrays with it through queues.
            private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string client, string link, Action<byte[], int> down)> _links = new System.Collections.Concurrent.ConcurrentDictionary<uint, (string, string, Action<byte[], int>)>();
            private readonly System.Collections.Concurrent.ConcurrentQueue<(Connection c, byte[] data, int len)> _out = new System.Collections.Concurrent.ConcurrentQueue<(Connection, byte[], int)>();
            private System.Threading.Thread _pump;
            private volatile bool _run;

            public void StartPump()
            {
                _run = true;
                _pump = new System.Threading.Thread(Pump) { IsBackground = true, Name = "DirectHost steam", Priority = System.Threading.ThreadPriority.AboveNormal };
                _pump.Start();
            }

            private void Pump()
            {
                while (_run)
                {
                    try
                    {
                        Receive(64);
                        while (_out.TryDequeue(out var m)) m.c.SendMessage(m.data, 0, m.len, Fast);
                    }
                    catch (Exception e) { if (!_run) return; Plugin.Log.LogError("steam pump: " + e.Message); }
                    System.Threading.Thread.Sleep(1);
                }
            }

            public void Stop()
            {
                _run = false;
                try { if (_pump != null && _pump.IsAlive) _pump.Join(500); } catch { }
                try { Close(); } catch { }
            }

            public override void OnConnecting(Connection c, ConnectionInfo info)
            {
                SteamId who = info.Identity.SteamId;
                if (Allow == null || Allow(who)) { c.Accept(); return; }
                _pending.Add((c, who, DateTime.UtcNow.AddSeconds(8)));      // lobby member list may lag a moment behind
            }

            /// <summary>Main thread, ~1/s: accept late lobby members, refuse strangers.</summary>
            public void Poll()
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (Allow == null || Allow(p.who)) { p.c.Accept(); _pending.RemoveAt(i); }
                    else if (DateTime.UtcNow > p.until)
                    {
                        Plugin.Log.LogWarning($"Steam: refused tunnel from {p.who} (not in the lobby)");
                        p.c.Close(false, 0, "not in lobby");
                        _pending.RemoveAt(i);
                    }
                }
            }

            public override void OnConnected(Connection c, ConnectionInfo info)
            {
                base.OnConnected(c, info);
                Plugin.Log.LogInfo($"Steam: player {info.Identity.SteamId} connected through the Steam tunnel");
            }

            public override void OnDisconnected(Connection c, ConnectionInfo info)
            {
                Plugin.Log.LogInfo($"Steam: tunnel of {info.Identity.SteamId} closed ({info.EndReason})");
                if (_links.TryRemove(c.Id, out var l)) Udp?.Post(() => Tunnel.DropLink(l.link));
                c.Close();
            }

            /// <summary>Pump thread (Receive is called from Pump): hand the datagram to the server thread.</summary>
            public override void OnMessage(Connection c, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                if (size <= 0 || size > 65536) return;
                var msg = new byte[size];
                Marshal.Copy(data, msg, 0, size);
                if (!_links.TryGetValue(c.Id, out var l))
                {
                    var conn = c;
                    l = (identity.SteamId.Value.ToString(), "steam:" + c.Id, (b, n) => _out.Enqueue((conn, b, n)));    // TunnelHost hands over a fresh array
                    _links[c.Id] = l;
                }
                Udp.Post(() => Tunnel.OnUp(l.client, l.link, msg, 0, msg.Length, l.down));
            }

            public IEnumerable<Connection> Live => Connected;
        }

        public static HostSocket Listen(UdpServer udp, TunnelHost tunnel, Func<SteamId, bool> allow)
        {
            Configure();
            var h = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(VirtualPort);
            h.Udp = udp;
            h.Tunnel = tunnel;
            h.Allow = allow;
            udp.SelectMicros = 1000;      // tunnelled datagrams arrive via Post: pick them up within ~1 ms
            HighResTimer.Enable();
            h.StartPump();
            return h;
        }

        // ================================================================= client
        internal sealed class ClientLink : ConnectionManager
        {
            public TunnelClient Tunnel;
            public volatile bool Dead;
            /// <summary>Closed by us (route chosen elsewhere / left): not worth a warning.</summary>
            public volatile bool Closing;
            public SteamId Host;
            private readonly byte[] _buf = new byte[65536];

            public override void OnConnected(ConnectionInfo info)
            {
                base.OnConnected(info);
                Plugin.Log.LogInfo($"Steam: tunnel to host {Host} is up");
            }

            public override void OnDisconnected(ConnectionInfo info)
            {
                base.OnDisconnected(info);
                if (Closing) Plugin.Log.LogInfo("Steam: spare tunnel to the host closed (not needed)");
                else Plugin.Log.LogWarning($"Steam: tunnel to host {Host} closed ({info.EndReason})");
                Dead = true;
                Connection.Close();
            }

            /// <summary>Tunnel thread (Receive is called from TunnelClient's loop).</summary>
            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                if (size <= 0 || size > _buf.Length) return;
                Marshal.Copy(data, _buf, 0, size);
                Tunnel.Deliver(_buf, size);
            }
        }
    }
}
