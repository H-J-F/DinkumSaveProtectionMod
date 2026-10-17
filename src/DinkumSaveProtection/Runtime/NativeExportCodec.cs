using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DinkumSaveProtection.Core;

namespace DinkumSaveProtection.Runtime
{
    internal sealed class NativeExportImage
    {
        internal byte[] Bytes = null!;
        internal int PackedItems, Extensions;
    }
    internal sealed class NativeExportCodec : INativeRecordFile
    {
        private readonly ES3File file;
        private NativeExportCodec(ES3File file) { this.file = file; }
        public bool Contains(string key) => file.KeyExists(key);
        public object Read(string key) => file.Load(key);
        public void Write(string key, object value)
        {
            // Match vanilla Save<T> exactly; Save<object> can add a polymorphic wrapper.
            var save = typeof(ES3File).GetMethods().Single(m => m.Name == "Save" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
            save.MakeGenericMethod(value.GetType()).Invoke(file, new[] { (object)key, value });
        }
        public void Delete(string key) => file.DeleteKey(key);
        private static Dictionary<string, string> RawFingerprints(ES3File file)
        {
            var cache = (IDictionary)typeof(ES3File).GetField("cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(file)!;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in cache)
            {
                var value = pair.Value!;
                var bytes = (byte[])value.GetType().GetField("bytes")!.GetValue(value)!;
                var type = value.GetType().GetField("type")!.GetValue(value)!;
                var runtimeType = (Type)type.GetType().GetField("type")!.GetValue(type)!;
                result.Add((string)pair.Key, runtimeType.AssemblyQualifiedName + ":" + BinaryData.Hash(bytes));
            }
            return result;
        }
        private static byte[] RawRecord(ES3File file, string key)
        {
            var cache = (IDictionary)typeof(ES3File).GetField("cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(file)!;
            var value = cache[key]!; return (byte[])value.GetType().GetField("bytes")!.GetValue(value)!;
        }
        private static HashSet<string> KnownPaths(Type type)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            void Walk(Type t, string path, int depth)
            {
                if (depth > 32 || t.IsPrimitive || t.IsEnum || t == typeof(string)) return;
                if (t.IsArray) { Walk(t.GetElementType()!,path+"[]",depth+1); return; }
                if (typeof(IList).IsAssignableFrom(t)) { if (t.IsGenericType) Walk(t.GetGenericArguments()[0],path+"[]",depth+1); return; }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                { string p = path+"/"+f.Name.Replace("~","~0").Replace("/","~1"); paths.Add(p); Walk(f.FieldType,p,depth+1); }
            }
            Walk(type,"",0); return paths;
        }
        // Game-thread only. ES3's type registry/default settings are never used from the worker.
        // A private ES3File preserves opaque records and never enters ES3File.cachedFiles.
        internal static NativeExportImage Prepare(byte[] nativeBytes, Dictionary<string, object?> modules)
        {
            var settings = new ES3Settings(ES3.Location.File) { postprocessRawCachedData = true };
            var source = new ES3File(nativeBytes, settings); var adapter = new NativeExportCodec(source);
            foreach (string key in NativeValidation.Required) if (!source.KeyExists(key)) throw new InvalidDataException("Native export baseline lacks required record: " + key);
            var before = RawFingerprints(source);
            var originalRaw = before.Keys.ToDictionary(k => k, k => (byte[])RawRecord(source,k).Clone(), StringComparer.Ordinal);
            var projection = NativeProjection.Apply(adapter, modules, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), id => { var item = Inventory.Instance.allItems[id]; return item != null && item.hasFuel; });
            byte[] bytes = source.LoadRawBytes();
            if (bytes.Length == 0 || bytes.Length > NativeExportTransaction.MaxNativeBytes) throw new InvalidDataException("Native export byte budget exceeded");
            var roundTrip = new ES3File(bytes, settings); var verified = new NativeExportCodec(roundTrip);
            projection.Verify(verified);
            foreach (var key in projection.Expected.Keys.Where(originalRaw.ContainsKey))
            {
                var originalPaths = JsonPropertyPaths.Read(originalRaw[key], settings.encoding);
                var newPaths = JsonPropertyPaths.Read(RawRecord(roundTrip,key), settings.encoding);
                originalPaths.ExceptWith(KnownPaths(projection.RecordTypes[key])); // Known owned list elements may legitimately be deleted.
                if (!originalPaths.IsSubsetOf(newPaths)) throw new InvalidDataException("Unknown native property would be lost inside " + key + "; refusing overwrite");
            }
            var after = RawFingerprints(roundTrip);
            var expectedKeys = new HashSet<string>(before.Keys.Where(k => k != "stash_0" && k != "stash_1"), StringComparer.Ordinal);
            expectedKeys.UnionWith(projection.Expected.Keys);
            if (!expectedKeys.SetEquals(after.Keys)) throw new InvalidDataException("Native export changed unrelated key inventory");
            foreach (var entry in before.Where(p => !projection.Expected.ContainsKey(p.Key) && p.Key != "stash_0" && p.Key != "stash_1"))
                if (!after.TryGetValue(entry.Key, out var value) || value != entry.Value) throw new InvalidDataException("Native export changed an unrelated world/mod record: " + entry.Key);
            return new NativeExportImage { Bytes = bytes, PackedItems = projection.PackedItems, Extensions = projection.Extensions };
        }
    }
}
