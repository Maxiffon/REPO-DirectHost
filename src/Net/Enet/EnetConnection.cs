// REPO DirectHost — one client connection of the ENet-style transport.
// Single-threaded: every method must be called from the server thread.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RepoDirectHost.Net.Enet
{
    internal interface IEnetHandler
    {
        void OnConnected(EnetConnection c);
        /// <summary>mode: 0 reliable, 1 unreliable, 2 unsequenced, 3 reliable-unsequenced (DeliveryMode of the sender).</summary>
        void OnMessage(EnetConnection c, byte channel, byte[] data, byte mode);
        void OnDisconnected(EnetConnection c, string reason);
    }

    internal static class Mode { public const byte Reliable = 0, Unreliable = 1, Unsequenced = 2, ReliableUnsequenced = 3; }

    internal sealed class EnetConnection
    {
        public readonly string Key;
        public readonly ushort PeerId;
        public readonly int Challenge;
        public readonly int Mtu;
        public readonly byte ChannelCount;
        public bool Closed { get; internal set; }
        public int LastReceive { get; private set; }
        public object Tag;                                    // owned by the application layer
        public byte Role;                                     // which listening socket accepted it (e.g. master / game)
        internal Action<byte[]> Transmit;
        internal Action<EnetConnection> MarkDirty;

        public int Rtt => _srtt;
        public int RttVar => _rttvar;
        public int Unacked => _unacked.Count;
        public long DatagramsSent, CommandsResent, MessagesFragmented, DatagramsReceived;

        private readonly IEnetHandler _handler;
        private readonly byte[] _connectPayload;

        // ---- outgoing
        private readonly int[] _outRel = new int[256];
        private readonly int[] _outUnrel = new int[256];
        private int _unseqGroup;
        private readonly List<byte[]> _acks = new List<byte[]>();
        private readonly List<Outgoing> _queue = new List<Outgoing>();
        private readonly Dictionary<long, Pending> _unacked = new Dictionary<long, Pending>();
        private int _srtt = 150, _rttvar = 75;
        private bool _rttSampled;
        private int _lastRetransmitScan;

        // SentTimes: the datagram time of every send. A real ack echoes one of them (ENet acks carry the sent time of the
        // datagram that delivered the command). The Photon client can emit a stray ack for a sequence it never received:
        // its receive thread reads the command's fields for the ack AFTER handing the command to the main thread, which
        // may already have recycled the pooled object for an outgoing command of its own. Trusting such an ack would end
        // the resends of a command the client still misses — a permanent stall of that channel. So acks are only
        // accepted with a matching sent time.
        private sealed class Pending { public byte[] Cmd; public int LastSent, Sends; public bool Queued, Acked; public readonly List<int> SentTimes = new List<int>(2); }
        public long AcksRejected;
        private struct Outgoing { public byte[] Cmd; public Pending P; }

        // ---- incoming
        private sealed class InChan { public int Delivered; public int LastUnrel; public readonly SortedDictionary<int, InCmd> Hold = new SortedDictionary<int, InCmd>(); }
        private readonly InChan[] _in = new InChan[256];
        private readonly HashSet<int> _seenUnseqGroups = new HashSet<int>();
        private readonly Queue<int> _seenUnseqOrder = new Queue<int>();
        private readonly HashSet<long> _seenRelUnseq = new HashSet<long>();
        private readonly Queue<long> _seenRelUnseqOrder = new Queue<long>();
        private readonly Dictionary<long, List<InCmd>> _unseqFrags = new Dictionary<long, List<InCmd>>();

        internal EnetConnection(string key, ushort peerId, int challenge, InCmd connect, int now, IEnetHandler handler)
        {
            Key = key; PeerId = peerId; Challenge = challenge; _handler = handler; LastReceive = now;
            _connectPayload = connect.Data;
            int mtu = connect.Data != null && connect.Data.Length >= 4 ? (ushort)Be.I16(connect.Data, 2) : 1200;
            Mtu = Math.Max(576, Math.Min(1200, mtu));
            ChannelCount = connect.Data != null && connect.Data.Length >= 12 ? connect.Data[11] : (byte)2;
        }

        private static long K(byte ch, int seq) => ((long)ch << 32) | (uint)seq;

        /// <summary>Diagnostics: the oldest unacknowledged reliable commands ("ch/seq sends=N age=ms").</summary>
        internal string DebugUnacked(int now, int max = 4)
        {
            var parts = new List<string>();
            foreach (var kv in _unacked.OrderBy(k => k.Key).Take(max))
                parts.Add($"{kv.Key >> 32}/{(int)kv.Key} sends={kv.Value.Sends} age={now - kv.Value.LastSent} queued={kv.Value.Queued}");
            var inState = new List<string>();
            for (int ch = 0; ch < 256; ch++) if (_in[ch] != null) inState.Add($"in{ch}: delivered {_in[ch].Delivered}, held {_in[ch].Hold.Count}");
            return $"unacked {_unacked.Count} [{string.Join("; ", parts)}], stray acks rejected {AcksRejected}, out seq [{string.Join(",", Enumerable.Range(0, 4).Select(c => _outRel[c]))}], srtt {_srtt}, {string.Join(", ", inState)}";
        }
        private InChan In(byte ch) => _in[ch] ?? (_in[ch] = new InChan());
        private void Dirty() => MarkDirty?.Invoke(this);

        // ================================================================== sending

        internal void SendVerify()
        {
            int seq = ++_outRel[Cmd.ControlChannel];
            Track(EnetWire.Verify(PeerId, seq, _connectPayload), Cmd.ControlChannel, seq);
        }

        /// <summary>Reliable, ordered on its channel. Messages that do not fit one datagram are fragmented.</summary>
        public void SendReliable(byte ch, byte[] data)
        {
            if (Closed) return;
            int maxSingle = Mtu - EnetWire.DatagramHeader - EnetWire.CmdHeader;
            if (data.Length <= maxSingle)
            {
                int seq = ++_outRel[ch];
                Track(EnetWire.Reliable(ch, seq, data, 0, data.Length), ch, seq);
                return;
            }
            int chunk = Mtu - EnetWire.DatagramHeader - EnetWire.FragHeader;
            int count = (data.Length + chunk - 1) / chunk;
            int start = _outRel[ch] + 1;
            _outRel[ch] += count;
            for (int i = 0; i < count; i++)
            {
                int off = i * chunk, len = Math.Min(chunk, data.Length - off);
                Track(EnetWire.Fragment(ch, start + i, start, count, i, data.Length, off, data, len), ch, start + i);
            }
            MessagesFragmented++;
        }

        /// <summary>Unreliable but ordered relative to the channel's reliable stream (client drops it if it falls behind).</summary>
        public void SendUnreliable(byte ch, byte[] data)
        {
            if (Closed) return;
            if (data.Length > Mtu - EnetWire.DatagramHeader - 16) { SendReliable(ch, data); return; }
            Queue(EnetWire.Unreliable(ch, _outRel[ch], ++_outUnrel[ch], data));
        }

        /// <summary>Unreliable, unsequenced: delivered immediately, never waits for anything (voice).</summary>
        public void SendUnsequenced(byte ch, byte[] data)
        {
            if (Closed) return;
            if (data.Length > Mtu - EnetWire.DatagramHeader - 16) { SendReliable(ch, data); return; }
            Queue(EnetWire.Unsequenced(ch, ++_unseqGroup, data));
        }

        internal void SendDisconnect(byte reason)
        {
            var d = EnetWire.Disconnect(reason);
            var list = new List<byte[]> { d, d };
            try { Transmit?.Invoke(EnetWire.Datagram(PeerId, 0, Challenge, list, 0, 2, d.Length * 2)); } catch { }
        }

        private void Track(byte[] cmd, byte ch, int seq)
        {
            var p = new Pending { Cmd = cmd, Queued = true };
            _unacked[K(ch, seq)] = p;
            _queue.Add(new Outgoing { Cmd = cmd, P = p });
            Dirty();
        }

        private void Queue(byte[] cmd) { _queue.Add(new Outgoing { Cmd = cmd }); Dirty(); }

        /// <summary>Packs acks + queued commands into datagrams of at most MTU bytes.</summary>
        internal void Flush(int now)
        {
            if (_acks.Count == 0 && _queue.Count == 0) return;
            var all = new List<byte[]>(_acks.Count + _queue.Count);
            all.AddRange(_acks);
            _acks.Clear();
            foreach (var q in _queue)
            {
                if (q.P != null)
                {
                    q.P.Queued = false;
                    if (q.P.Acked) continue;
                    q.P.LastSent = now;
                    if (q.P.SentTimes.Count == 0 || q.P.SentTimes[q.P.SentTimes.Count - 1] != now) q.P.SentTimes.Add(now);
                    if (++q.P.Sends > 1) CommandsResent++;
                }
                all.Add(q.Cmd);
            }
            _queue.Clear();
            int i = 0;
            while (i < all.Count)
            {
                int n = 0, bytes = 0;
                while (i + n < all.Count && n < 255)
                {
                    int len = all[i + n].Length;
                    if (n > 0 && EnetWire.DatagramHeader + bytes + len > Mtu) break;
                    bytes += len; n++;
                }
                try { Transmit?.Invoke(EnetWire.Datagram(PeerId, now, Challenge, all, i, n, bytes)); } catch { }
                DatagramsSent++;
                i += n;
            }
        }

        /// <summary>Retransmits reliable commands whose ack is overdue (RTO from measured RTT, exponential backoff).</summary>
        internal void Tick(int now)
        {
            if (now - _lastRetransmitScan < 10 || _unacked.Count == 0) return;
            _lastRetransmitScan = now;
            int rto = Math.Max(40, _srtt + 4 * _rttvar);
            foreach (var p in _unacked.Values)
            {
                if (p.Queued || p.Acked || p.Sends == 0) continue;
                int wait = Math.Min(1000, rto << Math.Min(p.Sends - 1, 3));   // backoff x2 up to x8, never more than 1 s
                if (now - p.LastSent >= wait) { p.Queued = true; _queue.Add(new Outgoing { Cmd = p.Cmd, P = p }); }
            }
            if (_queue.Count > 0) Dirty();
        }

        // ================================================================== receiving

        internal void Process(List<InCmd> cmds, int sentTime, int now)
        {
            LastReceive = now;
            DatagramsReceived++;
            foreach (var c in cmds)
            {
                if (Closed) return;
                if (c.IsReliable && c.Type != Cmd.Ack && c.Type != Cmd.AckUnsequenced) { _acks.Add(EnetWire.Ack(c, sentTime)); Dirty(); }
                switch (c.Type)
                {
                    case Cmd.Ack: OnAck(c, now); break;
                    case Cmd.AckUnsequenced: break;
                    case Cmd.Disconnect: Closed = true; _handler.OnDisconnected(this, "client disconnected"); return;
                    case Cmd.Reliable:
                        if ((c.Flags & Cmd.FlagUnsequenced) != 0) ReliableUnsequenced(c); else Ordered(c);
                        break;
                    case Cmd.Fragment: Ordered(c); break;
                    case Cmd.ReliableUnsequenced: ReliableUnsequenced(c); break;
                    case Cmd.FragmentUnsequenced: FragmentUnsequenced(c); break;
                    case Cmd.Unreliable:
                        {
                            var ch = In(c.Channel);
                            if (c.UnreliableSeq <= ch.LastUnrel) break;
                            ch.LastUnrel = c.UnreliableSeq;
                            Deliver(c.Channel, c.Data, Mode.Unreliable);
                            break;
                        }
                    case Cmd.Unsequenced:
                        if (!Remember(_seenUnseqGroups, _seenUnseqOrder, c.UnreliableSeq)) break;
                        Deliver(c.Channel, c.Data, Mode.Unsequenced);
                        break;
                    // Connect (handled by host), Ping, ServerTime: ack only
                }
            }
        }

        private void OnAck(in InCmd c, int now)
        {
            if (!_unacked.TryGetValue(K(c.Channel, c.AckSeq), out var p)) return;
            if (!p.SentTimes.Contains(c.AckSentTime)) { AcksRejected++; return; }   // stray ack (see Pending)
            _unacked.Remove(K(c.Channel, c.AckSeq));
            p.Acked = true;
            int sample = now - c.AckSentTime;
            if (sample < 0 || sample > 10000) return;
            if (!_rttSampled) { _srtt = Math.Max(1, sample); _rttvar = Math.Max(1, sample / 2); _rttSampled = true; }
            else { int err = sample - _srtt; _srtt = Math.Max(1, _srtt + err / 8); _rttvar = Math.Max(1, _rttvar + (Math.Abs(err) - _rttvar) / 4); }
        }

        private void Ordered(in InCmd c)
        {
            var ch = In(c.Channel);
            if (c.ReliableSeq <= ch.Delivered) return;                // duplicate (already acked again above)
            if (ch.Hold.Count > 16384) return;                       // runaway — ignore, client will resend
            ch.Hold[c.ReliableSeq] = c;
            while (true)
            {
                int next = ch.Delivered + 1;
                if (!ch.Hold.TryGetValue(next, out var h)) return;
                if (h.Type == Cmd.Fragment)
                {
                    if (h.Start != next || h.FragCount <= 0 || h.FragCount > 65536 || h.TotalLen <= 0 || h.TotalLen > 16 * 1024 * 1024)
                    { ch.Hold.Remove(next); ch.Delivered = next; continue; }   // malformed: skip the seq
                    for (int i = 1; i < h.FragCount; i++) if (!ch.Hold.ContainsKey(next + i)) return;   // wait for the rest
                    var buf = new byte[h.TotalLen];
                    for (int i = 0; i < h.FragCount; i++)
                    {
                        var f = ch.Hold[next + i];
                        ch.Hold.Remove(next + i);
                        if (f.Data != null && f.FragOffset >= 0 && f.FragOffset + f.Data.Length <= buf.Length)
                            Buffer.BlockCopy(f.Data, 0, buf, f.FragOffset, f.Data.Length);
                    }
                    ch.Delivered = next + h.FragCount - 1;
                    Deliver(h.Channel, buf, Mode.Reliable);
                }
                else
                {
                    ch.Hold.Remove(next);
                    ch.Delivered = next;
                    Deliver(h.Channel, h.Data, Mode.Reliable);
                }
                if (Closed) return;
            }
        }

        private void ReliableUnsequenced(in InCmd c)
        {
            if (!Remember(_seenRelUnseq, _seenRelUnseqOrder, K(c.Channel, c.ReliableSeq))) return;
            Deliver(c.Channel, c.Data, Mode.ReliableUnsequenced);
        }

        private void FragmentUnsequenced(in InCmd c)
        {
            if (!Remember(_seenRelUnseq, _seenRelUnseqOrder, K(c.Channel, c.ReliableSeq))) return;
            long key = K(c.Channel, c.Start);
            if (!_unseqFrags.TryGetValue(key, out var list)) _unseqFrags[key] = list = new List<InCmd>();
            list.Add(c);
            if (list.Count < c.FragCount) return;
            _unseqFrags.Remove(key);
            var buf = new byte[Math.Max(0, c.TotalLen)];
            foreach (var f in list)
                if (f.Data != null && f.FragOffset >= 0 && f.FragOffset + f.Data.Length <= buf.Length)
                    Buffer.BlockCopy(f.Data, 0, buf, f.FragOffset, f.Data.Length);
            Deliver(c.Channel, buf, Mode.ReliableUnsequenced);
        }

        private static bool Remember<T>(HashSet<T> set, Queue<T> order, T v)
        {
            if (!set.Add(v)) return false;
            order.Enqueue(v);
            if (order.Count > 4096) set.Remove(order.Dequeue());
            return true;
        }

        private void Deliver(byte ch, byte[] data, byte mode)
        {
            if (data == null || data.Length == 0) return;
            _handler.OnMessage(this, ch, data, mode);
        }

        /// <summary>Sends with the given delivery mode (see IEnetHandler.OnMessage).</summary>
        public void Send(byte ch, byte[] data, byte mode)
        {
            switch (mode)
            {
                case Mode.Unreliable: SendUnreliable(ch, data); break;
                case Mode.Unsequenced: SendUnsequenced(ch, data); break;
                default: SendReliable(ch, data); break;
            }
        }
    }
}
