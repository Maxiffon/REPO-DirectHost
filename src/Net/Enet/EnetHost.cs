// REPO DirectHost — accepts ENet-style connections from Photon clients over any datagram transport.
using System;
using System.Collections.Generic;

namespace RepoDirectHost.Net.Enet
{
    internal sealed class EnetHost
    {
        public int DisconnectTimeoutMs = 20000;
        public byte Role;                                    // copied to every accepted connection
        public Action<string> Log;

        private readonly IEnetHandler _handler;
        private readonly Func<int> _now;
        private readonly Dictionary<string, EnetConnection> _byKey = new Dictionary<string, EnetConnection>();
        private readonly List<InCmd> _scratch = new List<InCmd>();
        private readonly HashSet<EnetConnection> _dirty = new HashSet<EnetConnection>();
        private readonly List<EnetConnection> _tmp = new List<EnetConnection>();
        private readonly Action<EnetConnection> _markDirty;
        private ushort _nextPeerId = 1;

        public EnetHost(IEnetHandler handler, Func<int> now)
        {
            _handler = handler; _now = now;
            _markDirty = c => _dirty.Add(c);
        }

        public int Now => _now();
        public IEnumerable<EnetConnection> Connections => _byKey.Values;
        public int Count => _byKey.Count;

        /// <summary>Feed one received datagram. key identifies the remote endpoint; send transmits back to it.</summary>
        public void OnDatagram(string key, byte[] buf, int len, Action<byte[]> send)
        {
            if (!EnetWire.TryParse(buf, len, out _, out int sentTime, out int challenge, _scratch)) return;
            int now = _now();
            _byKey.TryGetValue(key, out var conn);

            for (int i = 0; i < _scratch.Count; i++)
            {
                var c = _scratch[i];
                if (c.Type != Cmd.Connect) continue;
                if (conn != null && !conn.Closed && conn.Challenge == challenge)
                    continue;                                       // duplicate connect: ack below, verify is being retransmitted
                if (conn != null) Close(conn, "new connection from the same address", notifyClient: false);
                conn = new EnetConnection(key, NextPeerId(), challenge, c, now, _handler) { Transmit = send, MarkDirty = _markDirty, Role = Role };
                _byKey[key] = conn;
                conn.SendVerify();
                Log?.Invoke($"[enet] {key}: connect (challenge {challenge:X8}, mtu {conn.Mtu}, channels {conn.ChannelCount}) -> peer {conn.PeerId}");
                _handler.OnConnected(conn);
            }

            if (conn == null || conn.Closed || conn.Challenge != challenge) return;
            conn.Transmit = send;
            conn.Process(_scratch, sentTime, now);
            if (conn.Closed) { _byKey.Remove(key); Log?.Invoke($"[enet] {key}: disconnected by client"); }
            FlushDirty(now);
        }

        /// <summary>Periodic work: retransmits, timeouts, flushing. Call every few milliseconds.</summary>
        public void Tick()
        {
            int now = _now();
            _tmp.Clear();
            _tmp.AddRange(_byKey.Values);
            foreach (var c in _tmp)
            {
                if (c.Closed) { _byKey.Remove(c.Key); continue; }
                if (now - c.LastReceive > DisconnectTimeoutMs) { Close(c, $"timeout ({(now - c.LastReceive) / 1000} s silent)", notifyClient: true); continue; }
                c.Tick(now);
            }
            FlushDirty(now);
        }

        public void Close(EnetConnection c, string reason, bool notifyClient)
        {
            if (c.Closed) { _byKey.Remove(c.Key); return; }
            if (notifyClient) c.SendDisconnect(2);
            c.Closed = true;
            _byKey.Remove(c.Key);
            Log?.Invoke($"[enet] {c.Key}: closed — {reason}");
            _handler.OnDisconnected(c, reason);
        }

        public void FlushDirty(int now)
        {
            if (_dirty.Count == 0) return;
            _tmp.Clear();
            _tmp.AddRange(_dirty);
            _dirty.Clear();
            foreach (var c in _tmp) if (!c.Closed) c.Flush(now);
        }

        private ushort NextPeerId()
        {
            ushort id = _nextPeerId++;
            if (_nextPeerId >= 0x7FFF) _nextPeerId = 1;
            return id;
        }
    }
}
