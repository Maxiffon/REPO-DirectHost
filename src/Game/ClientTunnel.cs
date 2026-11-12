// REPO DirectHost — the client's tunnel to the host. Photon (PUN + Voice) talks to two loopback ports (TunnelClient);
// the datagrams travel over the best of up to two links, re-chosen every second:
//   * the own NAT punch link (direct UDP, Net/Punch.cs) — preferred whenever it is up;
//   * the Steam link (Steam P2P via ICE, or Valve's relay) — the fallback, or used when it is clearly faster than the
//     punch link (which can happen when the punch traffic takes a VPN detour).
// Switching links mid-game does not disturb the Photon connection (the host keys sessions by player, see Tunnel.cs).
using System;
using System.Net;
using RepoDirectHost.Net;
using RepoDirectHost.Server;
using Steamworks;
using Steamworks.Data;

namespace RepoDirectHost.Game
{
    internal sealed class ClientTunnel : IDisposable
    {
        /// <summary>Lobby MEMBER data key with the client's punch endpoints (read by the host).</summary>
        public const string MemberKey = "directhost_punch";

        public readonly SteamId Host;
        public readonly TunnelClient Tunnel;
        public readonly bool HasSteam;
        public bool HasPunch => _punch != null;

        private volatile SteamTransport.ClientLink _link;
        private readonly PunchClient _punch;
        private volatile bool _preferSteam;
        private DateTime _nextRetry, _nextReport;
        private Lobby _lobby;
        private string _published, _lastKind;
        // Not Environment.TickCount: it turns negative after 24.9 days of uptime (Fast Startup keeps counting across
        // "shutdowns"), and "TickCount - 0 < 100" then held forever, so the punch data was never published.
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _checkAt = -1000;

        public ClientTunnel(SteamId host, bool steam, HostCandidates punchTo, Lobby lobby)
        {
            SteamTransport.Configure();
            HighResTimer.Enable();
            Host = host;
            HasSteam = steam;
            _lobby = lobby;
            if (punchTo != null && punchTo.PunchPort > 0)
            {
                try
                {
                    _punch = new PunchClient(SteamClient.SteamId.Value, host.Value, punchTo.PunchEps, punchTo.Ips, punchTo.PunchPort, 7779, 7778, 7777) { Log = s => Plugin.Log.LogInfo(s) };
                    _hostCand = punchTo.ToString();
                }
                catch (Exception e) { Plugin.Log.LogWarning("punch link unavailable: " + e.Message); }
            }
            var p = _punch;
            Tunnel = new TunnelClient(Role.Master, Role.Game, p?.Sock, p == null ? null : (Action<byte[], int, EndPoint>)p.OnDatagram)
            {
                OnError = e => Plugin.Log.LogError("tunnel: " + e)
            };
            if (p != null) p.Deliver = (b, off, n) => Tunnel.Deliver(b, off, n);
            Tunnel.SendUp = Send;
            Tunnel.PollLink = () =>
            {
                p?.Tick();
                var l = _link;
                return l != null && !l.Dead ? l.Receive(64) : 0;
            };
            if (steam) Open();
        }

        /// <summary>Tunnel thread: one frame to the host over the chosen link.</summary>
        private void Send(byte[] b, int n)
        {
            var p = _punch;
            if (p != null && !_preferSteam && p.Alive) { p.Send(b, n); return; }
            var l = _link;
            if (l != null && !l.Dead) l.Connection.SendMessage(b, 0, n, SteamTransport.Fast);
            else if (p != null && p.Alive) p.Send(b, n);
        }

        private void Open()
        {
            var l = SteamNetworkingSockets.ConnectRelay<SteamTransport.ClientLink>(Host, SteamTransport.VirtualPort);
            l.Host = Host;
            l.Tunnel = Tunnel;
            _link = l;
            _nextReport = DateTime.UtcNow.AddSeconds(5);
        }

        /// <summary>
        /// Same host, entered again (another lobby, or the same one after leaving it): publish the punch data again —
        /// Steam drops a member's data when it leaves, so the host would not know our key — and knock again.
        /// </summary>
        public void Attach(Lobby lobby, HostCandidates c)
        {
            if (lobby.Id.Value != _lobby.Id.Value) { ClearMemberData(); _lobby = lobby; }
            _published = null;
            if (_punch == null || c == null) return;
            _hostCand = c.ToString();
            _punch.Retarget(c.PunchEps, c.Ips, c.PunchPort);
            _punch.Rearm();
        }

        /// <summary>
        /// Main thread, ~1/s: the host republished its addresses mid-game (e.g. its punch endpoint was learned late):
        /// aim the punch there and, if the link is not up, start over.
        /// </summary>
        public void WatchHost()
        {
            var p = _punch;
            if (p == null || !_lobby.Id.IsValid) return;
            try { if (_lobby.Owner.Id.Value != Host.Value) return; } catch { return; }
            string v = _lobby.GetData(GameHooks.LobbyCandidatesKey);
            if (string.IsNullOrEmpty(v) || v == _hostCand) return;
            var c = HostCandidates.Parse(v);
            if (c == null) return;
            _hostCand = v;
            p.Retarget(c.PunchEps, c.Ips, c.PunchPort);
            if (!p.Alive)
            {
                Plugin.Log.LogInfo("punch: the host published new addresses — knocking again");
                p.Rearm();
            }
        }
        private string _hostCand;

        /// <summary>Main thread, every frame: publish the punch endpoints as soon as STUN found them.</summary>
        public void Frame()
        {
            var p = _punch;
            if (p == null || _clock.ElapsedMilliseconds - _checkAt < 100) return;
            _checkAt = _clock.ElapsedMilliseconds;
            string v = p.MemberValue();
            if (v == null || v == _published) return;
            try
            {
                if (!_lobby.Id.IsValid) return;
                _lobby.SetMemberData(MemberKey, v);
                _published = v;
            }
            catch (Exception e) { Plugin.Log.LogWarning("punch: could not publish lobby member data: " + e.Message); }
        }

        private void ClearMemberData()
        {
            if (_published == null) return;
            try { if (_lobby.Id.IsValid) _lobby.SetMemberData(MemberKey, ""); } catch { }
            _published = null;
        }

        /// <summary>Main thread: is the Steam link up, and is it direct (ICE) or relayed?</summary>
        public bool GetSteamRoute(out int ping, out bool relayed)
        {
            ping = -1; relayed = false;
            var l = _link;
            if (l == null || l.Dead || !l.Connected) return false;
            try { ping = l.Connection.QuickStatus().Ping; } catch { }
            relayed = SteamTransport.IsRelayed(l.Connection);
            return true;
        }

        /// <summary>Is the own direct (punch) link up, and its RTT.</summary>
        public bool GetPunch(out int rtt)
        {
            rtt = -1;
            var p = _punch;
            if (p == null || !p.Alive) return false;
            rtt = p.Rtt;
            return true;
        }

        /// <summary>Main thread, ~1/s: choose the link, reconnect a dropped Steam link, log the route now and then.</summary>
        public void Poll()
        {
            var l = _link;
            if (HasSteam && l != null && l.Dead && DateTime.UtcNow >= _nextRetry)
            {
                _nextRetry = DateTime.UtcNow.AddSeconds(3);
                Plugin.Log.LogInfo("Steam: reconnecting the tunnel");
                Open();
            }

            bool punchUp = GetPunch(out int prtt);
            bool steamUp = GetSteamRoute(out int sping, out bool relayed);
            // The punch link is direct, but not always the fastest: through a VPN or a long detour it can be slower even
            // than Valve's relay (seen: punch 170 ms, relay 115 ms). Steam takes over when it is clearly faster (a wider
            // margin for the relay, whose ping wanders more), and gives the link back only once the punch has caught up.
            bool both = punchUp && steamUp && sping > 0 && prtt > 0;
            _preferSteam = both && (_preferSteam ? prtt > sping + 5 : prtt > sping + (relayed ? 30 : 15));
            string kind, path;
            if (punchUp && !_preferSteam) { kind = "punch"; path = $"DIRECT (own NAT punch-through), ping {prtt} ms"; }
            else if (steamUp) { kind = relayed ? "relay" : "p2p"; path = $"Steam, ping {sping} ms, {(relayed ? "via Steam relay" : "direct P2P")}"; }
            else { kind = "none"; path = "no link up yet"; }
            if (kind != _lastKind)
            {
                if (_lastKind != null || kind == "punch") Plugin.Log.LogInfo($"route: now {path}");
                _lastKind = kind;
            }
            if (DateTime.UtcNow >= _nextReport && kind != "none")
            {
                _nextReport = DateTime.UtcNow.AddSeconds(30);
                Plugin.Log.LogInfo($"tunnel to host: {path} (sent {Tunnel.Up}, received {Tunnel.Down} datagrams)");
            }
        }

        public void Dispose()
        {
            ClearMemberData();
            Tunnel.Dispose();
            try { _punch?.Dispose(); } catch { }
            var l = _link;
            _link = null;
            if (l == null) return;
            l.Closing = true;
            try { l.Close(false, 0, "DirectHost tunnel closed"); } catch { }
        }
    }
}
