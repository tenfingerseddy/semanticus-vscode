// The only part of the plan that is remembered.
//
// The gaps are recomputed on every call and are never written down (docs/redesign/jev-plan.md section 2a:
// "gaps are recomputed, never stored"). What cannot be recomputed is what a PERSON decided: which items they
// set aside and why. That is kept here, beside the model's other settings, in the same shape and with the
// same write discipline as decision-settings.json (LocalEngine.Decisions.cs): lock, write a temp file, move
// over the old one.
//
// The history is kept here too, and it is a log rather than a derivation for one honest reason: "what
// changed since last time" cannot be computed from the model alone, because the model does not remember what
// the plan said an hour ago. Two hundred entries, oldest dropped.
//
// AN OLD ENTRY IS READ, NOT DISCARDED. Entries written before the structured shape carry a sentence and
// nothing else. They are read as kind "redrawn" by actor "engine", which is exactly what every one of them
// was: the only thing that wrote history then was the plan composing itself. Nothing is invented for the
// fields those entries cannot have (no item, no revision, no detail), and they come back empty rather than
// filled with a plausible value.
//
// Reading fails open. A missing, unreadable or malformed file reads as no skips and no history, because a
// broken settings file must never be the reason a person cannot see their plan. The cost is honest and
// small: a corrupt file loses the skips, and the items come back with their reasons gone. The alternative,
// refusing to show the plan, is worse.
//
// ONE FILE, ONE ENTRY PER MODEL. The sidecar belongs to a FOLDER, not to a model, so two models opened from
// one directory (and every model opened against one workspace) share this file. The entries therefore sit
// under the model's own durable identity, the same "disk:<hash>" / "live:<endpoint|database>" identity a
// saved check is bound to (LocalEngine.Interview.cs, PaneIdentity). Without that key, skipping an item in
// A.bim also skipped it in B.bim, and B inherited A's completions and refresh log (Astra 10 on PR 385).
//
// A model with NO durable identity - never saved, not live - has nothing to key on, so nothing is
// remembered for it and the caller is told, exactly as it already is when there is no sidecar at all. That
// is the honest answer: the alternative is one shared bucket, which is the bug this key exists to fix.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Semanticus.Engine
{
    /// <summary>One item a person set aside, and why.</summary>
    public sealed class PlanSkip
    {
        public string ItemId { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset When { get; set; }

        /// <summary>The object ref the item id ends with ("table:dim_product" in "jev.column-summarize.table:dim_product"), and
        /// that object's lineage tag, when it had both at the skip. A rename moves the row to a new id, and these let the skip
        /// move with it (hero15 H13), so a row set aside does not come back because its object got a better name.</summary>
        public string Ref { get; set; }
        public string Tag { get; set; }
    }

    /// <summary>One item whose gap has gone. Remembered rather than recomputed, and that is NOT a contradiction
    /// of "gaps are recomputed, never stored": a gap that is gone leaves nothing in the model to recompute from,
    /// so a finished item can only be known by having watched it go. It is recorded once, at the refresh where
    /// the gap vanished, and never written again.</summary>
    public sealed class PlanDone
    {
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string Section { get; set; }
        public DateTimeOffset When { get; set; }

        /// <summary>Who the row's doer was when it was last seen open (lane E5), so the strip counts a finished row
        /// on its own track. Null on a record written before this field existed; such a row reads as Semanticus's,
        /// as every done row did before.</summary>
        public string Doer { get; set; }

        /// <summary>Set when the row's gap closed by another change and not by its own work ("Fix 7 best-practice issues closed
        /// this."), so the row never claims its owner did it. Null on a row that did its own work and on an older record.</summary>
        public string ClosedWhy { get; set; }
    }

    /// <summary>An item the plan carried last time, kept so the next refresh can name what went away using the
    /// words the person actually read.</summary>
    public sealed class PlanSeenItem
    {
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string Section { get; set; }

        /// <summary>The row's doer at that refresh; carried onto the done record when the row goes (lane E5).</summary>
        public string Doer { get; set; }

        /// <summary>When the plan last wrote this row down as open (carried unchanged while the plan did not move, so a read still
        /// writes nothing). The assistant's edits made since are the ones its row may be credited with. Null on an older file.</summary>
        public DateTimeOffset? SeenUtc { get; set; }

        /// <summary>The "rule|object" keys of the findings the row held when it was last written down (ModelPlanItem.Claims). What lets the
        /// plan tell a row the person declined (every one of them now waived) from a row that was worked (any one of them gone). Empty on an
        /// older file or a row built from no findings.</summary>
        public string[] Claims { get; set; } = Array.Empty<string>();
    }

    /// <summary>A session, and how many saves that wrote edits it had made at one moment. How "Save the changes you have
    /// made" tells a save from a discard across a restart (LocalEngine.Plan.cs, SavedSinceUnsavedRowSeen).</summary>
    public sealed class PlanSaveMark
    {
        public string Session { get; set; }
        public int Saves { get; set; }
        public string By { get; set; }
    }

    /// <summary>What is remembered about ONE item's change set: the header, never the live lines.
    ///
    /// THE LINES ARE NOT STORED AND THE DIGEST IS NOT TRUSTED. The lines live on the session's change plan
    /// and are read fresh on every call, and the digest is recomputed over them every time
    /// (LocalEngine.PlanChanges.cs). Storing a digest and believing it is the one thing the change contract
    /// exists to prevent: a line somebody re-authored since the proposal must change the digest, so that an
    /// approve quoting the old one is refused. What has to be remembered is only what cannot be recomputed:
    /// which revision and which source version the lines were worked out against, which session worked them
    /// out, and whether the set has already been applied.
    ///
    /// <see cref="Lines"/> is the one exception and it is a SNAPSHOT FOR READING, never for applying. When
    /// the session that proposed a set is gone, the lines cannot be read back, and showing a person an empty
    /// row where their proposal was is worse than showing them what it said and that it is out of date.
    /// Nothing is ever applied from it: a set whose session is gone is refused before the digest is even
    /// looked at.</summary>
    public sealed class PlanChangeHeader
    {
        public string JobId { get; set; }
        public string SessionId { get; set; }

        /// <summary>The model this set was worked out against, as the engine's durable model identity. The
        /// sidecar is already keyed by it, so this is belt and braces there; it earns its place by being
        /// hashed into the digest.</summary>
        public string ModelIdentity { get; set; }

        /// <summary>One of <see cref="ModelPlanChangeStates"/>.</summary>
        public string State { get; set; }

        /// <summary>The model revision the lines were worked out against.</summary>
        public long Revision { get; set; }

        /// <summary>The source version they were worked out against. Empty when there is no source to
        /// version, which is not the same as unknown and is not treated as one.</summary>
        public string SourceVersion { get; set; }

        /// <summary>True when the versions above were already not current at the moment of the proposal.</summary>
        public bool StaleAtProposal { get; set; }

        public DateTimeOffset ProposedAt { get; set; }
        public DateTimeOffset? AppliedAt { get; set; }

        /// <summary>The digest that was approved when this set was applied. Remembered so that approving the
        /// same digest twice is answered "it is already in" rather than "that was never proposed", which is
        /// the honest answer to a double submit and the first check the contract makes.</summary>
        public string AppliedDigest { get; set; }

        /// <summary>What an applied set can be put back from: today an undo marker, because approve's
        /// promise is the session undo timeline (LocalEngine.PlanChanges.cs, CheckpointPromise).</summary>
        public string Checkpoint { get; set; }

        /// <summary>What the proposal looked at and left out, with the reason.</summary>
        public ModelPlanChangeRefusal[] Refused { get; set; } = Array.Empty<ModelPlanChangeRefusal>();

        /// <summary>The line that could not be made when this set rolled back (its stable line id and value) and why,
        /// so a Jev row is rebuilt without that one line instead of being worked out again from nothing.</summary>
        public string FailedLineId { get; set; }
        public string FailedAfter { get; set; }
        public string FailureReason { get; set; }

        /// <summary>The lines as they were at the proposal. FOR READING ONLY; see the note on this class.</summary>
        public ModelPlanChangeLine[] Lines { get; set; } = Array.Empty<ModelPlanChangeLine>();
    }

    /// <summary>The four places an asked item can be (contract 2.2). "claimed" is written by the queue (lane E3).</summary>
    public static class PlanTaskStates
    {
        public const string Open = "open";
        public const string Claimed = "claimed";
        public const string Done = "done";
        public const string SetAside = "set_aside";

        public static bool IsClosed(string state) => state == Done || state == SetAside;
    }

    /// <summary>Who holds a task, as stored. The token is only ever kept as a SHA-256 hash, and the claim
    /// belongs to one engine run: a claim from a run that has ended is released on read (lane E3), and one from
    /// another live engine on the same model is left alone (EngineRunLiveness).</summary>
    public sealed class PlanTaskClaim
    {
        public string ClaimedBy { get; set; }
        public DateTimeOffset ClaimedUtc { get; set; }
        public string ClaimTokenHash { get; set; }
        public string EngineRunId { get; set; }

        /// <summary>Which reader holds it: "mcp" for the MCP door in the engine's own process, a pipe connection's id
        /// for an attached assistant (lane E3). Meaningful only within the engine run that made the claim.</summary>
        public string Reader { get; set; }
    }

    /// <summary>One asked item: typed in the Plan's field, sent from a page, a migrated aim, or a repeat's
    /// occurrence. STORED, unlike a found row, because nothing in the model can recompute a person's ask.
    /// It lives in the Plan's own file whether or not any assistant is attached (contract 2.2).</summary>
    public sealed class PlanTask
    {
        /// <summary>"pt-" and 8 lowercase hex from a cryptographic source, so a restart can never mint an id an
        /// earlier task already had (the counters behind wfr-N and ar-N restart at 1).</summary>
        public string Id { get; set; }

        /// <summary>The title, the person's own words, 280 characters or fewer.</summary>
        public string Words { get; set; }

        /// <summary>The person's whole text, when it was longer than a title (UAT S12-1-005). The title is its start.
        /// Null when the words fit the title.</summary>
        public string FullText { get; set; }

        /// <summary>One of <see cref="PlanSources"/>: words, you, or repeat.</summary>
        public string Source { get; set; }
        public string SourcePage { get; set; }

        /// <summary>The page the Plan's field showed for these words when Enter added them (PlanTaskRequest.FieldPage).
        /// The row names it, so the field and the row agree (verify-askdata-r6 F1). Null on every other task, and on
        /// every task kept before it existed.</summary>
        public string FieldPage { get; set; }

        /// <summary>The engine-built prompt a Send carried, 4000 characters or fewer.</summary>
        public string Brief { get; set; }

        /// <summary>One of <see cref="PlanDoers"/>.</summary>
        public string Doer { get; set; }
        public string[] Objects { get; set; } = Array.Empty<string>();

        /// <summary>The findings (<c>rule|object</c>) or objects (a ref) the row this task came from listed when it was handed over (PlanTaskRequest.Claims).
        /// The task is done when they are gone, and a finding that appears later is not the task's. Empty on a task that came from nowhere in particular.</summary>
        public string[] Claims { get; set; } = Array.Empty<string>();

        /// <summary>For a task given from a found row that lists objects (the words row): the objects the row listed at that moment. The row stays
        /// hidden behind this task only while it lists nothing else; an object that joins it later shows the row again (D1).</summary>
        public string[] Carried { get; set; } = Array.Empty<string>();

        /// <summary>One of <see cref="PlanDoneWhen"/>.</summary>
        public string DoneWhen { get; set; }
        public PlanOriginLink OriginLink { get; set; }
        /// <summary>For a has_words task: where the words go when it is not the objects' Description (PlanTaskRequest.Slot).</summary>
        public string Slot { get; set; }
        public string Note { get; set; }
        /// <summary>The assistant's own note (set_plan_task_note from the agent door): its reason for leaving the row. It leads the row's state note
        /// and never replaces the person's <see cref="Note"/>; both are kept.</summary>
        public string AssistantNote { get; set; }
        public string Reply { get; set; }
        public string DeclineReason { get; set; }
        /// <summary>Who handed it back: <see cref="PlanDoers.You"/> for the person's Could not; null for the assistant's decline_request.</summary>
        public string DeclinedBy { get; set; }

        /// <summary>The honest line after an engine restart released an assistant's claim on this task (lane E3).
        /// Cleared when it is claimed again.</summary>
        public string ReleaseNote { get; set; }

        /// <summary>Place among the assistant's tasks, 1 first. Null unless the doer is the assistant.</summary>
        public int? Order { get; set; }
        public bool Held { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }

        /// <summary>One of <see cref="PlanTaskStates"/>.</summary>
        public string State { get; set; } = PlanTaskStates.Open;
        public PlanTaskClaim Claim { get; set; }
        public DateTimeOffset? DoneUtc { get; set; }
        public string DoneBy { get; set; }

        /// <summary>Set while History has taken back the edit that closed this task: it is open again, and this keeps
        /// how it was closed so a redo of that same edit closes it again. Null otherwise (UAT S01-j2-007).</summary>
        public PlanTaskTakenBack TakenBack { get; set; }

        /// <summary>Kept only on a migrated or newly typed AIM, so the goal filter "Your aims" still files it.
        /// Null on every other task.</summary>
        public string Section { get; set; }

        /// <summary>The repeat this is an occurrence of (lane RP), or null.</summary>
        public string RepeatId { get; set; }

        /// <summary>The objects this task named that were in the model when it was added, each with its lineage tag,
        /// so a later read can tell a deletion from a rename (UAT S12-5-006, S12-6-002). An object named but not in
        /// the model then (one the task is to create) is not here, and is never called gone.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PlanObjectGround[] Grounds { get; set; }

        /// <summary>Old ids that still name this task: a migrated target keeps its slug and its
        /// "target/slug" item id here, so a skip, an assistant or a saved link naming either still resolves.</summary>
        public string[] Aliases { get; set; } = Array.Empty<string>();
    }

    /// <summary>How a task was closed before History took its edit back (<see cref="PlanTask.TakenBack"/>).</summary>
    public sealed class PlanTaskTakenBack
    {
        /// <summary>The ledger edit, filed under the task, whose undo reopened it. Only a redo of this edit closes
        /// the task again; newer work on the task is its own.</summary>
        public string EntryId { get; set; }
        public DateTimeOffset? DoneUtc { get; set; }
        public string DoneBy { get; set; }
    }

    /// <summary>One object a task was grounded to: its ref when the task was added, and its lineage tag if it had one.</summary>
    public sealed class PlanObjectGround
    {
        public string Ref { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string Tag { get; set; }
    }

    /// <summary>What a person decided about a DERIVED row (found, Jev): the row is recomputed every call, so
    /// the decision is kept beside it by the row's stable id (contract 2.1).</summary>
    public sealed class PlanRowResidue
    {
        public bool Held { get; set; }

        /// <summary>A doer the person chose over the row's own, or null for the row's own.</summary>
        public string Doer { get; set; }
        public string Note { get; set; }
        public string AssistantNote { get; set; }

        /// <summary>How many composes in a row this id has been absent. Pruned at
        /// <see cref="PlanMemory.ResidueAbsentLimit"/>.</summary>
        public int Absent { get; set; }

        internal bool IsEmpty => !Held && Doer == null && Note == null && AssistantNote == null;
    }

    /// <summary>What is remembered about a model's plan between calls.</summary>
    public sealed class PlanMemoryState
    {
        /// <summary>The asked items (contract 2.2), open and closed.</summary>
        public List<PlanTask> Tasks { get; set; } = new List<PlanTask>();

        /// <summary>The person's decisions about derived rows, keyed by the row's id.</summary>
        public Dictionary<string, PlanRowResidue> Residue { get; set; } =
            new Dictionary<string, PlanRowResidue>(StringComparer.Ordinal);

        /// <summary>The switch, one of <see cref="PlanAssistantQueue"/>. On unless the person paused it.</summary>
        public string AssistantQueue { get; set; } = PlanAssistantQueue.On;

        /// <summary>The saved repeats (lane RP, contract 1.7). Each card they bring back is an ordinary task in
        /// <see cref="Tasks"/> with its RepeatId.</summary>
        public List<PlanRepeat> Repeats { get; set; } = new List<PlanRepeat>();

        /// <summary>The task with this id, or the task that carries it as an old alias.</summary>
        public PlanTask FindTask(string id) =>
            string.IsNullOrWhiteSpace(id) ? null
            : Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
              ?? Tasks.FirstOrDefault(t => (t.Aliases ?? Array.Empty<string>()).Contains(id, StringComparer.Ordinal));

        public List<PlanSkip> Skips { get; set; } = new List<PlanSkip>();
        public List<ModelPlanHistoryEntry> History { get; set; } = new List<ModelPlanHistoryEntry>();

        /// <summary>The items the plan carried the last time it was composed. The whole basis of "1 item done,
        /// 1 new": without it the plan would have to guess, and a guess in that sentence is worse than none.</summary>
        public List<PlanSeenItem> LastItems { get; set; } = new List<PlanSeenItem>();

        /// <summary>Items whose gap has gone, newest last, capped at <see cref="PlanMemory.MaxDone"/>.</summary>
        public List<PlanDone> Done { get; set; } = new List<PlanDone>();

        /// <summary>The finished rows Refresh plan took off the list (Plan/LocalEngine.PlanRefresh.cs), by row id. Their record
        /// stays in <see cref="Done"/>, the tasks and the history; only the list stops showing them. A row that opens again
        /// leaves this list, so finishing it a second time shows it again.</summary>
        public List<string> ClearedDone { get; set; } = new List<string>();

        /// <summary>Where "Save the changes you have made" was last on the plan, as the facts were read; null while it
        /// is not. Kept in the file because the read that finds the row gone can be the next engine's.</summary>
        public PlanSaveMark UnsavedSeen { get; set; }

        /// <summary>The newest save that wrote edits after the Plan saw the model unsaved, recorded by the save itself,
        /// so a restart straight after it still counts the row as done.</summary>
        public PlanSaveMark LastSave { get; set; }

        /// <summary>The change set headers worked out for this model's items, keyed by ITEM ID.
        ///
        /// Remembered for the same reason the skips are: it cannot be recomputed. Which revision a set was
        /// worked out against, and whether it has already been applied, are facts about a past moment and
        /// the model does not hold them.
        ///
        /// Keyed by item id, so one item has at most one open proposal and a second propose replaces the
        /// first rather than stacking two sets a person could approve in either order. It sits under the
        /// model's own durable identity like everything else here (T266's F-187 fix), so a proposal worked
        /// out against A.bim can never be offered against B.bim in the same folder.</summary>
        public Dictionary<string, PlanChangeHeader> Changes { get; set; } =
            new Dictionary<string, PlanChangeHeader>(StringComparer.Ordinal);

        /// <summary>The lines the person typed into "what this model should become". Remembered for the same
        /// reason a skip is: it is a DECISION and there is nothing in the model to recompute it from. The
        /// rule-derived aims are not here and never will be; those are derived on every call from the
        /// sections themselves (PlanTargets.RuleAims), exactly as the gaps are.</summary>
        public List<ModelPlanTarget> Targets { get; set; } = new List<ModelPlanTarget>();

        public PlanSkip Find(string itemId) =>
            itemId == null ? null : Skips.FirstOrDefault(s => string.Equals(s.ItemId, itemId, StringComparison.Ordinal));
    }

    /// <summary>The file behind <see cref="PlanMemoryState"/>. Static and path-taking so it can be tested
    /// without an engine and without a model.</summary>
    public static class PlanMemory
    {
        public const string FileName = "plan-memory.json";

        /// <summary>How many history entries are kept. It was twenty while an entry was only a refresh note.
        /// The timeline shows the plan's whole story grouped by day and every action writes one now, so
        /// twenty would be most of one afternoon.</summary>
        public const int MaxHistory = 200;

        /// <summary>How many finished items are kept. The page's "done" count is what a person reads their
        /// progress off, so it is generous, but it is bounded: a file that only grows is a file that eventually
        /// makes opening a plan slow.</summary>
        public const int MaxDone = 200;

        /// <summary>How many open tasks one model keeps. An add past it is refused rather than dropping one.</summary>
        public const int MaxOpenTasks = 500;

        /// <summary>How many closed tasks one model keeps; the oldest closed drop first.</summary>
        public const int MaxClosedTasks = 200;

        /// <summary>How many composes a derived row may be absent before what the person decided about it goes.</summary>
        public const int ResidueAbsentLimit = 30;

        private static readonly object Gate = new object();

        /// <summary>Runs once inside <see cref="Mutate"/>, after the file was read and before it is written back. A
        /// test seam: it is the window a second engine process writing the same file used to fall into (Sol 6,
        /// slice 7, finding 4). Keyed by the file, so a test only ever stops its own write; empty on every real path
        /// and consumed once.</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action> AfterReadForTest =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Action>(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions Write = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>The change sets are stored in their WIRE shape, camelCase, the same shape both doors
        /// hand out. One shape, so a set read off the file and a set read off the wire cannot differ, which
        /// matters because the digest is computed over the values and not over the file.</summary>
        private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>The object every model's entries hang off, so one file can hold several models.</summary>
        private const string ModelsKey = "models";

        public static string FileIn(string sidecarDir) =>
            string.IsNullOrEmpty(sidecarDir) ? null : Path.Combine(sidecarDir, FileName);

        /// <summary>Read what is remembered for ONE model. Never throws: anything unreadable, and any model
        /// with no durable identity, reads as nothing remembered.</summary>
        public static PlanMemoryState Read(string file, string modelIdentity)
        {
            var state = new PlanMemoryState();
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(modelIdentity) || !File.Exists(file)) return state;

            try
            {
                var whole = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                // Only the keyed shape is read. A file written before the key existed is ignored rather than
                // handed to whichever model opens first, which is the very mix-up the key is here to stop.
                var root = whole?[ModelsKey] is JsonObject models ? models[modelIdentity] as JsonObject : null;
                if (root == null) return state;

                if (root["skips"] is JsonArray skips)
                {
                    foreach (var node in skips.OfType<JsonObject>())
                    {
                        var id = node["itemId"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        state.Skips.Add(new PlanSkip
                        {
                            ItemId = id,
                            Reason = node["reason"]?.GetValue<string>(),
                            When = ReadWhen(node["when"]),
                            Ref = ReadText(node["ref"]),
                            Tag = ReadText(node["tag"]),
                        });
                    }
                }

                if (root["history"] is JsonArray history)
                {
                    foreach (var node in history.OfType<JsonObject>())
                    {
                        var text = node["text"]?.GetValue<string>();
                        var sentence = node["sentence"]?.GetValue<string>();
                        // Either field alone is enough to have an entry: an old file has only text, and a
                        // reader that wrote only the structured half is still readable.
                        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(sentence)) continue;
                        var line = string.IsNullOrWhiteSpace(text) ? sentence : text;
                        state.History.Add(new ModelPlanHistoryEntry
                        {
                            When = ReadWhen(node["when"]),
                            Text = line,
                            Sentence = string.IsNullOrWhiteSpace(sentence) ? line : sentence,
                            // An entry with no kind was written before the structured shape, and every one
                            // of those was the plan composing itself.
                            Kind = ReadText(node["kind"]) ?? ModelPlanHistoryKinds.Redrawn,
                            Actor = ReadText(node["actor"]) ?? ModelPlanActors.Engine,
                            ItemId = ReadText(node["itemId"]),
                            Title = ReadText(node["title"]),
                            Revision = ReadLong(node["revision"]),
                            Detail = ReadLines(node["detail"]),
                            Appeared = ReadLines(node["appeared"]),
                            Gone = ReadLines(node["gone"]),
                            Completed = ReadLines(node["completed"]),
                        });
                    }
                }

                if (root["lastItems"] is JsonArray last)
                {
                    foreach (var node in last.OfType<JsonObject>())
                    {
                        var id = node["itemId"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        state.LastItems.Add(new PlanSeenItem
                        {
                            ItemId = id,
                            Title = node["title"]?.GetValue<string>(),
                            Section = node["section"]?.GetValue<string>(),
                            Doer = ReadText(node["doer"]),
                            SeenUtc = ReadWhenOrNull(node["seen"]),
                            Claims = (node["claims"] as JsonArray)?.Select(ReadText).Where(c => !string.IsNullOrWhiteSpace(c)).ToArray() ?? Array.Empty<string>(),
                        });
                    }
                }

                state.UnsavedSeen = ReadSaveMark(root["unsavedSeen"]);
                state.LastSave = ReadSaveMark(root["lastSave"]);

                if (root["targets"] is JsonArray targets)
                {
                    foreach (var node in targets.OfType<JsonObject>())
                    {
                        var id = ReadText(node["id"]);
                        var text = ReadText(node["text"]);
                        // Both halves are needed for the entry to mean anything: an id with no words is a
                        // target nobody can read, and words with no id cannot be removed again.
                        if (id == null || text == null) continue;
                        state.Targets.Add(new ModelPlanTarget
                        {
                            Id = id,
                            Text = text,
                            Section = ReadText(node["section"]),
                            // Only a target the person typed is ever written, so the source is not read back
                            // from the file: a file claiming "rules" would be claiming a line the engine
                            // derives, and the derived ones are added fresh on every compose.
                            Source = ModelPlanTargetSources.You,
                            When = ReadWhenOrNull(node["when"]),
                            DoneUtc = ReadWhenOrNull(node["doneUtc"]),
                        });
                    }
                }

                if (root["done"] is JsonArray done)
                {
                    foreach (var node in done.OfType<JsonObject>())
                    {
                        var id = node["itemId"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        state.Done.Add(new PlanDone
                        {
                            ItemId = id,
                            Title = node["title"]?.GetValue<string>(),
                            Section = node["section"]?.GetValue<string>(),
                            When = ReadWhen(node["when"]),
                            Doer = ReadText(node["doer"]),
                            ClosedWhy = ReadText(node["closedWhy"]),
                        });
                    }
                }

                if (root["clearedDone"] is JsonArray cleared)
                    foreach (var node in cleared)
                        if (ReadText(node) is string id && !string.IsNullOrWhiteSpace(id)) state.ClearedDone.Add(id);

                // The asked items, the residue and the switch (contract 2.1). Round-tripped through the types
                // in the wire shape, like the change headers below, and one unreadable task is dropped on its
                // own rather than taking the rest of the memory with it.
                if (root["tasks"] is JsonArray tasks)
                {
                    foreach (var node in tasks)
                    {
                        try
                        {
                            var task = node?.Deserialize<PlanTask>(Wire);
                            if (task == null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Words)) continue;
                            task.Objects ??= Array.Empty<string>();
                            task.Aliases ??= Array.Empty<string>();
                            task.State ??= PlanTaskStates.Open;
                            state.Tasks.Add(task);
                        }
                        catch { /* one bad task is not a reason to lose the others */ }
                    }
                }
                // The saved repeats (lane RP), one unreadable repeat dropped on its own like a task.
                if (root["repeats"] is JsonArray repeats)
                {
                    foreach (var node in repeats)
                    {
                        try
                        {
                            var repeat = node?.Deserialize<PlanRepeat>(Wire);
                            if (repeat == null || string.IsNullOrWhiteSpace(repeat.Id) || string.IsNullOrWhiteSpace(repeat.Words)
                                || repeat.Cadence == null || PlanCadences.Normalise(repeat.Cadence.Kind) == null) continue;
                            state.Repeats.Add(repeat);
                        }
                        catch { /* one bad repeat is not a reason to lose the others */ }
                    }
                }
                if (root["residue"] is JsonObject residue)
                {
                    foreach (var pair in residue)
                    {
                        if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                        try
                        {
                            var r = pair.Value.Deserialize<PlanRowResidue>(Wire);
                            if (r != null) state.Residue[pair.Key] = r;
                        }
                        catch { /* as above */ }
                    }
                }
                // Anything but the one word that pauses reads as on, so a damaged file never silently pauses
                // what the person's assistant is offered.
                state.AssistantQueue = string.Equals(ReadText(root["assistantQueue"]), PlanAssistantQueue.Paused, StringComparison.Ordinal)
                    ? PlanAssistantQueue.Paused
                    : PlanAssistantQueue.On;

                // The change set headers. Round-tripped through the type itself rather than field by field,
                // because a hand-written reader that forgets a field would quietly change what a set was
                // worked out against, which is the one thing approve checks. One unreadable header is
                // dropped on its own; the rest of the memory is kept.
                if (root["changes"] is JsonObject changes)
                {
                    foreach (var pair in changes)
                    {
                        if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                        try
                        {
                            var header = pair.Value.Deserialize<PlanChangeHeader>(Wire);
                            if (header != null) state.Changes[pair.Key] = header;
                        }
                        catch { /* one bad set is not a reason to lose the skips */ }
                    }
                }
            }
            catch
            {
                // A malformed file must not be the reason a person cannot see their plan. Losing the skips is
                // visible on the page (the items come back), which is the honest failure of the two.
                return new PlanMemoryState();
            }

            return state;
        }

        /// <summary>Apply a change to the remembered state and write it back, atomically. A null file (no
        /// sidecar: a model that was never saved and is not live) is a NO-OP that reports itself, so a caller
        /// can tell the person their skip will not be remembered rather than pretending it was.
        ///
        /// A CHANGE THAT CHANGES NOTHING IS NOT WRITTEN (UAT S00, F-291). This file sits in the model's folder and
        /// goes into the person's repository with it, so every write is a line in their diff. Reading the plan
        /// calls this to record what it saw, and when it saw the same as last time the file is left byte for byte
        /// as it was. "Nothing" is judged on what the file MEANS, not on its bytes: the state before and after the
        /// change are put in the one shape this method writes, and compared. A file another tool wrote, in its own
        /// layout, is therefore not rewritten just because the engine would have laid it out differently.
        ///
        /// A real change keeps the file's own format (see <see cref="Merge"/> and <see cref="FileFormat"/>), so the
        /// diff a person sees is the change and not a reformat of everything around it.</summary>
        public static bool Mutate(string file, string modelIdentity, Action<PlanMemoryState> mutate)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(modelIdentity)) return false;

            lock (Gate)
            {
                // The C# lock orders this process's writers; the file lock orders every engine process on the model,
                // held from the read to the move so no other process's write falls between them (Sol 6, slice 7,
                // finding 4). Mutate re-reads inside it, so the change is applied to what is on disk now.
                using var crossProcess = SidecarFileLock.Acquire(file);
                var state = Read(file, modelIdentity);
                // What the file says now, in the shape this method writes. Taken before anything below touches the
                // state; EntryNode copies every value, so the change cannot reach back into it.
                var before = EntryNode(state);
                if (AfterReadForTest.TryRemove(file, out var afterRead)) afterRead();
                // The lazy migration (contract 15): on the first write after the upgrade, every typed target
                // becomes an aim task, so no writer ever sees both shapes at once.
                PlanTasks.MigrateTargets(state);
                mutate(state);
                PlanTasks.Trim(state);

                if (state.History.Count > MaxHistory)
                    state.History.RemoveRange(0, state.History.Count - MaxHistory);
                if (state.Done.Count > MaxDone)
                    state.Done.RemoveRange(0, state.Done.Count - MaxDone);
                if (state.ClearedDone.Count > MaxDone)
                    state.ClearedDone.RemoveRange(0, state.ClearedDone.Count - MaxDone);

                var entry = EntryNode(state);
                // Nothing changed, so nothing is written: not the file, not its time stamp. True, because what the
                // caller asked to be remembered is remembered.
                if (JsonNode.DeepEquals(before, entry)) return true;

                // Read the WHOLE file back and replace only this model's entry, so a write for one model
                // never drops what is remembered for another in the same folder.
                string text = null;
                JsonObject whole = null;
                try
                {
                    if (File.Exists(file))
                    {
                        text = File.ReadAllText(file);
                        whole = JsonNode.Parse(text) as JsonObject;
                    }
                }
                catch { whole = null; }
                if (whole == null) { whole = new JsonObject(); text = null; }
                if (whole[ModelsKey] is not JsonObject models) { models = new JsonObject(); whole[ModelsKey] = models; }
                // Only the parts that changed take the engine's shape; everything else stays as the file had it.
                models[modelIdentity] = models[modelIdentity] is JsonObject old ? Merge(old, before, entry) : entry;

                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, FileFormat.Of(text).Write(whole));
                File.Move(tmp, file, overwrite: true);
                return true;
            }
        }

        private static PlanSaveMark ReadSaveMark(JsonNode node)
        {
            if (node is not JsonObject o) return null;
            var session = ReadText(o["session"]);
            return session == null ? null : new PlanSaveMark { Session = session, Saves = (int)ReadLong(o["saves"]), By = ReadText(o["by"]) };
        }

        // A mark is written only while it is set, so a model whose plan never saw an unsaved change keeps its file as it
        // was and an older file reads the same.
        private static JsonObject WithSaveMarks(JsonObject entry, PlanMemoryState state)
        {
            if (state.UnsavedSeen?.Session != null)
                entry["unsavedSeen"] = new JsonObject { ["session"] = state.UnsavedSeen.Session, ["saves"] = state.UnsavedSeen.Saves };
            if (state.LastSave?.Session != null)
                entry["lastSave"] = new JsonObject { ["session"] = state.LastSave.Session, ["saves"] = state.LastSave.Saves, ["by"] = state.LastSave.By };
            return entry;
        }

        // The ref and tag are written only while a skip has them, so a skip kept before they existed reads and writes as it did.
        private static JsonObject SkipNode(PlanSkip s)
        {
            var node = new JsonObject
            {
                ["itemId"] = s.ItemId,
                ["reason"] = s.Reason,
                ["when"] = s.When.ToString("o", CultureInfo.InvariantCulture),
            };
            if (s.Ref != null && s.Tag != null) { node["ref"] = s.Ref; node["tag"] = s.Tag; }
            return node;
        }

        /// <summary>One model's entry in the one shape this file is written in. Pure: it copies every value, so
        /// the node it returns does not change when the state does afterwards.</summary>
        private static JsonObject EntryNode(PlanMemoryState state) => WithClearedDone(WithSaveMarks(
                new JsonObject
                {
                    ["skips"] = new JsonArray(state.Skips.Select(s => (JsonNode)SkipNode(s)).ToArray()),
                    ["history"] = new JsonArray(state.History.Select(h => (JsonNode)new JsonObject
                    {
                        ["when"] = h.When.ToString("o", CultureInfo.InvariantCulture),
                        // Text is written on every entry, old readers included, and carries the sentence.
                        ["text"] = string.IsNullOrWhiteSpace(h.Text) ? h.Sentence : h.Text,
                        ["kind"] = h.Kind,
                        ["actor"] = h.Actor,
                        ["itemId"] = h.ItemId,
                        ["title"] = h.Title,
                        ["revision"] = h.Revision,
                        ["sentence"] = string.IsNullOrWhiteSpace(h.Sentence) ? h.Text : h.Sentence,
                        ["detail"] = Lines(h.Detail),
                        ["appeared"] = Lines(h.Appeared),
                        ["gone"] = Lines(h.Gone),
                        ["completed"] = Lines(h.Completed),
                    }).ToArray()),
                    ["lastItems"] = new JsonArray(state.LastItems.Select(i =>
                    {
                        var node = new JsonObject
                        {
                            ["itemId"] = i.ItemId,
                            ["title"] = i.Title,
                            ["section"] = i.Section,
                            ["doer"] = i.Doer,
                        };
                        if (i.SeenUtc.HasValue) node["seen"] = i.SeenUtc.Value.ToString("o", CultureInfo.InvariantCulture);
                        if (i.Claims is { Length: > 0 }) node["claims"] = new JsonArray(i.Claims.Select(c => (JsonNode)JsonValue.Create(c)).ToArray());
                        return (JsonNode)node;
                    }).ToArray()),
                    ["done"] = new JsonArray(state.Done.Select(d =>
                    {
                        var node = new JsonObject
                        {
                            ["itemId"] = d.ItemId,
                            ["title"] = d.Title,
                            ["section"] = d.Section,
                            ["when"] = d.When.ToString("o", CultureInfo.InvariantCulture),
                            ["doer"] = d.Doer,
                        };
                        if (d.ClosedWhy != null) node["closedWhy"] = d.ClosedWhy;   // only a row somebody else's change closed carries it
                        return (JsonNode)node;
                    }).ToArray()),
                    ["changes"] = ChangesNode(state.Changes),
                    ["targets"] = new JsonArray(state.Targets.Select(t => (JsonNode)new JsonObject
                    {
                        ["id"] = t.Id,
                        ["text"] = t.Text,
                        ["section"] = t.Section,
                        ["when"] = t.When?.ToString("o", CultureInfo.InvariantCulture),
                        ["doneUtc"] = t.DoneUtc?.ToString("o", CultureInfo.InvariantCulture),
                    }).ToArray()),
                    ["tasks"] = new JsonArray(state.Tasks
                        .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id))
                        .Select(t => JsonSerializer.SerializeToNode(t, Wire)).ToArray()),
                    ["residue"] = ResidueNode(state.Residue),
                    ["repeats"] = new JsonArray((state.Repeats ?? new List<PlanRepeat>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id))
                        .Select(r => JsonSerializer.SerializeToNode(r, Wire)).ToArray()),
                    ["assistantQueue"] = state.AssistantQueue == PlanAssistantQueue.Paused
                        ? PlanAssistantQueue.Paused
                        : PlanAssistantQueue.On,
                }, state), state);

        /// <summary>The rows Refresh plan cleared, written only while there are some, so a file that never had one keeps its shape.</summary>
        private static JsonObject WithClearedDone(JsonObject entry, PlanMemoryState state)
        {
            var ids = (state.ClearedDone ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length > 0) entry["clearedDone"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)).ToArray());
            return entry;
        }

        /// <summary>The new entry, built from the file's own nodes wherever a part did not change.
        ///
        /// <paramref name="before"/> is what <paramref name="raw"/> says, in the engine's shape (it was read from
        /// it), and <paramref name="after"/> is the same after the change. Where the two agree, the file's own node
        /// is kept as it was: its key order, its times as they were spelled, and any field it left out. Where they
        /// differ, the engine's node goes in, recursing into objects and lining arrays up so one changed field or
        /// one added entry is all that differs. A field the engine does not know is kept, so a newer engine's
        /// field survives an older engine's write. Every kept node reads back to the same value, because it is
        /// the one <paramref name="before"/> was read from.</summary>
        private static JsonNode Merge(JsonNode raw, JsonNode before, JsonNode after)
        {
            if (after == null) return null;
            if (raw == null || before == null) return after.DeepClone();
            if (JsonNode.DeepEquals(before, after)) return raw.DeepClone();

            if (raw is JsonObject ro && before is JsonObject bo && after is JsonObject ao)
            {
                var merged = new JsonObject();
                foreach (var pair in ro)
                {
                    if (ao.TryGetPropertyValue(pair.Key, out var a))
                        merged[pair.Key] = Merge(pair.Value, bo.TryGetPropertyValue(pair.Key, out var b) ? b : null, a);
                    else if (!bo.ContainsKey(pair.Key))
                        merged[pair.Key] = pair.Value?.DeepClone();   // a field this engine does not write
                    // else: it was there and the change removed it
                }
                foreach (var pair in ao)
                {
                    if (ro.ContainsKey(pair.Key)) continue;
                    // The file left this field out and it still means the same: leave it out.
                    if (bo.TryGetPropertyValue(pair.Key, out var b) && JsonNode.DeepEquals(b, pair.Value)) continue;
                    merged[pair.Key] = pair.Value?.DeepClone();
                }
                return merged;
            }

            // An array lines up with the file's only when reading dropped nothing from it; otherwise which raw
            // element an engine element came from is unknown, and the engine's array is written whole.
            if (raw is JsonArray ra && before is JsonArray ba && after is JsonArray aa && ra.Count == ba.Count)
            {
                var merged = new JsonArray();
                var next = 0;
                foreach (var a in aa)
                {
                    // The same element further on: the ones skipped over were removed (the oldest history drops).
                    var same = -1;
                    for (var k = next; k < ba.Count; k++)
                        if (JsonNode.DeepEquals(ba[k], a)) { same = k; break; }
                    if (same >= 0) { merged.Add(ra[same]?.DeepClone()); next = same + 1; }
                    // Not found: an element changed in place, or a new one. In place keeps its unchanged fields.
                    else if (next < ba.Count && ba[next] is JsonObject && a is JsonObject) { merged.Add(Merge(ra[next], ba[next], a)); next++; }
                    else merged.Add(a?.DeepClone());
                }
                return merged;
            }
            return after.DeepClone();
        }

        /// <summary>How the file on disk is laid out, so a write keeps it: indented or on one line, the indent, the
        /// line ends, a final newline, and whether characters such as "+" are escaped. A new file, or one this
        /// cannot read, is written the way the engine always has.</summary>
        private sealed class FileFormat
        {
            private bool _asFound;
            private bool _indented;
            private string _unit;
            private string _newLine;
            private bool _finalNewLine;
            private bool _relaxed;

            public static FileFormat Of(string text)
            {
                if (string.IsNullOrEmpty(text)) return new FileFormat();
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var first = lines.Skip(1).FirstOrDefault(l => l.Length > 0 && (l[0] == ' ' || l[0] == '\t'));
                return new FileFormat
                {
                    _asFound = true,
                    _indented = text.IndexOf('\n') >= 0,
                    _unit = first == null ? "  " : first.Substring(0, first.Length - first.TrimStart(' ', '\t').Length),
                    _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
                    _finalNewLine = text.EndsWith("\n", StringComparison.Ordinal),
                    // A file with no escapes at all was written by something that does not escape; one with any
                    // was written the way the engine writes, and keeps being.
                    _relaxed = !text.Contains("\\u", StringComparison.Ordinal),
                };
            }

            public string Write(JsonNode whole)
            {
                if (!_asFound) return whole.ToJsonString(PlanMemory.Write);
                var json = whole.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = _indented,
                    Encoder = _relaxed ? System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping : null,
                });
                if (!_indented) return _finalNewLine ? json + _newLine : json;
                // The serializer indents by two spaces. A string in JSON cannot hold a raw line break, so every
                // leading space on a line is indent and can be swapped for the file's own unit.
                var lines = json.Replace("\r\n", "\n").Split('\n').Select(l =>
                {
                    var pad = l.Length - l.TrimStart(' ').Length;
                    return string.Concat(Enumerable.Repeat(_unit, pad / 2)) + l.Substring(pad);
                });
                return string.Join(_newLine, lines) + (_finalNewLine ? _newLine : "");
            }
        }

        /// <summary>A string field, or null when it is absent, blank or not a string. Never an empty string:
        /// the caller distinguishes "no kind recorded" from "a kind", and "" would read as a kind.</summary>
        private static string ReadText(JsonNode node)
        {
            try
            {
                var raw = node?.GetValue<string>();
                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            catch { return null; }
        }

        private static long ReadLong(JsonNode node)
        {
            try { return node == null ? 0L : node.GetValue<long>(); }
            catch { return 0L; }
        }

        private static string[] ReadLines(JsonNode node) =>
            node is JsonArray array
                ? array.Select(ReadText).Where(s => s != null).ToArray()
                : Array.Empty<string>();

        /// <summary>The change sets as one object keyed by item id. Serialised through the wire options so
        /// what is written back is byte-for-byte what was read, field for field.</summary>
        private static JsonObject ChangesNode(Dictionary<string, PlanChangeHeader> changes)
        {
            var node = new JsonObject();
            foreach (var pair in changes ?? new Dictionary<string, PlanChangeHeader>(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                node[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, Wire);
            }
            return node;
        }

        private static JsonObject ResidueNode(Dictionary<string, PlanRowResidue> residue)
        {
            var node = new JsonObject();
            foreach (var pair in residue ?? new Dictionary<string, PlanRowResidue>(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.IsEmpty) continue;
                node[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, Wire);
            }
            return node;
        }

        private static JsonArray Lines(string[] values) =>
            new JsonArray((values ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => (JsonNode)v)
                .ToArray());

        /// <summary>A time that may legitimately be absent, unlike <see cref="ReadWhen"/>, whose callers all
        /// have a real moment to record. A target has two of these: when it was added, and when it was marked
        /// done, which is null for as long as it is not.</summary>
        private static DateTimeOffset? ReadWhenOrNull(JsonNode node)
        {
            string raw;
            try { raw = node?.GetValue<string>(); }
            catch { return null; }
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? when
                : (DateTimeOffset?)null;
        }

        private static DateTimeOffset ReadWhen(JsonNode node)
        {
            var raw = node?.GetValue<string>();
            DateTimeOffset when;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.RoundtripKind, out when)
                ? when
                : DateTimeOffset.MinValue;
        }

        /// <summary>The refresh note for a plan whose item set has just been recomputed. It counts, it never
        /// characterises: "1 item done, 2 new" is checkable against the two lists, and anything warmer would be
        /// a claim nobody could check.
        ///
        /// An item leaving the plan and an item being done are ONE event, not two. The gaps are recomputed from
        /// the model every time, so the only way an item can leave is for the gap it named to be gone, which is
        /// what done means here. Counting them separately said the same thing twice ("1 item done, 1 no longer
        /// on the plan") and made two changes look like one. The count is therefore taken from the sets, not
        /// passed in beside them, so the note and the lists cannot disagree.
        ///
        /// TWO REASONS A ROW LEAVES WITHOUT ITS WORK BEING DONE. A rule row standing aside while a Pro suggestion does its
        /// job (<paramref name="aside"/>): its gap is still there, so it is told as replaced, never as done. And, when the
        /// caller passes <paramref name="completed"/> (the rows it recorded as done, verifier round 3, 26 Sep), a row that left
        /// for another reason, such as "Save the changes you have made" after the changes were thrown away: only the
        /// completed rows are done and any other is counted as off the plan, which is what happened. Without
        /// <paramref name="completed"/> every row that left and did not stand aside is done, as before.</summary>
        /// <param name="aside">Rows that left because a suggestion took their place, not because their work was done.
        /// They are told as replaced, never as done.</param>
        /// <param name="completed">The rows the caller recorded as done, or null for every row that left.</param>
        public static string RefreshNote(IReadOnlyCollection<string> before, IReadOnlyCollection<string> now,
                                         IReadOnlyCollection<string> aside = null, IReadOnlyCollection<string> completed = null)
        {
            if (before == null || before.Count == 0)
                return "Semanticus made the plan from the model for the first time: "
                     + Plural(now == null ? 0 : now.Count, "task", "tasks") + ".";

            var previous = new HashSet<string>(before, StringComparer.Ordinal);
            var current = new HashSet<string>(now ?? Array.Empty<string>(), StringComparer.Ordinal);
            var replaced = new HashSet<string>(aside ?? Array.Empty<string>(), StringComparer.Ordinal);
            var added = current.Count(id => !previous.Contains(id));
            var left = previous.Where(id => !current.Contains(id)).ToList();
            var finished = completed == null ? null : new HashSet<string>(completed, StringComparer.Ordinal);
            var swapped = left.Count(replaced.Contains);
            var done = left.Count(id => !replaced.Contains(id) && (finished == null || finished.Contains(id)));
            var off = left.Count - done - swapped;

            var parts = new List<string>();
            if (done > 0) parts.Add(Plural(done, "task done", "tasks done"));
            if (swapped > 0) parts.Add(Plural(swapped, "task replaced by a suggestion", "tasks replaced by a suggestion"));
            if (off > 0) parts.Add(Plural(off, "task off the plan", "tasks off the plan"));
            if (added > 0) parts.Add(Plural(added, "new", "new"));

            return parts.Count == 0
                ? "Nothing on the plan changed."
                : string.Join(", ", parts) + ".";
        }

        private static string Plural(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
    }
}
