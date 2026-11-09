// REPO DirectHost — reachability probe used to pick the fastest direct path to the host.
// Client -> server port: "DHPROBE1" + 8-byte nonce (16 B). Server answers from the same socket:
// "DHPONG01" + nonce + 8-byte host tag (the host's SteamID), 24 B. ENet never sends such datagrams.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RepoDirectHost.Net
{
    internal static class Probe
    {
        private static readonly byte[] PingMagic = Encoding.ASCII.GetBytes("DHPROBE1");
        private static readonly byte[] PongMagic = Encoding.ASCII.GetBytes("DHPONG01");
        public const int PingLen = 16, PongLen = 24;

        public static bool IsPing(byte[] b, int n) => n == PingLen && Starts(b, PingMagic);

        public static byte[] MakePong(byte[] ping, ulong tag)
        {
            var r = new byte[PongLen];
            Buffer.BlockCopy(PongMagic, 0, r, 0, 8);
            Buffer.BlockCopy(ping, 8, r, 8, 8);
            for (int i = 0; i < 8; i++) r[16 + i] = (byte)(tag >> (8 * i));
            return r;
        }

        private static bool Starts(byte[] b, byte[] magic)
        {
            for (int i = 0; i < magic.Length; i++) if (b[i] != magic[i]) return false;
            return true;
        }

        public sealed class Result
        {
            public IPAddress Address;
            public int RttMs;
            public override string ToString() => $"{Address} ({RttMs} ms)";
        }

        /// <summary>
        /// Probes every candidate on both ports at the same moment and returns the first address whose master AND
        /// game port answered with the expected tag. All probes leave together, so the first full answer is the
        /// fastest path (LAN, Radmin/Hamachi/ZeroTier, or the public IP). Lost probes are repeated every 150 ms.
        /// Returns null when nothing answered within <paramref name="timeoutMs"/>.
        /// </summary>
        public static Result FindFastest(IList<IPAddress> candidates, int masterPort, int gamePort, ulong expectTag, int timeoutMs, Action<string> log = null)
        {
            var rtt = Measure(candidates, masterPort, gamePort, expectTag, timeoutMs, _ => true, log);
            for (int i = 0; i < rtt.Length; i++) if (rtt[i] >= 0) return new Result { Address = candidates[i], RttMs = rtt[i] };
            return null;
        }

        /// <summary>
        /// Probes all candidates at once; returns the RTT in ms per candidate (-1 = no full answer on both ports).
        /// Stops early as soon as <paramref name="enough"/> returns true for a candidate that fully answered.
        /// </summary>
        public static int[] Measure(IList<IPAddress> candidates, int masterPort, int gamePort, ulong expectTag, int timeoutMs, Func<int, bool> enough, Action<string> log = null)
        {
            int count = candidates == null ? 0 : Math.Min(candidates.Count, 64);
            var rtt = new int[count];
            for (int i = 0; i < count; i++) rtt[i] = -1;
            if (count == 0) return rtt;
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                try { s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                var nonce = new byte[6];
                new Random().NextBytes(nonce);
                var got = new int[count];                  // bit 1 = master answered, bit 2 = game answered
                var sw = Stopwatch.StartNew();
                var roundAt = new List<long>();
                var buf = new byte[64];
                long nextRound = 0;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (sw.ElapsedMilliseconds >= nextRound && roundAt.Count < 8)
                    {
                        int round = roundAt.Count;
                        roundAt.Add(sw.ElapsedMilliseconds);
                        for (int i = 0; i < count; i++)
                            for (int p = 0; p < 2; p++)
                            {
                                if ((got[i] & (1 << p)) != 0) continue;
                                var ping = new byte[PingLen];
                                Buffer.BlockCopy(PingMagic, 0, ping, 0, 8);
                                Buffer.BlockCopy(nonce, 0, ping, 8, 6);
                                ping[14] = (byte)i;
                                ping[15] = (byte)(p | (round << 1));
                                try { s.SendTo(ping, new IPEndPoint(candidates[i], p == 0 ? masterPort : gamePort)); } catch { }
                            }
                        nextRound = sw.ElapsedMilliseconds + 150;
                    }
                    if (!s.Poll(1000, SelectMode.SelectRead)) continue;
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref ep); } catch (SocketException) { continue; }
                    if (n != PongLen || !Starts(buf, PongMagic)) continue;
                    bool mine = true;
                    for (int k = 0; k < 6; k++) if (buf[8 + k] != nonce[k]) mine = false;
                    if (!mine) continue;
                    ulong tag = 0;
                    for (int k = 0; k < 8; k++) tag |= (ulong)buf[16 + k] << (8 * k);
                    int idx = buf[14], port = buf[15] & 1, r = buf[15] >> 1;
                    if (idx >= count) continue;
                    if (expectTag != 0 && tag != expectTag)
                    {
                        log?.Invoke($"probe: {candidates[idx]} answered, but it is another DirectHost server — skipped");
                        continue;
                    }
                    if ((got[idx] & (1 << port)) != 0) continue;
                    got[idx] |= 1 << port;
                    if (got[idx] == 3)
                    {
                        rtt[idx] = (int)(sw.ElapsedMilliseconds - roundAt[Math.Min(r, roundAt.Count - 1)]);
                        if (enough(idx)) return rtt;
                        bool all = true;
                        for (int i = 0; i < count; i++) if (got[i] != 3) all = false;
                        if (all) return rtt;
                    }
                }
                for (int i = 0; i < count; i++)
                    if (got[i] == 1 || got[i] == 2) log?.Invoke($"probe: {candidates[i]} answered only on the {(got[i] == 1 ? "master" : "game")} port — is the other port forwarded?");
                return rtt;
            }
        }
    }
}
