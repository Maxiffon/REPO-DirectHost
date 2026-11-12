// End-to-end: real Photon Realtime clients (game's PhotonRealtime.dll) through master + game server of DirectHost,
// over lossy UDP on both ports. Mirrors the REPO flow: host JoinOrCreate, late joiner JoinRoom, buffered events,
// cache removal, properties, master leave.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using RepoDirectHost.Net;
using RepoDirectHost.Net.Enet;
using RepoDirectHost.Server;
using Hashtable = ExitGames.Client.Photon.Hashtable;

internal static class RoomTest
{
    private const string AppId = "11111111-2222-3333-4444-555555555555"; // the server does not check it;

    private sealed class Client
    {
        public readonly string Name;
        public readonly LoadBalancingClient C;
        public readonly List<EventData> Events = new List<EventData>();
        public readonly List<string> States = new List<string>();
        public Client(string name, int masterPort)
        {
            Name = name;
            C = new LoadBalancingClient(ConnectionProtocol.Udp) { AuthValues = new AuthenticationValues(name) };
            C.EventReceived += e =>
            {
                var copy = new EventData { Code = e.Code };
                foreach (var kv in e.Parameters) copy.Parameters.Add(kv.Key, kv.Value);
                Events.Add(copy);
            };
            C.StateChanged += (a, b) => States.Add(b.ToString());
            C.ConnectUsingSettings(new AppSettings { AppIdRealtime = AppId, Server = "127.0.0.1", Port = masterPort, UseNameServer = false, Protocol = ConnectionProtocol.Udp, AppVersion = "test", AuthMode = AuthModeOption.Auth });
        }
        public bool Joined => C.State == ClientState.Joined;
        public List<EventData> Got(byte code) => Events.Where(e => e.Code == code).ToList();
    }

    private static bool Pump(IEnumerable<Client> clients, Func<bool> until, int ms)
    {
        var sw = Stopwatch.StartNew();
        var list = clients.ToList();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var c in list) c.C.Service();
            if (until()) return true;
            Thread.Sleep(5);
        }
        return until();
    }

    private static bool Check(string what, bool ok) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); return ok; }

    public static bool Run(double loss)
    {
        Console.WriteLine($"== Room test: real Photon Realtime clients <-> DirectHost master/game, {loss:P0} loss each way");
        var serverLog = new ConcurrentQueue<string>();
        UdpServer udp = null;
        var photon = new PhotonServer(UdpServer.Now, (c, r) => udp.Hosts.First(h => h.Role == c.Role).Close(c, r, true))
        {
            GameServerAddress = "127.0.0.1:17778",
            Log = s => serverLog.Enqueue(s),
            Warn = s => serverLog.Enqueue("WARN " + s)
        };
        // Not 7777/7778: a running game holds those exclusively.
        udp = new UdpServer(photon, IPAddress.Loopback, (37777, Role.Master), (37778, Role.Game)) { Log = s => serverLog.Enqueue(s), OnError = e => serverLog.Enqueue("ERROR " + e) };
        udp.Tick = photon.Tick;
        udp.Start();
        using var pm = new LossyProxy(17777, 37777, loss);
        using var pg = new LossyProxy(17778, 37778, loss);
        bool ok = true;
        try
        {
            // ---- host: JoinOrCreate like REPO (private lobby, custom props, cleanup on leave, broadcast props)
            var host = new Client("76561198000000011", 17777);
            bool joinRequested = false;
            ok &= Check("host connected to master and joined room", Pump(new[] { host }, () =>
            {
                if (!joinRequested && host.C.State == ClientState.ConnectedToMasterServer)
                {
                    joinRequested = true;
                    host.C.OpJoinOrCreateRoom(new EnterRoomParams
                    {
                        RoomName = "109775240000000001",
                        Lobby = new TypedLobby("private", LobbyType.Default),
                        RoomOptions = new RoomOptions { MaxPlayers = 6, IsVisible = false, PublishUserId = false, CustomRoomProperties = new Hashtable { { "PASSWORD", "" }, { "PUNPersistentObjects", 1 } }, CustomRoomPropertiesForLobby = new[] { "server_name" } }
                    });
                }
                return host.Joined;
            }, 15000));
            if (!host.Joined) { Console.WriteLine("  host states: " + string.Join(",", host.States)); return false; }
            ok &= Check($"host is actor 1 and master (actor {host.C.LocalPlayer.ActorNumber}, master {host.C.CurrentRoom.MasterClientId})", host.C.LocalPlayer.ActorNumber == 1 && host.C.CurrentRoom.MasterClientId == 1);

            // ---- buffered events (like PUN AllBuffered RPCs / Instantiate)
            var buffered = new RaiseEventOptions { CachingOption = EventCaching.AddToRoomCache };
            host.C.OpRaiseEvent(200, new Hashtable { { (byte)0, 1001 }, { (byte)2, 123456 }, { (byte)3, "ModuleAmountRPC" } }, buffered, SendOptions.SendReliable);
            host.C.OpRaiseEvent(200, new Hashtable { { (byte)0, 1002 }, { (byte)2, 123457 }, { (byte)3, "ToggleMuteRPC" } }, buffered, SendOptions.SendReliable);
            var bigLevel = new byte[6000]; for (int i = 0; i < bigLevel.Length; i++) bigLevel[i] = (byte)(i * 7);
            host.C.OpRaiseEvent(202, new Hashtable { { (byte)0, "Level/Desert/Start Room" }, { (byte)7, 5000 }, { (byte)9, false }, { (byte)5, new object[] { bigLevel } } }, buffered, SendOptions.SendReliable);
            // RemoveBufferedRPCs(viewId 1002) — must remove only that one and must NOT be delivered as an event
            host.C.OpRaiseEvent(200, new Hashtable { { (byte)0, 1002 } }, new RaiseEventOptions { CachingOption = EventCaching.RemoveFromRoomCache }, SendOptions.SendReliable);
            host.C.CurrentRoom.SetCustomProperties(new Hashtable { { "curScn", "Lobby" } });
            Pump(new[] { host }, () => false, 400);

            // ---- late joiner: JoinRoom (like REPO clients)
            var guest = new Client("76561198000000012", 17777);
            bool guestJoinRequested = false;
            ok &= Check("late joiner connected and joined", Pump(new[] { host, guest }, () =>
            {
                if (!guestJoinRequested && guest.C.State == ClientState.ConnectedToMasterServer) { guestJoinRequested = true; guest.C.OpJoinRoom(new EnterRoomParams { RoomName = "109775240000000001" }); }
                return guest.Joined && guest.Got(200).Count >= 1 && guest.Got(202).Count >= 1;
            }, 15000));
            if (!guest.Joined) { Console.WriteLine("  joiner states: " + string.Join(",", guest.States)); return false; }
            ok &= Check($"joiner is actor 2, sees master 1, {guest.C.CurrentRoom.PlayerCount} players", guest.C.LocalPlayer.ActorNumber == 2 && guest.C.CurrentRoom.MasterClientId == 1 && guest.C.CurrentRoom.PlayerCount == 2);
            ok &= Check($"host sees joiner ({host.C.CurrentRoom.PlayerCount} players)", Pump(new[] { host, guest }, () => host.C.CurrentRoom.PlayerCount == 2, 5000));
            var rpcs = guest.Got(200);
            ok &= Check($"cached RPCs replayed: {rpcs.Count} (expected 1 — the removed one must be gone)", rpcs.Count == 1 && (rpcs[0].CustomData as Hashtable)?[(byte)0] is int v && v == 1001);
            ok &= Check("cached RPC keeps byte keys/int types and sender = 1", rpcs.Count == 1 && rpcs[0].Sender == 1 && (rpcs[0].CustomData as Hashtable)?[(byte)2] is int);
            var inst = guest.Got(202);
            bool bigOk = inst.Count == 1 && (inst[0].CustomData as Hashtable)?[(byte)5] is object[] arr && arr[0] is byte[] bb && bb.Length == 6000 && bb.Select((x, i) => x == (byte)(i * 7)).All(t => t);
            ok &= Check("cached 6 KB instantiate arrives intact (fragmented)", bigOk);
            ok &= Check($"room property seen by joiner (curScn={guest.C.CurrentRoom.CustomProperties["curScn"]})", "Lobby".Equals(guest.C.CurrentRoom.CustomProperties["curScn"]));

            // ---- live events and properties
            host.C.CurrentRoom.SetCustomProperties(new Hashtable { { "curScn", "Level - Desert" } });
            guest.C.LocalPlayer.SetCustomProperties(new Hashtable { { "color", 7 } });
            guest.C.OpRaiseEvent(200, new Hashtable { { (byte)0, 77 }, { (byte)2, 1 }, { (byte)3, "LevelLoadedRPC" } }, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
            ok &= Check("room property change reaches joiner", Pump(new[] { host, guest }, () => "Level - Desert".Equals(guest.C.CurrentRoom.CustomProperties["curScn"]), 5000));
            ok &= Check("actor property reaches host", Pump(new[] { host, guest }, () => host.C.CurrentRoom.GetPlayer(2)?.CustomProperties["color"] is int c && c == 7, 5000));
            ok &= Check("event to MasterClient arrives at host with sender 2", Pump(new[] { host, guest }, () => host.Got(200).Any(e => e.Sender == 2), 5000));

            // ---- host leaves: master must switch to 2
            host.C.Disconnect();
            // Photon sends its disconnect command once, without retries: on a lossy path it can be lost, and then the
            // server notices the leave only by its 20 s silence timeout.
            ok &= Check("after host leaves, joiner becomes master", Pump(new[] { host, guest }, () => guest.C.CurrentRoom?.MasterClientId == 2 && guest.C.CurrentRoom.PlayerCount == 1, loss > 0 ? 26000 : 8000));
            guest.C.Disconnect();
            Pump(new[] { host, guest }, () => false, 300);
        }
        finally
        {
            udp.Dispose();
            int shown = 0;
            while (serverLog.TryDequeue(out var l)) if (shown++ < 40 && (l.Contains("[room]") || l.Contains("WARN") || l.Contains("ERROR"))) Console.WriteLine("  server: " + l);
            Console.WriteLine($"  proxies dropped {pm.Dropped + pg.Dropped} of {pm.Dropped + pg.Dropped + pm.Forwarded + pg.Forwarded}; server routed {photon.EventsRouted} events, cache +{photon.CacheAdds}/-{photon.CacheRemoves}");
        }
        return ok;
    }
}
