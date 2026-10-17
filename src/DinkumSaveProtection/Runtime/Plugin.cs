using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using DinkumSaveProtection.Core;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Snapshot = DinkumSaveProtection.Core.Snapshot;
using BackgroundTask = System.Threading.Tasks.Task;

namespace DinkumSaveProtection.Runtime
{
    [BepInPlugin(Guid, "Dinkum Save Protection", "0.1.5")]
    [BepInProcess("Dinkum.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "io.dinkumsaveprotection.playerdata";
        internal const string Build = "5106da5d624aaedabb16159a9bdaa9ab3ce4d09eb992bd4759d881dd9d9bea24";
        internal static Plugin? Instance;
        private Harmony? harmony;
        internal readonly GameData Data = new GameData();
        internal NetworkBackup? Network;
        internal SnapshotStore? Store;
        internal Snapshot? Head;
        internal Capture? Recovery;
        internal bool Active, Blocked, Restoring, Initialized, Connecting;
        private bool replayConnect, originalLoad, mailLoaded, stashLoaded, loadComplete, earlyRestored;
        private readonly HashSet<string> loaded = new HashSet<string>();
        private readonly ConcurrentQueue<Action> completions = new ConcurrentQueue<Action>();
        private BackgroundTask worker = BackgroundTask.CompletedTask;
        private readonly object workerLock = new object();
        private CheckpointWriter? writer;
        private readonly List<CheckpointWriter> retiredWriters = new List<CheckpointWriter>();
        private BackgroundTask? nativeExportWork;
        private bool exporting;
        internal bool PreviousWritesPending => retiredWriters.Any(w => !w.Idle) || nativeExportWork != null && !nativeExportWork.IsCompleted;
        private ResumeState? resume;
        private ExitIntent exitIntent = new ExitIntent();
        private Exception? reportedDiskError;
        private NativeSaveDecision? reportedSaveDecision;
        private int generation, queuedNetworkWork;
        private bool departing, unexpected, exitPending, replayDeparture, allowQuit, destroyed, newBaseline, restored;
        private string host = "", hostSession = "", resumePath = "", root = "", restoreArchive = "";
        internal bool RecoveryAllowed { get; private set; }
        internal bool FreshBaseline => newBaseline;
        internal int Generation => generation;
        internal string HostSession => hostSession;
        private bool preferNative;
        private int dirtyPending;
        private string character = "", session = "", statePath = "", nativePath = "", nativeHash = "", checkpoint = "", account = "", slotDirectory = "";
        private ChangeSchedule schedule = new ChangeSchedule();
        private ConfigEntry<string> recoveryChoice = null!;
        private ConfigEntry<string> recoverySource = null!;
        internal string RecoverySource => recoverySource.Value;
        internal string Context = "";
        internal string Root => root;
        internal string Character => character;
        internal bool ReplayConnect => replayConnect;
        private void Awake()
        {
            Instance = this;
            root = Path.Combine(Application.persistentDataPath, "DinkumSaveProtection"); PlayerDataAdapters.BindMainThread();
            recoveryChoice = Config.Bind("Recovery", "Choice", "Pause", "When native data has diverged or recovery is blocked: Pause, Native, Backup. Reset to Pause after successful choice.");
            recoverySource = Config.Bind("Recovery", "Source", "Auto", "On a host/client branch conflict: Auto (pause), Local or Host. Explicit choices are consumed after loading.");
            try
            {
                string assembly = typeof(PlayerInv).Assembly.Location;
                if (BinaryData.Hash(File.ReadAllBytes(assembly)) != Build) throw new InvalidOperationException("Unsupported game assembly. Re-audit before enabling patches.");
                harmony = new Harmony(Guid); Patches.Install(harmony); Network = new NetworkBackup(this);
                PlayerDataAdapters.Changed += Dirty;
                SceneManager.sceneLoaded += SceneLoaded;
                Application.wantsToQuit += WantsToQuit;
                Logger.LogInfo("Loaded. Original personal fields protected; extension fields require registered adapters. No periodic network heartbeat.");
            }
            catch (Exception e) { Blocked = true; Logger.LogError(e); harmony?.UnpatchSelf(); }
        }
        internal new void Info(string message) => Logger.LogInfo(message);
        internal void Warn(string message) => Logger.LogWarning(message);
        internal void Error(Exception e) => Logger.LogError(e);
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.buildIndex != 1) return;
            // Objects may already be destroyed. Final snapshots belong to the earlier disconnect/exit hooks.
            if (writer != null) retiredWriters.Add(writer);
            generation++; Network?.ResetClient(); Active = false; Initialized = false; Connecting = false; Store = null; Head = null; Recovery = null; writer = null; resume = null;
            departing = unexpected = exitPending = newBaseline = restored = false; restoreArchive = ""; reportedDiskError = null;
            exitIntent = new ExitIntent();
            reportedSaveDecision = null;
            exporting = false;
            loaded.Clear(); originalLoad = mailLoaded = stashLoaded = loadComplete = earlyRestored = preferNative = false;
        }
        internal void Dirty() { if (Active && !Restoring && !departing) System.Threading.Interlocked.Exchange(ref dirtyPending, 1); }
        internal void BeginSlot()
        {
            if (Initialized) return;
            account = SteamManager.Initialized ? SteamUser.GetSteamID().ToString() : "local";
            slotDirectory = SaveLoad.saveOrLoad.newFileSaver.GetSaveSlotDirectory(); nativePath = Path.Combine(slotDirectory, "Player.es3");
            string identityRoot = Path.Combine(Root, "characters", account, "slot" + SaveLoad.saveOrLoad.currentSaveSlotNo());
            statePath = Path.Combine(identityRoot, "identity.bin");
            Dictionary<string, object?> state;
            if (File.Exists(statePath) || File.Exists(statePath + ".previous")) state = ReadIdentity();
            else state = new Dictionary<string, object?> { ["character"] = System.Guid.NewGuid().ToString("N"), ["native"] = "", ["checkpoint"] = "" };
            character = BinaryData.Text(state["character"]); if (!System.Guid.TryParseExact(character, "N", out _)) throw new InvalidDataException("Invalid character sidecar");
            if (NativeExportTransaction.Reconcile(nativePath, statePath, Path.Combine(identityRoot, character, "native-export.pending"), character))
            { state = ReadIdentity(); Info("Completed native export checkpoint recovery"); }
            nativeHash = BinaryData.Text(state["native"]); checkpoint = BinaryData.Text(state["checkpoint"]);
            session = System.Guid.NewGuid().ToString("N");
            Store = new SnapshotStore(Path.Combine(identityRoot, character));
            resumePath = Path.Combine(identityRoot, character, "resume.bin"); resume = ResumeState.Read(resumePath);
            if (nativeHash == "") { nativeHash = NativeHash(); WriteState(); }
            DiscoverHost(); Initialized = true; Blocked = false; loadComplete = false; ConfigureEligibility();
            SelectRecovery();
        }
        private string NativeHash() => File.Exists(nativePath) ? BinaryData.Hash(File.ReadAllBytes(nativePath)) : "missing";
        private void WriteState() => SnapshotStore.AtomicWrite(statePath, BinaryData.Encode(new Dictionary<string, object?> { ["character"] = character, ["native"] = nativeHash, ["checkpoint"] = checkpoint }));
        private Dictionary<string, object?> ReadIdentity()
        {
            foreach (var file in new[] { statePath, statePath + ".previous" })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    if (new FileInfo(file).Length > 4096) continue;
                    var state = BinaryData.Map(BinaryData.Decode(File.ReadAllBytes(file)));
                    if (state.Count == 3 && System.Guid.TryParseExact(BinaryData.Text(state["character"]), "N", out _) &&
                        (BinaryData.Text(state["native"]) == "missing" || BinaryData.IsHash(BinaryData.Text(state["native"]))) &&
                        (BinaryData.Text(state["checkpoint"]) == "" || BinaryData.IsHash(BinaryData.Text(state["checkpoint"])))) return state;
                }
                catch (Exception e) { Warn("Identity candidate rejected: " + e.Message); }
            }
            throw new InvalidDataException("Identity and previous copy unavailable; refusing a new character identity");
        }
        private void DiscoverHost()
        {
            host = hostSession = Context = "";
            if (NetworkServer.active) { host = "self"; hostSession = Network?.ServerSession ?? ""; Context = WorldIdentity(); return; }
            if (!SteamManager.Initialized || SteamLobby.Instance == null || CustomNetworkManager.manage.checkIfLanGame()) return;
            var lobby = SteamLobby.Instance.currentLobby; if (lobby == CSteamID.Nil) return;
            host = SteamMatchmaking.GetLobbyOwner(lobby).ToString();
            var token = SteamMatchmaking.GetLobbyData(lobby, NetworkBackup.SessionCapability);
            hostSession = System.Guid.TryParseExact(token, "N", out _) && SteamMatchmaking.GetLobbyData(lobby, NetworkBackup.Capability) == "2" ? "mod:" + token : "lobby:" + lobby;
            // Without a mod handshake, no durable remote world identity is available for daily flags.
        }
        private void ConfigureEligibility()
        {
            RecoveryAllowed = false;
            if (resume != null)
            {
                if (resume.Character != character) throw new InvalidDataException("Resume character mismatch");
                RecoveryAllowed = resume.Allows(character, host, hostSession);
                if (resume.Snapshot == "") { RecoveryAllowed = false; newBaseline = preferNative = true; return; }
                if (!RecoveryAllowed) { newBaseline = preferNative = true; Head = null; Info("Voluntary exit into another/unknown host session: native data selected; old caches retained"); }
            }
            else if (Store!.ValidVersions(character).Length != 0)
            {
                Config.Reload();
                if (recoveryChoice.Value == "Native") newBaseline = preferNative = true;
                else if (recoveryChoice.Value == "Backup") { RecoveryAllowed = true; Warn("Explicit legacy-cache recovery without exit/session metadata"); }
                else Pause("Legacy cache lacks exit/session metadata. Inspect and explicitly choose Native or Backup");
            }
            else newBaseline = true;
        }
        internal void ConfirmHostSession(string token, string world)
        {
            if ("mod:" + token != hostSession) throw new InvalidDataException("Lobby/handshake host session mismatch");
            Context = host + ":" + world;
        }
        internal string WorldIdentity()
        {
            var path = Path.Combine(Root, "worlds", "slot" + SaveLoad.saveOrLoad.currentSaveSlotNo() + ".id");
            if (!File.Exists(path)) SnapshotStore.AtomicWrite(path, System.Text.Encoding.ASCII.GetBytes(System.Guid.NewGuid().ToString("N")));
            var id = File.ReadAllText(path); if (!System.Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid world identity"); return id;
        }
        internal void SelectRecovery()
        {
            if (Store == null || Blocked) return;
            Recovery = null;
            if (!RecoveryAllowed || preferNative) { Head = null; return; }
            Config.Reload();
            if (recoveryChoice.Value == "Backup") Head = Store.Latest(character); // explicit inspection/branch selection; eligibility already checked
            if (Head == null && resume != null && resume.Snapshot != "") Head = Store.Find(resume.Snapshot);
            if (Head == null && resume == null) Head = Store.Latest(character); // explicit legacy recovery only
            if (Head == null && resume != null && resume.Snapshot != "") { Pause("Authorized snapshot manifest unavailable"); return; }
            if (Head == null) return;
            if (Head.Build != Build) { Pause("Snapshot belongs to a different game build"); return; }
            if (NativeHash() != nativeHash)
            {
                Config.Reload();
                if (recoveryChoice.Value == "Native") { preferNative = newBaseline = true; RecoveryAllowed = false; Head = null; return; }
                if (recoveryChoice.Value != "Backup") { Pause("Native save changed outside the known checkpoint. Choose Recovery.Choice=Native or Backup, then reconnect. Both candidates retained."); return; }
            }
            // Sidecars also contain cursor/equipment/extension fields omitted by native saving.
            var candidate = Head;
            for (int i = 0; candidate != null && i < 1024; i++, candidate = Store.Find(candidate.Parent))
            {
                if (candidate.Character != character || candidate.Build != Build) break;
                try { Recovery = Store.Read(candidate); Head = candidate; break; }
                catch (IOException e) { Warn("Incomplete candidate retained: " + e.Message); }
            }
            if (Recovery == null) { Pause("No complete version in authorized ancestry"); return; }
            Blocked = false;
        }
        internal void Pause(string message) { Blocked = true; Active = false; Warn("PROTECTION PAUSED: " + message); }
        internal bool InterceptConnect(CustomNetworkManager manager)
        {
            if (exporting) { NotifyExport("请等待原版存档保存完成后再加入房间。", true); NetworkClient.Disconnect(); return false; }
            if (replayConnect || NetworkServer.active) return true;
            if (!SaveLoad.saveOrLoad.loadingNewSaveFormat) { Warn("Legacy save format: protection is disabled; convert with the game's normal save workflow first."); return true; }
            if (Connecting) return false;
            try { Connecting = true; StartCoroutine(ConnectAfterRecovery(manager)); }
            catch (Exception e) { Pause(e.Message); Error(e); }
            return false;
        }
        private IEnumerator ConnectAfterRecovery(CustomNetworkManager manager)
        {
            int epoch = generation;
            while (epoch == generation && PreviousWritesPending && NetworkClient.isConnected) yield return null;
            if (epoch != generation || !NetworkClient.isConnected) { Connecting = false; yield break; }
            try { retiredWriters.Clear(); BeginSlot(); }
            catch (Exception e) { Connecting = false; Pause(e.Message); Error(e); yield break; }
            Network!.BeginRecovery();
            while (epoch == generation && Network.WaitingRecovery && Time.realtimeSinceStartup - Network.LastRecoveryProgress < 12 && Time.realtimeSinceStartup - Network.RecoveryStarted < 600 && NetworkClient.isConnected) yield return null;
            if (epoch != generation || !NetworkClient.isConnected) { Connecting = false; yield break; }
            if (Network.WaitingRecovery) { Network.DisableClient("Recovery request timed out; using local candidates"); }
            try
            {
                if (Blocked) { Connecting = false; yield break; }
                SelectRecovery();
                if (Blocked) { Connecting = false; yield break; }
                BeforePersonalLoad();
                if (Blocked) { Connecting = false; yield break; }
                replayConnect = true; manager.OnClientConnect();
            }
            finally { replayConnect = false; Connecting = false; }
        }
        internal bool BeforePersonalLoad(bool applyEarly = true)
        {
            try
            {
                BeginSlot();
                if (Blocked) return false;
                if (Recovery != null)
                {
                    Data.RequireRestoreTargets(true);
                    var validated = Data.DecodeAndValidate(Recovery); ArchiveBeforeRestore();
                    if (applyEarly && !earlyRestored && !NetworkServer.active)
                    {
                        Restoring = true;
                        try { Data.Restore(validated, Context, true); earlyRestored = true; }
                        catch (Exception e) { throw new InvalidOperationException("Recovery failed at " + Data.RestoreStage + ": " + e.Message, e); }
                        finally { Restoring = false; }
                    }
                    originalLoad = true; loaded.UnionWith(new[] { "playerInfo", "quests", "levels", "recipes", "licences", "pedia", "npc" }); CustomNetworkManager.manage.clientInvLoaded = true; return false;
                }
                RepairNativeBackup(); originalLoad = true; return true;
            }
            catch (Exception e) { Pause(e.Message); Error(e); return false; }
        }
        private void RepairNativeBackup()
        {
            var backup = NativeFileRecovery.Select(nativePath, File.Exists, NativeValidation.Validate, Warn);
            if (backup == null) return;
            Directory.CreateDirectory(Path.Combine(Root, "quarantine"));
            if (File.Exists(nativePath)) File.Copy(nativePath, Path.Combine(Root, "quarantine", System.Guid.NewGuid().ToString("N") + ".es3"));
            SnapshotStore.AtomicWrite(nativePath, File.ReadAllBytes(backup)); nativeHash = NativeHash(); WriteState(); Warn("Repaired native file from explicitly validated " + Path.GetExtension(backup));
        }
        private void ArchiveBeforeRestore()
        {
            if (restoreArchive != "") return;
            restoreArchive = Path.Combine(Root, "recovery", System.Guid.NewGuid().ToString("N")); Directory.CreateDirectory(restoreArchive);
            if (File.Exists(nativePath)) SnapshotStore.AtomicWrite(Path.Combine(restoreArchive, "Player.before.es3"), File.ReadAllBytes(nativePath));
            if (Recovery != null) SnapshotStore.AtomicWrite(Path.Combine(restoreArchive, "source.snap"), Recovery.Manifest.Serialize());
        }
        internal void AcceptRecovery(Snapshot snapshot)
        {
            if (!RecoveryAllowed || Blocked || snapshot.Character != character || snapshot.Build != Build) throw new InvalidDataException("Recovery candidate is ineligible");
            var capture = Store!.Read(snapshot); Data.DecodeAndValidate(capture); // validate before publishing/selecting
            Store.Commit(snapshot); if (RecoverySource == "Host") Store.SelectBranch(snapshot.Id);
            Head = snapshot;
        }
        internal void PersonalStage(string key, bool ran)
        {
            if (!ran || !originalLoad || Recovery != null || Blocked) return;
            try
            {
                var settings = (ES3Settings?)AccessTools.Field(typeof(SaveAndLoad), "cacheFileSettings").GetValue(SaveLoad.saveOrLoad.newFileSaver);
                if (settings != null && ES3.KeyExists(key, settings)) loaded.Add(key);
            }
            catch (Exception e) { Pause("Personal stage failed: " + key); Error(e); }
        }
        internal bool AuxiliaryLoad(string kind)
        {
            if (Blocked) return false;
            if (Recovery != null) { if (kind == "mail") mailLoaded = true; else stashLoaded = true; return false; }
            return true;
        }
        internal void AuxiliaryComplete(string kind, bool ran) { if (!ran || Blocked) return; if (kind == "mail") mailLoaded = true; else stashLoaded = true; }
        internal bool BeforeUnlock(CharMovement movement)
        {
            if (!movement.isLocalPlayer) return true;
            if (!SaveLoad.saveOrLoad.loadingNewSaveFormat) return true;
            if (Blocked || Restoring) return false;
            if (loadComplete) return true;
            try
            {
                BeginSlot();
                if (!SaveLoad.saveOrLoad.loadingNewSaveFormat) { Warn("Legacy personal format is not protected"); return true; }
                if (!mailLoaded || !stashLoaded || !originalLoad || loaded.Count < 7) { Pause("Not all native personal modules loaded successfully"); return false; }
                Data.RequireRestoreTargets(false);
                if (Recovery != null && !restored)
                {
                    var validated = Data.DecodeAndValidate(Recovery);
                    ArchiveBeforeRestore();
                    SnapshotStore.AtomicWrite(Path.Combine(restoreArchive, "actor.before"), BinaryData.Encode(CheckpointWriter.EncodeModules(Data.Capture(Context)).ToDictionary(p => p.Key, p => (object?)p.Value)));
                    Restoring = true;
                    try { Data.Restore(validated, Context); restored = true; }
                    catch (Exception e) { Pause("Recovery failed at " + Data.RestoreStage + ". Source snapshot retained; reload before choosing another candidate."); throw new InvalidOperationException("Recovery failed at " + Data.RestoreStage + ": " + e.Message, e); }
                    finally { Restoring = false; }
                    Info("Recovered complete personal snapshot " + Recovery.Manifest.Id); Recovery = null; recoveryChoice.Value = "Pause";
                }
                var identityFile = statePath; var owner = character; int epoch = generation;
                writer = new CheckpointWriter(Store!, character, session, Build, Head, resumePath, host, hostSession, newBaseline || preferNative || recoverySource.Value != "Auto",
                    (snapshot, hash) =>
                    {
                        SnapshotStore.AtomicWrite(identityFile, BinaryData.Encode(new Dictionary<string, object?> { ["character"] = owner, ["native"] = hash!, ["checkpoint"] = snapshot.Id }));
                        completions.Enqueue(() => { if (!destroyed && epoch == generation) { nativeHash = hash!; checkpoint = snapshot.Id; } });
                    }, pinnedCheckpoint: checkpoint);
                Active = true; loadComplete = true; schedule = new ChangeSchedule(Time.realtimeSinceStartup); CaptureNow(preferNative ? NativeHash() : null);
                preferNative = false; recoveryChoice.Value = "Pause";
                recoverySource.Value = "Auto";
                return true;
            }
            catch (Exception e) { Restoring = false; Pause(e.Message); Error(e); return false; }
        }
        internal void NativeLoadBegin()
        {
            try { BeforePersonalLoad(false); if (Recovery != null && !Blocked) RepairNativeBackup(); }
            catch (Exception e) { Pause(e.Message); Error(e); }
        }
        internal void NativeSaveComplete(bool success)
        {
            if (!success || !CanSave || writer == null) return;
            try { NativeValidation.Validate(nativePath); CaptureNow(NativeHash()); }
            catch (Exception e) { Pause("Native save validation failed; checkpoint unchanged: " + e.Message); }
        }
        private NativeSaveDecision SaveDecision => NativeSavePolicy.Decide(SaveLoad.saveOrLoad.loadingNewSaveFormat, Initialized, loadComplete, Blocked, Restoring, unexpected);
        internal bool CanSave => SaveDecision == NativeSaveDecision.Allow;
        internal bool BeforeNativeSave()
        {
            var decision = SaveDecision;
            if (decision == NativeSaveDecision.Allow) { reportedSaveDecision = null; return true; }
            if (reportedSaveDecision != decision)
            {
                var message = NativeSavePolicy.Message(decision);
                if (decision == NativeSaveDecision.UnexpectedDisconnect) Info(message); else Warn(message);
                reportedSaveDecision = decision;
            }
            return false;
        }
        private void LateUpdate()
        {
            while (completions.TryDequeue(out var callback)) { try { callback(); } catch (Exception e) { Pause(e.Message); Error(e); } }
            try
            {
                Network?.Tick();
                if (writer != null)
                {
                    if (writer.Error != null && !ReferenceEquals(reportedDiskError, writer.Error)) { reportedDiskError = writer.Error; Warn("Checkpoint write failed; committed data retained: " + writer.Error.Message); }
                    var committed = writer.Head;
                    if (committed != null && committed.Id != Head?.Id) { Head = committed; Network?.Changed(committed); }
                }
                if (System.Threading.Interlocked.Exchange(ref dirtyPending, 0) != 0) schedule.Dirty(Time.realtimeSinceStartup);
                if (Active && !Blocked && !Restoring && !departing && schedule.Due(Time.realtimeSinceStartup)) CaptureNow();
            }
            catch (Exception e) { Warn("Protection operation failed; previous committed data retained: " + e.Message); }
        }
        internal bool QueueWork(Action work, Action? done = null, Action<Exception>? failed = null)
        {
            lock (workerLock)
            {
                if (queuedNetworkWork >= 32 || destroyed) return false;
                queuedNetworkWork++; int epoch = generation;
                worker = worker.ContinueWith(_ =>
                {
                    try { work(); if (done != null) completions.Enqueue(() => { if (!destroyed && epoch == generation) done(); }); }
                    catch (Exception e) { completions.Enqueue(() => { if (!destroyed && epoch == generation) { if (failed != null) failed(e); else Warn("Network backup failed: " + e.Message); } }); }
                    finally { lock (workerLock) queuedNetworkWork--; }
                }, TaskScheduler.Default);
                return true;
            }
        }
        internal void CaptureNow(string? nativeCheckpoint = null)
        {
            if (!Active || Blocked || Restoring || writer == null || !loadComplete || departing && nativeCheckpoint == null) return;
            writer.Submit(Data.Capture(Context), nativeCheckpoint); schedule.Captured(Time.realtimeSinceStartup);
        }
        internal void FlushFinal()
        {
            try { CaptureNow(); }
            catch (Exception e) { Warn("Final capture failed; using previous complete snapshot: " + e.Message); }
        }
        internal void ClientDisconnected()
        {
            if (!Initialized || unexpected || departing) return;
            FlushFinal(); exitIntent.Disconnected(); unexpected = true; departing = true; Active = false;
            writer?.Seal(exitIntent.Reason); Network?.ResetClient();
        }
        internal bool SaveCacheAndReturn(CustomNetworkManager manager)
        {
            if (!unexpected) return true; // Still connected: retain the game's normal save path.
            if (exporting) return false;
            if (!Initialized || !loadComplete || Blocked || Restoring || writer == null || Store == null)
            { NotifyExport("无法安全保存：没有完成初始化的有效缓存。原存档未改动。", true); StartCoroutine(RestoreDisconnectNextFrame(manager, generation)); return false; }
            if (SaveLoad.saveOrLoad.isSaving) { NotifyExport("已有保存操作进行中，请稍后再试。", true); StartCoroutine(RestoreDisconnectNextFrame(manager, generation)); return false; }
            StartCoroutine(SaveCacheAndReturnFlow(manager, writer, Store, generation)); return false;
        }
        private void NotifyExport(string message, bool error = false)
        {
            if (error) Warn(message); else Info(message);
            try { if (NotificationManager.manage != null) NotificationManager.manage.makeTopNotification(message); }
            catch (Exception e) { Warn("Save notification unavailable: " + e.Message); }
        }
        private static void HoldDisconnectScreen(CustomNetworkManager manager)
        {
            if (manager != null && manager.disconectedScreen != null && !manager.disconectedScreen.activeSelf) manager.disconectedScreen.SetActive(true);
        }
        private IEnumerator RestoreDisconnectNextFrame(CustomNetworkManager manager, int epoch)
        { yield return null; if (epoch == generation) HoldDisconnectScreen(manager); }
        private void TrackNativeExport(BackgroundTask task)
        {
            nativeExportWork = task;
            _ = task.ContinueWith(t => { _ = t.Exception; }, System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private IEnumerator SaveCacheAndReturnFlow(CustomNetworkManager manager, CheckpointWriter target, SnapshotStore store, int epoch)
        {
            exporting = true; var gameSave = SaveLoad.saveOrLoad; gameSave.isSaving = true;
            bool returned = false;
            string slotFile = nativePath, identityFile = statePath, owner = character;
            string journal = Path.Combine(Path.GetDirectoryName(resumePath)!, "native-export.pending");
            string backup = Path.Combine(Root, "native-exports", owner, System.Guid.NewGuid().ToString("N"));
            try
            {
                NotifyExport("正在将最近有效缓存保存到原版存档，请稍候……");
                // The button's second UnityEvent hides the screen. Resume after that event finishes.
                yield return null;
                if (epoch != generation) yield break;
                HoldDisconnectScreen(manager);
                float deadline = Time.realtimeSinceStartup + 30;
                while (epoch == generation && !target.Idle && Time.realtimeSinceStartup < deadline) { HoldDisconnectScreen(manager); yield return null; }
                if (epoch != generation) yield break;
                if (!target.Idle || target.Head == null) { NotifyExport("缓存尚未完整提交，保存已取消，原存档未改动。", true); yield break; }
                if (target.Error != null) NotifyExport("最后一次缓存写入失败，将检查最近一份完整缓存是否可安全保存。", true);
                var snapshot = target.Head;
                var read = BackgroundTask.Run(() =>
                {
                    NativeExportTransaction.Reconcile(slotFile, identityFile, journal, owner);
                    if (new FileInfo(slotFile).Length > NativeExportTransaction.MaxNativeBytes) throw new InvalidDataException("Native export baseline exceeds budget");
                    if (new FileInfo(identityFile).Length > 4096) throw new InvalidDataException("Export identity exceeds budget");
                    var bytes = File.ReadAllBytes(slotFile); var identity = File.ReadAllBytes(identityFile);
                    var state = BinaryData.Map(BinaryData.Decode(identity));
                    if (BinaryData.Text(state["character"]) != owner || BinaryData.Text(state["native"]) != BinaryData.Hash(bytes)) throw new InvalidDataException("Native save changed since the known checkpoint; refusing overwrite");
                    string nativeCheckpoint = BinaryData.Text(state["checkpoint"]);
                    NativeExportTransaction.RequireHistory(snapshot, nativeCheckpoint, store.Find(nativeCheckpoint), store.Find);
                    var capture = store.Read(snapshot);
                    if (capture.Manifest.Character != owner || capture.Manifest.Build != Build) throw new InvalidDataException("Native export snapshot identity mismatch");
                    return (Bytes: bytes, Identity: identity, Capture: capture);
                });
                TrackNativeExport(read);
                while (!read.IsCompleted && epoch == generation) { HoldDisconnectScreen(manager); yield return null; }
                if (epoch != generation) yield break;
                if (read.IsFaulted || read.IsCanceled) { NotifyExport("读取有效缓存或原存档失败，文件未改动：" + read.Exception?.GetBaseException().Message, true); yield break; }
                var input = read.Result; NativeExportImage? image = null;
                try { image = NativeExportCodec.Prepare(input.Bytes, Data.DecodeAndValidate(input.Capture)); }
                catch (Exception e) { NotifyExport("无法安全转换缓存，原存档未改动：" + e.GetBaseException().Message, true); }
                if (image == null) yield break;
                string expected = BinaryData.Hash(input.Bytes), identityHash = BinaryData.Hash(input.Identity);
                var commit = BackgroundTask.Run(() => NativeExportTransaction.Commit(slotFile, identityFile, journal, backup, owner, snapshot.Id, expected, identityHash, image.Bytes,
                    () => !System.Threading.Volatile.Read(ref destroyed) && System.Threading.Volatile.Read(ref generation) == epoch));
                TrackNativeExport(commit);
                while (!commit.IsCompleted && epoch == generation) { HoldDisconnectScreen(manager); yield return null; }
                if (epoch != generation) yield break;
                if (commit.IsFaulted || commit.IsCanceled)
                {
                    var failure = commit.Exception?.Flatten().InnerExceptions.FirstOrDefault();
                    bool published = failure is NativeExportException exported && exported.NativePublished;
                    NotifyExport((published ? "原版文件已替换，但后续验证、备份或检查点未完成；请保留缓存和事务备份：" : "原版写入已取消，原文件和缓存保留：") + failure?.GetBaseException().Message, true); yield break;
                }
                nativeHash = commit.Result; checkpoint = snapshot.Id;
                Info("Native personal save committed from cached snapshot " + snapshot.Id + "; previous native files retained at " + backup + "; packed cursor/idol items=" + image.PackedItems);
                if (image.Extensions != 0) Warn("Only native-supported personal fields were exported; " + image.Extensions + " extension modules remain in the recovery cache");
                NotifyExport("原版存档保存成功，正在返回主菜单。");
                gameSave.isSaving = false; exporting = false;
                manager.disconectionScreenButton();
                returned = true;
            }
            finally
            {
                if (epoch == generation) { exporting = false; if (ReferenceEquals(gameSave, SaveLoad.saveOrLoad)) gameSave.isSaving = false; if (!returned) HoldDisconnectScreen(manager); }
            }
        }
        internal bool RequestDeparture(Action continuation)
        {
            if (replayDeparture || !Initialized || !loadComplete || unexpected) return true;
            if (!exitIntent.TryVoluntary()) return true;
            if (exitPending) return false;
            FlushFinal(); departing = true; exitPending = true;
            var target = writer; if (target == null) { exitPending = departing = false; return true; }
            long ticket = target.Seal(exitIntent.Reason); StartCoroutine(DepartureAfterCommit(target, ticket, generation, continuation)); return false;
        }
        private IEnumerator DepartureAfterCommit(CheckpointWriter target, long ticket, int epoch, Action continuation)
        {
            float deadline = Time.realtimeSinceStartup + 2;
            while (epoch == generation && !target.Completed(ticket) && target.Error == null && Time.realtimeSinceStartup < deadline) yield return null;
            if (epoch != generation) yield break;
            exitPending = false;
            if (!target.Completed(ticket))
            {
                target.Unseal(); exitIntent.CancelVoluntary(); departing = false; schedule.Dirty(Time.realtimeSinceStartup);
                Warn("Exit cancelled: voluntary-exit marker did not commit within 2 seconds; retry after disk recovers"); yield break;
            }
            try { replayDeparture = true; continuation(); } finally { replayDeparture = false; }
        }
        private bool WantsToQuit()
        {
            if (exporting) { Warn("Wait for explicit native export to finish before quitting"); return false; }
            if (allowQuit || unexpected || !Initialized || !loadComplete) return true;
            return RequestDeparture(() => { allowQuit = true; Application.Quit(); });
        }
        private void OnDestroy()
        {
            destroyed = true; generation++; Active = false;
            PlayerDataAdapters.Changed -= Dirty; SceneManager.sceneLoaded -= SceneLoaded; Application.wantsToQuit -= WantsToQuit;
            Network?.Dispose(); harmony?.UnpatchSelf(); Instance = null;
        }
    }
    internal static class Patches
    {
        internal static void Install(Harmony h)
        {
            void Patch(Type t, string name, string? pre = null, string? post = null, Type[]? parameters = null)
            {
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.Name == name).ToArray();
                var method = parameters != null ? AccessTools.DeclaredMethod(t, name, parameters) : methods.Length == 1 ? methods[0] : null;
                if (method == null) throw new MissingMethodException("Missing or ambiguous patch target: " + t.Name + "." + name);
                    var info = Harmony.GetPatchInfo(method); if (info != null && info.Owners.Any(o => o != Plugin.Guid)) Plugin.Instance!.Warn("Shared patch target " + t.Name + "." + name + ": " + string.Join(",", info.Owners));
                    h.Patch(method, pre == null ? null : new HarmonyMethod(typeof(Patches), pre), post == null ? null : new HarmonyMethod(typeof(Patches), post));
            }
            foreach (var target in new[] { (typeof(InventorySlot), "updateSlotContentsAndRefresh"), (typeof(Inventory), "changeWallet"), (typeof(Inventory), "changeWalletToLoad"), (typeof(BankMenu), "confirmButton"), (typeof(CharLevelManager), "addXp"), (typeof(CharLevelManager), "unlockRecipe"), (typeof(Licence), "buyNextLevel"), (typeof(PermitPointsManager), "addPoints"), (typeof(PermitPointsManager), "spendPoints"), (typeof(Milestone), "addPoints"), (typeof(MailManager), "deleteLetter"), (typeof(QuestManager), "completeQuest") }) Patch(target.Item1, target.Item2, post: nameof(Dirty));
            Patch(typeof(MailManager), "takeAttachment", post: nameof(Dirty), parameters: Type.EmptyTypes);
            Patch(typeof(MailManager), "takeAttachment", post: nameof(Dirty), parameters: new[] { typeof(int) });
            Patch(typeof(CustomNetworkManager), "OnClientConnect", nameof(Connect));
            Patch(typeof(CustomNetworkManager), "OnClientDisconnect", nameof(Disconnected));
            Patch(typeof(CustomNetworkManager), "DisconnectScreen_SaveAndReturnToMenu", nameof(SaveCacheAndReturn));
            Patch(typeof(SaveLoad), "returnToMenu", nameof(ReturnToMenu)); Patch(typeof(SaveLoad), "quitGame", nameof(QuitGame));
            Patch(typeof(CustomNetworkManager), "OnServerDisconnect", nameof(ServerDisconnect));
            Patch(typeof(CustomNetworkManager), "OnServerConnect", post: nameof(ServerConnect));
            Patch(typeof(CustomNetworkManager), "OnStartHost", post: nameof(ServerStarted));
            Patch(typeof(CustomNetworkManager), "OnStopServer", nameof(ServerStopped));
            Patch(typeof(SaveAndLoad), "LoadInvForMultiplayerConnect", nameof(PersonalLoad));
            Patch(typeof(SaveAndLoad), "LoadOverFrames", post: nameof(LoadCoroutine));
            foreach (string name in new[] { "LoadInv", "LoadQuests", "LoadLevels", "LoadRecipes", "LoadLicences", "LoadPedia", "LoadNPCRelations" }) Patch(typeof(SaveAndLoad), name, post: nameof(Stage));
            Patch(typeof(SaveAndLoad), "LoadMail", nameof(MailPrefix), nameof(MailPostfix));
            Patch(typeof(SaveAndLoad), "LoadStash", nameof(StashPrefix), nameof(StashPostfix));
            Patch(typeof(CharMovement), "unlockClientOnLoad", nameof(Unlock));
            Patch(typeof(SaveAndLoad), "SaveCoroutine", nameof(SavePrefix), nameof(SaveCoroutine));
            Patch(typeof(SteamLobby), "OnLobbyEntered", post: nameof(Lobby));
        }
        private static void Dirty() => Plugin.Instance?.Dirty();
        private static void Disconnected() => Plugin.Instance?.ClientDisconnected();
        private static bool SaveCacheAndReturn(CustomNetworkManager __instance) => Plugin.Instance?.SaveCacheAndReturn(__instance) ?? true;
        private static bool ReturnToMenu(SaveLoad __instance) => __instance.isSaving || (Plugin.Instance?.RequestDeparture(__instance.returnToMenu) ?? true);
        private static bool QuitGame(SaveLoad __instance) => __instance.isSaving || (Plugin.Instance?.RequestDeparture(__instance.quitGame) ?? true);
        private static bool Connect(CustomNetworkManager __instance) => Plugin.Instance?.InterceptConnect(__instance) ?? true;
        private static bool PersonalLoad() => Plugin.Instance?.BeforePersonalLoad() ?? true;
        private static bool Unlock(CharMovement __instance) => Plugin.Instance?.BeforeUnlock(__instance) ?? true;
        private static void Stage(MethodBase __originalMethod, bool __runOriginal)
        {
            string key;
            switch (__originalMethod.Name) { case "LoadInv": key = "playerInfo"; break; case "LoadQuests": key = "quests"; break; case "LoadLevels": key = "levels"; break; case "LoadRecipes": key = "recipes"; break; case "LoadLicences": key = "licences"; break; case "LoadPedia": key = "pedia"; break; default: key = "npc"; break; }
            Plugin.Instance?.PersonalStage(key, __runOriginal);
        }
        private static bool MailPrefix() => Plugin.Instance?.AuxiliaryLoad("mail") ?? true;
        private static void MailPostfix(bool __runOriginal) => Plugin.Instance?.AuxiliaryComplete("mail", __runOriginal);
        private static bool StashPrefix() => Plugin.Instance?.AuxiliaryLoad("stash") ?? true;
        private static void StashPostfix(bool __runOriginal) => Plugin.Instance?.AuxiliaryComplete("stash", __runOriginal);
        private static void ServerDisconnect(NetworkConnection conn) { try { Plugin.Instance?.Network?.Disconnected(conn); } catch (Exception e) { Plugin.Instance?.Error(e); } }
        private static void ServerConnect() { try { Plugin.Instance?.Network?.Advertise(); } catch (Exception e) { Plugin.Instance?.Error(e); } }
        private static void ServerStarted() => Plugin.Instance?.Network?.StartServerSession();
        private static void ServerStopped() { Plugin.Instance?.FlushFinal(); Plugin.Instance?.Network?.StopServerSession(); }
        private static void Lobby() { try { Plugin.Instance?.Network?.Advertise(); } catch (Exception e) { Plugin.Instance?.Error(e); } }
        private static void LoadCoroutine(ref IEnumerator __result, bool __runOriginal)
        {
            if (!__runOriginal) return;
            __result = PrepareLoad(__result);
        }
        private static IEnumerator PrepareLoad(IEnumerator inner)
        {
            while (Plugin.Instance?.PreviousWritesPending == true) yield return null;
            Plugin.Instance?.NativeLoadBegin();
            if (Plugin.Instance?.Blocked == true) yield break;
            yield return new ObservedEnumerator(inner, () => { }, () => Plugin.Instance?.Pause("Native load raised an exception"));
        }
        private static IEnumerator Empty() { yield break; }
        private static bool SavePrefix(ref IEnumerator __result)
        {
            if (Plugin.Instance?.BeforeNativeSave() != false) return true;
            __result = Empty(); return false;
        }
        private static void SaveCoroutine(SaveAndLoad __instance, ref IEnumerator __result, bool __runOriginal)
        {
            if (!__runOriginal) return;
            Plugin.Instance?.FlushFinal();
            __result = new ObservedEnumerator(__result, () => Plugin.Instance?.NativeSaveComplete((bool)AccessTools.Field(typeof(SaveAndLoad), "savedSucessfully").GetValue(__instance)), () => Plugin.Instance?.Warn("Native save failed; independent snapshot retained"));
        }
    }
    internal sealed class ObservedEnumerator : IEnumerator, IDisposable
    {
        private readonly IEnumerator inner; private readonly Action completed, failed; private bool ended;
        internal ObservedEnumerator(IEnumerator inner, Action completed, Action failed) { this.inner = inner; this.completed = completed; this.failed = failed; }
        public object Current => inner.Current;
        public bool MoveNext()
        {
            bool more;
            try { more = inner.MoveNext(); } catch { ended = true; failed(); throw; }
            if (!more && !ended) { ended = true; completed(); } return more;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() { try { (inner as IDisposable)?.Dispose(); } finally { if (!ended) { ended = true; failed(); } } }
    }
}
