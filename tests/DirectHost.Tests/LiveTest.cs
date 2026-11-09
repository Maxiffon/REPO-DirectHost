// Live test against the DirectHost server running INSIDE the game: a real Photon Realtime client joins the
// host's actual room (and its voice room) as an extra player, records what it receives, then leaves.
// Usage: DirectHost.Tests.dll live <roomName> [loss] [host] [masterPort]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;

internal static class LiveTest
{
    private const string AppRealtime = "11111111-2222-3333-4444-555555555555"; // the server does not check it
    private static readonly string AppVoice = new RepoDirectHost.Server.PhotonServer(() => 0, (c, r) => { }).VoiceAppIdPrefix + "0000"; // the prefix marks voice
    private static readonly PropertyInfo GsAddr = typeof(LoadBalancingClient).GetProperty(nameof(LoadBalancingClient.GameServerAddress));

    private sealed class Probe
    {
        public readonly LoadBalancingClient C;
        public readonly List<(long t, EventData e)> Events = new List<(long, EventData)>();
        public readonly List<string> States = new List<string>();
        public string LastError = "";
        private readonly Stopwatch _sw;
        public Probe(string appId, string userId, string host, int port, Stopwatch sw)
        {
            _sw = sw;
            C = new LoadBalancingClient(ConnectionProtocol.Udp) { AuthValues = new AuthenticationValues(userId), NickName = "DH-probe" };
            C.EventReceived += e =>
            {
                var copy = new EventData { Code = e.Code };
                foreach (var kv in e.Parameters) copy.Parameters.Add(kv.Key, kv.Value);
                Events.Add((_sw.ElapsedMilliseconds, copy));
            };
            C.OpResponseReceived += r => { if (r.ReturnCode != 0) LastError = $"op {r.OperationCode} rc {r.ReturnCode} {r.DebugMessage}"; };
            C.StateChanged += (a, b) =>
            {
                States.Add(b.ToString());
                // same rewrite the mod does in the game: "@:7778" = same host as the master server
                var gs = C.GameServerAddress;
                if (gs != null && gs.StartsWith("@:")) GsAddr.SetValue(C, host + ":" + RewritePort(int.Parse(gs.Substring(2))));
            };
            C.ConnectUsingSettings(new AppSettings { AppIdRealtime = appId, Server = host, Port = port, UseNameServer = false, Protocol = ConnectionProtocol.Udp, AppVersion = "v0.5.0.net3", AuthMode = AuthModeOption.Auth });
        }
        public static Func<int, int> RewritePort = p => p;
    }

    private static bool Pump(IEnumerable<Probe> ps, Func<bool> until, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var p in ps) p.C.Service();
            if (until()) return true;
            Thread.Sleep(5);
        }
        return until();
    }

    private static bool Check(string what, bool ok) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); return ok; }

    /// <summary>Probe stays in the room while the host starts a level; leaves 15 s after the scene changes (REPO waits for every player).</summary>
    public static bool Stay(string room, double loss, int maxSeconds)
    {
        Console.WriteLine($"== LIVE stay: probe sits in '{room}' while the host starts a level, {loss:P0} extra loss; max {maxSeconds} s");
        LossyProxy pm = null, pg = null;
        int port = 7777;
        if (loss > 0) { pm = new LossyProxy(27777, 7777, loss); pg = new LossyProxy(27778, 7778, loss); port = 27777; Probe.RewritePort = p => p == 7778 ? 27778 : p; }
        var clock = Stopwatch.StartNew();
        var probe = new Probe(AppRealtime, "76561190000000001", "127.0.0.1", port, clock);
        probe.C.LoadBalancingPeer.TrafficStatsEnabled = true;
        bool ok = true;
        try
        {
            bool req = false;
            if (!Pump(new[] { probe }, () =>
            {
                if (!req && probe.C.State == ClientState.ConnectedToMasterServer) { req = true; probe.C.OpJoinRoom(new EnterRoomParams { RoomName = room }); }
                return probe.C.State == ClientState.Joined;
            }, 20000)) { Console.WriteLine("  could not join: " + string.Join(",", probe.States) + " " + probe.LastError); return false; }
            string scene0 = probe.C.CurrentRoom.CustomProperties["curScn"]?.ToString();
            Console.WriteLine($"  joined as actor {probe.C.LocalPlayer.ActorNumber}, scene '{scene0}'. START THE LEVEL NOW.");
            long sceneChangedAt = -1, lastReport = 0;
            string scene = scene0;
            while (clock.ElapsedMilliseconds < maxSeconds * 1000L)
            {
                probe.C.Service();
                Thread.Sleep(5);
                if (probe.C.State != ClientState.Joined) { Console.WriteLine($"  !! probe no longer joined: {probe.C.State} ({probe.C.DisconnectedCause})"); ok = false; break; }
                string s = probe.C.CurrentRoom.CustomProperties["curScn"]?.ToString();
                if (s != scene) { Console.WriteLine($"  [{clock.ElapsedMilliseconds / 1000.0:F1}s] scene '{scene}' -> '{s}'"); scene = s; if (sceneChangedAt < 0) sceneChangedAt = clock.ElapsedMilliseconds; }
                if (clock.ElapsedMilliseconds - lastReport > 3000) { lastReport = clock.ElapsedMilliseconds; Report(probe, clock); }
                if (sceneChangedAt >= 0 && clock.ElapsedMilliseconds - sceneChangedAt > 15000) break;
            }
            Report(probe, clock);
            var byCode = probe.Events.GroupBy(x => x.e.Code).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            Console.WriteLine($"  total events: {probe.Events.Count} ({string.Join(" ", byCode)})");
            ok &= Check("scene change seen by the probe", sceneChangedAt >= 0);
            ok &= Check("no kick", !probe.Events.Any(x => x.e.Code == 199));
            probe.C.OpLeaveRoom(false);
            Pump(new[] { probe }, () => probe.C.State != ClientState.Joined, 5000);
            probe.C.Disconnect();
            Pump(new[] { probe }, () => false, 500);
            Console.WriteLine($"  [{clock.ElapsedMilliseconds / 1000.0:F1}s] probe left — the host's loading should continue now");
        }
        finally
        {
            pm?.Dispose(); pg?.Dispose();
            if (pm != null) Console.WriteLine($"  proxies dropped {pm.Dropped + pg.Dropped} of {pm.Dropped + pg.Dropped + pm.Forwarded + pg.Forwarded}");
        }
        return ok;
    }

    // ready RPCs a real client sends to the master during a level change (see ReloadScene/LevelGenerator/ValuableDirector/NetworkManager)
    private static readonly Dictionary<string, string[]> ReadyRpcs = new Dictionary<string, string[]>
    {
        ["ReloadScene"] = new[] { "PlayerReady" },
        ["LevelGenerator"] = new[] { "ModulesReadyRPC", "EnemyReadyRPC", "PlayerSpawnedRPC" },
        ["ValuableDirector"] = new[] { "PlayerReadyValuableRPC", "PlayerReadyCosmeticWorldObjectRPC", "VolumesAndSwitchReadyRPC" },
        ["NetworkManager"] = new[] { "PlayerSpawnedRPC" },
    };

    /// <summary>
    /// Bot "player": sits in the room and answers the ready handshakes (view ids from the host mod's
    /// Debug.BotSupport dump), so the host really generates and loads levels with a remote player present.
    /// Watches the incoming stream for stalls.
    /// </summary>
    public static bool Player(string room, double loss, int maxSeconds, string viewsFile)
    {
        Console.WriteLine($"== BOT player in '{room}', {loss:P0} extra loss, up to {maxSeconds} s; views from {viewsFile}");
        LossyProxy pm = null, pg = null;
        int port = 7777;
        if (loss > 0) { pm = new LossyProxy(27777, 7777, loss); pg = new LossyProxy(27778, 7778, loss); port = 27777; Probe.RewritePort = p => p == 7778 ? 27778 : p; }
        var clock = Stopwatch.StartNew();
        var probe = new Probe(AppRealtime, "76561190000000001", "127.0.0.1", port, clock);
        probe.C.LoadBalancingPeer.TrafficStatsEnabled = true;
        bool ok = true;
        try
        {
            bool req = false;
            if (!Pump(new[] { probe }, () =>
            {
                if (!req && probe.C.State == ClientState.ConnectedToMasterServer) { req = true; probe.C.OpJoinRoom(new EnterRoomParams { RoomName = room }); }
                return probe.C.State == ClientState.Joined;
            }, 20000)) { Console.WriteLine("  could not join: " + string.Join(",", probe.States) + " " + probe.LastError); return false; }
            Console.WriteLine($"  joined as actor {probe.C.LocalPlayer.ActorNumber}. Start levels whenever you like; the bot answers 'ready'.");
            string scene = probe.C.CurrentRoom.CustomProperties["curScn"]?.ToString(), dumpScene = "";
            long lastSend = 0, lastReport = 0, lastEventAt = clock.ElapsedMilliseconds, maxGap = 0;
            int lastEventCount = 0, sent = 0, changes = 0;
            var views = new Dictionary<string, int>();
            while (clock.ElapsedMilliseconds < maxSeconds * 1000L)
            {
                probe.C.Service();
                Thread.Sleep(5);
                long now = clock.ElapsedMilliseconds;
                if (probe.C.State != ClientState.Joined) { Console.WriteLine($"  !! bot no longer joined: {probe.C.State} ({probe.C.DisconnectedCause})"); ok = false; break; }
                if (probe.Events.Count != lastEventCount) { maxGap = Math.Max(maxGap, now - lastEventAt); lastEventAt = now; lastEventCount = probe.Events.Count; }
                if (probe.Events.Any(x => x.e.Code == 199)) { Console.WriteLine("  !! kicked (event 199)"); ok = false; break; }
                string s = probe.C.CurrentRoom.CustomProperties["curScn"]?.ToString();
                if (s != scene) { Console.WriteLine($"  [{now / 1000.0:F1}s] scene '{scene}' -> '{s}'"); scene = s; changes++; }
                if (now - lastSend >= 1000)
                {
                    lastSend = now;
                    views.Clear();
                    try
                    {
                        foreach (var line in System.IO.File.ReadAllLines(viewsFile))
                        {
                            var kv = line.Split('=');
                            if (kv.Length != 2) continue;
                            if (kv[0] == "scene") { if (kv[1] != dumpScene) { dumpScene = kv[1]; Console.WriteLine($"  [{now / 1000.0:F1}s] host scene '{dumpScene}'"); } }
                            else if (int.TryParse(kv[1], out int id)) views[kv[0]] = id;
                        }
                    }
                    catch { }
                    foreach (var v in views)
                        if (ReadyRpcs.TryGetValue(v.Key, out var rpcs))
                            foreach (var name in rpcs)
                            {
                                var rpc = new ExitGames.Client.Photon.Hashtable { { (byte)0, v.Value }, { (byte)2, probe.C.LoadBalancingPeer.ServerTimeInMilliSeconds }, { (byte)3, name } };
                                probe.C.OpRaiseEvent(200, rpc, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
                                sent++;
                            }
                }
                if (now - lastReport > 5000)
                {
                    lastReport = now;
                    Report(probe, clock);
                    Console.WriteLine($"         views [{string.Join(" ", views.Select(v => v.Key + "=" + v.Value))}], ready RPCs sent {sent}, longest silence {maxGap} ms");
                }
            }
            Report(probe, clock);
            var byCode = probe.Events.GroupBy(x => x.e.Code).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            Console.WriteLine($"  total events: {probe.Events.Count} ({string.Join(" ", byCode)}), scene changes {changes}, longest silence {maxGap} ms");
            probe.C.OpLeaveRoom(false);
            Pump(new[] { probe }, () => probe.C.State != ClientState.Joined, 5000);
            probe.C.Disconnect();
            Pump(new[] { probe }, () => false, 500);
            Console.WriteLine($"  [{clock.ElapsedMilliseconds / 1000.0:F1}s] bot left");
        }
        finally
        {
            pm?.Dispose(); pg?.Dispose();
            if (pm != null) Console.WriteLine($"  proxies dropped {pm.Dropped + pg.Dropped} of {pm.Dropped + pg.Dropped + pm.Forwarded + pg.Forwarded}");
        }
        return ok;
    }

    private static void Report(Probe p, Stopwatch clock)
    {
        var peer = p.C.LoadBalancingPeer;
        var tin = peer.TrafficStatsIncoming;
        Console.WriteLine($"  [{clock.ElapsedMilliseconds / 1000.0:F1}s] events {p.Events.Count}, in: {tin.TotalPacketCount} pkts {tin.TotalPacketBytes / 1024} KB, reliable {tin.ReliableCommandCount}, fragments {tin.FragmentCommandCount}; RTT {peer.RoundTripTime}±{peer.RoundTripTimeVariance}, resent {peer.ResentReliableCommands}, players {p.C.CurrentRoom?.PlayerCount}");
    }

    public static bool Run(string room, double loss, string host, int masterPort)
    {
        Console.WriteLine($"== LIVE test: probe joins room '{room}' on {host}:{masterPort}, {loss:P0} extra loss each way");
        LossyProxy pm = null, pg = null;
        int port = masterPort;
        if (loss > 0)
        {
            pm = new LossyProxy(27777, masterPort, loss);
            pg = new LossyProxy(27778, masterPort + 1, loss);
            port = 27777;
            Probe.RewritePort = p => p == masterPort + 1 ? 27778 : p;
        }
        var clock = Stopwatch.StartNew();
        bool ok = true;
        var probe = new Probe(AppRealtime, "76561190000000001", host, port, clock);
        Probe voice = null;
        try
        {
            bool req = false;
            long t0 = clock.ElapsedMilliseconds;
            bool joined = Pump(new[] { probe }, () =>
            {
                if (!req && probe.C.State == ClientState.ConnectedToMasterServer) { req = true; probe.C.OpJoinRoom(new EnterRoomParams { RoomName = room }); }
                return probe.C.State == ClientState.Joined;
            }, 20000);
            ok &= Check($"connected + joined the game's room in {clock.ElapsedMilliseconds - t0} ms", joined);
            if (!joined) { Console.WriteLine("  states: " + string.Join(",", probe.States) + "  last error: " + probe.LastError); return false; }
            long tJoin = clock.ElapsedMilliseconds;

            var r = probe.C.CurrentRoom;
            Console.WriteLine($"  room: {r.PlayerCount} players, master {r.MasterClientId}, me actor {probe.C.LocalPlayer.ActorNumber}, open {r.IsOpen}, visible {r.IsVisible}, max {r.MaxPlayers}");
            foreach (var p in r.Players.Values) Console.WriteLine($"    actor {p.ActorNumber} '{p.NickName}' userId {p.UserId} props {p.CustomProperties.Count}");
            Console.WriteLine("  room props: " + string.Join(", ", r.CustomProperties.Keys.Select(k => k.ToString())));
            ok &= Check("master is the host (actor 1)", r.MasterClientId == 1);
            ok &= Check("host player visible", r.Players.ContainsKey(1));

            // collect cache replay + live traffic for a few seconds
            Pump(new[] { probe }, () => false, 4000);
            var byCode = probe.Events.GroupBy(x => x.e.Code).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            Console.WriteLine($"  events in 4 s after join: {probe.Events.Count} ({string.Join(" ", byCode)})");
            int replayed = probe.Events.Count(x => x.t - tJoin < 300 && x.e.Code != 255);
            Console.WriteLine($"  of them within 300 ms of join (≈cache replay): {replayed}");
            ok &= Check("no unexpected disconnect / kick", probe.C.State == ClientState.Joined && !probe.Events.Any(x => x.e.Code == 199));
            Console.WriteLine($"  RTT {probe.C.LoadBalancingPeer.RoundTripTime} ms (var {probe.C.LoadBalancingPeer.RoundTripTimeVariance}), resent {probe.C.LoadBalancingPeer.ResentReliableCommands}");

            // events to master: host should not crash; send a harmless custom event code not used by PUN
            probe.C.OpRaiseEvent(99, new object[] { "dh-probe" }, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

            // ---- voice room
            voice = new Probe(AppVoice, "76561190000000001", host, port, clock);
            bool vreq = false;
            bool vjoined = Pump(new[] { probe, voice }, () =>
            {
                if (!vreq && voice.C.State == ClientState.ConnectedToMasterServer) { vreq = true; voice.C.OpJoinRoom(new EnterRoomParams { RoomName = room + "_voice_" }); }
                return voice.C.State == ClientState.Joined;
            }, 20000);
            ok &= Check("voice client joined the voice room", vjoined);
            if (vjoined)
            {
                var vr = voice.C.CurrentRoom;
                Console.WriteLine($"  voice room: {vr.PlayerCount} players, master {vr.MasterClientId}");
                foreach (var p in vr.Players.Values) Console.WriteLine($"    voice actor {p.ActorNumber} props: {string.Join(", ", p.CustomProperties.Select(kv => kv.Key + "=" + kv.Value))}");
                ok &= Check("host voice actor present", vr.PlayerCount >= 2);
                Pump(new[] { probe, voice }, () => false, 3000);
                var vcodes = voice.Events.GroupBy(x => x.e.Code).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
                Console.WriteLine($"  voice events in 3 s: {voice.Events.Count} ({string.Join(" ", vcodes)})  (202 = voice info, 203 = frames; frames only while the host talks)");
            }

            // ---- leave cleanly
            voice.C.OpLeaveRoom(false);
            probe.C.OpLeaveRoom(false);
            Pump(new[] { probe, voice }, () => probe.C.State != ClientState.Joined && voice.C.State != ClientState.Joined, 5000);
            probe.C.Disconnect(); voice.C.Disconnect();
            Pump(new[] { probe, voice }, () => false, 500);
            ok &= Check("left cleanly", probe.C.State != ClientState.Joined);
        }
        finally
        {
            pm?.Dispose(); pg?.Dispose();
            if (pm != null) Console.WriteLine($"  proxies dropped {pm.Dropped + pg.Dropped} of {pm.Dropped + pg.Dropped + pm.Forwarded + pg.Forwarded}");
        }
        return ok;
    }
}
