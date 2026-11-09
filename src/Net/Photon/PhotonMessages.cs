// REPO DirectHost — Photon message framing (F3 xx) on top of the P18 codec.
using System;
using System.Collections.Generic;

namespace RepoDirectHost.Net.Photon
{
    internal static class Msg
    {
        public const byte Magic = 0xF3;
        public const byte Init = 0, InitResponse = 1, Operation = 2, OperationResponse = 3, Event = 4, Disconnect = 5,
            InternalOperation = 6, InternalOperationResponse = 7, EncryptedFlag = 0x80;
    }

    /// <summary>A parsed operation request: parameters are raw typed-value spans into Body.</summary>
    internal sealed class OpRequest
    {
        public byte Code;
        public byte[] Body;                                 // [opCode][count][key typed]* (decrypted, without F3 xx)
        public readonly Dictionary<byte, (int Start, int End)> Params = new Dictionary<byte, (int, int)>();
        public bool Internal, WasEncrypted;

        public static OpRequest Parse(byte[] body, bool isInternal, bool encrypted)
        {
            var r = new OpRequest { Body = body, Internal = isInternal, WasEncrypted = encrypted };
            if (body.Length < 2) return null;
            r.Code = body[0];
            int n = body[1], p = 2;
            for (int i = 0; i < n; i++)
            {
                byte key = body[p++];
                int s = p;
                P18.SkipTyped(body, ref p);
                r.Params[key] = (s, p);
            }
            return r;
        }

        public bool Has(byte key) => Params.ContainsKey(key);
        public byte[] RawValue(byte key) { if (!Params.TryGetValue(key, out var sp)) return null; var a = new byte[sp.End - sp.Start]; Buffer.BlockCopy(Body, sp.Start, a, 0, a.Length); return a; }

        public object Simple(byte key)
        {
            if (!Params.TryGetValue(key, out var sp)) return null;
            int p = sp.Start;
            try { return P18.TryReadSimple(Body, ref p, out var v) ? v : null; } catch { return null; }
        }
        public string Str(byte key) => Simple(key) as string;
        public int? Int(byte key) => Simple(key) is int i ? i : (int?)null;
        public byte? Byte(byte key) => Simple(key) is byte b ? b : (byte?)null;
        public bool? Bool(byte key) => Simple(key) is bool b ? b : (bool?)null;
        public int[] IntArray(byte key) => Simple(key) as int[];

        public List<P18.Entry> Hashtable(byte key)
        {
            if (!Params.TryGetValue(key, out var sp)) return null;
            int p = sp.Start;
            try { return P18.ReadHashtableEntries(Body, ref p); } catch { return null; }
        }
    }

    /// <summary>Builds outgoing messages.</summary>
    internal sealed class MsgBuilder
    {
        private readonly P18Writer _w = new P18Writer();
        private int _countPos = -1;
        private int _count;

        private MsgBuilder() { }

        public static MsgBuilder Response(byte opCode, short returnCode = 0, string debug = null, bool isInternal = false)
        {
            var m = new MsgBuilder();
            m._w.U8(Msg.Magic).U8(isInternal ? Msg.InternalOperationResponse : Msg.OperationResponse).U8(opCode).I16LE(returnCode);
            if (debug == null) m._w.Null(); else m._w.Str(debug);
            m.BeginParams();
            return m;
        }

        public static MsgBuilder Event(byte code)
        {
            var m = new MsgBuilder();
            m._w.U8(Msg.Magic).U8(Msg.Event).U8(code);
            m.BeginParams();
            return m;
        }

        public static MsgBuilder DisconnectMessage(short code, string debug)
        {
            var m = new MsgBuilder();
            m._w.U8(Msg.Magic).U8(Msg.Disconnect).I16LE(code);
            m._w.Str(debug);
            m.BeginParams();
            return m;
        }

        private void BeginParams() { _countPos = _w.Length; _w.U8(0); }

        /// <summary>Starts a parameter; the caller writes exactly one typed value through W.</summary>
        public P18Writer Param(byte key) { _count++; _w.U8(key); return _w; }
        public MsgBuilder ParamRaw(byte key, byte[] typedValue) { Param(key).Raw(typedValue); return this; }
        public MsgBuilder ParamRaw(byte key, byte[] src, int start, int end) { Param(key).Raw(src, start, end - start); return this; }
        public MsgBuilder ParamInt(byte key, int v) { Param(key).Int(v); return this; }
        public MsgBuilder ParamStr(byte key, string v) { Param(key).Str(v); return this; }
        public MsgBuilder ParamBool(byte key, bool v) { Param(key).Bool(v); return this; }
        public MsgBuilder ParamByte(byte key, byte v) { Param(key).Byte(v); return this; }
        public MsgBuilder ParamBytes(byte key, byte[] v) { Param(key).Bytes(v); return this; }
        public MsgBuilder ParamIntArray(byte key, IReadOnlyCollection<int> v) { Param(key).IntArray(v); return this; }

        public byte[] Build()
        {
            var a = _w.ToArray();
            a[_countPos] = (byte)_count;
            return a;
        }
    }

    /// <summary>Ordered property table storing raw typed keys/values (room or actor properties).</summary>
    internal sealed class PropTable
    {
        private sealed class Item { public string Id; public byte[] Key; public byte[] Value; }
        private readonly List<Item> _items = new List<Item>();
        private readonly Dictionary<string, Item> _byId = new Dictionary<string, Item>();
        public int Count => _items.Count;

        public static string Id(byte k) => "b" + k;
        public static string Id(string k) => "s" + k;

        public void Set(string id, byte[] key, byte[] value)
        {
            if (_byId.TryGetValue(id, out var it)) { it.Value = value; return; }
            it = new Item { Id = id, Key = key, Value = value };
            _items.Add(it); _byId[id] = it;
        }
        public void SetByte(byte k, Action<P18Writer> value) { var w = new P18Writer(); value(w); Set(Id(k), new P18Writer().Byte(k).ToArray(), w.ToArray()); }
        public void Remove(string id) { if (_byId.TryGetValue(id, out var it)) { _byId.Remove(id); _items.Remove(it); } }
        public byte[] Get(string id) => _byId.TryGetValue(id, out var it) ? it.Value : null;

        public object GetSimple(string id)
        {
            var v = Get(id);
            if (v == null) return null;
            int p = 0;
            try { return P18.TryReadSimple(v, ref p, out var o) ? o : null; } catch { return null; }
        }

        /// <summary>Merges raw hashtable entries from a request; null values delete the key when deleteNulls.</summary>
        public void Merge(byte[] src, List<P18.Entry> entries, bool deleteNulls)
        {
            if (entries == null) return;
            foreach (var e in entries)
            {
                string id = P18.KeyId(src, e.KeyStart, e.KeyEnd);
                bool isNull = e.ValEnd - e.ValStart == 1 && src[e.ValStart] == P18.Null;
                if (isNull && deleteNulls) { Remove(id); continue; }
                var k = new byte[e.KeyEnd - e.KeyStart]; Buffer.BlockCopy(src, e.KeyStart, k, 0, k.Length);
                var v = new byte[e.ValEnd - e.ValStart]; Buffer.BlockCopy(src, e.ValStart, v, 0, v.Length);
                Set(id, k, v);
            }
        }

        /// <summary>True if every expected entry equals the current value (raw byte comparison of canonical form).</summary>
        public bool Matches(byte[] src, List<P18.Entry> expected)
        {
            if (expected == null) return true;
            foreach (var e in expected)
            {
                string id = P18.KeyId(src, e.KeyStart, e.KeyEnd);
                var cur = Get(id);
                string want = P18.KeyId(src, e.ValStart, e.ValEnd);
                string have = cur == null ? "n" : P18.KeyId(cur, 0, cur.Length);
                if (want != have) return false;
            }
            return true;
        }

        public void WriteHashtable(P18Writer w)
        {
            w.HashtableHeader(_items.Count);
            foreach (var it in _items) { w.Raw(it.Key); w.Raw(it.Value); }
        }

        public byte[] ToHashtable() { var w = new P18Writer(); WriteHashtable(w); return w.ToArray(); }
    }
}
