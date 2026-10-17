using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace DinkumSaveProtection.Core
{
    public sealed class Snapshot
    {
        public const long Format = 1;
        public static readonly IReadOnlyList<string> Required = Array.AsReadOnly(new[] { "assets", "growth", "quests", "pedia", "relations", "mail", "stash", "vitals", "environment" });
        public string Character { get; }
        public string Session { get; }
        public long Sequence { get; }
        public string Parent { get; }
        public string SessionParent { get; }
        public string Build { get; }
        public string Id { get; }
        private readonly Dictionary<string, string> modules;
        public IReadOnlyDictionary<string, string> Modules => new ReadOnlyDictionary<string, string>(modules);
        public Snapshot(string character, string session, long sequence, string parent, string build, IDictionary<string, string> modules, string sessionParent = "")
        {
            if (!Guid.TryParseExact(character, "N", out _) || !Guid.TryParseExact(session, "N", out _) || sequence < 1 || (!string.IsNullOrEmpty(parent) && !BinaryData.IsHash(parent)))
                throw new InvalidDataException("Invalid snapshot identity");
            if (modules.Count > 128 || Required.Any(m => !modules.ContainsKey(m)) || modules.Any(p => !ValidModule(p.Key) || !BinaryData.IsHash(p.Value)))
                throw new InvalidDataException("Incomplete snapshot module list");
            if (sessionParent != "" && !BinaryData.IsHash(sessionParent)) throw new InvalidDataException("Invalid session parent");
            Character = character; Session = session; Sequence = sequence; Parent = parent; SessionParent = sessionParent; Build = build;
            this.modules = new Dictionary<string, string>(modules, StringComparer.Ordinal);
            Id = BinaryData.Hash(Serialize());
        }
        public static bool ValidModule(string s) => s.Length > 0 && s.Length <= 128 && s.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_');
        public byte[] Serialize() => BinaryData.Encode(new Dictionary<string, object?>
        {
            ["format"] = Format, ["character"] = Character, ["session"] = Session, ["sequence"] = Sequence,
            ["parent"] = Parent, ["sessionParent"] = SessionParent, ["build"] = Build,
            ["modules"] = modules.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal)
        });
        public static Snapshot Parse(byte[] bytes)
        {
            var m = BinaryData.Map(BinaryData.Decode(bytes));
            if (BinaryData.Number(m["format"]) != Format) throw new InvalidDataException("Unsupported snapshot format");
            return new Snapshot(BinaryData.Text(m["character"]), BinaryData.Text(m["session"]), BinaryData.Number(m["sequence"]),
                BinaryData.Text(m["parent"]), BinaryData.Text(m["build"]), BinaryData.Map(m["modules"]).ToDictionary(p => p.Key, p => BinaryData.Text(p.Value), StringComparer.Ordinal), BinaryData.Text(m["sessionParent"]));
        }
    }
    public sealed class Capture
    {
        public Snapshot Manifest { get; }
        public IReadOnlyDictionary<string, byte[]> Blocks { get; }
        public Capture(Snapshot manifest, IDictionary<string, byte[]> blocks)
        {
            Manifest = manifest;
            var copy = blocks.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal);
            if (copy.Values.Sum(b => (long)b.Length) > 24L * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds bounded transfer budget");
            foreach (var hash in manifest.Modules.Values)
                if (!copy.TryGetValue(hash, out var bytes) || BinaryData.Hash(bytes) != hash) throw new InvalidDataException("Incomplete capture");
            Blocks = copy;
        }
        public static Capture? Create(string character, string session, long sequence, Snapshot? previous, string build, IDictionary<string, byte[]> modules, bool force = false)
        {
            var hashes = modules.ToDictionary(p => p.Key, p => BinaryData.Hash(p.Value), StringComparer.Ordinal);
            if (!force && previous != null && previous.Modules.Count == hashes.Count && hashes.All(p => previous.Modules.TryGetValue(p.Key, out var h) && p.Value == h)) return null;
            var manifest = new Snapshot(character, session, sequence, previous?.Id ?? "", build, hashes, previous?.Session == session ? previous.SessionParent : previous?.Id ?? "");
            var blocks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var pair in modules) blocks[hashes[pair.Key]] = pair.Value;
            return new Capture(manifest, blocks);
        }
    }
    public enum VersionOrder { Same, LeftNewer, RightNewer, Conflict }
    public static class Versions
    {
        public static VersionOrder Compare(Snapshot a, Snapshot b, Func<string, Snapshot?> find)
        {
            if (a.Character != b.Character) return VersionOrder.Conflict;
            if (a.Id == b.Id) return VersionOrder.Same;
            if (a.Session == b.Session && a.Sequence == b.Sequence) return VersionOrder.Conflict;
            // A session sequence orders complete client-authored versions; never compare wall clocks.
            if (a.Session == b.Session) return a.Sequence > b.Sequence ? VersionOrder.LeftNewer : VersionOrder.RightNewer;
            if (Descends(a, b, find)) return VersionOrder.LeftNewer;
            if (Descends(b, a, find)) return VersionOrder.RightNewer;
            return VersionOrder.Conflict;
        }
        private static bool Descends(Snapshot a, Snapshot ancestor, Func<string, Snapshot?> find)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 1024 && seen.Add(a.Id); i++)
            {
                if (a.Id == ancestor.Id || a.Session == ancestor.Session && a.Sequence >= ancestor.Sequence) return true;
                if (a.Parent == ancestor.Id || a.SessionParent == ancestor.Id) return true;
                var next = find(a.SessionParent != "" ? a.SessionParent : a.Parent); if (next == null || next.Character != a.Character) break; a = next;
            }
            return false;
        }
    }
    public sealed class ChangeSchedule
    {
        private readonly double debounce, interval, audit;
        private double first = double.NaN, last, lastCapture = double.NegativeInfinity, nextAudit;
        public ChangeSchedule(double now = 0, double debounce = .25, double interval = 1, double audit = 5)
        { this.debounce = debounce; this.interval = interval; this.audit = audit; nextAudit = now + audit; }
        public void Dirty(double now) { if (double.IsNaN(first)) first = now; last = now; }
        public bool Due(double now) => now >= nextAudit || !double.IsNaN(first) && now - lastCapture >= interval && (now - last >= debounce || now - first >= interval);
        public void Captured(double now) { first = double.NaN; lastCapture = now; nextAudit = now + audit; }
    }
}
