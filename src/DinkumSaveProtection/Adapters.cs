using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DinkumSaveProtection
{
    public interface IPlayerDataAdapter
    {
        string Id { get; }
        string Version { get; }
        byte[] Capture();
        void Validate(byte[] data, string savedVersion);
        void Restore(byte[] data, string savedVersion);
    }
    public static class PlayerDataAdapters
    {
        private static readonly Dictionary<string, IPlayerDataAdapter> adapters = new Dictionary<string, IPlayerDataAdapter>(StringComparer.Ordinal);
        private static int mainThread;
        internal static void BindMainThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
        internal static void RequireMainThread()
        { if (mainThread == 0 || Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Adapters must be registered and accessed on the game main thread after plugin initialization"); }
        internal static IPlayerDataAdapter[] All { get { RequireMainThread(); return adapters.Values.ToArray(); } }
        public static event Action? Changed;
        public static void Register(IPlayerDataAdapter adapter)
        {
            RequireMainThread();
            if (adapter == null || !Core.Snapshot.ValidModule(adapter.Id) || adapter.Id.Length > 124 || string.IsNullOrWhiteSpace(adapter.Version)) throw new ArgumentException("Invalid adapter");
            if (adapters.ContainsKey(adapter.Id)) throw new InvalidOperationException("Duplicate adapter ID");
            adapters.Add(adapter.Id, adapter); Changed?.Invoke();
        }
        public static void NotifyChanged() => Changed?.Invoke();
        internal static IPlayerDataAdapter? Find(string id) { RequireMainThread(); return adapters.TryGetValue(id, out var a) ? a : null; }
    }
}
