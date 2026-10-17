using System;
using System.IO;

namespace DinkumSaveProtection.Core
{
    // Readable but incompatible is not corrupt: never roll it back to an older backup.
    public sealed class NativeCompatibilityException : Exception
    {
        public NativeCompatibilityException(string message) : base(message) { }
    }
    public static class NativeLayoutRules
    {
        public static int Prefix(string field, Array? saved, int capacity, bool optional = false)
        {
            if (capacity < 1) throw new NativeCompatibilityException(field + ": runtime collection not initialized (capacity=" + capacity + ")");
            if (saved == null)
            {
                if (optional) return 0;
                throw new NativeCompatibilityException(field + ": required saved collection is null");
            }
            if (saved.Length > BinaryData.MaxElements || saved.Length > capacity)
                throw new NativeCompatibilityException(field + ": saved=" + saved.Length + ", runtime=" + capacity + "; refusing truncation");
            return saved.Length;
        }
        public static void Exact(string field, Array? saved, int capacity, bool optional = false)
        {
            int count = Prefix(field, saved, capacity, optional);
            if (saved != null && count != capacity) throw new NativeCompatibilityException(field + ": saved=" + count + ", runtime=" + capacity + "; native loader cannot expand this replacement array");
        }
        public static void Pair(string field, Array? left, Array? right)
        {
            if (left == null || right == null || left.Length != right.Length || left.Length > BinaryData.MaxElements)
                throw new NativeCompatibilityException(field + ": paired arrays differ (left=" + (left?.Length.ToString() ?? "null") + ", right=" + (right?.Length.ToString() ?? "null") + ")");
        }
        public static void Item(string field, int id, int stack, int count, Func<int, bool> exists)
        {
            try { PersonalDataRules.Item(id, stack, count, exists); }
            catch (InvalidDataException e) { throw new NativeCompatibilityException(field + ": " + e.Message + " (id=" + id + ", stack=" + stack + ")"); }
        }
    }
}
