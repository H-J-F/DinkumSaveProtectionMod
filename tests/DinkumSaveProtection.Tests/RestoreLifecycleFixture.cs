using DinkumSaveProtection;
using DinkumSaveProtection.Core;
using DinkumSaveProtection.Runtime;

internal static class RestoreLifecycleFixture
{
    private sealed class ActorAdapter : IPlayerDataAdapter
    {
        public string Id => "tests.restore_phase";
        public string Version => "1";
        public int Restores;
        public bool FailRestore, MutateValidation;
        public byte[] Capture() => new byte[] { 41, 42 };
        public void Validate(byte[] data, string version)
        {
            Check(version == "1" && data.SequenceEqual(new byte[] { 41, 42 }));
            if (MutateValidation) data[0] = 0;
        }
        public void Restore(byte[] data, string version)
        {
            FixtureRuntime.RequireActor(); Check(data.SequenceEqual(new byte[] { 41, 42 }));
            if (FailRestore) throw new InvalidOperationException("Fixture adapter failure");
            data[0] = 0; Restores++;
        }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Restore lifecycle fixture failed"); }
    private static void Refuses(Action action, string reason)
    {
        try { action(); }
        catch (InvalidOperationException e) { Check(e.Message.Contains(reason)); return; }
        throw new Exception("Expected readiness refusal: " + reason);
    }
    internal static Capture Source()
    {
        FixtureRuntime.Reset(true);
        Inventory.Instance.walletSlot.stack = 1234; Inventory.Instance.invSlots[3].itemNo = 17; Inventory.Instance.invSlots[3].stack = 9;
        BankMenu.menu.accountBalance = 321; CharLevelManager.manage.currentXp[0] = 77;
        PermitPointsManager.manage.loadFromSave(45); QuestManager.manage.isQuestCompleted[0] = true;
        PediaManager.manage.allEntries[0].amountCaught = 8; CatalogueManager.manage.collectedItem[17] = true;
        NPCManager.manage.npcStatus[0].relationshipLevel = 37; NPCManager.manage.npcStatus[0].hasBeenTalkedToToday = true;
        MailManager.manage.mailInBox.Add(new Letter { id = 7 }); MailManager.manage.tomorrowsLetters.Add(new Letter { id = 8 });
        var chest = new Chest(0, 0); chest.itemIds[0] = 17; chest.itemStacks[0] = 6; ContainerManager.manage!.privateStashes.Add(chest);
        StatusManager.manage.connectedDamge!.health = 63; StatusManager.manage.loadNewMaxStaminaAndHealth(140, 130);
        typeof(StatusManager).GetField("stamina", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(StatusManager.manage, 86f);
        return Capture.Create("77777777777777777777777777777777", "88888888888888888888888888888888", 1, null, "test-build",
            CheckpointWriter.EncodeModules(new GameData().Capture("world")))!;
    }
    private static void Intact(Capture source)
    { foreach (var block in source.Blocks) Check(BinaryData.Hash(block.Value) == block.Key); }
    internal static void Run(Action<string, Action> test)
    {
        PlayerDataAdapters.BindMainThread(); var adapter = new ActorAdapter(); PlayerDataAdapters.Register(adapter);
        void Case(string name, Action<GameData, Capture, Dictionary<string, object?>> body) => test(name, () =>
        {
            adapter.FailRestore = adapter.MutateValidation = false; adapter.Restores = 0;
            var source = Source(); FixtureRuntime.Reset(false); var data = new GameData();
            var decoded = data.DecodeAndValidate(source); body(data, source, decoded); Intact(source);
        });
        Case("actual GameData early restore works with no spawned container or player", (data, source, m) =>
        {
            Check(ContainerManager.manage == null && NetworkMapSharer.Instance == null && StatusManager.manage.connectedDamge == null);
            data.Restore(m, "world", true);
            Check(data.RestoreStage == "early.complete" && Inventory.Instance.walletSlot.stack == 1234 && Inventory.Instance.invSlots[3].stack == 9);
            Check(CharLevelManager.manage.currentXp[0] == 77 && NPCManager.manage.npcStatus[0].relationshipLevel == 37);
            Check(ContainerManager.manage == null && adapter.Restores == 0 && LicenceManager.manage.Refreshes == 0 && Inventory.Instance.DerivedRefreshes == 0);
        });
        Case("early recovery with an empty stash still never requires ContainerManager", (data, source, m) =>
        {
            m["stash"] = Array.Empty<object?>(); data.Validate(m); data.Restore(m, "world", true);
            Check(ContainerManager.manage == null && data.RestoreStage == "early.complete");
        });
        Case("repeated early preparation does not invoke actor commands or extension restore", (data, source, m) =>
        {
            data.Restore(m, "world", true); data.Restore(m, "world", true);
            Check(adapter.Restores == 0 && CharLevelManager.manage.RecipeRefreshes == 0 && Inventory.Instance.EquipRefreshes == 0);
            Check(MailManager.manage.mailInBox.Count == 1 && MailManager.manage.tomorrowsLetters.Count == 1);
        });
        Case("post-spawn final restore applies stash vitals equipment and extension state", (data, source, m) =>
        {
            data.Restore(m, "world", true); Check(!NPCManager.manage.npcStatus[0].hasBeenTalkedToToday);
            FixtureRuntime.Spawn(); var stashList = ContainerManager.manage!.privateStashes; stashList.Add(new Chest(9, 9));
            data.Restore(m, "world");
            Check(data.RestoreStage == "final.complete" && ReferenceEquals(stashList, ContainerManager.manage.privateStashes));
            Check(stashList.Count == 1 && stashList[0].itemIds[0] == 17 && stashList[0].itemStacks[0] == 6);
            Check(StatusManager.manage.connectedDamge!.health == 63 && StatusManager.manage.connectedDamge.maxHealth == 130);
            Check(StatusManager.manage.getStamina() == 86 && StatusManager.manage.getStaminaMax() == 140);
            Check(Inventory.Instance.localChar!.Commands == 10 && adapter.Restores == 1 && NPCManager.manage.npcStatus[0].hasBeenTalkedToToday);
        });
        Case("final restore refuses a missing container before changing any personal field", (data, source, m) =>
        {
            Refuses(() => data.Restore(m, "world"), "ContainerManager");
            Check(data.RestoreStage == "final.targets" && Inventory.Instance.walletSlot.stack == 0 && Inventory.Instance.invSlots[3].itemNo == -1);
        });
        Case("partially spawned final recovery reports the unavailable actor target", (data, source, m) =>
        {
            ContainerManager.manage = new ContainerManager(); Refuses(() => data.Restore(m, "world"), "NetworkMapSharer");
            FixtureRuntime.Spawn(); StatusManager.manage.connectedDamge = null;
            Refuses(() => data.Restore(m, "world"), "StatusManager.connectedDamge"); Check(Inventory.Instance.walletSlot.stack == 0);
        });
        Case("late adapter failure identifies stage and preserves original snapshot bytes", (data, source, m) =>
        {
            FixtureRuntime.Spawn(); adapter.FailRestore = true; Refuses(() => data.Restore(m, "world"), "Fixture adapter failure");
            Check(data.RestoreStage == "final.adapters");
            var ext = BinaryData.Map(m["ext." + adapter.Id]); Check(((byte[])ext["data"]!).SequenceEqual(new byte[] { 41, 42 }));
        });
        Case("stash construction failure does not clear an existing stash or change wallet", (data, source, m) =>
        {
            FixtureRuntime.Spawn(); var stash = ContainerManager.manage!.privateStashes; var original = new Chest(9, 9); stash.Add(original);
            var bad = new Dictionary<string, object?> { ["notAChestField"] = 1L }; m["stash"] = new object?[] { bad };
            bool refused = false; try { data.Restore(m, "world"); } catch (MissingFieldException) { refused = true; }
            Check(refused && data.RestoreStage == "final.prepare-stash" && stash.Count == 1 && ReferenceEquals(stash[0], original) && Inventory.Instance.walletSlot.stack == 0);
        });
        Case("unknown world restores stable relationships but never daily interaction flags", (data, source, m) =>
        {
            data.Restore(m, "", true); FixtureRuntime.Spawn(); data.Restore(m, "");
            Check(NPCManager.manage.npcStatus[0].relationshipLevel == 37 && !NPCManager.manage.npcStatus[0].hasBeenTalkedToToday);
        });
        Case("pre-spawn world dates cannot authorize early daily relationship replay", (data, source, m) =>
        {
            WorldManager.Instance = new WorldManager(); data.Restore(m, "world", true);
            Check(!NPCManager.manage.npcStatus[0].hasBeenTalkedToToday);
            FixtureRuntime.Spawn(); data.Restore(m, "world"); Check(NPCManager.manage.npcStatus[0].hasBeenTalkedToToday);
        });
        Case("capture refuses unavailable actor or container instead of producing cleared state", (data, source, m) =>
        {
            Refuses(() => data.Capture("world"), "Player data unavailable");
            FixtureRuntime.Spawn(); ContainerManager.manage = null; Refuses(() => data.Capture("world"), "Player data unavailable");
        });
        Case("early readiness guard names a missing menu manager before applying assets", (data, source, m) =>
        {
            MailManager.manage = null!; Refuses(() => data.Restore(m, "world", true), "MailManager");
            Check(Inventory.Instance.walletSlot.stack == 0 && data.RestoreStage == "early.targets");
        });
        Case("adapter argument mutation cannot change a validated recovery candidate", (data, source, m) =>
        {
            adapter.MutateValidation = true; data.DecodeAndValidate(source); data.Validate(m);
            data.Restore(m, "world", true); FixtureRuntime.Spawn(); data.Restore(m, "world");
            Check(adapter.Restores == 1 && ((byte[])BinaryData.Map(m["ext." + adapter.Id])["data"]!).SequenceEqual(new byte[] { 41, 42 }));
        });
    }
}
