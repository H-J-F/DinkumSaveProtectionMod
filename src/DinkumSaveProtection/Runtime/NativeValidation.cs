using System;
using System.Reflection;
using DinkumSaveProtection.Core;

namespace DinkumSaveProtection.Runtime
{
    internal static class NativeValidation
    {
        internal static readonly string[] Required = { "playerInfo", "quests", "levels", "recipes", "licences", "pedia", "npc" };
        private static object Read(string path, string key, string typeName)
        {
            if (!ES3.KeyExists(key, path)) throw new NativeCompatibilityException("Missing native module: " + key + "; readable source retained");
            var type = typeof(PlayerInv).Assembly.GetType(typeName, true)!;
            var value = Activator.CreateInstance(type)!;
            ES3.LoadInto(key, path, value);
            return value;
        }
        private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value);
        private static Array? ArrayField(object value, string name) => Field(value, name) as Array;
        internal static void Validate(string path)
        {
            var inv = (PlayerInv)Read(path, "playerInfo", "PlayerInv");
            if (Inventory.Instance?.invSlots == null || Inventory.Instance.allItems == null || Inventory.Instance.allItems.Length == 0 ||
                CatalogueManager.manage?.collectedItem == null || CharLevelManager.manage == null || QuestManager.manage == null ||
                LicenceManager.manage == null || MilestoneManager.manage == null || PediaManager.manage == null || NPCManager.manage == null)
                throw new NativeCompatibilityException("Native preflight requires initialized game collections; no file was changed");
            NativeLayoutRules.Pair("playerInfo.inventory", inv.itemsInInvSlots, inv.stacksInSlots);
            NativeLayoutRules.Prefix("playerInfo.inventory", inv.itemsInInvSlots, Inventory.Instance.invSlots.Length);
            // Native LoadInv copies a prefix and grows catalogue with Math.Max; null catalogue is optional.
            NativeLayoutRules.Prefix("playerInfo.catalogue", inv.catalogue, CatalogueManager.manage.collectedItem.Length, true);
            for (int i = 0; i < inv.itemsInInvSlots.Length; i++) ValidateSlot(inv.itemsInInvSlots[i], inv.stacksInSlots[i]);
            foreach (var pair in new[] { (inv.face, inv.faceStack), (inv.head, inv.headStack), (inv.body, inv.bodyStack), (inv.pants, inv.pantsStack), (inv.shoes, inv.shoesStack) }) ValidateSlot(pair.Item1, pair.Item2);
            if (float.IsNaN(inv.stamina) || float.IsInfinity(inv.stamina) || float.IsNaN(inv.staminaMax) || float.IsInfinity(inv.staminaMax))
                throw new NativeCompatibilityException("Invalid native stamina; readable source retained");
            var quests = Read(path, "quests", "QuestSave");
            var accepted = ArrayField(quests, "accepted"); var completed = ArrayField(quests, "completed");
            NativeLayoutRules.Pair("quests.accepted/completed", accepted, completed);
            NativeLayoutRules.Prefix("quests.accepted", accepted, QuestManager.manage.isQuestAccepted.Length);
            NativeLayoutRules.Prefix("quests.completed", completed, QuestManager.manage.isQuestCompleted.Length);
            var levels = Read(path, "levels", "LevelSave");
            foreach (string field in new[] { "todaysXp", "currentXp", "currentLevels" })
                NativeLayoutRules.Exact("levels." + field, ArrayField(levels, field),
                    (typeof(CharLevelManager).GetField(field)!.GetValue(CharLevelManager.manage) as Array)?.Length ?? 0, field == "todaysXp");
            var recipes = Read(path, "recipes", "RecipesUnlockedSave");
            NativeLayoutRules.Prefix("recipes.recipesUnlocked", ArrayField(recipes, "recipesUnlocked"), CharLevelManager.manage.recipes.Count);
            var licences = Read(path, "licences", "LicenceAndPermitPointSave");
            NativeLayoutRules.Prefix("licences.licenceSave", ArrayField(licences, "licenceSave"), LicenceManager.manage.allLicences.Length);
            NativeLayoutRules.Prefix("licences.milestoneSave", ArrayField(licences, "milestoneSave"), MilestoneManager.manage.milestones.Count, true);
            NativeLayoutRules.Prefix("pedia.entries", ArrayField(Read(path, "pedia", "PediaSave"), "entries"), PediaManager.manage.allEntries.Count, true);
            var statuses = ArrayField(Read(path, "npc", "NPCsave"), "savedStatuses");
            NativeLayoutRules.Prefix("npc.savedStatuses", statuses, NPCManager.manage.npcStatus.Count, true);
            if (statuses != null) foreach (var status in statuses) if (status == null) throw new NativeCompatibilityException("npc.savedStatuses contains a null entry");
            if (ES3.KeyExists("mail", path) && ArrayField(Read(path, "mail", "MailSave"), "allMail") == null)
                throw new NativeCompatibilityException("mail.allMail is null");
            for (int i = 0; i < 2; i++) if (ES3.KeyExists("stash_" + i, path))
            {
                var chest = (ChestSave)Read(path, "stash_" + i, "ChestSave");
                NativeLayoutRules.Pair("stash_" + i, chest.itemId, chest.itemStack);
                for (int j = 0; j < chest.itemId.Length; j++) ValidateSlot(chest.itemId[j], chest.itemStack[j]);
            }
        }
        private static void ValidateSlot(int id, int stack) =>
            NativeLayoutRules.Item("playerInfo/stash.item", id, stack, Inventory.Instance.allItems.Length, i => Inventory.Instance.allItems[i] != null);
    }
}
