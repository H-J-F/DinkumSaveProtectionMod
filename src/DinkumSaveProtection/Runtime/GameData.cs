using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using DinkumSaveProtection.Core;

namespace DinkumSaveProtection.Runtime
{
    internal static class Fields
    {
        private static FieldInfo Field(Type t, string name) => t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException(t.FullName, name);
        internal static Dictionary<string, object?> Take(object target, params string[] names) => names.ToDictionary(n => n, n => TakeValue(Field(target.GetType(), n).GetValue(target)), StringComparer.Ordinal);
        internal static void Apply(object target, Dictionary<string, object?> data)
        {
            foreach (var p in data) { var f = Field(target.GetType(), p.Key); f.SetValue(target, MakeValue(p.Value, f.FieldType)); }
        }
        internal static object? TakeValue(object? value, int depth = 0)
        {
            if (depth > 24) throw new InvalidDataException("Object graph too deep");
            if (value == null) return null;
            var type = value.GetType();
            if (value is string || value is bool || value is byte[]) return value is byte[] b ? b.Clone() : value;
            if (value is ulong ul) return ul;
            if (value is float || value is double) return Convert.ToDouble(value);
            if (type.IsEnum || type.IsPrimitive) return Convert.ToInt64(value);
            if (value is IList list) { if (list.Count > BinaryData.MaxElements) throw new InvalidDataException("Collection exceeds capture budget"); var result = new object?[list.Count]; for (int i = 0; i < result.Length; i++) result[i] = TakeValue(list[i], depth + 1); return result; }
            if (value is UnityEngine.Object) throw new InvalidDataException("Unity references are not save data");
            if (!type.IsDefined(typeof(SerializableAttribute), false) || type.Assembly != typeof(PlayerInv).Assembly) throw new InvalidDataException("An adapter is required for " + type.FullName);
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public).ToDictionary(f => f.Name, f => TakeValue(f.GetValue(value), depth + 1), StringComparer.Ordinal);
        }
        internal static object? MakeValue(object? value, Type type)
        {
            if (value == null) { if (type.IsValueType) throw new InvalidDataException("Null value type"); return null; }
            if (type == typeof(string)) return BinaryData.Text(value);
            if (type == typeof(byte[])) return ((byte[])value).Clone();
            if (type.IsEnum)
            {
                var result = Enum.ToObject(type, BinaryData.Number(value));
                if (!type.IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(type, result)) throw new InvalidDataException("Unknown saved enum value");
                return result;
            }
            if (type.IsPrimitive)
            {
                if (type == typeof(bool) && !(value is bool) || type == typeof(ulong) && !(value is ulong) ||
                    (type == typeof(float) || type == typeof(double)) && !(value is double) ||
                    type != typeof(bool) && type != typeof(ulong) && type != typeof(float) && type != typeof(double) && !(value is long)) throw new InvalidDataException("Saved primitive type mismatch");
                return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (type.IsArray)
            {
                var values = BinaryData.List(value); var itemType = type.GetElementType()!;
                var result = Array.CreateInstance(itemType, values.Length);
                for (int i = 0; i < values.Length; i++) result.SetValue(MakeValue(values[i], itemType), i);
                return result;
            }
            if (typeof(IList).IsAssignableFrom(type))
            {
                var result = (IList)Activator.CreateInstance(type)!; var itemType = type.GetGenericArguments()[0];
                foreach (var item in BinaryData.List(value)) result.Add(MakeValue(item, itemType));
                return result;
            }
            if (!type.IsDefined(typeof(SerializableAttribute), false) || type.Assembly != typeof(PlayerInv).Assembly) throw new InvalidDataException("Unsupported saved type");
            var data = BinaryData.Map(value);
            PersonalDataRules.Keys(data, type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).ToArray());
            var instance = Activator.CreateInstance(type)!; Apply(instance, data); return instance;
        }
        internal static void Validate(object target, Dictionary<string, object?> data, bool exactArrays = true, bool exactLists = false)
        {
            foreach (var p in data)
            {
                var f = Field(target.GetType(), p.Key);
                var incoming = MakeValue(p.Value, f.FieldType);
                if (exactArrays && incoming is Array saved && f.GetValue(target) is Array current && saved.Length != current.Length)
                    throw new InvalidDataException("Array shape changed: " + p.Key + "; adapter or explicit migration required");
                if (exactLists && incoming is IList savedList && f.GetValue(target) is IList currentList && savedList.Count != currentList.Count)
                    throw new InvalidDataException("List shape changed: " + p.Key + "; adapter or explicit migration required");
            }
        }
    }
    internal sealed class GameData
    {
        internal string RestoreStage { get; private set; } = "not-started";
        private static void Require(UnityEngine.Object? value, string name)
        { if (value == null) throw new InvalidOperationException("Recovery target not ready: " + name + "; player remains locked and source cache is retained"); }
        internal void RequireRestoreTargets(bool early)
        {
            Require(Inventory.Instance, "Inventory"); Require(EquipWindow.equip, "EquipWindow"); Require(BankMenu.menu, "BankMenu");
            Require(CharLevelManager.manage, "CharLevelManager"); Require(LicenceManager.manage, "LicenceManager"); Require(MilestoneManager.manage, "MilestoneManager");
            Require(PermitPointsManager.manage, "PermitPointsManager"); Require(DailyTaskGenerator.generate, "DailyTaskGenerator");
            Require(QuestManager.manage, "QuestManager"); Require(MailManager.manage, "MailManager"); Require(PediaManager.manage, "PediaManager");
            Require(CatalogueManager.manage, "CatalogueManager"); Require(NPCManager.manage, "NPCManager"); Require(StatusManager.manage, "StatusManager");
            if (early) return;
            Require(ContainerManager.manage, "ContainerManager"); Require(NetworkMapSharer.Instance, "NetworkMapSharer");
            Require(NetworkMapSharer.Instance!.localChar, "NetworkMapSharer.localChar"); Require(Inventory.Instance.localChar, "Inventory.localChar");
            Require(StatusManager.manage.connectedDamge, "StatusManager.connectedDamge");
            if (ContainerManager.manage!.privateStashes == null) throw new InvalidOperationException("Recovery target not ready: ContainerManager.privateStashes");
        }
        private static readonly string[] Appearance = { "playerName", "islandName", "playerHair", "playerEyes", "playerEyeColor", "playerHairColour", "skinTone", "nose", "mouth", "face2Slot", "isCreative", "hasBeenCreative" };
        private static readonly string[] StableRelations = { "relationshipLevel", "moneySpentAtStore", "hasMet", "howManyDaysSincePlayerInteract" };
        private static readonly string[] DailyRelations = { "hasBeenTalkedToToday", "hasGossipedToday", "hasHungOutToday", "acceptedRequest", "completedRequest", "last3TextSaid", "lastTextSaidSlot" };
        private static InventorySlot[] Gear() => new[] { EquipWindow.equip.faceSlot, EquipWindow.equip.hatSlot, EquipWindow.equip.shirtSlot, EquipWindow.equip.pantsSlot, EquipWindow.equip.shoeSlot, EquipWindow.equip.idolSlot };
        private static object? Slot(InventorySlot? s) => s == null ? null : new object?[] { (long)s.itemNo, (long)s.stack };
        private static Dictionary<string, object?> Map(params object?[] pairs)
        { var m = new Dictionary<string, object?>(StringComparer.Ordinal); for (int i = 0; i < pairs.Length; i += 2) m.Add((string)pairs[i]!, pairs[i + 1]); return m; }
        internal string ItemFingerprint()
        {
            return BinaryData.Hash(BinaryData.Encode(ItemDescriptors()));
        }
        private static object?[] ItemDescriptors() => Inventory.Instance.allItems.Select((item, i) => item == null ? null : (object?)new object?[] { (long)i, item.name, item.itemName, item.tag }).ToArray();
        internal string WorldDay => WorldManager.Instance == null ? "" : string.Join("/", WorldManager.Instance.year, WorldManager.Instance.month, WorldManager.Instance.week, WorldManager.Instance.day);
        internal Dictionary<string, object?> Capture(string context)
        {
            if (Inventory.Instance == null || StatusManager.manage?.connectedDamge == null || NetworkMapSharer.Instance?.localChar == null || ContainerManager.manage == null) throw new InvalidOperationException("Player data unavailable");
            var inventory = Inventory.Instance;
            var modules = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["assets"] = Map("appearance", Fields.Take(inventory, Appearance), "slots", inventory.invSlots.Select(Slot).ToArray(), "gear", Gear().Select(Slot).ToArray(), "drag", Slot(inventory.dragSlot), "wallet", (long)inventory.walletSlot.stack, "bank", Fields.Take(BankMenu.menu, "accountBalance", "accountOverflow")),
                ["growth"] = Map("levels", Fields.Take(CharLevelManager.manage, "todaysXp", "currentXp", "currentLevels", "recipes"), "licences", Fields.Take(LicenceManager.manage, "allLicences"), "points", (long)PermitPointsManager.manage.getCurrentPoints(), "milestones", Fields.Take(MilestoneManager.manage, "milestones"), "distance", (long)DailyTaskGenerator.generate.tiredDistance),
                ["quests"] = Fields.Take(QuestManager.manage, "isQuestAccepted", "isQuestCompleted"),
                ["pedia"] = Map("entries", Fields.TakeValue(PediaManager.manage.allEntries), "catalogue", Fields.TakeValue(CatalogueManager.manage.collectedItem)),
                ["relations"] = Map("context", context, "day", WorldDay, "stable", NPCManager.manage.npcStatus.Select(n => (object?)Fields.Take(n, StableRelations)).ToArray(), "daily", NPCManager.manage.npcStatus.Select(n => (object?)Fields.Take(n, DailyRelations)).ToArray()),
                ["mail"] = Fields.Take(MailManager.manage, "mailInBox", "tomorrowsLetters"),
                ["stash"] = ContainerManager.manage.privateStashes.Select(c => (object?)Fields.Take(c, "itemIds", "itemStacks", "xPos", "yPos", "inside", "insideX", "insideY")).ToArray(),
                ["vitals"] = Map("health", (long)StatusManager.manage.connectedDamge.health, "maxHealth", (long)StatusManager.manage.connectedDamge.maxHealth, "stamina", (double)StatusManager.manage.getStamina(), "maxStamina", (double)StatusManager.manage.getStaminaMax(), "snagsEaten", (long)StatusManager.manage.snagsEaten),
                ["environment"] = Map("items", ItemDescriptors(), "plugins", Chainloader.PluginInfos.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (object?)new object?[] { p.Key, p.Value.Metadata.Version.ToString() }).ToArray())
            };
            var adapters = PlayerDataAdapters.All;
            if (modules.Count + adapters.Length > 128) throw new InvalidDataException("Too many personal modules");
            long extensionBytes = 0;
            foreach (var adapter in adapters)
            {
                var bytes = adapter.Capture() ?? throw new InvalidDataException("Null adapter capture: " + adapter.Id);
                extensionBytes += bytes.Length;
                if (bytes.Length > BinaryData.MaxModuleBytes - 4096 || extensionBytes > 24L * 1024 * 1024) throw new InvalidDataException("Oversized adapter capture: " + adapter.Id);
                modules.Add("ext." + adapter.Id, Map("version", adapter.Version, "data", (byte[])bytes.Clone()));
            }
            Validate(modules);
            return modules;
        }
        internal Dictionary<string, object?> DecodeAndValidate(Capture capture)
        {
            var m = capture.Manifest.Modules.ToDictionary(p => p.Key, p => BinaryData.Decode(capture.Blocks[p.Value]), StringComparer.Ordinal);
            _ = BinaryData.Text(BinaryData.Map(m["environment"])["items"]); // raw descriptors are only accepted from local capture
            Validate(m); return m;
        }
        internal void Validate(Dictionary<string, object?> m)
        {
            if (Snapshot.Required.Any(k => !m.ContainsKey(k))) throw new InvalidDataException("Missing personal module");
            if (m.Keys.Any(k => !Snapshot.Required.Contains(k) && !k.StartsWith("ext.", StringComparison.Ordinal))) throw new InvalidDataException("Unknown personal module");
            var environment = BinaryData.Map(m["environment"]);
            Keys(environment, "items", "plugins");
            foreach (var plugin in BinaryData.List(environment["plugins"]))
            { var pair = BinaryData.List(plugin); if (pair.Length != 2) throw new InvalidDataException("Invalid plugin metadata"); _ = BinaryData.Text(pair[0]); _ = BinaryData.Text(pair[1]); }
            if (environment["items"] is string fingerprint && fingerprint != ItemFingerprint()) throw new InvalidDataException("Item mapping changed; automatic restoration paused");
            if (!(environment["items"] is string) && !(environment["items"] is object?[])) throw new InvalidDataException("Invalid item mapping");
            var assets = BinaryData.Map(m["assets"]);
            Keys(assets, "appearance", "slots", "gear", "drag", "wallet", "bank"); Keys(BinaryData.Map(assets["appearance"]), Appearance); Keys(BinaryData.Map(assets["bank"]), "accountBalance", "accountOverflow");
            checked { _ = (int)BinaryData.Number(assets["wallet"]); }
            ValidateSlots(BinaryData.List(assets["slots"]), Inventory.Instance.invSlots.Length);
            var gear = BinaryData.List(assets["gear"]); if (gear.Length != Gear().Length) throw new InvalidDataException("Equipment schema mismatch");
            for (int i = 0; i < gear.Length; i++) { if ((gear[i] == null) != (Gear()[i] == null)) throw new InvalidDataException("Equipment availability changed"); if (gear[i] != null) ValidateSlot(gear[i]); }
            ValidateSlot(assets["drag"]); Fields.Validate(Inventory.Instance, BinaryData.Map(assets["appearance"])); Fields.Validate(BankMenu.menu, BinaryData.Map(assets["bank"]));
            var growth = BinaryData.Map(m["growth"]);
            Keys(growth, "levels", "licences", "points", "milestones", "distance"); Keys(BinaryData.Map(growth["levels"]), "todaysXp", "currentXp", "currentLevels", "recipes"); Keys(BinaryData.Map(growth["licences"]), "allLicences"); Keys(BinaryData.Map(growth["milestones"]), "milestones");
            checked { _ = (int)BinaryData.Number(growth["points"]); _ = (int)BinaryData.Number(growth["distance"]); }
            Fields.Validate(CharLevelManager.manage, BinaryData.Map(growth["levels"]), true, true); Fields.Validate(LicenceManager.manage, BinaryData.Map(growth["licences"])); Fields.Validate(MilestoneManager.manage, BinaryData.Map(growth["milestones"]), true, true);
            Keys(BinaryData.Map(m["quests"]), "isQuestAccepted", "isQuestCompleted"); Keys(BinaryData.Map(m["mail"]), "mailInBox", "tomorrowsLetters"); Fields.Validate(QuestManager.manage, BinaryData.Map(m["quests"])); Fields.Validate(MailManager.manage, BinaryData.Map(m["mail"]));
            var pedia = BinaryData.Map(m["pedia"]);
            Keys(pedia, "entries", "catalogue"); _ = Fields.MakeValue(pedia["entries"], typeof(List<PediaEntry>)); _ = Fields.MakeValue(pedia["catalogue"], typeof(bool[]));
            if (BinaryData.List(pedia["entries"]).Length != PediaManager.manage.allEntries.Count || BinaryData.List(pedia["catalogue"]).Length != CatalogueManager.manage.collectedItem.Length) throw new InvalidDataException("Pedia/catalogue shape changed");
            var relations = BinaryData.Map(m["relations"]);
            Keys(relations, "context", "day", "stable", "daily");
            _ = BinaryData.Text(relations["context"]); _ = BinaryData.Text(relations["day"]);
            if (BinaryData.List(relations["stable"]).Length != NPCManager.manage.npcStatus.Count || BinaryData.List(relations["daily"]).Length != NPCManager.manage.npcStatus.Count) throw new InvalidDataException("NPC shape changed");
            var stableRelations = BinaryData.List(relations["stable"]); var dailyRelations = BinaryData.List(relations["daily"]);
            for (int i = 0; i < stableRelations.Length; i++) { var stable = BinaryData.Map(stableRelations[i]); var daily = BinaryData.Map(dailyRelations[i]); Keys(stable, StableRelations); Keys(daily, DailyRelations); Fields.Validate(NPCManager.manage.npcStatus[i], stable); Fields.Validate(NPCManager.manage.npcStatus[i], daily); }
            foreach (var chest in BinaryData.List(m["stash"]))
            { var c = BinaryData.Map(chest); Keys(c, "itemIds", "itemStacks", "xPos", "yPos", "inside", "insideX", "insideY"); ValidateSlotsFromArrays(BinaryData.List(c["itemIds"]), BinaryData.List(c["itemStacks"])); Fields.Validate(new Chest(0, 0), c, false); }
            var vitals = BinaryData.Map(m["vitals"]); Keys(vitals, "health", "maxHealth", "stamina", "maxStamina", "snagsEaten");
            checked { _ = (int)BinaryData.Number(vitals["health"]); _ = (int)BinaryData.Number(vitals["maxHealth"]); _ = (int)BinaryData.Number(vitals["snagsEaten"]); }
            if (!(vitals["stamina"] is double) || !(vitals["maxStamina"] is double)) throw new InvalidDataException("Invalid stamina type");
            float stamina = Convert.ToSingle(vitals["stamina"]), maxStamina = Convert.ToSingle(vitals["maxStamina"]);
            PersonalDataRules.Vitals(stamina, maxStamina, BinaryData.Number(vitals["maxHealth"]));
            foreach (var ext in m.Where(p => p.Key.StartsWith("ext.", StringComparison.Ordinal)))
            {
                var adapter = PlayerDataAdapters.Find(ext.Key.Substring(4)) ?? throw new InvalidDataException("Missing adapter: " + ext.Key);
                var wrapper = BinaryData.Map(ext.Value); Keys(wrapper, "version", "data"); adapter.Validate((byte[])((byte[])wrapper["data"]!).Clone(), BinaryData.Text(wrapper["version"]));
            }
            // Newly registered adapters may carry native state that an old backup cannot describe.
            if (PlayerDataAdapters.All.Any(a => !m.ContainsKey("ext." + a.Id))) throw new InvalidDataException("Backup lacks a required extension adapter");
        }
        private static void Keys(Dictionary<string, object?> map, params string[] allowed)
        { PersonalDataRules.Keys(map, allowed); }
        private static void ValidateSlot(object? value)
        {
            var pair = BinaryData.List(value); if (pair.Length != 2) throw new InvalidDataException("Invalid slot");
            long id = BinaryData.Number(pair[0]); long stack = BinaryData.Number(pair[1]);
            PersonalDataRules.Item(id, stack, Inventory.Instance.allItems.Length, i => Inventory.Instance.allItems[i] != null);
        }
        private static void ValidateSlots(object?[] slots, int count) { if (slots.Length != count) throw new InvalidDataException("Inventory size changed; refusing truncation"); foreach (var s in slots) ValidateSlot(s); }
        private static void ValidateSlotsFromArrays(object?[] ids, object?[] stacks) { if (ids.Length != stacks.Length) throw new InvalidDataException("Chest array mismatch"); for (int i = 0; i < ids.Length; i++) ValidateSlot(new object?[] { ids[i], stacks[i] }); }
        private static void ApplySlot(InventorySlot slot, object? value)
        {
            var pair = BinaryData.List(value); slot.itemNo = checked((int)BinaryData.Number(pair[0])); slot.stack = checked((int)BinaryData.Number(pair[1])); slot.refreshSlot(false);
        }
        internal void Restore(Dictionary<string, object?> m, string context, bool early = false)
        {
            // Before Ready/AddPlayer only menu-resident personal data is available.
            // Network managers, actor state and derived UI refresh belong to the final loading lock.
            RestoreStage = early ? "early.targets" : "final.targets";
            RequireRestoreTargets(early);
            List<Chest>? restoredStashes = null;
            if (!early)
            {
                RestoreStage = "final.prepare-stash";
                restoredStashes = new List<Chest>();
                foreach (var value in BinaryData.List(m["stash"])) { var chest = new Chest(0, 0); Fields.Apply(chest, BinaryData.Map(value)); restoredStashes.Add(chest); }
            }
            RestoreStage = early ? "early.assets" : "final.assets";
            var assets = BinaryData.Map(m["assets"]); var inventory = Inventory.Instance;
            Fields.Apply(inventory, BinaryData.Map(assets["appearance"])); Fields.Apply(BankMenu.menu, BinaryData.Map(assets["bank"]));
            inventory.changeWalletToLoad(checked((int)BinaryData.Number(assets["wallet"])));
            var slots = BinaryData.List(assets["slots"]); for (int i = 0; i < slots.Length; i++) ApplySlot(inventory.invSlots[i], slots[i]);
            var gear = BinaryData.List(assets["gear"]); for (int i = 0; i < gear.Length; i++) if (gear[i] != null) ApplySlot(Gear()[i], gear[i]);
            // Restore the cursor itself: it is not added to inventory or dropped into the world.
            ApplySlot(inventory.dragSlot, assets["drag"]);
            RestoreStage = early ? "early.growth" : "final.growth";
            var growth = BinaryData.Map(m["growth"]);
            Fields.Apply(CharLevelManager.manage, BinaryData.Map(growth["levels"])); Fields.Apply(LicenceManager.manage, BinaryData.Map(growth["licences"])); Fields.Apply(MilestoneManager.manage, BinaryData.Map(growth["milestones"]));
            PermitPointsManager.manage.loadFromSave(checked((int)BinaryData.Number(growth["points"]))); DailyTaskGenerator.generate.tiredDistance = checked((int)BinaryData.Number(growth["distance"]));
            RestoreStage = early ? "early.quests-mail" : "final.quests-mail";
            Fields.Apply(QuestManager.manage, BinaryData.Map(m["quests"])); Fields.Apply(MailManager.manage, BinaryData.Map(m["mail"]));
            var pedia = BinaryData.Map(m["pedia"]);
            PediaManager.manage.allEntries = (List<PediaEntry>)Fields.MakeValue(pedia["entries"], typeof(List<PediaEntry>))!;
            CatalogueManager.manage.collectedItem = (bool[])Fields.MakeValue(pedia["catalogue"], typeof(bool[]))!;
            RestoreStage = early ? "early.relations" : "final.relations";
            var relations = BinaryData.Map(m["relations"]); var stable = BinaryData.List(relations["stable"]); var daily = BinaryData.List(relations["daily"]);
            bool sameDay = !early && context != "" && WorldDay != "" && BinaryData.Text(relations["context"]) == context && BinaryData.Text(relations["day"]) == WorldDay;
            for (int i = 0; i < stable.Length; i++) { Fields.Apply(NPCManager.manage.npcStatus[i], BinaryData.Map(stable[i])); if (sameDay) Fields.Apply(NPCManager.manage.npcStatus[i], BinaryData.Map(daily[i])); }
            var vitals = BinaryData.Map(m["vitals"]);
            StatusManager.manage.snagsEaten = checked((int)BinaryData.Number(vitals["snagsEaten"]));
            if (early) { RestoreStage = "early.complete"; return; }
            RestoreStage = "final.stash";
            ContainerManager.manage!.privateStashes.Clear(); ContainerManager.manage.privateStashes.AddRange(restoredStashes!);
            RestoreStage = "final.licences-ui";
            LicenceManager.manage.setLicenceLevelsAndPrice(); inventory.setSlotsUnlocked();
            RestoreStage = "final.vitals";
            StatusManager.manage.loadNewMaxStaminaAndHealth(Convert.ToSingle(vitals["maxStamina"]), checked((int)BinaryData.Number(vitals["maxHealth"])));
            typeof(StatusManager).GetField("stamina", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(StatusManager.manage, Convert.ToSingle(vitals["stamina"]));
            StatusManager.manage.connectedDamge!.CmdChangeHealthTo(checked((int)BinaryData.Number(vitals["health"])));
            RestoreStage = "final.adapters";
            foreach (var ext in m.Where(p => p.Key.StartsWith("ext.", StringComparison.Ordinal))) { var w = BinaryData.Map(ext.Value); PlayerDataAdapters.Find(ext.Key.Substring(4))!.Restore((byte[])((byte[])w["data"]!).Clone(), BinaryData.Text(w["version"])); }
            RestoreStage = "final.equipment";
            CharLevelManager.manage.checkForLevelRecipesAndUnlock(); inventory.equipNewSelectedSlot();
            var equipment = inventory.localChar;
            if (equipment != null)
            {
                equipment.CmdChangeSkin(inventory.skinTone); equipment.CmdChangeHairId(inventory.playerHair); equipment.CmdChangeHairColour(inventory.playerHairColour);
                equipment.CmdChangeEyes(inventory.playerEyes, inventory.playerEyeColor); equipment.CmdChangeFaceId(EquipWindow.equip.faceSlot.itemNo); equipment.CmdChangeFace2Id(inventory.face2Slot);
                equipment.CmdChangeNose(inventory.nose); equipment.CmdChangeMouth(inventory.mouth); equipment.CmdSendEquipedClothes(EquipWindow.equip.getEquipSlotsArray()); equipment.CmdSendName(inventory.playerName, equipment.islandId);
            }
            RestoreStage = "final.complete";
        }
    }
}
