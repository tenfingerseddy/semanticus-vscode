using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine
{
    /// <summary>
    /// One entry in the append-only Verified Edits audit trail — what a verified op (or an accountable
    /// override) did, with the deterministic evidence that backs the verdict. Persisted on the model
    /// (annotation <see cref="VerifiedEditsStore.Annotation"/>) so it travels with it — reload, git, deploy.
    /// Keyed to the session revision AND a content hash of the changed bodies (the revision counter is
    /// per-session and resets, so the hash is the durable half of the weld). Revision 0 = the record does not
    /// correspond to a model mutation (deploy outcomes, kept-the-original optimize verdicts). Hash-CHAINED,
    /// not signed — the license key is verify-only, so signing would be theater; the chain + the head anchor
    /// make editing, reordering, mid-chain deletion AND tail truncation detectable. The one thing they cannot
    /// catch: deleting BOTH audit annotations wholesale, which reads as a model with no history (inherent
    /// without an external anchor — say so, never claim more).
    /// </summary>
    public sealed class VerifiedEditRecord
    {
        public int Seq { get; set; }                 // 1-based chain position (stamped by Append; verified by Load)
        public string When { get; set; }             // ISO-8601 UTC, engine-stamped (clients send no time)
        public string SessionId { get; set; }
        public long Revision { get; set; }           // the mutation's revision; 0 = no mutation backs this record
        public string Origin { get; set; }           // "human" | "agent" | "system"
        public string Op { get; set; }               // optimize_measure | set_dax | create_measure | ... | apply_plan | deploy_live | deploy_stage | chain-reset | chain-archived
        public string ObjectRef { get; set; }        // e.g. "measure:Sales/Total"; null = model-level
        public string Verdict { get; set; }          // proven | validated | needs-review | overridden | deployed | batch | info
        public string Summary { get; set; }          // one human-readable line
        public string OverrideReason { get; set; }   // set only on verdict=overridden — the accountable act
        public string Evidence { get; set; }         // compact op-specific JSON (grid size, rows compared, truncated, benchmark…)
        public string BodyHash { get; set; }         // sha256 of the changed DAX body/bodies ("" when no body changed)
        public string BaseCommit { get; set; }       // the commit the model PROVABLY sat on — stamped only when the model is tracked AND clean vs HEAD; "" otherwise (dirty/untracked tree, or no repo), because a dirty tree means HEAD does not contain this state. The durable-time-travel anchor: the chain stores no prior body, so REVERT comes from this commit, not the chain.
        public string PrevHash { get; set; }         // prior record's Hash ("" for the first record)
        public string Hash { get; set; }             // sha256 over the canonical fields + PrevHash (+ BaseCommit when non-empty — a presence-implied 14th field; see HashOf); the chain link
    }

    /// <summary>The loaded trail + its self-check. <see cref="ChainIntact"/> false means a record was
    /// edited, removed, reordered or the tail truncated after the fact (or the blob is damaged) — surfaced,
    /// never hidden. <see cref="FirstBrokenSeq"/> is the 1-based POSITION of the first failing link (0 = none;
    /// positions match Seq while the prefix is intact, and unlike Seq they can't be forged to 0).</summary>
    public sealed class VerifiedEditsChain
    {
        public VerifiedEditRecord[] Records { get; set; } = Array.Empty<VerifiedEditRecord>();
        public bool ChainIntact { get; set; } = true;
        public int FirstBrokenSeq { get; set; }      // 0 = none
        public string Note { get; set; }
    }

    /// <summary>
    /// The append-only audit store (the Pro "memory" of Verified Edits). Mirrors WaiverStore's
    /// annotation-JSON persistence but with three deliberate differences:
    ///  • writes go through <see cref="AuditAnnotations"/> (NON-undoable) — an audit record must survive
    ///    undo_change from either door, or "append-only" is a false promise (callers must also mark the
    ///    session audit-dirty, or a save after undo-to-checkpoint silently drops the trail);
    ///  • records are hash-chained (length-prefixed canonical basis — no field-boundary smearing) AND
    ///    anchored by a head annotation (count + last hash), so Load proves the trail wasn't edited,
    ///    reordered, mid-deleted or tail-truncated;
    ///  • a corrupt blob fails LOUD: it is preserved verbatim under <see cref="Damaged"/> and the new chain
    ///    starts with an explicit chain-reset record — never the waivers' silent degrade-to-empty (silent
    ///    data loss is exactly what an audit trail exists to prevent).
    /// The active chain is capped at <see cref="SegmentCap"/> records: on overflow it is archived whole under
    /// a numbered annotation and a fresh chain opens with a chain-archived record carrying the archive's
    /// count + last hash (so the active chain vouches for the frozen segment). Bounded append cost, no loss.
    /// Callers append on the dispatcher thread, AFTER the mutation they record — so a rolled-back edit
    /// never leaves a phantom record, and the single-writer dispatcher is the chain's concurrency discipline.
    /// </summary>
    public static class VerifiedEditsStore
    {
        public const string Annotation = "Semanticus_VerifiedEdits";
        public const string HeadAnnotation = "Semanticus_VerifiedEdits_Head";     // "<count>|<lastHash>" — the tail-truncation anchor
        public const string Damaged = "Semanticus_VerifiedEdits_Damaged";
        public const string ArchivePrefix = "Semanticus_VerifiedEdits_Archive_";  // frozen full segments, numbered from 1
        private const int EvidenceCap = 20_000;      // chars per record — keeps the chain compact inside the model file
        private const int SegmentCap = 500;          // records per active chain — bounds the O(chain) reparse per append

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public static VerifiedEditsChain Load(Model m)
        {
            var raw = AuditAnnotations.Get(m, Annotation);
            var head = AuditAnnotations.Get(m, HeadAnnotation);
            if (string.IsNullOrWhiteSpace(raw))
                return string.IsNullOrWhiteSpace(head)
                    ? new VerifiedEditsChain()
                    : new VerifiedEditsChain { ChainIntact = false, Note = "head anchor present but the chain annotation is missing — the trail was deleted" };
            List<VerifiedEditRecord> recs;
            try { recs = JsonSerializer.Deserialize<List<VerifiedEditRecord>>(raw, JsonOpts) ?? new List<VerifiedEditRecord>(); }
            catch
            {
                return new VerifiedEditsChain { ChainIntact = false, Note = "audit blob unparseable — records unreadable (the next append preserves it under " + Damaged + " and starts a fresh chain)" };
            }
            var chain = new VerifiedEditsChain { Records = recs.Where(r => r != null).ToArray() };
            var broken = FirstBroken(recs);
            if (broken != 0)
            {
                chain.ChainIntact = false; chain.FirstBrokenSeq = broken;
                chain.Note = $"chain broken at seq {broken} — a record was edited, removed or reordered after it was written";
                return chain;
            }
            // Internal links verified — now the head anchor, which is what catches TAIL truncation (a prefix of a
            // valid chain is itself a valid chain, so link-checking alone can't see a dropped newest record).
            var expected = HeadOf(recs);
            if (head != expected)
            {
                chain.ChainIntact = false;
                chain.Note = head == null
                    ? "head anchor missing — the trail's tail cannot be vouched for (was it truncated?)"
                    : "head anchor mismatch — the chain's tail was truncated or replaced after it was written";
            }
            return chain;
        }

        /// <summary>Stamp (Seq/When/PrevHash/Hash) and append <paramref name="rec"/>, non-undoably. Archives the
        /// active chain first when it is full.</summary>
        public static VerifiedEditRecord Append(Model m, VerifiedEditRecord rec)
        {
            var recs = LoadForAppend(m);
            if (recs.Count >= SegmentCap) recs = ArchiveSegment(m, recs);
            if (rec.Evidence != null && rec.Evidence.Length > EvidenceCap)
                rec.Evidence = JsonSerializer.Serialize(new { omitted = "evidence exceeded the size cap", size = rec.Evidence.Length });
            Stamp(recs, rec);
            recs.Add(rec);
            AuditAnnotations.Set(m, Annotation, JsonSerializer.Serialize(recs));
            AuditAnnotations.Set(m, HeadAnnotation, HeadOf(recs));
            return rec;
        }

        // Parse the existing chain for an append. An unparseable blob — or one carrying null elements — is
        // PRESERVED under Damaged (appended, never overwritten) and the fresh chain opens with an explicit
        // chain-reset record — loud, lossless.
        private static List<VerifiedEditRecord> LoadForAppend(Model m)
        {
            var raw = AuditAnnotations.Get(m, Annotation);
            if (string.IsNullOrWhiteSpace(raw)) return new List<VerifiedEditRecord>();
            List<VerifiedEditRecord> parsed = null;
            try { parsed = JsonSerializer.Deserialize<List<VerifiedEditRecord>>(raw, JsonOpts); } catch { }
            if (parsed != null && !parsed.Contains(null)) return parsed;
            var prior = AuditAnnotations.Get(m, Damaged);
            AuditAnnotations.Set(m, Damaged, string.IsNullOrEmpty(prior) ? raw : prior + "\n----\n" + raw);
            var recs = new List<VerifiedEditRecord>();
            var reset = new VerifiedEditRecord
            {
                Op = "chain-reset", Verdict = "info", Origin = "system",
                Summary = "prior audit blob was unparseable — preserved verbatim under " + Damaged + "; chain restarted",
            };
            Stamp(recs, reset);
            recs.Add(reset);
            return recs;
        }

        // Freeze the full active segment under the next numbered archive annotation and open a fresh chain whose
        // first record vouches for the frozen one (count + last hash, themselves hash-chained forward).
        private static List<VerifiedEditRecord> ArchiveSegment(Model m, List<VerifiedEditRecord> recs)
        {
            var n = 1;
            while (AuditAnnotations.Get(m, ArchivePrefix + n) != null) n++;
            AuditAnnotations.Set(m, ArchivePrefix + n, JsonSerializer.Serialize(recs));
            var fresh = new List<VerifiedEditRecord>();
            var marker = new VerifiedEditRecord
            {
                Op = "chain-archived", Verdict = "info", Origin = "system",
                Summary = $"active chain reached {recs.Count} records — archived whole under {ArchivePrefix + n}",
                Evidence = JsonSerializer.Serialize(new { archived = recs.Count, lastHash = recs[recs.Count - 1].Hash, annotation = ArchivePrefix + n }),
            };
            Stamp(fresh, marker);
            fresh.Add(marker);
            return fresh;
        }

        private static void Stamp(List<VerifiedEditRecord> recs, VerifiedEditRecord rec)
        {
            var last = recs.Count == 0 ? null : recs[recs.Count - 1];
            rec.Seq = (last?.Seq ?? 0) + 1;
            rec.When = DateTime.UtcNow.ToString("o");
            rec.PrevHash = last?.Hash ?? "";
            rec.Hash = HashOf(rec);
        }

        /// <summary>The head-anchor value for a chain: count + the last record's hash.</summary>
        private static string HeadOf(List<VerifiedEditRecord> recs)
            => recs.Count == 0 ? "" : recs.Count + "|" + recs[recs.Count - 1].Hash;

        /// <summary>1-based POSITION of the first record failing the self-check (0 = links intact). Uses the
        /// position, not the stored Seq, as the failure signal — a tampered record could set its own Seq to 0
        /// and collide with the no-break sentinel. A null element, a Seq that disagrees with its position, a
        /// broken PrevHash link, or a hash mismatch all flag that position.</summary>
        private static int FirstBroken(List<VerifiedEditRecord> recs)
        {
            string prev = "";
            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r == null || r.Seq != i + 1 || (r.PrevHash ?? "") != prev || r.Hash != HashOf(r)) return i + 1;
                prev = r.Hash;
            }
            return 0;
        }

        // The canonical hash basis. Every field is LENGTH-PREFIXED before joining, so content can never smear
        // across a field boundary (a bare \n join would let "Summary\nX" | "Y" re-split as "Summary" | "X\nY"
        // with an identical digest — an undetectable edit of the override reason). Evidence enters via its own
        // hash so the link stays short. The format (order + prefixing) is part of the chain — changing it
        // invalidates every existing chain.
        //
        // PRESENCE-IMPLIED BASIS VERSION (the BaseCommit back-compat migration). BaseCommit joins the basis ONLY
        // when it is non-empty — the presence of the field IS the version marker, so no version byte is stored:
        //  • A record with NO BaseCommit (every record written before the field existed, and any non-git model)
        //    hashes over the EXACT original 13-field basis → every chain already in the wild keeps verifying byte
        //    for byte. This is why the migration cannot break existing chains.
        //  • A record WITH a BaseCommit folds it in as a 14th field, so the commit pointer is tamper-evident.
        //  • Self-protecting both ways: STRIPPING BaseCommit off a stamped record drops it to the short basis
        //    (hash mismatch → detected); FORGING one onto an unstamped record lifts it to the long basis
        //    (hash mismatch → detected). There is no ambiguous middle where a swap goes unnoticed.
        private static string HashOf(VerifiedEditRecord r)
        {
            var basis = new List<string>
            {
                r.Seq.ToString(), r.When ?? "", r.SessionId ?? "", r.Revision.ToString(), r.Origin ?? "", r.Op ?? "",
                r.ObjectRef ?? "", r.Verdict ?? "", r.Summary ?? "", r.OverrideReason ?? "",
                Sha256(r.Evidence ?? ""), r.BodyHash ?? "", r.PrevHash ?? "",
            };
            if (!string.IsNullOrEmpty(r.BaseCommit)) basis.Add(r.BaseCommit);   // presence-implied — see the block above
            return Sha256(Canon(basis.ToArray()));
        }

        private static string Canon(params string[] fields)
            => string.Join("\n", fields.Select(f => (f ?? "").Length + ":" + (f ?? "")));

        public static string Sha256(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).ToLowerInvariant();
        }

        /// <summary>Content hash of the changed DAX bodies — the durable half of the revision↔evidence weld.</summary>
        public static string BodyHash(params string[] bodies) => Sha256(Canon(bodies));

        /// <summary>The human export (boss/auditor). JSON export is just the serialized chain.</summary>
        public static string ToMarkdown(VerifiedEditsChain chain)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Verified Edits — audit trail");
            sb.AppendLine();
            sb.AppendLine(chain.ChainIntact
                ? $"Chain intact — {chain.Records.Length} record(s), each linking the previous by hash."
                : $"CHAIN NOT INTACT{(chain.FirstBrokenSeq > 0 ? $" (first break at seq {chain.FirstBrokenSeq})" : "")} — {chain.Note}");
            sb.AppendLine();
            foreach (var r in chain.Records)
            {
                sb.AppendLine($"## {r.Seq}. {Flat(r.Op)} — {Flat(r.Verdict)}");
                sb.AppendLine($"- **When:** {r.When}  ·  **Actor:** {Flat(r.Origin)}  ·  **Revision:** {r.Revision}");
                // The git commit the model sat on when this edit was recorded — the durable revert anchor (the chain
                // stores no prior body). Short sha; the line is omitted entirely for a non-git model, so those trails
                // render exactly as before this field existed. Sanitized like its neighbours: the chain is hashed but
                // NOT signed, so a fully-rewritten internally-consistent chain could carry a forged BaseCommit — Flat
                // strips newlines and the backtick swap keeps it from breaking out of the code span on export.
                if (!string.IsNullOrEmpty(r.BaseCommit)) sb.AppendLine($"- **Base commit:** `{Flat(ShortSha(r.BaseCommit)).Replace('`', '\'')}`");
                if (!string.IsNullOrEmpty(r.ObjectRef)) sb.AppendLine($"- **Object:** {Flat(r.ObjectRef)}");
                if (!string.IsNullOrEmpty(r.Summary)) sb.AppendLine($"- {Flat(r.Summary)}");
                if (!string.IsNullOrEmpty(r.OverrideReason)) sb.AppendLine($"- **Override reason:** {Flat(r.OverrideReason)}");
                if (!string.IsNullOrEmpty(r.Evidence)) sb.AppendLine($"- Evidence: `{Flat(r.Evidence).Replace('`', '\'')}`");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // Caller-supplied prose (an override reason, a summary) is rendered on ONE line — a multi-line reason
        // could otherwise inject a fabricated record heading into the auditor-facing markdown.
        private static string Flat(string s)
            => string.IsNullOrEmpty(s) ? s : s.Replace("\r", " ").Replace("\n", " ⏎ ");

        // A full 40-char commit is noise in a human report; show the first 12 (git's own abbreviation is longer than
        // enough to be unambiguous, and the full sha is still in the JSON export + the hash basis).
        private static string ShortSha(string sha)
            => string.IsNullOrEmpty(sha) || sha.Length <= 12 ? sha : sha.Substring(0, 12);
    }
}
