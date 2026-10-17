using DinkumSaveProtection.Core;
using DinkumSaveProtection.Runtime;
using System.Reflection;

// NativeValidation.cs is compiled unchanged into this test executable against these
// in-memory stand-ins. No Unity, ES3 or game assembly is loaded or executed.
internal static class NativeValidationFixture
{
    private const string Source = "fixture/Player.es3";
    private static void Check(bool condition) { if (!condition) throw new Exception("Native preflight fixture failed"); }
    private static NativeCompatibilityException Incompatible(Action action, string field)
    {
        try { action(); } catch (NativeCompatibilityException e) { Check(e.Message.Contains(field)); return e; }
        throw new Exception("Expected native compatibility refusal: " + field);
    }
    private static Dictionary<string, object> Save() => new()
    {
        ["playerInfo"] = new PlayerInv(), ["quests"] = new QuestSave(), ["levels"] = new LevelSave(),
        ["recipes"] = new RecipesUnlockedSave(), ["licences"] = new LicenceAndPermitPointSave(),
        ["pedia"] = new PediaSave(), ["npc"] = new NPCsave()
    };
    private static void Reset()
    {
        Inventory.Instance = new Inventory(); CatalogueManager.manage = new CatalogueManager(); CharLevelManager.manage = new CharLevelManager();
        QuestManager.manage = new QuestManager(); LicenceManager.manage = new LicenceManager(); MilestoneManager.manage = new MilestoneManager();
        PediaManager.manage = new PediaManager(); NPCManager.manage = new NPCManager();
        ES3.Files.Clear(); ES3.Errors.Clear(); ES3.Reads.Clear(); ES3.Files[Source] = Save();
    }
    private static string? Select() => NativeFileRecovery.Select(Source, p => ES3.Files.ContainsKey(p) || ES3.Errors.ContainsKey(p), NativeValidation.Validate);
    internal static void Run(Action<string, Action> test)
    {
        void Case(string name, Action action) => test(name, () => { Reset(); action(); });
        Case("native preflight accepts 32 saved slots into 44 and shorter catalogue without mutation", () =>
        {
            var inv = (PlayerInv)ES3.Files[Source]["playerInfo"]; inv.itemsInInvSlots[3] = 2; inv.stacksInSlots[3] = 9; inv.catalogue![5] = true;
            var ids = inv.itemsInInvSlots.ToArray(); var catalogue = inv.catalogue.ToArray(); var runtimeCatalogue = CatalogueManager.manage.collectedItem;
            Check(Select() == null); Check(inv.itemsInInvSlots.SequenceEqual(ids) && inv.catalogue.SequenceEqual(catalogue));
            Check(ReferenceEquals(runtimeCatalogue, CatalogueManager.manage.collectedItem) && runtimeCatalogue.All(v => !v));
        });
        Case("native historical optional arrays may be null as allowed by native loader", () =>
        {
            ((PlayerInv)ES3.Files[Source]["playerInfo"]).catalogue = null;
            ((LevelSave)ES3.Files[Source]["levels"]).todaysXp = null;
            ((LicenceAndPermitPointSave)ES3.Files[Source]["licences"]).milestoneSave = null;
            ((PediaSave)ES3.Files[Source]["pedia"]).entries = null;
            ((NPCsave)ES3.Files[Source]["npc"]).savedStatuses = null;
            Check(Select() == null);
        });
        Case("native preflight accepts safe prefix growth across personal modules", () =>
        {
            Check(((QuestSave)ES3.Files[Source]["quests"]).completed.Length < QuestManager.manage.isQuestCompleted.Length);
            Check(((RecipesUnlockedSave)ES3.Files[Source]["recipes"]).recipesUnlocked.Length < CharLevelManager.manage.recipes.Count);
            Check(Select() == null);
        });
        Case("readable incompatible inventory never probes or replaces an older backup", () =>
        {
            var inv = (PlayerInv)ES3.Files[Source]["playerInfo"]; inv.itemsInInvSlots = new int[45]; inv.stacksInSlots = new int[45];
            ES3.Files[Source + ".bak"] = Save(); Incompatible(() => Select(), "saved=45, runtime=44");
            Check(ES3.Reads.All(p => p == Source)); Check(ReferenceEquals(inv, ES3.Files[Source]["playerInfo"]));
        });
        Case("native paired array mismatch is compatibility failure with field details", () =>
        {
            ((PlayerInv)ES3.Files[Source]["playerInfo"]).stacksInSlots = new int[31];
            Incompatible(() => Select(), "left=32, right=31");
        });
        Case("native oversized catalogue and missing item mapping do not authorize rollback", () =>
        {
            var inv = (PlayerInv)ES3.Files[Source]["playerInfo"]; inv.catalogue = new bool[2175];
            Incompatible(() => Select(), "playerInfo.catalogue");
            inv.catalogue = null; inv.itemsInInvSlots[0] = 2174;
            Incompatible(() => Select(), "id=2174");
        });
        Case("native replacement skill arrays remain exact while optional todaysXp can be absent", () =>
        {
            var levels = (LevelSave)ES3.Files[Source]["levels"]; levels.todaysXp = null; Check(Select() == null);
            levels.currentLevels = new int[6]; Incompatible(() => Select(), "levels.currentLevels");
        });
        Case("uninitialized runtime is not mistaken for a damaged native file", () =>
        {
            Inventory.Instance.invSlots = Array.Empty<InventorySlot>(); ES3.Files[Source + ".bak"] = Save();
            Incompatible(() => Select(), "runtime collection not initialized"); Check(ES3.Reads.All(p => p == Source));
        });
        Case("missing required native module pauses without substituting older progress", () =>
        {
            ES3.Files[Source].Remove("quests"); ES3.Files[Source + ".bac"] = Save();
            Incompatible(() => Select(), "Missing native module: quests"); Check(ES3.Reads.All(p => p == Source));
        });
        Case("native truncating recipe arrays are rejected before game application", () =>
        {
            ((RecipesUnlockedSave)ES3.Files[Source]["recipes"]).recipesUnlocked = new RecipeToUnlock[21];
            Incompatible(() => Select(), "recipes.recipesUnlocked");
        });
        Case("decode failure selects only a fully validated backup and reports failed candidates", () =>
        {
            ES3.Files[Source + ".bak"] = Save(); ((PlayerInv)ES3.Files[Source + ".bak"]["playerInfo"]).stacksInSlots = new int[1];
            ES3.Files[Source + ".bac"] = Save(); var warnings = new List<string>();
            foreach (var failure in new Exception[] { new FormatException("Damaged fixture bytes"), new InvalidDataException("Invalid compression"), new System.Security.Cryptography.CryptographicException("Invalid ciphertext") })
            {
                ES3.Errors[Source] = failure;
                string? chosen = NativeFileRecovery.Select(Source, p => ES3.Files.ContainsKey(p), NativeValidation.Validate, warnings.Add);
                Check(chosen == Source + ".bac" && warnings.Any(w => w.Contains(".bak") && w.Contains("paired arrays")));
            }
            Check(ES3.Errors.ContainsKey(Source)); // selector does not write or repair files
        });
        Case("a missing native file may use a validated backup", () =>
        {
            ES3.Files.Remove(Source); ES3.Files[Source + ".bak"] = Save(); Check(Select() == Source + ".bak");
        });
        Case("unexpected code errors never trigger native backup replacement", () =>
        {
            foreach (var failure in new Exception[] { new NullReferenceException("Initialization bug"), new IOException("Sharing violation"), new UnauthorizedAccessException("Permissions") })
            {
                int calls = 0; bool rejected = false;
                try { NativeFileRecovery.Select(Source, _ => true, _ => { calls++; throw failure; }); }
                catch (Exception e) { rejected = ReferenceEquals(e, failure); }
                Check(rejected && calls == 1);
            }
        });
    }
}

public static class ES3
{
    public enum Location { File, Cache, InternalMS }
    public static readonly Dictionary<string, Dictionary<string, object>> Files = new();
    public static readonly Dictionary<string, Exception> Errors = new();
    public static readonly List<string> Reads = new();
    public static bool KeyExists(string key, string path)
    { Reads.Add(path); if (Errors.TryGetValue(path, out var e)) throw e; return Files.TryGetValue(path, out var file) && file.ContainsKey(key); }
    public static void LoadInto(string key, string path, object target)
    {
        var source = Files[path][key];
        foreach (var field in source.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(target, field.GetValue(source));
    }
}
[Serializable] public sealed class PlayerInv
{
    public int[] itemsInInvSlots = Enumerable.Repeat(-1, 32).ToArray(), stacksInSlots = new int[32];
    public bool[]? catalogue = new bool[1024];
    public int face = -1, head = -1, body = -1, pants = -1, shoes = -1;
    public int faceStack, headStack, bodyStack, pantsStack, shoesStack;
    public float stamina = 100, staminaMax = 100;
    public int health = 100, healthMax = 100, money, bankBalance, snagsEaten, face2 = -1;
    public int hair, hairColour, eyeStyle, eyeColour, skinTone, nose, mouth;
    public string playerName = "BasePlayer", islandName = "BaseIsland";
    public long bankBalanceOverflow, savedTime;
    public ulong accountOverflow;
    public bool isCreative, hasBeenCreative;
}
public sealed class Inventory : UnityEngine.Object
{
    public static Inventory Instance = new();
    public InventorySlot[] invSlots = Enumerable.Range(0, 44).Select(_ => new InventorySlot()).ToArray();
    public InventoryItem?[] allItems = Enumerable.Range(0, 2174).Select(i => (InventoryItem?)new InventoryItem { name = "item" + i, itemName = "item" + i, tag = "item" }).ToArray();
    public InventorySlot walletSlot = new(), dragSlot = new();
    public EquipItemToChar? localChar;
    public string playerName = "Fixture", islandName = "FixtureIsland";
    public int playerHair, playerEyes, playerEyeColor, playerHairColour, skinTone, nose, mouth, face2Slot;
    public bool isCreative, hasBeenCreative;
    public void changeWalletToLoad(int value) => walletSlot.stack = value;
    public void setSlotsUnlocked() { FixtureRuntime.RequireActor(); DerivedRefreshes++; }
    public void equipNewSelectedSlot() { FixtureRuntime.RequireActor(); EquipRefreshes++; }
    public int DerivedRefreshes, EquipRefreshes;
}
public sealed class InventoryItem : UnityEngine.Object { public string name = "", itemName = "", tag = ""; public bool hasFuel; }
public sealed class InventorySlot : UnityEngine.Object
{
    public int itemNo = -1, stack, Refreshes;
    public void refreshSlot(bool playAnimation) => Refreshes++;
}
public sealed class EquipWindow : UnityEngine.Object
{
    public static EquipWindow equip = new();
    public InventorySlot faceSlot = new(), hatSlot = new(), shirtSlot = new(), pantsSlot = new(), shoeSlot = new(), idolSlot = new();
    public int[] getEquipSlotsArray() => new[] { faceSlot.itemNo, hatSlot.itemNo, shirtSlot.itemNo, pantsSlot.itemNo, shoeSlot.itemNo, idolSlot.itemNo };
}
public sealed class EquipItemToChar : UnityEngine.Object
{
    public int islandId, Commands;
    private void Sent() { FixtureRuntime.RequireActor(); Commands++; }
    public void CmdChangeSkin(int v) => Sent();
    public void CmdChangeHairId(int v) => Sent();
    public void CmdChangeHairColour(int v) => Sent();
    public void CmdChangeEyes(int a, int b) => Sent();
    public void CmdChangeFaceId(int v) => Sent();
    public void CmdChangeFace2Id(int v) => Sent();
    public void CmdChangeNose(int v) => Sent();
    public void CmdChangeMouth(int v) => Sent();
    public void CmdSendEquipedClothes(int[] v) => Sent();
    public void CmdSendName(string name, int island) => Sent();
}
public sealed class BankMenu : UnityEngine.Object { public static BankMenu menu = new(); public int accountBalance; public ulong accountOverflow; }
public sealed class CatalogueManager : UnityEngine.Object { public static CatalogueManager manage = new(); public bool[] collectedItem = new bool[2174]; }
public sealed class QuestManager : UnityEngine.Object { public static QuestManager manage = new(); public bool[] isQuestAccepted = new bool[12], isQuestCompleted = new bool[12]; }
public sealed class CharLevelManager : UnityEngine.Object
{
    public static CharLevelManager manage = new();
    public int[] todaysXp = new int[7], currentXp = new int[7], currentLevels = new int[7];
    public List<RecipeToUnlock> recipes = Enumerable.Range(0, 20).Select(i => new RecipeToUnlock { recipeId = i }).ToList();
    public int RecipeRefreshes;
    public void checkForLevelRecipesAndUnlock() { FixtureRuntime.RequireActor(); RecipeRefreshes++; }
}
[Serializable] public sealed class RecipeToUnlock { public int recipeId; public bool unlocked; }
[Serializable] public sealed class Licence { public int level, currentLevel; }
[Serializable] public sealed class Milestone { public int points; }
public sealed class LicenceManager : UnityEngine.Object
{
    public static LicenceManager manage = new();
    public Licence[] allLicences = Enumerable.Range(0, 20).Select(_ => new Licence()).ToArray();
    public int Refreshes;
    public void setLicenceLevelsAndPrice() { FixtureRuntime.RequireActor(); Refreshes++; }
}
public sealed class MilestoneManager : UnityEngine.Object { public static MilestoneManager manage = new(); public List<Milestone> milestones = Enumerable.Range(0, 20).Select(_ => new Milestone()).ToList(); }
[Serializable] public sealed class PediaEntry { public int itemId, amountCaught; }
public sealed class PediaManager : UnityEngine.Object { public static PediaManager manage = new(); public List<PediaEntry> allEntries = Enumerable.Range(0, 20).Select(i => new PediaEntry { itemId = i }).ToList(); }
[Serializable] public sealed class NPCStatus
{
    public int relationshipLevel, moneySpentAtStore, howManyDaysSincePlayerInteract, lastTextSaidSlot;
    public bool hasMet, hasBeenTalkedToToday, hasGossipedToday, hasHungOutToday, acceptedRequest, completedRequest;
    public int[] last3TextSaid = new int[3];
    public bool hasMovedIn, hasAskedToMoveIn;
    public int[]? dateMovedIn;
}
public sealed class NPCManager : UnityEngine.Object { public static NPCManager manage = new(); public List<NPCStatus> npcStatus = Enumerable.Range(0, 20).Select(_ => new NPCStatus()).ToList(); }
public sealed class PermitPointsManager : UnityEngine.Object
{
    public static PermitPointsManager manage = new(); private int permitPoints;
    public int getCurrentPoints() => permitPoints;
    public void loadFromSave(int points) => permitPoints = points;
}
public sealed class DailyTaskGenerator : UnityEngine.Object { public static DailyTaskGenerator generate = new(); public int tiredDistance; }
[Serializable] public sealed class Letter { public int id; }
public sealed class MailManager : UnityEngine.Object { public static MailManager manage = new(); public List<Letter> mailInBox = new(), tomorrowsLetters = new(); }
[Serializable] public sealed class Chest
{
    public bool inside; public int xPos, yPos, insideX = -1, insideY = -1;
    public int[] itemIds = Enumerable.Repeat(-1, 24).ToArray(), itemStacks = new int[24];
    public Chest(int x, int y) { xPos = x; yPos = y; }
}
public sealed class ContainerManager : UnityEngine.Object { public static ContainerManager? manage; public List<Chest> privateStashes = new(); }
public sealed class StatusManager : UnityEngine.Object
{
    public static StatusManager manage = new(); public Damageable? connectedDamge; public int snagsEaten;
    private float stamina = 100, staminaMax = 100;
    public float getStamina() => stamina;
    public float getStaminaMax() => staminaMax;
    public void loadNewMaxStaminaAndHealth(float s, int h) { FixtureRuntime.RequireActor(); staminaMax = s; connectedDamge!.maxHealth = h; }
}
public sealed class Damageable : UnityEngine.Object
{
    public int health = 100, maxHealth = 100, Commands;
    public void CmdChangeHealthTo(int v) { FixtureRuntime.RequireActor(); health = v; Commands++; }
}
public sealed class NetworkMapSharer : UnityEngine.Object { public static NetworkMapSharer? Instance; public UnityEngine.Object? localChar; }
public sealed class WorldManager : UnityEngine.Object { public static WorldManager? Instance; public int year = 1, month = 1, week = 1, day = 1; }
internal static class FixtureRuntime
{
    internal static void RequireActor()
    {
        if (Inventory.Instance.localChar == null || StatusManager.manage.connectedDamge == null || NetworkMapSharer.Instance?.localChar == null)
            throw new InvalidOperationException("Actor-dependent method called before spawn");
    }
    internal static void Reset(bool spawned)
    {
        Inventory.Instance = new Inventory(); EquipWindow.equip = new EquipWindow(); BankMenu.menu = new BankMenu();
        CatalogueManager.manage = new CatalogueManager(); QuestManager.manage = new QuestManager(); CharLevelManager.manage = new CharLevelManager();
        LicenceManager.manage = new LicenceManager(); MilestoneManager.manage = new MilestoneManager(); PediaManager.manage = new PediaManager();
        NPCManager.manage = new NPCManager(); PermitPointsManager.manage = new PermitPointsManager(); DailyTaskGenerator.generate = new DailyTaskGenerator();
        MailManager.manage = new MailManager(); StatusManager.manage = new StatusManager();
        ContainerManager.manage = null; NetworkMapSharer.Instance = null; WorldManager.Instance = null;
        if (spawned) Spawn();
    }
    internal static void Spawn()
    {
        ContainerManager.manage = new ContainerManager(); Inventory.Instance.localChar = new EquipItemToChar();
        StatusManager.manage.connectedDamge = new Damageable();
        NetworkMapSharer.Instance = new NetworkMapSharer { localChar = new UnityEngine.Object() };
        WorldManager.Instance = new WorldManager();
    }
}
[Serializable] public sealed class QuestSave { public bool[] accepted = new bool[10], completed = new bool[10]; public bool[]? hasBeencrafted; public int[]? dailyTasks; }
[Serializable] public sealed class LevelSave { public int[]? todaysXp = new int[7]; public int[] currentXp = new int[7], currentLevels = new int[7]; }
[Serializable] public sealed class RecipesUnlockedSave { public RecipeToUnlock[] recipesUnlocked = Enumerable.Range(0,10).Select(i => new RecipeToUnlock { recipeId = i }).ToArray(); public int crafterLevel, currentPoints, crafterWorkingOnItemId = -1; public bool crafterCurrentlyWorking, crafterHasBerkonium; }
[Serializable] public sealed class LicenceAndPermitPointSave { public Licence[] licenceSave = Enumerable.Range(0,10).Select(_ => new Licence()).ToArray(); public Milestone[]? milestoneSave = Enumerable.Range(0,10).Select(_ => new Milestone()).ToArray(); public int permitPoints, tiredDistance; }
[Serializable] public sealed class PediaSave { public PediaEntry[]? entries = Enumerable.Range(0,10).Select(i => new PediaEntry { itemId = i }).ToArray(); }
[Serializable] public sealed class NPCInventory { public int[] inventorySlots = new int[24], inventoryStacks = new int[24]; }
[Serializable] public sealed class NPCsave { public NPCStatus[]? savedStatuses = Enumerable.Range(0, 10).Select(_ => new NPCStatus()).ToArray(); public NPCInventory[]? saveInvs; }
[Serializable] public sealed class MailSave { public Letter[]? allMail = Array.Empty<Letter>(), tomorrowsMail = Array.Empty<Letter>(); }
[Serializable] public sealed class ChestSave { public int[] itemId = new int[24], itemStack = new int[24]; public int xPos, yPos, houseX = -1, houseY = -1; }

namespace UnityEngine { public class Object { } }
namespace BepInEx.Bootstrap
{
    public static class Chainloader { public static readonly Dictionary<string, FixturePluginInfo> PluginInfos = new(); }
    public sealed class FixturePluginInfo { public FixtureMetadata Metadata = new(); }
    public sealed class FixtureMetadata { public Version Version = new(1, 0); }
}
