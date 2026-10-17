using System.Reflection;
using DinkumSaveProtection.Core;
using DinkumSaveProtection.Runtime;

// In-memory stand-in for ES3's raw-record container. Real ES3/game code is not executed.
public sealed class ES3Settings
{
    public bool postprocessRawCachedData;
    public System.Text.Encoding encoding = System.Text.Encoding.UTF8;
    public ES3Settings(params Enum[] flags) { }
}
public sealed class FixtureES3Type { public Type type = typeof(object); }
public sealed class FixtureES3Data { public byte[] bytes = Array.Empty<byte>(), typed = Array.Empty<byte>(); public FixtureES3Type type = new(); }
public sealed class ES3File
{
    public static readonly Dictionary<string, ES3File> cachedFiles = new();
    public static Func<byte[], byte[]>? TransformOutput;
    private readonly Dictionary<string, FixtureES3Data> cache = new();
    private readonly ES3Settings settings;
    public ES3File(byte[] bytes, ES3Settings settings)
    {
        this.settings = settings;
        foreach (var p in BinaryData.Map(BinaryData.Decode(bytes)))
        {
            var m = BinaryData.Map(p.Value);
            cache[p.Key] = new FixtureES3Data { bytes = (byte[])m["bytes"]!, typed = (byte[])m["typed"]!, type = new FixtureES3Type { type = Type.GetType(BinaryData.Text(m["type"]), true)! } };
        }
    }
    public ES3File() { settings = new ES3Settings { postprocessRawCachedData = true }; }
    public bool KeyExists(string key) => cache.ContainsKey(key);
    public string[] GetKeys() => cache.Keys.ToArray();
    public void DeleteKey(string key) => cache.Remove(key);
    public object Load(string key) => Fields.MakeValue(BinaryData.Decode(cache[key].typed), cache[key].type.type)!;
    public void Save<T>(string key, T value)
    {
        if (typeof(T) == typeof(object)) throw new Exception("Native records require typed ES3.Save, not Save<object>");
        object boxed = value!;
        var tree = Fields.TakeValue(boxed);
        cache[key] = new FixtureES3Data { bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tree), typed = BinaryData.Encode(tree), type = new FixtureES3Type { type = boxed.GetType() } };
    }
    public void Opaque(string key, byte[] bytes) => cache[key] = new FixtureES3Data { bytes = bytes.ToArray(), type = new FixtureES3Type { type = typeof(UnreadableWorldRecord) } };
    public byte[] Raw(string key) => cache[key].bytes.ToArray();
    public void UnknownProperty(string key)
    {
        string json = System.Text.Encoding.UTF8.GetString(cache[key].bytes);
        cache[key].bytes = System.Text.Encoding.UTF8.GetBytes(json.Substring(0,json.Length-1)+",\"mod_extra_progress\":123}");
    }
    public byte[] LoadRawBytes()
    {
        if (!settings.postprocessRawCachedData) throw new Exception("Export must retain native codec processing settings");
        var bytes = BinaryData.Encode(cache.ToDictionary(p => p.Key, p => (object?)new Dictionary<string, object?> { ["type"] = p.Value.type.type.AssemblyQualifiedName, ["bytes"] = p.Value.bytes, ["typed"] = p.Value.typed }));
        return TransformOutput == null ? bytes : TransformOutput(bytes);
    }
}
public sealed class UnreadableWorldRecord { public UnreadableWorldRecord() => throw new Exception("World record must remain opaque"); }

internal static class NativeExportFixture
{
    private static void Check(bool value) { if (!value) throw new Exception("Native export fixture failed"); }
    private static void Refuse(Action action, string contains)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException || e is NativeExportException) { Check(e.Message.Contains(contains)); return; }
        throw new Exception("Expected export refusal: " + contains);
    }
    private static ES3File Baseline()
    {
        var f = new ES3File(); f.Save("playerInfo", new PlayerInv { bankBalanceOverflow = 777 });
        f.Save("levels", new LevelSave()); f.Save("recipes", new RecipesUnlockedSave { crafterLevel = 99, currentPoints = 123, crafterCurrentlyWorking = true });
        f.Save("licences", new LicenceAndPermitPointSave()); f.Save("quests", new QuestSave { hasBeencrafted = new[] { true }, dailyTasks = new[] { 999 } });
        f.Save("pedia", new PediaSave());
        var npc = new NPCsave { saveInvs = new[] { new NPCInventory { inventorySlots = new[] { 333 }, inventoryStacks = new[] { 5 } } } };
        npc.savedStatuses![0].hasMovedIn = true; npc.savedStatuses[0].dateMovedIn = new[] { 8, 8, 8, 8 }; f.Save("npc", npc);
        f.Save("stash_0", new ChestSave()); f.Save("stash_1", new ChestSave());
        f.Opaque("date", new byte[] { 9, 8, 7 }); f.Opaque("external.mod.private", new byte[] { 1, 255, 4 }); return f;
    }
    internal static void Run(Action<string, Action> test, string root, string character)
    {
        void Case(string name, Action<Dictionary<string, object?>, ES3File> action) => test(name, () =>
        {
            ES3File.TransformOutput = null;
            var source = RestoreLifecycleFixture.Source(); FixtureRuntime.Reset(false);
            var m = new GameData().DecodeAndValidate(source); action(m, Baseline());
            foreach (var b in source.Blocks) Check(BinaryData.Hash(b.Value) == b.Key);
            ES3File.TransformOutput = null;
        });
        Case("native export codec persists player progress with no live player or container", (m, baseline) =>
        {
            var original = baseline.LoadRawBytes(); int globals = ES3File.cachedFiles.Count;
            var image = NativeExportCodec.Prepare(original, m); var f = new ES3File(image.Bytes, new ES3Settings());
            var inv = (PlayerInv)f.Load("playerInfo"); Check(inv.money == 1234 && inv.itemsInInvSlots[3] == 17 && inv.stacksInSlots[3] == 9);
            Check(inv.health == 63 && inv.healthMax == 130 && inv.stamina == 86 && inv.staminaMax == 140 && inv.catalogue![17]);
            Check(((LevelSave)f.Load("levels")).currentXp[0] == 77 && ((LicenceAndPermitPointSave)f.Load("licences")).permitPoints == 45);
            Check(((MailSave)f.Load("mail")).allMail![0].id == 7 && ((ChestSave)f.Load("stash_0")).itemStack[0] == 6);
            Check(ES3File.cachedFiles.Count == globals && ContainerManager.manage == null && StatusManager.manage.connectedDamge == null);
            Check(Inventory.Instance.walletSlot.stack == 0 && MailManager.manage.mailInBox.Count == 0);
            Check(original.SequenceEqual(baseline.LoadRawBytes()));
            ES3.Files["exported-native"] = NativeValidation.Required.Concat(new[] { "mail", "stash_0" }).ToDictionary(k => k, f.Load);
            NativeValidation.Validate("exported-native"); // no protection plugin methods are needed to read native records
        });
        Case("native export preserves opaque world and unknown mod records byte for byte", (m, baseline) =>
        {
            var f = new ES3File(NativeExportCodec.Prepare(baseline.LoadRawBytes(), m).Bytes, new ES3Settings());
            Check(f.Raw("date").SequenceEqual(new byte[] { 9, 8, 7 }) && f.Raw("external.mod.private").SequenceEqual(new byte[] { 1, 255, 4 }));
            Check(((RecipesUnlockedSave)f.Load("recipes")).crafterLevel == 99 && ((RecipesUnlockedSave)f.Load("recipes")).currentPoints == 123);
            var npc = (NPCsave)f.Load("npc"); Check(npc.saveInvs![0].inventorySlots[0] == 333 && npc.savedStatuses![0].hasMovedIn && npc.savedStatuses[0].dateMovedIn![0] == 8);
            Check(npc.savedStatuses![0].relationshipLevel == 37 && !npc.savedStatuses[0].hasBeenTalkedToToday);
            Check(((QuestSave)f.Load("quests")).hasBeencrafted![0] && ((QuestSave)f.Load("quests")).dailyTasks![0] == 999);
            Check(((PlayerInv)f.Load("playerInfo")).bankBalanceOverflow == 777);
        });
        Case("embedded unknown native properties cause refusal instead of silent loss", (m, baseline) =>
        {
            baseline.UnknownProperty("playerInfo");
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(),m), "Unknown native property would be lost");
        });
        Case("deleting all native mail is legitimate and does not resurrect old attachments", (m, baseline) =>
        {
            var first = NativeExportCodec.Prepare(baseline.LoadRawBytes(), m);
            var letters = BinaryData.Map(m["mail"]); letters["mailInBox"] = Array.Empty<object?>(); letters["tomorrowsLetters"] = Array.Empty<object?>();
            var f = new ES3File(NativeExportCodec.Prepare(first.Bytes,m).Bytes,new ES3Settings());
            Check(((MailSave)f.Load("mail")).allMail!.Length == 0 && ((MailSave)f.Load("mail")).tomorrowsMail!.Length == 0);
        });
        Case("cached array shrink cannot discard larger valid native inventory", (m, baseline) =>
        {
            var inv = (PlayerInv)baseline.Load("playerInfo"); inv.itemsInInvSlots = new int[45]; inv.stacksInSlots = new int[45]; baseline.Save("playerInfo",inv);
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(),m),"would truncate itemsInInvSlots");
        });
        Case("cursor and unsupported idol equipment are packed into distinct empty native slots", (m, baseline) =>
        {
            var assets = BinaryData.Map(m["assets"]); assets["drag"] = new object?[] { 7L, 2L }; BinaryData.List(assets["gear"])[5] = new object?[] { 8L, 0L };
            Inventory.Instance.allItems[8]!.hasFuel = true;
            var image = NativeExportCodec.Prepare(baseline.LoadRawBytes(), m); var inv = (PlayerInv)new ES3File(image.Bytes, new ES3Settings()).Load("playerInfo");
            Check(image.PackedItems == 2 && inv.itemsInInvSlots[0] == 7 && inv.stacksInSlots[0] == 2 && inv.itemsInInvSlots[1] == 8 && inv.stacksInSlots[1] == 0);
            Check(BinaryData.Number(BinaryData.List(assets["drag"])[0]) == 7); // cache remains a complete, unmodified alternate representation
        });
        Case("full backpack aborts cursor export rather than dropping or duplicating the item", (m, baseline) =>
        {
            var assets = BinaryData.Map(m["assets"]); assets["slots"] = Enumerable.Range(0,44).Select(_ => (object?)new object?[] { 3L, 1L }).ToArray(); assets["drag"] = new object?[] { 7L, 2L };
            var before = baseline.LoadRawBytes(); Refuse(() => NativeExportCodec.Prepare(before, m), "no empty backpack slot"); Check(before.SequenceEqual(baseline.LoadRawBytes()));
        });
        Case("empty locked slots cannot conceal packed cursor or idol progress", (m, baseline) =>
        {
            var assets = BinaryData.Map(m["assets"]);
            assets["slots"] = Enumerable.Range(0,44).Select(i => (object?)new object?[] { (i < 11 ? i <= 7 : i <= 34) ? 3L : -1L, (i < 11 ? i <= 7 : i <= 34) ? 1L : 0L }).ToArray();
            assets["drag"] = new object?[] { 7L, 2L };
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(),m),"no empty backpack slot");
        });
        Case("repeating the same cached export cannot duplicate packed items mail or stash", (m, baseline) =>
        {
            BinaryData.Map(m["assets"])["drag"] = new object?[] { 7L, 2L };
            var first = NativeExportCodec.Prepare(baseline.LoadRawBytes(), m);
            var second = NativeExportCodec.Prepare(first.Bytes, m);
            var f = new ES3File(second.Bytes, new ES3Settings()); var inv = (PlayerInv)f.Load("playerInfo");
            Check(inv.itemsInInvSlots.Count(id => id == 7) == 1 && inv.stacksInSlots[Array.IndexOf(inv.itemsInInvSlots,7)] == 2);
            Check(((MailSave)f.Load("mail")).allMail!.Length == 1 && ((ChestSave)f.Load("stash_0")).itemStack[0] == 6);
        });
        Case("zero-count non-fuel packed items cannot silently disappear in vanilla", (m, baseline) =>
        {
            BinaryData.Map(m["assets"])["drag"] = new object?[] { 7L, 0L };
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(),m), "zero-count non-fuel");
        });
        Case("stale native stash keys are removed while unrelated stash-like mod keys survive", (m, baseline) =>
        {
            baseline.Opaque("stash_external", new byte[] { 88 }); m["stash"] = Array.Empty<object?>();
            var f = new ES3File(NativeExportCodec.Prepare(baseline.LoadRawBytes(), m).Bytes, new ES3Settings());
            Check(!f.KeyExists("stash_0") && !f.KeyExists("stash_1") && f.Raw("stash_external")[0] == 88);
        });
        Case("non-vanilla stash layouts cannot silently truncate into native output", (m, baseline) =>
        {
            var stash = BinaryData.List(m["stash"])[0]; m["stash"] = new object?[] { stash, stash, stash };
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(), m), "only two private stashes");
            m["stash"] = new object?[] { stash }; BinaryData.Map(stash)["itemIds"] = new object?[25];
            Refuse(() => NativeExportCodec.Prepare(baseline.LoadRawBytes(), m), "exactly 24 slots");
        });
        Case("native export rejects a damaged serialized output before any disk commit", (m, baseline) =>
        {
            var original = baseline.LoadRawBytes();
            ES3File.TransformOutput = bytes => new byte[] { 255 };
            Refuse(() => NativeExportCodec.Prepare(original, m), "Unknown value tag");
        });
        Case("extension fields remain explicitly separate from native-only persistence", (m, baseline) =>
        {
            var image = NativeExportCodec.Prepare(baseline.LoadRawBytes(), m);
            Check(image.Extensions == m.Keys.Count(k => k.StartsWith("ext.")) && image.Extensions > 0);
        });
        void TransactionCase(string name, Action<string, string, string, string, byte[], byte[]> action) => test(name, () =>
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string native = Path.Combine(dir,"Player.es3"), identity = Path.Combine(dir,"identity.bin"), journal = Path.Combine(dir,"pending.bin"), backup = Path.Combine(dir,"backup");
            byte[] old = new byte[] { 1, 2, 3 }, next = new byte[] { 4, 5, 6 };
            File.WriteAllBytes(native, old); File.WriteAllBytes(identity, BinaryData.Encode(new Dictionary<string, object?> { ["character"] = character, ["native"] = BinaryData.Hash(old), ["checkpoint"] = "" }));
            action(native, identity, journal, backup, old, next);
        });
        string snapshot = BinaryData.Hash(new byte[] { 9 });
        void Commit(string native, string identity, string journal, string backup, byte[] old, byte[] next, Action<string>? fault = null, Func<bool>? current = null) => NativeExportTransaction.Commit(native,identity,journal,backup,character,snapshot,BinaryData.Hash(old),BinaryData.Hash(File.ReadAllBytes(identity)),next,current,fault);
        TransactionCase("native export atomically publishes verified bytes backups and checkpoint", (n,i,j,b,old,next) =>
        {
            Commit(n,i,j,b,old,next); Check(File.ReadAllBytes(n).SequenceEqual(next) && File.ReadAllBytes(n+".bak").SequenceEqual(next) && File.ReadAllBytes(n+".bac").SequenceEqual(next));
            Check(File.ReadAllBytes(Path.Combine(b,"Player.before.es3")).SequenceEqual(old) && !File.Exists(j));
            Check(BinaryData.Text(BinaryData.Map(BinaryData.Decode(File.ReadAllBytes(i)))["native"]) == BinaryData.Hash(next));
        });
        TransactionCase("native export aborts an external source-file change before publication", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="before-publish") File.WriteAllBytes(n,new byte[] { 99 }); }), "refusing overwrite");
            Check(File.ReadAllBytes(n).SequenceEqual(new byte[] { 99 }));
            Check(!File.Exists(j));
        });
        TransactionCase("identity change during export cannot authorize an old character checkpoint", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="before-publish") File.WriteAllBytes(i,new byte[] { 55 }); }), "refusing overwrite");
            Check(File.ReadAllBytes(n).SequenceEqual(old) && File.ReadAllBytes(i)[0] == 55);
        });
        TransactionCase("corrupt staged bytes cannot replace a good native file or backup", (n,i,j,b,old,next) =>
        {
            File.WriteAllBytes(n+".bak", old);
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="before-publish") File.WriteAllBytes(Directory.GetFiles(Path.GetDirectoryName(n)!,"*.export.tmp").Single(),new byte[] { 88 }); }), "Staged native file changed");
            Check(File.ReadAllBytes(n).SequenceEqual(old) && File.ReadAllBytes(n+".bak").SequenceEqual(old));
        });
        TransactionCase("pre-publication interruption leaves native progress unchanged", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="before-publish") throw new IOException("Power loss"); }), "before publication");
            Check(File.ReadAllBytes(n).SequenceEqual(old)); Check(!NativeExportTransaction.Reconcile(n,i,j,character) && !File.Exists(j));
        });
        TransactionCase("post-publication interruption reconciles metadata and native backups", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="published") throw new IOException("Power loss"); }), "was published");
            Check(File.ReadAllBytes(n).SequenceEqual(next) && File.Exists(j)); Check(NativeExportTransaction.Reconcile(n,i,j,character));
            Check(File.ReadAllBytes(n+".bak").SequenceEqual(next) && BinaryData.Text(BinaryData.Map(BinaryData.Decode(File.ReadAllBytes(i)))["checkpoint"]) == snapshot);
        });
        TransactionCase("cancelled export session cannot replace the native file", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next,current:()=>false), "session ended"); Check(File.ReadAllBytes(n).SequenceEqual(old));
        });
        TransactionCase("journal conflict never overwrites newer external progress", (n,i,j,b,old,next) =>
        {
            Refuse(() => Commit(n,i,j,b,old,next, phase => { if (phase=="published") throw new IOException("Stop"); }), "was published");
            File.WriteAllBytes(n,new byte[] { 90 }); Refuse(() => NativeExportTransaction.Reconcile(n,i,j,character), "differs from both"); Check(File.ReadAllBytes(n)[0] == 90);
        });
    }
}
