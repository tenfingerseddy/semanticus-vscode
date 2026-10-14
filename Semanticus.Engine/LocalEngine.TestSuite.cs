using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // ---- wire DTOs (both doors) -------------------------------------------------------------------

    /// <summary>One suite run: the graded health + the three full reports (the UI's drill-down) + the
    /// run context. Health.Grade and Health.CoveragePct travel INSIDE one object by construction (I2).</summary>
    public sealed class TestSuiteRunResult
    {
        public string RunId { get; set; }
        public string When { get; set; }                  // ISO-8601 UTC
        public string ModelName { get; set; }
        public string ModelFingerprint { get; set; }
        public bool Live { get; set; }                    // was a live XMLA connection available for the probes
        public TestHealth Health { get; set; }
        public RelationshipIntegrityReport Relationships { get; set; }
        public ReconcileOutcome[] Reconciles { get; set; } = Array.Empty<ReconcileOutcome>();
        public int DefinitionCount { get; set; }
        public bool Persisted { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
        public long DurationMs { get; set; }              // whole-run wall clock
        public string Environment { get; set; }           // where the probes ran (database on server); null offline
        public bool CacheCleared { get; set; }            // the timing pass cleared the SE cache before its runs
        public TestRunScope Scope { get; set; }
    }

    /// <summary>What this run covered. A partial scope never mints a letter grade and is never recorded.</summary>
    public sealed class TestRunScope
    {
        public string Mode { get; set; }                 // everything | selected | section | refused
        public string[] Only { get; set; }
        public string[] Sections { get; set; }
        public int SelectedCount { get; set; }
        public int SuiteCount { get; set; }
        public bool Partial => !string.IsNullOrEmpty(Mode) && !string.Equals(Mode, "everything", StringComparison.Ordinal);

        /// <summary>Which groups this run actually covered, and which it left out, in the words a person reads:
        /// "saved checks", "relationships", "table counts". The page used to promise that relationships are
        /// "checked on every run" while a ticked-checks run silently skipped them, so the run says it itself.</summary>
        public string[] Ran { get; set; } = Array.Empty<string>();
        public string[] Skipped { get; set; } = Array.Empty<string>();

        /// <summary>What the grade on this run is the grade OF, in plain words. Absent on a full run, where the
        /// grade covers everything. A part-run is still graded on what it ran; it just never offers that letter
        /// as the model's overall grade.</summary>
        public string GradeCovers { get; set; }
    }

    public sealed class TestReportResult
    {
        public string Markdown { get; set; }
        public string Html { get; set; }
        public string Json { get; set; }
        public string ContentHash { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class TestSuiteInfo
    {
        public TestDefinition[] Definitions { get; set; } = Array.Empty<TestDefinition>();
        public int UnreadableLines { get; set; }
        public string Note { get; set; }
        /// <summary>Which named SQL source each saved reconciliation uses, if any, and which of them point at a
        /// source that is no longer saved. One entry per check that reads SQL; checks with nothing to say are absent.</summary>
        public TestSqlSourceUse[] SqlSourceUse { get; set; } = Array.Empty<TestSqlSourceUse>();
    }

    /// <summary>One persisted run: its health and context, plus every check's own outcome. Recorded runs used
    /// to keep the totals only, so opening one from History could show a grade with nothing behind it and the
    /// person had no way to see what had actually differed at the time. <see cref="Outcomes"/> is null on a run
    /// recorded before 1.2.0; the reader says so instead of showing an empty run.</summary>
    public sealed class TestRunRecord
    {
        public int SchemaVersion { get; set; } = TestSuiteStore.SchemaVersion;
        public string RunId { get; set; }
        public string When { get; set; }
        public string ModelFingerprint { get; set; }
        public bool Live { get; set; }
        public TestHealth Health { get; set; }
        public TestRunOutcomes Outcomes { get; set; }

        /// <summary>Enough of the run's own context that the snapshot can be read on its own years later: which
        /// model, which connection it asked, how many saved checks were in the suite, and what scope it covered.</summary>
        public string ModelName { get; set; }
        public string Environment { get; set; }
        public int DefinitionCount { get; set; }
        public TestRunScope Scope { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>Every check a recorded run decided, kept with the run. Detail rows are capped at the number the
    /// report shows and the uncapped total travels beside them, so "showing 20 of 340" stays honest years later.</summary>
    public sealed class TestRunOutcomes
    {
        public RecordedCheck[] Checks { get; set; } = Array.Empty<RecordedCheck>();
        public int ContextCap { get; set; }
        public string Note { get; set; }
    }

    /// <summary>One check as it stood when the run was recorded.</summary>
    public sealed class RecordedCheck
    {
        public string Kind { get; set; }             // savedCheck | relationship | tableRowCount
        public string DefId { get; set; }            // the saved definition's id; null for a check derived from the model
        public string Name { get; set; }
        public string Check { get; set; }            // which check within a relationship; null elsewhere
        public Verdict Verdict { get; set; }
        /// <summary>The answer the check was agreed against, what it actually got, and the difference. A
        /// relationship check that counts rows expects zero of them; one that compares data types has no
        /// number on either side, and leaves all three absent rather than inventing one.</summary>
        public string Expected { get; set; }
        public string Actual { get; set; }
        public decimal? Difference { get; set; }
        public string Note { get; set; }
        public RecordedContext[] Contexts { get; set; }
        public int ContextsTotal { get; set; }

        /// <summary>The SETTINGS this check ran with, kept as they were at execution time. Reading today's
        /// definition to describe a run from weeks ago describes a check that may since have been edited, which
        /// is not evidence (Astra, 2026-09-15). <see cref="Query"/> is the person's own SQL for a
        /// compare-with-source check and the DAX the engine ran for a trusted-answer one; <see cref="Source"/> is
        /// the SQL source the run actually RESOLVED to, by the name it had then. All four are absent when the
        /// check had none of that, never blank.</summary>
        public string Query { get; set; }
        public string Filter { get; set; }
        public string Tolerance { get; set; }
        public string Source { get; set; }

        /// <summary>How long the check's two sides took in this run, in milliseconds: the DAX on the model and the SQL on
        /// the source. Absent when the check did not run a query on that side.</summary>
        public long? DaxMs { get; set; }
        public long? SqlMs { get; set; }
    }

    /// <summary>One filter context of a recorded check, in the same words as the live run's compare table.</summary>
    public sealed class RecordedContext
    {
        public string Context { get; set; }
        public decimal? Expected { get; set; }
        public decimal? Actual { get; set; }
        public decimal? Difference { get; set; }
        public Verdict Verdict { get; set; }
        public string Note { get; set; }
        public bool GrandTotal { get; set; }
    }

    public sealed class TestHistoryInfo
    {
        public TestRunRecord[] Runs { get; set; } = Array.Empty<TestRunRecord>();
        public string Note { get; set; }
    }

    /// <summary>One recorded run, opened in full. <see cref="Run"/> is null when the id is not in this model's
    /// history; <see cref="Note"/> always says why there is nothing, or what the run could not keep.</summary>
    public sealed class TestRunDetail
    {
        public TestRunRecord Run { get; set; }
        public string Note { get; set; }

        /// <summary>True when this run was graded before role-filter checks left Tests in 1.2.0. Its grade is kept
        /// exactly as it was scored and is never rewritten; this says which rules produced it, so History can
        /// label the row instead of inviting a comparison across a scoring change.</summary>
        public bool GradedBeforeSecurityLeftTests { get; set; }
    }

    /// <summary>The answer to "record the run I am looking at". <see cref="Recorded"/> is false when there was
    /// nothing to record or it was already recorded; <see cref="Note"/> always says which.</summary>
    public sealed class TestRunRecordResult
    {
        public bool Recorded { get; set; }
        public string RunId { get; set; }
        public string Note { get; set; }
    }

    /// <summary>The many-side key values a relationship check counted as unmatched, read live and bounded. Each
    /// row is one key value the fact side uses that the lookup side does not have, with how many fact rows use
    /// it. This reads the data as it is NOW, which is not necessarily the data the recorded check measured.</summary>
    public sealed class UnmatchedRowsResult
    {
        public string Relationship { get; set; }
        public string ManyTable { get; set; }
        public string ManyColumn { get; set; }
        public string OneTable { get; set; }
        public string OneColumn { get; set; }
        public string ModelFingerprint { get; set; }
        public string Query { get; set; }
        public string[] Columns { get; set; }
        public object[][] Rows { get; set; }
        public int RowsReturned { get; set; }
        public int Cap { get; set; }
        public bool Truncated { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    // ============================================================================================
    // The Tests-tab suite COORDINATOR — the live adapter E4/E1 left open (docs/tests-tab-spec.md):
    // maps the wrapper model to the pure evaluators' inputs, executes RelationshipProbes over the
    // live connection, feeds the counts back, runs the saved reconcile definitions through
    // ReconcileMeasureAsync, and hands everything to TestHealthAnalyzer. Read-only w.r.t. the
    // model — no undo entry, no broadcast. Free/Pro (ratified): RUNNING + full evidence = free;
    // the persisted suite + run history = Pro.
    // ============================================================================================
    public sealed partial class LocalEngine
    {
        private TestSuiteRunResult _lastTestRun;

        /// <summary>The last table row counts this session actually TOOK, and for which model. Reading them out
        /// of <see cref="_lastTestRun"/> meant a run that did not count tables (a ticked saved check, say)
        /// replaced the run that did, and every table went back to "not counted yet" (Astra, 2026-09-15). The
        /// last real count belongs to the model, not to whichever run happened most recently, so it is kept
        /// beside its model identity and is never shown against a different one.</summary>
        private (string ModelIdentity, TableRowCountResult[] Results, string When) _lastTableCounts;

        private string TestsDirFor(Session s)
        {
            // The VitalsFileFor anchor ladder: the model's own .semanticus sidecar when it lives on disk, else
            // the workspace's; a live-only session with no workspace has no home → null (callers degrade to
            // "not persisted" honestly, never a throw).
            var anchored = !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? LayoutStore.DirFor(s.SourcePath) : null;
            return anchored ?? (_workspaceDir == null ? null : Path.Combine(_workspaceDir, LayoutStore.DirName));
        }

        /// <summary>Load only the open model's definitions. A .semanticus directory can be shared by live models
        /// (workspace fallback) or by multiple model files in one folder, so file placement alone never proves
        /// ownership. Legacy rows are claimed only when their rename-safe tag/sidecar identity resolves here;
        /// name-only rows stay unattributed rather than being guessed onto a same-named measure.</summary>
        private async Task<(List<TestDefinition> Defs, int Unreadable, TestObjectIdentityIndex Identities, string Note)>
            LoadScopedTestDefinitionsAsync(Session s)
        {
            var dir = TestsDirFor(s);
            var (all, unreadable) = TestSuiteStore.LoadSuite(dir);
            var identities = TestObjectIdentityStore.Load(dir);
            var modelIdentity = PaneIdentity(s, null);
            if (string.IsNullOrEmpty(modelIdentity))
                return (new List<TestDefinition>(), unreadable, identities,
                    all.Count == 0 ? null : "Saved SQL mappings are hidden because this unsaved model has no durable identity. Save or open the model before binding mappings.");

            var scoped = new List<TestDefinition>();
            var migrated = new List<TestDefinition>();
            await s.ReadAsync(m =>
            {
                var snapshots = TestObjectIdentityStore.Capture(m);
                foreach (var d in all)
                {
                    if (!string.IsNullOrEmpty(d.ModelIdentity))
                    {
                        if (string.Equals(d.ModelIdentity, modelIdentity, StringComparison.Ordinal)) scoped.Add(d);
                        continue;
                    }

                    var belongsHere = !string.IsNullOrEmpty(d.TargetTag)
                        ? m.Tables.SelectMany(t => t.Measures).Any(x => string.Equals(x.LineageTag, d.TargetTag, StringComparison.Ordinal))
                        : !string.IsNullOrEmpty(d.TargetIdentity) && identities.Resolve(d.TargetIdentity, snapshots) != null;
                    if (!belongsHere) continue;
                    d.ModelIdentity = modelIdentity;
                    scoped.Add(d);
                    migrated.Add(d);
                }
                return true;
            });

            var identityRefsChanged = identities.Dirty;
            TestObjectIdentityStore.Save(dir, identities);
            foreach (var d in migrated.Concat(identityRefsChanged
                ? scoped.Where(d => !string.IsNullOrWhiteSpace(d.TargetIdentity))
                : Enumerable.Empty<TestDefinition>()).Distinct())
                TestSuiteStore.Upsert(dir, d, () => d.Id);

            var hidden = all.Count - scoped.Count;
            return (scoped, unreadable, identities, hidden == 0 ? null
                : $"{hidden} saved mapping(s) belong to another model or predate model scoping and were hidden.");
        }

        public async Task<TestSuiteRunResult> RunTestSuiteAsync(bool persist = false, string origin = "human", string[] only = null, string[] sections = null, string runId = null)
        {
            if (persist) GuardOutsideWrite(origin);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = _sessions.Current;
            if (s == null) return new TestSuiteRunResult { Error = "No open model. Use open_model, connect_local or connect_xmla first." };

            // Asking for nothing must RUN nothing and say so. A ticked-nothing run used to fall through to
            // "everything", which is the opposite of what the person asked for, and a section with no evaluator
            // behind it was accepted and quietly ignored. Both are refused here, before any work.
            var refusal = ScopeRefusal(only, sections);
            if (refusal != null)
                return new TestSuiteRunResult
                {
                    RunId = Guid.NewGuid().ToString("N"),
                    When = DateTime.UtcNow.ToString("o"),
                    ModelFingerprint = VitalsFingerprintFor(s),
                    Scope = new TestRunScope { Mode = "refused", Only = only, Sections = sections },
                    Note = refusal,
                    DurationMs = sw.ElapsedMilliseconds,
                };

            var dir = TestsDirFor(s);
            var loaded = await LoadScopedTestDefinitionsAsync(s);
            // a run that keeps its record was approved for the tests as they stood: a test added or changed since is a new question
            if (persist) RequireApprovedBasis(nameof(RunTestSuiteAsync), SuiteBasis(dir), "tests");
            var defs = loaded.Defs;
            var badDefs = loaded.Unreadable;
            var identities = loaded.Identities;
            var suiteCount = defs.Count;
            var onlyIds = (only ?? Array.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
            var sectionNames = (sections ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var selected = onlyIds.Length > 0;
            var sectioned = !selected && sectionNames.Length > 0;
            var scope = new TestRunScope
            {
                Mode = selected ? "selected" : sectioned ? "section" : "everything",
                Only = selected ? onlyIds : null,
                Sections = sectioned ? sectionNames : null,
                SelectedCount = selected ? onlyIds.Length : sectioned ? 0 : suiteCount,
                SuiteCount = suiteCount,
            };
            if (selected)
            {
                var want = new HashSet<string>(onlyIds, StringComparer.Ordinal);
                defs = defs.Where(d => want.Contains(d.Id)).ToList();
                scope.SelectedCount = defs.Count;
            }
            bool HasSection(string name) => sectionNames.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            var runMeasures = !sectioned || HasSection("measures");
            var runRelationships = selected ? false : !sectioned || HasSection("relationships");
            // Table row counts are their OWN family, not a rider on relationships. The page offered the two as
            // separate choices while one flag ran both, so "Only this" on table counts also read every
            // relationship, and "Only this" on relationships opened a SQL connection nobody asked for
            // (Astra, 2026-09-15). One choice, one family.
            var runTableCounts = selected ? false : !sectioned || HasSection("tableCounts");
            // Which groups this run covers, in the words the page uses. The old page told the person that
            // relationships are "checked on every run" while a ticked-checks run quietly skipped them, so the
            // run states the truth about itself and no surface has to infer it from the mode.
            var ran = new List<string>();
            var skipped = new List<string>();
            (runMeasures ? ran : skipped).Add("saved checks");
            (runRelationships ? ran : skipped).Add("relationships");
            (runTableCounts ? ran : skipped).Add("table counts");
            scope.Ran = ran.ToArray();
            scope.Skipped = skipped.ToArray();
            if (scope.Partial)
                scope.GradeCovers = selected
                    ? scope.SelectedCount == 1 ? "the 1 check you picked" : "the " + scope.SelectedCount.ToString(CultureInfo.InvariantCulture) + " checks you picked"
                    : string.Join(" and ", ran);

            var measureDefs = runMeasures ? defs : new List<TestDefinition>();
            // SOURCE RESOLUTION: the table mappings this model has saved. A mapped table counts against the named SQL
            // source the person chose; an unmapped one still reads the model's own partitions, exactly as before.
            var tableMappings = runTableCounts ? await ScopedTableMappingsAsync(s) : new List<TableSourceMapping>();
            // An unmapped table reads the model's SQL endpoint next, then the old partition reading (contract 19.3).
            var tableEndpoint = runTableCounts ? await ModelSqlEndpointResolutionAsync(s) : null;
            var tablesOnPartitions = TablesOnPartitions(s);   // after the upgrade pass the line above ran
            // ONE model read builds every pure input (relationship endpoints + def bindings).
            var (relInputs, tableInputs, reconPlans, unmappedMeasures, modelName) = await s.ReadAsync(m =>
            {
                var plans = BindReconcileDefs(m, measureDefs, identities);
                var unmapped = runMeasures && !selected ? BuildUnmappedMeasureOutcomes(m, plans, measureDefs) : new List<ReconcileOutcome>();
                return (runRelationships ? BuildRelationshipInputs(m) : new List<RelationshipCheckInput>(),
                    runTableCounts ? TableRowCountReconciliation.Discover(m, tableMappings, tableEndpoint, tablesOnPartitions).ToList() : new List<TableRowCountInput>(),
                    plans, unmapped,
                    string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            });
            var identityRefsChanged = identities.Dirty;
            if (TestObjectIdentityStore.Save(dir, identities) && identityRefsChanged)
                foreach (var definition in defs.Where(d => !string.IsNullOrWhiteSpace(d.TargetIdentity)))
                    TestSuiteStore.Upsert(dir, definition, () => definition.Id);

            // The probes read data from the live model exactly like run_dax does, so an AGENT-origin run is
            // governed by the same standard QueryData gate on the XMLA target (one session grant per run —
            // the ReconcileMeasureAsync precedent). Humans run ungated. Denied ⇒ the probes are skipped and
            // every probe-backed check stays NotVerifiable (I1) — the static checks still run and report.
            var live = _live;
            string gateNote = null;
            if (live != null)
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"run the test suite: execute relationship-integrity probes against {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    intentBasis: "querydata", consumeGrant: false);
                if (gate != null) { live = null; gateNote = gate; }
            }

            // One sign-in per (mode, tenant) for the whole run: table counts and compare-with-source checks share it.
            var sqlTokens = new SqlTokenCache();
            var phase = System.Diagnostics.Stopwatch.StartNew();
            // The checks that take time, counted up front so the page can say "312 of 691": a relationship and a table count
            // each, a saved check each. A run with no live connection sends no query, so it counts only the saved checks.
            var valueDefs = runMeasures
                ? measureDefs.Where(d => d.Enabled && string.Equals(d.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<TestDefinition>();
            var progress = new RunProgressTicker(_sessions.Bus, runId,
                (live != null ? relInputs.Count + tableInputs.Count : 0) + (runMeasures ? reconPlans.Count : 0) + valueDefs.Count,
                RunProgressIntervalForTests ?? RunProgressMinInterval);
            progress.Start("relationships");
            if (live != null)
            {
                // The source half of every table count starts now: it is SQL on another server, so it runs while the
                // relationship probes and the table DAX use the single DAX lane. At most three SQL queries run at once.
                var sourceCounts = tableInputs.Select(t => Task.Run(() => ExecuteTableSourceCountAsync(t, origin, sqlTokens))).ToList();
                try
                {
                    foreach (var rel in relInputs)
                    {
                        rel.Probe = await ExecuteRelationshipProbesAsync(live, rel);
                        progress.Step("relationships");
                    }
                    LogRunPhase("relationships", relInputs.Count, phase);
                    phase.Restart();
                    for (var i = 0; i < tableInputs.Count; i++)
                    {
                        await ExecuteTableModelCountAsync(live, tableInputs[i]);
                        await sourceCounts[i];
                        progress.Step("table counts");
                    }
                    LogRunPhase("table counts", tableInputs.Count, phase);
                }
                finally { await SettleTasksAsync(sourceCounts); }
            }
            else
            {
                LogRunPhase("relationships", 0, phase);
                LogRunPhase("table counts", 0, phase);
            }

            var relReport = RelationshipIntegrity.Evaluate(relInputs);
            relReport.TableRowCounts = tableInputs.Select(TableRowCountReconciliation.Evaluate).ToArray();
            // Only a run that actually COUNTED replaces what the table-count card shows. A run that skipped the
            // family leaves the earlier counts and their own date alone, which is what the page promises.
            if (runTableCounts)
                _lastTableCounts = (PaneIdentity(s, live), relReport.TableRowCounts.ToArray(), DateTime.UtcNow.ToString("o"));

            var outcomes = new List<ReconcileOutcome>(runMeasures ? unmappedMeasures : Enumerable.Empty<ReconcileOutcome>());
            if (runMeasures)
            {
                phase.Restart();
                var modelEndpointId = reconPlans.Count == 0 ? null : await ModelSqlEndpointIdAsync(s);
                // Up to three checks are in flight at once, so one check's SQL runs while another's DAX holds the lane. The DAX
                // lane is still one query at a time, the SQL lane takes three, and the results land in the order of the plans.
                var reconOutcomes = new ReconcileOutcome[reconPlans.Count];
                using (var inFlight = new SemaphoreSlim(RunChecksInFlight))
                {
                    var running = reconPlans.Select((plan, i) => Task.Run(async () =>
                    {
                        await inFlight.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            reconOutcomes[i] = await RunReconcileDefAsync(plan, live != null, origin, modelEndpointId, sqlTokens).ConfigureAwait(false);
                            progress.Step("saved checks");
                        }
                        finally { inFlight.Release(); }
                    })).ToList();
                    try { await Task.WhenAll(running); }
                    finally { await SettleTasksAsync(running); }
                }
                outcomes.AddRange(reconOutcomes);
                LogRunPhase("compare with source", reconPlans.Count, phase,
                    outcomes.Sum(x => x.DurationMs), outcomes.Sum(x => x.SqlDurationMs));
                phase.Restart();
                outcomes.AddRange(await RunMeasureValuesAsync(valueDefs, identities, live != null, origin, () => progress.Step("saved checks")));
                LogRunPhase("trusted numbers", valueDefs.Count, phase);
            }
            else
            {
                LogRunPhase("compare with source", 0, phase);
                LogRunPhase("trusted numbers", 0, phase);
            }
            // An enabled definition whose kind has no evaluator yet must NOT vanish from the run (the suite
            // rots visibly, never silently): it surfaces as NotVerifiable with the reason. Covers the
            // stored-now-evaluated-later rowLevelAssertion kind and any kind from a newer engine's store.
            if (runMeasures)
            foreach (var d in measureDefs.Where(d => d.Enabled
                && !string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(d.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase)))
                outcomes.Add(new ReconcileOutcome
                {
                    DefId = d.Id,
                    Title = d.Title,
                    TargetRef = d.TargetRef,
                    CreatedBy = d.CreatedBy,
                    CreatedWhen = d.CreatedWhen,
                    Verdict = Verdict.NotVerifiable,
                    Message = string.Equals(d.Kind, TestKinds.RowLevelAssertion, StringComparison.OrdinalIgnoreCase)
                        ? "row-level assertions need view-as-role, which ships as its own gated slice; the test is stored and will run then"
                        : $"no evaluator exists for kind '{d.Kind}' in this engine version, so the test was counted but not run",
                });

            // I3 cross-measure cascade: a failing measure downstream of ANOTHER failing measure is a cascade,
            // not a second root — demoted to Suspect naming the dependency, BEFORE the analyzer counts roots.
            // (The relationship→measure cascade remains a later refinement.)
            DemoteDependentFailures(outcomes, reconPlans
                .Where(p => !string.IsNullOrWhiteSpace(p.KeyName))
                .ToDictionary(p => p.Def.Id, p => (p.KeyName, (IReadOnlyList<string>)p.DependsOnBound)));

            // E5: the OPT-IN clear-cache timing pass — only tests that DECLARED a budget are timed-judged, so
            // the Performance category can never activate (and move the grade) by surprise.
            var timingVerdicts = new List<Verdict>();
            var cacheCleared = false;
            phase.Restart();
            if (live != null)
                (timingVerdicts, cacheCleared) = await ExecuteTimingPassAsync(live, reconPlans, outcomes);
            else
                // Offline, a DECLARED budget is still a planned measurement (the E5 plan-reconciled rule:
                // planned-but-missing is NotVerifiable, never silently absent) — it shrinks coverage honestly
                // while Performance stays dormant (nothing decided), and the outcome says why.
                foreach (var p in BudgetedPlans(reconPlans))
                {
                    timingVerdicts.Add(Verdict.NotVerifiable);
                    var o = outcomes.FirstOrDefault(x => x.DefId == p.Def.Id);
                    if (o == null) continue;
                    o.TimingVerdict = Verdict.NotVerifiable;
                    o.TimingDetail = "Not run: no live connection, so the clear-cache timing could not execute.";
                }

            // E6 variants are supporting evidence for each base measure's family. They never enter the
            // score calculation; the chip verdicts carry their own signal without pretending they are tests.
            LogRunPhase("timing", timingVerdicts.Count, phase);
            phase.Restart();
            await ExecuteVariantPassAsync(live, reconPlans, outcomes);
            LogRunPhase("variants", reconPlans.Count, phase);

            // The Model interview left Tests in 1.2.0 (Kane, 2026-09-15). A run used to replay every saved
            // value question here and write the outcome back to the interview store, which made a Tests run
            // quietly change a second, separate record. The store, its questions and its own operations are
            // untouched and stay where a person put them; a Tests run simply does not call them.
            var health = TestHealthAnalyzer.Analyze(relReport, outcomes, timingVerdicts);
            // A part-run IS graded, on what it ran. It used to throw that away and report the word "Partial",
            // which told the person nothing about the checks they had just asked for. What it must never do is
            // offer that letter as the model's overall grade, so the scope says what the letter covers and the
            // note says it plainly. Recording still needs a full run, so no stored trend mixes the two.
            var run = new TestSuiteRunResult
            {
                RunId = Guid.NewGuid().ToString("N"),
                When = DateTime.UtcNow.ToString("o"),
                ModelName = modelName,
                ModelFingerprint = VitalsFingerprintFor(s),
                Live = live != null,
                Environment = live == null ? null
                    : string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource,
                CacheCleared = cacheCleared,
                Health = health,
                Relationships = relReport,
                Reconciles = outcomes.ToArray(),
                DefinitionCount = selected || sectioned ? defs.Count : suiteCount,
                Scope = scope,
                Note = JoinNotes(
                    badDefs > 0 ? $"{badDefs} unreadable line(s) in the saved suite were skipped." : null,
                    loaded.Note,
                    gateNote,
                    live == null && gateNote == null ? "Offline: relationship probes and reconciliations are not verifiable. Connect a live model in Connections for the full run." : null,
                    scope.Partial
                        ? $"This grade is for only the part you ran ({scope.GradeCovers}), not for the whole model. "
                          + "The last full run's grade still stands."
                          + (scope.Skipped.Length == 0 ? "" : " Left out: " + string.Join(", ", scope.Skipped) + ".")
                        : null),
            };

            // The run's time is stamped BEFORE the record is built from it. It was stamped after, so every recorded run
            // said it took 0 ms.
            run.DurationMs = sw.ElapsedMilliseconds;
            if (persist)
            {
                if (scope.Partial)
                    run.Note = JoinNotes(run.Note, "Only a full run can be recorded.");
                else if (dir == null)
                    run.Note = JoinNotes(run.Note, "This session has no saved project folder, so the run could not be stored.");
                else
                    run.Persisted = TestSuiteStore.AppendRun(dir, TestSuiteStore.Serialize(BuildRunRecord(run)));
            }
            LogRunTotal(run);
            RememberTestRun(run);
            return run;
        }

        // ---- progress while a run goes: "312 of 691 checks" -----------------------------------------------------
        // A run of a few hundred checks takes about a minute, and the page said only "Running". Each finished check is a step;
        // the ticker tells the page at most a few times a second (always the first and the last), so a fast run is two
        // messages and a slow one is not a message per check. It carries counts and a phase word, nothing from the model.
        internal static readonly TimeSpan RunProgressMinInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Test seam: the shortest gap between two progress events of one run.</summary>
        internal TimeSpan? RunProgressIntervalForTests;

        private sealed class RunProgressTicker
        {
            private readonly ChangeBus _bus;
            private readonly string _runId;
            private readonly int _total;
            private readonly long _minMs;
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private long _lastMs;
            private int _done;

            public RunProgressTicker(ChangeBus bus, string runId, int total, TimeSpan minInterval)
            {
                _bus = bus; _total = total; _minMs = (long)minInterval.TotalMilliseconds;
                _runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
            }

            public void Start(string note) { if (_total > 0) Publish(note); }

            public void Step(string note)
            {
                lock (this)
                {
                    _done++;
                    if (_done < _total && _clock.ElapsedMilliseconds - _lastMs < _minMs) return;
                    Publish(note);
                }
            }

            private void Publish(string note)
            {
                _lastMs = _clock.ElapsedMilliseconds;
                _bus.PublishProgress(new OperationProgress { OpKey = "run_tests", RunId = _runId, Done = _done, Total = _total, Note = note });
            }
        }

        // ---- the run's own log: one line per phase, counts and milliseconds only ----------------------------------
        // A run on a live model used to leave no trace of where its time went, so a slow run could not be explained. These
        // lines go to the engine log (the extension's output channel). They carry counts and milliseconds and nothing the
        // model holds: no value, no name, no query.
        private void RunLog(string line)
        {
            var seam = RunLogForTests;
            if (seam != null) { seam(line); return; }
            try { Console.Error.WriteLine(line); } catch { }
        }

        /// <summary>How many saved checks run side by side. Their DAX still goes one query at a time; this is what lets the
        /// SQL of one check run while another's DAX holds the lane.</summary>
        private const int RunChecksInFlight = 3;

        /// <summary>Wait for every task in the list, whatever happened to it, so nothing is left running or reported as an
        /// unobserved failure when a run ends early.</summary>
        private static async Task SettleTasksAsync(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks) { try { await t.ConfigureAwait(false); } catch { } }
        }

        private void LogRunPhase(string phase, int count, System.Diagnostics.Stopwatch took, long? daxMs = null, long? sqlMs = null)
            => RunLog("[tests] " + phase + ": " + count.ToString(CultureInfo.InvariantCulture) + " checked, "
                + (daxMs.HasValue ? "DAX " + daxMs.Value.ToString(CultureInfo.InvariantCulture) + " ms, SQL "
                    + (sqlMs ?? 0).ToString(CultureInfo.InvariantCulture) + " ms, " : "")
                + took.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");

        private void LogRunTotal(TestSuiteRunResult run)
        {
            var results = (run.Reconciles?.Length ?? 0)
                + (run.Relationships?.Relationships?.Sum(r => r?.Checks?.Count() ?? 0) ?? 0)
                + (run.Relationships?.TableRowCounts?.Count ?? 0);
            RunLog("[tests] run: " + results.ToString(CultureInfo.InvariantCulture) + " results, "
                + run.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>The parts of Tests that have an evaluator behind them. Anything else is refused by name
        /// rather than accepted and ignored: "history" and "saved reports" are places to look at recorded runs,
        /// not things to run, and role filters and the Model interview left Tests in 1.2.0.</summary>
        private static readonly string[] RunnableSections = { "measures", "relationships", "tableCounts" };

        /// <summary>Why this run scope cannot be honoured, in plain words, or null when it can. Omitting both
        /// lists still means "run everything"; supplying an EMPTY one means "run these none", which is a
        /// request to run nothing and is answered as one.</summary>
        private static string ScopeRefusal(string[] only, string[] sections)
        {
            if (only != null)
                // A real list of ticks wins over any sections argument, exactly as the run itself treats them.
                return only.Any(x => !string.IsNullOrWhiteSpace(x))
                    ? null
                    : "You did not pick any checks, so nothing ran. Tick the checks you want, or choose to run everything.";
            if (sections == null) return null;

            var named = sections.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (named.Length == 0)
                return "You did not pick any part of Tests, so nothing ran. Pick a part to run, or choose to run everything.";
            var dead = named.Where(x => !RunnableSections.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (dead.Length == 0) return null;

            if (dead.Any(x => string.Equals(x, "security", StringComparison.OrdinalIgnoreCase)))
                return "Role filters are no longer part of Tests, so nothing ran. Reading a saved filter never proved "
                    + "which rows a person can see, so it is not a test. You can still edit roles on the model.";
            if (dead.Any(x => string.Equals(x, "interview", StringComparison.OrdinalIgnoreCase)))
                return "The model interview is no longer part of Tests, so nothing ran. Your saved questions are still "
                    + "there and you can still ask them from the model interview.";
            return "There is nothing to run called " + string.Join(" or ", dead.Select(x => "'" + x + "'"))
                + ", so nothing ran. The parts you can run are " + string.Join(" and ", RunnableSections) + ".";
        }

        /// <summary>The snapshot one finished run is recorded as: its health, every check's outcome, and enough
        /// of its own context to be read on its own later. Built from the run that ALREADY happened, never from a
        /// fresh one.</summary>
        private static TestRunRecord BuildRunRecord(TestSuiteRunResult run) => new TestRunRecord
        {
            RunId = run.RunId,
            When = run.When,
            ModelFingerprint = run.ModelFingerprint,
            Live = run.Live,
            Health = run.Health,
            Outcomes = BuildRunOutcomes(run),
            ModelName = run.ModelName,
            Environment = run.Environment,
            DefinitionCount = run.DefinitionCount,
            Scope = run.Scope,
            DurationMs = run.DurationMs,
        };

        private const int RecentTestRunCap = 8;
        private readonly List<TestSuiteRunResult> _recentTestRuns = new List<TestSuiteRunResult>();

        /// <summary>Keep the last few finished runs so "record the run I am looking at" can mean exactly that.
        /// Bounded on purpose: this is a convenience for the run still on screen, not a second history.</summary>
        private void RememberTestRun(TestSuiteRunResult run)
        {
            _lastTestRun = run;
            NoteNextStepChecks(run);   // 1.5: a finished run moves the Next line on from "Run your checks"
            lock (_recentTestRuns)
            {
                _recentTestRuns.Add(run);
                while (_recentTestRuns.Count > RecentTestRunCap) _recentTestRuns.RemoveAt(0);
            }
        }

        /// <summary>Record a run that has ALREADY finished, exactly as it finished. Recording used to be a flag on
        /// a NEW execution, so "record it" saved a second run against data that may have moved in between, and the
        /// saved run could disagree with the one on screen. Nothing here executes anything: it writes the snapshot
        /// of the run named by <paramref name="runId"/>. Recording the same run twice keeps one snapshot.</summary>
        public Task<TestRunRecordResult> RecordTestRunAsync(string runId, string origin = "human")
        {
            GuardOutsideWrite(origin);
            TestRunRecordResult No(string note) => new TestRunRecordResult { RunId = runId, Note = note };
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(No("No open model."));
            if (string.IsNullOrWhiteSpace(runId))
                return Task.FromResult(No("Name the run to record. A finished run carries its own id."));

            TestSuiteRunResult run;
            lock (_recentTestRuns) run = _recentTestRuns.LastOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
            if (run == null)
                return Task.FromResult(No("There is no run with that id to record. Recording saves a run this session "
                    + "just finished, so run the tests again and record that run."));
            if (run.Health == null)
                return Task.FromResult(No("That run did not finish, so there is nothing to record."));
            if (run.Scope?.Partial == true)
                return Task.FromResult(No("Only a full run can be recorded, so that the saved history compares like "
                    + "with like. Run everything, then record that run."));

            var fingerprint = VitalsFingerprintFor(s);
            if (!string.IsNullOrEmpty(fingerprint) && !string.IsNullOrEmpty(run.ModelFingerprint)
                && !string.Equals(fingerprint, run.ModelFingerprint, StringComparison.Ordinal))
                return Task.FromResult(No("That run belongs to a different model. Open that model to record its run."));
            var dir = TestsDirFor(s);
            if (dir == null)
                return Task.FromResult(No("This session has no saved project folder, so the run could not be stored."));
            if (TestSuiteStore.ReadRunLines(dir).Select(TestSuiteStore.Deserialize<TestRunRecord>)
                .Any(r => r != null && string.Equals(r.RunId, runId, StringComparison.Ordinal)))
                return Task.FromResult(No("That run is already recorded. Opening it from the run list shows the same evidence."));

            var ok = TestSuiteStore.AppendRun(dir, TestSuiteStore.Serialize(BuildRunRecord(run)));
            if (ok) run.Persisted = true;
            return Task.FromResult(new TestRunRecordResult
            {
                Recorded = ok,
                RunId = runId,
                Note = ok ? null : "The run could not be written to the project folder, so it was not recorded.",
            });
        }

        /// <summary>The unmatched key values behind a relationship's orphan count, read LIVE and bounded. The
        /// count a check reports is a number; this is the answer to "which ones?". It reads the data as it is
        /// now, which is why the result says so: run it after opening a recorded check and the data may have
        /// moved since that check was measured. Agent callers pass the same QueryData gate as any other
        /// row-returning query, because that is the path this uses.</summary>
        public async Task<UnmatchedRowsResult> GetUnmatchedRowsAsync(string relationship, int limit = 200, string origin = "human")
        {
            const int HardCap = 200;
            var s = _sessions.Current;
            if (s == null) return new UnmatchedRowsResult { Relationship = relationship, Note = "No open model." };
            if (string.IsNullOrWhiteSpace(relationship))
                return new UnmatchedRowsResult { Note = "Name the relationship to look at. A run names each one it checked." };

            var match = await s.ReadAsync(m => BuildRelationshipInputs(m)
                .FirstOrDefault(r => string.Equals(r.Name, relationship, StringComparison.OrdinalIgnoreCase)));
            if (match == null)
                return new UnmatchedRowsResult
                {
                    Relationship = relationship,
                    Note = "This model has no relationship by that name. A run names each relationship it checked; use that name.",
                };

            var result = new UnmatchedRowsResult
            {
                Relationship = match.Name,
                ManyTable = match.ManyTable, ManyColumn = match.ManyColumn,
                OneTable = match.OneTable, OneColumn = match.OneColumn,
                ModelFingerprint = VitalsFingerprintFor(s),
                Cap = limit <= 0 ? HardCap : Math.Min(limit, HardCap),
            };
            if (_live == null)
            {
                result.Note = "Looking at the rows themselves needs a live connection. Connect a test model in Connections and try again.";
                return result;
            }

            result.Query = RelationshipProbes.UnmatchedRowsQuery(
                match.ManyTable, match.ManyColumn, match.OneTable, match.OneColumn, result.Cap);
            ResultSet rs;
            try { rs = await RunDaxAsync(result.Query, result.Cap + 1, origin); }
            catch (Exception ex) { result.Error = "Could not read the rows: " + ex.Message; return result; }
            if (rs == null || !string.IsNullOrEmpty(rs.Error)) { result.Error = rs?.Error ?? "Could not read the rows."; return result; }

            var rows = rs.Rows ?? Array.Empty<object[]>();
            result.Truncated = rows.Length > result.Cap || rs.Truncated;
            result.Rows = rows.Take(result.Cap).ToArray();
            result.RowsReturned = result.Rows.Length;
            result.Columns = (rs.Columns ?? Array.Empty<ColumnDef>()).Select(c => c?.Name).ToArray();
            result.Note = "Each row is one " + match.ManyTable + " value that " + match.OneTable
                + " does not have, with how many rows use it. This reads the data as it is now, so it can differ from "
                + "what the check counted when it ran."
                + (result.Truncated
                    ? " Showing the " + result.Cap.ToString(CultureInfo.InvariantCulture) + " with the most rows; there are more."
                    : "");
            return result;
        }

        /// <summary>How many comparisons differed and how many matched, in one plain sentence. "3 differ" printed
        /// beside a total invites the reader to believe those three explain the total, which the numbers do not
        /// say. The cap is disclosed only when it actually hides a DIFFERENCE: the rows are ordered worst first,
        /// so showing every differing comparison means nothing a person is looking for was cut.</summary>
        internal static string ComparisonCountLine(int mismatches, int matches, int shown, int total)
        {
            var line = mismatches.ToString(CultureInfo.InvariantCulture)
                + (mismatches == 1 ? " comparison differs; " : " comparisons differ; ")
                + matches.ToString(CultureInfo.InvariantCulture)
                + (matches == 1 ? " matches." : " match.");
            if (shown >= mismatches) return line;
            return line + " Showing the " + shown.ToString(CultureInfo.InvariantCulture)
                + (shown == 1 ? " biggest difference of " : " biggest differences of ")
                + total.ToString(CultureInfo.InvariantCulture) + " comparisons.";
        }

        public Task<TestReportResult> ExportTestReportAsync()
        {
            var s = _sessions.Current;
            if (s == null)
                return Task.FromResult(new TestReportResult { Error = "No open model. Use open_model, connect_local or connect_xmla first." });
            // Snapshot once: both doors can run the suite concurrently, so guarding and rendering the
            // field directly could pass the fingerprint check on one run and render another.
            var run = _lastTestRun;
            if (run == null || !string.Equals(run.ModelFingerprint, VitalsFingerprintFor(s), StringComparison.Ordinal))
                return Task.FromResult(new TestReportResult
                {
                    Note = "The last run belongs to a different model (or no run exists); run the suite again.",
                });
            var artifact = Semanticus.Engine.Evidence.EvidenceArtifact.Seal(TestReportRenderer.BuildEvidence(run));
            return Task.FromResult(new TestReportResult
            {
                Markdown = TestReportRenderer.Render(run),
                Html = artifact.Html,
                Json = artifact.Json,
                ContentHash = artifact.ContentHash,
            });
        }

        public async Task<TestSuiteInfo> ListTestDefinitionsAsync()
        {
            return await ListTestDefinitionsCoreAsync();
        }

        // The ungated core, for the engine's own callers that have already resolved the entitlement.
        private async Task<TestSuiteInfo> ListTestDefinitionsCoreAsync()
        {
            var s = _sessions.Current;
            if (s == null) return new TestSuiteInfo { Note = "No open model." };
            var loaded = await LoadScopedTestDefinitionsAsync(s);
            return new TestSuiteInfo
            {
                Definitions = loaded.Defs.ToArray(), UnreadableLines = loaded.Unreadable, Note = loaded.Note,
                SqlSourceUse = SqlSources.DescribeUse(loaded.Defs),
            };
        }

        /// <summary>Run one unsaved definition once. Writes nothing. Tests is a Pro feature as a whole, this
        /// included; probe_measure is the free way to check one number.</summary>
        public async Task<ReconcileOutcome> TryTestAsync(TestDefinition def, string origin = "human")
        {
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model. Use open_model or connect first.");
            if (def == null || string.IsNullOrWhiteSpace(def.Kind))
                throw new ArgumentException("A try needs a test kind and a title.");
            var identities = TestObjectIdentityStore.Load(TestsDirFor(s));
            var live = _live != null;
            if (string.Equals(def.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase))
            {
                var plans = await s.ReadAsync(m => BindReconcileDefs(m, new List<TestDefinition> { def }, identities));
                return await RunReconcileDefAsync(plans[0], live, origin, await ModelSqlEndpointIdAsync(s));
            }
            if (string.Equals(def.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase))
                return await RunMeasureValueDefAsync(def, identities, live, origin);
            return new ReconcileOutcome
            {
                Title = def.Title,
                TargetRef = def.TargetRef,
                Verdict = Verdict.NotVerifiable,
                Message = $"Can't try kind '{def.Kind}' yet.",
            };
        }

        /// <summary>Upsert a saved definition. The PERSISTED suite is the Pro side of the ratified line (the
        /// ambient suite runs free forever). Ground truth is AI-drafted, HUMAN-ACCEPTED — callers surface the
        /// SQL to the user before saving (the MCP description says so; the UI makes acceptance explicit).</summary>
        public async Task<TestDefinition> SaveTestDefinitionAsync(TestDefinition def, string origin = "human", string expectedJson = null)
        {
            GuardOutsideWrite(origin);
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model. Use open_model or connect first.");
            var dir = TestsDirFor(s) ?? throw new InvalidOperationException(
                "This session has no on-disk home. Save the model (save_model) so the .semanticus sidecar exists, then retry.");
            if (def == null || string.IsNullOrWhiteSpace(def.Title)) throw new ArgumentException("A test needs at least a title.");
            def.ModelIdentity = PaneIdentity(s, null) ?? throw new InvalidOperationException(
                "This unsaved model has no durable identity. Save it (save_model) or open/connect the model before saving a SQL mapping.");
            // Refuse an unknown kind AT SAVE (sol review): a typo'd kind would store fine and then never run,
            // which reads as coverage that does not exist. Known-but-not-yet-runnable kinds ARE saveable (the
            // run surfaces them NotVerifiable with the reason), so a suite can be authored ahead of its evaluator.
            var knownKinds = new[] { TestKinds.MeasureReconcile, TestKinds.MeasureValue, TestKinds.RowLevelAssertion };
            if (!knownKinds.Any(k => string.Equals(k, def.Kind, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"Unknown test kind '{def.Kind}'. Use '{TestKinds.MeasureValue}' (expected total), '{TestKinds.MeasureReconcile}' (SQL vs DAX reconciliation), or '{TestKinds.RowLevelAssertion}' (stored now, runs when view-as-role ships).");
            def.Kind = knownKinds.First(k => string.Equals(k, def.Kind, StringComparison.OrdinalIgnoreCase));   // canonical casing
            if (string.Equals(def.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase))
            {
                var value = string.IsNullOrWhiteSpace(def.ParamsJson) ? null : TestSuiteStore.Deserialize<MeasureValueRequest>(def.ParamsJson);
                if (value == null || string.IsNullOrWhiteSpace(value.ExpectedValue))
                    throw new ArgumentException("An expected-total test needs the number you trust.");
            }
            if (string.Equals(def.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(def.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase))
            {
                var reconcile = string.Equals(def.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)
                    ? (string.IsNullOrWhiteSpace(def.ParamsJson) ? null : TestSuiteStore.Deserialize<ReconcileRequest>(def.ParamsJson))
                    : null;
                var valueReq = string.Equals(def.Kind, TestKinds.MeasureValue, StringComparison.OrdinalIgnoreCase)
                    ? (string.IsNullOrWhiteSpace(def.ParamsJson) ? null : TestSuiteStore.Deserialize<MeasureValueRequest>(def.ParamsJson))
                    : null;
                var candidateRef = !string.IsNullOrWhiteSpace(def.TargetRef) ? def.TargetRef : (reconcile?.MeasureRef ?? valueReq?.MeasureRef);
                var binding = await s.ReadAsync(m =>
                {
                    var measure = ObjectRefs.Resolve(m, candidateRef) as Measure;
                    if (measure == null) return (Tag: (string)null, Snapshot: (TestMeasureIdentity)null, Ref: candidateRef);
                    var snapshot = TestObjectIdentityStore.Capture(m)
                        .First(x => string.Equals(x.Ref, ObjectRefs.For(measure), StringComparison.OrdinalIgnoreCase));
                    return (Tag: string.IsNullOrWhiteSpace(measure.LineageTag) ? null : measure.LineageTag,
                        Snapshot: snapshot, Ref: snapshot.Ref);
                });
                if (!string.IsNullOrWhiteSpace(binding.Tag))
                {
                    def.TargetTag = binding.Tag;
                    def.TargetIdentity = null;
                    def.BindingWarning = null;
                    def.TargetRef = binding.Ref;
                }
                else if (binding.Snapshot != null)
                {
                    var index = TestObjectIdentityStore.Load(dir);
                    def.TargetIdentity = index.Bind(def.TargetIdentity, binding.Snapshot);
                    def.TargetTag = null;
                    def.TargetRef = binding.Ref;
                    if (TestObjectIdentityStore.Save(dir, index)) def.BindingWarning = null;
                    else
                    {
                        def.TargetIdentity = null;
                        def.BindingWarning = "Rename safety warning: the tagless measure could not be written to the sidecar identity store, so this test is bound by name and may need re-binding after a rename.";
                    }
                }
                else if (string.IsNullOrWhiteSpace(def.TargetTag) && string.IsNullOrWhiteSpace(def.TargetIdentity))
                    def.BindingWarning = "Rename safety warning: the target did not resolve to a measure, so this test is bound by name and may need re-binding after a rename.";
            }
            def.CreatedWhen ??= DateTime.UtcNow.ToString("o");
            def.CreatedBy ??= origin;
            // RECORD the Tests-and-queries connection the check was agreed against, through BOTH doors, without
            // asking the caller for it. Only when the caller supplied neither field (re-saving an edit keeps the
            // original authoring) and only when something is connected — an offline save records nothing rather
            // than inventing a target. It never pins the run: the run always uses the current connection.
            if (string.IsNullOrEmpty(def.AuthoredAgainst) && string.IsNullOrEmpty(def.AuthoredAgainstLabel))
            {
                def.AuthoredAgainst = LiveIdentityFor(_live);
                def.AuthoredAgainstLabel = LiveTargetLabel(_live);
            }
            // the stored test this save replaces, as it was when the person was asked (a test they rewrote since is theirs)
            if (GateContext.Ticket?.ContentBasis != null)
                RequireApprovedBasis(nameof(SaveTestDefinitionAsync), TestDefinitionBasis(dir, def.Id), "test");
            var stored = TestSuiteStore.Upsert(dir, def, () => Guid.NewGuid().ToString("N"),
                expectedJson == null ? null : prior => GuardExpectedFields("check", expectedJson, prior))
                ?? throw new InvalidOperationException("The test could not be written to .semanticus/tests/suite.jsonl.");
            return stored;
        }

        public async Task<bool> DeleteTestDefinitionAsync(string id, string origin = "human")
        {
            GuardOutsideWrite(origin);
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model.");
            var scoped = await LoadScopedTestDefinitionsAsync(s);
            if (!scoped.Defs.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal))) return false;
            // checked under the store's own lock: the stored test this removes, as it was when the person was asked (a test they rewrote since is theirs)
            return TestSuiteStore.Remove(TestsDirFor(s), id, GateContext.Ticket?.ContentBasis == null ? null
                : stored => Stale(() => RequireApprovedBasis(nameof(DeleteTestDefinitionAsync), TestDefinitionBasisOf(stored), "test")));
        }

        /// <summary>Run history, the drift-trend substrate, fingerprint-filtered to the open model. Pro with the
        /// rest of the Tests feature, refused at entry rather than degraded to a note.</summary>
        public Task<TestHistoryInfo> ListTestRunsAsync(int last = 20)
        {
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(new TestHistoryInfo { Note = "No open model." });
            var fp = VitalsFingerprintFor(s);
            var runs = TestSuiteStore.ReadRunLines(TestsDirFor(s))
                .Select(TestSuiteStore.Deserialize<TestRunRecord>)
                .Where(r => r != null)
                .Where(r => string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(r.ModelFingerprint) || string.Equals(fp, r.ModelFingerprint, StringComparison.Ordinal))
                .ToList();
            var take = last <= 0 ? 20 : last;
            // A SUMMARY list: the per-check detail stays out of it and is fetched one run at a time with
            // GetTestRunAsync. Two hundred recorded runs of full evidence is not a list anyone wants pushed at them.
            foreach (var r in runs) r.Outcomes = null;
            return Task.FromResult(new TestHistoryInfo { Runs = runs.Skip(Math.Max(0, runs.Count - take)).ToArray() });
        }

        /// <summary>Open ONE recorded run in full: its health plus every check's outcome. The honest note carries
        /// the two cases a caller must be able to tell apart: a run this model's history does not have, and a run
        /// recorded before 1.2.0, which kept its totals but no per-check outcomes.</summary>
        public Task<TestRunDetail> GetTestRunAsync(string runId)
        {
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(new TestRunDetail { Note = "No open model." });
            if (string.IsNullOrWhiteSpace(runId))
                return Task.FromResult(new TestRunDetail { Note = "Name the recorded run to open. The run list shows their ids." });
            var fp = VitalsFingerprintFor(s);
            var record = TestSuiteStore.ReadRunLines(TestsDirFor(s))
                .Select(TestSuiteStore.Deserialize<TestRunRecord>)
                .Where(r => r != null)
                .Where(r => string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(r.ModelFingerprint) || string.Equals(fp, r.ModelFingerprint, StringComparison.Ordinal))
                .LastOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
            if (record == null)
                return Task.FromResult(new TestRunDetail { Note = "This model has no recorded run with that id. The run list shows the ones it does have." });
            // Old grades are kept EXACTLY as they were scored; nothing here rewrites one. What the reader needs
            // is which rules produced it, so History can label the row instead of inviting a comparison across a
            // scoring change it cannot see.
            var roleFilters = record.Health?.Categories?
                .FirstOrDefault(c => string.Equals(c.Category, "Security", StringComparison.OrdinalIgnoreCase));
            var oldRules = record.SchemaVersion < TestSuiteStore.SchemaVersion || roleFilters != null;
            return Task.FromResult(new TestRunDetail
            {
                Run = record,
                GradedBeforeSecurityLeftTests = oldRules,
                Note = JoinNotes(
                    record.Outcomes == null
                        ? "Outcomes were not recorded before 1.2.0, so this run kept its grade and totals but not what each check found."
                        : null,
                    !oldRules ? null
                        : roleFilters != null && roleFilters.HasChecks
                            ? "This run was graded before 1.2.0, when role filter checks still counted towards the grade, and "
                              + roleFilters.Checked.ToString(CultureInfo.InvariantCulture)
                              + " of them were decided here. The grade is kept as it was scored, so do not read it against a newer one."
                            : "This run was graded before 1.2.0, when role filter checks still counted towards the grade. "
                              + "None were decided in this run, so its grade would be the same today."),
            });
        }

        // ---- model → pure-evaluator inputs -------------------------------------------------------------

        /// <summary>STRUCTURAL side mapping (the E4 contract): the MANY fields hold the foreign-key side
        /// whichever way the TOM relationship happens to be authored — From is usually the many side, but a
        /// reversed oneToMany must not make us probe the wrong column. Many-to-many keeps a nominal order;
        /// the analyzer already refuses to trust single-key checks for it (there is no key side).</summary>
        private static List<RelationshipCheckInput> BuildRelationshipInputs(Model m)
        {
            var inputs = new List<RelationshipCheckInput>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn?.Table == null || r.ToColumn?.Table == null) continue;   // dangling — nothing probeable
                var fromMany = r.FromCardinality.ToString().Equals("Many", StringComparison.OrdinalIgnoreCase);
                var toMany = r.ToCardinality.ToString().Equals("Many", StringComparison.OrdinalIgnoreCase);
                var (manyCol, oneCol) = !fromMany && toMany ? (r.ToColumn, r.FromColumn) : (r.FromColumn, r.ToColumn);
                var cardinality = fromMany && toMany ? "manyToMany" : !fromMany && !toMany ? "oneToOne" : "manyToOne";
                inputs.Add(new RelationshipCheckInput
                {
                    Name = $"{manyCol.Table.Name}[{manyCol.Name}] → {oneCol.Table.Name}[{oneCol.Name}]",
                    ManyTable = manyCol.Table.Name,
                    ManyColumn = manyCol.Name,
                    OneTable = oneCol.Table.Name,
                    OneColumn = oneCol.Name,
                    Cardinality = cardinality,
                    IsActive = r.IsActive,
                    CrossFilter = r.CrossFilteringBehavior.ToString(),
                    ManyColumnType = manyCol.DataType.ToString(),
                    OneColumnType = oneCol.DataType.ToString(),
                });
            }
            return inputs;
        }

        /// <summary>Execute the six probe queries for one relationship. Each result is nullable-per-probe:
        /// a failed probe leaves ITS field null (that check stays NotVerifiable) without discarding the
        /// counts that did land — partial evidence honestly beats none.</summary>
        private static Task<RelationshipProbeResult> ExecuteRelationshipProbesAsync(LiveConnection live, RelationshipCheckInput rel)
            => RelationshipProbeRunner.RunAsync(q => live.ExecuteAsync(q, 2, 120), rel);

        /// <summary>Run one deterministic COUNTROWS / COUNT_BIG pair. The two halves are separate so the source half (SQL, another
        /// server) can run while the model half (DAX, the single lane) and the relationship probes do. A mismatch is not a failure here because
        /// the two connections cannot prove snapshot alignment; the pure judge keeps it NotVerifiable.</summary>
        private async Task ExecuteTableModelCountAsync(LiveConnection live, TableRowCountInput table)
        {
            if (!string.IsNullOrEmpty(table.DiscoveryError)) return;
            var dax = $"EVALUATE ROW(\"__table_rows\", COUNTROWS('{table.ModelTable.Replace("'", "''")}'))";
            try
            {
                var model = await live.ExecuteAsync(dax, 2, 120);
                table.ModelObservedUtc = DateTime.UtcNow.ToString("o");
                if (!TryReadCount(model, out var modelCount, out var modelError)) table.ModelError = modelError;
                else table.ModelCount = modelCount;
            }
            catch (Exception ex)
            {
                table.ModelObservedUtc = DateTime.UtcNow.ToString("o");
                table.ModelError = ex.Message;
            }
        }

        /// <summary>The source half of a table count: the SQL COUNT_BIG. It touches only the source fields of the input, so it
        /// can run while the model half and the relationship probes run on the DAX lane.</summary>
        private async Task ExecuteTableSourceCountAsync(TableRowCountInput table, string origin, SqlTokenCache sqlTokens)
        {
            if (!string.IsNullOrEmpty(table.DiscoveryError)) return;
            var gate = GuardAgent(AgentCapability.QueryData, table.Server, table.Database, origin, isCommit: true,
                summary: $"run the ambient row-count check: read {table.Schema}.{table.Entity} from {table.Database} on {table.Server}",
                intentBasis: "querydata", consumeGrant: false);
            if (gate != null) { table.SourceError = gate; return; }

            // A MAPPED table signs in the way its named SQL source says to. Only an unmapped table keeps the old
            // endpoint lookup, which finds a sign-in mode only when a remembered XMLA target happens to share the
            // address, and otherwise leaves the helper to read an absent mode as azcli.
            var authMode = table.AuthMode;
            var tenantId = table.TenantId;
            if (string.IsNullOrWhiteSpace(authMode))
            {
                var connection = ConnectionRegistry.FindByEndpoint(table.Server, table.Database);
                authMode = connection?.AuthMode;
                tenantId = connection?.TenantId;
            }
            string token;
            try { token = ReconcileSqlForTests != null && SqlTokenFetchForTests == null ? null : await RunSqlTokenAsync(sqlTokens, authMode, tenantId, origin).ConfigureAwait(false); }
            catch (Exception ex)
            {
                table.SourceError = "could not acquire a SQL token: " + ScrubSchemaError(ex.Message);
                return;
            }

            var sql = $"SELECT COUNT_BIG(*) AS [semanticus_table_rows] FROM {SqlIdentifier(table.Schema)}.{SqlIdentifier(table.Entity)}";
            var source = await ReconcileSqlAsync(table.Server, table.Database, token, sql, 2).ConfigureAwait(false);
            table.SourceObservedUtc = DateTime.UtcNow.ToString("o");
            if (!TryReadCount(source, out var sourceCount, out var sourceError)) table.SourceError = sourceError;
            else table.SourceCount = sourceCount;
        }

        /// <summary>The SQL sign-in for a test run, fetched ONCE per (mode, tenant) and then reused by every table count and
        /// every compare-with-source check in the run. Each fetch builds a browser credential and reads the saved sign-in
        /// from disk again, which was paid once per check. Only a token that was got is kept: a sign-in that failed is
        /// tried again by the next check, so each check still says for itself why it could not run. A run is minutes long
        /// and a token lives about an hour. With no cache (a single direct call) it is a plain fetch.</summary>
        private async Task<string> RunSqlTokenAsync(SqlTokenCache cache, string authMode, string tenantId, string origin)
        {
            if (cache == null) return await AcquireSqlTokenAsync(authMode, tenantId, origin, CancellationToken.None).ConfigureAwait(false);
            var key = string.Join("\u001f", authMode ?? "azcli", tenantId ?? "");
            return await cache.GetAsync(key, () => AcquireSqlTokenAsync(authMode, tenantId, origin, CancellationToken.None)).ConfigureAwait(false);
        }

        /// <summary>The run's sign-in cache. Checks now run side by side, so two of them can ask for the same sign-in at once:
        /// the second waits for the first fetch instead of starting its own. A fetch that failed is never kept, and a check
        /// that waited on a failed fetch tries once for itself, so every check still says for itself why it could not run.</summary>
        internal sealed class SqlTokenCache
        {
            private readonly Dictionary<string, Task<string>> _fetches = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

            public async Task<string> GetAsync(string key, Func<Task<string>> fetch)
            {
                var joined = false;
                while (true)
                {
                    Task<string> mine;
                    bool creator;
                    lock (_fetches)
                    {
                        creator = !_fetches.TryGetValue(key, out mine);
                        if (creator) _fetches[key] = mine = Task.Run(fetch);
                    }
                    try { return await mine.ConfigureAwait(false); }
                    catch
                    {
                        lock (_fetches) { if (_fetches.TryGetValue(key, out var now) && ReferenceEquals(now, mine)) _fetches.Remove(key); }
                        if (creator || joined) throw;
                        joined = true;   // I only waited on someone else's failed fetch: try once myself
                    }
                }
            }
        }

        private static bool TryReadCount(ResultSet result, out long count, out string error)
        {
            count = 0;
            error = result?.Error;
            if (!string.IsNullOrEmpty(error)) return false;
            if (result?.Rows == null || result.Rows.Length != 1 || result.Rows[0] == null || result.Rows[0].Length != 1)
            {
                error = "the count query did not return exactly one value";
                return false;
            }
            if (result.Rows[0][0] == null || result.Rows[0][0] is DBNull)
            {
                error = "the count query returned a blank value";
                return false;
            }
            try
            {
                count = Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture);
                if (count < 0) { error = "the count query returned a negative value"; return false; }
                return true;
            }
            catch
            {
                error = "the count query returned a non-integer value";
                return false;
            }
        }

        private static string SqlIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";

        // ---- saved reconcile definitions ---------------------------------------------------------------

        private sealed class ReconcilePlan
        {
            public TestDefinition Def;
            public ReconcileRequest Request;   // null = unparseable params (reported, not thrown)
            public bool Missing;               // tag-bound target vanished
            public string BindError;
            /// <summary>The bound measure's own NAME, taken from the resolved object at bind time. A measure name is unique in a model, so the by-name consumers (the cascade, the
            /// time-intelligence variants, the timing pass) key on this and never on <see cref="ReconcileRequest.MeasureRef"/>, which now holds the resolved object's full ref
            /// (a measure literally named 'measure:Revenue' parses as 'Revenue' when its ref is read back as a name; Sol join 11, A5 finding 1). Null when the plan never bound an object.</summary>
            public string MeasureName;
            /// <summary>How the run finds the bound measure again: by identity, never by a ref string that is parsed (<see cref="ReconcileBinding"/>). Null for a check bound to nothing.</summary>
            public ReconcileBinding Binding;
            public string KeyName => MeasureName ?? Request?.MeasureRef;
            // OTHER bound measures this plan's measure transitively depends on (I3's cross-measure cascade:
            // a Fail whose dependency also failed is demoted to Suspect naming the root). Captured during the
            // one model read; empty when unresolved.
            public List<string> DependsOnBound = new List<string>();
            public List<ReconcileVariantPlan> Variants = new List<ReconcileVariantPlan>();
        }

        private sealed class ReconcileVariantPlan
        {
            public string Measure;
            public TiClassification Classification;
        }

        private static List<ReconcilePlan> BindReconcileDefs(Model m, List<TestDefinition> defs, TestObjectIdentityIndex identities)
        {
            var plans = new List<ReconcilePlan>();
            var snapshots = TestObjectIdentityStore.Capture(m);
            foreach (var d in defs.Where(d => d.Enabled && string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)))
            {
                var plan = new ReconcilePlan { Def = d };
                plan.Request = string.IsNullOrWhiteSpace(d.ParamsJson) ? null : TestSuiteStore.Deserialize<ReconcileRequest>(d.ParamsJson);
                if (plan.Request == null)
                    plan.BindError = "the test's paramsJson does not parse as a ReconcileRequest. Edit or re-save the test";
                else if (!string.IsNullOrEmpty(d.TargetTag))
                {
                    // The tag is the identity (rename-safe). Resolve it to the measure's CURRENT name so the
                    // request follows a rename; a vanished tag is MISSING — surfaced loudly, never silently run
                    // against a same-named impostor (the FindTableBy terminal-miss discipline).
                    var measure = m.Tables.SelectMany(t => t.Measures).FirstOrDefault(x => x.LineageTag == d.TargetTag);
                    if (measure == null) plan.Missing = true;
                    else { plan.Request.MeasureRef = ObjectRefs.For(measure); plan.MeasureName = measure.Name; plan.Binding = ReconcileBinding.ForTag(d.TargetTag); }   // the run finds it by its tag: neither its name nor its ref is read back as a name
                }
                else if (!string.IsNullOrEmpty(d.TargetIdentity))
                {
                    var measure = identities.Resolve(d.TargetIdentity, snapshots);
                    if (measure == null) plan.Missing = true;
                    else
                    {
                        plan.Request.MeasureRef = measure.Ref;
                        plan.MeasureName = measure.MeasureName;
                        plan.Binding = ReconcileBinding.ForNames(measure.TableName, measure.MeasureName);
                        d.TargetRef = measure.Ref;
                    }
                }
                plans.Add(plan);
            }
            CaptureBoundDependencies(m, plans);
            CaptureTimeIntelligenceVariants(m, plans);
            return plans;
        }

        /// <summary>Every model measure belongs in correctness coverage even before it has accepted source
        /// SQL. Omitting unmapped measures made a relationship-only run read A / 100% while measure coverage
        /// was actually zero. They remain grade-neutral NotVerifiable rows until a saved definition replaces
        /// the placeholder, preserving I1 while making I2's denominator describe the whole model.</summary>
        private static List<ReconcileOutcome> BuildUnmappedMeasureOutcomes(Model m, List<ReconcilePlan> plans, List<TestDefinition> defs = null)
        {
            var mappedTags = plans.Select(p => p.Def.TargetTag)
                .Concat((defs ?? Enumerable.Empty<TestDefinition>()).Select(d => d.TargetTag))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToHashSet(StringComparer.Ordinal);
            // TargetRef is display-only once a stable tag exists. Using a stale ref from a missing tagged
            // target would hide a newly-created same-name impostor from coverage, defeating the tag binding.
            var mappedRefs = plans.Where(p => string.IsNullOrWhiteSpace(p.Def.TargetTag)).Select(p => p.Def.TargetRef)
                .Concat((defs ?? Enumerable.Empty<TestDefinition>()).Where(d => string.IsNullOrWhiteSpace(d.TargetTag)).Select(d => d.TargetRef))
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outcomes = new List<ReconcileOutcome>();
            foreach (var measure in m.Tables.SelectMany(t => t.Measures))
            {
                var targetRef = ObjectRefs.For(measure);
                if ((!string.IsNullOrWhiteSpace(measure.LineageTag) && mappedTags.Contains(measure.LineageTag))
                    || mappedRefs.Contains(targetRef)) continue;
                var empty = string.IsNullOrWhiteSpace(measure.Expression);
                outcomes.Add(new ReconcileOutcome
                {
                    DefId = "unmapped:" + targetRef,
                    Title = measure.Name,
                    TargetRef = targetRef,
                    Verdict = empty ? Verdict.Fail : Verdict.NotVerifiable,
                    Message = empty
                        ? "This measure has no formula."
                        : "No human-accepted source SQL mapping exists for this measure.",
                });
            }
            return outcomes;
        }

        /// <summary>Discover direct dependent measures only. Recognized identities lead in model order;
        /// unfamiliar dependents remain visible at the end as honest NotVerifiable evidence.</summary>
        private static void CaptureTimeIntelligenceVariants(Model m, List<ReconcilePlan> plans)
        {
            var measures = m.Tables.SelectMany(t => t.Measures).ToList();
            var byName = measures.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                if (plan.Missing || string.IsNullOrWhiteSpace(plan.KeyName)) continue;
                if (!byName.TryGetValue(plan.KeyName, out var baseMeasure)) continue;
                var direct = new List<ReconcileVariantPlan>();
                foreach (var candidate in measures)
                {
                    bool dependsOnBase;
                    try { dependsOnBase = candidate.DependsOn.Keys.OfType<Measure>().Contains(baseMeasure); }
                    catch { continue; }
                    if (!dependsOnBase) continue;
                    direct.Add(new ReconcileVariantPlan
                    {
                        Measure = candidate.Name,
                        Classification = TimeIntelligenceVariants.Classify(candidate.Expression, baseMeasure.Name),
                    });
                }
                plan.Variants = direct.Where(v => v.Classification.Kind != TiVariantKind.Unrecognized)
                    .Concat(direct.Where(v => v.Classification.Kind == TiVariantKind.Unrecognized))
                    .Take(8)
                    .ToList();
            }
        }

        /// <summary>For each bound plan, which OTHER bound measures its measure transitively depends on —
        /// the input to the I3 cross-measure demotion (a Fail whose dependency also failed is a cascade,
        /// not a second root). BFS over the wrapper's dependency graph, measures only, cycle-guarded; a
        /// dependency read that throws just yields no edges (cascade is a refinement, never a blocker).</summary>
        private static void CaptureBoundDependencies(Model m, List<ReconcilePlan> plans)
        {
            var bound = plans.Where(p => !p.Missing && !string.IsNullOrWhiteSpace(p.KeyName))
                .Select(p => p.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bound.Count < 2) return;   // nothing to cascade between
            var byName = m.Tables.SelectMany(t => t.Measures)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                if (plan.Missing || string.IsNullOrWhiteSpace(plan.KeyName)) continue;
                if (!byName.TryGetValue(plan.KeyName, out var start)) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Name };
                var queue = new Queue<Measure>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    List<Measure> deps;
                    try { deps = queue.Dequeue().DependsOn.Keys.OfType<Measure>().ToList(); }
                    catch { continue; }
                    foreach (var dep in deps)
                    {
                        if (!seen.Add(dep.Name)) continue;
                        if (bound.Contains(dep.Name)) plan.DependsOnBound.Add(dep.Name);
                        queue.Enqueue(dep);
                    }
                }
            }
        }

        /// <summary>The I3 cross-measure cascade, pure over the outcomes (unit-testable without a model):
        /// a FAILING outcome whose measure transitively depends on another measure that ALSO failed cannot
        /// be attributed independently — it is demoted to Suspect NAMING the failing dependency, so the
        /// root-cause count reports only true roots. Demotion is decided against the ORIGINAL fail set
        /// (an A→B→C chain of fails leaves exactly A as the root, however the list is ordered). Mirrors
        /// E4's uniqueness→referential-integrity demotion.</summary>
        internal static void DemoteDependentFailures(
            List<ReconcileOutcome> outcomes,
            IReadOnlyDictionary<string, (string Measure, IReadOnlyList<string> DependsOnBound)> planByDefId)
        {
            var failedMeasures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in outcomes)
                if (o.Verdict == Verdict.Fail && planByDefId.TryGetValue(o.DefId ?? "", out var p) && !string.IsNullOrEmpty(p.Measure))
                    failedMeasures.Add(p.Measure);
            if (failedMeasures.Count < 2) return;
            foreach (var o in outcomes)
            {
                if (o.Verdict != Verdict.Fail || !planByDefId.TryGetValue(o.DefId ?? "", out var p)) continue;
                var failingDeps = (p.DependsOnBound ?? Array.Empty<string>()).Where(failedMeasures.Contains).ToList();
                if (failingDeps.Count == 0) continue;
                o.Verdict = Verdict.Suspect;
                o.Message = $"demoted: it depends on {string.Join(" and ", failingDeps.Select(d => "[" + d + "]"))}, "
                    + "which failed its own reconciliation, so this failure cannot be attributed independently. "
                    + $"Fix the dependency first, then retest. Original result: {o.Message}";
            }
        }

        private async Task<ReconcileOutcome> RunReconcileDefAsync(ReconcilePlan plan, bool live, string origin, string modelEndpointId,
            SqlTokenCache sqlTokens = null)
        {
            var o = new ReconcileOutcome
            {
                DefId = plan.Def.Id,
                Title = plan.Def.Title,
                TargetRef = plan.Def.TargetRef,
                // Provenance travels with every outcome (accepted-ground-truth honesty), and the SQL text is the
                // def's own content — carried even when the run couldn't execute, so the evidence view can still
                // show WHAT would have been checked.
                CreatedBy = plan.Def.CreatedBy,
                CreatedWhen = plan.Def.CreatedWhen,
                BudgetMs = plan.Def.BudgetMs,
                Sql = plan.Request?.Sql,
                // Where it was authored is a RECORD; where it ran is this run's own current connection. Both
                // travel so the evidence can say they differ instead of implying the saved one was used.
                AuthoredAgainst = plan.Def.AuthoredAgainst,
                AuthoredAgainstLabel = plan.Def.AuthoredAgainstLabel,
                RanAgainstLabel = LiveTargetLabel(_live),
            };
            if (plan.Missing)
            {
                o.Missing = true; o.Verdict = Verdict.NotVerifiable;
                o.Message = "the measure this test was bound to no longer exists (or was recreated with a new identity). Re-bind or delete the test";
                return o;
            }
            if (plan.BindError != null) { o.Verdict = Verdict.NotVerifiable; o.Message = plan.BindError; return o; }
            // ---- SQL SOURCE RESOLUTION (the only place in this file that decides WHERE the ground truth is read) ----
            // The check's own choice wins (a named source, then its inline server / database / sign-in), so every
            // definition saved before named sources existed runs unchanged; a check that names nothing reads the
            // model's SQL endpoint (contract 19.3), and only then the old fallback of the model's own partitions. A source that has
            // been removed is reported HERE, before the live check, so the person is told what to fix rather than told
            // to connect. Everything downstream reads the request's own fields and never learns named sources exist.
            var sqlSource = SqlSources.Apply(plan.Request, EndpointForCheck(_sessions.Current, plan.Def?.Id, modelEndpointId));
            if (sqlSource.Error != null) { o.Verdict = Verdict.NotVerifiable; o.Message = sqlSource.Error; return o; }
            // ---- end SQL source resolution --------------------------------------------------------------------------
            // The settings this run is ABOUT to use, stamped now so a run that cannot execute still records what it
            // was going to ask with, and so a recorded run never has to be described by today's definition.
            o.Filter = string.IsNullOrWhiteSpace(plan.Request?.FilterDax) ? null : plan.Request.FilterDax.Trim();
            o.SourceLabel = SourceLabelFor(sqlSource);
            o.ToleranceNote = ToleranceSettingText(plan.Request?.ToleranceAbsolute, plan.Request?.ToleranceRelative, plan.Request?.BlankPolicy);
            if (!live) { o.Verdict = Verdict.NotVerifiable; o.Message = "reconciliation needs a live connection. Connect a live model in Connections and re-run"; return o; }

            // Resolved ONCE, above: the run executes exactly where it just reported, so a check pinned to the model's
            // partitions is never handed the model's endpoint on the way to the query.
            var r = await ReconcileMeasureCoreAsync(plan.Request, origin, sqlSource, plan.Binding, sqlTokens);
            o.Dax = r.DaxQuery;
            o.Rows = BuildCompareRows(r.Cells, out var rowsTotal);
            o.RowsTotal = rowsTotal;
            // The same three fields a trusted-answer check carries, so one contract serves both kinds. Here the
            // expected side is the SOURCE's grand total, and it is absent (not zero) when the comparison was
            // grouped with no grand-total cell: the per-context rows are the evidence in that case.
            var grandTotal = o.Rows.FirstOrDefault(x => x.GrandTotal);
            o.Expected = grandTotal?.Sql?.ToString(CultureInfo.InvariantCulture);
            o.Actual = grandTotal?.Dax?.ToString(CultureInfo.InvariantCulture);
            o.Difference = grandTotal?.Delta;
            o.Matches = r.Matches; o.Mismatches = r.Mismatches; o.Unverifiable = r.Unverifiable;
            o.DurationMs = r.DaxElapsedMs; o.SqlDurationMs = r.SqlElapsedMs;
            o.ToleranceNote = ComposeToleranceNote(r, plan.Request);
            // Grade on Status + the caveat facts (the DTO's own contract), never AnyMismatch alone. A CAVEATED
            // green (unverifiable cells / truncation) stays Pass — cells that were checked matched — with the
            // caveat carried in the message; too-little-verified is already its own status (InsufficientCoverage).
            switch (r.Status)
            {
                case ReconcileStatus.Reconciled:
                    o.Verdict = Verdict.Pass;
                    o.Message = r.Unverifiable == 0 && r.Complete ? r.Summary : $"{r.Summary} (caveated: {r.Unverifiable} unverifiable cell(s){(r.Complete ? "" : ", truncated")})";
                    break;
                case ReconcileStatus.Mismatch:
                    o.Verdict = Verdict.Fail;
                    o.Message = ComparisonCountLine(r.Mismatches, r.Matches, o.Rows.Length, o.RowsTotal) + " "
                        + (string.IsNullOrEmpty(r.WorstExplanation) ? r.Summary : $"{r.Summary} Worst: {r.WorstKey}: {r.WorstExplanation}");
                    break;
                default:
                    o.Verdict = Verdict.NotVerifiable;
                    o.Message = r.Error ?? r.Summary ?? "the run could not verify enough to certify";
                    break;
            }
            return o;
        }

        private async Task<ReconcileOutcome> RunMeasureValueDefAsync(TestDefinition def, TestObjectIdentityIndex identities, bool live, string origin)
        {
            var prepared = await PrepareMeasureValueAsync(def, identities, live);
            return prepared.Pending == null ? prepared.Outcome : await AskMeasureValueAsync(prepared.Pending, origin);
        }

        /// <summary>A trusted-number check that is ready to ask: the outcome so far, what it expects, the query it would run
        /// on its own, and (when it is a plain scalar) the one expression a batch can ask beside others.</summary>
        private sealed class MeasureValuePending
        {
            public ReconcileOutcome Outcome;
            public MeasureValueRequest Request;
            public string Dax;
            public string Scalar;
        }

        /// <summary>Everything a trusted-number check settles before it asks the model anything: the number it expects, the
        /// measure it binds to, the query it would send. A check that ends here (no number, no measure, refused scope, no
        /// connection) comes back with its outcome and no pending part; one that must ask comes back with both.</summary>
        private async Task<(ReconcileOutcome Outcome, MeasureValuePending Pending)> PrepareMeasureValueAsync(TestDefinition def, TestObjectIdentityIndex identities, bool live)
        {
            var request = string.IsNullOrWhiteSpace(def.ParamsJson) ? null : TestSuiteStore.Deserialize<MeasureValueRequest>(def.ParamsJson);
            var o = new ReconcileOutcome
            {
                DefId = def.Id,
                Title = def.Title,
                TargetRef = def.TargetRef,
                CreatedBy = def.CreatedBy,
                CreatedWhen = def.CreatedWhen,
                BudgetMs = def.BudgetMs,
                // The window this check allows, plus where its trusted number came from. Stamped up front so a
                // check that never gets to run still records the settings it was going to be judged on.
                ToleranceNote = ToleranceSettingText(request?.ToleranceAbsolute ?? InterviewScoring.OracleAbsTol,
                    request?.ToleranceRelative ?? InterviewScoring.OracleRelTol, null)
                    + (string.IsNullOrWhiteSpace(request?.Provenance) ? "" : " Where this number came from: " + request.Provenance + "."),
                AuthoredAgainst = def.AuthoredAgainst,
                AuthoredAgainstLabel = def.AuthoredAgainstLabel,
                RanAgainstLabel = LiveTargetLabel(_live),
            };
            if (request == null || string.IsNullOrWhiteSpace(request.ExpectedValue))
            {
                o.Verdict = Verdict.NotVerifiable;
                o.Message = "This expected-total test has no number to trust.";
                return (o, null);
            }
            // Say what the check is looking for from here on, whatever happens next: a check that could not run
            // still has an expected answer, and the page should be able to show it.
            o.Expected = request.ExpectedValue;
            var bound = await _sessions.Current.ReadAsync(m =>
            {
                Measure measure = null;
                if (!string.IsNullOrEmpty(def.TargetTag))
                    measure = m.Tables.SelectMany(t => t.Measures).FirstOrDefault(x => x.LineageTag == def.TargetTag);
                else if (!string.IsNullOrEmpty(def.TargetIdentity))
                {
                    var snap = identities.Resolve(def.TargetIdentity, TestObjectIdentityStore.Capture(m));
                    if (snap != null) measure = ReconcileBinding.ForNames(snap.TableName, snap.MeasureName).Find(m);   // by the recorded names, not by a ref that splits at its first '/'
                }
                else
                    measure = ObjectRefs.Resolve(m, def.TargetRef ?? request.MeasureRef) as Measure;
                if (measure == null) return (Missing: true, Name: (string)null, Empty: false, Ref: def.TargetRef);
                return (Missing: false, Name: measure.Name, Empty: string.IsNullOrWhiteSpace(measure.Expression), Ref: ObjectRefs.For(measure));
            });
            o.TargetRef = bound.Ref ?? o.TargetRef;
            if (bound.Missing)
            {
                o.Missing = true; o.Verdict = Verdict.NotVerifiable;
                o.Message = "the measure this test was bound to no longer exists (or was recreated with a new identity). Re-bind or delete the test";
                return (o, null);
            }
            var expression = request.ExpressionDax?.Trim();
            // An expression test checks DAX the model does not hold yet (the DAX Lab "Test this expression" path),
            // so the bound measure's own formula being empty is not this test's failure.
            if (bound.Empty && string.IsNullOrEmpty(expression))
            {
                o.Verdict = Verdict.Fail;
                o.Message = "This measure has no formula.";
                return (o, null);
            }
            var filter = !string.IsNullOrWhiteSpace(request.FilterDax) ? request.FilterDax.Trim()
                : !string.IsNullOrWhiteSpace(request.FilterColumn) && !string.IsNullOrWhiteSpace(request.FilterValue)
                    ? request.FilterColumn + " = \"" + request.FilterValue.Replace("\"", "\"\"") + "\""
                    : null;
            o.Filter = filter;
            var dax = BuildMeasureValueDax(bound.Name, expression, filter, out var refusal);
            var scalar = BuildMeasureValueScalar(bound.Name, expression, filter);
            // A query this check cannot scope has no verdict to give, so it is refused BEFORE the query is sent
            // and before the no-connection message: a check that can never be asked says so whatever is connected.
            if (refusal != null)
            {
                o.Verdict = Verdict.NotVerifiable;
                o.Message = refusal;
                return (o, null);
            }
            o.Dax = dax;
            if (!live)
            {
                o.Verdict = Verdict.NotVerifiable;
                o.Message = "Not run: no live connection, so the measure could not be asked.";
                return (o, null);
            }
            return (o, new MeasureValuePending { Outcome = o, Request = request, Dax = dax, Scalar = scalar });
        }

        /// <summary>Ask one trusted-number check its own query and judge the answer.</summary>
        private async Task<ReconcileOutcome> AskMeasureValueAsync(MeasureValuePending p, string origin)
        {
            ResultSet rs;
            try { rs = await RunDaxAsync(p.Dax, 8, origin); }
            catch (Exception ex)
            {
                p.Outcome.Verdict = Verdict.NotVerifiable;
                p.Outcome.Message = "Could not ask the measure: " + ex.Message;
                return p.Outcome;
            }
            return JudgeMeasureValue(p, rs);
        }

        /// <summary>The verdict for one trusted-number check from the answer it got. One path for a check asked on its own
        /// and a check answered inside a batch, so the words and the numbers cannot differ between them.</summary>
        private static ReconcileOutcome JudgeMeasureValue(MeasureValuePending p, ResultSet rs)
        {
            var o = p.Outcome;
            var request = p.Request;
            if (rs == null || rs.Error != null)
            {
                o.Verdict = Verdict.NotVerifiable;
                o.Message = rs?.Error ?? "Could not ask the measure.";
                return o;
            }
            // The check promised one number. Require that shape BEFORE any verdict: reading the first cell of a
            // table answers a question the check never asked, and the answer can look right by accident.
            var shapeProblem = MeasureValueShapeProblem(rs);
            if (shapeProblem != null)
            {
                o.Verdict = Verdict.NotVerifiable;
                o.Message = shapeProblem;
                return o;
            }
            object actual = null;
            if (rs.Rows != null && rs.Rows.Length > 0 && rs.Rows[0] != null && rs.Rows[0].Length > 0) actual = rs.Rows[0][0];
            var abs = request.ToleranceAbsolute ?? InterviewScoring.OracleAbsTol;
            var rel = request.ToleranceRelative ?? InterviewScoring.OracleRelTol;
            var match = MeasureValueMatches(actual, request.ExpectedValue, abs, rel);
            o.Verdict = match ? Verdict.Pass : Verdict.Fail;
            o.Message = match
                ? "The measure matched the number you trust."
                : "The measure did not match the number you trust.";
            // The three numbers travel as fields. NO compare row is built: there is no source side to put in one,
            // and the empty cell the old row carried is what made the page print "SQL result NULL".
            o.Actual = MeasureValueActualText(actual);
            o.Difference = MeasureValueDifference(actual, request.ExpectedValue);
            o.Matches = match ? 1 : 0;
            o.Mismatches = match ? 0 : 1;
            return o;
        }


        /// <summary>The most trusted-number checks asked in one query.</summary>
        internal const int MeasureValueBatchSize = 20;

        /// <summary>Run every trusted-number check of a run, in order, and return their outcomes in that same order. Plain
        /// scalar checks are asked in batches: ONE query, EVALUATE ROW with a column per check (each column is exactly the
        /// expression the check would send on its own), so 67 checks cost 4 round trips, not 67. The result is the same
        /// number the single query gives. Three things keep it that way. (1) A batch that errors, or comes back with the
        /// wrong shape, is thrown away and every check in it is asked on its own, so a bad check blanks only itself and
        /// says what it always said. (2) A check whose batched number does not match is asked on its own again, so a
        /// failing check shows the exact query and the exact answer a person can run. (3) A check that passes shows the
        /// batch query as its evidence, because that is the query that produced its number. Checks that are not a plain
        /// scalar (a complete query, a comment in the text) are never batched.</summary>
        private async Task<List<ReconcileOutcome>> RunMeasureValuesAsync(List<TestDefinition> defs, TestObjectIdentityIndex identities,
            bool live, string origin, Action step)
        {
            var prepared = new List<(ReconcileOutcome Outcome, MeasureValuePending Pending)>();
            foreach (var d in defs) prepared.Add(await PrepareMeasureValueAsync(d, identities, live));
            var outcomes = prepared.Select(x => x.Outcome).ToList();
            var done = new bool[prepared.Count];
            var batchable = Enumerable.Range(0, prepared.Count)
                .Where(i => prepared[i].Pending?.Scalar != null && !HasCommentMarker(prepared[i].Pending.Scalar)).ToList();
            for (var at = 0; at < batchable.Count; at += MeasureValueBatchSize)
            {
                var group = batchable.Skip(at).Take(MeasureValueBatchSize).ToList();
                if (group.Count < 2) continue;   // one check gains nothing from a batch: it is asked on its own below
                var query = BuildMeasureValueBatchDax(group.Select(i => prepared[i].Pending.Scalar).ToList());
                ResultSet rs = null;
                try { rs = await RunDaxAsync(query, 8, origin); } catch { }
                if (rs == null || rs.Error != null || rs.Rows == null || rs.Rows.Length != 1
                    || rs.Rows[0] == null || rs.Rows[0].Length != group.Count) continue;   // any trouble: every check in it goes on its own
                for (var k = 0; k < group.Count; k++)
                {
                    var p = prepared[group[k]].Pending;
                    var cell = rs.Rows[0][k];
                    var abs = p.Request.ToleranceAbsolute ?? InterviewScoring.OracleAbsTol;
                    var rel = p.Request.ToleranceRelative ?? InterviewScoring.OracleRelTol;
                    if (!MeasureValueMatches(cell, p.Request.ExpectedValue, abs, rel)) continue;   // not a pass: asked on its own below
                    JudgeMeasureValue(p, new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "[v]" } }, Rows = new[] { new[] { cell } }, RowCount = 1, ElapsedMs = rs.ElapsedMs,
                    });
                    p.Outcome.Dax = query;
                    done[group[k]] = true;
                    step();
                }
            }
            for (var i = 0; i < prepared.Count; i++)
            {
                if (done[i]) continue;
                if (prepared[i].Pending != null) await AskMeasureValueAsync(prepared[i].Pending, origin);
                step();
            }
            return outcomes;
        }

        /// <summary>One query for many plain scalar checks: a column per check, named v0, v1 and so on.</summary>
        internal static string BuildMeasureValueBatchDax(IReadOnlyList<string> scalars) =>
            "EVALUATE ROW(" + string.Join(",", scalars.Select((x, i) => "\n" + QUOTE + "v" + i + QUOTE + ", " + x)) + "\n)";

        /// <summary>A comment would swallow the text after it inside a shared query, so such a check is asked on its own.</summary>
        private static bool HasCommentMarker(string dax) =>
            dax.Contains("//") || dax.Contains("--") || dax.Contains("/*");

        /// <summary>The scalar a plain trusted-number check evaluates (the part after the column name in its ROW query), or null
        /// when the check is a complete query that is run as it is written. BuildMeasureValueDax is this plus the ROW wrapper.</summary>
        internal static string BuildMeasureValueScalar(string measureName, string expressionDax, string filterDax)
        {
            var expression = expressionDax?.Trim();
            var filter = string.IsNullOrWhiteSpace(filterDax) ? null : filterDax.Trim().TrimEnd(',', ' ', TAB, CR, LF);
            if (!string.IsNullOrEmpty(expression) && StartsQuery(expression)) return null;
            var scalar = string.IsNullOrEmpty(expression) ? "[" + (measureName ?? "").Replace("]", "]]") + "]" : expression;
            return filter == null ? scalar : "CALCULATE(" + scalar + ", " + filter + ")";
        }

        /// <summary>A trusted-answer check asks for ONE number, so a result that is not a single cell has no verdict
        /// to give. Judging the first cell of a table answers a question the check never asked, and it can look
        /// right by accident: a grouped visual query saved as a measure check compared the YEAR sitting in column
        /// one with the trusted number 2024 and read as a pass (Astra's spot review, 2026-09-14). Returns null when
        /// the result is a single cell, otherwise the plain-words reason the check could not be judged. Internal so
        /// the shape rule is provable offline, and shared by both doors because both run this one path.</summary>
        internal static string MeasureValueShapeProblem(ResultSet rs)
        {
            var rows = rs?.Rows ?? Array.Empty<object[]>();
            if (rows.Length == 0)
                return "Could not check: that expression returned no rows, so there is no number to compare. "
                    + "A measure check compares one number.";
            var columns = rs.Columns != null && rs.Columns.Length > 0
                ? rs.Columns.Length
                : rows.Max(r => r?.Length ?? 0);
            if (rows.Length == 1 && columns == 1) return null;
            var rowText = (rs.Truncated ? "at least " : "") + rows.Length + (rows.Length == 1 ? " row" : " rows");
            var columnText = columns + (columns == 1 ? " column" : " columns");
            return "Could not check: that expression returned a table of " + rowText + " by " + columnText
                + ". A measure check compares one number, so there is nothing here to compare with the number you "
                + "trust. Change the check to ask for a single value.";
        }

        /// <summary>The DAX one expected-total test asks. Four shapes, in priority: a complete EVALUATE query the
        /// author supplied with no filter of its own (run as written); the same query with a saved filter, scoped
        /// from OUTSIDE with CALCULATETABLE; an author-supplied scalar expression, narrowed by the saved filters;
        /// or the bound measure's own reference, narrowed the same way. Returns null and sets <paramref
        /// name="refusal"/> when the query cannot be scoped safely, so the caller refuses before any verdict.
        /// This used to run EVERY complete query verbatim on the claim that it already carried its own filter
        /// context. It does not: Astra (2026-09-14) saved EVALUATE ROW("v", [Total Sales] + 1) together with
        /// 'Date'[Year] = 2024, and the year was silently dropped, so the check compared the wrong scope with the
        /// trusted number. There is no out parameter free overload on purpose: a caller that cannot see the
        /// refusal is how the filter got dropped in the first place. Internal so the shape is provable offline.</summary>
        internal static string BuildMeasureValueDax(string measureName, string expressionDax, string filterDax, out string refusal)
        {
            refusal = null;
            var expression = expressionDax?.Trim();
            var filter = string.IsNullOrWhiteSpace(filterDax) ? null : filterDax.Trim().TrimEnd(',', ' ', TAB, CR, LF);
            if (!string.IsNullOrEmpty(expression) && StartsQuery(expression))
            {
                refusal = QueryScopeRefusal(expression, filter != null);
                if (refusal != null) return null;
                return filter == null
                    ? expression
                    : "EVALUATE CALCULATETABLE(" + QueryBody(expression) + ", " + filter + ")";
            }
            return "EVALUATE ROW(" + QUOTE + "v" + QUOTE + ", " + BuildMeasureValueScalar(measureName, expression, filter) + ")";
        }

        private const char TAB = (char)9;
        private const char LF = (char)10;
        private const char CR = (char)13;
        private const char QUOTE = (char)34;

        /// <summary>True when the text is a whole DAX query rather than a scalar expression: only DEFINE and
        /// EVALUATE can open one.</summary>
        private static bool StartsQuery(string text) =>
            FirstWord(text) is "EVALUATE" or "DEFINE";

        /// <summary>Why this query cannot be scoped from outside, in plain words, or null when it can. A query is
        /// scopeable when it is ONE EVALUATE and nothing else: its body is then a table expression, and
        /// CALCULATETABLE(body, filter) asks the same question inside the filter. A DEFINE block is a prefix, not
        /// an expression, so there is nothing to wrap; ORDER BY and START AT are trailing clauses that belong to
        /// the query and not to the table; a second EVALUATE is a second answer. A DEFINE block is refused even
        /// with no filter, because the old code fell through and wrapped it in ROW(), which is not valid DAX and
        /// reached the server as a syntax error instead of a reason.</summary>
        private static string QueryScopeRefusal(string query, bool hasFilter)
        {
            var words = TopLevelWords(query).Select(w => w.Word).ToList();
            var why = (string)null;
            if (words.Count > 0 && words[0] == "DEFINE")
                why = "its query starts with DEFINE";
            else if (words.Count(w => w == "EVALUATE") > 1)
                why = "its query has more than one EVALUATE";
            else if (words.Contains("ORDER"))
                why = "its query ends with ORDER BY";
            else if (words.Contains("START"))
                why = "its query uses START AT";
            if (why == null) return null;
            return hasFilter
                ? "Could not check: this check has a filter, but " + why + ", so a trusted-answer check cannot run it. "
                    + "Check the measure as saved instead, or give this check a single value such as CALCULATE([Sales], 'Date'[Year] = 2024)."
                : "Could not check: " + why + ", which a trusted-answer check cannot ask on its own. Ask it in DAX "
                    + "Lab instead, or give this check a single value such as CALCULATE([Sales], 'Date'[Year] = 2024).";
        }

        /// <summary>The table expression a one EVALUATE query asks, with the keyword taken off. Cut at the
        /// keyword's own end position rather than at a fixed offset, because a query may open with a comment
        /// ("// the 2024 total" on its own line) and chopping eight characters off THAT mangles the query.
        /// Only ever called once <see cref="QueryScopeRefusal"/> has agreed there is nothing else in the
        /// query.</summary>
        private static string QueryBody(string query)
        {
            var words = TopLevelWords(query);
            return words.Count == 0 ? (query ?? "").Trim() : (query ?? "").Substring(words[0].End).Trim();
        }

        private static string FirstWord(string text)
        {
            var words = TopLevelWords(text);
            return words.Count == 0 ? null : words[0].Word;
        }

        /// <summary>The upper-cased bare words of a DAX query that sit OUTSIDE every bracket, string literal,
        /// quoted name and comment, at paren depth zero, each with the index just past it. Structure only lives
        /// out here, so EVALUATE inside ROW("EVALUATE", ...) or 'Order By'[x] is read as the text it is. Built
        /// for the scope decision, so it is deliberately coarse: it names keywords, it does not parse
        /// DAX.</summary>
        private static List<(string Word, int End)> TopLevelWords(string text)
        {
            var words = new List<(string Word, int End)>();
            var s = text ?? "";
            var depth = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != LF) i++; continue; }
                if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != LF) i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == QUOTE) { i++; while (i < s.Length && !(s[i] == QUOTE && (i + 1 >= s.Length || s[i + 1] != QUOTE))) { if (s[i] == QUOTE) i++; i++; } continue; }
                if (c == SINGLE) { i++; while (i < s.Length && !(s[i] == SINGLE && (i + 1 >= s.Length || s[i + 1] != SINGLE))) { if (s[i] == SINGLE) i++; i++; } continue; }
                if (c == '[') { while (i < s.Length && s[i] != ']') i++; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (depth == 0 && (char.IsLetter(c) || c == '_'))
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    words.Add((s.Substring(start, i - start).ToUpperInvariant(), i));
                    i--;
                }
            }
            return words;
        }

        private const char SINGLE = (char)39;

        private static bool MeasureValueMatches(object actual, string expected, double absTol, double relTol)
        {
            if (InterviewScoring.IsBlankSentinel(expected)) return actual == null;
            if (actual == null) return false;
            if (double.TryParse(expected, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var want)
                && (actual is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            {
                var got = Convert.ToDouble(actual);
                return Math.Abs(got - want) <= Math.Max(absTol, relTol * Math.Abs(want));
            }
            return InterviewScoring.OracleMatches(actual, expected);
        }

        /// <summary>What the model actually returned, as text a person reads. A DAX BLANK is reported as blank
        /// rather than as an empty string, because "the measure came back blank" and "the run recorded nothing"
        /// are different answers and only one of them is this check's result.</summary>
        private static string MeasureValueActualText(object actual)
        {
            if (actual == null) return "blank";
            var number = ToDecimal(actual);
            return number.HasValue
                ? number.Value.ToString(CultureInfo.InvariantCulture)
                : Convert.ToString(actual, CultureInfo.InvariantCulture);
        }

        /// <summary>Actual minus expected, or null when either side is not a number (a blank result, or a trusted
        /// answer written as text). A missing difference is honest; a zero would claim the two sides agree.</summary>
        private static decimal? MeasureValueDifference(object actual, string expected)
        {
            var got = ToDecimal(actual);
            if (!got.HasValue) return null;
            return decimal.TryParse(expected, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var want)
                ? got.Value - want
                : (decimal?)null;
        }

        private static decimal? ToDecimal(object actual)
        {
            if (actual == null) return null;
            try { return Convert.ToDecimal(actual); } catch { return null; }
        }

        private static string JoinNotes(params string[] notes)
        {
            var parts = notes.Where(n => !string.IsNullOrEmpty(n)).ToArray();
            return parts.Length == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>Flatten a finished run into the per-check outcomes a recorded run keeps: every saved
        /// definition, every relationship check and every table row count, with its verdict, the two numbers it
        /// compared, the difference and its own words. Detail rows are the SAME rows the live run and the report
        /// show (capped at <see cref="CompareRowCap"/>), and the uncapped count travels beside them.</summary>
        private static TestRunOutcomes BuildRunOutcomes(TestSuiteRunResult run)
        {
            var checks = new List<RecordedCheck>();
            static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

            foreach (var o in run.Reconciles ?? Array.Empty<ReconcileOutcome>())
            {
                if (o == null) continue;
                checks.Add(new RecordedCheck
                {
                    Kind = "savedCheck",
                    DefId = o.DefId,
                    Name = string.IsNullOrWhiteSpace(o.Title) ? o.TargetRef : o.Title,
                    Verdict = o.Verdict,
                    Expected = o.Expected,
                    Actual = o.Actual,
                    Difference = o.Difference,
                    Note = o.Message,
                    // The settings AS THEY WERE. A compare-with-source check records the person's own SQL; a
                    // trusted-answer check records the DAX the engine actually ran, which is the query it asked.
                    Query = string.IsNullOrWhiteSpace(o.Sql) ? NullIfBlank(o.Dax) : o.Sql,
                    Filter = NullIfBlank(o.Filter),
                    Tolerance = NullIfBlank(o.ToleranceNote),
                    Source = NullIfBlank(o.SourceLabel),
                    // How long each side took, as the page shows it. A check that sent no query on a side has no time for it.
                    DaxMs = o.DurationMs > 0 ? o.DurationMs : (long?)null,
                    SqlMs = o.SqlDurationMs > 0 ? o.SqlDurationMs : (long?)null,
                    ContextsTotal = o.RowsTotal,
                    Contexts = (o.Rows ?? Array.Empty<CompareRow>()).Where(r => r != null).Select(r => new RecordedContext
                    {
                        Context = r.Context,
                        Expected = r.Sql,
                        Actual = r.Dax,
                        Difference = r.Delta,
                        Verdict = r.Verdict,
                        Note = r.Explanation,
                        GrandTotal = r.GrandTotal,
                    }).ToArray(),
                });
            }

            foreach (var rel in run.Relationships?.Relationships ?? Array.Empty<RelationshipResult>())
            {
                if (rel == null) continue;
                foreach (var check in rel.Checks ?? Array.Empty<CheckResult>())
                {
                    if (check == null) continue;
                    checks.Add(new RecordedCheck
                    {
                        Kind = "relationship",
                        Name = rel.Name,
                        Check = check.Check,
                        Verdict = check.Verdict,
                        // A check that COUNTS bad rows passes at zero of them, so zero is a real expected answer.
                        // A check that compares data types counts nothing, and leaves both sides absent.
                        Expected = check.Count.HasValue ? "0" : null,
                        Actual = check.Count?.ToString(CultureInfo.InvariantCulture),
                        Difference = check.Count,
                        Note = string.IsNullOrWhiteSpace(check.RootCause) ? check.Message : check.Message + " " + check.RootCause,
                    });
                }
            }

            foreach (var table in run.Relationships?.TableRowCounts ?? Array.Empty<TableRowCountResult>())
            {
                if (table == null) continue;
                checks.Add(new RecordedCheck
                {
                    Kind = "tableRowCount",
                    Name = table.ModelTable,
                    Verdict = table.Check?.Verdict ?? Verdict.NotVerifiable,
                    Expected = table.SourceCount?.ToString(CultureInfo.InvariantCulture),
                    Actual = table.ModelCount?.ToString(CultureInfo.InvariantCulture),
                    Difference = table.ModelCount.HasValue && table.SourceCount.HasValue
                        ? table.ModelCount.Value - table.SourceCount.Value
                        : (decimal?)null,
                    Note = table.Check?.Message,
                    // Which source the count was read from, and which table in it, as they were at the time.
                    Source = NullIfBlank(table.SqlSourceName) ?? NullIfBlank(table.Database),
                    Query = string.IsNullOrWhiteSpace(table.Schema) || string.IsNullOrWhiteSpace(table.Entity)
                        ? null : table.Schema + "." + table.Entity,
                });
            }

            return new TestRunOutcomes
            {
                Checks = checks.ToArray(),
                ContextCap = CompareRowCap,
                Note = "Each check keeps up to " + CompareRowCap.ToString(CultureInfo.InvariantCulture)
                    + " rows of detail, the same number the report shows. Where a check had more, the full count is"
                    + " kept beside them so the saved run never looks smaller than it was.",
            };
        }

        // ---- evidence flattening -----------------------------------------------------------------------

        private const int CompareRowCap = 20;

        /// <summary>Flatten the reconciler's per-cell verdicts for the UI's compare table: grand total first,
        /// then mismatches worst-first (by relative delta), then the rest in arrival order (OrderBy is stable),
        /// capped — with the uncapped count out so "showing N of M" stays honest. Cell verdicts are mapped to
        /// the SHARED vocabulary (a cell is never Suspect); the nuance stays in the explanation.</summary>
        private static CompareRow[] BuildCompareRows(CellVerdict[] cells, out int total)
        {
            var safe = (cells ?? Array.Empty<CellVerdict>()).Where(cv => cv != null).ToList();
            total = safe.Count;
            if (safe.Count == 0) return Array.Empty<CompareRow>();
            return safe
                .OrderByDescending(cv => IsGrandTotalCell(cv) ? 1 : 0)
                .ThenByDescending(cv => cv.Verdict == ReconcileVerdict.Mismatch ? 1 : 0)
                .ThenByDescending(cv => cv.RelDelta ?? 0)
                .Take(CompareRowCap)
                .Select(cv => new CompareRow
                {
                    Context = cv.Cell == null ? "Invalid input"
                        : IsGrandTotalCell(cv) ? "Grand total"
                        : string.Join(" · ", cv.Cell.GroupingKey),
                    GrandTotal = IsGrandTotalCell(cv),
                    Sql = cv.Cell?.Sql,
                    Dax = cv.Cell?.Dax,
                    Delta = cv.Delta,
                    Verdict = cv.Verdict == ReconcileVerdict.Match ? Verdict.Pass
                        : cv.Verdict == ReconcileVerdict.Mismatch ? Verdict.Fail
                        : Verdict.NotVerifiable,
                    Explanation = cv.Explanation,
                })
                .ToArray();
        }

        private static bool IsGrandTotalCell(CellVerdict cv) =>
            cv.Cell != null && (cv.Cell.GroupingKey == null || cv.Cell.GroupingKey.Length == 0);

        /// <summary>The effective match window + blank policy in the words a person reads, so a green is
        /// auditable at a glance. The loose-window warning is carried INTO the note (never a silent green at 5%
        /// tolerance).
        ///
        /// It used to read "match window: 1e-7 relative to ground truth, 1.2e-6 absolute floor", and it
        /// OVERWROTE the precise setting text the moment a reconciliation returned. Two things were wrong with
        /// that (Astra, 2026-09-15, round two): the formatter rounded a small number to two significant digits,
        /// so a check set to 0.000001234567 recorded 1.2e-6 and History could not recover what it had allowed;
        /// and a run that could NOT finish carries no window at all, so the overwrite replaced the check's real
        /// limits with the empty result's zeros. One sentence now, in plain decimals, falling back to what the
        /// check is SET to whenever the run never established its own.</summary>
        private static string ComposeToleranceNote(ReconcileRunResult r, ReconcileRequest request)
        {
            // BlankPolicy is filled only once the reconciliation has parsed its inputs and started work, so its
            // absence is the honest signal that this run never got far enough to have an effective window.
            var measured = r != null && !string.IsNullOrEmpty(r.BlankPolicy);
            var note = measured
                ? ToleranceSettingText(r.ToleranceAbsolute, r.ToleranceRelative, r.BlankPolicy)
                : ToleranceSettingText(request?.ToleranceAbsolute, request?.ToleranceRelative, request?.BlankPolicy);
            if (r != null && r.SuspiciouslyLoose)
                note += " Warning: this window is loose enough to hide a real error.";
            return note;
        }

        /// <summary>Test seam. Reaching the executed reconciliation needs a real warehouse, and a degraded live
        /// gate is never reported as a pass, so the FORMATTER that path uses is exercised directly.</summary>
        public static string ComposeToleranceNoteForTest(ReconcileRunResult result, ReconcileRequest request)
            => ComposeToleranceNote(result, request);

        /// <summary>The tolerance a check is SET to, from its own request, said the way a person reads a number.
        /// <see cref="ComposeToleranceNote"/> describes a finished reconciliation; this describes the setting, so
        /// a check that never got to run still records what it would have allowed. No scientific notation: the
        /// number a person typed is the number they should read back.</summary>
        private static string ToleranceSettingText(double? absolute, double? relative, string blankPolicy)
        {
            var abs = absolute ?? 0;
            var rel = relative ?? 0;
            // Both vocabularies: the request's own words ("zero") and the reconciler's enum name
            // ("BlankIsZero"). One sentence has to serve a check's SETTING and a finished run's window.
            var blank = blankPolicy switch
            {
                "zero" or nameof(BlankPolicy.BlankIsZero) => "blanks read as zero",
                "null" or nameof(BlankPolicy.BlankIsNull) => "blanks read as no value",
                "distinct" or nameof(BlankPolicy.BlankIsDistinct) => "blanks kept distinct from zero and null",
                _ => null,
            };
            string window;
            if (abs == 0 && rel == 0) window = "Allowed difference: none. The numbers must match exactly.";
            else if (rel == 0) window = "Allowed difference: " + PlainNumber(abs) + ".";
            else if (abs == 0) window = "Allowed difference: " + PlainNumber(rel, 2) + "%.";
            else window = "Allowed difference: " + PlainNumber(abs) + " or " + PlainNumber(rel, 2) + "%, whichever is larger.";
            return blank == null ? window : window + " " + char.ToUpperInvariant(blank[0]) + blank.Substring(1) + ".";
        }

        /// <summary>A number written out in full, never as 1E-09 and never rounded.
        ///
        /// It used to take fifteen significant digits and stop at twenty decimal places, which is fine for a
        /// tolerance someone typed and wrong for one the engine ACCEPTS: 1e-21 recorded as "0" and
        /// 0.12345678901234566 lost its tail, on the completed and the failed path alike (Astra, round three).
        /// ReconcileMeasureAsync takes any finite, non-negative tolerance, so a limit that is written down must
        /// survive being written down.
        ///
        /// So this expands the ROUND-TRIP representation (the shortest string that reads back as the same
        /// double) rather than reformatting the value. <paramref name="shiftRight"/> moves the decimal point in
        /// that expansion, which is how a fraction becomes a percent: multiplying by 100 in floating point turns
        /// 1e-7 into 9.999999999999999e-06, and no amount of later rounding can tell that apart from a number
        /// the person actually meant.</summary>
        private static string PlainNumber(double value, int shiftRight = 0)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(culture);

            var text = value.ToString("R", culture);
            var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
            var exponent = shiftRight;
            if (exponentAt >= 0)
            {
                exponent += int.Parse(text.Substring(exponentAt + 1), culture);
                text = text.Substring(0, exponentAt);
            }

            var negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative) text = text.Substring(1);
            var pointAt = text.IndexOf('.');
            var digits = pointAt < 0 ? text : text.Remove(pointAt, 1);
            var point = (pointAt < 0 ? text.Length : pointAt) + exponent;

            string plain;
            if (point <= 0) plain = "0." + new string('0', -point) + digits;
            else if (point >= digits.Length) plain = digits + new string('0', point - digits.Length);
            else plain = digits.Substring(0, point) + "." + digits.Substring(point);

            if (plain.IndexOf('.') >= 0) plain = plain.TrimEnd('0').TrimEnd('.');
            plain = plain.TrimStart('0');
            if (plain.Length == 0 || plain[0] == '.') plain = "0" + plain;
            return negative && plain != "0" ? "-" + plain : plain;
        }

        /// <summary>Which SQL source a run RESOLVED to, by the name it had then. A named source reads as its name;
        /// a check carrying its own coordinates reads as the database it pointed at. Null when the check asks no
        /// database at all, which is every trusted-answer check.</summary>
        private static string SourceLabelFor(SqlSourceResolution resolved)
        {
            if (resolved == null) return null;
            if (!string.IsNullOrWhiteSpace(resolved.SourceName)) return resolved.SourceName;
            if (!string.IsNullOrWhiteSpace(resolved.Database)) return resolved.Database;
            return string.IsNullOrWhiteSpace(resolved.Server) ? null : resolved.Server;
        }

        // ---- E6: time-intelligence variants -------------------------------------------------------------

        private const int TiIdentityProbeBudget = 24;

        private sealed class TiDateRange
        {
            public DateTime Min;
            public DateTime Max;
            public string Reason;
        }

        private async Task ExecuteVariantPassAsync(
            LiveConnection live, List<ReconcilePlan> plans, List<ReconcileOutcome> outcomes)
        {
            var ranges = new Dictionary<string, TiDateRange>(StringComparer.OrdinalIgnoreCase);
            var identityProbes = 0;
            foreach (var plan in plans)
            {
                var outcome = outcomes.FirstOrDefault(o => o.DefId == plan.Def.Id);
                if (outcome == null || plan.Variants.Count == 0) continue;
                var verdicts = new List<TiVariantVerdict>();
                foreach (var variant in plan.Variants)
                {
                    var c = variant.Classification;
                    if (c.Kind == TiVariantKind.Unrecognized)
                    {
                        verdicts.Add(TimeIntelligenceVariants.Judge(c, variant.Measure, null, null, false));
                        continue;
                    }
                    if (live == null)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "no live connection, so the identity query could not run"));
                        continue;
                    }
                    if (identityProbes >= TiIdentityProbeBudget)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "probe budget exhausted for this run"));
                        continue;
                    }

                    if (!ranges.TryGetValue(c.DateColumnRef, out var range))
                    {
                        range = await ProbeTiDateRangeAsync(live, c.DateColumnRef);
                        ranges[c.DateColumnRef] = range;
                    }
                    if (range.Reason != null)
                    {
                        verdicts.Add(UnverifiableVariant(variant, range.Reason));
                        continue;
                    }
                    var period = TimeIntelligenceVariants.SelectSamplePeriod(range.Min, range.Max, c.Kind);
                    if (!period.Verifiable)
                    {
                        verdicts.Add(UnverifiableVariant(variant, period.Reason));
                        continue;
                    }

                    TiIdentityQuery query;
                    try
                    {
                        query = TimeIntelligenceVariants.BuildIdentityQuery(c, variant.Measure,
                            period.PeriodStart, period.PeriodEnd, period.PriorStart, period.PriorEnd);
                    }
                    catch (Exception ex)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "identity query could not be built: " + ex.Message));
                        continue;
                    }

                    identityProbes++;
                    ResultSet rs;
                    try { rs = await live.ExecuteAsync(query.Dax, 2, 60); }
                    catch (Exception ex)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "identity query failed: " + ex.Message));
                        continue;
                    }
                    if (!string.IsNullOrEmpty(rs.Error))
                    {
                        verdicts.Add(UnverifiableVariant(variant, "identity query failed: " + rs.Error));
                        continue;
                    }
                    if (rs.Rows == null || rs.Rows.Length == 0 || rs.Rows[0] == null || rs.Rows[0].Length < 2)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "identity query did not return both values"));
                        continue;
                    }

                    var variantValue = ReconcileCoercion.Coerce(rs.Rows[0][0]);
                    var expectedValue = ReconcileCoercion.Coerce(rs.Rows[0][1]);
                    if (variantValue.Unsupported || expectedValue.Unsupported)
                    {
                        verdicts.Add(UnverifiableVariant(variant, "identity query did not return numeric values"));
                        continue;
                    }
                    verdicts.Add(TimeIntelligenceVariants.Judge(c, variant.Measure,
                        variantValue.Empty ? null : variantValue.Value,
                        expectedValue.Empty ? null : expectedValue.Value,
                        executed: true));
                }
                outcome.Variants = verdicts.ToArray();
            }
        }

        private static async Task<TiDateRange> ProbeTiDateRangeAsync(LiveConnection live, string dateColumnRef)
        {
            var dax = $"EVALUATE ROW(\"__ti_min\", MIN({dateColumnRef}), \"__ti_max\", MAX({dateColumnRef}))";
            ResultSet rs;
            try { rs = await live.ExecuteAsync(dax, 2, 60); }
            catch (Exception ex) { return new TiDateRange { Reason = "date range probe failed: " + ex.Message }; }
            if (!string.IsNullOrEmpty(rs.Error))
                return new TiDateRange { Reason = "date range probe failed: " + rs.Error };
            if (rs.Rows == null || rs.Rows.Length == 0 || rs.Rows[0] == null || rs.Rows[0].Length < 2
                || !TryTiDate(rs.Rows[0][0], out var min) || !TryTiDate(rs.Rows[0][1], out var max))
                return new TiDateRange { Reason = "the date column did not return dates" };
            return new TiDateRange { Min = min.Date, Max = max.Date };
        }

        private static bool TryTiDate(object value, out DateTime date)
        {
            if (value is DateTime typed) { date = typed; return true; }
            if (value is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed)) { date = parsed; return true; }
            date = default;
            return false;
        }

        private static TiVariantVerdict UnverifiableVariant(ReconcileVariantPlan variant, string reason)
            => new TiVariantVerdict
            {
                Variant = variant.Measure,
                Kind = variant.Classification.Kind,
                Verdict = Verdict.NotVerifiable,
                Explanation = "Not verifiable: " + (reason ?? "the identity could not be checked").TrimEnd('.') + ".",
            };

        // ---- E5: the opt-in clear-cache timing pass --------------------------------------------------------

        /// <summary>Times each BUDGETED test's measure with the ratified discipline: clear the storage-engine
        /// cache, run once, judge against the test's own budget (never a default — no budget, no judgement, and
        /// the Performance category stays dormant). A refused or failed cache clear degrades that measure to
        /// NotVerifiable rather than selling a warm number as cold. The run's QueryData gate was already answered
        /// for this suite run, so there is no second ask here.</summary>
        private static List<ReconcilePlan> BudgetedPlans(List<ReconcilePlan> plans) => plans
            .Where(p => !p.Missing && p.BindError == null && !string.IsNullOrWhiteSpace(p.KeyName)
                        && p.Def.BudgetMs.HasValue && p.Def.BudgetMs.Value > 0)
            .ToList();

        private async Task<(List<Verdict> Verdicts, bool CacheCleared)> ExecuteTimingPassAsync(
            LiveConnection live, List<ReconcilePlan> plans, List<ReconcileOutcome> outcomes)
        {
            var budgeted = BudgetedPlans(plans);
            if (budgeted.Count == 0) return (new List<Verdict>(), false);

            // One target per distinct measure; when several tests share a measure the TIGHTEST budget judges it
            // (the strictest declared expectation is the honest bar).
            var byMeasure = budgeted
                .GroupBy(p => p.KeyName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var policy = new TimingPolicy
            {
                TargetMs = byMeasure.Values.Min(l => l.Min(p => p.Def.BudgetMs.Value)),   // fallback only; every measure has an override
                Overrides = byMeasure.ToDictionary(kv => kv.Key, kv => kv.Value.Min(p => p.Def.BudgetMs.Value), StringComparer.OrdinalIgnoreCase),
            };
            var targets = byMeasure.Keys.Select(name => new TimingTarget(name, null)).ToList();

            var runs = new List<MeasureTimingRun>();
            // The run-level flag claims "cache cleared for timing" to the user, so it must mean ALL timed
            // evaluations ran cold — one refused clear among three measures is not "cleared" (the per-measure
            // verdicts stay honest either way: a failed clear degrades ITS measure to NotRun below).
            var anyTimed = false;
            var allCleared = true;
            var lastClearOk = false;
            foreach (var step in TimingPlan.BuildPlan(targets, policy))
            {
                if (step.Kind == TimingStepKind.ClearCache)
                {
                    var cc = await DaxCache.ClearAsync(live);
                    lastClearOk = cc.Cleared;
                    if (!cc.Cleared) allCleared = false;
                    continue;
                }
                if (!lastClearOk)
                {
                    runs.Add(MeasureTimingRun.NotRun(step.Measure, step.HomeTable));
                    continue;
                }
                anyTimed = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ResultSet rs;
                try { rs = await live.ExecuteAsync(step.Dax, 2, (int)Math.Max(1, step.TimeoutMs / 1000)); }
                catch (Exception ex)
                {
                    // A thrown ExecuteAsync (disposal / dispatcher teardown mid-run) must cost ONE measure,
                    // never the whole suite run: judged as a broken evaluation, loudly, in place.
                    sw.Stop();
                    runs.Add(new MeasureTimingRun
                    {
                        Measure = step.Measure, HomeTable = step.HomeTable, Ran = true,
                        DurationMs = sw.ElapsedMilliseconds, Success = false, Error = ex.Message,
                    });
                    continue;
                }
                sw.Stop();
                runs.Add(new MeasureTimingRun
                {
                    Measure = step.Measure,
                    HomeTable = step.HomeTable,
                    Ran = true,
                    DurationMs = rs.ElapsedMs > 0 ? rs.ElapsedMs : sw.ElapsedMilliseconds,
                    Success = string.IsNullOrEmpty(rs.Error),
                    Error = rs.Error,
                    // ExecuteAsync surfaces a timeout as an error string, not a flag — attribute it to the
                    // ceiling only when the wall clock actually reached it (a best-effort read, stated as such).
                    TimedOut = !string.IsNullOrEmpty(rs.Error) && sw.ElapsedMilliseconds >= step.TimeoutMs,
                });
            }

            var report = MeasureTiming.Summarize(targets, runs, policy);
            foreach (var v in report.Verdicts)
            {
                if (!byMeasure.TryGetValue(v.Measure, out var plansForMeasure)) continue;
                foreach (var p in plansForMeasure)
                {
                    var o = outcomes.FirstOrDefault(x => x.DefId == p.Def.Id);
                    if (o == null) continue;
                    o.TimingVerdict = MapTimingVerdict(v.Verdict);
                    o.TimingDetail = v.Detail;
                    o.BudgetMs = v.TargetMs;
                    // The judged cold number replaces the warm reconcile query time in the timing column
                    // (>= 0: a genuine sub-millisecond cold run is a real measurement, not an absence).
                    if (v.Verifiable && v.DurationMs >= 0) o.DurationMs = v.DurationMs;
                }
            }
            return (report.Verdicts.Select(v => MapTimingVerdict(v.Verdict)).ToList(), anyTimed && allCleared);
        }

        private static Verdict MapTimingVerdict(TimingVerdict v) => v switch
        {
            TimingVerdict.Pass => Verdict.Pass,
            TimingVerdict.Fail => Verdict.Fail,
            TimingVerdict.Suspect => Verdict.Suspect,
            _ => Verdict.NotVerifiable,
        };
    }
}
