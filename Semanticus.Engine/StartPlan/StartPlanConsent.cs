// What the person has let Jev see while a new model is being started from a SQL source (design-spec-first-start-v2.md,
// finding S2, lane sf-consent).
//
// WHY A SECOND PERMISSION. The model's own permission (set_consent, DecisionConsentStore) is given for an empty model,
// and says "this model's shape". A start plan reads a hidden SOURCE: tables, columns and, when a sample is on, facts
// worked out from rows. Permission for the empty model does not tell the person that. So before the first ask the
// person is shown the source, the tables included and whether sample facts are on, and what they agree to is recorded
// here, beside the model's own grant: a DisclosedScope.
//
// WHAT IT BINDS TO. The model's consent scope ("<address>#<marker>", F-177) and that scope's consent GENERATION, plus
// the plan's id. So:
//   - another model at the same path has another marker, finds no record, and is asked again (F-177);
//   - withdrawing the model's permission, or giving it again, moves the generation, and the old disclosure no longer
//     counts (a withdrawal followed by a fresh grant is a different permission);
//   - a different plan (discard, start again) is a different disclosure.
//
// WHAT IS CHECKED, on EVERY ask and not once at the start (the fence below):
//   1. a grant exists and still stands for this model, this generation and this plan;
//   2. the plan as it is NOW discloses nothing the grant did not name: its included tables are a subset of the granted
//      ones, its source is the granted source, and the sample flag is the granted flag. A table added after the grant,
//      or a sample turned on, stops EVERY ask until the person agrees again, not only the asks that mention it;
//   3. the tables THIS ask says it is about are a subset of the granted ones, and its source is the granted one;
//   4. no table the plan leaves out (or the grant never named) appears BY NAME anywhere in the request that would go.
// Check 4 is the one that does not trust the caller: 3 is what the asker declares, 4 reads what would really be sent.
//
// FAILS SHUT, like the consent store: an unreadable or malformed file is no grant.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>What a grant covers: one source, the tables of it Jev may see, and whether facts worked out from a sample
    /// of rows go too. Source names are "schema.table" as the source calls them, so a rename in the model does not
    /// move what was agreed.</summary>
    public sealed class DisclosedScope
    {
        public DisclosedScope(string sourceKey, IEnumerable<string> includedTables, bool sampleFacts, IEnumerable<string> sampleTables = null)
        {
            SourceKey = Norm(sourceKey);
            IncludedTables = (includedTables ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal).ToArray();
            SampleFacts = sampleFacts;
            // The tables whose sample may go: none when no sample is on; every included table when the grant does not say (a record made before the person could choose);
            // else only the ones it names that are also included.
            SampleTables = !sampleFacts ? Array.Empty<string>()
                : sampleTables == null ? IncludedTables
                : sampleTables.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Norm).Distinct(StringComparer.Ordinal).Intersect(IncludedTables, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        }

        public string SourceKey { get; }
        public string[] IncludedTables { get; }
        public bool SampleFacts { get; }
        /// <summary>The included tables whose sample facts may go to Jev (a table the person refused the sample for is in <see cref="IncludedTables"/> and not here).</summary>
        public string[] SampleTables { get; }

        /// <summary>A short fingerprint of the whole scope. It rides the fence, so a change of scope makes an ask in flight stale.</summary>
        public string Digest
        {
            get
            {
                var text = SourceKey + "\n" + string.Join("\n", IncludedTables) + "\n" + (SampleFacts ? "sample" : "no-sample")
                           + (SampleFacts && SampleTables.Length != IncludedTables.Length ? "\n" + string.Join("\n", SampleTables) : "");   // a grant that names every table's sample keeps the digest it always had
                using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Substring(0, 16).ToLowerInvariant();
            }
        }

        /// <summary>Names are compared without case or surrounding space: a source that spells a table twice in two cases is one table to a person.</summary>
        internal static string Norm(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>The scope a plan would disclose right now: its source, the tables its manifest includes, and whether a sample is on.</summary>
        public static DisclosedScope Of(StartPlanDoc doc, bool sampleFacts)
        {
            var refused = new HashSet<string>((doc?.SampleRefused ?? new List<string>()).Select(Norm), StringComparer.Ordinal);
            var included = IncludedSourceTables(doc);
            return new DisclosedScope(SourceKeyOf(doc?.Source), included, sampleFacts, sampleFacts ? included.Where(t => !refused.Contains(Norm(t))) : null);
        }

        public static DisclosedScope Of(StartDisclosure disclosure) =>
            disclosure == null ? null : new DisclosedScope(disclosure.SourceKey, disclosure.IncludedTables, disclosure.SampleFacts, disclosure.SampleTables);

        /// <summary>"server/database", the one name a source is known by in a grant.</summary>
        public static string SourceKeyOf(StartSource source) =>
            source == null ? string.Empty : (source.Server ?? string.Empty).Trim() + "/" + (source.Database ?? string.Empty).Trim();

        /// <summary>Every included table of the manifest, by source name.</summary>
        public static string[] IncludedSourceTables(StartPlanDoc doc) =>
            (doc?.Manifest?.Tables ?? new List<ManifestTable>())
                .Where(t => t != null && t.Include).Select(SourceNameOf).ToArray();

        /// <summary>"schema.table" as the source names it; the model name when the manifest has no source name yet.</summary>
        public static string SourceNameOf(ManifestTable t)
        {
            if (!string.IsNullOrWhiteSpace(t.SourceName))
                return string.IsNullOrWhiteSpace(t.SourceSchema) ? t.SourceName : t.SourceSchema + "." + t.SourceName;
            return t.Name ?? string.Empty;
        }

    }

    /// <summary>What the consent verbs need of the start plan's document. LocalEngine gets one through
    /// <c>StartPlanHost</c>; lane sf-core's store is what stands behind it. Kept this small on purpose: the document's own
    /// storage, locking and digest are not this lane's.
    ///
    /// <see cref="DigestOf"/> is the digest the view shows, and it must NOT cover <see cref="StartPlanDoc.Approval"/>
    /// (an approval that moved the digest could never match it). <see cref="UpdateAsync"/> runs the change under the
    /// document's own lock, saves it, and hands back the fresh view; a change that returns false is not saved.</summary>
    internal interface IStartPlanConsentHost
    {
        /// <summary>The plan's document, or null when there is none. Callers treat it as read only.</summary>
        StartPlanDoc Current();
        string DigestOf(StartPlanDoc doc);
        /// <summary>Whether sample facts are on for this plan right now (a sample was read, and the gate let it).</summary>
        bool SampleFactsOn(StartPlanDoc doc);
        System.Threading.Tasks.Task<StartPlanView> UpdateAsync(Func<StartPlanDoc, bool> change);
    }

    /// <summary>What one ask says it is about. The asker declares it; the request itself is checked as well (check 4).</summary>
    public sealed class StartAskScope
    {
        public string SourceKey { get; set; }
        /// <summary>The source names ("schema.table") of every table whose names or facts the request carries.</summary>
        public string[] Tables { get; set; } = Array.Empty<string>();
        public bool SampleFacts { get; set; }

        /// <summary>Build the scope from the plan's own manifest: model table names (what the scratch model carries) are mapped to
        /// source names, and a name the manifest does not know stays as it is, so it can never be a subset of a grant.</summary>
        public static StartAskScope For(StartPlanDoc doc, IEnumerable<string> modelTableNames, bool sampleFacts)
        {
            var byModelName = (doc?.Manifest?.Tables ?? new List<ManifestTable>())
                .Where(t => t != null && !string.IsNullOrEmpty(t.Name))
                .GroupBy(t => DisclosedScope.Norm(t.Name)).ToDictionary(g => g.Key, g => DisclosedScope.SourceNameOf(g.First()));
            return new StartAskScope
            {
                SourceKey = DisclosedScope.SourceKeyOf(doc?.Source),
                SampleFacts = sampleFacts,
                Tables = (modelTableNames ?? Enumerable.Empty<string>())
                    .Select(n => byModelName.TryGetValue(DisclosedScope.Norm(n), out var source) ? source : n).ToArray(),
            };
        }
    }

    /// <summary>The pure rules. No engine, no files: tests drive them directly, and the engine only gathers their inputs.</summary>
    public static class StartPlanConsent
    {
        public const string NoGrantReason = "you have not yet said which tables Pro may see for this plan";
        public const string GrantWithdrawnReason = "the permission for this plan was taken back, or given again, so it needs your OK again";
        public const string WrongSourceReason = "this question is about a different source than the one you agreed to";

        /// <summary>Why an ask must not go, as a clause the caller finishes ("..., so nothing was sent"), or null when it may. <paramref name="granted"/> is null when no grant stands.</summary>
        public static string WhyNot(DisclosedScope granted, DisclosedScope planNow, StartAskScope ask)
        {
            if (granted == null) return NoGrantReason;

            // 2. The plan as it stands now. One new table, or a sample switched on, stops every ask: the person agreed to a
            // picture that is no longer the plan's, so asking about the part that has not changed would still be acting on it.
            if (!string.Equals(granted.SourceKey, planNow.SourceKey, StringComparison.Ordinal)) return WrongSourceReason;
            var extra = planNow.IncludedTables.Except(granted.IncludedTables, StringComparer.Ordinal).ToArray();
            if (extra.Length > 0) return Quote(extra) + " was added after you agreed, so Pro needs your OK again";
            if (planNow.SampleFacts != granted.SampleFacts)
                return planNow.SampleFacts
                    ? "facts from a sample of rows were switched on after you agreed, so Pro needs your OK again"
                    : "the sample of rows was switched off after you agreed, so Pro needs your OK again";
            var extraSample = planNow.SampleTables.Except(granted.SampleTables, StringComparer.Ordinal).ToArray();
            if (extraSample.Length > 0) return "the sample of " + Quote(extraSample) + " was allowed after you agreed, so Pro needs your OK again";

            // 3. What this ask says it is about.
            if (ask != null)
            {
                if (!string.IsNullOrEmpty(ask.SourceKey) && !string.Equals(DisclosedScope.Norm(ask.SourceKey), granted.SourceKey, StringComparison.Ordinal))
                    return WrongSourceReason;
                var outside = (ask.Tables ?? Array.Empty<string>()).Select(DisclosedScope.Norm).Distinct(StringComparer.Ordinal)
                    .Except(granted.IncludedTables, StringComparer.Ordinal).ToArray();
                if (outside.Length > 0) return "this question is about " + Quote(outside) + ", which you have not let Pro see";
                if (ask.SampleFacts != granted.SampleFacts)
                    return "this question carries sample facts that do not match what you agreed to";
            }
            return null;
        }

        /// <summary>Check 4: the table names the plan leaves out must not appear in the request that would be sent. A name is the
        /// source name ("hr.Payroll"), its table part ("Payroll") or the model's name for it, matched as a whole phrase with
        /// no case. A leftover in a question, an option or any snapshot value counts, because all of it goes.</summary>
        public static string WhyNotRequest(StartPlanDoc doc, DisclosedScope granted, DecisionRequest request)
        {
            if (request == null || doc?.Manifest?.Tables == null) return null;
            var keep = new HashSet<string>(
                granted == null ? Enumerable.Empty<string>() : granted.IncludedTables
                    .Intersect(DisclosedScope.Of(doc, false).IncludedTables, StringComparer.Ordinal), StringComparer.Ordinal);

            var inScope = doc.Manifest.Tables.Where(t => t != null && keep.Contains(DisclosedScope.Norm(DisclosedScope.SourceNameOf(t)))).ToList();
            var keptNames = new HashSet<string>(inScope.SelectMany(NamesOf), StringComparer.Ordinal);

            var text = SendText(request);
            foreach (var table in doc.Manifest.Tables.Where(t => t != null && !keep.Contains(DisclosedScope.Norm(DisclosedScope.SourceNameOf(t)))))
                foreach (var name in NamesOf(table).Where(n => n.Length >= 2 && !keptNames.Contains(n)))
                    if (ContainsPhrase(text, name))
                        return "the question names a table you did not let Pro see (" + (table.Name ?? table.SourceName) + ")";
            return null;
        }

        private static IEnumerable<string> NamesOf(ManifestTable t)
        {
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(t.Name)) names.Add(DisclosedScope.Norm(t.Name));
            if (!string.IsNullOrWhiteSpace(t.SourceName)) names.Add(DisclosedScope.Norm(t.SourceName));
            var full = DisclosedScope.Norm(DisclosedScope.SourceNameOf(t));
            if (full.Length > 0) names.Add(full);
            return names.Distinct(StringComparer.Ordinal);
        }

        /// <summary>Everything that would leave the machine for one request: the snapshot as it goes on the wire, and every
        /// question's name, words and option ids and criteria.</summary>
        internal static string SendText(DecisionRequest request)
        {
            var sb = new StringBuilder();
            sb.Append(DecisionDigest.CanonicalSnapshotJson(request.Snapshot));
            foreach (var q in request.Questions)
            {
                sb.Append('\n').Append(q.Name).Append('\n').Append(q.Instructions);
                foreach (var o in q.Options) sb.Append('\n').Append(o.Id).Append('\n').Append(o.Criterion);
            }
            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>A whole-phrase match: the characters either side must not be letters or digits, so "Log" is not found in
        /// "Catalog" and "Payroll" is found in "[Payroll]" and "hr.Payroll".</summary>
        internal static bool ContainsPhrase(string haystack, string needleLower)
        {
            var from = 0;
            while (from <= haystack.Length - needleLower.Length)
            {
                var at = haystack.IndexOf(needleLower, from, StringComparison.Ordinal);
                if (at < 0) return false;
                var before = at == 0 ? ' ' : haystack[at - 1];
                var afterAt = at + needleLower.Length;
                var after = afterAt >= haystack.Length ? ' ' : haystack[afterAt];
                if (!IsWordChar(before) && !IsWordChar(after)) return true;
                from = at + 1;
            }
            return false;
        }

        // An underscore is part of a word here: "type" is not found in the snapshot's own key "data_type".
        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static string Quote(string[] names) => string.Join(", ", names.Select(n => "\"" + n + "\""));

        /// <summary>The approval stands only for the exact plan it was made on (S1). Null when it stands, else why not.</summary>
        public static string WhyNoApproval(StartPlanDoc doc, string currentDigest, string argumentDigest)
        {
            var approval = doc?.Approval;
            if (approval == null || string.IsNullOrEmpty(approval.Digest)) return "You have not approved this plan yet.";
            if (!string.Equals(approval.Digest, currentDigest, StringComparison.Ordinal)) return "The plan changed after you approved it, so your approval no longer counts. Look at it and approve it again.";
            if (!string.IsNullOrEmpty(argumentDigest) && !string.Equals(approval.Digest, argumentDigest, StringComparison.Ordinal))
                return "That is not the plan you approved. Look at the plan as it is now and approve it again.";
            return null;
        }

        /// <summary>Void an approval whose digest is no longer the plan's. Called by whatever changes the plan, after the change.</summary>
        public static bool ClearApprovalIfMoved(StartPlanDoc doc, string currentDigest)
        {
            if (doc?.Approval == null) return false;
            if (string.Equals(doc.Approval.Digest, currentDigest, StringComparison.Ordinal)) return false;
            doc.Approval = null;
            return true;
        }
    }

    /// <summary>The kept record of what the person agreed Pro may see, per model scope. In the per-user home beside the model's own
    /// permission, never in the model files. One record per model address, like the consent store: an older witness at the same
    /// address is swept, so a restore or an undo cannot walk back into a grant nobody remembers.</summary>
    public static class StartDisclosureStore
    {
        private const string FileName = "start-disclosure.json";
        private const int FileVersion = 1;
        private const char WitnessMark = '#';
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public sealed class Record
        {
            public string planId { get; set; }
            public string sourceKey { get; set; }
            public List<string> includedTables { get; set; }
            public bool sampleFacts { get; set; }
            /// <summary>The included tables whose sample may go to Jev. Null in a record made before the person could refuse one table's sample: every included table.</summary>
            public List<string> sampleTables { get; set; }
            public DateTime grantedUtc { get; set; }
            /// <summary>The generation of the MODEL's permission this was given under. Moving it (withdraw, give again) ends this grant.</summary>
            public long consentGeneration { get; set; }
        }

        private sealed class Store
        {
            public int version { get; set; }
            public Dictionary<string, Record> scopes { get; set; }
        }

        internal static string FilePath() => Path.Combine(ConnectionRegistry.Root(), FileName);

        /// <summary>The grant for this model scope, plan and consent generation, or null. Anything unreadable, another model's
        /// grant, another plan's, and an earlier generation all read as null.</summary>
        public static DisclosedScope Read(string scope, string planId, long consentGeneration)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(planId)) return null;
            lock (Gate)
            {
                try
                {
                    var store = Load();
                    if (!store.scopes.TryGetValue(scope, out var r) || r == null) return null;
                    if (!string.Equals(r.planId, planId, StringComparison.Ordinal) || r.consentGeneration != consentGeneration) return null;
                    return new DisclosedScope(r.sourceKey, r.includedTables, r.sampleFacts, r.sampleTables);
                }
                catch { return null; }
            }
        }

        public static void Grant(string scope, string planId, DisclosedScope granted, long consentGeneration, DateTime utc)
        {
            if (string.IsNullOrEmpty(scope)) throw new ArgumentException("A grant needs the model's scope.", nameof(scope));
            lock (Gate)
            {
                Store store;
                try { store = Load(); } catch { store = Fresh(); }
                var identity = IdentityOf(scope);
                foreach (var k in store.scopes.Keys.Where(k => k != scope && IdentityOf(k) == identity).ToList()) store.scopes.Remove(k);
                store.scopes[scope] = new Record
                {
                    planId = planId, sourceKey = granted.SourceKey, includedTables = granted.IncludedTables.ToList(),
                    sampleFacts = granted.SampleFacts, sampleTables = granted.SampleTables.ToList(), grantedUtc = utc, consentGeneration = consentGeneration,
                };
                Save(store);
            }
        }

        /// <summary>Forget the grant for one model scope (withdrawal, or the plan was discarded).</summary>
        public static void Clear(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            lock (Gate)
            {
                try
                {
                    var store = Load();
                    if (store.scopes.Remove(scope)) Save(store);
                }
                catch { /* an unreadable file already reads as no grant */ }
            }
        }

        private static string IdentityOf(string scope)
        {
            var mark = scope.IndexOf(WitnessMark);
            return mark < 0 ? scope : scope.Substring(0, mark);
        }

        private static Store Fresh() => new Store { version = FileVersion, scopes = new Dictionary<string, Record>(StringComparer.Ordinal) };

        private static Store Load()
        {
            var path = FilePath();
            if (!File.Exists(path)) return Fresh();
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path), Json);
            return store == null || store.version != FileVersion || store.scopes == null ? Fresh() : store;
        }

        private static void Save(Store store)
        {
            Directory.CreateDirectory(ConnectionRegistry.Root());
            var path = FilePath();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
