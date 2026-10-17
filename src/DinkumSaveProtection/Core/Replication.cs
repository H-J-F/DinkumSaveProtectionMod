using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DinkumSaveProtection.Core
{
    // Transport independent. Acknowledgements exist only after a complete manifest commits.
    public sealed class ReplicaReceiver
    {
        private readonly SnapshotStore store;
        private readonly string character;
        private bool allowBranchReplacement;
        private Snapshot? pending;
        public ReplicaReceiver(SnapshotStore store, string character, bool allowBranchReplacement = false) { this.store = store; this.character = character; this.allowBranchReplacement = allowBranchReplacement; }
        public Dictionary<string, object?> Offer(byte[] bytes)
        {
            var next = Snapshot.Parse(bytes);
            if (next.Character != character) throw new InvalidDataException("Character mismatch");
            Snapshot? current;
            try { current = store.Latest(character); }
            catch (InvalidDataException) when (allowBranchReplacement) { current = null; }
            if (current != null && Versions.Compare(current, next, store.Find) == VersionOrder.Conflict && !allowBranchReplacement) throw new InvalidDataException("Unresolved remote save branch");
            if (current != null && Versions.Compare(current, next, store.Find) == VersionOrder.LeftNewer) return Message("newer", "manifest", current.Serialize());
            pending = next;
            return NeedOrCommit();
        }
        public Dictionary<string, object?> Block(string manifestId, string hash, byte[] bytes)
        {
            if (pending == null || pending.Id != manifestId || !pending.Modules.Values.Contains(hash)) throw new InvalidDataException("Unsolicited block");
            store.PutBlock(hash, bytes);
            if (store.Missing(pending).Length != 0) return Message("progress", "id", pending.Id);
            return NeedOrCommit();
        }
        private Dictionary<string, object?> NeedOrCommit()
        {
            var missing = store.Missing(pending!);
            if (missing.Length != 0) return Message("need", "id", pending!.Id, "hashes", missing.Cast<object?>().ToArray());
            store.Commit(pending!); if (allowBranchReplacement) { store.SelectBranch(pending!.Id); allowBranchReplacement = false; } return Message("ack", "id", pending!.Id);
        }
        public static Dictionary<string, object?> Message(string kind, params object?[] pairs)
        { var m = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind }; for (int i = 0; i < pairs.Length; i += 2) m.Add((string)pairs[i]!, pairs[i + 1]); return m; }
    }
    public sealed class UploadQueue
    {
        public Snapshot? Active { get; private set; }
        public Snapshot? Pending { get; private set; }
        public string Confirmed { get; private set; } = "";
        private int retries;
        private double retryAt = double.PositiveInfinity;
        public void Enqueue(Snapshot snapshot)
        {
            if (snapshot.Id == Confirmed || Active?.Id == snapshot.Id || Pending?.Id == snapshot.Id) return;
            Pending = snapshot;
        }
        public Snapshot? Poll(double now)
        {
            if (Active == null && Pending != null) { Active = Pending; Pending = null; retries = 0; retryAt = now + 2; return Active; }
            if (Active != null && now >= retryAt && retries < 4)
            { retries++; retryAt = retries == 4 ? double.PositiveInfinity : now + Math.Pow(2, retries + 1); return Active; }
            return null;
        }
        public void Progress(double now) { if (Active != null) retryAt = now + 8; }
        public void Ack(string id)
        { if (Active?.Id != id) return; Confirmed = id; Active = null; retryAt = double.PositiveInfinity; }
        public void Reset() { Active = null; Pending = null; Confirmed = ""; retries = 0; retryAt = double.PositiveInfinity; }
    }
    public sealed class FragmentBuffer
    {
        private string id = "";
        private int length, offset;
        private MemoryStream? stream;
        private double started;
        // One bounded, ordered message at a time per authenticated reliable connection.
        public byte[]? Add(string message, int total, int position, byte[] bytes, double now)
        {
            if (!BinaryData.IsHash(message) || total < 1 || total > BinaryData.MaxBytes || bytes.Length == 0 || position < 0 || position > total - bytes.Length) throw new InvalidDataException("Invalid fragment");
            if (position == 0) { stream?.Dispose(); stream = new MemoryStream(); id = message; length = total; offset = 0; started = now; }
            if (stream == null || id != message || total != length || position != offset || now - started > 300) { Clear(); throw new InvalidDataException("Out of order or expired fragment"); }
            stream.Write(bytes, 0, bytes.Length); offset += bytes.Length;
            if (offset != length) return null;
            var complete = stream.ToArray(); Clear();
            if (BinaryData.Hash(complete) != message) throw new InvalidDataException("Message checksum mismatch");
            return complete;
        }
        public void Clear() { stream?.Dispose(); stream = null; id = ""; offset = 0; }
    }
}
