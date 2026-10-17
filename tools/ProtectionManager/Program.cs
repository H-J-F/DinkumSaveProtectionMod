using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using DinkumSaveProtection.Core;

try
{
    if (args.Length == 0) { Console.WriteLine("ProtectionManager inspect|install|uninstall|rollback --game <directory> [--package <directory>]\nProtectionManager list|select --store <directory> --character <uuid> [--snapshot <hash>]\nProtectionManager self-test --plugin <DLL> --bepinex <core directory>\ninspect is read-only; other operations must be explicitly selected. Does not launch games or install loaders."); return 0; }
    string Option(string key) { int i = Array.IndexOf(args, "--" + key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing --" + key); }
    switch (args[0])
    {
        case "self-test": Manager.SelfTest(Option("plugin"), Option("bepinex")); break;
        case "list":
        case "select":
            var store = new SnapshotStore(Option("store"), false); var character = Option("character");
            if (args[0] == "select") { Manager.RequireGameStopped(); var id = Option("snapshot"); var snapshot = store.Find(id) ?? throw new InvalidDataException("Unknown snapshot"); if (snapshot.Character != character) throw new InvalidDataException("Wrong character"); store.SelectBranch(id); }
            foreach (var snapshot in store.ValidVersions(character)) Console.WriteLine($"{snapshot.Id} session={snapshot.Session} sequence={snapshot.Sequence} parent={snapshot.Parent}");
            break;
        case "inspect": Manager.Inspect(Option("game")); break;
        case "install": Manager.Install(Option("game"), Option("package")); break;
        case "uninstall": Manager.Uninstall(Option("game")); break;
        case "rollback": Manager.Rollback(Option("game")); break;
        default: throw new ArgumentException("Unknown command");
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine("FAILED: " + e.Message); return 1; }

internal sealed record Receipt(string Owner, string Version, Dictionary<string, string> Files);
internal sealed record AssemblyInfo(string Name, Version Version, string[] PluginIds);
internal static class Manager
{
    internal const string Owner = "io.dinkumsaveprotection.playerdata";
    private const string Dll = "DinkumSaveProtection.dll", Manifest = "install-manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string? selfTestRoot;
    internal static void RequireGameStopped()
    {
        var processes = Process.GetProcessesByName("Dinkum");
        try { if (processes.Length != 0) throw new InvalidOperationException("Close Dinkum before modifying installed plugins or selecting recovery branches"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static void RequireGameStoppedFor(string game)
    {
        // Only the randomly generated, explicitly scoped fixture can run beside a live game.
        // Real installations and the separate recovery-branch command retain the global guard.
        if (selfTestRoot != null && Full(game).StartsWith(selfTestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        RequireGameStopped();
    }
    private static string PluginDir(string game) => Path.Combine(Full(game), "BepInEx", "plugins", "DinkumSaveProtection");
    private static string Backups(string game) => Path.Combine(Full(game), "BepInEx", "DinkumSaveProtection-backups");
    // Check every existing ancestor and child. Reparse points must not escape the chosen game tree.
    private static void Safe(string path)
    {
        string? current = Full(path);
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing reparse point: " + current);
            current = Path.GetDirectoryName(current);
        }
    }
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static Receipt ReadReceipt(string directory)
    {
        Safe(directory); string manifestPath = Path.Combine(directory, Manifest); Safe(manifestPath);
        if (new FileInfo(manifestPath).Length > 65536) throw new InvalidDataException("Oversized installation receipt");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Invalid installation receipt");
        if (receipt.Owner != Owner || receipt.Files.Count != 1 || !receipt.Files.ContainsKey(Dll) || !BinaryData.IsHash(receipt.Files[Dll])) throw new InvalidDataException("Receipt is not owned by this plugin");
        foreach (var item in receipt.Files) { var path = Path.Combine(directory, item.Key); Safe(path); if (!File.Exists(path) || Digest(path) != item.Value) throw new InvalidDataException("Owned file was modified or is missing: " + path); }
        return receipt;
    }
    private static AssemblyInfo Metadata(string file)
    {
        using var stream = File.OpenRead(file); using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new BadImageFormatException("Not a managed assembly");
        var reader = pe.GetMetadataReader(); var assembly = reader.GetAssemblyDefinition(); var ids = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            foreach (var attributeHandle in reader.GetTypeDefinition(handle).GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle); EntityHandle type;
                if (attribute.Constructor.Kind == HandleKind.MemberReference) type = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                else if (attribute.Constructor.Kind == HandleKind.MethodDefinition) type = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                else continue;
                string name = type.Kind == HandleKind.TypeReference ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Name) : type.Kind == HandleKind.TypeDefinition ? reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)type).Name) : "";
                if (name != "BepInPlugin") continue;
                var blob = reader.GetBlobReader(attribute.Value); if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid plugin metadata");
                var guid = blob.ReadSerializedString(); if (guid != null) ids.Add(guid);
            }
        }
        return new AssemblyInfo(reader.GetString(assembly.Name), assembly.Version, ids.ToArray());
    }
    private static AssemblyInfo[] Scan(string game, bool excludeOwned)
    {
        var results = new List<AssemblyInfo>(); var plugins = Path.Combine(Full(game), "BepInEx", "plugins"); if (!Directory.Exists(plugins)) return results.ToArray();
        // Explicit walk checks links before following them.
        var folders = new Stack<string>(); folders.Push(plugins);
        while (folders.Count != 0)
        {
            string folder = folders.Pop(); Safe(folder);
            foreach (var child in Directory.EnumerateDirectories(folder)) { Safe(child); folders.Push(child); }
            foreach (var file in Directory.EnumerateFiles(folder, "*.dll"))
            {
                Safe(file); if (excludeOwned && string.Equals(Full(file), Path.Combine(PluginDir(game), Dll), StringComparison.OrdinalIgnoreCase)) continue;
                try { results.Add(Metadata(file)); } catch (BadImageFormatException) { /* Native libraries do not define managed plugin IDs. */ }
            }
        }
        return results.ToArray();
    }
    private static void CheckLoader(string game)
    {
        string core = Path.Combine(Full(game), "BepInEx", "core", "BepInEx.dll"); Safe(core);
        if (!File.Exists(core)) throw new InvalidOperationException("BepInEx 5 is missing. Install the loader as a separate prerequisite; this tool will not alter core or winhttp.dll.");
        var info = Metadata(core); if (info.Name != "BepInEx" || info.Version.Major != 5 || info.Version < new Version(5, 4, 23, 0)) throw new InvalidOperationException("Unsupported BepInEx; baseline is official 5.4.23.5 x64. Existing loader will not be replaced.");
        string harmony = Path.Combine(Path.GetDirectoryName(core)!, "0Harmony.dll"); Safe(harmony); if (!File.Exists(harmony) || Metadata(harmony).Version.Major != 2) throw new InvalidOperationException("Compatible HarmonyX is required");
        string exe = Path.Combine(Full(game), "Dinkum.exe"); if (!File.Exists(exe)) throw new InvalidOperationException("Dinkum.exe is missing");
        using var stream = File.OpenRead(exe); using var pe = new PEReader(stream); if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidOperationException("An x64 game/loader installation is required");
    }
    internal static void Inspect(string game)
    {
        Safe(game); Console.WriteLine("Game: " + Full(game));
        try { CheckLoader(game); Console.WriteLine("Loader: compatible BepInEx 5 / Harmony 2"); } catch (Exception e) { Console.WriteLine("Loader: " + e.Message); }
        var assemblies = Scan(game, false); foreach (var a in assemblies) Console.WriteLine(a.Name + " " + a.Version + " plugins=" + string.Join(",", a.PluginIds));
        Console.WriteLine("Read-only inspection complete. No game or plugin execution.");
    }
    private static void CheckConflicts(string game, bool ownReceipt)
    {
        foreach (var a in Scan(game, ownReceipt))
            if (a.Name == "DinkumSaveProtection" || a.PluginIds.Contains(Owner, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Duplicate plugin GUID/assembly detected outside the owned installation");
    }
    private static void CopyOwned(string source, string destination)
    {
        var receipt = ReadReceipt(source); Safe(destination); Directory.CreateDirectory(destination);
        foreach (var file in receipt.Files.Keys.Append(Manifest))
        {
            var path = Path.Combine(destination, file); Safe(path); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Safe(temp); try { File.Copy(Path.Combine(source, file), temp, false); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static string Backup(string game)
    {
        var directory = Path.Combine(Backups(game), DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N")); Safe(directory);
        CopyOwned(PluginDir(game), directory); return directory;
    }
    internal static void Install(string game, string package)
    {
        RequireGameStoppedFor(game); Safe(game); CheckLoader(game); var receipt = ReadReceipt(package); var metadata = Metadata(Path.Combine(package, Dll));
        if (metadata.Name != "DinkumSaveProtection" || metadata.PluginIds.Count(id => id == Owner) != 1) throw new InvalidDataException("Package DLL identity does not match receipt");
        if (metadata.Version.ToString(3) != receipt.Version) throw new InvalidDataException("Package DLL version does not match receipt");
        string destination = PluginDir(game); bool exists = Directory.Exists(destination);
        if (exists) ReadReceipt(destination);
        CheckConflicts(game, exists); string? backup = exists ? Backup(game) : null;
        if (exists && Directory.EnumerateFiles(destination).Any(p => Path.GetFileName(p) != Dll && Path.GetFileName(p) != Manifest)) Console.WriteLine("Unowned files will be preserved");
        try { CopyOwned(package, destination); ReadReceipt(destination); }
        catch { if (backup != null) CopyOwned(backup, destination); throw; }
        Console.WriteLine("Installed owned plugin " + receipt.Version + ". Loader, other plugins, configuration and saves untouched.");
    }
    internal static void Uninstall(string game)
    {
        RequireGameStoppedFor(game); Safe(game); var directory = PluginDir(game); var receipt = ReadReceipt(directory); Backup(game);
        foreach (var file in receipt.Files.Keys.Append(Manifest)) { string path = Path.Combine(directory, file); Safe(path); File.Delete(path); }
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        Console.WriteLine("Uninstalled only receipt-owned files. Configuration and player backups retained.");
    }
    internal static void Rollback(string game)
    {
        RequireGameStoppedFor(game); Safe(game); CheckLoader(game); Safe(Backups(game));
        var backup = Directory.EnumerateDirectories(Backups(game)).OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() ?? throw new InvalidOperationException("No backup available");
        ReadReceipt(backup); if (Directory.Exists(PluginDir(game))) { ReadReceipt(PluginDir(game)); CheckConflicts(game, true); } else CheckConflicts(game, false);
        // Stage the selected backup before creating a receipt for the current version.
        var stage = Path.Combine(Backups(game), "rollback-stage-" + Guid.NewGuid().ToString("N")); CopyOwned(backup, stage);
        if (Directory.Exists(PluginDir(game))) Backup(game);
        CopyOwned(stage, PluginDir(game));
        foreach (var file in new[] { Dll, Manifest }) File.Delete(Path.Combine(stage, file)); Directory.Delete(stage);
        Console.WriteLine("Restored prior owned plugin version; configuration and saves retained.");
    }
    internal static void SelfTest(string plugin, string bepinex)
    {
        string temp = Full(Path.GetTempPath());
        string root = Full(Path.Combine(temp, "dinkum-installer-test-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid fixture root");
        Safe(root); Directory.CreateDirectory(root); selfTestRoot = root;
        void ExpectFailure(Action action) { bool failed = false; try { action(); } catch (Exception e) when (e is InvalidOperationException || e is InvalidDataException || e is IOException) { failed = true; } if (!failed) throw new Exception("Expected refusal"); }
        try
        {
            var running = Process.GetProcessesByName("Dinkum");
            try
            {
                if (running.Length != 0)
                {
                    ExpectFailure(() => RequireGameStoppedFor(root + "-outside"));
                    ExpectFailure(RequireGameStopped);
                }
            }
            finally { foreach (var process in running) process.Dispose(); }
            var game = Path.Combine(root, "game"); var package = Path.Combine(root, "package"); Directory.CreateDirectory(package);
            File.Copy(plugin, Path.Combine(package, Dll)); File.WriteAllText(Path.Combine(package, Manifest), JsonSerializer.Serialize(new Receipt(Owner, Metadata(plugin).Version.ToString(3), new() { [Dll] = Digest(plugin) }), Json));
            // This is a throwaway fixture. Its EXE is just a PE header, never an executable game.
            Directory.CreateDirectory(game); var fakeExe = new byte[512]; fakeExe[0] = 0x4d; fakeExe[1] = 0x5a; BitConverter.GetBytes(128).CopyTo(fakeExe, 60); fakeExe[128] = 0x50; fakeExe[129] = 0x45; fakeExe[132] = 0x64; fakeExe[133] = 0x86; BitConverter.GetBytes((short)240).CopyTo(fakeExe, 148); fakeExe[152] = 0x0b; fakeExe[153] = 0x02; File.WriteAllBytes(Path.Combine(game, "Dinkum.exe"), fakeExe);
            ExpectFailure(() => Install(game, package));
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
            foreach (var file in new[] { "BepInEx.dll", "0Harmony.dll" }) File.Copy(Path.Combine(bepinex, file), Path.Combine(game, "BepInEx", "core", file));
            var config = Path.Combine(game, "BepInEx", "config"); Directory.CreateDirectory(config); File.WriteAllText(Path.Combine(config, "other.cfg"), "keep");
            var other = Path.Combine(game, "BepInEx", "plugins", "other"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");
            string coreHash = Digest(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"));
            Install(game, package); File.WriteAllText(Path.Combine(PluginDir(game), "personal.cfg"), "keep"); Install(game, package); Rollback(game);
            File.Copy(plugin, Path.Combine(other, "renamed.dll")); ExpectFailure(() => Install(game, package)); File.Delete(Path.Combine(other, "renamed.dll"));
            File.AppendAllText(Path.Combine(PluginDir(game), Dll), "changed"); ExpectFailure(() => Uninstall(game));
            File.Copy(plugin, Path.Combine(PluginDir(game), Dll), true); Uninstall(game);
            if (File.ReadAllText(Path.Combine(config, "other.cfg")) != "keep" || File.ReadAllText(Path.Combine(other, "keep.txt")) != "keep" || File.ReadAllText(Path.Combine(PluginDir(game), "personal.cfg")) != "keep" || Digest(Path.Combine(game, "BepInEx", "core", "BepInEx.dll")) != coreHash) throw new Exception("Isolation violated");
            Console.WriteLine("PASS installer: missing loader, isolated install/upgrade/rollback/uninstall, duplicate GUID, modified owned file, other files/core/config retained. Fixtures only.");
        }
        finally { selfTestRoot = null; Safe(root); Directory.Delete(root, true); }
    }
}
