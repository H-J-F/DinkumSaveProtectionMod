using System;
using System.Collections.Generic;
using System.IO;

namespace DinkumSaveProtection.Core
{
    public sealed class NativeExportException : IOException
    {
        public bool NativePublished { get; }
        public NativeExportException(string message, bool published, Exception inner) : base(message, inner) { NativePublished = published; }
    }
    public static class NativeExportTransaction
    {
        public const int MaxNativeBytes = 64 * 1024 * 1024;
        public static void RequireHistory(Snapshot selected, string checkpointId, Snapshot? checkpoint, Func<string, Snapshot?> find)
        {
            if (checkpointId == "" || selected.Id == checkpointId) return;
            if (checkpoint == null || checkpoint.Character != selected.Character || checkpoint.Build != selected.Build)
                throw new InvalidDataException("Cannot prove native checkpoint ancestry; original native progress retained");
            var order = Versions.Compare(selected, checkpoint, find);
            if (order != VersionOrder.Same && order != VersionOrder.LeftNewer)
                throw new InvalidDataException("Cached candidate is older than or diverges from the native checkpoint; refusing overwrite");
        }
        private static byte[] ReadBounded(string path)
        {
            if (new FileInfo(path).Length > MaxNativeBytes) throw new InvalidDataException("Native file exceeds export budget");
            return File.ReadAllBytes(path);
        }
        private static void CheckIdentity(byte[] bytes, string character, string hash, string snapshot)
        {
            if (bytes.Length > 4096) throw new InvalidDataException("Oversized export identity");
            var m = BinaryData.Map(BinaryData.Decode(bytes));
            PersonalDataRules.Keys(m, "character", "native", "checkpoint");
            if (BinaryData.Text(m["character"]) != character || BinaryData.Text(m["native"]) != hash || BinaryData.Text(m["checkpoint"]) != snapshot)
                throw new InvalidDataException("Export identity does not match the authorized snapshot");
        }
        // Bytes have already been serialized and round-trip validated on the game thread.
        // Only filesystem/hash operations run here. Never invoke Unity or ES3 from a worker.
        public static string Commit(string nativePath, string identityPath, string journalPath, string backupDirectory,
            string character, string snapshot, string expectedNativeHash, string expectedIdentityHash,
            byte[] prepared, Func<bool>? current = null, Action<string>? fault = null)
        {
            if (!Guid.TryParseExact(character, "N", out _) || !BinaryData.IsHash(snapshot) || !BinaryData.IsHash(expectedNativeHash) ||
                prepared.Length == 0 || prepared.Length > MaxNativeBytes) throw new InvalidDataException("Invalid native export request");
            string targetHash = BinaryData.Hash(prepared), temp = nativePath + "." + Guid.NewGuid().ToString("N") + ".export.tmp";
            bool published = false, journalWritten = false;
            void CheckCurrent() { if (current != null && !current()) throw new IOException("Export session ended; original native save retained"); }
            void CheckSource()
            {
                CheckCurrent();
                if (BinaryData.Hash(ReadBounded(nativePath)) != expectedNativeHash || BinaryData.Hash(File.ReadAllBytes(identityPath)) != expectedIdentityHash)
                    throw new IOException("Native save or character identity changed during export; refusing overwrite");
            }
            try
            {
                CheckSource();
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(prepared, 0, prepared.Length); stream.Flush(true); }
                fault?.Invoke("staged");
                if (BinaryData.Hash(ReadBounded(temp)) != targetHash) throw new InvalidDataException("Staged native file checksum mismatch");
                CheckSource();
                Directory.CreateDirectory(backupDirectory);
                SnapshotStore.AtomicWrite(Path.Combine(backupDirectory, "Player.before.es3"), ReadBounded(nativePath));
                foreach (string suffix in new[] { ".bak", ".bac" })
                    if (File.Exists(nativePath + suffix)) SnapshotStore.AtomicWrite(Path.Combine(backupDirectory, "Player.before.es3" + suffix), ReadBounded(nativePath + suffix));
                byte[] identity = BinaryData.Encode(new Dictionary<string, object?> { ["character"] = character, ["native"] = targetHash, ["checkpoint"] = snapshot });
                SnapshotStore.AtomicWrite(journalPath, BinaryData.Encode(new Dictionary<string, object?> {
                    ["format"] = 1L, ["character"] = character, ["snapshot"] = snapshot, ["source"] = expectedNativeHash, ["target"] = targetHash, ["identity"] = identity }));
                journalWritten = true;
                fault?.Invoke("before-publish"); CheckSource();
                if (BinaryData.Hash(ReadBounded(temp)) != targetHash) throw new InvalidDataException("Staged native file changed before publication");
                File.Replace(temp, nativePath, Path.Combine(backupDirectory, "Player.replaced.es3")); published = true;
                fault?.Invoke("published");
                if (BinaryData.Hash(ReadBounded(nativePath)) != targetHash) throw new IOException("Published native file changed unexpectedly; keep all recovery copies");
                // Only a completely validated native candidate can replace the game's backups.
                SnapshotStore.AtomicWrite(nativePath + ".bak", prepared);
                SnapshotStore.AtomicWrite(nativePath + ".bac", prepared);
                fault?.Invoke("backups-written");
                SnapshotStore.AtomicWrite(identityPath, identity);
                File.Delete(journalPath);
                return targetHash;
            }
            catch (Exception e)
            {
                if (!published && journalWritten) { try { File.Delete(journalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                throw new NativeExportException(published ? "Native file was published; recovery journal retained: " + e.Message : "Native export aborted before publication: " + e.Message, published, e);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        // Reconcile only this caller-supplied slot and identity; journal never supplies filesystem paths.
        public static bool Reconcile(string nativePath, string identityPath, string journalPath, string character)
        {
            if (!File.Exists(journalPath)) return false;
            if (new FileInfo(journalPath).Length > 8192) throw new InvalidDataException("Oversized native export journal");
            var m = BinaryData.Map(BinaryData.Decode(File.ReadAllBytes(journalPath)));
            PersonalDataRules.Keys(m, "format", "character", "snapshot", "source", "target", "identity");
            string source = BinaryData.Text(m["source"]), target = BinaryData.Text(m["target"]), snapshot = BinaryData.Text(m["snapshot"]);
            if (BinaryData.Number(m["format"]) != 1 || BinaryData.Text(m["character"]) != character || !BinaryData.IsHash(source) || !BinaryData.IsHash(target) || !BinaryData.IsHash(snapshot))
                throw new InvalidDataException("Invalid native export journal identity");
            byte[] identity = (byte[])m["identity"]!; CheckIdentity(identity, character, target, snapshot);
            string actual = BinaryData.Hash(ReadBounded(nativePath));
            if (actual == target)
            {
                byte[] bytes = ReadBounded(nativePath);
                SnapshotStore.AtomicWrite(nativePath + ".bak", bytes); SnapshotStore.AtomicWrite(nativePath + ".bac", bytes);
                SnapshotStore.AtomicWrite(identityPath, identity);
                File.Delete(journalPath); return true;
            }
            if (actual == source) { File.Delete(journalPath); return false; }
            throw new InvalidDataException("Native file differs from both export candidates; no recovery file was overwritten");
        }
    }
}
