using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DinkumSaveProtection.Core
{
    // One independent disk pump per player. Incoming DTOs are exclusively owned by the pump.
    // Network work never occupies this queue, and callers never wait on its Task.
    public sealed class CheckpointWriter
    {
        private readonly object sync = new object();
        private readonly SnapshotStore store;
        private readonly string character, session, build, resumePath, host, hostSession;
        private readonly Action<Snapshot, string?>? checkpoint;
        private readonly Action? beforeWrite;
        private Request? pending;
        private bool pumping, sealedWriter, newBranch;
        private ExitReason sealedReason;
        private Snapshot? head;
        private Exception? error;
        private long submitted, completed;
        private long sequence;
        private string pinned;
        private byte[]? lastState;
        private bool collected;
        private sealed class Request
        {
            internal Dictionary<string, object?>? Modules;
            internal ExitReason Reason;
            internal string? NativeHash;
            internal long Ticket;
        }
        public CheckpointWriter(SnapshotStore store, string character, string session, string build, Snapshot? head,
            string resumePath, string host, string hostSession, bool newBranch = false,
            Action<Snapshot, string?>? checkpoint = null, Action? beforeWrite = null, string pinnedCheckpoint = "")
        {
            this.store = store; this.character = character; this.session = session; this.build = build; this.head = head;
            this.resumePath = resumePath; this.host = host; this.hostSession = hostSession; this.newBranch = newBranch;
            this.checkpoint = checkpoint; this.beforeWrite = beforeWrite;
            sequence = head?.Session == session ? checked(head.Sequence + 1) : 1; pinned = pinnedCheckpoint;
        }
        public Snapshot? Head { get { lock (sync) return head; } }
        public Exception? Error { get { lock (sync) return error; } }
        public bool Idle { get { lock (sync) return !pumping; } }
        public bool Completed(long ticket) { lock (sync) return ticket > 0 && completed >= ticket && error == null; }
        public long Submit(Dictionary<string, object?> modules, string? nativeHash = null)
        {
            lock (sync)
            {
                if (sealedWriter && nativeHash == null) return 0;
                // A successful native save must not lose its checkpoint to a later routine capture.
                if (pending?.NativeHash != null && nativeHash == null) nativeHash = pending.NativeHash;
                pending = new Request { Modules = modules, Reason = sealedWriter ? sealedReason : ExitReason.Running, NativeHash = nativeHash, Ticket = ++submitted };
                Start(); return submitted;
            }
        }
        public long Seal(ExitReason reason)
        {
            lock (sync)
            {
                sealedWriter = true; sealedReason = reason;
                if (pending == null) pending = new Request();
                pending.Reason = reason; pending.Ticket = ++submitted;
                Start(); return submitted;
            }
        }
        public void Unseal() { lock (sync) sealedWriter = false; }
        private void Start()
        {
            if (pumping) return;
            pumping = true; error = null; _ = System.Threading.Tasks.Task.Run(Pump);
        }
        private void Pump()
        {
            while (true)
            {
                Request request; Snapshot? previous;
                lock (sync)
                {
                    if (pending == null) { pumping = false; return; }
                    request = pending; pending = null; previous = head;
                }
                try
                {
                    beforeWrite?.Invoke();
                    if (!collected) { store.CollectUnreferencedBlocks(); collected = true; }
                    Capture? capture = null;
                    if (request.Modules != null)
                    {
                        var modules = EncodeModules(request.Modules);
                        // Ensure encoder/decoder budgets agree before any candidate is published.
                        foreach (var bytes in modules.Values)
                        {
                            if (bytes.Length > BinaryData.MaxModuleBytes) throw new InvalidDataException("Module exceeds transport-safe limit");
                            BinaryData.Decode(bytes);
                        }
                        capture = Capture.Create(character, session, sequence,
                            previous, build, modules, newBranch);
                        if (capture != null) sequence = checked(sequence + 1); // never reuse an orphan manifest's sequence after a failed pointer write
                    }
                    var next = capture?.Manifest ?? previous;
                    if (capture != null)
                    {
                        store.Commit(capture);
                        if (newBranch) store.SelectBranch(capture.Manifest.Id);
                    }
                    var state = new ResumeState(character, host, hostSession, request.Reason, next?.Id ?? "").Serialize();
                    if (lastState == null || !lastState.SequenceEqual(state)) { SnapshotStore.AtomicWrite(resumePath, state); lastState = state; }
                    if (next != null && request.NativeHash != null) { checkpoint?.Invoke(next, request.NativeHash); pinned = next.Id; }
                    lock (sync) { head = next; completed = request.Ticket; error = null; }
                    if (capture != null)
                    {
                        newBranch = false;
                        // Only locally validated, authorized commits may drive retention.
                        try { store.Prune(character, pinned); } catch (IOException) { /* Keep extra versions on maintenance failure. */ }
                    }
                }
                catch (Exception e) { lock (sync) { error = e; pending = null; pumping = false; } return; }
            }
        }
        public static Dictionary<string, byte[]> EncodeModules(Dictionary<string, object?> modules)
        {
            return modules.ToDictionary(p => p.Key, p =>
            {
                if (p.Key != "environment" || !(p.Value is Dictionary<string, object?> environment) || !environment.TryGetValue("items", out var items) || !(items is object?[])) return BinaryData.Encode(p.Value);
                var copy = new Dictionary<string, object?>(environment, StringComparer.Ordinal) { ["items"] = BinaryData.Hash(BinaryData.Encode(items)) };
                return BinaryData.Encode(copy);
            }, StringComparer.Ordinal);
        }
    }
}
