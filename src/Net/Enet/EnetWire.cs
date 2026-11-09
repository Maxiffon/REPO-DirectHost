// REPO DirectHost — own implementation of the ENet-style transport used by Photon clients.
// Written from the client's wire behaviour (Photon3Unity3D), not from any other server.
using System;
using System.Collections.Generic;

namespace RepoDirectHost.Net.Enet
{
    internal static class Be
    {
        public static int I32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        public static short I16(byte[] b, int o) => (short)((b[o] << 8) | b[o + 1]);
        public static void W32(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        public static void W16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    }

    internal static class Cmd
    {
        public const byte Ack = 1, Connect = 2, VerifyConnect = 3, Disconnect = 4, Ping = 5, Reliable = 6, Unreliable = 7,
            Fragment = 8, Unsequenced = 11, ServerTime = 12, ReliableUnsequenced = 14, FragmentUnsequenced = 15, AckUnsequenced = 16;
        public const byte FlagReliable = 1, FlagUnsequenced = 2;
        public const byte ControlChannel = 0xFF;
    }

    /// <summary>One parsed incoming command. Data is a private copy of the command payload.</summary>
    internal struct InCmd
    {
        public byte Type, Channel, Flags, Reserved;
        public int Size, ReliableSeq, UnreliableSeq;              // UnreliableSeq = unreliable# (7) or unsequenced group (11)
        public int Start, FragCount, FragNum, TotalLen, FragOffset; // fragments (8, 15)
        public int AckSeq, AckSentTime;                            // acks (1, 16)
        public byte[] Data;
        public bool IsReliable => (Flags & Cmd.FlagReliable) != 0;
    }

    internal static class EnetWire
    {
        public const int DatagramHeader = 12, CmdHeader = 12, FragHeader = 32;

        /// <summary>Datagram header (client → server): peerId(2) flags(1) count(1) sentTime(4) challenge(4).</summary>
        public static bool TryParse(byte[] buf, int len, out byte flags, out int sentTime, out int challenge, List<InCmd> cmds)
        {
            flags = 0; sentTime = 0; challenge = 0;
            cmds.Clear();
            if (buf == null || len < DatagramHeader) return false;
            flags = buf[2];
            int count = buf[3];
            sentTime = Be.I32(buf, 4);
            challenge = Be.I32(buf, 8);
            if (flags == 1) return false;                 // datagram encryption — not supported
            int o = DatagramHeader;
            if (flags == 204) o += 4;                     // CRC appended after header (not verified)
            for (int i = 0; i < count; i++)
            {
                if (o + CmdHeader > len) return false;
                var c = new InCmd
                {
                    Type = buf[o], Channel = buf[o + 1], Flags = buf[o + 2], Reserved = buf[o + 3],
                    Size = Be.I32(buf, o + 4), ReliableSeq = Be.I32(buf, o + 8)
                };
                if (c.Size < CmdHeader || o + c.Size > len) return false;
                int p = o + CmdHeader;
                switch (c.Type)
                {
                    case Cmd.Ack:
                    case Cmd.AckUnsequenced:
                        if (c.Size >= 20) { c.AckSeq = Be.I32(buf, p); c.AckSentTime = Be.I32(buf, p + 4); }
                        p = o + c.Size; break;
                    case Cmd.Unreliable:
                    case Cmd.Unsequenced:
                        if (c.Size < 16) return false;
                        c.UnreliableSeq = Be.I32(buf, p); p += 4; break;
                    case Cmd.Fragment:
                    case Cmd.FragmentUnsequenced:
                        if (c.Size < FragHeader) return false;
                        c.Start = Be.I32(buf, p); c.FragCount = Be.I32(buf, p + 4); c.FragNum = Be.I32(buf, p + 8);
                        c.TotalLen = Be.I32(buf, p + 12); c.FragOffset = Be.I32(buf, p + 16); p += 20; break;
                }
                int dataLen = o + c.Size - p;
                if (dataLen > 0) { c.Data = new byte[dataLen]; Buffer.BlockCopy(buf, p, c.Data, 0, dataLen); }
                cmds.Add(c);
                o += c.Size;
            }
            return true;
        }

        private static byte[] Header(byte type, byte ch, byte flags, int size, int seq, byte reserved = 0)
        {
            var b = new byte[size];
            b[0] = type; b[1] = ch; b[2] = flags; b[3] = reserved;
            Be.W32(b, 4, size); Be.W32(b, 8, seq);
            return b;
        }

        public static byte[] Ack(in InCmd c, int sentTime)
        {
            bool unseq = (c.Flags & Cmd.FlagUnsequenced) != 0;
            var b = Header(unseq ? Cmd.AckUnsequenced : Cmd.Ack, c.Channel, 0, 20, 0, 4);
            Be.W32(b, 12, c.ReliableSeq); Be.W32(b, 16, sentTime);
            return b;
        }

        /// <summary>VerifyConnect: the client reads its peerId from the first 2 payload bytes (payload must be 32 bytes).</summary>
        public static byte[] Verify(ushort peerId, int seq, byte[] connectPayload)
        {
            var b = Header(Cmd.VerifyConnect, Cmd.ControlChannel, Cmd.FlagReliable, 44, seq, 4);
            if (connectPayload != null) Buffer.BlockCopy(connectPayload, 0, b, 12, Math.Min(32, connectPayload.Length));
            Be.W16(b, 12, peerId);
            return b;
        }

        public static byte[] Reliable(byte ch, int seq, byte[] data, int off, int len)
        {
            var b = Header(Cmd.Reliable, ch, Cmd.FlagReliable, CmdHeader + len, seq, 4);
            Buffer.BlockCopy(data, off, b, CmdHeader, len);
            return b;
        }

        public static byte[] Fragment(byte ch, int seq, int start, int count, int num, int total, int fragOffset, byte[] data, int len)
        {
            var b = Header(Cmd.Fragment, ch, Cmd.FlagReliable, FragHeader + len, seq, 4);
            Be.W32(b, 12, start); Be.W32(b, 16, count); Be.W32(b, 20, num); Be.W32(b, 24, total); Be.W32(b, 28, fragOffset);
            Buffer.BlockCopy(data, fragOffset, b, FragHeader, len);
            return b;
        }

        /// <summary>Unreliable: reliableSeq must be the channel's last sent reliable seq (client discards older ones).</summary>
        public static byte[] Unreliable(byte ch, int reliableSeq, int unreliableSeq, byte[] data)
        {
            var b = Header(Cmd.Unreliable, ch, 0, 16 + data.Length, reliableSeq, 4);
            Be.W32(b, 12, unreliableSeq);
            Buffer.BlockCopy(data, 0, b, 16, data.Length);
            return b;
        }

        /// <summary>Unsequenced: delivered immediately, no ordering, deduplicated by group number window (128).</summary>
        public static byte[] Unsequenced(byte ch, int group, byte[] data)
        {
            var b = Header(Cmd.Unsequenced, ch, Cmd.FlagUnsequenced, 16 + data.Length, 0, 4);
            Be.W32(b, 12, group);
            Buffer.BlockCopy(data, 0, b, 16, data.Length);
            return b;
        }

        public static byte[] Disconnect(byte reason) => Header(Cmd.Disconnect, Cmd.ControlChannel, 0, 12, 0, reason);

        public static byte[] Datagram(ushort peerId, int sentTime, int challenge, List<byte[]> cmds, int from, int count, int bytes)
        {
            var d = new byte[DatagramHeader + bytes];
            Be.W16(d, 0, peerId);
            d[2] = 0;
            d[3] = (byte)count;
            Be.W32(d, 4, sentTime);
            Be.W32(d, 8, challenge);
            int o = DatagramHeader;
            for (int i = from; i < from + count; i++) { var c = cmds[i]; Buffer.BlockCopy(c, 0, d, o, c.Length); o += c.Length; }
            return d;
        }
    }
}
