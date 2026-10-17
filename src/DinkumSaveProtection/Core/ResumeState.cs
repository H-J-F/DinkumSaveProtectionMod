using System;
using System.Collections.Generic;
using System.IO;

namespace DinkumSaveProtection.Core
{
    public enum ExitReason { Running, Unexpected, Voluntary }
    public sealed class ExitIntent
    {
        public ExitReason Reason { get; private set; } = ExitReason.Running;
        public bool TryVoluntary()
        { if (Reason == ExitReason.Unexpected) return false; Reason = ExitReason.Voluntary; return true; }
        public void Disconnected() { if (Reason == ExitReason.Running) Reason = ExitReason.Unexpected; }
        public void CancelVoluntary() { if (Reason == ExitReason.Voluntary) Reason = ExitReason.Running; }
    }

    // This durable pointer, rather than a directory's newest manifest, authorizes recovery.
    public sealed class ResumeState
    {
        public string Character { get; }
        public string Host { get; }
        public string HostSession { get; }
        public ExitReason Reason { get; }
        public string Snapshot { get; }
        public ResumeState(string character, string host, string hostSession, ExitReason reason, string snapshot)
        {
            if (!Guid.TryParseExact(character, "N", out _) || host.Length > 256 || hostSession.Length > 256 ||
                !Enum.IsDefined(typeof(ExitReason), reason) || snapshot != "" && !BinaryData.IsHash(snapshot)) throw new InvalidDataException("Invalid resume state");
            Character = character; Host = host; HostSession = hostSession; Reason = reason; Snapshot = snapshot;
        }
        public bool Allows(string character, string host, string hostSession) => Character == character &&
            (Reason != ExitReason.Voluntary || Host != "" && HostSession != "" && Host == host && HostSession == hostSession);
        public byte[] Serialize() => BinaryData.Encode(new Dictionary<string, object?> {
            ["format"] = 1L, ["character"] = Character, ["host"] = Host, ["hostSession"] = HostSession,
            ["reason"] = Reason.ToString(), ["snapshot"] = Snapshot });
        public static ResumeState Parse(byte[] bytes)
        {
            var m = BinaryData.Map(BinaryData.Decode(bytes));
            if (m.Count != 6 || BinaryData.Number(m["format"]) != 1 || !Enum.TryParse(BinaryData.Text(m["reason"]), out ExitReason reason)) throw new InvalidDataException("Invalid resume format");
            return new ResumeState(BinaryData.Text(m["character"]), BinaryData.Text(m["host"]), BinaryData.Text(m["hostSession"]), reason, BinaryData.Text(m["snapshot"]));
        }
        public static ResumeState? Read(string path)
        {
            // Never fall back to a previous Running record: it could erase a voluntary exit.
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Oversized resume state");
            return Parse(File.ReadAllBytes(path));
        }
    }
}
