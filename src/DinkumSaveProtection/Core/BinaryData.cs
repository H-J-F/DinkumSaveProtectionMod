using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DinkumSaveProtection.Core
{
    // A bounded, canonical value format. No BinaryFormatter, type names, or executable objects.
    public static class BinaryData
    {
        public const int MaxBytes = 16 * 1024 * 1024;
        public const int MaxModuleBytes = MaxBytes - 65536;
        public const int MaxElements = 200000;
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }
        public static bool IsHash(string? s) => s != null && s.Length == 64 && s.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        public static byte[] Encode(object? value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                int budget = MaxElements;
                Write(w, value, 0, ref budget);
                if (ms.Length > MaxBytes) throw new InvalidDataException("Data exceeds limit");
                return ms.ToArray();
            }
        }
        public static object? Decode(byte[] bytes)
        {
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Data exceeds limit");
            using (var ms = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
            {
                int budget = MaxElements;
                var result = Read(r, 0, ref budget);
                if (ms.Position != ms.Length) throw new InvalidDataException("Trailing bytes");
                return result;
            }
        }
        public static Dictionary<string, object?> Map(object? v) => v as Dictionary<string, object?> ?? throw new InvalidDataException("Expected map");
        public static object?[] List(object? v) => v as object?[] ?? throw new InvalidDataException("Expected list");
        public static string Text(object? v) => v as string ?? throw new InvalidDataException("Expected string");
        public static long Number(object? v) => v is long n ? n : throw new InvalidDataException("Expected integer");
        private static void Write(BinaryWriter w, object? v, int depth, ref int budget)
        {
            if (depth > 32 || --budget < 0) throw new InvalidDataException("Encode budget exceeded");
            if (v == null) { w.Write((byte)0); return; }
            if (v is bool b) { w.Write((byte)1); w.Write(b); }
            else if (v is long n) { w.Write((byte)2); w.Write(n); }
            else if (v is ulong u) { w.Write((byte)3); w.Write(u); }
            else if (v is double d) { if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidDataException("Non finite value"); w.Write((byte)4); w.Write(d); }
            else if (v is string s) { w.Write((byte)5); var bytes = Encoding.UTF8.GetBytes(s); if (bytes.Length > 65536) throw new InvalidDataException("String too long"); w.Write(bytes.Length); w.Write(bytes); }
            else if (v is byte[] bytes) { w.Write((byte)6); if (bytes.Length > MaxBytes) throw new InvalidDataException("Block too long"); w.Write(bytes.Length); w.Write(bytes); }
            else if (v is Dictionary<string, object?> map)
            {
                w.Write((byte)7); Count(map.Count); w.Write(map.Count);
                foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal)) { Write(w, pair.Key, depth + 1, ref budget); Write(w, pair.Value, depth + 1, ref budget); }
            }
            else if (v is object?[] list) { w.Write((byte)8); Count(list.Length); w.Write(list.Length); foreach (var item in list) Write(w, item, depth + 1, ref budget); }
            else throw new InvalidDataException("Unsupported value: " + v.GetType().FullName);
            if (w.BaseStream.Position > MaxBytes) throw new InvalidDataException("Data exceeds limit");
        }
        private static void Count(int count) { if (count < 0 || count > MaxElements) throw new InvalidDataException("Invalid element count"); }
        private static object? Read(BinaryReader r, int depth, ref int budget)
        {
            if (depth > 32 || --budget < 0) throw new InvalidDataException("Decode budget exceeded");
            switch (r.ReadByte())
            {
                case 0: return null;
                case 1: return r.ReadBoolean();
                case 2: return r.ReadInt64();
                case 3: return r.ReadUInt64();
                case 4: var d = r.ReadDouble(); if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidDataException("Non finite value"); return d;
                case 5: return new UTF8Encoding(false, true).GetString(ReadBytes(r, 65536));
                case 6: return ReadBytes(r, MaxBytes);
                case 7:
                    int count = r.ReadInt32(); Count(count);
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (int i = 0; i < count; i++) { var key = Text(Read(r, depth + 1, ref budget)); if (map.ContainsKey(key)) throw new InvalidDataException("Duplicate key"); map.Add(key, Read(r, depth + 1, ref budget)); }
                    return map;
                case 8:
                    int len = r.ReadInt32(); Count(len);
                    if (len > budget) throw new InvalidDataException("List budget exceeded");
                    var list = new object?[len]; for (int i = 0; i < len; i++) list[i] = Read(r, depth + 1, ref budget); return list;
                default: throw new InvalidDataException("Unknown value tag");
            }
        }
        private static byte[] ReadBytes(BinaryReader r, int max)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > max || len > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid byte length");
            return r.ReadBytes(len);
        }
    }
}
