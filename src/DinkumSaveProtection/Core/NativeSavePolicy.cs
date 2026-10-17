namespace DinkumSaveProtection.Core
{
    public enum NativeSaveDecision { Allow, ProtectionPaused, InitializationIncomplete, Restoring, UnexpectedDisconnect }

    public static class NativeSavePolicy
    {
        // Matches the existing safety gate. Classification only improves diagnostics.
        public static NativeSaveDecision Decide(bool newFormat, bool initialized, bool complete, bool blocked, bool restoring, bool unexpected)
        {
            if (!newFormat) return NativeSaveDecision.Allow;
            if (blocked) return NativeSaveDecision.ProtectionPaused;
            if (!initialized) return NativeSaveDecision.Allow;
            if (restoring) return NativeSaveDecision.Restoring;
            if (!complete) return NativeSaveDecision.InitializationIncomplete;
            if (unexpected) return NativeSaveDecision.UnexpectedDisconnect;
            return NativeSaveDecision.Allow;
        }
        public static string Message(NativeSaveDecision decision)
        {
            switch (decision)
            {
                case NativeSaveDecision.UnexpectedDisconnect: return "Skipped native save after unexpected disconnect to protect the original save. Returning to menu may continue; last committed recovery cache retained.";
                case NativeSaveDecision.ProtectionPaused: return "Blocked native save because protection is paused; inspect the preceding PROTECTION PAUSED message.";
                case NativeSaveDecision.InitializationIncomplete: return "Blocked native save: personal initialization has not completed.";
                case NativeSaveDecision.Restoring: return "Blocked native save while recovery is being applied; source save and committed cache retained.";
                default: return "Native save allowed.";
            }
        }
    }
}
