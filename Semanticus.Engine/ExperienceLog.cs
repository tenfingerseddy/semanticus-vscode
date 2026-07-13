using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// Learning Loop L0 — CAPTURE (docs/learning-loop-plan.md §3.1). The append-only experience log:
    /// a host-attached ChangeBus tee writes the whole dual-drive stream (change notifications + activity
    /// events, which now include the apply_plan report digest) to `.semanticus/experience.jsonl`, each
    /// record wrapped in a provenance envelope. Placement: beside the model for a file-backed session
    /// (LayoutStore's sidecar path authority, PBIP hop included); a LIVE session's anchor is an ephemeral
    /// %TEMP% snapshot that dies with cleanup, so those — the highest-value sessions — fall back to the
    /// host WORKSPACE's `.semanticus/`. The on-model hash-chained Semanticus_VerifiedEdits annotation
    /// remains the tamper-evident AUDIT (linked by session/revision, never duplicated here). Best-effort
    /// by design: capture is a ride-along — a failed append must never break the op that produced it.
    /// </summary>
    internal static class ExperienceStore
    {
        public const string FileName = "experience.jsonl";
        public const int SchemaVersion = 1;
        private const int MaxLineBytes = 32 * 1024;   // one oversized Result must not bloat the log forever

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static string FileFor(string sourcePath)
        {
            var dir = LayoutStore.DirFor(sourcePath);   // the one sidecar-path authority (incl. the PBIP definition/ hop)
            return dir == null ? null : Path.Combine(dir, FileName);
        }

        /// <summary>The GLOBAL %USERPROFILE%/.semanticus root — the last-resort durable home SHARED by the
        /// experience log and the vitals store (LocalEngine.VitalsFileFor) for a live/unsaved (ephemeral)
        /// session with no workspace anchored either. Without it those highest-value sessions (a live XMLA
        /// model, no folder open) would be captured nowhere and silently dropped. Honors USERPROFILE literally
        /// (GlobalKnowledgeDir's convention) so it's redirectable/testable; falls back to the OS profile only
        /// if the var is unset.</summary>
        public static string GlobalHomeDir()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, LayoutStore.DirName);
        }

        /// <summary>The GLOBAL experience.jsonl target beneath <see cref="GlobalHomeDir"/> — the tee routes
        /// its fallback here when there's no workspace either.</summary>
        public static string GlobalFile() => Path.Combine(GlobalHomeDir(), FileName);

        /// <summary>An unsaved model or a live/local XMLA snapshot (guid dirs under %TEMP%\semanticus-*) has
        /// no durable on-disk anchor — its log belongs in the workspace fallback, not a dir that evaporates.</summary>
        public static bool IsEphemeralAnchor(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return true;
            try
            {
                var full = Path.GetFullPath(sourcePath);
                var tempPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "semanticus-");
                return full.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Append one record as a JSON line. Returns false (never throws) when there is no target
        /// file or the write fails. <paramref name="recordWithoutPayload"/> replaces an oversized record.</summary>
        public static bool AppendLine(string file, object record, object recordWithoutPayload = null)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return false;
                var line = JsonSerializer.Serialize(record, JsonOpts);
                if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
                {
                    // Enforce the per-line cap HERE, at the one write chokepoint, regardless of caller — a change
                    // record (which supplies no fallback) must not be able to bloat the log with a giant delta set.
                    // Prefer the caller's payload-free fallback; if none was supplied, or it is ALSO oversized, drop
                    // to a generic summary so no single record can ever exceed the cap.
                    var fallback = recordWithoutPayload != null ? JsonSerializer.Serialize(recordWithoutPayload, JsonOpts) : null;
                    line = fallback != null && Encoding.UTF8.GetByteCount(fallback) <= MaxLineBytes
                        ? fallback
                        : JsonSerializer.Serialize(new
                        {
                            schemaVersion = SchemaVersion,
                            when = DateTime.UtcNow.ToString("o"),
                            kind = "oversized",
                            note = "record exceeded the per-line cap and was summarized",
                            bytes = Encoding.UTF8.GetByteCount(line),
                        }, JsonOpts);
                }
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
                }
                return true;
            }
            catch { return false; }
        }

        public static bool Append(string sourcePath, object record, object recordWithoutPayload = null)
            => AppendLine(FileFor(sourcePath), record, recordWithoutPayload);

        /// <summary>A cheap, stable model identity for the provenance envelope, so records group correctly
        /// across sessions: the sidecar anchor dir hashed for a file-backed model, endpoint|database for a
        /// live one. The SEMANTIC fingerprint (shape signature, domain hints, grade) is Phase L1.</summary>
        public static string FingerprintFor(string sourcePath)
        {
            var dir = LayoutStore.DirFor(sourcePath);
            return dir == null ? null : Hash(dir.ToUpperInvariant());
        }

        public static string FingerprintForLive(LiveOrigin origin)
            => origin == null ? null : Hash((origin.Endpoint + "|" + origin.Database).ToUpperInvariant());

        private static string Hash(string s)
        {
            try
            {
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)), 0, 8).ToLowerInvariant();
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The host-attached ChangeBus subscriber (RpcServer's subscribe pattern). Attached ONLY by the owner
    /// host (Program.Serve / owner-mode Mcp): an attaching MCP proxy never double-writes, and engine
    /// instances in tests capture nothing unless a test attaches one deliberately. Broadcasts arrive on
    /// the publishing thread, so appends are small, synchronous, and swallowed on failure.
    /// </summary>
    internal sealed class ExperienceTee : IDisposable
    {
        private readonly SessionManager _sessions;
        private readonly string _fallbackFile;   // durable target for an ephemeral (live/unsaved) session: the workspace when one exists, else global

        public ExperienceTee(SessionManager sessions, string workspaceDir = null)
        {
            _sessions = sessions;
            // An ephemeral (live/XMLA/unsaved) anchor has no durable on-disk home. Prefer the host workspace's
            // .semanticus/; with NO workspace either, fall back to the GLOBAL store rather than DROPPING capture —
            // a live-XMLA-no-folder-open session is the highest-value case and used to be lost silently.
            _fallbackFile = string.IsNullOrEmpty(workspaceDir)
                ? ExperienceStore.GlobalFile()
                : Path.Combine(workspaceDir, LayoutStore.DirName, ExperienceStore.FileName);
            _sessions.Bus.Changed += OnChanged;
            _sessions.Bus.Activity += OnActivity;
        }

        public void Dispose()
        {
            _sessions.Bus.Changed -= OnChanged;
            _sessions.Bus.Activity -= OnActivity;
        }

        private Session CurrentFor(string sessionId)
        {
            // M1 is single-session; ActivityEvent carries no session id, so both paths anchor on Current.
            var cur = _sessions.Current;
            return cur != null && (sessionId == null || cur.Id == sessionId) ? cur : null;
        }

        private string FileFor(Session s)
            => !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? ExperienceStore.FileFor(s.SourcePath) : _fallbackFile;

        private string FingerprintFor(Session s)
            => s.LiveOrigin != null ? ExperienceStore.FingerprintForLive(s.LiveOrigin)
             : !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? ExperienceStore.FingerprintFor(s.SourcePath)
             : null;

        private void OnChanged(ChangeNotification n)
        {
            var s = CurrentFor(n.SessionId);
            if (s == null) return;
            ExperienceStore.AppendLine(FileFor(s), new
            {
                schemaVersion = ExperienceStore.SchemaVersion,
                when = DateTime.UtcNow.ToString("o"),
                kind = "change",
                sessionId = n.SessionId,
                origin = n.Origin,
                modelFingerprint = FingerprintFor(s),
                inputSources = Array.Empty<string>(),
                revision = n.Revision,
                label = n.Label,
                deltas = n.Deltas?.Select(d => new { d.Kind, d.Ref, d.Props }).ToArray(),
            });
        }

        private void OnActivity(ActivityEvent e)
        {
            // Attribute on the session FROZEN onto the event at emit (PublishActivityAsync), not on whatever is
            // current now: a model swap between emit and this (possibly RPC-forwarded) handler must NOT record the
            // result under the new model. A mismatch ⇒ CurrentFor returns null ⇒ we DROP (never misattribute); a
            // null id (a direct in-op Bus.PublishActivity) falls back to Current, which is correct for those.
            var s = CurrentFor(e.SessionId);
            if (s == null) return;
            object Envelope(object result) => new
            {
                schemaVersion = ExperienceStore.SchemaVersion,
                when = DateTime.UtcNow.ToString("o"),
                kind = "activity",
                sessionId = s.Id,
                origin = e.Origin,
                modelFingerprint = FingerprintFor(s),
                inputSources = Array.Empty<string>(),
                seq = e.Seq,
                op = e.Kind,
                label = e.Label,
                target = e.Target,
                ok = e.Ok,
                error = e.Error,
                rowCount = e.RowCount,
                elapsedMs = e.ElapsedMs,
                result,
            };
            ExperienceStore.AppendLine(FileFor(s), Envelope(e.Result),
                Envelope("(payload dropped — exceeded the per-line cap)"));
        }
    }
}
