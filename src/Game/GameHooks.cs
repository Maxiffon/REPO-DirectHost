// REPO DirectHost — wiring into the game: the Steam lobby owner hosts the server inside the game,
// everyone points Photon (PUN + Voice) at it. Parts of the client redirect follow the idea of
// REPO-SelfHostedPhoton (MIT, (c) 2025 zyxterus): UseNameServer=false + custom Server/Port, "eu" region fallback.
using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using RepoDirectHost.Net;
using RepoDirectHost.Server;
using Steamworks;
using Steamworks.Data;

namespace RepoDirectHost.Game
{
    internal static class GameHooks
    {
        public const string LobbyAddressKey = "directhost_addr";       // 0.1.x: "ip:port"
        public const string LobbyCandidatesKey = "directhost_cand";    // 0.2+: see HostCandidates

        private static UdpServer _udp;
        private static PhotonServer _server;
        private static SteamTransport.HostSocket _steamHost;
        private static TunnelHost _tunnelHost;
        private static PunchHost _punchHost;
        private static readonly System.Collections.Generic.Dictionary<ulong, string> _punchSeen = new System.Collections.Generic.Dictionary<ulong, string>();
        private static ClientTunnel _tunnel;
        private static volatile string _publicIp;
        private static volatile System.Collections.Generic.List<IPAddress> _publicIps;
        private static string _published, _publishedCand;
        private static FieldInfo _currentLobby, _connectRandom;
        internal static bool Hosting => _udp != null;

        public static void Apply(Harmony h)
        {
            _currentLobby = AccessTools.Field(typeof(SteamLobbyManager), "currentLobby");
            _connectRandom = AccessTools.Field(typeof(GameManager), "connectRandom");
            Patch(h, AccessTools.Method(typeof(PhotonNetwork), nameof(PhotonNetwork.ConnectUsingSettings), Type.EmptyTypes), prefix: nameof(ConnectPrefix));
            Patch(h, AccessTools.PropertySetter(typeof(LoadBalancingClient), nameof(LoadBalancingClient.GameServerAddress)), prefix: nameof(GameServerAddressPrefix));
            Patch(h, AccessTools.Method(typeof(Lobby), nameof(Lobby.SetData)), prefix: nameof(LobbySetDataPrefix));
            try
            {
                SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
                SteamMatchmaking.OnLobbyMemberDataChanged += (lobby, member) => { try { ReadPunchData(lobby); } catch { } };
            }
            catch (Exception e) { Plugin.Log.LogWarning("lobby hooks: " + e.Message); }

            if (string.IsNullOrWhiteSpace(Plugin.PublicAddress.Value))
                new Thread(() =>
                {
                    var all = Stun.QueryAll();
                    foreach (var f in all) Plugin.Log.LogInfo($"public address (STUN): {f.ip} — seen by {f.servers}");
                    if (all.Count > 1) Plugin.Log.LogInfo("several public addresses: a VPN routes some STUN servers; all are published, clients test which one works");
                    if (all.Count == 0) Plugin.Log.LogInfo("public address: STUN failed — set [Network] PublicAddress if you host");
                    _publicIps = all.Select(f => f.ip).ToList();
                    _publicIp = all.Count > 0 ? all[0].ip.ToString() : null;
                }) { IsBackground = true, Name = "DirectHost STUN" }.Start();
        }

        private static void Patch(Harmony h, MethodBase target, string prefix)
        {
            if (target == null) { Plugin.Log.LogError($"patch target for {prefix} not found — game update?"); return; }
            h.Patch(target, prefix: new HarmonyMethod(typeof(GameHooks), prefix));
        }

        private static Lobby CurrentLobby() => _currentLobby != null && SteamLobbyManager.instance != null ? (Lobby)_currentLobby.GetValue(SteamLobbyManager.instance) : default;

        private static bool AmLobbyOwner(Lobby lobby)
        {
            try { return lobby.Id.IsValid && lobby.IsOwnedBy(SteamClient.SteamId); } catch { return false; }
        }

        // ---------------------------------------------------------------- PhotonNetwork.ConnectUsingSettings()
        // Returning false skips the original call: a client whose route is still being decided connects a moment
        // later (Frame() calls ConnectUsingSettings again). REPO ignores the return value and waits for callbacks.
        private static bool ConnectPrefix(ref bool __result)
        {
            try
            {
                if (GameManager.instance != null && _connectRandom != null && (bool)_connectRandom.GetValue(GameManager.instance))
                {
                    Plugin.Log.LogInfo("public matchmaking (connectRandom) — using the official Photon cloud, DirectHost not used");
                    return true;
                }
                var lobby = CurrentLobby();
                bool host = !lobby.Id.IsValid || AmLobbyOwner(lobby) || (GameManager.instance != null && GameManager.instance.localTest);
                string address;
                if (host)
                {
                    CancelResolver();
                    CloseTunnel();
                    StartServer();
                    address = "127.0.0.1";
                    PublishAddress(lobby);
                }
                else
                {
                    if (_udp != null)
                    {
                        Plugin.Log.LogInfo("joining someone else's lobby: own server stopped");
                        StopServer();
                    }
                    address = ClientAddress(lobby, out bool wait);
                    if (wait)
                    {
                        _connectPending = true;
                        __result = true;
                        Plugin.Log.LogInfo("CLIENT: connect postponed until the route to the host is chosen");
                        return false;
                    }
                    if (string.IsNullOrEmpty(address)) return true;
                }
                ParseHostPort(address, Plugin.MasterPort.Value, out string ip, out int port);

                var s = PhotonNetwork.PhotonServerSettings.AppSettings;
                s.UseNameServer = false;
                s.Server = ip;
                s.Port = port;
                s.AuthMode = AuthModeOption.Auth;
                s.Protocol = ExitGames.Client.Photon.ConnectionProtocol.Udp;
                if (string.IsNullOrEmpty(s.FixedRegion)) s.FixedRegion = "eu";
                PhotonNetwork.ServerPortOverrides = default;   // REPO forces 27000-27002; our server uses its own ports
                FakeRegionList();
                Plugin.Log.LogInfo($"{(host ? "HOST" : "CLIENT")}: Photon -> {ip}:{port} (master)");
            }
            catch (Exception e) { Plugin.Log.LogError("ConnectUsingSettings hook failed: " + e); }
            return true;
        }

        // ---------------------------------------------------------------- client: pick the connection path
        // See RouteResolver for the ladder. It starts on Steam lobby entry; ConnectUsingSettings waits for it.
        private static RouteResolver _resolver;
        private static bool _connectPending;
        private static float _pendingSince;

        private static string Mode => (Plugin.Route.Value ?? "Auto").Trim().ToLowerInvariant();

        private static void OnLobbyEntered(Lobby lobby)
        {
            try
            {
                if (AmLobbyOwner(lobby) || !string.IsNullOrEmpty(Plugin.HostAddress.Value?.Trim())) return;
                StartResolver(lobby);
            }
            catch (Exception e) { Plugin.Log.LogWarning("route check on lobby entry failed: " + e.Message); }
        }

        private static bool StartResolver(Lobby lobby)
        {
            var cand = HostCandidates.Parse(lobby.GetData(LobbyCandidatesKey));
            if (cand == null) return false;
            ulong owner = 0;
            try { owner = lobby.Owner.Id.Value; } catch { }
            CancelResolver();
            var reuse = _tunnel;      // an existing tunnel to the same host is reused instead of reconnecting
            _tunnel = null;
            _resolver = new RouteResolver(lobby, owner, cand, Mode, reuse);
            _resolver.Tick();
            return true;
        }

        private static void CancelResolver()
        {
            var r = _resolver;
            _resolver = null;
            _connectPending = false;
            r?.Cancel();
        }

        /// <summary>Master address for this client, or wait=true while the route is still being chosen.</summary>
        private static string ClientAddress(Lobby lobby, out bool wait)
        {
            wait = false;
            string manual = Plugin.HostAddress.Value?.Trim();
            if (!string.IsNullOrEmpty(manual))
            {
                CancelResolver();
                CloseTunnel();
                Plugin.Log.LogInfo("route: manual [Network] HostAddress " + manual);
                return manual;
            }
            if (_resolver == null || _resolver.LobbyId != lobby.Id.Value || _resolver.Used)
            {
                if (!StartResolver(lobby))
                {
                    string old = lobby.GetData(LobbyAddressKey);       // host runs DirectHost 0.1.x
                    if (string.IsNullOrEmpty(old))
                    {
                        Plugin.Log.LogError("the host did not publish a DirectHost address in the Steam lobby (does the host have REPO DirectHost?). Set [Network] HostAddress manually.");
                        return null;
                    }
                    CloseTunnel();
                    return old;
                }
            }
            if (!_resolver.Done)
            {
                if (!_connectPending) _pendingSince = UnityEngine.Time.realtimeSinceStartup;
                wait = true;
                return null;
            }
            _resolver.Used = true;
            _connectPending = false;
            if (_resolver.Tunnel != null && _resolver.Tunnel != _tunnel) { CloseTunnel(); _tunnel = _resolver.Tunnel; }
            else if (_resolver.Tunnel == null) CloseTunnel();
            return _resolver.Address;
        }

        /// <summary>Plugin.Update, every frame: drive the route choice, then run the postponed connect.</summary>
        internal static void Frame()
        {
            var r = _resolver;
            var rt = r?.Tunnel;
            try { rt?.Frame(); if (_tunnel != null && _tunnel != rt) _tunnel.Frame(); } catch (Exception e) { Plugin.Log.LogWarning("tunnel: " + e.Message); }
            if (r == null) return;
            try { r.Tick(); } catch (Exception e) { Plugin.Log.LogError("route: " + e); }
            if (!_connectPending) return;
            if (!r.Done && UnityEngine.Time.realtimeSinceStartup - _pendingSince < 12f) return;
            if (!r.Done) { Plugin.Log.LogWarning("route: still undecided after 12 s, connecting with what we have"); r.ForceDecide(); }
            _connectPending = false;
            try { PhotonNetwork.ConnectUsingSettings(); } catch (Exception e) { Plugin.Log.LogError("postponed connect failed: " + e); }
        }

        private static void CloseTunnel()
        {
            var t = _tunnel;
            _tunnel = null;
            if (t == null) return;
            try { t.Dispose(); } catch { }
            Plugin.Log.LogInfo("Steam tunnel closed");
        }

        private static bool AllowTunnel(SteamId who)
        {
            try
            {
                var lobby = CurrentLobby();
                if (!lobby.Id.IsValid) return false;
                foreach (var m in lobby.Members) if (m.Id == who) return true;
            }
            catch { }
            return false;
        }

        // Without the Photon name server the client never receives a region list, so RegionHandler stays null and
        // the "Host game / Play random" region page (MenuPageRegions.GetRegions) throws on EnabledRegions.
        // There is only one server (the host's), so offer a single region; its code matches the "eu" fallback.
        private static PropertyInfo _enabledRegions;
        private static void FakeRegionList()
        {
            try
            {
                var client = PhotonNetwork.NetworkingClient;
                if (client == null) return;
                var rh = client.RegionHandler ?? new RegionHandler();
                if (rh.EnabledRegions != null && rh.EnabledRegions.Count > 0) { client.RegionHandler = rh; return; }
                _enabledRegions = _enabledRegions ?? AccessTools.Property(typeof(RegionHandler), nameof(RegionHandler.EnabledRegions));
                _enabledRegions.SetValue(rh, new System.Collections.Generic.List<Region> { new Region("eu", 1) });
                client.RegionHandler = rh;
            }
            catch (Exception e) { Plugin.Log.LogWarning("could not fill the region list: " + e.Message); }
        }

        private static void ParseHostPort(string s, int defPort, out string host, out int port)
        {
            host = s; port = defPort;
            int i = s.LastIndexOf(':');
            if (i > 0 && int.TryParse(s.Substring(i + 1), out int p)) { host = s.Substring(0, i); port = p; }
        }

        private static void StartServer()
        {
            if (_udp != null) return;
            try
            {
                UdpServer udp = null;
                _server = new PhotonServer(UdpServer.Now, (c, r) => udp?.Hosts.FirstOrDefault(x => x.Role == c.Role)?.Close(c, r, true))
                {
                    GameServerAddress = "@:" + Plugin.GamePort.Value,
                    Log = s => Plugin.Log.LogInfo(s),
                    Warn = s => Plugin.Log.LogWarning(s),
                };
                udp = new UdpServer(_server, IPAddress.Any, (Plugin.MasterPort.Value, Role.Master), (Plugin.GamePort.Value, Role.Game))
                {
                    Log = Plugin.Verbose.Value ? (Action<string>)(s => Plugin.Log.LogInfo(s)) : null,
                    OnError = e => Plugin.Log.LogError("server loop: " + e),
                    Tick = _server.Tick,
                };
                try { udp.ProbeTag = SteamClient.SteamId.Value; } catch { }
                foreach (var w in udp.BindWarnings) Plugin.Log.LogWarning(w);
                var th = new TunnelHost(udp);
                PunchHost ph = null;
                try
                {
                    ph = new PunchHost(th, Plugin.GamePort.Value + 1) { Log = s => Plugin.Log.LogInfo(s) };
                    if (ph.Port != Plugin.GamePort.Value + 1)
                        Plugin.Log.LogWarning($"UDP {Plugin.GamePort.Value + 1} is taken by another program — NAT punch-through uses UDP {ph.Port} instead");
                    try { ph.Tag = SteamClient.SteamId.Value; } catch { }
                    udp.AddSocket(ph.Sock, ph.OnDatagram, ph.Tick);
                    ph.StartStun();
                }
                catch (Exception e) { Plugin.Log.LogWarning("NAT punch-through unavailable: " + e.Message); ph = null; }
                udp.Start();
                _udp = udp;
                _tunnelHost = th;
                _punchHost = ph;
                _punchSeen.Clear();
                Plugin.Log.LogInfo($"server started: UDP {Plugin.MasterPort.Value} (master) + {Plugin.GamePort.Value} (game){(ph != null ? $" + {ph.Port} (NAT punch-through)" : "")}");
                try
                {
                    _steamHost = SteamTransport.Listen(udp, th, AllowTunnel);
                    Plugin.Log.LogInfo("Steam tunnel open: friends who cannot reach your address directly connect through Steam automatically");
                }
                catch (Exception e) { Plugin.Log.LogWarning("Steam tunnel unavailable (only direct connections will work): " + e.Message); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"could not start the server on UDP {Plugin.MasterPort.Value}/{Plugin.GamePort.Value} (port busy? another P2P mod running?): {e.Message}");
            }
        }

        /// <summary>
        /// Host: publishes every way to reach this PC in the Steam lobby — public IP (STUN or PublicAddress), all local
        /// IPv4 addresses (LAN, Radmin/Hamachi/ZeroTier/Tailscale adapters) and whether the Steam tunnel is open.
        /// Also keeps the 0.1.x key ("ip:port") for older clients.
        /// </summary>
        internal static void PublishAddress(Lobby lobby)
        {
            if (!lobby.Id.IsValid || !AmLobbyOwner(lobby)) return;
            string pub = Plugin.PublicAddress.Value?.Trim();
            if (string.IsNullOrEmpty(pub)) pub = _publicIp;

            var cand = new HostCandidates { MasterPort = Plugin.MasterPort.Value, GamePort = Plugin.GamePort.Value, Steam = _steamHost != null, PunchPort = _punchHost?.Port ?? 0 };
            if (_punchHost != null) cand.PunchEps.AddRange(_punchHost.Mapped());
            string manualPub = Plugin.PublicAddress.Value?.Trim();
            if (!string.IsNullOrEmpty(manualPub))
            {
                ParseHostPort(manualPub, Plugin.MasterPort.Value, out string pubIp, out _);
                if (IPAddress.TryParse(pubIp, out var a)) cand.Ips.Add(a);
            }
            else
            {
                var ips = _publicIps;
                if (ips != null) foreach (var a in ips) if (!cand.Ips.Contains(a)) cand.Ips.Add(a);
            }
            RefreshLocalAddresses();
            foreach (var a in _lan) if (!cand.Ips.Contains(a)) cand.Ips.Add(a);
            foreach (var a in _vlan) if (!cand.Ips.Contains(a) && !cand.Vips.Contains(a)) cand.Vips.Add(a);
            string value = cand.ToString();
            if (value != _publishedCand || lobby.GetData(LobbyCandidatesKey) != value)
                if (lobby.SetData(LobbyCandidatesKey, value)) { _publishedCand = value; Plugin.Log.LogInfo($"published in Steam lobby: {value}"); }

            if (string.IsNullOrEmpty(pub)) return;
            string old = pub.Contains(":") ? pub : pub + ":" + Plugin.MasterPort.Value;
            if (old == _published && lobby.GetData(LobbyAddressKey) == old) return;
            if (lobby.SetData(LobbyAddressKey, old)) _published = old;
        }

        // Local IPv4 addresses, split into the real network (LAN: same house = best path) and virtual LANs
        // (Radmin VPN, ZeroTier, Hamachi, Tailscale, ...): they rank below Steam P2P in the client's ladder.
        private static System.Collections.Generic.List<IPAddress> _lan = new System.Collections.Generic.List<IPAddress>(), _vlan = new System.Collections.Generic.List<IPAddress>();
        private static DateTime _localAt;
        private static readonly string[] VirtualAdapters = { "radmin", "zerotier", "hamachi", "tailscale", "wireguard", "openvpn", "tap-windows", "wintun", "softether", "nordlynx", "netbird", "vpn" };

        private static void RefreshLocalAddresses()
        {
            if (DateTime.UtcNow - _localAt < TimeSpan.FromSeconds(30)) return;
            _localAt = DateTime.UtcNow;
            var lan = new System.Collections.Generic.List<IPAddress>();
            var vlan = new System.Collections.Generic.List<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    string name = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
                    bool virt = VirtualAdapters.Any(k => name.Contains(k));
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        var b = a.GetAddressBytes();
                        if (b[0] == 0 || b[0] == 127) continue;
                        if (b[0] == 169 && b[1] == 254) continue;                 // no DHCP lease
                        if (b[0] == 198 && (b[1] & 0xFE) == 18) continue;         // 198.18/15: Clash/mihomo TUN fake addresses
                        bool v = virt || b[0] == 26 || b[0] == 25                  // Radmin VPN 26/8, Hamachi 25/8
                                 || (b[0] == 100 && (b[1] & 0xC0) == 64);          // Tailscale 100.64/10
                        var list = v ? vlan : lan;
                        if (!list.Contains(a)) list.Add(a);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("could not list local addresses: " + e.Message); }
            _lan = lan;
            _vlan = vlan;
        }

        /// <summary>Called from Plugin.Update about once a second.</summary>
        internal static void Poll()
        {
            try { SteamTransport.Configure(); } catch { }
            try { _tunnel?.Poll(); _tunnel?.WatchHost(); } catch (Exception e) { Plugin.Log.LogWarning("tunnel poll: " + e.Message); }
            if (_udp == null) return;
            var lobby = CurrentLobby();
            try { PublishAddress(lobby); } catch { }
            try { ReadPunchData(lobby); } catch (Exception e) { Plugin.Log.LogWarning("punch data: " + e.Message); }
            try
            {
                _steamHost?.Poll();
                if (DateTime.UtcNow >= _hostReportAt)
                {
                    _hostReportAt = DateTime.UtcNow.AddSeconds(30);
                    if (_steamHost != null)
                        foreach (var c in _steamHost.Connected)
                            Plugin.Log.LogInfo($"Steam tunnel {c.Id}: {SteamTransport.Describe(c)}");
                    ReportPlayers(lobby);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("steam host poll: " + e.Message); }
        }
        private static DateTime _hostReportAt;

        /// <summary>
        /// Host: lobby members publish their punch endpoints as member data ("v=1;k=key;n=;ep=ip:port,..."); hand
        /// new/changed ones to the punch socket (it starts knocking), forget members that left.
        /// </summary>
        private static void ReadPunchData(Lobby lobby)
        {
            var ph = _punchHost; var udp = _udp;
            if (ph == null || udp == null || !lobby.Id.IsValid || !AmLobbyOwner(lobby)) return;
            ulong me = SteamClient.SteamId.Value;
            var present = new System.Collections.Generic.HashSet<ulong>();
            foreach (var m in lobby.Members)
            {
                ulong id = m.Id.Value;
                if (id == me) continue;
                present.Add(id);
                string v = lobby.GetMemberData(m, ClientTunnel.MemberKey) ?? "";
                if (_punchSeen.TryGetValue(id, out var seen) && seen == v) continue;
                _punchSeen[id] = v;
                byte[] key = null;
                var eps = new System.Collections.Generic.List<IPEndPoint>();
                foreach (var part in v.Split(';'))
                {
                    if (part.StartsWith("k=")) key = PunchWire.FromHex(part.Substring(2));
                    else if (part.StartsWith("ep="))
                        foreach (var s in part.Substring(3).Split(','))
                            if (PunchWire.TryParseEndpoint(s.Trim(), out var ep) && !eps.Contains(ep) && eps.Count < 8) eps.Add(ep);
                }
                if (key == null) udp.Post(() => ph.RemoveClient(id));
                else udp.Post(() => ph.SetClient(id, key, eps));
            }
            foreach (var id in _punchSeen.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _punchSeen.Remove(id);
                udp.Post(() => ph.RemoveClient(id));
            }
        }

        /// <summary>Host, every 30 s: everyone's ping and path, e.g. "players: Alice 8 ms (direct UDP), Bob 51 ms (own NAT punch)".</summary>
        private static void ReportPlayers(Lobby lobby)
        {
            var udp = _udp; var th = _tunnelHost;
            if (udp == null) return;
            var names = new System.Collections.Generic.Dictionary<string, string>();
            try { foreach (var m in lobby.Members) names[m.Id.Value.ToString()] = m.Name; } catch { }
            string self = "";
            try { self = SteamClient.SteamId.Value.ToString(); } catch { }
            udp.Post(() =>
            {
                var game = udp.HostFor(Role.Game);
                if (game == null) return;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var c in game.Connections)
                {
                    if (!(c.Tag is PhotonServer.Peer p) || p.Voice || p.Room == null || p.UserId == null) continue;
                    if (p.UserId == self) continue;       // the host itself (loopback)
                    string path = "direct UDP";
                    int slash = c.Key.IndexOf('/');
                    if (!c.Key.StartsWith("udp:") && slash > 0 && th != null && th.LastLink.TryGetValue(c.Key.Substring(0, slash), out var link))
                        path = link.StartsWith("punch:") ? "own NAT punch" : "Steam";
                    parts.Add($"{(names.TryGetValue(p.UserId, out var n) ? n : p.UserId)} {c.Rtt} ms ({path})");
                }
                if (parts.Count > 0) Plugin.Log.LogInfo("players: " + string.Join(", ", parts));
            });
        }

        private static void StopServer()
        {
            try { _udp?.Dispose(); } catch { }
            _udp = null;
            try { _steamHost?.Stop(); } catch { }
            _steamHost = null;
            _punchHost = null;
            _tunnelHost = null;
            _server = null;
            _punchSeen.Clear();
            _published = null;
            _publishedCand = null;
        }

        // ---------------------------------------------------------------- LoadBalancingClient.GameServerAddress setter
        // The server returns "@:<port>": use the same host the client used for the master server
        // (through the Steam tunnel: the tunnel's local game port).
        private static void GameServerAddressPrefix(LoadBalancingClient __instance, ref string value)
        {
            if (value == null || !value.StartsWith("@:")) return;
            string master = __instance.MasterServerAddress ?? "";
            var t = _tunnel;
            if (t != null && master == "127.0.0.1:" + t.Tunnel.MasterPort) { value = "127.0.0.1:" + t.Tunnel.GamePort; return; }
            int i = master.LastIndexOf(':');
            string host = i > 0 ? master.Substring(0, i) : master;
            value = host + value.Substring(1);
        }

        // ---------------------------------------------------------------- Lobby.SetData (region fallback, from SelfHostedPhoton)
        private static void LobbySetDataPrefix(string key, ref string value)
        {
            if (key == "Region" && string.IsNullOrWhiteSpace(value)) value = "eu";
        }

        // ---------------------------------------------------------------- test-bot support (Debug.BotSupport)
        // Writes the view ids of the scene objects that wait for "ready" RPCs from every player, so the
        // offline test bot (tests/LiveTest.cs "player" mode) can answer them like a real client.
        private static string _lastViews;
        internal static void DumpViews()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                void Add<T>(string name) where T : UnityEngine.Component
                {
                    var o = UnityEngine.Object.FindFirstObjectByType<T>();
                    var pv = o != null ? o.GetComponent<PhotonView>() : null;
                    if (pv != null && pv.ViewID != 0) sb.Append(name).Append('=').Append(pv.ViewID).Append('\n');
                }
                Add<ReloadScene>("ReloadScene");
                Add<LevelGenerator>("LevelGenerator");
                Add<ValuableDirector>("ValuableDirector");
                Add<NetworkManager>("NetworkManager");
                sb.Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append('\n');
                string text = sb.ToString();
                if (text == _lastViews) return;
                _lastViews = text;
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "directhost-views.txt");
                System.IO.File.WriteAllText(path + ".tmp", text);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(path + ".tmp", path);
            }
            catch (Exception e) { Plugin.Log.LogWarning("view dump failed: " + e.Message); }
        }

        public static void Shutdown()
        {
            CancelResolver();
            CloseTunnel();
            StopServer();
        }
    }

    /// <summary>
    /// Lobby value "v=1;m=7777;g=7778;s=1;ip=203.0.113.7,192.168.1.5;vip=26.1.2.3;pp=7779" — every way to reach the host:
    /// ip = public IP + real LAN (ladder step 1), pp = NAT punch-through port (step 2), vip = virtual LAN adapters
    /// (step 3), s = Steam tunnel (steps 2 and 4). Older clients ignore keys they do not know.
    /// </summary>
    internal sealed class HostCandidates
    {
        public readonly System.Collections.Generic.List<IPAddress> Ips = new System.Collections.Generic.List<IPAddress>();
        public readonly System.Collections.Generic.List<IPAddress> Vips = new System.Collections.Generic.List<IPAddress>();
        /// <summary>Public (STUN-mapped) endpoints of the host's punch socket.</summary>
        public readonly System.Collections.Generic.List<IPEndPoint> PunchEps = new System.Collections.Generic.List<IPEndPoint>();
        public int MasterPort = 7777, GamePort = 7778, PunchPort;
        public bool Steam;

        public override string ToString() => $"v=1;m={MasterPort};g={GamePort};s={(Steam ? 1 : 0)};ip={string.Join(",", Ips)};vip={string.Join(",", Vips)}"
            + (PunchPort > 0 ? $";pp={PunchPort};pe={string.Join(",", PunchEps)}" : "");

        public static HostCandidates Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("v=")) return null;
            var c = new HostCandidates();
            foreach (var part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                switch (k)
                {
                    case "m": int.TryParse(v, out c.MasterPort); break;
                    case "g": int.TryParse(v, out c.GamePort); break;
                    case "pp": int.TryParse(v, out c.PunchPort); break;
                    case "pe":
                        foreach (var e in v.Split(','))
                            if (PunchWire.TryParseEndpoint(e.Trim(), out var ep) && !c.PunchEps.Contains(ep) && c.PunchEps.Count < 8) c.PunchEps.Add(ep);
                        break;
                    case "s": c.Steam = v == "1"; break;
                    case "ip":
                    case "vip":
                        var list = k == "ip" ? c.Ips : c.Vips;
                        foreach (var ip in v.Split(','))
                            if (IPAddress.TryParse(ip.Trim(), out var a) && !list.Contains(a)) list.Add(a);
                        break;
                }
            }
            return c;
        }
    }
}
