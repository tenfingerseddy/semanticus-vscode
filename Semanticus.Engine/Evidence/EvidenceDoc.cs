using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.Evidence
{
    /// <summary>
    /// The five-word verdict vocabulary of the complexity doctrine (Rule 3). This library speaks ONLY these
    /// five words. The Tests-tab engines emit their own domain verdicts (Pass / Fail / Suspect / NotVerifiable);
    /// mapping those onto these five is the CONSUMER's job, not this library's, so an evidence document never
    /// carries a domain verdict.
    /// </summary>
    public enum Verdict
    {
        // Unknown is value ZERO on purpose. A deserialized doc/finding/probe/step with no verdict field must NOT
        // default to green - absent means "we do not know", never "verified". Every honest default lands here.
        Unknown,      // could not be checked (never silently promoted to green)
        Verified,     // proven true by a deterministic check
        NeedsReview,  // a human should look before this is trusted
        Broken,       // proven wrong
        Overridden,   // a human accepted it against the evidence, on the record, with a reason
    }

    /// <summary>Thrown when an <see cref="EvidenceDoc"/> is structurally invalid (see <see cref="EvidenceDoc.Validate"/>).</summary>
    public sealed class EvidenceValidationException : Exception
    {
        public EvidenceValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// How many of the things this document set out to prove were actually proven. The Grade+Coverage pairing is
    /// STRUCTURAL: a verdict with no coverage is not allowed to render a badge, because a green word with nothing
    /// behind it is exactly the false comfort the doctrine forbids. See <see cref="EvidenceDoc.Coverage"/>.
    /// </summary>
    public sealed class Coverage
    {
        public int Verified { get; set; }
        public int Total { get; set; }
        public int Unknowns { get; set; }
    }

    /// <summary>
    /// ONE artifact format for everything the product proves: test-suite reports, workflow evidence packs and
    /// certificates, change-plan reviews, deploy records, safe-rename certificates, the review bundle. There is
    /// exactly one renderer (<see cref="EvidenceRenderer"/>) and one writer (<see cref="EvidenceStore"/>) for it.
    /// Integrity is a content hash over the canonical JSON (the field excludes itself), optionally chained to the
    /// model's existing audit head via <see cref="PrevHash"/> so a certificate ties into the same trail the
    /// Verified Edits store already keeps. The hashing idiom is REUSED from that store, never reinvented.
    /// </summary>
    // Unknown-member rejection is enforced GLOBALLY via EvidenceHash.SerializerOptions.UnmappedMemberHandling,
    // never per-type: a root-only attribute here once left every NESTED object open to silently-dropped junk.
    public sealed class EvidenceDoc
    {
        // ---- identity ----
        public string Id { get; set; }                 // stable id (a guid string)
        public string Kind { get; set; }               // kebab: "test-suite" | "workflow-run" | "change-plan" | "deploy" | ...
        public string Title { get; set; }
        public string CreatedUtc { get; set; }         // ISO-8601 UTC, stamped by the producer (Render adds no clock)
        public string Producer { get; set; }           // op or workflow name that produced it
        public string ProducerVersion { get; set; }    // engine version string
        public string Origin { get; set; }             // "human" | "agent" | "system"

        // ---- model anchors ----
        public string ModelName { get; set; }
        public string ModelFingerprint { get; set; }   // nullable
        public string BaseCommit { get; set; }         // nullable - the audit anchor
        public string SessionId { get; set; }          // nullable
        public long? Revision { get; set; }            // nullable

        // ---- verdict (the Rule-3 word) ----
        // NULLABLE on purpose: a graded record must state its verdict EXPLICITLY. A missing verdict is not a
        // silent green - Validate requires it present whenever the doc carries coverage or verdict-bearing sections.
        public Verdict? Verdict { get; set; }
        public Dictionary<string, int> VerdictCounts { get; set; }  // word -> count (keys are the five words)
        public Coverage Coverage { get; set; }         // nullable; absent = the renderer shows no badge
        public string OverrideReason { get; set; }     // required iff Verdict == Overridden (see Validate)

        // ---- body ----
        public List<EvidenceSection> Sections { get; set; } = new List<EvidenceSection>();

        // ---- integrity ----
        public string PrevHash { get; set; }           // nullable; chains to the audit head when produced in-session
        public string ContentHash { get; set; }        // SHA-256 over canonical JSON with this field excluded

        /// <summary>
        /// The structural rules the doctrine makes non-negotiable. Called by <see cref="EvidenceStore.Write"/> AND
        /// by <see cref="EvidenceRenderer.Render"/> before anything is persisted or drawn; a consumer may call it
        /// earlier. This is RECURSIVE - it checks identity, the grade+coverage pairing, the coverage/counts
        /// invariants, the override biconditional, enum definedness, and every section's rows - so a document that
        /// passes can never render a chip without coverage, an overridden badge without a reason, or a count that
        /// contradicts its own coverage.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Id)) throw new EvidenceValidationException("an evidence record needs an id");
            if (string.IsNullOrWhiteSpace(Kind)) throw new EvidenceValidationException("an evidence record needs a kind");
            if (string.IsNullOrWhiteSpace(Title)) throw new EvidenceValidationException("an evidence record needs a title");

            // The Grade+Coverage pairing is biconditional and it also binds the verdict-bearing sections. If the doc
            // carries a verdict, a coverage block, OR any section that would render a verdict chip, then BOTH the
            // doc-level verdict and coverage must be present. A chip therefore never appears without coverage, and a
            // coverage block never appears without a verdict to grade it. Absent-everywhere = an ungraded record.
            var graded = Verdict.HasValue || Coverage != null || CarriesVerdicts();
            if (graded)
            {
                if (!Verdict.HasValue)
                    throw new EvidenceValidationException("a graded record must state a verdict; absent is not verified");
                if (Coverage == null)
                    throw new EvidenceValidationException("a verdict owes coverage: a graded record must state what it measured");
            }

            if (Verdict.HasValue && !Enum.IsDefined(typeof(Verdict), Verdict.Value))
                throw new EvidenceValidationException("the record carries an unknown verdict value");

            // The Overridden rule is a biconditional: an override reason exists if and ONLY if the verdict is
            // Overridden, so a reason can neither be missing where it is owed nor present where it means nothing.
            var hasReason = !string.IsNullOrWhiteSpace(OverrideReason);
            var isOverridden = Verdict == Evidence.Verdict.Overridden;
            if (isOverridden && !hasReason)
                throw new EvidenceValidationException("an overridden verdict requires an override reason on the record");
            if (!isOverridden && hasReason)
                throw new EvidenceValidationException("an override reason is only valid when the verdict is overridden");

            ValidateCoverageAndCounts();

            if (Sections != null)
                foreach (var s in Sections)
                    ValidateSection(s);
        }

        /// <summary>True when a section would render a verdict chip, so coverage is owed. A findings section ALWAYS
        /// renders a rollup chip (even when empty), so its mere presence counts; probe/step sections render one chip
        /// per row, so only a non-empty one counts.</summary>
        private bool CarriesVerdicts()
        {
            if (Sections == null) return false;
            foreach (var s in Sections)
            {
                switch (s)
                {
                    case FindingsSection: return true;
                    case ProbeSection p when p.Probes != null && p.Probes.Count > 0: return true;
                    case StepsSection st when st.Steps != null && st.Steps.Count > 0: return true;
                }
            }
            return false;
        }

        // Coverage and VerdictCounts must not contradict each other, or the honesty line and the badge lie. All
        // counts are nonnegative; count keys come ONLY from the five-word vocabulary; the coverage verified/unknown
        // tallies equal their counterparts in the counts when those keys are present; and total never claims fewer
        // items than the coverage or the counts already account for. NOTE (coordinator carve-out): section ROWS are
        // NOT required to sum to these counts - a section may legitimately show only a subset of the graded set.
        private void ValidateCoverageAndCounts()
        {
            // All bound arithmetic is done in LONG. Two int tallies near int.MaxValue would wrap negative under
            // int addition and slip PAST these comparisons (Verified=int.MaxValue + Unknowns=1 wraps to a huge
            // negative, so any Total "exceeds" it) - exactly the contradiction this method exists to reject.
            if (Coverage != null)
            {
                if (Coverage.Verified < 0 || Coverage.Total < 0 || Coverage.Unknowns < 0)
                    throw new EvidenceValidationException("coverage counts cannot be negative");
                if (Coverage.Total < (long)Coverage.Verified + Coverage.Unknowns)
                    throw new EvidenceValidationException("coverage total cannot be smaller than the verified plus unknown items it already names");
            }

            if (VerdictCounts != null)
            {
                long sum = 0;
                foreach (var kv in VerdictCounts)
                {
                    if (!Verdicts.IsWord(kv.Key))
                        throw new EvidenceValidationException("a verdict count uses a word outside the five-word vocabulary: " + kv.Key);
                    if (kv.Value < 0)
                        throw new EvidenceValidationException("a verdict count cannot be negative");
                    sum += kv.Value;
                }

                if (Coverage != null)
                {
                    if (VerdictCounts.TryGetValue("Verified", out var v) && Coverage.Verified != v)
                        throw new EvidenceValidationException("coverage verified count disagrees with the verified verdict count");
                    if (VerdictCounts.TryGetValue("Unknown", out var u) && Coverage.Unknowns != u)
                        throw new EvidenceValidationException("coverage unknown count disagrees with the unknown verdict count");
                    if (Coverage.Total < sum)
                        throw new EvidenceValidationException("coverage total cannot be smaller than the number of graded items the counts imply");
                }
            }
        }

        private static void ValidateSection(EvidenceSection section)
        {
            switch (section)
            {
                case FindingsSection s when s.Rows != null:
                    foreach (var r in s.Rows) RequireDefined(r.Verdict);
                    break;
                case ProbeSection s when s.Probes != null:
                    foreach (var p in s.Probes) RequireDefined(p.Verdict);
                    break;
                case StepsSection s when s.Steps != null:
                    foreach (var st in s.Steps) RequireDefined(st.Verdict);
                    break;
                case NoteSection n:
                    // Tone is enum-like, NOT free text: it is interpolated toward class/attribute context in the
                    // renderer, so it is pinned to the closed vocabulary here (absent defaults to "info"). The
                    // renderer then only ever sources the emitted attribute from this validated set.
                    if (!string.IsNullOrEmpty(n.Tone) && n.Tone != "info" && n.Tone != "warning")
                        throw new EvidenceValidationException("a note tone must be exactly \"info\" or \"warning\"");
                    break;
            }
        }

        private static void RequireDefined(Verdict v)
        {
            if (!Enum.IsDefined(typeof(Verdict), v))
                throw new EvidenceValidationException("a section row carries an unknown verdict value");
        }
    }

    /// <summary>Base class for the typed sections. The <c>$type</c> discriminator selects the concrete type in JSON.
    /// The default "$type" name is deliberate: it starts with '$', which sorts before every letter and digit, so the
    /// discriminator stays FIRST after the canonicalizer sorts keys. That matters because .NET 8 polymorphic
    /// deserialization requires the discriminator to be the first property; a custom lower-case name (e.g. "kind")
    /// would be sorted into the middle of a section object and make the canonical JSON un-round-trippable.</summary>
    [JsonPolymorphic]
    [JsonDerivedType(typeof(SummarySection), "summary")]
    [JsonDerivedType(typeof(KeyValueSection), "keyValue")]
    [JsonDerivedType(typeof(FindingsSection), "findings")]
    [JsonDerivedType(typeof(DiffSection), "diff")]
    [JsonDerivedType(typeof(ProbeSection), "probe")]
    [JsonDerivedType(typeof(StepsSection), "steps")]
    [JsonDerivedType(typeof(NoteSection), "note")]
    public abstract class EvidenceSection
    {
        public string Title { get; set; }
    }

    /// <summary>Prose. One or more paragraphs.</summary>
    public sealed class SummarySection : EvidenceSection
    {
        public List<string> Paragraphs { get; set; } = new List<string>();
    }

    public sealed class KeyValuePairRow
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>A labelled table of facts (key on the left, value on the right).</summary>
    public sealed class KeyValueSection : EvidenceSection
    {
        public List<KeyValuePairRow> Pairs { get; set; } = new List<KeyValuePairRow>();
    }

    public sealed class FindingRow
    {
        public string Name { get; set; }
        public Verdict Verdict { get; set; }
        public string Detail { get; set; }
        public int? Count { get; set; }
    }

    /// <summary>A list of graded findings. The section's rollup verdict is the worst verdict among its rows.</summary>
    public sealed class FindingsSection : EvidenceSection
    {
        public List<FindingRow> Rows { get; set; } = new List<FindingRow>();

        /// <summary>The worst verdict present, in severity order Broken &gt; NeedsReview &gt; Unknown &gt; Overridden &gt; Verified.
        /// An empty section rolls up to Unknown (nothing was actually checked).</summary>
        public Verdict Rollup()
            => Rows == null || Rows.Count == 0 ? Verdict.Unknown : Verdicts.Worst(Rows.Select(r => r.Verdict));
    }

    /// <summary>A before/after of a body of text (DAX, M, TMDL). The language hint is a label only.</summary>
    public sealed class DiffSection : EvidenceSection
    {
        public string Before { get; set; }
        public string After { get; set; }
        public string Language { get; set; }   // hint: "dax" | "m" | "tmdl" | ...
    }

    public sealed class ProbeRow
    {
        public string Query { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public Verdict Verdict { get; set; }
        public double? DurationMs { get; set; }
    }

    /// <summary>Queries run against the model with an expected and actual value each.</summary>
    public sealed class ProbeSection : EvidenceSection
    {
        public List<ProbeRow> Probes { get; set; } = new List<ProbeRow>();
    }

    public sealed class StepRow
    {
        public string Name { get; set; }
        public Verdict Verdict { get; set; }
        public string Note { get; set; }
        public string WhenUtc { get; set; }
    }

    /// <summary>The ordered steps of a workflow or pipeline, each graded.</summary>
    public sealed class StepsSection : EvidenceSection
    {
        public List<StepRow> Steps { get; set; } = new List<StepRow>();
    }

    /// <summary>A single call-out. Tone drives styling only.</summary>
    public sealed class NoteSection : EvidenceSection
    {
        public string Text { get; set; }
        public string Tone { get; set; }   // "info" | "warning"
    }

    /// <summary>Display + ordering helpers for the five-word vocabulary. The one place the words become English.</summary>
    public static class Verdicts
    {
        /// <summary>The five words as their stable token form (matches the enum member names).</summary>
        public static readonly string[] Words = { "Verified", "NeedsReview", "Broken", "Unknown", "Overridden" };

        private static readonly HashSet<string> WordSet = new HashSet<string>(Words, StringComparer.Ordinal);

        /// <summary>Whether a string is exactly one of the five vocabulary words (ordinal, case-sensitive). Used to
        /// reject verdict-count keys that fall outside the vocabulary.</summary>
        public static bool IsWord(string s) => s != null && WordSet.Contains(s);

        /// <summary>The human label for a verdict. Still within the five-word vocabulary; only NeedsReview gets a space.</summary>
        public static string Label(Verdict v) => v switch
        {
            Verdict.Verified => "Verified",
            Verdict.NeedsReview => "Needs review",
            Verdict.Broken => "Broken",
            Verdict.Unknown => "Unknown",
            Verdict.Overridden => "Overridden",
            _ => "Unknown",
        };

        /// <summary>The css-class suffix for a verdict (lower-case token).</summary>
        public static string Slug(Verdict v) => v switch
        {
            Verdict.Verified => "verified",
            Verdict.NeedsReview => "needsreview",
            Verdict.Broken => "broken",
            Verdict.Unknown => "unknown",
            Verdict.Overridden => "overridden",
            _ => "unknown",
        };

        // Severity for rollups: a proven failure dominates, then the human-attention states, then a clean pass.
        private static int Severity(Verdict v) => v switch
        {
            Verdict.Broken => 4,
            Verdict.NeedsReview => 3,
            Verdict.Unknown => 2,
            Verdict.Overridden => 1,
            Verdict.Verified => 0,
            _ => 2,
        };

        /// <summary>The worst (highest-severity) verdict in a set. Empty defaults to Unknown.</summary>
        public static Verdict Worst(IEnumerable<Verdict> verdicts)
        {
            var worst = Verdict.Verified;
            var any = false;
            foreach (var v in verdicts)
            {
                if (!any || Severity(v) > Severity(worst)) worst = v;
                any = true;
            }
            return any ? worst : Verdict.Unknown;
        }
    }
}
