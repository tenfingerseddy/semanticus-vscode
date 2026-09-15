using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Lineage;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The engine-owned REPORT SCOPE.
    //
    // Before this, the "which reports did we check?" answer lived in the webview. The engine was then
    // asked for an impact fresh, with no reports, and truthfully said reports were not checked, while
    // the page beside it said they were. Worse, a deletion re-checked the model only, so a field a
    // checked cloud report displayed could be swept.
    //
    // One scope fixes that, split the way its two halves behave:
    //   • the CHOICES are a setting. They are saved with the model's other sidecar data and come back
    //     on reopen, as "not checked" - a saved selection is not evidence that anything was read.
    //   • the CHECK RESULTS are evidence. They belong to ONE READING of ONE REPORT in ONE SESSION.
    //
    // Astra's rejection of the first build (R1 to R5) was entirely about how that evidence could be
    // lost or changed between the answer a person read and the deletion that followed it. Each repair
    // is named at the code that carries it:
    //   R1  missing coverage is UNKNOWN, never a quiet fall back to the model-only safe set.
    //   R2  a proposal binds the basis it was reviewed against (see ReportScopeSignature).
    //   R3  identity and freshness are PER REPORT, keyed by its stable choice id, never merged by the
    //       display name and never given one shared revision stamp.
    //   R4  evidence is keyed by the SESSION. Reopening the same model restores the choices and not
    //       the reading, because the reading was about a session that has ended.
    //   R5  a slow read captures a scope generation and refuses to commit over a newer selection.
    //
    // Golden rule 1 holds: nothing here runs inference or holds a credential. A published report is
    // read through the same Entra helper the XMLA side uses, at use time.
    // ============================================================================================
    public sealed partial class LocalEngine
    {
        /// <summary>One report, as it was READ. R3: the reading, the outcome and the model revision it was read
        /// at all belong to this one report, keyed by its stable choice id.</summary>
        private sealed class ReportEvidence
        {
            public string ChoiceId { get; set; }
            public (string path, string error, ReportDefinitionReader.ParseResult result) Part { get; set; }
            public ReportScopeReport State { get; set; }
            public long RevisionAtCheck { get; set; }
        }

        /// <summary>Everything read in ONE session, against ONE selection.</summary>
        private sealed class ScopeEvidence
        {
            /// <summary>R5: the scope generation this evidence was committed against.</summary>
            public long ScopeGeneration { get; set; }
            public Dictionary<string, ReportEvidence> ByChoiceId { get; } =
                new Dictionary<string, ReportEvidence>(StringComparer.OrdinalIgnoreCase);
        }

        // R4: keyed by SESSION, so a close and reopen leaves no reading behind. The choices below are keyed
        // by the model's durable identity, because a choice is a setting and is meant to survive.
        private readonly Dictionary<string, ScopeEvidence> _reportScopeChecks =
            new Dictionary<string, ScopeEvidence>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _reportScopeGenerations =
            new Dictionary<string, long>(StringComparer.Ordinal);
        // Which sessions have chosen reports themselves. A session that has NOT is looking at choices restored
        // from disk, which is exactly the reopen case R4 has to say out loud.
        private readonly HashSet<string> _reportScopeTouched = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _reportScopeGate = new object();

        /// <summary>Test seam: stand in for the whole published-report read leg (token + getDefinition), so the
        /// scope's own behaviour is provable offline. Keyed by workspace id, then report id.</summary>
        internal Func<string, string[], Task<List<(string path, string error, ReportDefinitionReader.ParseResult result)>>> PublishedReportReaderForTests;

        /// <summary>Test seam: the parts a deletion would be checked against right now, or null.</summary>
        internal List<(string path, string error, ReportDefinitionReader.ParseResult result)> CheckedReportPartsForTests()
        {
            var s = _sessions.Current;
            var coverage = s == null ? null : CheckedCoverageFor(s);
            return coverage == null || coverage.Parts.Count == 0 ? null : coverage.Parts;
        }

        // R4: evidence belongs to one open session. Session.Id is minted per open, so a reopen is a new key.
        private static string EvidenceKey(Session s) => "sess:" + s.Id;
        private static string SessionScopeKey(Session s) => "session:" + s.Id;

        // ---- choices ---------------------------------------------------------------------------

        /// <summary>The reports this model is checked against, with what came of reading each one.</summary>
        public async Task<ReportScopeResult> ListReportScopeAsync()
        {
            var s = _sessions.Current;
            if (s == null) return ReportScopeResult.From(null, Array.Empty<ReportScopeReport>(), "No open model.");
            var modelName = await s.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            return ComposeScope(s, modelName);
        }

        /// <summary>Replace the reports this model is checked against. Choices are saved; nothing is read here, so
        /// every choice comes back as not checked until check_reports is run.</summary>
        public async Task<ReportScopeResult> SetReportScopeAsync(ReportScopeChoice[] choices, string origin = "human")
        {
            var s = _sessions.Current ?? throw new InvalidOperationException(
                "No open model. Use open_model, connect_local or connect_xmla first, then choose the reports to check it against.");
            var identity = PaneIdentity(s, null);
            var dir = TestsDirFor(s);
            var modelName = await s.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);

            var normalized = NormalizeChoices(choices, identity, origin);
            // B2. The write rides the SAME single-writer dispatcher the removal does. This used to happen on the
            // caller's own thread, so a selection could change part-way through a deletion's turn: the removal's
            // final check had already read the old basis and the delete then ran against a selection nothing had
            // ever checked. On the dispatcher the write can only land BETWEEN turns, never inside one, which is
            // what lets the removal hold one coherent snapshot for the whole of its own turn.
            var note = await s.RunAsync(() =>
            {
                string n = null;
                if (identity == null)
                    n = "This model has no durable identity yet, so the choice is kept for this session only. Save or open the model to keep it.";
                else if (dir == null)
                    n = "This session has no place on disk to keep the choice, so it is kept for this session only. Save the model to keep it.";
                else if (ReportScopeStore.Replace(dir, identity, normalized) == null)
                    n = "The choice could not be written beside the model, so it is kept for this session only.";

                lock (_reportScopeGate)
                {
                    // The choices changed, so nothing that was read still describes this selection. Drop the evidence
                    // rather than carrying a check of a report that is no longer on the list, and bump the generation
                    // so a read already in flight (R5) knows it has been superseded.
                    var key = EvidenceKey(s);
                    _reportScopeChecks.Remove(key);
                    _reportScopeGenerations[key] = _reportScopeGenerations.TryGetValue(key, out var g) ? g + 1 : 1;
                    _reportScopeTouched.Add(key);
                    _sessionOnlyReportChoices[identity ?? SessionScopeKey(s)] = normalized;
                }
                return n;
            });
            // Both doors hear it: a scope change moves every answer on the page (the cleanup list above all), and
            // an agent's set_report_scope must not leave a stale candidate list on screen (P1).
            PublishScopeChanged(s, origin, "report scope changed");
            return ComposeScope(s, modelName, note);
        }

        // A model with no durable identity still gets a working scope for the life of its session.
        private readonly Dictionary<string, List<ReportScopeChoice>> _sessionOnlyReportChoices =
            new Dictionary<string, List<ReportScopeChoice>>(StringComparer.Ordinal);

        private static List<ReportScopeChoice> NormalizeChoices(ReportScopeChoice[] choices, string identity, string origin)
        {
            var now = DateTime.UtcNow.ToString("o");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ReportScopeChoice>();
            foreach (var c in choices ?? Array.Empty<ReportScopeChoice>())
            {
                if (c == null) continue;
                var kind = string.Equals(c.Kind, "published", StringComparison.OrdinalIgnoreCase) ? "published" : "local";
                var id = string.IsNullOrWhiteSpace(c.Id)
                    ? (kind == "published" ? "published:" + (c.WorkspaceId ?? "") + "/" + (c.ReportId ?? c.Name ?? "") : "local:" + (c.Path ?? c.Name ?? ""))
                    : c.Id.Trim();
                if (kind == "published" && string.IsNullOrWhiteSpace(c.ReportId ?? c.Name))
                    throw new ArgumentException("A published report needs its report id. List the workspace's reports first, then choose from that list.");
                if (kind == "local" && string.IsNullOrWhiteSpace(c.Path))
                    throw new ArgumentException("A report folder needs its path: a Power BI project (.pbip) or its .Report folder. PBIX files are not supported.");
                if (!seen.Add(id)) continue;
                list.Add(new ReportScopeChoice
                {
                    Id = id,
                    Kind = kind,
                    Name = string.IsNullOrWhiteSpace(c.Name) ? (kind == "published" ? c.ReportId : System.IO.Path.GetFileName(c.Path.TrimEnd('/', '\\'))) : c.Name.Trim(),
                    WorkspaceId = c.WorkspaceId, WorkspaceName = c.WorkspaceName, ReportId = c.ReportId,
                    Path = c.Path, ModelIdentity = identity, ChosenWhenUtc = now, ChosenBy = origin,
                });
            }
            return list;
        }

        /// <summary>This model's saved choices: from the sidecar when it has a durable identity, else the
        /// session-only set. A sidecar can be shared, so a choice is claimed only by its own model.</summary>
        private List<ReportScopeChoice> ChoicesFor(Session s)
        {
            var identity = PaneIdentity(s, null);
            var key = identity ?? SessionScopeKey(s);
            lock (_reportScopeGate)
                if (_sessionOnlyReportChoices.TryGetValue(key, out var held) && identity == null) return held;
            if (identity == null) return new List<ReportScopeChoice>();
            var dir = TestsDirFor(s);
            if (dir == null)
            {
                lock (_reportScopeGate)
                    return _sessionOnlyReportChoices.TryGetValue(key, out var v) ? v : new List<ReportScopeChoice>();
            }
            var (all, _) = ReportScopeStore.Load(dir);
            var stored = all.Where(c => string.Equals(c.ModelIdentity, identity, StringComparison.Ordinal)).ToList();
            if (stored.Count > 0) return stored;
            lock (_reportScopeGate)
                return _sessionOnlyReportChoices.TryGetValue(key, out var v) ? v : stored;
        }

        private long CurrentScopeGeneration(Session s)
        {
            lock (_reportScopeGate)
                return _reportScopeGenerations.TryGetValue(EvidenceKey(s), out var g) ? g : 0;
        }

        // ---- the composed answer ----------------------------------------------------------------

        private ReportScopeResult ComposeScope(Session s, string modelName, string note = null)
        {
            var choices = ChoicesFor(s);
            var key = EvidenceKey(s);
            ScopeEvidence evidence;
            bool touched;
            lock (_reportScopeGate)
            {
                _reportScopeChecks.TryGetValue(key, out evidence);
                touched = _reportScopeTouched.Contains(key);
            }

            var reports = new List<ReportScopeReport>();
            foreach (var c in choices)
            {
                var source = c.Kind == "published"
                    ? (string.IsNullOrWhiteSpace(c.WorkspaceName) ? "a published report" : c.WorkspaceName)
                    : (c.Path ?? "a folder on this computer");
                // R3: freshness is this report's own. Rereading A never re-dates B.
                if (evidence != null && evidence.ByChoiceId.TryGetValue(c.Id, out var ev))
                {
                    var stale = ev.RevisionAtCheck != s.Revision;
                    reports.Add(new ReportScopeReport
                    {
                        Id = c.Id, Kind = c.Kind, Name = c.Name, Source = source,
                        State = stale && ev.State.State != ReportScopeStates.NotChecked ? ReportScopeStates.NeedsChecking : ev.State.State,
                        CheckedWhenUtc = ev.State.CheckedWhenUtc,
                        Note = stale && ev.State.State != ReportScopeStates.NotChecked
                            ? "The model changed after this report was read."
                            : ev.State.Note,
                        FieldsUsed = ev.State.FieldsUsed, Visuals = ev.State.Visuals,
                    });
                    continue;
                }
                reports.Add(new ReportScopeReport { Id = c.Id, Kind = c.Kind, Name = c.Name, Source = source, State = ReportScopeStates.NotChecked });
            }

            // R4: choices restored from disk into a session that has not chosen anything itself are a reopen.
            // Say what has to happen rather than implying the reading came back with them.
            if (note == null && choices.Count > 0 && evidence == null && !touched)
                note = "These reports are selected for this model. Check them again after reopening it.";
            return ReportScopeResult.From(modelName, reports, note);
        }

        // ---- checking ---------------------------------------------------------------------------

        /// <summary>
        /// Read the chosen reports and keep what they use. Published reports need consent first: reading a report's
        /// definition asks Fabric for permission that can also edit reports, even though Semanticus only reads it.
        /// Local report folders need no sign-in and no consent.
        /// </summary>
        public async Task<ReportScopeResult> CheckReportsAsync(string[] ids, bool consent = false, string authMode = null,
            string tenantId = null, string runId = null, string origin = "human", CancellationToken cancellationToken = default)
        {
            var s = _sessions.Require();
            var key = EvidenceKey(s);
            // R5: the selection this read is FOR. Captured before anything is awaited, compared before anything
            // is committed. A read that was overtaken must not repopulate the evidence a deletion relies on.
            var generationAtStart = CurrentScopeGeneration(s);
            var choices = ChoicesFor(s);
            var modelName = await s.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            if (choices.Count == 0)
                return ReportScopeResult.From(modelName, Array.Empty<ReportScopeReport>(),
                    "No reports are chosen yet. Choose the reports to check this model against, then check them.");

            var wantedIds = (ids ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = wantedIds.Count == 0 ? choices : choices.Where(c => wantedIds.Contains(c.Id)).ToList();
            if (targets.Count == 0)
                return ComposeScope(s, modelName, "None of those reports are on this model's list. Choose them first, then check them.");

            var published = targets.Where(c => c.Kind == "published").ToList();
            if (published.Count > 0 && !consent)
                throw new InvalidOperationException(
                    "Signing in to read a published report asks for permission that can also edit reports. Semanticus uses it only to read. " +
                    "Say yes to that before the reports can be checked.");

            // R3: every reading is carried under its own CHOICE ID, never under a display name, so two reports
            // that happen to share a name (the same report name in two workspaces) keep their own evidence.
            var readings = new Dictionary<string, (string path, string error, ReportDefinitionReader.ParseResult result)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in targets.Where(x => x.Kind == "local"))
            {
                var pr = ReportDefinitionReader.ReadLocalPbir(c.Path);
                readings[c.Id] = (c.Name ?? c.Path, pr.DefinitionFound ? null : "No readable report definition was found at that path.", pr);
            }
            foreach (var group in published.GroupBy(c => c.WorkspaceId ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var list = group.ToList();
                var reportIds = list.Select(c => c.ReportId ?? c.Name).ToArray();
                List<(string path, string error, ReportDefinitionReader.ParseResult result)> fetched;
                if (PublishedReportReaderForTests != null)
                    fetched = await PublishedReportReaderForTests(group.Key, reportIds).ConfigureAwait(false);
                else
                {
                    var byId = list.ToDictionary(c => c.ReportId ?? c.Name,
                        c => new CloudReport { Id = c.ReportId ?? c.Name, Name = c.Name, ReportType = "PowerBIReport" },
                        StringComparer.OrdinalIgnoreCase);
                    string fabricToken;
                    try { fabricToken = await AcquireFabricTokenAsync(authMode ?? "azcli", tenantId, origin, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not ArgumentException && !cancellationToken.IsCancellationRequested)
                    { throw new InvalidOperationException(FabricRest.Scrub(ex.Message)); }
                    fetched = await FetchCloudReportPartsAsync(group.Key, reportIds, byId, fabricToken, cancellationToken).ConfigureAwait(false);
                }
                for (var i = 0; i < list.Count && i < fetched.Count; i++) readings[list[i].Id] = fetched[i];
            }

            // R5: the gate. Everything above could take a minute; the person may have changed their mind.
            if (CurrentScopeGeneration(s) != generationAtStart)
                return ComposeScope(s, modelName,
                    "The report selection changed while these were being read, so that reading was not kept. Check the new selection.");

            var ordered = targets.Where(c => readings.ContainsKey(c.Id)).ToList();
            var parsedForAnalysis = ordered.Select(c => readings[c.Id]).ToList();
            var analysis = await s.ReadAsync(m => LineageGraph.AnalyzeReports(m, parsedForAnalysis));
            var now = DateTime.UtcNow.ToString("o");
            var revision = s.Revision;

            // B2, same reason as SetReportScopeAsync: committing a reading MOVES the basis (a chosen report goes
            // from unread to read), so it is a scope write too and rides the dispatcher rather than landing part-way
            // through a removal's turn.
            var overtaken = await s.RunAsync(() =>
            {
                lock (_reportScopeGate)
                {
                    // R5 again, under the lock: nothing may have moved between the check above and the commit.
                    if (_reportScopeGenerations.TryGetValue(key, out var gNow) ? gNow != generationAtStart : generationAtStart != 0)
                        return true;
                    if (!_reportScopeChecks.TryGetValue(key, out var evidence))
                        _reportScopeChecks[key] = evidence = new ScopeEvidence { ScopeGeneration = generationAtStart };

                    for (var i = 0; i < ordered.Count && i < analysis.Reports.Length; i++)
                    {
                        var c = ordered[i];
                        var usage = analysis.Reports[i];
                        var part = readings[c.Id];
                        var fullyRead = usage.Read && part.result.SkippedParts == 0 && usage.Unresolved == 0;
                        // R3: this report's own reading, its own outcome, its own revision stamp.
                        evidence.ByChoiceId[c.Id] = new ReportEvidence
                        {
                            ChoiceId = c.Id,
                            Part = part,
                            RevisionAtCheck = revision,
                            State = new ReportScopeReport
                            {
                                Id = c.Id, Kind = c.Kind, Name = c.Name,
                                State = fullyRead ? ReportScopeStates.Checked : ReportScopeStates.CouldNotBeFullyChecked,
                                CheckedWhenUtc = now,
                                Note = fullyRead ? null
                                    : usage.Read
                                        ? "Part of this report could not be read, so its use of this model is not fully known."
                                        : (usage.Error ?? "This report could not be read."),
                                FieldsUsed = usage.FieldCount,
                                Visuals = usage.Visuals?.Length ?? 0,
                            },
                        };
                    }
                    return false;
                }
            });
            if (overtaken)
                return ComposeScope(s, modelName,
                    "The report selection changed while these were being read, so that reading was not kept. Check the new selection.");

            if (DryRunScope.Current == null)
            {
                try
                {
                    await PublishActivityAsync(new ActivityEvent
                    {
                        Kind = "check_reports", Origin = origin, Ok = true,
                        Label = $"Checked {targets.Count} report(s) against {modelName}",
                        Result = new { checkedCount = ordered.Count, listed = choices.Count },
                    });
                }
                catch { }
            }
            // P1: a reading changes every answer on the page, so both doors hear about it, not just the caller.
            PublishScopeChanged(s, origin, "reports checked");

            var composed = ComposeScope(s, modelName);
            composed.Usage = analysis;
            return composed;
        }

        // ---- what the rest of the engine reads ---------------------------------------------------

        /// <summary>
        /// What a deletion would be checked against right now. R1: this is deliberately NOT just a list of parts.
        /// A chosen report that has not been read is MISSING COVERAGE, and every consumer has to see that rather
        /// than quietly receiving a shorter list and treating it as the whole intended scope.
        /// </summary>
        internal sealed class ReportScopeCoverage
        {
            public List<(string path, string error, ReportDefinitionReader.ParseResult result)> Parts { get; set; }
                = new List<(string, string, ReportDefinitionReader.ParseResult)>();
            public int Chosen { get; set; }
            public int Read { get; set; }
            /// <summary>The names of the chosen reports whose reading is missing or out of date.</summary>
            public string[] Unchecked { get; set; } = Array.Empty<string>();
            public bool HasChoices => Chosen > 0;
            /// <summary>True only when every chosen report has a current reading.</summary>
            public bool Complete => Chosen > 0 && Unchecked.Length == 0;
            /// <summary>The sentence both doors say when a deletion is asked for on incomplete coverage.</summary>
            public const string NothingRemoved = "Nothing removed. The chosen reports still need checking.";
            /// <summary>B2: the sentence both doors say when the selection moved between the review and the
            /// removal. It asks for the two things that repair it, in order: check the reports, then review the
            /// removal again against what they said.</summary>
            public const string SelectionChanged = "The report selection changed. Check it again and review this removal.";
            public string Caveat => Unchecked.Length == 0 ? null
                : (Unchecked.Length == 1 ? "1 chosen report still needs checking" : Unchecked.Length + " chosen reports still need checking")
                  + " (" + string.Join(", ", Unchecked.Take(3)) + (Unchecked.Length > 3 ? " and " + (Unchecked.Length - 3) + " more" : "")
                  + "), so nothing here can be called unused yet. Check them, then look again.";
        }

        /// <summary>
        /// B2: ONE reading of the scope, as a removal has to hold it. The coverage, the R2 signature and the
        /// generation all come out of the SAME evidence read, so they can never describe two different moments.
        /// They used to be fetched one at a time, each taking the lock for itself, which is a torn basis even
        /// before the bigger problem that scope writes did not go through the dispatcher at all.
        /// </summary>
        internal sealed class ScopeBasis
        {
            public ReportScopeCoverage Coverage { get; set; } = new ReportScopeCoverage();
            /// <summary>The R2 comparable basis string: which reports, and which of them have been read.</summary>
            public string Signature { get; set; } = "none";
            /// <summary>Bumped by every change of the SELECTION, so a re-selection of an identical list still
            /// reads as a moved basis (its evidence was dropped and has to be established again).</summary>
            public long Generation { get; set; }

            /// <summary>Has the basis moved since <paramref name="reviewed"/> was captured?</summary>
            public bool MovedSince(ScopeBasis reviewed) =>
                reviewed == null || Generation != reviewed.Generation
                || !string.Equals(Signature, reviewed.Signature, StringComparison.Ordinal);
        }

        /// <summary>The coverage a deletion or a cleanup list must answer from, its signature and its generation,
        /// taken together. R5: the committed evidence is filtered against the CURRENT choices, so a reading of a
        /// report nobody has chosen any more is ignored.</summary>
        internal ScopeBasis CaptureScopeBasis(Session s)
        {
            if (s == null) return new ScopeBasis();
            var choices = ChoicesFor(s);
            var key = EvidenceKey(s);
            ScopeEvidence evidence;
            long generation;
            lock (_reportScopeGate)
            {
                _reportScopeChecks.TryGetValue(key, out evidence);
                generation = _reportScopeGenerations.TryGetValue(key, out var g) ? g : 0;
            }

            var coverage = new ReportScopeCoverage { Chosen = choices.Count };
            var missing = new List<string>();
            foreach (var c in choices)
            {
                if (evidence != null && evidence.ByChoiceId.TryGetValue(c.Id, out var ev))
                {
                    coverage.Parts.Add(ev.Part);
                    coverage.Read++;
                    // A reading of an older model is still a reading of THAT report's contents, so it is kept for
                    // protection; it is not counted as current coverage, because the model has moved under it.
                    //
                    // A report that WAS read but only partly is deliberately NOT counted here. That is a different
                    // failure with its own mechanism: AnalyzeReports already demotes every bare "safe" to "caution"
                    // when a definition could not be fully parsed, so the candidate set is empty and each requested
                    // item skips with the coverage reason. Counting it twice would say "still needs checking" about
                    // a report that has been checked as far as it can be.
                    if (ev.RevisionAtCheck != s.Revision || ev.State.State == ReportScopeStates.NotChecked) missing.Add(c.Name ?? c.Id);
                    continue;
                }
                missing.Add(c.Name ?? c.Id);
            }
            coverage.Unchecked = missing.ToArray();

            // R2: one comparable string for the basis. Deliberately blind to the model revision and to the time of
            // the reading, so a model edit or an honest re-read of the SAME selection is not a changed basis, while
            // a different selection, or evidence that was dropped, is.
            var signature = choices.Count == 0 ? "none"
                : string.Join("|", choices.Select(c => c.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Select(id => id + ":" + (evidence != null && evidence.ByChoiceId.ContainsKey(id) ? "read" : "unread")));
            return new ScopeBasis { Coverage = coverage, Signature = signature, Generation = generation };
        }

        internal ReportScopeCoverage CheckedCoverageFor(Session s) => CaptureScopeBasis(s).Coverage;

        internal string ReportScopeSignature(Session s) => CaptureScopeBasis(s).Signature;

        /// <summary>The report parts this model was last checked against, or null when nothing has been read.</summary>
        internal List<(string path, string error, ReportDefinitionReader.ParseResult result)> CheckedReportPartsFor(Session s)
        {
            var coverage = CheckedCoverageFor(s);
            return coverage.Parts.Count > 0 ? coverage.Parts : null;
        }

        /// <summary>
        /// P1: a scope change moves every answer on the page, so it is broadcast as an ordinary model change
        /// rather than only returned to whoever asked for it. Without this an agent's set_report_scope left the
        /// cleanup list on screen carrying a new "checked against" label over candidates nobody had recomputed.
        /// No deltas: nothing in the model moved, only the basis every answer about it is measured against.
        /// </summary>
        private void PublishScopeChanged(Session s, string origin, string label)
        {
            try
            {
                _sessions.Bus.Publish(new ChangeNotification
                {
                    SessionId = s.Id, Revision = s.Revision, Origin = origin, Label = label,
                    Deltas = Array.Empty<ChangeDelta>(),
                });
            }
            catch { /* observational: a broken listener must never fail the choice or the reading */ }
        }

        /// <summary>The scope as the assessment carries it, without a model read of its own.</summary>
        internal ReportScopeResult ScopeSnapshot(Session s, string modelName) => ComposeScope(s, modelName);
    }
}
