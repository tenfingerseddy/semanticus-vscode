using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// One accepted ("waived") finding. A waiver is a DOCUMENTED decision to not fix a finding — it stops the finding
    /// counting against the score but is always surfaced (tagged + reasoned), never hidden, so the grade can't be
    /// silently inflated. <see cref="ObjectRef"/> null/empty = a RULE-LEVEL waiver (every instance of the rule,
    /// model-wide — e.g. "we never remove unused columns"); a ref = a single instance.
    /// </summary>
    public sealed class WaiverRecord
    {
        public string System { get; set; }      // "bpa" | "air"
        public string RuleId { get; set; }
        public string ObjectRef { get; set; }    // null/empty = rule-level (all instances)
        public string Reason { get; set; }       // required — a waiver without a reason is a silent suppression
        public string By { get; set; }           // origin: "human" | "agent"
        public string When { get; set; }         // ISO-8601 UTC
    }

    /// <summary>
    /// Reads/writes finding waivers, persisted on the model as the <c>Semanticus_Waivers</c> annotation (a JSON array
    /// — travels with the model, survives reload/deploy, single source of truth for BOTH doors). For BPA it also
    /// honours + mirrors Tabular Editor's native per-object <c>BestPracticeAnalyzer_IgnoreRules</c> annotation, so a
    /// rule ignored in TE3 is respected here and a per-instance BPA waiver set here is honoured back in TE3.
    /// </summary>
    public static class WaiverStore
    {
        public const string Annotation = "Semanticus_Waivers";
        private const string TeIgnore = "BestPracticeAnalyzer_IgnoreRules";
        private const string TeIgnoreLegacy = "BestPractizeAnalyzer_IgnoreRules";   // TE's historical typo — read both
        public const string TeReason = "Ignored in Tabular Editor (BestPracticeAnalyzer_IgnoreRules).";

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public static bool IsRuleLevel(string objectRef) => string.IsNullOrWhiteSpace(objectRef) || objectRef.Trim() == "*";

        // ---- the Semanticus waiver store (model annotation) -----------------------------------------------------
        public static List<WaiverRecord> Load(Model m)
        {
            try
            {
                var raw = (m as IAnnotationObject)?.GetAnnotation(Annotation);
                if (string.IsNullOrWhiteSpace(raw)) return new List<WaiverRecord>();
                return Normalize(JsonSerializer.Deserialize<List<WaiverRecord>>(raw, JsonOpts));
            }
            catch { return new List<WaiverRecord>(); }   // a corrupt/old annotation degrades to "no waivers", never throws
        }

        /// <summary>Drop entries that cannot act as waivers — a JSON <c>null</c> element, or a record missing its
        /// system/rule id — so DISPLAY consumers degrade on a mangled store instead of throwing on a null record.
        /// The WRITE path treats their presence as corruption (preserve-aside via <see cref="LoadForWrite"/>).</summary>
        private static List<WaiverRecord> Normalize(List<WaiverRecord> recs)
        {
            recs ??= new List<WaiverRecord>();
            recs.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.System) || string.IsNullOrWhiteSpace(r.RuleId));
            return recs;
        }

        /// <summary>Load for the WRITE path (add/remove): unlike <see cref="Load"/> (which degrades a corrupt store to
        /// empty for DISPLAY), this distinguishes a genuinely-empty store from a CORRUPT one — so the caller can
        /// preserve the unreadable bytes aside BEFORE <see cref="Save"/> overwrites them. Corrupt covers BOTH an
        /// unparseable value AND a parseable one carrying structurally invalid entries (e.g. <c>[null]</c>) that a
        /// filtered re-save would otherwise silently drop. On corrupt, <paramref name="corruptRaw"/> carries the raw
        /// value to preserve; the returned list holds only the usable records.</summary>
        public static List<WaiverRecord> LoadForWrite(Model m, out string corruptRaw)
        {
            corruptRaw = null;
            var raw = (m as IAnnotationObject)?.GetAnnotation(Annotation);
            if (string.IsNullOrWhiteSpace(raw)) return new List<WaiverRecord>();
            try
            {
                var recs = JsonSerializer.Deserialize<List<WaiverRecord>>(raw, JsonOpts);
                // A non-whitespace raw that deserializes to null is a literal JSON `null` (the whitespace/empty case
                // already returned above). Coalescing it to an empty list would leave corruptRaw null, so the next
                // Save overwrites the `null` with no preservation. Treat it as corrupt — preserve the original aside.
                if (recs == null) { corruptRaw = raw; return new List<WaiverRecord>(); }
                var before = recs.Count;
                recs = Normalize(recs);
                if (recs.Count != before) corruptRaw = raw;   // partially unreadable — preserve the original before Save drops the rest
                return recs;
            }
            catch { corruptRaw = raw; return new List<WaiverRecord>(); }   // unreadable — the caller must preserve `raw` before saving
        }

        /// <summary>Copy an unreadable annotation value aside to a <c>&lt;name&gt;.corrupt-&lt;8hex&gt;</c> sibling
        /// annotation (travels with the model, recoverable) BEFORE any Save can overwrite the original. The name is
        /// allocated through a HasAnnotation retry loop — SetAnnotation UPDATES an existing name, so a collision
        /// would clobber a PREVIOUS backup. THROWS if a backup can't be written — the caller must then REFUSE the
        /// mutation rather than silently destroy the data.</summary>
        public static string PreserveCorrupt(IAnnotationObject ao, string baseName, string raw)
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var name = baseName + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (ao.HasAnnotation(name)) continue;   // taken — roll a fresh name, never update an existing backup
                ao.SetAnnotation(name, raw);            // throws → the mutation aborts (data preserved over progress)
                return name;
            }
            throw new InvalidOperationException($"could not allocate a free '{baseName}.corrupt-*' backup annotation name.");
        }

        public static void Save(Model m, List<WaiverRecord> recs)
        {
            if (!(m is IAnnotationObject ao)) return;
            if (recs == null || recs.Count == 0) { if (ao.HasAnnotation(Annotation)) ao.RemoveAnnotation(Annotation); return; }
            ao.SetAnnotation(Annotation, JsonSerializer.Serialize(recs));
        }

        /// <summary>The waiver covering (system, ruleId, objectRef), or null. A rule-level record (null ref) covers
        /// every instance; otherwise the refs must match (case-insensitive).</summary>
        public static WaiverRecord Match(IEnumerable<WaiverRecord> recs, string system, string ruleId, string objectRef)
        {
            if (recs == null) return null;
            foreach (var r in recs)
            {
                if (!string.Equals(r.System, system, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsRuleLevel(r.ObjectRef) || string.Equals(r.ObjectRef, objectRef, StringComparison.OrdinalIgnoreCase)) return r;
            }
            return null;
        }

        /// <summary>Add a waiver, replacing any existing record with the same (system, ruleId, ref) key — idempotent.</summary>
        public static void Add(List<WaiverRecord> recs, WaiverRecord rec)
        {
            Remove(recs, rec.System, rec.RuleId, rec.ObjectRef);
            recs.Add(rec);
        }

        /// <summary>Remove the matching waiver. Rule-level (null ref) only removes the rule-level record; an instance
        /// ref only removes that instance — so un-waiving one column doesn't silently drop a whole rule-level waiver.</summary>
        public static bool Remove(List<WaiverRecord> recs, string system, string ruleId, string objectRef)
        {
            bool rl = IsRuleLevel(objectRef);
            return recs.RemoveAll(r =>
                string.Equals(r.System, system, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) &&
                (rl ? IsRuleLevel(r.ObjectRef) : string.Equals(r.ObjectRef, objectRef, StringComparison.OrdinalIgnoreCase))) > 0;
        }

        // ---- Tabular Editor interop (BPA only): honour + mirror BestPracticeAnalyzer_IgnoreRules ------------------
        public static bool IsTeIgnored(ITabularObject obj, string ruleId)
        {
            var ids = ReadTeIgnore(obj);
            return ids != null && ids.Contains(ruleId);
        }

        public static HashSet<string> ReadTeIgnore(ITabularObject obj)
        {
            if (!(obj is IAnnotationObject ao)) return null;
            var raw = ao.HasAnnotation(TeIgnore) ? ao.GetAnnotation(TeIgnore)
                    : ao.HasAnnotation(TeIgnoreLegacy) ? ao.GetAnnotation(TeIgnoreLegacy) : null;
            return TryParseTeIgnore(raw, out var set) ? set : null;   // display path: an invalid annotation degrades to null
        }

        /// <summary>Structural validation of a TE ignore annotation — the FULL expected schema, not just "parses as
        /// JSON": TE's <c>AnalyzerIgnoreRules</c> serializes exactly one property, <c>RuleIDs</c>, an array of
        /// strings. Anything else — an array root, <c>{"RuleIDs":"NAME"}</c>, non-string elements, or extra
        /// properties our <c>{RuleIDs:[…]}</c> rewrite would silently drop — is INVALID, so the write path preserves
        /// it aside first. Returns true with the parsed set (null when empty) only for the exact shape.</summary>
        private static bool TryParseTeIgnore(string raw, out HashSet<string> set)
        {
            set = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;   // absent/empty = validly nothing
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                var props = 0;
                JsonElement arr = default;
                foreach (var p in root.EnumerateObject())
                {
                    if (++props > 1 || !p.NameEquals("RuleIDs")) return false;   // an unknown sibling would be dropped by our rewrite
                    arr = p.Value;
                }
                if (props == 0 || arr.ValueKind != JsonValueKind.Array) return false;
                var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String) return false;
                    s.Add(e.GetString());
                }
                set = s.Count > 0 ? s : null;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Mirror a per-instance BPA waiver into TE's annotation (so TE3 honours it) — and back out on unwaive.
        /// Writes the same <c>{"RuleIDs":[...]}</c> shape TE serializes. Data-preservation contract: BOTH the preferred
        /// and the legacy-typo key are inspected BEFORE anything is removed or rewritten, and any value that isn't TE's
        /// exact shape is preserved to a <c>.corrupt-*</c> backup first (a failed backup throws, aborting the enclosing
        /// mutation — refuse over destroy).</summary>
        public static void WriteTeIgnore(ITabularObject obj, string ruleId, bool ignored)
        {
            if (!(obj is IAnnotationObject ao)) return;
            var prefRaw = ao.HasAnnotation(TeIgnore) ? ao.GetAnnotation(TeIgnore) : null;
            var legRaw = ao.HasAnnotation(TeIgnoreLegacy) ? ao.GetAnnotation(TeIgnoreLegacy) : null;
            var prefOk = TryParseTeIgnore(prefRaw, out var prefSet);
            var legOk = TryParseTeIgnore(legRaw, out var legSet);
            if (!prefOk) PreserveCorrupt(ao, TeIgnore, prefRaw);
            if (!legOk) PreserveCorrupt(ao, TeIgnoreLegacy, legRaw);
            // The preferred key governs when present-and-valid (the shipped consolidation semantics); a readable
            // LEGACY value seeds the set when the preferred is absent or corrupt — readable data beats an empty start.
            // "Present" means NON-WHITESPACE: TryParseTeIgnore reports whitespace as valid-nothing (set=null), so a
            // whitespace-only preferred would otherwise take precedence, shadow a VALID legacy set, and REMOVE it on
            // the consolidation below — destroying real ignore ids. Treat whitespace preferred as ABSENT here (the
            // display path keeps whitespace=valid-nothing).
            var set = !string.IsNullOrWhiteSpace(prefRaw) && prefOk ? (prefSet ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    : legOk && legSet != null ? legSet
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ignored) set.Add(ruleId); else set.Remove(ruleId);
            // Always retire the legacy-typo key (TE's historical "BestPractizeAnalyzer_IgnoreRules"): we consolidate to
            // the correct key, so a value read from the legacy key isn't left stale (which would keep the rule ignored).
            // Safe to remove HERE: a corrupt legacy was preserved above, a valid one was folded into the set.
            if (ao.HasAnnotation(TeIgnoreLegacy)) ao.RemoveAnnotation(TeIgnoreLegacy);
            if (set.Count == 0) { if (ao.HasAnnotation(TeIgnore)) ao.RemoveAnnotation(TeIgnore); }
            else ao.SetAnnotation(TeIgnore, JsonSerializer.Serialize(new { RuleIDs = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() }));
        }
    }
}
