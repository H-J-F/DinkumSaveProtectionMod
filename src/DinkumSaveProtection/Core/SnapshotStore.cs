using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DinkumSaveProtection.Core
{
    public sealed class SnapshotStore
    {
        private readonly string root;
        private readonly object sync = new object();
        public long Writes { get; private set; }
        public SnapshotStore(string directory, bool create = true)
        {
            root = Path.GetFullPath(directory);
            foreach (string path in new[] { root, Path.Combine(root, "blocks"), Path.Combine(root, "versions"), Path.Combine(root, "history") })
            { if (create) Directory.CreateDirectory(path); else if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path); }
        }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".previous"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private string BlockPath(string hash) { if (!BinaryData.IsHash(hash)) throw new InvalidDataException("Invalid block name"); return Path.Combine(root, "blocks", hash + ".blk"); }
        public bool HasBlock(string hash) { try { ReadBlock(hash); return true; } catch (Exception e) when (e is IOException || e is InvalidDataException) { return false; } }
        public byte[] ReadBlock(string hash)
        {
            var path = BlockPath(hash);
            if (new FileInfo(path).Length > BinaryData.MaxBytes) throw new InvalidDataException("Oversized block");
            var bytes = File.ReadAllBytes(path);
            if (BinaryData.Hash(bytes) != hash) throw new InvalidDataException("Block checksum mismatch");
            return bytes;
        }
        public void PutBlock(string hash, byte[] bytes)
        {
            lock (sync)
            {
                if (bytes.Length > BinaryData.MaxBytes || BinaryData.Hash(bytes) != hash) throw new InvalidDataException("Block checksum mismatch");
                if (!HasBlock(hash)) { AtomicWrite(BlockPath(hash), bytes); Writes++; }
            }
        }
        public string[] Missing(Snapshot s) => s.Modules.Values.Distinct().Where(h => !HasBlock(h)).ToArray();
        public void Commit(Snapshot snapshot)
        {
            lock (sync)
            {
                if (Missing(snapshot).Length != 0) throw new InvalidDataException("Cannot publish incomplete snapshot");
                string path = Path.Combine(root, "versions", snapshot.Id + ".snap");
                if (Find(snapshot.Id) == null || !File.Exists(path)) { AtomicWrite(path, snapshot.Serialize()); Writes++; }
            }
        }
        public void Commit(Capture capture) { lock (sync) { foreach (var p in capture.Blocks) PutBlock(p.Key, p.Value); Commit(capture.Manifest); } }
        public Snapshot? Find(string id)
        {
            if (!BinaryData.IsHash(id)) return null;
            try
            {
                string path = Path.Combine(root, "versions", id + ".snap");
                if (!File.Exists(path)) path = Path.Combine(root, "history", id + ".snap");
                if (new FileInfo(path).Length > 65536) return null;
                var s = Snapshot.Parse(File.ReadAllBytes(path));
                return s.Id == id ? s : null;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is KeyNotFoundException) { return null; }
        }
        public Snapshot[] ValidVersions(string character)
        {
            lock (sync) return Directory.EnumerateFiles(Path.Combine(root, "versions"), "*.snap")
                .Select(p => Find(Path.GetFileNameWithoutExtension(p))).Where(s => s != null && s.Character == character && Missing(s).Length == 0).Cast<Snapshot>().ToArray();
        }
        public Snapshot? Latest(string character)
        {
            var versions = ValidVersions(character);
            if (versions.Length == 0) return null;
            var selectedPath = Path.Combine(root, "selected.bin");
            if (File.Exists(selectedPath))
            {
                var selected = Find(BinaryData.Text(BinaryData.Decode(File.ReadAllBytes(selectedPath)))) ?? throw new InvalidDataException("Selected branch is unavailable");
                if (selected.Character != character) throw new InvalidDataException("Selected character mismatch");
                versions = versions.Where(s => Versions.Compare(selected, s, Find) != VersionOrder.Conflict).ToArray();
                if (versions.Length == 0) throw new InvalidDataException("Selected branch has no complete snapshot");
            }
            // If branches exist, the caller must explicitly resolve them; never guess by timestamp.
            Snapshot candidate = versions[0];
            foreach (var s in versions.Skip(1)) if (Versions.Compare(candidate, s, Find) == VersionOrder.RightNewer) candidate = s;
            if (versions.Any(s => Versions.Compare(candidate, s, Find) == VersionOrder.Conflict)) throw new InvalidDataException("Multiple save branches require a choice");
            return candidate;
        }
        public void SelectBranch(string id)
        {
            lock (sync)
            {
                var s = Find(id) ?? throw new InvalidDataException("Unknown snapshot");
                if (Missing(s).Length != 0) throw new InvalidDataException("Incomplete snapshot");
                AtomicWrite(Path.Combine(root, "selected.bin"), BinaryData.Encode(id));
            }
        }
        public Capture Read(Snapshot snapshot) => new Capture(snapshot, snapshot.Modules.Values.Distinct().ToDictionary(h => h, ReadBlock));
        public void Prune(string character, string? checkpoint, int keep = 32)
        {
            lock (sync)
            {
                var versions = ValidVersions(character);
                // Do not erase unresolved branches or history required to explain them.
                var latest = Latest(character); if (latest == null) return;
                var retain = new HashSet<string>(); var cursor = latest;
                for (int i = 0; cursor != null && i < keep; i++) { retain.Add(cursor.Id); cursor = Find(cursor.Parent); }
                if (checkpoint != null && BinaryData.IsHash(checkpoint)) retain.Add(checkpoint);
                foreach (var s in versions.Where(v => !retain.Contains(v.Id) && Versions.Compare(latest, v, Find) != VersionOrder.Conflict))
                { var path = Path.Combine(root, "versions", s.Id + ".snap"); var history = Path.Combine(root, "history", s.Id + ".snap"); if (!File.Exists(history)) File.Move(path, history); else File.Delete(path); }
                // GC is store-wide: another character or an in-flight transfer may still need blocks.
                // Transfer blocks are collected only at next startup, when none can be in flight.
            }
        }
        public void CollectUnreferencedBlocks()
        {
            lock (sync)
            {
                var hashes = new HashSet<string>(Directory.EnumerateFiles(Path.Combine(root, "versions"), "*.snap")
                    .Select(p => Find(Path.GetFileNameWithoutExtension(p))).Where(s => s != null).SelectMany(s => s!.Modules.Values));
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "blocks"), "*.blk"))
                    if (!hashes.Contains(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
            }
        }
    }
}
