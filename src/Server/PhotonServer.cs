// REPO DirectHost — the in-game Photon server (master + game + voice apps) on top of our ENet transport.
// Behaviour follows what the Photon client (PhotonRealtime / PUN / Voice) expects; see docs/photon-protocol.md.
using System;
using System.Collections.Generic;
using System.Linq;
using RepoDirectHost.Net;
using RepoDirectHost.Net.Enet;
using RepoDirectHost.Net.Photon;

namespace RepoDirectHost.Server
{
    internal static class Role { public const byte Master = 0, Game = 1; }

    internal sealed class PhotonServer : IEnetHandler
    {
        // ---- configuration
        /// <summary>Game-server address returned by the master. "@" as host means "the host you used for the master".</summary>
        public string GameServerAddress = "@:7778";
        /// <summary>First 32 characters of the Voice AppId (Init carries only 32 chars).</summary>
        public string VoiceAppIdPrefix = "dfc40a01-c3a4-4395-9707-b7111881";
        public Action<string> Log;
        public Action<string> Warn;
        public int MaxCachedEvents = 20000;

        // ---- state (server thread only)
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly List<(int at, Action a)> _timers = new List<(int, Action)>();
        private readonly Func<int> _now;
        private readonly Action<EnetConnection, string> _close;

        public int RoomCount => _rooms.Count;
        public IEnumerable<Room> Rooms => _rooms.Values;
        public long EventsRouted, CacheAdds, CacheRemoves;

        public PhotonServer(Func<int> now, Action<EnetConnection, string> close) { _now = now; _close = close; }

        internal sealed class Peer
        {
            public EnetConnection C;
            public bool Voice;
            public DhCrypto Dh;
            public string UserId, AppVersion;
            public Room Room;
            public Actor Actor;
            public string Name => $"{(Voice ? "voice" : "game")}#{C.PeerId}{(UserId != null ? " " + UserId : "")}";
        }

        internal sealed class Actor
        {
            public int Nr; public Peer Peer; public string UserId;
            public readonly PropTable Props = new PropTable();
        }

        internal sealed class CachedEvent
        {
            public int ActorNr; public byte Code; public byte[] Data;          // Data: raw typed value of param 245 (or null)
            public Dictionary<string, string> Filter;                           // decoded Data hashtable: keyId -> valueId
        }

        internal sealed class Room
        {
            public string Name; public bool Voice; public string Key;
            public readonly SortedDictionary<int, Actor> Actors = new SortedDictionary<int, Actor>();
            public readonly PropTable Props = new PropTable();
            public readonly List<CachedEvent> Cache = new List<CachedEvent>();
            public readonly HashSet<string> Banned = new HashSet<string>();
            public int NextActorNr = 1, MasterClientId, Flags = 0x23, MaxPlayers;
            public bool IsOpen = true, IsVisible = true, CleanupCacheOnLeave = true;
            public string Lobby;
            public int[] ActorList => Actors.Keys.ToArray();
        }

        private static string RoomKey(bool voice, string name) => (voice ? "v:" : "g:") + name;

        // ================================================================== transport callbacks

        public void OnConnected(EnetConnection c) { c.Tag = new Peer { C = c }; }

        public void OnDisconnected(EnetConnection c, string reason)
        {
            if (c.Tag is Peer p && p.Room != null) LeaveRoom(p, "disconnected: " + reason);
        }

        public void OnMessage(EnetConnection c, byte ch, byte[] data, byte mode)
        {
            if (!(c.Tag is Peer p) || data.Length < 2 || (data[0] != 0xF3 && data[0] != 0xFD)) return;
            byte type = (byte)(data[1] & 0x7F);
            bool enc = (data[1] & Msg.EncryptedFlag) != 0;
            try
            {
                switch (type)
                {
                    case Msg.Init:
                        {
                            string appId = data.Length >= 41 ? System.Text.Encoding.ASCII.GetString(data, 9, 32).TrimEnd('\0') : "";
                            p.Voice = appId == VoiceAppIdPrefix;
                            c.SendReliable(0, new byte[] { 0xF3, Msg.InitResponse });
                            return;
                        }
                    case Msg.InternalOperation:
                    case Msg.Operation:
                        {
                            byte[] body;
                            if (enc)
                            {
                                if (p.Dh == null || !p.Dh.Ready) { Warn?.Invoke($"[photon] {p.Name}: encrypted message before key exchange"); return; }
                                body = p.Dh.Decrypt(data, 2, data.Length - 2);
                            }
                            else { body = new byte[data.Length - 2]; Buffer.BlockCopy(data, 2, body, 0, body.Length); }
                            var req = OpRequest.Parse(body, type == Msg.InternalOperation, enc);
                            if (req == null) return;
                            if (req.Internal) HandleInternal(p, req, ch);
                            else HandleOperation(p, req, ch, mode);
                            return;
                        }
                }
            }
            catch (Exception e) { Warn?.Invoke($"[photon] {p.Name}: failed to handle message type {type}: {e.Message}"); }
        }

        /// <summary>Delayed actions (server thread). Call from UdpServer.Tick.</summary>
        public void Tick()
        {
            if (_timers.Count == 0) return;
            int now = _now();
            for (int i = _timers.Count - 1; i >= 0; i--)
                if (now - _timers[i].at >= 0) { var a = _timers[i].a; _timers.RemoveAt(i); try { a(); } catch { } }
        }

        // ================================================================== operations

        private void Send(Peer p, byte[] msg, byte ch = 0, byte mode = Mode.Reliable) => p.C.Send(ch, msg, mode);
        private void Respond(Peer p, byte op, short rc = 0, string debug = null) => Send(p, MsgBuilder.Response(op, rc, debug).Build());

        private void HandleInternal(Peer p, OpRequest req, byte ch)
        {
            if (req.Code != 0) { Send(p, MsgBuilder.Response(req.Code, -2, "unknown internal operation", isInternal: true).Build(), ch); return; }
            var clientKey = req.Simple(1) as byte[];
            if (clientKey == null || clientKey.Length == 0) { Send(p, MsgBuilder.Response(0, -1, "no client key", isInternal: true).Build(), ch); return; }
            p.Dh = new DhCrypto();
            p.Dh.Derive(clientKey);
            Send(p, MsgBuilder.Response(0, 0, null, isInternal: true).ParamBytes(1, p.Dh.PublicKey).Build(), ch);
        }

        private void HandleOperation(Peer p, OpRequest req, byte ch, byte mode)
        {
            bool game = p.C.Role == Role.Game;
            switch (req.Code)
            {
                case 230: case 231: Authenticate(p, req); break;
                case 229: case 228: case 218: case 248: Respond(p, req.Code); break;          // lobby / settings / interest groups: accept
                case 225: case 226: case 227:
                    if (game) GameJoin(p, req); else MasterJoin(p, req);
                    break;
                case 253: RaiseEvent(p, req, ch, mode); break;
                case 252: SetProperties(p, req); break;
                case 254: Respond(p, 254); if (p.Room != null) LeaveRoom(p, "left"); break;
                default: Respond(p, req.Code, -2, "Operation not supported by DirectHost: " + req.Code); break;
            }
        }

        private void Authenticate(Peer p, OpRequest req)
        {
            string user = req.Str(225);
            if (user == null && req.Simple(221) is string token && token.StartsWith("dh:")) user = token.Substring(3);
            p.UserId = user ?? ("anon" + p.C.PeerId);
            p.AppVersion = req.Str(220) ?? p.AppVersion;
            var m = MsgBuilder.Response(req.Code).ParamStr(221, "dh:" + p.UserId).ParamStr(225, p.UserId);
            Send(p, m.Build());
        }

        // ---- master: tell the client where the game server is (rooms live on the game server)
        private void MasterJoin(Peer p, OpRequest req)
        {
            string name = req.Str(255);
            byte joinMode = req.Byte(215) ?? 0;
            _rooms.TryGetValue(RoomKey(p.Voice, name ?? ""), out var room);
            short rc = 0; string err = null;
            if (req.Code == 227)
            {
                if (room != null && room.Actors.Count > 0) { rc = 32766; err = "A game with the specified id already exist."; }
                name ??= Guid.NewGuid().ToString("N");
            }
            else if (req.Code == 226)
            {
                if (room == null && joinMode != 1) { rc = 32758; err = "Game does not exist"; }
                else if (room != null) (rc, err) = CheckCanJoin(room, p.UserId);
            }
            else // 225 random
            {
                var pick = _rooms.Values.FirstOrDefault(r => r.Voice == p.Voice && r.IsOpen && r.IsVisible && CheckCanJoin(r, p.UserId).rc == 0);
                if (pick != null) name = pick.Name;
                else if (joinMode == 1) name ??= Guid.NewGuid().ToString("N");
                else { rc = 32760; err = "No match found"; }
            }
            if (rc != 0) { Respond(p, req.Code, rc, err); return; }
            Send(p, MsgBuilder.Response(req.Code).ParamStr(230, GameServerAddress).ParamStr(255, name).Build());
        }

        private static (short rc, string err) CheckCanJoin(Room room, string userId)
        {
            if (userId != null && room.Banned.Contains(userId)) return (32747, "UserId found in excluded list");
            if (!room.IsOpen) return (32764, "Game closed");
            int active = room.Actors.Values.Count(a => a.UserId != userId);
            if (room.MaxPlayers > 0 && active >= room.MaxPlayers) return (32765, "Game full");
            return (0, null);
        }

        // ---- game: create/join the room
        private void GameJoin(Peer p, OpRequest req)
        {
            if (p.Room != null) { Respond(p, req.Code, -3, "already in a room"); return; }
            string name = req.Str(255);
            byte joinMode = req.Byte(215) ?? 0;
            if (string.IsNullOrEmpty(name)) { Respond(p, req.Code, -2, "room name required"); return; }
            string key = RoomKey(p.Voice, name);
            _rooms.TryGetValue(key, out var room);

            if (room != null && room.Actors.Count == 0) { _rooms.Remove(key); room = null; }   // stale empty room
            bool create;
            if (req.Code == 227) { if (room != null) { Respond(p, 227, 32766, "A game with the specified id already exist."); return; } create = true; }
            else { create = room == null; if (create && joinMode != 1) { Respond(p, req.Code, 32758, "Game does not exist"); return; } }

            if (create)
            {
                room = new Room { Name = name, Voice = p.Voice, Key = key, Lobby = req.Str(213) };
                var gp = req.Hashtable(248);
                room.Props.Merge(req.Body, gp, deleteNulls: true);
                room.Flags = req.Int(191) ?? 0x23;
                room.CleanupCacheOnLeave = req.Bool(241) ?? ((room.Flags & 2) != 0);
                if (room.Props.GetSimple("b255") is byte mp) room.MaxPlayers = mp;
                if (room.Props.GetSimple("b243") is int mpi) room.MaxPlayers = mpi;
                if (room.Props.GetSimple("b253") is bool open) room.IsOpen = open;
                if (room.Props.GetSimple("b254") is bool vis) room.IsVisible = vis;
                room.Props.SetByte(249, w => w.Bool(room.CleanupCacheOnLeave));
                _rooms[key] = room;
                Log?.Invoke($"[room] created {(room.Voice ? "voice " : "")}room '{name}' by {p.Name} (max {room.MaxPlayers}, flags 0x{room.Flags:X})");
            }
            else
            {
                var (rc, err) = CheckCanJoin(room, p.UserId);
                if (rc != 0) { Respond(p, req.Code, rc, err); return; }
                // same user still listed (stale connection that has not timed out yet): drop the old actor first
                foreach (var old in room.Actors.Values.Where(a => a.UserId != null && a.UserId == p.UserId).ToList())
                {
                    Log?.Invoke($"[room] '{name}': {p.UserId} rejoined — removing stale actor {old.Nr}");
                    LeaveRoom(old.Peer, "replaced by a new connection of the same user");
                    _close(old.Peer.C, "replaced by a new connection");
                }
            }

            var actor = new Actor { Nr = room.NextActorNr++, Peer = p, UserId = p.UserId };
            actor.Props.Merge(req.Body, req.Hashtable(249), deleteNulls: true);
            room.Actors[actor.Nr] = actor;
            p.Room = room; p.Actor = actor;
            if (room.MasterClientId == 0 || !room.Actors.ContainsKey(room.MasterClientId)) room.MasterClientId = actor.Nr;
            room.Props.SetByte(248, w => w.Int(room.MasterClientId));
            room.Props.SetByte(253, w => w.Bool(room.IsOpen));
            room.Props.SetByte(254, w => w.Bool(room.IsVisible));

            // 1) join response
            var resp = MsgBuilder.Response(req.Code).ParamInt(254, actor.Nr);
            var players = resp.Param(249).HashtableHeader(room.Actors.Count);
            foreach (var a in room.Actors.Values) { players.Int(a.Nr); a.Props.WriteHashtable(players); }
            room.Props.WriteHashtable(resp.Param(248));
            resp.ParamIntArray(252, room.ActorList).ParamInt(191, room.Flags);
            Send(p, resp.Build());

            // 2) Join event to everyone, including the newcomer
            var join = MsgBuilder.Event(255).ParamInt(254, actor.Nr);
            actor.Props.WriteHashtable(join.Param(249));
            join.ParamIntArray(252, room.ActorList);
            var joinBytes = join.Build();
            foreach (var a in room.Actors.Values) Send(a.Peer, joinBytes);

            // 3) room cache, in order, with the original sender
            foreach (var ev in room.Cache) Send(p, BuildEvent(ev.Code, ev.Data, ev.ActorNr));

            Log?.Invoke($"[room] '{name}': {p.Name} joined as actor {actor.Nr} ({room.Actors.Count} in room, master {room.MasterClientId}, {room.Cache.Count} cached events replayed)");
        }

        private static byte[] BuildEvent(byte code, byte[] rawData, int sender)
        {
            var m = MsgBuilder.Event(code);
            if (rawData != null) m.ParamRaw(245, rawData);
            m.ParamInt(254, sender);
            return m.Build();
        }

        // ---- RaiseEvent: routing + room cache
        private void RaiseEvent(Peer p, OpRequest req, byte ch, byte mode)
        {
            var room = p.Room;
            if (room == null) return;
            byte code = req.Byte(244) ?? 0;
            byte cache = req.Byte(247) ?? 0;
            int[] targets = req.IntArray(252);

            if (cache == 6) { RemoveFromCache(room, code, targets, req); return; }
            if (cache == 7) { CacheRemoves += room.Cache.RemoveAll(e => e.ActorNr != 0 && !room.Actors.ContainsKey(e.ActorNr)); return; }
            if (cache >= 10 || (cache >= 1 && cache <= 3)) return;                            // slices / obsolete actor cache: not used by PUN/REPO

            if (!room.Voice && (code == 123 || code == 124) && p.Actor.Nr == room.MasterClientId)
            {
                int target = req.Simple(245) is object[] arr && arr.Length > 0 && arr[0] is int t ? t : 0;
                Kick(room, target, ban: code == 124);
                return;
            }

            byte[] data = req.RawValue(245);
            var msg = BuildEvent(code, data, p.Actor.Nr);
            IEnumerable<Actor> to;
            if (targets != null) to = targets.Where(room.Actors.ContainsKey).Distinct().Select(n => room.Actors[n]);
            else
            {
                byte receivers = req.Byte(246) ?? 0;
                if (receivers == 1) to = room.Actors.Values;
                else if (receivers == 2) to = room.Actors.TryGetValue(room.MasterClientId, out var m) ? new[] { m } : Array.Empty<Actor>();
                else to = room.Actors.Values.Where(a => a != p.Actor);
            }
            foreach (var a in to.ToList()) { Send(a.Peer, msg, ch, mode); EventsRouted++; }

            if (cache == 4 || cache == 5)
            {
                if (room.Cache.Count >= MaxCachedEvents) { Warn?.Invoke($"[room] '{room.Name}': cache full ({room.Cache.Count}), dropping oldest"); room.Cache.RemoveAt(0); }
                room.Cache.Add(new CachedEvent { ActorNr = cache == 5 ? 0 : p.Actor.Nr, Code = code, Data = data, Filter = DecodeFilter(data) });
                CacheAdds++;
            }
        }

        private static Dictionary<string, string> DecodeFilter(byte[] data)
        {
            if (data == null || data.Length == 0 || data[0] != P18.Hashtable) return null;
            try
            {
                int p = 0;
                var entries = P18.ReadHashtableEntries(data, ref p);
                var d = new Dictionary<string, string>();
                foreach (var e in entries) d[P18.KeyId(data, e.KeyStart, e.KeyEnd)] = P18.KeyId(data, e.ValStart, e.ValEnd);
                return d;
            }
            catch { return null; }
        }

        private void RemoveFromCache(Room room, byte code, int[] authors, OpRequest req)
        {
            Dictionary<string, string> filter = null;
            var raw = req.RawValue(245);
            if (raw != null && raw.Length > 0 && raw[0] == P18.Hashtable) filter = DecodeFilter(raw);
            int removed = room.Cache.RemoveAll(e =>
            {
                if (code != 0 && e.Code != code) return false;
                if (authors != null && !authors.Contains(e.ActorNr)) return false;
                if (filter != null)
                {
                    if (e.Filter == null) return false;
                    foreach (var kv in filter) if (!e.Filter.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
                }
                return true;
            });
            CacheRemoves += removed;
        }

        // ---- properties
        private void SetProperties(Peer p, OpRequest req)
        {
            var room = p.Room;
            if (room == null) { Respond(p, 252, -3, "not in a room"); return; }
            int target = req.Int(254) ?? 0;
            var props = req.Hashtable(251);
            var expected = req.Hashtable(231);
            PropTable table;
            if (target > 0) { if (!room.Actors.TryGetValue(target, out var a)) { Respond(p, 252, -2, "actor not found"); return; } table = a.Props; }
            else table = room.Props;
            if (!table.Matches(req.Body, expected)) { Respond(p, 252, -2, "CAS update failed: property values did not match"); return; }
            table.Merge(req.Body, props, deleteNulls: (room.Flags & 0x10) != 0);

            if (target == 0)
            {
                if (room.Props.GetSimple("b248") is int newMaster && newMaster != room.MasterClientId && room.Actors.ContainsKey(newMaster))
                { Log?.Invoke($"[room] '{room.Name}': master switched {room.MasterClientId} -> {newMaster} by actor {p.Actor.Nr}"); room.MasterClientId = newMaster; }
                if (room.Props.GetSimple("b253") is bool open) room.IsOpen = open;
                if (room.Props.GetSimple("b254") is bool vis) room.IsVisible = vis;
                if (room.Props.GetSimple("b255") is byte mp) room.MaxPlayers = mp;
                if (room.Props.GetSimple("b243") is int mpi) room.MaxPlayers = mpi;
            }
            Respond(p, 252);

            var ev = MsgBuilder.Event(253).ParamInt(253, target);
            var raw = req.RawValue(251);
            if (raw != null) ev.ParamRaw(251, raw);
            ev.ParamInt(254, p.Actor.Nr);
            var bytes = ev.Build();
            bool toAll = (room.Flags & 0x20) != 0 || expected != null;
            foreach (var a in room.Actors.Values.ToList()) if (toAll || a != p.Actor) Send(a.Peer, bytes);
        }

        // ---- leaving
        private void LeaveRoom(Peer p, string reason)
        {
            var room = p.Room; var actor = p.Actor;
            p.Room = null; p.Actor = null;
            if (room == null || actor == null || !room.Actors.Remove(actor.Nr)) return;
            if (room.CleanupCacheOnLeave) CacheRemoves += room.Cache.RemoveAll(e => e.ActorNr == actor.Nr);

            bool masterChanged = false;
            if (room.MasterClientId == actor.Nr)
            {
                room.MasterClientId = room.Actors.Count > 0 ? room.Actors.Keys.First() : 0;
                masterChanged = room.MasterClientId != 0;
                room.Props.SetByte(248, w => w.Int(room.MasterClientId));
            }
            var ev = MsgBuilder.Event(254).ParamInt(254, actor.Nr);
            if (masterChanged) ev.ParamInt(203, room.MasterClientId);
            var bytes = ev.Build();
            foreach (var a in room.Actors.Values) Send(a.Peer, bytes);
            Log?.Invoke($"[room] '{room.Name}': actor {actor.Nr} ({p.UserId}) left — {reason}; {room.Actors.Count} remain{(masterChanged ? $", new master {room.MasterClientId}" : "")}");
            if (room.Actors.Count == 0) { _rooms.Remove(room.Key); Log?.Invoke($"[room] '{room.Name}' closed (empty)"); }
        }

        private void Kick(Room room, int targetNr, bool ban)
        {
            if (!room.Actors.TryGetValue(targetNr, out var target)) return;
            if (ban && target.UserId != null) room.Banned.Add(target.UserId);
            Log?.Invoke($"[room] '{room.Name}': master {(ban ? "banned" : "kicked")} actor {targetNr} ({target.UserId})");
            var peer = target.Peer;
            Send(peer, MsgBuilder.Event(199).Build());
            Send(peer, MsgBuilder.DisconnectMessage(0, ban ? "banned" : "kicked").Build());
            LeaveRoom(peer, ban ? "banned by host" : "kicked by host");
            _timers.Add((_now() + 500, () => _close(peer.C, ban ? "banned" : "kicked")));
        }
    }
}
