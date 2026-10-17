using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DinkumSaveProtection.Core;

namespace DinkumSaveProtection.Runtime
{
    internal interface INativeRecordFile
    {
        bool Contains(string key);
        object Read(string key);
        void Write(string key, object value);
        void Delete(string key);
    }
    internal sealed class NativeProjection
    {
        internal readonly Dictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Type> RecordTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        internal int PackedItems, Extensions;
        private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException(target.GetType().Name, name);
        private static void Set(object target, string name, object? value) { var f = Field(target, name); f.SetValue(target, Fields.MakeValue(value, f.FieldType)); }
        private static object Create(string name) => Activator.CreateInstance(typeof(PlayerInv).Assembly.GetType(name, true)!)!;
        internal static string Fingerprint(object value) => BinaryData.Hash(BinaryData.Encode(Fields.TakeValue(value)));
        private void Save(INativeRecordFile file, string key, object record) { Expected.Add(key, Fingerprint(record)); RecordTypes.Add(key, record.GetType()); file.Write(key, record); }
        private static void SetArray(object target, string name, object? value)
        {
            if (!(value is object?[] incoming)) throw new InvalidDataException("Required cached native array is missing: " + name);
            if (Field(target,name).GetValue(target) is Array old && old.Length > incoming.Length) throw new InvalidDataException("Native export would truncate " + name);
            Set(target,name,value);
        }
        internal static NativeProjection Apply(INativeRecordFile file, Dictionary<string, object?> m, long savedTime, Func<int, bool>? zeroStackAllowed = null)
        {
            var result = new NativeProjection();
            var assets = BinaryData.Map(m["assets"]); var appearance = BinaryData.Map(assets["appearance"]);
            var inv = file.Read("playerInfo"); var slots = BinaryData.List(assets["slots"]);
            int[] ids = new int[slots.Length], stacks = new int[slots.Length];
            for (int i = 0; i < slots.Length; i++) { var pair = BinaryData.List(slots[i]); ids[i] = checked((int)BinaryData.Number(pair[0])); stacks[i] = checked((int)BinaryData.Number(pair[1])); }
            void Pack(object? value, string label)
            {
                if (value == null) return;
                var pair = BinaryData.List(value); int id = checked((int)BinaryData.Number(pair[0])), stack = checked((int)BinaryData.Number(pair[1]));
                if (id < 0) return;
                if (stack == 0 && !(zeroStackAllowed?.Invoke(id) ?? false)) throw new InvalidDataException("Cannot export zero-count non-fuel " + label + "; source cache retained");
                // Match this audited build's Inventory.setSlotsUnlocked without reading
                // the disconnected player's UI flags. Never hide a packed item in a locked slot.
                if (ids.Length != 44) throw new InvalidDataException("Cannot safely pack " + label + " into a nonstandard native backpack layout");
                var savedLicences = BinaryData.List(BinaryData.Map(BinaryData.Map(m["growth"])["licences"])["allLicences"]);
                if (savedLicences.Length <= 14) throw new InvalidDataException("Cannot determine unlocked native inventory slots");
                long belt = BinaryData.Number(BinaryData.Map(savedLicences[14])["currentLevel"]), bag = BinaryData.Number(BinaryData.Map(savedLicences[12])["currentLevel"]);
                int empty = -1;
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] == -1 && (i < 11 ? i <= 11 - (4 - belt) : i <= ids.Length - (10 - bag * 3))) { empty = i; break; }
                if (empty < 0) throw new InvalidDataException("Cannot export " + label + ": no empty backpack slot. Cache and native file retained; reconnect and put the item away first.");
                ids[empty] = id; stacks[empty] = stack; result.PackedItems++;
            }
            var gear = BinaryData.List(assets["gear"]);
            if (gear.Length != 6) throw new InvalidDataException("Unsupported native gear layout");
            Pack(assets["drag"], "cursor item"); Pack(gear[5], "idol equipment");
            foreach (var pair in new[] { ("playerName", "playerName"), ("islandName", "islandName"), ("hair", "playerHair"), ("hairColour", "playerHairColour"), ("eyeStyle", "playerEyes"), ("eyeColour", "playerEyeColor"), ("skinTone", "skinTone"), ("nose", "nose"), ("mouth", "mouth"), ("face2", "face2Slot"), ("isCreative", "isCreative"), ("hasBeenCreative", "hasBeenCreative") }) Set(inv, pair.Item1, appearance[pair.Item2]);
            SetArray(inv, "itemsInInvSlots", ids.Select(v => (object?)(long)v).ToArray()); SetArray(inv, "stacksInSlots", stacks.Select(v => (object?)(long)v).ToArray());
            foreach (var pair in new[] { (0, "face", "faceStack"), (1, "head", "headStack"), (2, "body", "bodyStack"), (3, "pants", "pantsStack"), (4, "shoes", "shoesStack") })
            {
                if (gear[pair.Item1] == null) throw new InvalidDataException("Missing native equipment slot");
                var item = BinaryData.List(gear[pair.Item1]); Set(inv, pair.Item2, item[0]); Set(inv, pair.Item3, item[1]);
            }
            Set(inv, "money", assets["wallet"]); var bank = BinaryData.Map(assets["bank"]);
            Set(inv, "bankBalance", bank["accountBalance"]); Set(inv, "accountOverflow", bank["accountOverflow"]);
            var vitals = BinaryData.Map(m["vitals"]);
            foreach (var pair in new[] { ("health", "health"), ("healthMax", "maxHealth"), ("stamina", "stamina"), ("staminaMax", "maxStamina"), ("snagsEaten", "snagsEaten") }) Set(inv, pair.Item1, vitals[pair.Item2]);
            SetArray(inv, "catalogue", BinaryData.Map(m["pedia"])["catalogue"]); Set(inv, "savedTime", savedTime);
            result.Save(file, "playerInfo", inv);
            var growth = BinaryData.Map(m["growth"]); var levels = BinaryData.Map(growth["levels"]);
            var levelSave = file.Read("levels"); foreach (string key in new[] { "todaysXp", "currentXp", "currentLevels" }) SetArray(levelSave, key, levels[key]); result.Save(file, "levels", levelSave);
            var recipes = file.Read("recipes"); SetArray(recipes, "recipesUnlocked", levels["recipes"]); result.Save(file, "recipes", recipes);
            var licences = file.Read("licences"); SetArray(licences, "licenceSave", BinaryData.Map(growth["licences"])["allLicences"]);
            SetArray(licences, "milestoneSave", BinaryData.Map(growth["milestones"])["milestones"]); Set(licences, "permitPoints", growth["points"]); Set(licences, "tiredDistance", growth["distance"]); result.Save(file, "licences", licences);
            var quests = file.Read("quests"); foreach (var p in BinaryData.Map(m["quests"])) SetArray(quests, p.Key == "isQuestAccepted" ? "accepted" : "completed", p.Value); result.Save(file, "quests", quests);
            var pedia = file.Read("pedia"); SetArray(pedia, "entries", BinaryData.Map(m["pedia"])["entries"]); result.Save(file, "pedia", pedia);
            var npc = file.Read("npc"); var statusField = Field(npc, "savedStatuses"); var old = statusField.GetValue(npc) as Array;
            var stable = BinaryData.List(BinaryData.Map(m["relations"])["stable"]);
            if (old != null && old.Length > stable.Length) throw new InvalidDataException("NPC export would truncate native records");
            Type statusType = statusField.FieldType.GetElementType()!; var statuses = Array.CreateInstance(statusType, stable.Length);
            for (int i = 0; i < stable.Length; i++)
            {
                object value = i < (old?.Length ?? 0) && old!.GetValue(i) != null ? old.GetValue(i)! : Activator.CreateInstance(statusType)!;
                foreach (var p in BinaryData.Map(stable[i])) Set(value, p.Key, p.Value);
                statuses.SetValue(value, i);
            }
            statusField.SetValue(npc, statuses); result.Save(file, "npc", npc); // preserve inventories and non-personal NPC fields
            var mail = file.Contains("mail") ? file.Read("mail") : Create("MailSave"); var letters = BinaryData.Map(m["mail"]);
            Set(mail, "allMail", letters["mailInBox"]); Set(mail, "tomorrowsMail", letters["tomorrowsLetters"]); result.Save(file, "mail", mail);
            var stashes = BinaryData.List(m["stash"]);
            if (stashes.Length > 2) throw new InvalidDataException("Vanilla loads only two private stashes; refusing to discard additional stashes");
            file.Delete("stash_0"); file.Delete("stash_1");
            for (int i = 0; i < stashes.Length; i++)
            {
                var c = BinaryData.Map(stashes[i]); if (BinaryData.List(c["itemIds"]).Length != 24 || BinaryData.List(c["itemStacks"]).Length != 24) throw new InvalidDataException("Native stash needs exactly 24 slots; cache retained");
                var record = Create("ChestSave"); Set(record, "itemId", c["itemIds"]); Set(record, "itemStack", c["itemStacks"]); Set(record, "xPos", c["xPos"]); Set(record, "yPos", c["yPos"]);
                bool inside = c["inside"] is bool b && b; Set(record, "houseX", inside ? c["insideX"] : -1L); Set(record, "houseY", inside ? c["insideY"] : -1L);
                result.Save(file, "stash_" + i, record);
            }
            result.Extensions = m.Keys.Count(k => k.StartsWith("ext.", StringComparison.Ordinal));
            return result;
        }
        internal void Verify(INativeRecordFile file)
        {
            foreach (var p in Expected) if (!file.Contains(p.Key) || Fingerprint(file.Read(p.Key)) != p.Value) throw new InvalidDataException("Native round-trip verification failed: " + p.Key);
        }
    }
}
