using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DinkumSaveProtection.Core;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using Snapshot = DinkumSaveProtection.Core.Snapshot;

namespace DinkumSaveProtection.Runtime
{
    // Explicit serializers: no Mirror Weaver or prefab NetworkBehaviour changes are needed.
    internal struct ProtectionWireV2 : NetworkMessage
    {
        internal string Id;
        internal int Total, Offset;
        internal byte[] Bytes;
    }
    internal sealed class NetworkBackup : IDisposable
    {
        internal const string Capability = "io.dinkumsaveprotection.protocol";
        internal const string SessionCapability = "io.dinkumsaveprotection.hostsession";
        internal string ServerSession { get; private set; } = "";
        private readonly Plugin plugin;
        private readonly UploadQueue upload = new UploadQueue();
        private readonly Dictionary<NetworkConnection, Peer> peers = new Dictionary<NetworkConnection, Peer>();
        private readonly Queue<Outgoing> outgoing = new Queue<Outgoing>();
        private readonly HashSet<string> sendingBlocks = new HashSet<string>();
        private bool clientRegistered, serverRegistered, enabled, disposed;
        private Snapshot? downloading;
        private string world = "";
        private string requestedRecovery = "";
        private int clientEpoch;
        private long workBytes;
        private double nextRegistration;
        private double lastSend;
        private double tokens = 32768;
        private object? clientHandler, serverHandler;
        internal bool WaitingRecovery { get; private set; }
        internal double LastRecoveryProgress { get; private set; }
        internal double RecoveryStarted { get; private set; }
        private sealed class Peer
        {
            internal readonly FragmentBuffer Fragments = new FragmentBuffer();
            internal SnapshotStore? Store;
            internal ReplicaReceiver? Receiver;
            internal string Character = "";
            internal string World = "";
            internal volatile bool Cancelled;
            internal bool Ready;
        }
        private sealed class Outgoing
        {
            internal NetworkConnection Connection = null!;
            internal byte[] Payload = null!;
            internal string Id = "";
            internal int Offset;
        }
        internal NetworkBackup(Plugin plugin)
        {
            this.plugin = plugin;
            Writer<ProtectionWireV2>.write = (w, m) => { w.WriteString(m.Id); w.WriteInt(m.Total); w.WriteInt(m.Offset); w.WriteInt(m.Bytes.Length); w.WriteBytes(m.Bytes, 0, m.Bytes.Length); };
            Reader<ProtectionWireV2>.read = r =>
            {
                var m = new ProtectionWireV2 { Id = r.ReadString(), Total = r.ReadInt(), Offset = r.ReadInt() };
                int length = r.ReadInt(); if (length < 1 || length > 8192) throw new InvalidDataException("Invalid protection fragment length"); m.Bytes = r.ReadBytes(length); return m;
            };
        }
        private static IDictionary Handlers(Type type) => (IDictionary)AccessTools.Field(type, "handlers").GetValue(null);
        private bool Register(bool server)
        {
            var handlers = Handlers(server ? typeof(NetworkServer) : typeof(NetworkClient)); var id = MessagePacking.GetId<ProtectionWireV2>();
            bool registered = server ? serverRegistered : clientRegistered;
            if (registered && handlers.Contains(id) && ReferenceEquals(handlers[id], server ? serverHandler : clientHandler)) return true;
            if (handlers.Contains(id)) { plugin.Warn("Protection message ID collision; network backup disabled on this side"); return false; }
            if (server) { NetworkServer.RegisterHandler<ProtectionWireV2>(ServerMessage); serverRegistered = true; serverHandler = handlers[id]; }
            else { NetworkClient.RegisterHandler<ProtectionWireV2>(ClientMessage); clientRegistered = true; clientHandler = handlers[id]; }
            return true;
        }
        internal void Advertise()
        {
            if (!SteamManager.Initialized || SteamLobby.Instance == null || !NetworkServer.active) return;
            if (!Register(true)) { RemoveAdvertisement(); return; }
            if (ServerSession == "") ServerSession = System.Guid.NewGuid().ToString("N");
            var lobby = SteamLobby.Instance.currentLobby;
            if (lobby != CSteamID.Nil && SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID())
            {
                if (SteamMatchmaking.GetLobbyData(lobby, SessionCapability) != ServerSession) SteamMatchmaking.SetLobbyData(lobby, SessionCapability, ServerSession);
                if (SteamMatchmaking.GetLobbyData(lobby, Capability) != "2") SteamMatchmaking.SetLobbyData(lobby, Capability, "2");
            }
        }
        internal void StartServerSession() { ServerSession = System.Guid.NewGuid().ToString("N"); Advertise(); }
        internal void StopServerSession() { RemoveAdvertisement(); ServerSession = ""; foreach (var p in peers.Values) { p.Cancelled = true; p.Fragments.Clear(); } peers.Clear(); outgoing.Clear(); }
        private void RemoveAdvertisement()
        {
            if (!SteamManager.Initialized || SteamLobby.Instance == null) return;
            var lobby = SteamLobby.Instance.currentLobby;
            if (lobby != CSteamID.Nil && SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID() && SteamMatchmaking.GetLobbyData(lobby, SessionCapability) == ServerSession)
            { SteamMatchmaking.DeleteLobbyData(lobby, SessionCapability); if (SteamMatchmaking.GetLobbyData(lobby, Capability) == "2") SteamMatchmaking.DeleteLobbyData(lobby, Capability); }
        }
        internal void BeginRecovery()
        {
            ResetClient();
            // Capability discovery is out of band. Never probe a vanilla Mirror connection.
            if (!SteamManager.Initialized || CustomNetworkManager.manage.checkIfLanGame() || SteamLobby.Instance == null) return;
            var lobby = SteamLobby.Instance.currentLobby;
            if (lobby == CSteamID.Nil || SteamMatchmaking.GetLobbyData(lobby, Capability) != "2" || !Register(false) || NetworkClient.connection == null) return;
            enabled = true; WaitingRecovery = true; LastRecoveryProgress = RecoveryStarted = Time.realtimeSinceStartup;
            Send(NetworkClient.connection, ReplicaReceiver.Message("hello", "protocol", 2L, "character", plugin.Character, "choice", plugin.RecoverySource, "restore", plugin.RecoveryAllowed, "baseline", plugin.FreshBaseline));
        }
        internal void Changed(Snapshot snapshot) { if (enabled) upload.Enqueue(snapshot); }
        internal void Tick()
        {
            if (disposed) return;
            double now = Time.realtimeSinceStartup;
            if (now >= nextRegistration)
            {
                nextRegistration = now + 1;
                if (NetworkServer.active) { if (!Register(true)) { serverRegistered = false; RemoveAdvertisement(); } else Advertise(); }
                else { serverRegistered = false; foreach (var connection in peers.Keys.Where(c => c is NetworkConnectionToClient).ToArray()) peers.Remove(connection); }
                if (!NetworkClient.active) { clientRegistered = false; foreach (var connection in peers.Keys.Where(c => !(c is NetworkConnectionToClient)).ToArray()) peers.Remove(connection); }
                if (enabled && !Register(false)) DisableClient("Protection client handler ownership lost; local protection continues");
            }
            if (enabled && plugin.Active && !WaitingRecovery && NetworkClient.connection != null)
            {
                var offer = upload.Poll(now);
                if (offer != null) { sendingBlocks.Clear(); Send(NetworkClient.connection, ReplicaReceiver.Message("offer", "manifest", offer.Serialize())); }
            }
            tokens = Math.Min(32768, tokens + Math.Max(0, now - lastSend) * 65536); lastSend = now;
            while (outgoing.Count > 0 && tokens >= 512)
            {
                var next = outgoing.Peek();
                bool valid = next.Connection is NetworkConnectionToClient ? NetworkServer.active && NetworkServer.connections.TryGetValue(next.Connection.connectionId, out var current) && ReferenceEquals(current, next.Connection) : enabled && ReferenceEquals(NetworkClient.connection, next.Connection);
                if (!valid) { outgoing.Dequeue(); continue; }
                int max = Math.Min(8192, Transport.activeTransport.GetMaxPacketSize(0) - 256);
                if (max < 128) { outgoing.Dequeue(); DisableClient("Transport packet limit too small"); continue; }
                int length = Math.Min(Math.Min(max, (int)tokens - 128), next.Payload.Length - next.Offset); if (length <= 0) break;
                var bytes = new byte[length]; Buffer.BlockCopy(next.Payload, next.Offset, bytes, 0, length);
                next.Connection.Send(new ProtectionWireV2 { Id = next.Id, Total = next.Payload.Length, Offset = next.Offset, Bytes = bytes });
                if (!(next.Connection is NetworkConnectionToClient)) upload.Progress(now);
                tokens -= length + 128; next.Offset += length; if (next.Offset == next.Payload.Length) outgoing.Dequeue();
            }
        }
        private void Send(NetworkConnection connection, Dictionary<string, object?> message)
        {
            var bytes = BinaryData.Encode(message);
            if (outgoing.Sum(p => (long)p.Payload.Length - p.Offset) + bytes.Length > 32L * 1024 * 1024) throw new InvalidDataException("Protection send queue limit");
            outgoing.Enqueue(new Outgoing { Connection = connection, Payload = bytes, Id = BinaryData.Hash(bytes) });
        }
        private void QueueOperation(int bytes, Action work, Action done, Action<Exception>? failed = null)
        {
            if (System.Threading.Interlocked.Add(ref workBytes, bytes) > 32L * 1024 * 1024)
            { System.Threading.Interlocked.Add(ref workBytes, -bytes); throw new InvalidDataException("Backup receive work budget exceeded"); }
            if (!plugin.QueueWork(() => { try { work(); } finally { System.Threading.Interlocked.Add(ref workBytes, -bytes); } }, done, failed))
            { System.Threading.Interlocked.Add(ref workBytes, -bytes); throw new InvalidDataException("Backup work queue full"); }
        }
        private Peer GetPeer(NetworkConnection connection)
        { if (!peers.TryGetValue(connection, out var peer)) peers.Add(connection, peer = new Peer()); return peer; }
        private Dictionary<string, object?>? Assemble(NetworkConnection conn, ProtectionWireV2 message)
        {
            var bytes = GetPeer(conn).Fragments.Add(message.Id, message.Total, message.Offset, message.Bytes, Time.realtimeSinceStartup);
            return bytes == null ? null : BinaryData.Map(BinaryData.Decode(bytes));
        }
        private void ServerMessage(NetworkConnection conn, ProtectionWireV2 wire)
        {
            try
            {
                // FizzySteamworks connection addresses identify the remote Steam user.
                if (conn.connectionId == 0 || !conn.isAuthenticated || !ulong.TryParse(conn.address, out var steam) || steam == 0 || CustomNetworkManager.manage.checkIfLanGame()) return;
                var message = Assemble(conn, wire); if (message == null) return;
                var peer = GetPeer(conn); string kind = BinaryData.Text(message["kind"]);
                if (kind == "hello")
                {
                    if (BinaryData.Number(message["protocol"]) != 2) throw new InvalidDataException("Protocol mismatch");
                    string character = BinaryData.Text(message["character"]); if (!System.Guid.TryParseExact(character, "N", out _)) throw new InvalidDataException("Invalid character");
                    if (peer.Character != "") throw new InvalidDataException("Duplicate handshake on an active connection");
                    peer.Character = character; peer.World = plugin.WorldIdentity();
                    string directory = Path.Combine(plugin.Root, "host", peer.World, steam.ToString(), character);
                    bool replace = message["baseline"] is bool baseline && baseline || message["choice"] is string selection && selection == "Local";
                    bool restore = message["restore"] is bool requested && requested && !(message["choice"] is string selected && selected == "Local");
                    Snapshot? latest = null; string serverSession = ServerSession;
                    QueueOperation(65536, () =>
                    {
                        if (peer.Cancelled) return;
                        peer.Store = new SnapshotStore(directory); peer.Receiver = new ReplicaReceiver(peer.Store, character, replace);
                        if (restore) latest = peer.Store.Latest(character);
                    }, () =>
                    {
                        if (peer.Cancelled || !peers.TryGetValue(conn, out var current) || !ReferenceEquals(current, peer)) return;
                        peer.Ready = true;
                        Send(conn, ReplicaReceiver.Message("welcome", "protocol", 2L, "world", peer.World, "hostSession", serverSession, "character", character, "manifest", latest?.Serialize()));
                    }, e => { if (!peer.Cancelled) Send(conn, ReplicaReceiver.Message("error", "message", "Host history unavailable")); plugin.Warn(e.Message); });
                    return;
                }
                if (!peer.Ready || peer.Receiver == null || peer.Store == null) throw new InvalidDataException("Handshake required");
                if (kind == "get")
                {
                    string id = BinaryData.Text(message["id"]); Capture? capture = null;
                    QueueOperation(24 * 1024 * 1024, () =>
                    {
                        if (peer.Cancelled) return;
                        var snapshot = peer.Store.Find(id) ?? throw new InvalidDataException("Unknown version");
                        if (snapshot.Character != peer.Character) throw new InvalidDataException("Wrong character");
                        capture = peer.Store.Read(snapshot);
                    }, () =>
                    {
                        if (!peers.TryGetValue(conn, out var current) || !ReferenceEquals(current, peer) || capture == null) return;
                        Send(conn, ReplicaReceiver.Message("download", "manifest", capture.Manifest.Serialize()));
                        foreach (var block in capture.Blocks) Send(conn, ReplicaReceiver.Message("download-block", "id", capture.Manifest.Id, "hash", block.Key, "data", block.Value));
                    }, e => { if (peers.ContainsKey(conn)) Send(conn, ReplicaReceiver.Message("error", "message", "Backup download failed")); plugin.Warn(e.Message); });
                }
                else if (kind == "offer" || kind == "block")
                {
                    Dictionary<string, object?>? reply = null;
                    // The plugin worker serializes store operations. Only enqueue Mirror sends on the main thread.
                    int bytes = kind == "offer" ? ((byte[])message["manifest"]!).Length : ((byte[])message["data"]!).Length;
                    if (kind == "offer" && (bytes > 65536 || Snapshot.Parse((byte[])message["manifest"]!).Build != Plugin.Build)) throw new InvalidDataException("Invalid manifest build/size");
                    if (kind == "block" && bytes > BinaryData.MaxModuleBytes) throw new InvalidDataException("Oversized module");
                    QueueOperation(bytes, () =>
                    {
                        if (peer.Cancelled) return;
                        reply = kind == "offer" ? peer.Receiver.Offer((byte[])message["manifest"]!) : peer.Receiver.Block(BinaryData.Text(message["id"]), BinaryData.Text(message["hash"]), (byte[])message["data"]!);
                        // Hosts store extension data opaquely: do not prune a good backup based on an unvalidated remote candidate.
                    }, () => { if (peers.TryGetValue(conn, out var current) && ReferenceEquals(current, peer) && reply != null) Send(conn, reply); },
                    e => { if (peers.ContainsKey(conn)) Send(conn, ReplicaReceiver.Message("error", "message", "Backup candidate rejected")); plugin.Warn(e.Message); });
                }
                else throw new InvalidDataException("Unknown protection operation");
            }
            catch (Exception e) { plugin.Warn("Host backup rejected message: " + e.Message); try { Send(conn, ReplicaReceiver.Message("error", "message", e.Message.Substring(0, Math.Min(512, e.Message.Length)))); } catch (Exception nested) { plugin.Warn(nested.Message); } }
        }
        private void ClientMessage(ProtectionWireV2 wire)
        {
            if (!enabled || NetworkClient.connection == null) return;
            try
            {
                LastRecoveryProgress = Time.realtimeSinceStartup;
                var message = Assemble(NetworkClient.connection, wire); if (message == null) return;
                string kind = BinaryData.Text(message["kind"]); var store = plugin.Store ?? throw new InvalidOperationException("No local store");
                if (kind == "welcome")
                {
                    if (!WaitingRecovery || requestedRecovery != "") throw new InvalidDataException("Unsolicited welcome");
                    if (BinaryData.Number(message["protocol"]) != 2 || BinaryData.Text(message["character"]) != plugin.Character) throw new InvalidDataException("Handshake mismatch");
                    world = BinaryData.Text(message["world"]); if (!System.Guid.TryParseExact(world, "N", out _)) throw new InvalidDataException("Invalid host world");
                    plugin.ConfirmHostSession(BinaryData.Text(message["hostSession"]), world);
                    if (!plugin.RecoveryAllowed) { WaitingRecovery = false; return; }
                    var remote = message["manifest"] is byte[] bytes ? Snapshot.Parse(bytes) : null;
                    var local = plugin.Head;
                    if (plugin.RecoverySource == "Local") { if (local != null) upload.Enqueue(local); WaitingRecovery = false; }
                    else if (remote != null && (plugin.RecoverySource == "Host" || local == null || Versions.Compare(local, remote, store.Find) == VersionOrder.RightNewer)) { requestedRecovery = remote.Id; Send(NetworkClient.connection, ReplicaReceiver.Message("get", "id", remote.Id)); }
                    else if (remote != null && local != null && Versions.Compare(local, remote, store.Find) == VersionOrder.Conflict) { plugin.Pause("Host and client histories diverged. Both copies retained; select a candidate before restoring."); WaitingRecovery = false; }
                    else { WaitingRecovery = false; if (local != null) upload.Enqueue(local); }
                }
                else if (kind == "download")
                {
                    downloading = Snapshot.Parse((byte[])message["manifest"]!);
                    if (!WaitingRecovery || !plugin.RecoveryAllowed || downloading.Id != requestedRecovery || downloading.Character != plugin.Character || downloading.Build != Plugin.Build) throw new InvalidDataException("Unsolicited/ineligible recovery snapshot");
                    if (store.Missing(downloading).Length == 0) { plugin.AcceptRecovery(downloading); downloading = null; WaitingRecovery = false; }
                }
                else if (kind == "download-block")
                {
                    if (!WaitingRecovery && BinaryData.Text(message["id"]) == requestedRecovery) return;
                    var snapshot = downloading ?? throw new InvalidDataException("Unsolicited recovery block"); string hash = BinaryData.Text(message["hash"]);
                    if (snapshot.Id != BinaryData.Text(message["id"]) || !snapshot.Modules.Values.Contains(hash)) throw new InvalidDataException("Recovery block mismatch");
                    store.PutBlock(hash, (byte[])message["data"]!);
                    if (store.Missing(snapshot).Length == 0) { plugin.AcceptRecovery(snapshot); downloading = null; WaitingRecovery = false; }
                }
                else if (kind == "need")
                {
                    if (upload.Active?.Id != BinaryData.Text(message["id"])) return;
                    var snapshot = upload.Active;
                    var hashes = BinaryData.List(message["hashes"]).Select(BinaryData.Text).Distinct().ToArray();
                    if (hashes.Length > 128) throw new InvalidDataException("Too many requested blocks");
                    var wanted = new List<string>();
                    foreach (var hash in hashes)
                    {
                        if (!snapshot.Modules.Values.Contains(hash)) throw new InvalidDataException("Host requested unknown block");
                        if (sendingBlocks.Add(snapshot.Id + hash)) wanted.Add(hash);
                    }
                    var connection = NetworkClient.connection; int epoch = clientEpoch; var blocks = new Dictionary<string, byte[]>();
                    if (wanted.Count != 0) QueueOperation(24 * 1024 * 1024,
                        () => { foreach (var hash in wanted) blocks.Add(hash, store.ReadBlock(hash)); },
                        () => { if (epoch != clientEpoch || !enabled || !ReferenceEquals(connection, NetworkClient.connection)) return; foreach (var block in blocks) Send(connection, ReplicaReceiver.Message("block", "id", snapshot.Id, "hash", block.Key, "data", block.Value)); },
                        e => DisableClient("Upload read failed: " + e.Message));
                    upload.Progress(Time.realtimeSinceStartup);
                }
                else if (kind == "progress") upload.Progress(Time.realtimeSinceStartup);
                else if (kind == "ack") { upload.Ack(BinaryData.Text(message["id"])); sendingBlocks.Clear(); }
                else if (kind == "error" || kind == "newer") DisableClient("Host reported a backup conflict or error; local protection continues");
                else throw new InvalidDataException("Unknown host operation");
            }
            catch (Exception e) { DisableClient("Invalid host backup response: " + e.Message); }
        }
        internal void Disconnected(NetworkConnection conn)
        {
            // Commit occurred before acknowledgement. Disconnect must never inspect a destroyed remote prefab.
            if (peers.TryGetValue(conn, out var peer)) { peer.Cancelled = true; peer.Fragments.Clear(); peers.Remove(conn); }
            RemoveOutgoing(c => ReferenceEquals(c, conn));
        }
        internal void DisableClient(string reason) { plugin.Warn(reason); ResetClient(); }
        internal void ResetClient()
        {
            clientEpoch++; enabled = false; WaitingRecovery = false; downloading = null; requestedRecovery = ""; upload.Reset(); sendingBlocks.Clear();
            foreach (var conn in peers.Keys.Where(c => !(c is NetworkConnectionToClient)).ToArray()) { peers[conn].Fragments.Clear(); peers.Remove(conn); }
            RemoveOutgoing(c => !(c is NetworkConnectionToClient));
        }
        private void RemoveOutgoing(Func<NetworkConnection, bool> remove)
        { var keep = outgoing.Where(p => !remove(p.Connection)).ToArray(); outgoing.Clear(); foreach (var p in keep) outgoing.Enqueue(p); }
        public void Dispose()
        {
            disposed = true; ResetClient(); outgoing.Clear();
            var id = MessagePacking.GetId<ProtectionWireV2>();
            if (serverHandler != null && ReferenceEquals(Handlers(typeof(NetworkServer))[id], serverHandler)) NetworkServer.UnregisterHandler<ProtectionWireV2>();
            if (clientHandler != null && ReferenceEquals(Handlers(typeof(NetworkClient))[id], clientHandler)) NetworkClient.UnregisterHandler<ProtectionWireV2>();
            RemoveAdvertisement();
        }
    }
}
