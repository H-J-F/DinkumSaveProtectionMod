using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DinkumSaveProtection.Core
{
    public static class NativeFileRecovery
    {
        // Selects a fully validated candidate; this method never writes a file.
        public static string? Select(string path, Func<string, bool> exists, Action<string> validate, Action<string>? warn = null)
        {
            try
            {
                if (!exists(path)) throw new FileNotFoundException("Native file is missing", path);
                validate(path); return null;
            }
            catch (NativeCompatibilityException e) { warn?.Invoke("Readable native save retained: " + e.Message); throw; }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException || e is EndOfStreamException || e is InvalidDataException || e is CryptographicException || e is FormatException)
            { warn?.Invoke("Native file could not be decoded: " + e.GetType().Name + ": " + e.Message); }
            var failures = new List<string>();
            foreach (string suffix in new[] { ".bak", ".bac" })
            {
                string candidate = path + suffix;
                if (!exists(candidate)) { failures.Add(suffix + ": missing"); continue; }
                try { validate(candidate); return candidate; }
                catch (Exception e) { string reason = suffix + ": " + e.GetType().Name + ": " + e.Message; failures.Add(reason); warn?.Invoke("Native backup rejected " + reason); }
            }
            throw new InvalidDataException("Native file could not be decoded; no usable backup. Source retained; no default data will be checkpointed. " + string.Join("; ", failures));
        }
    }
}
