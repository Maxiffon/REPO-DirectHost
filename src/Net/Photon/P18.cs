// REPO DirectHost — minimal Protocol18 (GpBinaryV18) codec.
// The server never re-serializes player data: values are kept as raw typed bytes and copied as-is.
// Only simple values needed by server logic are decoded.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RepoDirectHost.Net.Photon
{
    internal static class P18
    {
        public const byte Unknown = 0, Boolean = 2, Byte = 3, Short = 4, Float = 5, Double = 6, String = 7, Null = 8,
            CompressedInt = 9, CompressedLong = 10, Int1 = 11, Int1_ = 12, Int2 = 13, Int2_ = 14, L1 = 15, L1_ = 16, L2 = 17, L2_ = 18,
            Custom = 19, Dictionary = 20, Hashtable = 21, ObjectArray = 23, OperationRequest = 24, OperationResponse = 25, EventData = 26,
            BooleanFalse = 27, BooleanTrue = 28, ShortZero = 29, IntZero = 30, LongZero = 31, FloatZero = 32, DoubleZero = 33, ByteZero = 34,
            Array = 64, BooleanArray = 66, ByteArray = 67, ShortArray = 68, FloatArray = 69, DoubleArray = 70, StringArray = 71,
            CompressedIntArray = 73, CompressedLongArray = 74, CustomTypeArray = 83, DictionaryArray = 84, HashtableArray = 85;

        // ------------------------------------------------------------------ varints
        public static uint ReadVarUInt(byte[] b, ref int p)
        {
            uint v = 0; int shift = 0;
            while (true)
            {
                if (p >= b.Length) throw new EndOfStreamException("varuint");
                byte x = b[p++];
                v |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0 || shift >= 28) return v;
                shift += 7;
            }
        }
        public static ulong ReadVarULong(byte[] b, ref int p)
        {
            ulong v = 0; int shift = 0;
            while (true)
            {
                if (p >= b.Length) throw new EndOfStreamException("varulong");
                byte x = b[p++];
                v |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0 || shift >= 63) return v;
                shift += 7;
            }
        }
        public static int ZigZag32(uint v) => (int)(v >> 1) ^ -(int)(v & 1);
        public static long ZigZag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

        private static void Need(byte[] b, int p, int n) { if (n < 0 || p + n > b.Length) throw new EndOfStreamException("p18"); }

        // ------------------------------------------------------------------ skipping (any type)
        /// <summary>Skips one typed value (type byte + body); returns nothing, advances p.</summary>
        public static void SkipTyped(byte[] b, ref int p) { Need(b, p, 1); byte t = b[p++]; SkipValue(b, ref p, t); }

        /// <summary>Skips the body of a value whose type byte was already consumed.</summary>
        public static void SkipValue(byte[] b, ref int p, byte t)
        {
            if (t >= 128 && t <= 228) { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); p += n; return; }   // custom slim
            switch (t)
            {
                case Null: case BooleanFalse: case BooleanTrue: case ShortZero: case IntZero: case LongZero: case FloatZero: case DoubleZero: case ByteZero: return;
                case Boolean: case Byte: case Int1: case Int1_: case L1: case L1_: Need(b, p, 1); p += 1; return;
                case Short: case Int2: case Int2_: case L2: case L2_: Need(b, p, 2); p += 2; return;
                case Float: Need(b, p, 4); p += 4; return;
                case Double: Need(b, p, 8); p += 8; return;
                case String: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); p += n; return; }
                case CompressedInt: ReadVarUInt(b, ref p); return;
                case CompressedLong: ReadVarULong(b, ref p); return;
                case Custom: { Need(b, p, 1); p++; int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); p += n; return; }
                case Hashtable: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) { SkipTyped(b, ref p); SkipTyped(b, ref p); } return; }
                case Dictionary: SkipDictionary(b, ref p); return;
                case ObjectArray: case Array: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) SkipTyped(b, ref p); return; }
                case BooleanArray: { uint n = ReadVarUInt(b, ref p); int bytes = (int)((n + 7) / 8); Need(b, p, bytes); p += bytes; return; }
                case ByteArray: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); p += n; return; }
                case ShortArray: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n * 2); p += n * 2; return; }
                case FloatArray: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n * 4); p += n * 4; return; }
                case DoubleArray: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n * 8); p += n * 8; return; }
                case StringArray: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) { int l = (int)ReadVarUInt(b, ref p); Need(b, p, l); p += l; } return; }
                case CompressedIntArray: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) ReadVarUInt(b, ref p); return; }
                case CompressedLongArray: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) ReadVarULong(b, ref p); return; }
                case CustomTypeArray: { uint n = ReadVarUInt(b, ref p); Need(b, p, 1); p++; for (uint i = 0; i < n; i++) { int l = (int)ReadVarUInt(b, ref p); Need(b, p, l); p += l; } return; }
                case HashtableArray: { uint n = ReadVarUInt(b, ref p); for (uint i = 0; i < n; i++) SkipValue(b, ref p, Hashtable); return; }
                case DictionaryArray:
                    {
                        ReadDictHeader(b, ref p, out byte kt, out byte vt);
                        uint n = ReadVarUInt(b, ref p);
                        for (uint i = 0; i < n; i++) SkipDictElements(b, ref p, kt, vt);
                        return;
                    }
                default: throw new InvalidDataException("P18 type not supported for skipping: " + t);
            }
        }

        // Dictionary: [keyType][valueType + nested descriptors][count][elements]
        private static void ReadDictHeader(byte[] b, ref int p, out byte keyType, out byte valueType)
        {
            Need(b, p, 2);
            keyType = b[p++];
            valueType = b[p++];
            if (valueType == Dictionary) SkipNestedDictDescriptor(b, ref p);
            else if (valueType == Array) { while (true) { Need(b, p, 1); byte x = b[p++]; if (x != Array) break; } valueType = Unknown; }   // array value → typed elements
        }
        private static void SkipNestedDictDescriptor(byte[] b, ref int p)
        {
            Need(b, p, 2); p++; byte vt = b[p++];
            if (vt == Dictionary) SkipNestedDictDescriptor(b, ref p);
            else if (vt == Array) { while (true) { Need(b, p, 1); if (b[p++] != Array) break; } }
        }
        private static void SkipDictionary(byte[] b, ref int p)
        {
            ReadDictHeader(b, ref p, out byte kt, out byte vt);
            SkipDictElements(b, ref p, kt, vt);
        }
        private static void SkipDictElements(byte[] b, ref int p, byte kt, byte vt)
        {
            uint n = ReadVarUInt(b, ref p);
            for (uint i = 0; i < n; i++)
            {
                if (kt == Unknown) SkipTyped(b, ref p); else SkipValue(b, ref p, kt);
                if (vt == Unknown) SkipTyped(b, ref p); else SkipValue(b, ref p, vt);
            }
        }

        // ------------------------------------------------------------------ decoding simple values
        /// <summary>Decodes a typed value at p if it is a simple type; returns false (and skips it) otherwise.</summary>
        public static bool TryReadSimple(byte[] b, ref int p, out object value)
        {
            Need(b, p, 1);
            byte t = b[p++];
            int start = p;
            value = null;
            switch (t)
            {
                case Null: return true;
                case BooleanFalse: value = false; return true;
                case BooleanTrue: value = true; return true;
                case Boolean: Need(b, p, 1); value = b[p++] != 0; return true;
                case ByteZero: value = (byte)0; return true;
                case Byte: Need(b, p, 1); value = b[p++]; return true;
                case ShortZero: value = (short)0; return true;
                case Short: Need(b, p, 2); value = (short)(b[p] | (b[p + 1] << 8)); p += 2; return true;
                case IntZero: value = 0; return true;
                case Int1: Need(b, p, 1); value = (int)b[p++]; return true;
                case Int1_: Need(b, p, 1); value = -(int)b[p++]; return true;
                case Int2: Need(b, p, 2); value = b[p] | (b[p + 1] << 8); p += 2; return true;
                case Int2_: Need(b, p, 2); value = -(b[p] | (b[p + 1] << 8)); p += 2; return true;
                case CompressedInt: value = ZigZag32(ReadVarUInt(b, ref p)); return true;
                case LongZero: value = 0L; return true;
                case L1: Need(b, p, 1); value = (long)b[p++]; return true;
                case L1_: Need(b, p, 1); value = -(long)b[p++]; return true;
                case L2: Need(b, p, 2); value = (long)(b[p] | (b[p + 1] << 8)); p += 2; return true;
                case L2_: Need(b, p, 2); value = -(long)(b[p] | (b[p + 1] << 8)); p += 2; return true;
                case CompressedLong: value = ZigZag64(ReadVarULong(b, ref p)); return true;
                case String: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); value = Encoding.UTF8.GetString(b, p, n); p += n; return true; }
                case ByteArray: { int n = (int)ReadVarUInt(b, ref p); Need(b, p, n); var a = new byte[n]; Buffer.BlockCopy(b, p, a, 0, n); p += n; value = a; return true; }
                case CompressedIntArray: { uint n = ReadVarUInt(b, ref p); var a = new int[n]; for (uint i = 0; i < n; i++) a[i] = ZigZag32(ReadVarUInt(b, ref p)); value = a; return true; }
                case StringArray: { uint n = ReadVarUInt(b, ref p); var a = new string[n]; for (uint i = 0; i < n; i++) { int l = (int)ReadVarUInt(b, ref p); Need(b, p, l); a[i] = Encoding.UTF8.GetString(b, p, l); p += l; } value = a; return true; }
                case ObjectArray:
                    {
                        uint n = ReadVarUInt(b, ref p);
                        var a = new object[n];
                        bool simple = true;
                        for (uint i = 0; i < n; i++) { if (!TryReadSimple(b, ref p, out var v)) simple = false; a[i] = v; }
                        value = a; return simple;
                    }
                default:
                    SkipValue(b, ref p, t);
                    return false;
            }
        }

        public static int? AsInt(object v) => v is int i ? i : (int?)null;

        /// <summary>Canonical identity of a simple key (type matters: int 5 ≠ byte 5 ≠ "5"). Non-simple keys use their raw bytes.</summary>
        public static string KeyId(byte[] b, int start, int end)
        {
            int p = start;
            try
            {
                if (TryReadSimple(b, ref p, out var v) && p == end)
                {
                    switch (v)
                    {
                        case null: return "n";
                        case byte x: return "b" + x;
                        case short x: return "h" + x;
                        case int x: return "i" + x;
                        case long x: return "l" + x;
                        case bool x: return x ? "T" : "F";
                        case string s: return "s" + s;
                    }
                }
            }
            catch { }
            return "r" + Convert.ToBase64String(b, start, end - start);
        }

        // ------------------------------------------------------------------ hashtables (raw entries)
        public struct Entry { public int KeyStart, KeyEnd, ValStart, ValEnd; }

        /// <summary>Parses a typed Hashtable value at p into raw key/value spans.</summary>
        public static List<Entry> ReadHashtableEntries(byte[] b, ref int p)
        {
            Need(b, p, 1);
            byte t = b[p++];
            var list = new List<Entry>();
            if (t == Null) return list;
            if (t != Hashtable) { SkipValue(b, ref p, t); return null; }
            uint n = ReadVarUInt(b, ref p);
            for (uint i = 0; i < n; i++)
            {
                var e = new Entry { KeyStart = p };
                SkipTyped(b, ref p); e.KeyEnd = p; e.ValStart = p;
                SkipTyped(b, ref p); e.ValEnd = p;
                list.Add(e);
            }
            return list;
        }
    }

    /// <summary>Writes Protocol18 values. Raw typed bytes from clients can be appended unchanged.</summary>
    internal sealed class P18Writer
    {
        private byte[] _b = new byte[256];
        private int _n;
        public int Length => _n;
        public byte[] ToArray() { var a = new byte[_n]; Buffer.BlockCopy(_b, 0, a, 0, _n); return a; }

        private void Ensure(int more) { if (_n + more <= _b.Length) return; int c = _b.Length * 2; while (c < _n + more) c *= 2; Array.Resize(ref _b, c); }
        public P18Writer U8(byte v) { Ensure(1); _b[_n++] = v; return this; }
        public P18Writer Raw(byte[] src, int off, int len) { Ensure(len); Buffer.BlockCopy(src, off, _b, _n, len); _n += len; return this; }
        public P18Writer Raw(byte[] src) => Raw(src, 0, src.Length);
        public P18Writer VarUInt(uint v) { while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; } return U8((byte)v); }
        public P18Writer I16LE(short v) { U8((byte)v); return U8((byte)(v >> 8)); }

        public P18Writer Null() => U8(P18.Null);
        public P18Writer Bool(bool v) => U8(v ? P18.BooleanTrue : P18.BooleanFalse);
        public P18Writer Byte(byte v) => v == 0 ? U8(P18.ByteZero) : U8(P18.Byte).U8(v);
        public P18Writer Int(int v)
        {
            if (v == 0) return U8(P18.IntZero);
            if (v > 0 && v <= 0xFF) return U8(P18.Int1).U8((byte)v);
            if (v < 0 && v >= -0xFF) return U8(P18.Int1_).U8((byte)-v);
            if (v > 0 && v <= 0xFFFF) return U8(P18.Int2).U8((byte)v).U8((byte)(v >> 8));
            if (v < 0 && v >= -0xFFFF) { int m = -v; return U8(P18.Int2_).U8((byte)m).U8((byte)(m >> 8)); }
            return U8(P18.CompressedInt).VarUInt((uint)((v << 1) ^ (v >> 31)));
        }
        public P18Writer Str(string s)
        {
            if (s == null) return Null();
            var bytes = Encoding.UTF8.GetBytes(s);
            return U8(P18.String).VarUInt((uint)bytes.Length).Raw(bytes);
        }
        public P18Writer Bytes(byte[] a) => a == null ? Null() : U8(P18.ByteArray).VarUInt((uint)a.Length).Raw(a);
        public P18Writer IntArray(IReadOnlyCollection<int> a)
        {
            U8(P18.CompressedIntArray).VarUInt((uint)a.Count);
            foreach (int v in a) VarUInt((uint)((v << 1) ^ (v >> 31)));
            return this;
        }
        public P18Writer HashtableHeader(int count) => U8(P18.Hashtable).VarUInt((uint)count);
    }
}
