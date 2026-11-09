// REPO DirectHost — client: chooses how to reach the host. Starts as soon as the client enters the Steam lobby
// (seconds before the game calls ConnectUsingSettings), so usually it is finished before it is needed.
//
// Ladder (best first):
//   1. Direct:      host's public (white) IP or the same home LAN    — probed on both UDP ports, first answer wins
//   2. Punch:       own NAT punch-through (Net/Punch.cs), a direct UDP link arranged through lobby data — works when
//                   the host is behind CGNAT but the client is not (forwarded port), and for most "cone" NAT pairs
//      Steam P2P:   Steam's NAT punch-through (ICE), also a direct UDP path — 2 and this compete on ping
//   3. Virtual LAN: Radmin VPN / ZeroTier / Hamachi / Tailscale address of the host (also probed)
//   4. Steam relay: Valve SDR relays — last resort (detours via Europe, ~100-160 ms for RU <-> RU),
//                   used before a virtual LAN only if it is clearly faster.
// Paths 2 and 4 share one tunnel (ClientTunnel): if a direct link appears or dies later, the tunnel moves the traffic
// by itself, mid-game, without reconnecting.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using RepoDirectHost.Net;
using Steamworks.Data;

namespace RepoDirectHost.Game
{
    internal sealed class RouteResolver
    {
        public enum Path { Direct, Punch, SteamP2P, VirtualLan, SteamRelay, Steam, Unverified }

        private const int ProbeMs = 700;        // probe window for direct + virtual addresses
        private const int TunnelWaitMs = 2500;  // no virtual LAN: wait this long for a direct tunnel link before taking the relay
        private const int IceWaitMs = 4000;     // virtual LAN answered: give the direct tunnel links this long
        private const int SteamWaitMs = 7000;   // Steam not connected at all by then: take the virtual LAN

        public readonly ulong LobbyId, Owner;
        public bool Done { get; private set; }
        public bool Used;
        public Path Chosen { get; private set; }
        /// <summary>"ip:port" of the master server for Photon (127.0.0.1:tunnel port for tunnel paths).</summary>
        public string Address { get; private set; }
        /// <summary>The tunnel when a tunnel path was chosen (ownership passes to the caller), otherwise null.</summary>
        public ClientTunnel Tunnel { get; private set; }

        private readonly HostCandidates _c;
        private readonly List<IPAddress> _all = new List<IPAddress>();
        private readonly int _directCount;
        private volatile int[] _rtt;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public RouteResolver(Lobby lobby, ulong owner, HostCandidates c, string mode, ClientTunnel reuse)
        {
            LobbyId = lobby.Id.Value; Owner = owner; _c = c;
            _all.AddRange(c.Ips);
            _directCount = _all.Count;
            foreach (var v in c.Vips) if (!_all.Contains(v)) _all.Add(v);

            bool probe = mode != "steam" && _all.Count > 0;
            bool steam = mode != "direct" && c.Steam && owner != 0;
            bool punch = mode != "steam" && c.PunchPort > 0 && owner != 0;
            Plugin.Log.LogInfo($"route: checking the host — direct [{string.Join(", ", c.Ips)}], virtual LAN [{string.Join(", ", c.Vips)}], NAT punch {(punch ? "yes" : "no")}, Steam {(steam ? "yes" : "no")}");

            if (steam || punch)
            {
                if (reuse != null && reuse.Host.Value == owner && reuse.HasSteam == steam && reuse.HasPunch == punch) { Tunnel = reuse; reuse.Attach(lobby, c); }
                else { try { reuse?.Dispose(); } catch { } Tunnel = new ClientTunnel(owner, steam, punch ? c : null, lobby); }
            }
            else { try { reuse?.Dispose(); } catch { } }

            if (probe)
            {
                var list = _all; int directCount = _directCount; int m = c.MasterPort, g = c.GamePort;
                new Thread(() =>
                {
                    int[] r;
                    try { r = Probe.Measure(list, m, g, owner, ProbeMs, i => i < directCount, s => Plugin.Log.LogInfo(s)); }
                    catch (Exception e) { Plugin.Log.LogWarning("probe failed: " + e.Message); r = new int[list.Count]; for (int i = 0; i < r.Length; i++) r[i] = -1; }
                    _rtt = r;
                }) { IsBackground = true, Name = "DirectHost probe" }.Start();
            }
            else _rtt = new int[0];
        }

        /// <summary>Main thread, every frame until Done.</summary>
        public void Tick()
        {
            if (Done) return;
            var rtt = _rtt;
            if (rtt == null) return;                       // probe still running (<= 0.7 s)

            int d = Best(rtt, 0, Math.Min(_directCount, rtt.Length));
            if (d >= 0) { Finish(Path.Direct, _all[d], $"{_all[d]} answered in {rtt[d]} ms"); return; }
            int v = Best(rtt, _directCount, rtt.Length);

            if (Tunnel == null)
            {
                if (v >= 0) Finish(Path.VirtualLan, _all[v], $"{_all[v]} answered in {rtt[v]} ms");
                else if (_all.Count > 0) Finish(Path.Unverified, _all[0], "nothing answered and there is no tunnel fallback — trying anyway");
                else Fail("the host published no address and no Steam tunnel");
                return;
            }

            long t = _sw.ElapsedMilliseconds;
            bool punchUp = Tunnel.GetPunch(out int prtt);
            bool steamUp = Tunnel.GetSteamRoute(out int sping, out bool relayed);
            string vlan = v >= 0 ? $"; virtual LAN {_all[v]} was {rtt[v]} ms" : "";

            // a direct tunnel link (own punch or Steam ICE) is up: take the faster one unless the virtual LAN beats it
            int best = -1; Path bp = Path.Punch;
            if (punchUp) best = Math.Max(prtt, 0);
            if (steamUp && !relayed && sping >= 0 && (best < 0 || sping + 15 < best)) { best = sping; bp = Path.SteamP2P; }
            if (best >= 0)
            {
                if (v >= 0 && rtt[v] + 10 < best) { Finish(Path.VirtualLan, _all[v], $"{_all[v]} answered in {rtt[v]} ms, faster than {Name(bp)} ({best} ms)"); return; }
                FinishTunnel(bp, bp == Path.Punch ? $"own NAT punch-through to the host worked (ping {prtt} ms){vlan}" : $"Steam punched through directly (ping {sping} ms){vlan}");
                return;
            }

            if (v >= 0)
            {
                if (t < IceWaitMs) return;
                if (steamUp && sping > 0 && sping + 20 < rtt[v]) FinishTunnel(Path.SteamRelay, $"Steam relay {sping} ms is faster than virtual LAN {_all[v]} ({rtt[v]} ms)");
                else if (steamUp) Finish(Path.VirtualLan, _all[v], $"Steam has only a relay (ping {sping} ms); virtual LAN {_all[v]} answered in {rtt[v]} ms");
                else if (!Tunnel.HasSteam) Finish(Path.VirtualLan, _all[v], $"no direct tunnel link came up; virtual LAN {_all[v]} answered in {rtt[v]} ms");
                else if (t >= SteamWaitMs) Finish(Path.VirtualLan, _all[v], $"Steam did not connect in {SteamWaitMs / 1000} s; virtual LAN {_all[v]} answered in {rtt[v]} ms");
                return;
            }

            // Nothing to compare with: give a direct tunnel link a moment, then take what there is. The tunnel moves to a
            // direct link by itself whenever one comes up later.
            if (t < TunnelWaitMs) return;
            if (steamUp) FinishTunnel(Path.SteamRelay, $"no direct path yet: Steam relay (ping {sping} ms); switches to a direct link by itself if one comes up");
            else if (Tunnel.HasSteam) FinishTunnel(Path.Steam, "Steam is still connecting; P2P if Steam can punch through, otherwise relay");
            else if (t >= SteamWaitMs) FinishTunnel(Path.Punch, "no address of the host answered and the NAT punch-through has not worked yet — waiting on it");
        }

        private static int Best(int[] rtt, int from, int to)
        {
            int best = -1;
            for (int i = from; i < to; i++) if (rtt[i] >= 0 && (best < 0 || rtt[i] < rtt[best])) best = i;
            return best;
        }

        private void Finish(Path p, IPAddress ip, string why)
        {
            try { Tunnel?.Dispose(); } catch { }
            Tunnel = null;
            Address = ip + ":" + _c.MasterPort;
            Chosen = p;
            Done = true;
            Plugin.Log.LogInfo($"route: {Name(p)} -> {Address} ({why}; decided in {_sw.ElapsedMilliseconds} ms)");
        }

        private void FinishTunnel(Path p, string why)
        {
            Address = "127.0.0.1:" + Tunnel.Tunnel.MasterPort;
            Chosen = p;
            Done = true;
            Plugin.Log.LogInfo($"route: {Name(p)} ({why}; decided in {_sw.ElapsedMilliseconds} ms)");
        }

        private void Fail(string why)
        {
            try { Tunnel?.Dispose(); } catch { }
            Tunnel = null;
            Address = null;
            Done = true;
            Plugin.Log.LogError("route: " + why);
        }

        /// <summary>Out of time: take the best thing known so far (the tunnel if there is one, else the first address).</summary>
        public void ForceDecide()
        {
            if (Done) return;
            if (Tunnel != null) FinishTunnel(Tunnel.HasSteam ? Path.Steam : Path.Punch, "timeout while comparing paths");
            else if (_all.Count > 0) Finish(Path.Unverified, _all[0], "timeout");
            else Fail("timeout and nothing to connect to");
        }

        /// <summary>Abandon (lobby left / replaced): close the tunnel unless it was handed over.</summary>
        public void Cancel()
        {
            if (Done) return;
            Done = true;
            try { Tunnel?.Dispose(); } catch { }
            Tunnel = null;
        }

        private static string Name(Path p)
        {
            switch (p)
            {
                case Path.Direct: return "DIRECT (public IP / LAN)";
                case Path.Punch: return "DIRECT (own NAT punch-through)";
                case Path.SteamP2P: return "STEAM P2P (direct)";
                case Path.VirtualLan: return "VIRTUAL LAN (Radmin/ZeroTier/Hamachi/Tailscale)";
                case Path.SteamRelay: return "STEAM RELAY";
                case Path.Steam: return "STEAM (P2P if Steam can punch through, otherwise relay)";
                default: return "UNVERIFIED address";
            }
        }
    }
}
