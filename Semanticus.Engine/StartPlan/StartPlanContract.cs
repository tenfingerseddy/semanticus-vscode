// The start plan's contract (design-spec-first-start-v2.md section 3): the document, the manifest, the line, the
// view both doors render, and the engine verbs. Lane sf-core's FIRST commit, tagged `sf-contract`; every wave-2
// lane starts from it. Bodies are NotImplemented on purpose: this file fixes SHAPES, not behaviour.
//
// WHY ONE FILE, AND WHAT IS LEFT OPEN. The shapes are frozen so lanes can build in parallel without editing each
// other's files. StructureManifest was a partial seam: sf-manifest's real types (StructureManifest.cs) replaced the stubs
// at integration, so StartPlanDoc and StartPlanView are the closed shapes left here. The
// internal seams named in section 3 (PlanLineExecutor.ApplyInBatch, WrapperGate.Run, ISourceFacts and
// SourceSample.ReadAsync, JevRunTarget, StartPlanBuild.Apply, StartPlanProjection.Of) are declared by their own
// lanes, not here, so two lanes never declare one type.
//
// THE ENGINE VERBS LIVE ON IStartPlanEngine, NOT IEngine. IEngine.cs, RemoteEngine.cs and the RPC target are lane
// sf-doors', and editing them here would make every wave-2 lane conflict with it. sf-doors folds this interface
// into IEngine (one line: `IEngine : IStartPlanEngine`) and wires both doors. LocalEngine implements it now
// (LocalEngine.StartPlan.cs). ApproveStartPlanAsync is on both doors since Kane's decision of 10 Oct 2026 (the person's
// word in chat is enough): approve_start_plan and EngineRpcTarget.approveStartPlan. The approval is void the moment the plan changes.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Semanticus.Engine.StartPlan
{
    /// <summary>The state names of the decide loop (Pro). Free stays "off": its words come from its own assistant.</summary>
    public static class StartDecideState
    {
        public const string Off = "off";
        public const string WaitingGrant = "waiting_grant";
        public const string Deciding = "deciding";
        public const string Stable = "stable";
        public const string Incomplete = "incomplete";
    }

    /// <summary>Who decided a line. A person's decision always wins; an assistant line comes from the person's own
    /// Claude (words); jev is the typed decision service; rule is a deterministic starter.</summary>
    public static class StartDecidedBy
    {
        public const string Rule = "rule";
        public const string Jev = "jev";
        public const string Assistant = "assistant";
        public const string Person = "person";
    }

    /// <summary>The ids of the five view sections, in the order the person reads them.</summary>
    public static class StartSectionId
    {
        public const string Tables = "tables";          // Tables and columns
        public const string Links = "links";            // Links and dates
        public const string Measures = "measures";      // Measures
        public const string Words = "words";            // Words for Copilot
        public const string BestPractice = "bestpractice"; // Best practice
        public static readonly string[] All = { Tables, Links, Measures, Words, BestPractice };
    }

    // ---------------------------------------------------------------- the request

    /// <summary>start_model_from_sql: where the SQL source is, and where the new model goes. Fabric SQL only.</summary>
    public sealed class StartModelRequest
    {
        public string Server { get; set; }
        public string Database { get; set; }
        /// <summary>Left out, the engine picks the sign-in that is ready, the way the endpoint listing does (D1); az is the last choice.</summary>
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
        public string StorageMode { get; set; } = "import";
        /// <summary>The folder the model is created in. It must be empty (or not exist yet): a folder holding anything
        /// is refused, so another project is never written (defect 1).</summary>
        public string Folder { get; set; }
        /// <summary>The model name; the model is saved at &lt;Folder&gt;/&lt;Name&gt;.SemanticModel.</summary>
        public string Name { get; set; }
        /// <summary>The PERSON's own confirmation (Studio, after its "discard unsaved changes?" question) that the open
        /// model's unsaved changes may be thrown away. The assistant's door ignores it: an agent start with unsaved work is
        /// always refused. (Added after sf-contract; additive.)</summary>
        public bool DiscardUnsaved { get; set; }
    }

    // ---------------------------------------------------------------- the document

    public sealed class StartDestination
    {
        public string Folder { get; set; }
        public string Name { get; set; }
        public string ModelPath { get; set; }
    }

    public sealed class StartSource
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
        public string StorageMode { get; set; }
    }

    /// <summary>What the person has let Jev see (S2): the source, the tables, and whether sample facts are on.</summary>
    public sealed class StartDisclosure
    {
        public string SourceKey { get; set; }
        public string[] IncludedTables { get; set; } = Array.Empty<string>();
        public bool SampleFacts { get; set; }
        /// <summary>The included tables whose sample may go to Jev ("schema.table"). Null on a record made before this field existed, which reads as every included table
        /// when <see cref="SampleFacts"/> is on. A table the person refused the sample for is in <see cref="IncludedTables"/> (Jev sees its names and settings) and not here.</summary>
        public string[] SampleTables { get; set; }
        public DateTime? Utc { get; set; }
    }

    /// <summary>The approval of ONE exact plan (S1), by the person or by an assistant on the person's word. Void the moment the digest moves.</summary>
    public sealed class StartApproval
    {
        public string Digest { get; set; }
        public DateTime? Utc { get; set; }
        /// <summary>The origin that approved: "agent" for an assistant, "human" (or null, on a record made before this field) for the person.</summary>
        public string By { get; set; }
    }

    /// <summary>How far one decision family has got (S11): asked, answered, unusable, remaining.</summary>
    public sealed class StartFamilyProgress
    {
        public string Family { get; set; }
        public int Asked { get; set; }
        public int Answered { get; set; }
        public int Unusable { get; set; }
        public int Remaining { get; set; }
    }

    public sealed class StartDecide
    {
        /// <summary>One of <see cref="StartDecideState"/>.</summary>
        public string State { get; set; } = StartDecideState.Off;
        public int Round { get; set; }
        /// <summary>Plain words for why the loop is waiting, or stopped short (Incomplete never reads as done).</summary>
        public string Reason { get; set; }
        public List<StartFamilyProgress> Progress { get; set; } = new List<StartFamilyProgress>();
    }

    public sealed class StartWords
    {
        public int Total { get; set; }
        public int Open { get; set; }
    }

    /// <summary>What the preview scratch says the built model would look like.</summary>
    public sealed class StartPreview
    {
        public int? AiReadiness { get; set; }
        /// <summary>The AI readiness letter, A to F (ReadinessScorecard.Grade), or null when it could not be read.</summary>
        public string AiReadinessGrade { get; set; }
        /// <summary>The Best practice LETTER, A to F (BpaGrading, BpaScorecard.BestPractice), or null when it could not be read. It was a violation count
        /// until the wiring step: the count includes info notes, which the letter ignores, so it could read worse than the grade a person is shown.</summary>
        public string BestPractice { get; set; }
        /// <summary>How many errors and warnings the same scan leaves open (an accepted finding and an info note are not open), or null when the scan could not be read or the preview
        /// was made before this was kept. The page says this count beside the letter; it never counts the plan's own lines for it.</summary>
        public int? BestPracticeOpen { get; set; }
        /// <summary>The digest of the preview scratch's TMDL minus the TE2 tool stamps. The build must equal it (S7).</summary>
        public string ProjectionDigest { get; set; }
        /// <summary>The plan's own digest (StartPlanViewBuilder.Digest) when this preview was made. A preview whose plan digest is not the plan's now is out of
        /// date: the view says so and a build waits for a new one, so a person never builds against numbers they were not shown.</summary>
        public string PlanDigest { get; set; }
    }

    /// <summary>The sample of the source's rows (K1): where the read stands, in plain words. The rows never leave the engine; Jev and the assistant see derived
    /// facts only.</summary>
    public sealed class StartSampleStatus
    {
        public const string None = "none";
        public const string Reading = "reading";
        public const string Read = "read";
        public const string Refused = "refused";
        /// <summary>One of none | reading | read | refused.</summary>
        public string State { get; set; } = None;
        /// <summary>Plain words for why nothing was read (a refusal, a failed sign-in, a source that is not a Fabric endpoint), or null.</summary>
        public string Reason { get; set; }
        /// <summary>How many source tables the read measured, and how many it could not.</summary>
        public int Measured { get; set; }
        public int NotMeasured { get; set; }
        /// <summary>The fiscal year end the source's dates suggest, month/day (6/30), or null when the dates say a calendar year or nothing. A month number worked out
        /// from the sample, never a date from a row. It becomes a "You" question (fiscal_year) while the plan's own fiscal year end is not set.</summary>
        public string FiscalYearEnd { get; set; }
    }

    // ---------------------------------------------------------------- the manifest (what the model's structure will be)
    // StructureManifest and its parts (ManifestTable, ManifestColumn, ManifestLink, ManifestDateTable, ManifestBaseMeasure,
    // ManifestTimeIntelligence) live in StructureManifest.cs, in this namespace. The stubs that stood here until the
    // sf-manifest merge were folded into those real types (every member the contract promised is kept there).

    // ---------------------------------------------------------------- lines

    /// <summary>One kept-or-dropped change, stored against manifest identities (refs refreshed by the executor, S5).</summary>
    public sealed class StartLine
    {
        public ChangeItem Item { get; set; }
        /// <summary>One of <see cref="StartDecidedBy"/>.</summary>
        public string DecidedBy { get; set; }
        /// <summary>The Jev decision point (family question) that produced it, if any.</summary>
        public string Point { get; set; }
        /// <summary>The assistant's words, when this line carries them.</summary>
        public string Word { get; set; }
        /// <summary>The decide round that last set it.</summary>
        public int Round { get; set; }
        public bool Kept { get; set; } = true;
        /// <summary>Plain words for why the line exists (or why it was dropped).</summary>
        public string Why { get; set; }
        /// <summary>Set while the line is HELD: a line the person or their assistant made (words, a pick, a pin, a keep or a drop) whose object is out of the plan
        /// right now because a table, a column, a measure, a link or a pick was left out or replaced. A held line stays on the plan and is shown, is not built,
        /// and comes back with its object. The words say why. Null when the line stands. (Added by the wiring step; additive.)</summary>
        public string Held { get; set; }
        /// <summary>True on a line the VIEW makes from the manifest and that is never stored: a time measure. Drop and Keep act on the manifest (the time measure is left
        /// out or put back); a formula cannot be pinned on it, so Studio shows no Pin there. Never set on a stored line.</summary>
        public bool Derived { get; set; }
        /// <summary>What the build sets on this line's object besides what its title says, in plain words (the folder a measure is filed in, the number format it
        /// carries). The review shows every property the build writes, so each one is on a line the person reads: the build checks the model it made against these
        /// (StartPlanReview) and takes itself back when it wrote one that is not here. Null when there is nothing to add. Never stored: the view makes it from the manifest.</summary>
        public List<StartSet> Sets { get; set; }
        /// <summary>Plain words for why the name of the table or base measure this line is about would still be flagged by the naming rules, when nobody has kept it.
        /// The person keeps it with <c>write_start_words</c> kind <c>keep_name</c> (Studio's "Keep this name"); a kept name is not flagged any more. Null otherwise.
        /// Never stored: the view makes it. (Join 8, K1.)</summary>
        public string NameFlag { get; set; }
    }

    /// <summary>One property the build sets on a reviewed object, with the plain words the page shows for it (UAT-J7-S2-008).</summary>
    public sealed class StartSet
    {
        /// <summary>The object, as a ref ("measure:Sales/Total", "column:Sales/Id", "table:Date", "link:Sales.DateKey>Date.DateKey").</summary>
        public string Ref { get; set; }
        /// <summary>The TOM property name (DisplayFolder, FormatString, IsHidden, IsKey, SummarizeBy, DataCategory, IsActive).</summary>
        public string Property { get; set; }
        public string Value { get; set; }
        public string Words { get; set; }
    }

    /// <summary>One column of a table in the review: what the build brings in, so the person reads names, not only a count (UAT-J7-S1-004, S2-006).</summary>
    public sealed class StartColumn
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        /// <summary>The name in the source, when the person or a rule changed it.</summary>
        public string SourceName { get; set; }
        public string DataType { get; set; }
        /// <summary>False when the column is left out of the plan (it stays listed so it can be put back).</summary>
        public bool Included { get; set; } = true;
        public bool Hidden { get; set; }
        public bool Key { get; set; }
        /// <summary>The summarize setting when it is not Power BI's own default for the column's type; null otherwise.</summary>
        public string Summarize { get; set; }
        /// <summary>As <see cref="StartLine.NameFlag"/>, for a column of the plan: why its name would still be flagged, when it is brought in and nobody has kept the name.</summary>
        public string NameFlag { get; set; }
    }

    // ---------------------------------------------------------------- the document

    /// <summary>The start plan, kept under the engine's state dir so a restart keeps it. ONE per engine at a time.</summary>
    public sealed class StartPlanDoc
    {
        public string Id { get; set; }
        public int Version { get; set; }
        public StartDestination Destination { get; set; }
        public StartSource Source { get; set; }
        /// <summary>The schema as read (with its constraint groups, once sf-manifest adds them to FabricSchema).</summary>
        public FabricSchema Schema { get; set; }
        public StructureManifest Manifest { get; set; }
        public List<StartLine> Lines { get; set; } = new List<StartLine>();
        /// <summary>The person's pins: a decided value the loop must keep, by item target.</summary>
        public Dictionary<string, string> Pins { get; set; } = new Dictionary<string, string>();
        /// <summary>The person's answers to Needs-you questions, by question id.</summary>
        public Dictionary<string, string> Answers { get; set; } = new Dictionary<string, string>();
        public StartDisclosure Disclosure { get; set; }
        public StartApproval Approval { get; set; }
        public StartDecide Decide { get; set; } = new StartDecide();
        public StartWords Words { get; set; } = new StartWords();
        public StartPreview Preview { get; set; } = new StartPreview();
        /// <summary>True when facts worked out from a sample of the source's rows are on for this plan. It is IN the digest: the person agrees to a plan that
        /// says whether a sample is part of it, and a sample read after they looked moves the digest, so a grant made on the old one is refused as stale.</summary>
        public bool SampleFacts { get; set; }
        /// <summary>The tables ("schema.table", as the source names them) whose sample the person refused: Jev may still see the table's names and settings, never facts worked out
        /// from its rows. In the digest, so refusing or allowing one moves it and a grant made on the old one is refused as stale. Both doors set it (edit_start_plan, set_table_sample).</summary>
        public List<string> SampleRefused { get; set; } = new List<string>();
        /// <summary>Where the sample read stands (reading, read, or refused with its reason).</summary>
        public StartSampleStatus Sample { get; set; } = new StartSampleStatus();
        /// <summary>The person chose to carry on without Jev (Pro): the plan is made the way Free's is, and can be granted later, which clears this.</summary>
        public bool JevDeclined { get; set; }
        /// <summary>Set when the plan was built into its model: the plan is kept (not deleted) so an undo followed by an edit can build again without losing the
        /// words or the approval. Built is worked out live too: a model with no tables is not built, whatever this says.</summary>
        public DateTime? BuiltUtc { get; set; }
        /// <summary>The plan's digest when it was built.</summary>
        public string BuiltDigest { get; set; }
        /// <summary>The revision of the person's empty model when the plan began; the build refuses if it has moved.</summary>
        public long BaselineRevision { get; set; }
        /// <summary>The seed every relationship name and lineage tag is allocated from (S3).</summary>
        public string IdentitySeed { get; set; }
        /// <summary>True while the plan is parked (another model was opened). A parked plan keeps its document.</summary>
        public bool Parked { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    // ---------------------------------------------------------------- the view both doors render

    public sealed class StartCounts
    {
        public int Rule { get; set; }
        public int Jev { get; set; }
        public int Assistant { get; set; }
        public int You { get; set; }
    }

    public sealed class StartGroup
    {
        public string Table { get; set; }
        /// <summary>The source name beside the model name (mockup C), when it differs.</summary>
        public string SourceName { get; set; }
        public List<StartLine> Lines { get; set; } = new List<StartLine>();
        /// <summary>The table's columns, on the Tables and columns group only (null elsewhere). Shown folded under the table's line.</summary>
        public List<StartColumn> Columns { get; set; }
        /// <summary>True when the person refused this table's sample (the Tables and columns group only): Jev sees the table's names and settings but nothing worked out from its rows.</summary>
        public bool SampleOff { get; set; }
    }

    public sealed class StartSection
    {
        /// <summary>One of <see cref="StartSectionId"/>.</summary>
        public string Id { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public StartCounts Counts { get; set; } = new StartCounts();
        public List<StartGroup> Groups { get; set; } = new List<StartGroup>();
    }

    /// <summary>One line in the Needs-you band: the grant, an unsure Jev line, a composite key, the fiscal year
    /// start, an oscillation, a name clash.</summary>
    public sealed class StartNeed
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string[] Options { get; set; } = Array.Empty<string>();
        public string Recommended { get; set; }
    }

    public sealed class StartGrades
    {
        /// <summary>AI readiness now (the empty model has nothing to grade, so null until something is built) and after the build (a number from 0 to 100).</summary>
        public int? Now { get; set; }
        public int? AfterBuild { get; set; }
        /// <summary>The same two as letters A to F, and the Best practice letter after the build (BpaGrading).</summary>
        public string NowGrade { get; set; }
        public string AfterBuildGrade { get; set; }
        public string NowBestPractice { get; set; }
        public string AfterBuildBestPractice { get; set; }
        /// <summary>The open errors and warnings the best practice scan of the preview leaves (see <see cref="StartPreview.BestPracticeOpen"/>), or null when not known.</summary>
        public int? AfterBuildBestPracticeOpen { get; set; }
        /// <summary>True until the decide has settled: "after the build" is what the plan would build right now, and it can still move.</summary>
        public bool AfterBuildProvisional { get; set; }
        /// <summary>True when the plan was built and the build has been undone: the model holds nothing of it now, so "now" is no grade, and the forecast is the plan's.</summary>
        public bool NowUndone { get; set; }
    }

    /// <summary>What both doors render. The Digest covers everything the person approves; any edit moves it.</summary>
    public sealed class StartPlanView
    {
        [Newtonsoft.Json.JsonProperty(Order = -100, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonPropertyOrder(-100)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public LeanResultSummary Summary { get; set; }
        /// <summary>False when no start plan exists; every other field is then empty.</summary>
        public bool Exists { get; set; }
        public string Id { get; set; }
        public StartDestination Destination { get; set; }
        public StartSource Source { get; set; }
        /// <summary>Always five, in <see cref="StartSectionId.All"/> order.</summary>
        public List<StartSection> Sections { get; set; } = new List<StartSection>();
        public List<StartNeed> NeedsYou { get; set; } = new List<StartNeed>();
        public StartDecide Decide { get; set; }
        public StartWords Words { get; set; }
        public StartGrades Grades { get; set; } = new StartGrades();
        public string Digest { get; set; }
        public StartApproval Approval { get; set; }
        public bool CanBuild { get; set; }
        /// <summary>Plain words for why CanBuild is false (null when it is true).</summary>
        public string BlockedBecause { get; set; }
        /// <summary>True when the plan was built and its model still has what it built (an undo makes it false again). The engine answers a read after a
        /// build with this set, never with "no plan".</summary>
        public bool Built { get; set; }
        /// <summary>Whether facts from a sample of rows are on, and where the sample read stands. The digest covers the flag.</summary>
        public bool SampleFacts { get; set; }
        public StartSampleStatus Sample { get; set; }
        /// <summary>True when the person chose to carry on without Jev.</summary>
        public bool JevDeclined { get; set; }
        /// <summary>One plain sentence the person and the assistant must be told about what this call did beside its
        /// answer, today: which open model a start closed to make room. Set on that call's answer only, never stored
        /// in the plan. Null otherwise. (Added by sf-doors after sf-contract; additive.)</summary>
        public string Notice { get; set; }
        /// <summary>True when this call put a question to the person and changed nothing. No start-plan call does now: the assistant's
        /// grant_start_disclosure grants on the person's yes in chat. Kept for the wire shape; always false. Never stored in the plan.</summary>
        public bool Asked { get; set; }
        /// <summary>The lines an edit moved besides the one the person named (dropped with, kept with, removed, or held), each with its plain reason.
        /// Set on that edit's answer only, never stored in the plan. Null when nothing else moved. (Added by sf-decide; additive.)</summary>
        public List<StartKnockOn> KnockOns { get; set; }
        /// <summary>The links the build writes, exactly: the manifest's own list after drops and leave-outs (the list the build's spec is made from), each with whether it is on.
        /// The page draws and counts this list, never the Links lines, which also hold a dropped guess and a link to a table that is left out. Empty when the build writes no link.</summary>
        public List<StartLinkEdge> Links { get; set; } = new List<StartLinkEdge>();
    }

    /// <summary>One link the build writes: the two tables it joins and whether it is on (an off link is written switched off).</summary>
    public sealed class StartLinkEdge
    {
        public string From { get; set; }
        public string To { get; set; }
        public bool Active { get; set; }
        /// <summary>The link line's own id ("relationship:" and its identity), so a drawn link can be found among the lines.</summary>
        public string Id { get; set; }
        /// <summary>Why the link is as it is, in the words of its line.</summary>
        public string Note { get; set; }
    }

    // ---------------------------------------------------------------- verb inputs and results

    /// <summary>One edit to the plan. Follows Needs closure on both doors (S12): dropping a measure drops its followers.</summary>
    public sealed class StartPlanEdit
    {
        /// <summary>include_table | include_column | rename | set_link | set_pick | pin | drop_line | keep_line | answer | set_fiscal_year_end | set_table_sample</summary>
        public string Op { get; set; }
        public string Target { get; set; }
        public string Value { get; set; }
    }

    /// <summary>One piece of words the assistant wrote for an object (becomes an assistant line).</summary>
    public sealed class StartWord
    {
        public string Ref { get; set; }
        /// <summary>description | synonyms | display_name</summary>
        public string Kind { get; set; }
        public string Text { get; set; }
        /// <summary>The words the person was looking at when they started editing (the person's door only; the assistant's is not checked). When the line's words are not
        /// these any more, nothing is written and the person is told, so an edit never covers words written after the person looked (the class of join 6: every save names what it started from).
        /// Null skips the check.</summary>
        public string Expected { get; set; }
    }

    public sealed class StartWordsPage
    {
        public int Total { get; set; }
        public int Offset { get; set; }
        public List<StartWordItem> Items { get; set; } = new List<StartWordItem>();
    }

    public sealed class StartWordItem
    {
        public string Ref { get; set; }
        public string Kind { get; set; }
        public string Context { get; set; }
    }

    public sealed class StartWordsResult
    {
        public int Written { get; set; }
        /// <summary>One entry per refused item, naming the ref and the plain reason.</summary>
        public List<string> Refused { get; set; } = new List<string>();
        public StartPlanView View { get; set; }
    }

    public sealed class StartPlanBuildResult
    {
        public bool Built { get; set; }
        public string Refused { get; set; }
        public long Revision { get; set; }
        public StartPlanView View { get; set; }
    }

    // ---------------------------------------------------------------- the engine verbs

    /// <summary>The start plan's engine verbs. LocalEngine implements it; sf-doors folds it into IEngine and wires
    /// both doors.</summary>
    public interface IStartPlanEngine
    {
        /// <summary>Create the empty model in an empty folder, save it, read the schema, make the plan. Refused on the
        /// agent door when the open model has unsaved work.</summary>
        Task<StartPlanView> StartModelFromSqlAsync(StartModelRequest request, string origin);
        Task<StartPlanView> GetStartPlanAsync();
        Task<StartPlanView> EditStartPlanAsync(StartPlanEdit edit, string origin);
        Task<StartPlanView> GrantStartDisclosureAsync(string digest, string origin);
        Task<StartPlanView> ApproveStartPlanAsync(string digest, string origin = "human");
        Task<StartWordsPage> GetStartWordsAsync(int max, int offset);
        Task<StartWordsResult> WriteStartWordsAsync(StartWord[] words, string origin);
        Task<StartPlanBuildResult> BuildStartPlanAsync(string digest, string origin);
        Task<StartPlanView> DiscardStartPlanAsync(string origin);
    }
}
