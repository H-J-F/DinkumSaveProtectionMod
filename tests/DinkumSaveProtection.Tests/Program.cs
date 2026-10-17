using DinkumSaveProtection.Core;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var root = Path.Combine(Path.GetTempPath(), "dinkum-protection-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var character = Guid.NewGuid().ToString("N"); var session = Guid.NewGuid().ToString("N");
Dictionary<string, byte[]> Modules(int value = 0) => Snapshot.Required.ToDictionary(k => k, k => BinaryData.Encode(new Dictionary<string, object?> { ["value"] = k == "assets" ? (long)value : 0L, ["module"] = k }));
Capture Make(int value, Snapshot? previous = null, string? useSession = null) => Capture.Create(character, useSession ?? session, previous?.Session == (useSession ?? session) ? previous.Sequence + 1 : 1, previous, "test-build", Modules(value))!;
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException) { return; } throw new Exception("Expected an IO/validation failure"); }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
Dictionary<string, object?> Raw(int value) => Modules(value).ToDictionary(p => p.Key, p => BinaryData.Decode(p.Value), StringComparer.Ordinal);
void Drain(CheckpointWriter writer)
{ if (!SpinWait.SpinUntil(() => writer.Idle, TimeSpan.FromSeconds(5))) throw new Exception("Writer did not finish"); }
try
{
    Test("canonical binary format, deep copy, large balances, validation bounds", () =>
    {
        var a = new Dictionary<string, object?> { ["z"] = ulong.MaxValue, ["a"] = new object?[] { -1L, 123L, .5, true, "中文", null } };
        var b = new Dictionary<string, object?> { ["a"] = a["a"], ["z"] = ulong.MaxValue };
        Check(BinaryData.Encode(a).SequenceEqual(BinaryData.Encode(b)));
        var encoded = BinaryData.Encode(a); Check((ulong)BinaryData.Map(BinaryData.Decode(encoded))["z"]! == ulong.MaxValue);
        Throws(() => BinaryData.Decode(encoded.Concat(new byte[] { 1 }).ToArray()));
        Throws(() => BinaryData.Decode(new byte[] { 6, 255, 255, 255, 127 }));
        Throws(() => BinaryData.Encode(double.NaN));
    });
    Test("idle audits cause zero new versions and zero disk writes", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "idle")); var c = Make(1); store.Commit(c); long before = store.Writes;
        for (int i = 0; i < 120; i++) Check(Capture.Create(character, session, 2, c.Manifest, "test-build", Modules(1)) == null);
        Check(store.Writes == before);
    });
    Test("notification debounce, continuous activity deadline and local audit", () =>
    {
        var s = new ChangeSchedule(); s.Dirty(0); Check(!s.Due(.2)); Check(s.Due(.25)); s.Captured(.25);
        for (int i = 0; i < 10; i++) s.Dirty(.3 + i * .1);
        Check(!s.Due(1.2)); Check(s.Due(1.3)); s.Captured(1.3); Check(!s.Due(6.29)); Check(s.Due(6.3));
    });
    Test("receiver acknowledges only complete disk committed snapshots", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "host")); var receiver = new ReplicaReceiver(store, character); var first = Make(1);
        var need = receiver.Offer(first.Manifest.Serialize()); Check((string)need["kind"]! == "need");
        int remaining = first.Blocks.Count; foreach (var block in first.Blocks)
        { var reply = receiver.Block(first.Manifest.Id, block.Key, block.Value); remaining--; Check((string)reply["kind"]! == (remaining == 0 ? "ack" : "progress")); if (remaining > 0) Check(store.Latest(character) == null); }
        Check(store.Latest(character)!.Id == first.Manifest.Id);
        long writes = store.Writes; Check((string)receiver.Offer(first.Manifest.Serialize())["kind"]! == "ack"); Check(store.Writes == writes);
        var next = Make(2, first.Manifest); var changed = receiver.Offer(next.Manifest.Serialize()); var missing = BinaryData.List(changed["hashes"]); Check(missing.Length == 1, "Only assets must upload");
        string hash = BinaryData.Text(missing[0]); Check((string)receiver.Block(next.Manifest.Id, hash, next.Blocks[hash])["kind"]! == "ack");
        Check(store.Latest(character)!.Id == next.Manifest.Id); Check((string)receiver.Offer(first.Manifest.Serialize())["kind"]! == "newer");
        Throws(() => receiver.Block(next.Manifest.Id, hash, new byte[] { 1 }));
    });
    Test("partial update and interrupted temporary files preserve last complete version", () =>
    {
        string path = Path.Combine(root, "interrupted"); var store = new SnapshotStore(path); var a = Make(1); store.Commit(a); var b = Make(2, a.Manifest);
        File.WriteAllBytes(Path.Combine(path, "versions", b.Manifest.Id + ".snap.tmp"), b.Manifest.Serialize());
        Throws(() => store.Commit(b.Manifest)); Check(new SnapshotStore(path).Latest(character)!.Id == a.Manifest.Id);
        var receiver = new ReplicaReceiver(store, character); receiver.Offer(b.Manifest.Serialize());
        Throws(() => receiver.Block(b.Manifest.Id, b.Manifest.Modules["assets"], new byte[] { 0 })); Check(store.Latest(character)!.Id == a.Manifest.Id);
    });
    Test("corruption falls back to complete older snapshot and can be repaired", () =>
    {
        string path = Path.Combine(root, "corruption"); var store = new SnapshotStore(path); var a = Make(1); store.Commit(a); var b = Make(2, a.Manifest); store.Commit(b);
        File.WriteAllBytes(Path.Combine(path, "blocks", b.Manifest.Modules["assets"] + ".blk"), new byte[] { 0 });
        Check(store.Latest(character)!.Id == a.Manifest.Id); store.Commit(b); Check(store.Latest(character)!.Id == b.Manifest.Id);
        File.WriteAllBytes(Path.Combine(path, "versions", b.Manifest.Id + ".snap"), new byte[] { 0 });
        store.Commit(b); Check(store.Find(b.Manifest.Id) != null);
    });
    Test("single active upload, latest pending, acknowledgement loss retry and eventual silence", () =>
    {
        var a = Make(1); var b = Make(2, a.Manifest); var c = Make(3, b.Manifest); var q = new UploadQueue(); q.Enqueue(a.Manifest); Check(q.Poll(0)!.Id == a.Manifest.Id);
        q.Enqueue(b.Manifest); q.Enqueue(c.Manifest); Check(q.Poll(1) == null); Check(q.Pending!.Id == c.Manifest.Id);
        Check(q.Poll(2)!.Id == a.Manifest.Id); q.Ack("wrong"); Check(q.Active != null); q.Ack(a.Manifest.Id); Check(q.Poll(2.1)!.Id == c.Manifest.Id); q.Ack(c.Manifest.Id);
        for (int i = 0; i < 100; i++) Check(q.Poll(i + 3) == null);
        q.Enqueue(c.Manifest); Check(q.Poll(200) == null);
        q.Enqueue(b.Manifest); for (int i = 0; i < 10; i++) q.Poll(201 + i * 100); Check(q.Poll(10000) == null);
    });
    Test("newest same-session version wins; clocks are not part of sorting", () =>
    {
        var a = Make(1); var b = Make(2, a.Manifest); Check(Versions.Compare(a.Manifest, b.Manifest, _ => null) == VersionOrder.RightNewer);
        var other = Make(3, null, Guid.NewGuid().ToString("N")); Check(Versions.Compare(a.Manifest, other.Manifest, _ => null) == VersionOrder.Conflict);
        var wrongCharacter = new Snapshot(Guid.NewGuid().ToString("N"), session, 1, "", "test-build", new Dictionary<string, string>(a.Manifest.Modules));
        Check(Versions.Compare(a.Manifest, wrongCharacter, _ => null) == VersionOrder.Conflict);
    });
    Test("branch selection retains both histories", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "branches")); var a = Make(1); var b = Make(3, null, Guid.NewGuid().ToString("N")); store.Commit(a); store.Commit(b);
        Throws(() => store.Latest(character)); store.SelectBranch(a.Manifest.Id); Check(store.Latest(character)!.Id == a.Manifest.Id); Check(store.Find(b.Manifest.Id) != null);
    });
    Test("branch replacement needs explicit selection and keeps rejected branch data", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "explicit-branch")); var a = Make(1); store.Commit(a); var branch = Make(9, null, Guid.NewGuid().ToString("N"));
        Throws(() => new ReplicaReceiver(store, character).Offer(branch.Manifest.Serialize()));
        var selected = new ReplicaReceiver(store, character, true); selected.Offer(branch.Manifest.Serialize());
        foreach (var hash in store.Missing(branch.Manifest)) selected.Block(branch.Manifest.Id, hash, branch.Blocks[hash]);
        Check(store.Latest(character)!.Id == branch.Manifest.Id); Check(store.Read(a.Manifest).Manifest.Id == a.Manifest.Id);
    });
    Test("disk commit failure cannot publish a new version", () =>
    {
        var path = Path.Combine(root, "disk-failure"); var store = new SnapshotStore(path); var a = Make(1); store.Commit(a); var b = Make(2, a.Manifest);
        var obstruction = Path.Combine(path, "versions", b.Manifest.Id + ".snap"); Directory.CreateDirectory(obstruction);
        Throws(() => store.Commit(b)); Directory.Delete(obstruction); Check(store.Latest(character)!.Id == a.Manifest.Id);
    });
    Test("async capture bytes do not share mutable source arrays", () =>
    {
        var source = Modules(5); var capture = Capture.Create(character, session, 1, null, "test-build", source)!;
        string hash = capture.Manifest.Modules["assets"]; source["assets"][0] = 255;
        Check(BinaryData.Hash(capture.Blocks[hash]) == hash);
    });
    Test("a new session can order against a distant checkpoint without reading every frame", () =>
    {
        var first = Make(1); var far = new Snapshot(character, session, 50000, first.Manifest.Id, "test-build", new Dictionary<string, string>(first.Manifest.Modules));
        var child = Make(2, far, Guid.NewGuid().ToString("N"));
        Check(Versions.Compare(first.Manifest, child.Manifest, id => id == far.Id ? far : null) == VersionOrder.RightNewer);
    });
    Test("retention preserves checkpoint, ancestry headers and shared blocks", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "retention")); var first = Make(0); store.Commit(first); var prev = first;
        for (int i = 1; i <= 40; i++) { prev = Make(i, prev.Manifest); store.Commit(prev); }
        var nextSession = Guid.NewGuid().ToString("N"); var latest = Make(41, prev.Manifest, nextSession); store.Commit(latest); store.Prune(character, first.Manifest.Id, 32); store.CollectUnreferencedBlocks();
        Check(store.ValidVersions(character).Length == 33); Check(store.Read(first.Manifest).Manifest.Id == first.Manifest.Id); Check(store.Latest(character)!.Id == latest.Manifest.Id);
        Check(Versions.Compare(first.Manifest, latest.Manifest, store.Find) == VersionOrder.RightNewer);
        var pedia = first.Manifest.Modules["pedia"]; Check(store.HasBlock(pedia));
    });
    Test("expanded arrays, custom values, cursor item and extension bytes round trip without truncation", () =>
    {
        var payload = new Dictionary<string, object?> { ["inventory"] = Enumerable.Range(0, 88).Select(i => (object?)new object?[] { (long)i, 1234L }).ToArray(), ["cursor"] = new object?[] { 99L, 7L }, ["extension"] = new byte[] { 9, 8, 7 } };
        var restored = BinaryData.Map(BinaryData.Decode(BinaryData.Encode(payload))); Check(BinaryData.List(restored["inventory"]).Length == 88); Check(BinaryData.Number(BinaryData.List(restored["cursor"])[1]) == 7);
        Check(((byte[])restored["extension"]!).SequenceEqual(new byte[] { 9, 8, 7 }));
    });
    Test("bounded reliable fragments reject corruption, wrong order and oversized input", () =>
    {
        var data = BinaryData.Encode(Modules(2).ToDictionary(p => p.Key, p => (object?)p.Value)); var hash = BinaryData.Hash(data); var f = new FragmentBuffer();
        Check(f.Add(hash, data.Length, 0, data.Take(100).ToArray(), 0) == null);
        Check(f.Add(hash, data.Length, 100, data.Skip(100).ToArray(), 1)!.SequenceEqual(data));
        Throws(() => f.Add(hash, BinaryData.MaxBytes + 1, 0, new byte[] { 1 }, 0));
        f.Add(hash, data.Length, 0, data.Take(100).ToArray(), 0); Throws(() => f.Add(hash, data.Length, 101, new byte[] { 1 }, 1));
        Throws(() => f.Add(hash, 1, 0, new byte[] { 1 }, 0));
    });
    Test("durable resume matrix survives client restart and distinguishes host sessions", () =>
    {
        var id = Make(1).Manifest.Id;
        foreach (var reason in new[] { ExitReason.Running, ExitReason.Unexpected, ExitReason.Voluntary })
        {
            var reloaded = ResumeState.Parse(new ResumeState(character, "steam:123", "mod:sessionA", reason, id).Serialize());
            Check(reloaded.Allows(character, "steam:123", "mod:sessionA"));
            Check(reloaded.Allows(character, "steam:123", "mod:sessionB") == (reason != ExitReason.Voluntary));
            Check(reloaded.Allows(character, "steam:456", "mod:sessionA") == (reason != ExitReason.Voluntary));
            Check(reloaded.Allows(character, "", "") == (reason != ExitReason.Voluntary));
            Check(!reloaded.Allows(Guid.NewGuid().ToString("N"), "steam:123", "mod:sessionA"));
        }
        var vanilla = ResumeState.Parse(new ResumeState(character, "123", "lobby:789", ExitReason.Voluntary, id).Serialize());
        Check(vanilla.Allows(character, "123", "lobby:789")); Check(!vanilla.Allows(character, "123", "lobby:790"));
        Check(!new ResumeState(character, "", "", ExitReason.Voluntary, id).Allows(character, "", ""));
    });
    Test("passive-disconnect cleanup cannot reclassify an unexpected exit", () =>
    {
        var intent = new ExitIntent(); intent.Disconnected(); Check(!intent.TryVoluntary()); Check(intent.Reason == ExitReason.Unexpected);
        var voluntary = new ExitIntent(); Check(voluntary.TryVoluntary()); voluntary.Disconnected(); Check(voluntary.Reason == ExitReason.Voluntary);
        voluntary.CancelVoluntary(); Check(voluntary.Reason == ExitReason.Running);
    });
    Test("corrupt resume never falls back to a previous running record", () =>
    {
        string path = Path.Combine(root, "resume-fail-closed", "resume.bin");
        Check(ResumeState.Read(path) == null);
        SnapshotStore.AtomicWrite(path, new ResumeState(character, "h", "a", ExitReason.Running, "").Serialize());
        SnapshotStore.AtomicWrite(path, new ResumeState(character, "h", "a", ExitReason.Voluntary, "").Serialize());
        File.WriteAllBytes(path, new byte[] { 7 });
        Throws(() => ResumeState.Read(path));
        Check(ResumeState.Parse(File.ReadAllBytes(path + ".previous")).Reason == ExitReason.Running);
    });
    Test("blocked disk does not block caller; latest pending capture precedes voluntary marker", () =>
    {
        string path = Path.Combine(root, "async-bounded"), resumeFile = Path.Combine(path, "resume.bin");
        var store = new SnapshotStore(path);
        using var gate = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        int calls = 0;
        var writer = new CheckpointWriter(store, character, session, "test-build", null, resumeFile, "h", "s", beforeWrite: () =>
        { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Fake disk timeout"); } });
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew(); writer.Submit(Raw(1)); Check(clock.ElapsedMilliseconds < 1000);
            Check(entered.Wait(TimeSpan.FromSeconds(2))); Check(!writer.Idle);
            writer.Submit(Raw(2)); writer.Submit(Raw(3)); long final = writer.Seal(ExitReason.Voluntary);
            Check(!writer.Completed(final)); Check(writer.Submit(Raw(4)) == 0);
            gate.Set(); Drain(writer); Check(writer.Completed(final));
            var versions = store.ValidVersions(character); Check(versions.Length == 2); Check(writer.Head!.Sequence == 2);
            var assets = BinaryData.Map(BinaryData.Decode(store.Read(writer.Head).Blocks[writer.Head.Modules["assets"]])); Check(BinaryData.Number(assets["value"]) == 3);
            var record = ResumeState.Read(resumeFile)!; Check(record.Snapshot == writer.Head.Id && record.Reason == ExitReason.Voluntary);
            Check(versions.Any(v => v.Id == writer.Head.Parent));
        }
        finally { gate.Set(); Drain(writer); }
    });
    Test("asynchronous idle audits do not rewrite versions or resume metadata", () =>
    {
        string path = Path.Combine(root, "async-idle"), file = Path.Combine(path, "resume.bin"); var store = new SnapshotStore(path);
        var writer = new CheckpointWriter(store, character, session, "test-build", null, file, "h", "s");
        writer.Submit(Raw(1)); Drain(writer); Check(writer.Error == null); long writes = store.Writes;
        var stamp = File.GetLastWriteTimeUtc(file);
        for (int i = 0; i < 10; i++) { writer.Submit(Raw(1)); Drain(writer); }
        Check(store.Writes == writes && File.GetLastWriteTimeUtc(file) == stamp && !File.Exists(file + ".previous"));
    });
    Test("native save after voluntary seal preserves reason and updates checkpoint only after commit", () =>
    {
        string path = Path.Combine(root, "native-after-exit"), file = Path.Combine(path, "resume.bin"); var store = new SnapshotStore(path);
        string checkpointHash = "", checkpointId = "";
        var writer = new CheckpointWriter(store, character, session, "test-build", null, file, "h", "s", checkpoint: (s, h) => { checkpointHash = h!; checkpointId = s.Id; });
        writer.Submit(Raw(1)); Drain(writer); writer.Seal(ExitReason.Voluntary); Drain(writer);
        string hash = BinaryData.Hash(new byte[] { 4 }); var ticket = writer.Submit(Raw(2), hash); Drain(writer);
        Check(writer.Completed(ticket)); Check(checkpointHash == hash && checkpointId == writer.Head!.Id);
        Check(ResumeState.Read(file)!.Reason == ExitReason.Voluntary);
    });
    Test("disk failure cancels exit authorization and retry keeps prior complete data", () =>
    {
        string path = Path.Combine(root, "async-failure"), file = Path.Combine(path, "resume.bin"); var store = new SnapshotStore(path);
        var good = Make(1); store.Commit(good);
        SnapshotStore.AtomicWrite(file, new ResumeState(character, "h", "s", ExitReason.Running, good.Manifest.Id).Serialize());
        bool fail = true;
        var writer = new CheckpointWriter(store, character, session, "test-build", good.Manifest, file, "h", "s", beforeWrite: () => { if (fail) throw new IOException("Fake disk full"); });
        writer.Submit(Raw(2)); var ticket = writer.Seal(ExitReason.Voluntary); Drain(writer);
        Check(writer.Error != null && !writer.Completed(ticket)); Check(ResumeState.Read(file)!.Snapshot == good.Manifest.Id);
        Check(store.Read(good.Manifest).Manifest.Id == good.Manifest.Id);
        fail = false; writer.Unseal(); writer.Submit(Raw(3)); Drain(writer); Check(writer.Error == null);
        Check(ResumeState.Read(file)!.Reason == ExitReason.Running);
    });
    Test("manifest without durable resume publication cannot replace authorized head", () =>
    {
        string path = Path.Combine(root, "pointer-failure"), file = Path.Combine(path, "resume.bin"); var store = new SnapshotStore(path);
        var good = Make(1); store.Commit(good); var state = new ResumeState(character, "h", "s", ExitReason.Running, good.Manifest.Id);
        SnapshotStore.AtomicWrite(file, state.Serialize());
        var writer = new CheckpointWriter(store, character, session, "test-build", good.Manifest, file, "h", "s");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        { writer.Submit(Raw(2)); Drain(writer); Check(writer.Error != null); Check(writer.Head!.Id == good.Manifest.Id); }
        Check(ResumeState.Read(file)!.Snapshot == good.Manifest.Id);
        Check(store.ValidVersions(character).Length == 2); // orphan candidate is retained, not authorized
        writer.Submit(Raw(3)); Drain(writer); Check(writer.Error == null && writer.Head!.Sequence == 3);
        Check(store.Latest(character)!.Id == writer.Head!.Id); // no reused sequence creates a false branch
    });
    Test("new native baseline isolates abandoned branch and keeps old blocks", () =>
    {
        string path = Path.Combine(root, "new-baseline"), file = Path.Combine(path, "resume.bin"); var store = new SnapshotStore(path);
        var old = Make(1); store.Commit(old); store.SelectBranch(old.Manifest.Id);
        var writer = new CheckpointWriter(store, character, Guid.NewGuid().ToString("N"), "test-build", null, file, "h", "new", true);
        writer.Submit(Raw(2)); Drain(writer); Check(writer.Error == null);
        Check(writer.Head!.Parent == "" && store.Latest(character)!.Id == writer.Head.Id);
        Check(store.Read(old.Manifest).Manifest.Id == old.Manifest.Id);
        Check(ResumeState.Read(file)!.Snapshot == writer.Head.Id);
    });
    Test("independent retired and current writers cannot change each other's stores", () =>
    {
        string oldPath = Path.Combine(root, "old-slot"), newPath = Path.Combine(root, "new-slot");
        using var gate = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        var oldStore = new SnapshotStore(oldPath); var newStore = new SnapshotStore(newPath);
        var oldWriter = new CheckpointWriter(oldStore, character, session, "test-build", null, Path.Combine(oldPath, "resume.bin"), "h", "a", beforeWrite: () => { entered.Set(); if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Timeout"); });
        var newWriter = new CheckpointWriter(newStore, character, Guid.NewGuid().ToString("N"), "test-build", null, Path.Combine(newPath, "resume.bin"), "h", "b");
        try
        {
            oldWriter.Submit(Raw(1)); Check(entered.Wait(TimeSpan.FromSeconds(2))); newWriter.Submit(Raw(2)); Drain(newWriter); Check(newWriter.Error == null);
            string current = newWriter.Head!.Id; gate.Set(); Drain(oldWriter); Check(newWriter.Head.Id == current && oldWriter.Head!.Id != current);
        }
        finally { gate.Set(); Drain(oldWriter); }
    });
    Test("semantic invalid fields cannot be mistaken for valid hash-checked data", () =>
    {
        var map = BinaryData.Map(BinaryData.Decode(BinaryData.Encode(new Dictionary<string, object?> { ["item"] = 9999L, ["stack"] = 1L })));
        Throws(() => PersonalDataRules.Item(BinaryData.Number(map["item"]), BinaryData.Number(map["stack"]), 10, _ => true));
        Throws(() => PersonalDataRules.Item(2, -1, 10, _ => true)); Throws(() => PersonalDataRules.Item(2, 1, 10, _ => false));
        PersonalDataRules.Item(-1, 0, 10, _ => false); PersonalDataRules.Item(2, 0, 10, _ => true); // tools may have zero durability
        Throws(() => PersonalDataRules.Vitals(double.NaN, 100, 100)); Throws(() => PersonalDataRules.Vitals(1, double.MaxValue, 100));
        Throws(() => PersonalDataRules.Keys(new Dictionary<string, object?> { ["a"] = 1L }, "a", "b"));
    });
    Test("encoder rejects aggregate element budgets that decoder would reject", () =>
    {
        var list = Enumerable.Repeat<object?>(0L, 100000).ToArray();
        Throws(() => BinaryData.Encode(new object?[] { list, list }));
        Throws(() => new Capture(Make(1).Manifest, new Dictionary<string, byte[]> { [BinaryData.Hash(new byte[] { 0 })] = new byte[25 * 1024 * 1024] }));
    });
    Test("adapter registry snapshots and main-thread contract isolate registration changes", () =>
    {
        DinkumSaveProtection.PlayerDataAdapters.BindMainThread();
        var a = new TestAdapter("tests.first"); DinkumSaveProtection.PlayerDataAdapters.Register(a);
        var snapshot = DinkumSaveProtection.PlayerDataAdapters.All;
        DinkumSaveProtection.PlayerDataAdapters.Register(new TestAdapter("tests.second"));
        Check(snapshot.Length == 1 && DinkumSaveProtection.PlayerDataAdapters.All.Length == 2);
        bool rejected = false;
        System.Threading.Tasks.Task.Run(() => { try { DinkumSaveProtection.PlayerDataAdapters.Register(new TestAdapter("tests.worker")); } catch (InvalidOperationException) { rejected = true; } }).GetAwaiter().GetResult();
        Check(rejected);
    });
    Test("explicit branch replacement permission is consumed after the first complete commit", () =>
    {
        var store = new SnapshotStore(Path.Combine(root, "one-branch-reset")); var old = Make(1); store.Commit(old);
        var reset = Make(2, null, Guid.NewGuid().ToString("N")); var receiver = new ReplicaReceiver(store, character, true);
        receiver.Offer(reset.Manifest.Serialize()); foreach (var hash in store.Missing(reset.Manifest)) receiver.Block(reset.Manifest.Id, hash, reset.Blocks[hash]);
        var unexpectedBranch = Make(3, null, Guid.NewGuid().ToString("N")); Throws(() => receiver.Offer(unexpectedBranch.Manifest.Serialize()));
        Check(store.Read(old.Manifest).Manifest.Id == old.Manifest.Id);
    });
    Test("latest pending capture keeps a pending native hash without dropping current data", () =>
    {
        string path = Path.Combine(root, "pending-native"); var store = new SnapshotStore(path);
        using var gate = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim(); int calls = 0;
        string seen = ""; var writer = new CheckpointWriter(store, character, session, "test-build", null, Path.Combine(path, "resume.bin"), "h", "s",
            checkpoint: (_, hash) => seen = hash!, beforeWrite: () => { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Timeout"); } });
        try
        {
            writer.Submit(Raw(1)); Check(entered.Wait(TimeSpan.FromSeconds(2))); string hash = BinaryData.Hash(new byte[] { 2 });
            writer.Submit(Raw(2), hash); writer.Submit(Raw(3)); gate.Set(); Drain(writer); Check(seen == hash);
            var head = writer.Head!; Check(BinaryData.Number(BinaryData.Map(BinaryData.Decode(store.Read(head).Blocks[head.Modules["assets"]]))["value"]) == 3);
        }
        finally { gate.Set(); Drain(writer); }
    });
    Test("writer retention preserves native checkpoint and resume pointer through background collection", () =>
    {
        string path = Path.Combine(root, "writer-retention"); var store = new SnapshotStore(path); string file = Path.Combine(path, "resume.bin");
        var writer = new CheckpointWriter(store, character, session, "test-build", null, file, "h", "s");
        writer.Submit(Raw(0), BinaryData.Hash(new byte[] { 0 })); Drain(writer); var first = writer.Head!;
        for (int i = 1; i <= 40; i++) { writer.Submit(Raw(i)); Drain(writer); Check(writer.Error == null); }
        Check(store.ValidVersions(character).Length == 33 && ResumeState.Read(file)!.Snapshot == writer.Head!.Id);
        var restarted = new CheckpointWriter(store, character, Guid.NewGuid().ToString("N"), "test-build", writer.Head, file, "h", "s", pinnedCheckpoint: first.Id);
        restarted.Submit(Raw(41)); Drain(restarted); Check(restarted.Error == null);
        Check(store.Read(first).Manifest.Id == first.Id && store.Latest(character)!.Id == restarted.Head!.Id);
    });
    NativeValidationFixture.Run(Test);
    RestoreLifecycleFixture.Run(Test);
    NativeExportFixture.Run(Test, root, character);
    Test("native JSON property inspection handles nesting escapes and array schema paths", () =>
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"__type\":\"T\",\"npc\":[{\"progress\":12},{\"progress\":2}],\"a/b\":{\"x\\u0061\":true},\"number\":-1.5e+2}");
        var paths = JsonPropertyPaths.Read(json,System.Text.Encoding.UTF8);
        Check(paths.Contains("/npc[]/progress") && paths.Contains("/a~1b/xa") && !paths.Contains("/__type"));
        Check(JsonPropertyPaths.Read(System.Text.Encoding.UTF8.GetPreamble().Concat(json).ToArray(),System.Text.Encoding.UTF8).SetEquals(paths));
        Throws(() => JsonPropertyPaths.Read(System.Text.Encoding.UTF8.GetBytes("{\"a\":1.}"),System.Text.Encoding.UTF8));
    });
    Test("native export cannot replace a newer or divergent valid native checkpoint", () =>
    {
        var a = Make(1); var b = Make(2,a.Manifest); var branch = Make(3,null,Guid.NewGuid().ToString("N"));
        var versions = new[] { a.Manifest,b.Manifest,branch.Manifest }.ToDictionary(s => s.Id);
        Snapshot? Find(string id) => versions.TryGetValue(id,out var s) ? s : null;
        NativeExportTransaction.RequireHistory(b.Manifest,a.Manifest.Id,a.Manifest,Find);
        NativeExportTransaction.RequireHistory(b.Manifest,b.Manifest.Id,null,Find);
        Throws(() => NativeExportTransaction.RequireHistory(a.Manifest,b.Manifest.Id,b.Manifest,Find));
        Throws(() => NativeExportTransaction.RequireHistory(branch.Manifest,b.Manifest.Id,b.Manifest,Find));
        Throws(() => NativeExportTransaction.RequireHistory(b.Manifest,a.Manifest.Id,null,Find));
    });
    Test("native save policy preserves all previous allow/block combinations", () =>
    {
        for (int flags = 0; flags < 64; flags++)
        {
            bool format = (flags & 1) != 0, initialized = (flags & 2) != 0, complete = (flags & 4) != 0;
            bool blocked = (flags & 8) != 0, restoring = (flags & 16) != 0, unexpected = (flags & 32) != 0;
            bool previous = !format || !blocked && (!initialized || complete && !restoring && !unexpected);
            Check((NativeSavePolicy.Decide(format, initialized, complete, blocked, restoring, unexpected) == NativeSaveDecision.Allow) == previous);
        }
    });
    Test("host shutdown followed by save-and-return remains an unexpected client exit", () =>
    {
        var intent = new ExitIntent(); intent.Disconnected(); Check(!intent.TryVoluntary());
        var decision = NativeSavePolicy.Decide(true, true, true, false, false, intent.Reason == ExitReason.Unexpected);
        Check(decision == NativeSaveDecision.UnexpectedDisconnect);
        Check(!NativeSavePolicy.Message(decision).Contains("failed") && NativeSavePolicy.Message(decision).Contains("cache retained"));
        var saved = new ResumeState(character, "host", "sessionA", intent.Reason, Make(1).Manifest.Id);
        Check(ResumeState.Parse(saved.Serialize()).Allows(character, "host", "sessionB"));
    });
    Test("voluntary connected exit still allows its native save", () =>
    {
        var intent = new ExitIntent(); Check(intent.TryVoluntary()); intent.Disconnected();
        Check(intent.Reason == ExitReason.Voluntary && NativeSavePolicy.Decide(true, true, true, false, false, false) == NativeSaveDecision.Allow);
    });
    Test("real initialization and recovery blocks have specific diagnostic reasons", () =>
    {
        Check(NativeSavePolicy.Decide(true, true, true, true, false, true) == NativeSaveDecision.ProtectionPaused);
        Check(NativeSavePolicy.Decide(true, true, false, false, false, false) == NativeSaveDecision.InitializationIncomplete);
        Check(NativeSavePolicy.Decide(true, true, false, false, true, false) == NativeSaveDecision.Restoring);
    });
    if (args.Length > 0)
        Test("offline metadata: exact game build and patch targets exist (game code is never loaded)", () =>
        {
            string file = Path.Combine(args[0], "Dinkum_Data", "Managed", "Assembly-CSharp.dll");
            Check(BinaryData.Hash(File.ReadAllBytes(file)) == "5106da5d624aaedabb16159a9bdaa9ab3ce4d09eb992bd4759d881dd9d9bea24");
            using var stream = File.OpenRead(file); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
            var methods = reader.TypeDefinitions.GroupBy(h => reader.GetString(reader.GetTypeDefinition(h).Name)).ToDictionary(g => g.Key, g => g.SelectMany(h => reader.GetTypeDefinition(h).GetMethods()).Select(m => reader.GetString(reader.GetMethodDefinition(m).Name)).ToHashSet());
            foreach (var (type, method) in new[] { ("CustomNetworkManager", "DisconnectScreen_SaveAndReturnToMenu"), ("CustomNetworkManager", "OnClientDisconnect"), ("CustomNetworkManager", "OnServerDisconnect"), ("CustomNetworkManager", "OnClientConnect"), ("CustomNetworkManager", "OnStartHost"), ("CustomNetworkManager", "OnStopServer"), ("SaveLoad", "returnToMenu"), ("SaveLoad", "quitGame"), ("SaveAndLoad", "LoadInvForMultiplayerConnect"), ("SaveAndLoad", "SaveCoroutine"), ("SaveAndLoad", "LoadOverFrames"), ("CharMovement", "unlockClientOnLoad"), ("InventorySlot", "updateSlotContentsAndRefresh"), ("SteamLobby", "OnLobbyEntered") }) Check(methods[type].Contains(method), type + "." + method);
        });
    Console.WriteLine($"{passed} offline tests passed. No game execution, installation, or real save mutation.");
}
finally { Directory.Delete(root, true); }

sealed class TestAdapter(string id) : DinkumSaveProtection.IPlayerDataAdapter
{
    public string Id => id;
    public string Version => "1";
    public byte[] Capture() => new byte[] { 1 };
    public void Validate(byte[] data, string savedVersion) { }
    public void Restore(byte[] data, string savedVersion) { }
}
