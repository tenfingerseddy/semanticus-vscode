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
        public SecurityStaticReport Security { get; set; }
        public ReconcileOutcome[] Reconciles { get; set; } = Array.Empty<ReconcileOutcome>();
        public int DefinitionCount { get; set; }
        public bool Persisted { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
        public long DurationMs { get; set; }              // whole-run wall clock
        public string Environment { get; set; }           // where the probes ran (database on server); null offline
        public bool CacheCleared { get; set; }            // the timing pass cleared the SE cache before its runs
        public InterviewEvidence[] Interview { get; set; } = Array.Empty<InterviewEvidence>();
        public string InterviewNote { get; set; }
    }

    /// <summary>Evidence-only interview snapshot. It is rendered/exported but never enters health analysis.</summary>
    public sealed class InterviewEvidence
    {
        public string QuestionId { get; set; }
        public string Question { get; set; }
        public string Tier { get; set; }
        public string ReplayStatus { get; set; }          // replayed | chat-only
        public string Outcome { get; set; }
        public string PreviousOutcome { get; set; }
        public bool Changed { get; set; }
        public string Detail { get; set; }
        public string When { get; set; }
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
    }

    /// <summary>The LEAN persisted history line (the drift-trend substrate): health + context, never the
    /// per-cell evidence — evidence is the live run's UI; history is trends (the VitalsStore cap discipline).</summary>
    public sealed class TestRunRecord
    {
        public int SchemaVersion { get; set; } = TestSuiteStore.SchemaVersion;
        public string RunId { get; set; }
        public string When { get; set; }
        public string ModelFingerprint { get; set; }
        public bool Live { get; set; }
        public TestHealth Health { get; set; }
    }

    public sealed class TestHistoryInfo
    {
        public TestRunRecord[] Runs { get; set; } = Array.Empty<TestRunRecord>();
        public string Note { get; set; }
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

        public async Task<TestSuiteRunResult> RunTestSuiteAsync(bool persist = false, string origin = "human")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = _sessions.Current;
            if (s == null) return new TestSuiteRunResult { Error = "No open model. Use open_model, connect_local or connect_xmla first." };

            var dir = TestsDirFor(s);
            var loaded = await LoadScopedTestDefinitionsAsync(s);
            var defs = loaded.Defs;
            var badDefs = loaded.Unreadable;
            var identities = loaded.Identities;

            // ONE model read builds every pure input (relationship endpoints + role filters + OLS visibility + def bindings).
            var (relInputs, tableInputs, secInputs, olsSummaries, reconPlans, unmappedMeasures, modelName) = await s.ReadAsync(m =>
            {
                var plans = BindReconcileDefs(m, defs, identities);
                return (BuildRelationshipInputs(m), TableRowCountReconciliation.Discover(m).ToList(), BuildSecurityInputs(m), BuildOlsSummaries(m), plans,
                    BuildUnmappedMeasureOutcomes(m, plans),
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

            if (live != null)
            {
                foreach (var rel in relInputs)
                    rel.Probe = await ExecuteRelationshipProbesAsync(live, rel);
                var sqlTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var table in tableInputs)
                    await ExecuteTableRowCountAsync(live, table, origin, sqlTokens);
            }

            var relReport = RelationshipIntegrity.Evaluate(relInputs);
            relReport.TableRowCounts = tableInputs.Select(TableRowCountReconciliation.Evaluate).ToArray();
            var secReport = SecurityStaticChecks.Evaluate(secInputs);
            secReport.Ols = olsSummaries;   // informational visibility read: no verdicts, never moves the grade

            var outcomes = new List<ReconcileOutcome>(unmappedMeasures);
            foreach (var plan in reconPlans)
                outcomes.Add(await RunReconcileDefAsync(plan, live != null, origin));
            // An enabled definition whose kind has no evaluator yet must NOT vanish from the run (the suite
            // rots visibly, never silently): it surfaces as NotVerifiable with the reason. Covers the
            // stored-now-evaluated-later rowLevelAssertion kind and any kind from a newer engine's store.
            foreach (var d in defs.Where(d => d.Enabled && !string.Equals(d.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase)))
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
                .Where(p => !string.IsNullOrWhiteSpace(p.Request?.MeasureRef))
                .ToDictionary(p => p.Def.Id, p => (p.Request.MeasureRef, (IReadOnlyList<string>)p.DependsOnBound)));

            // E5: the OPT-IN clear-cache timing pass — only tests that DECLARED a budget are timed-judged, so
            // the Performance category can never activate (and move the grade) by surprise.
            var timingVerdicts = new List<Verdict>();
            var cacheCleared = false;
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
            await ExecuteVariantPassAsync(live, reconPlans, outcomes);

            var interviewReplay = await ReplayInterviewEvidenceForTestsAsync(origin);

            // T71: interview is behavioral evidence, not a fifth verdict family. Keeping it outside this call is
            // the mechanical guard that unasked/unverified questions cannot move grade or coverage (I1).
            var health = TestHealthAnalyzer.Analyze(relReport, secReport, outcomes, timingVerdicts);
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
                Interview = interviewReplay.Evidence,
                InterviewNote = interviewReplay.Note,
                Health = health,
                Relationships = relReport,
                Security = secReport,
                Reconciles = outcomes.ToArray(),
                DefinitionCount = defs.Count,
                Note = JoinNotes(
                    badDefs > 0 ? $"{badDefs} unreadable line(s) in the saved suite were skipped." : null,
                    loaded.Note,
                    gateNote,
                    live == null && gateNote == null ? "Offline: relationship probes and reconciliations are not verifiable. Connect (connect_xmla or connect_local) for the full run." : null),
            };

            if (persist)
            {
                if (_entitlement == null || !_entitlement.IsPro)
                    run.Note = JoinNotes(run.Note, "Run history is Pro. This run was evaluated in full but not stored (everything above is free).");
                else if (dir == null)
                    run.Note = JoinNotes(run.Note, "This session has no on-disk home (.semanticus), so the run could not be stored.");
                else
                    run.Persisted = TestSuiteStore.AppendRun(dir, TestSuiteStore.Serialize(new TestRunRecord
                    {
                        RunId = run.RunId, When = run.When, ModelFingerprint = run.ModelFingerprint, Live = run.Live, Health = health,
                    }));
            }
            run.DurationMs = sw.ElapsedMilliseconds;
            _lastTestRun = run;
            return run;
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
            if (_entitlement == null || !_entitlement.IsPro)
                return Task.FromResult(new TestReportResult
                {
                    Note = "The signable test report is Pro. Running the suite and reviewing all evidence stays free.",
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
            var s = _sessions.Current;
            if (s == null) return new TestSuiteInfo { Note = "No open model." };
            var loaded = await LoadScopedTestDefinitionsAsync(s);
            return new TestSuiteInfo { Definitions = loaded.Defs.ToArray(), UnreadableLines = loaded.Unreadable, Note = loaded.Note };
        }

        /// <summary>Upsert a saved definition. The PERSISTED suite is the Pro side of the ratified line (the
        /// ambient suite runs free forever). Ground truth is AI-drafted, HUMAN-ACCEPTED — callers surface the
        /// SQL to the user before saving (the MCP description says so; the UI makes acceptance explicit).</summary>
        public async Task<TestDefinition> SaveTestDefinitionAsync(TestDefinition def, string origin = "human")
        {
            Entitlement.EntitlementGuard.RequirePro(_entitlement, "Saving a persisted test suite",
                "run_tests runs the ambient suite (relationship integrity + static security) free anytime; saving named tests with expected values needs Pro.");
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model. Use open_model or connect first.");
            var dir = TestsDirFor(s) ?? throw new InvalidOperationException(
                "This session has no on-disk home. Save the model (save_model) so the .semanticus sidecar exists, then retry.");
            if (def == null || string.IsNullOrWhiteSpace(def.Title)) throw new ArgumentException("A test needs at least a title.");
            def.ModelIdentity = PaneIdentity(s, null) ?? throw new InvalidOperationException(
                "This unsaved model has no durable identity. Save it (save_model) or open/connect the model before saving a SQL mapping.");
            // Refuse an unknown kind AT SAVE (sol review): a typo'd kind would store fine and then never run,
            // which reads as coverage that does not exist. Known-but-not-yet-runnable kinds ARE saveable (the
            // run surfaces them NotVerifiable with the reason), so a suite can be authored ahead of its evaluator.
            var knownKinds = new[] { TestKinds.MeasureReconcile, TestKinds.RowLevelAssertion };
            if (!knownKinds.Any(k => string.Equals(k, def.Kind, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"Unknown test kind '{def.Kind}'. Use '{TestKinds.MeasureReconcile}' (SQL vs DAX reconciliation; paramsJson carries the ReconcileRequest) or '{TestKinds.RowLevelAssertion}' (stored now, runs when view-as-role ships).");
            def.Kind = knownKinds.First(k => string.Equals(k, def.Kind, StringComparison.OrdinalIgnoreCase));   // canonical casing
            if (string.Equals(def.Kind, TestKinds.MeasureReconcile, StringComparison.OrdinalIgnoreCase))
            {
                var request = string.IsNullOrWhiteSpace(def.ParamsJson) ? null : TestSuiteStore.Deserialize<ReconcileRequest>(def.ParamsJson);
                var candidateRef = !string.IsNullOrWhiteSpace(def.TargetRef) ? def.TargetRef : request?.MeasureRef;
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
            var stored = TestSuiteStore.Upsert(dir, def, () => Guid.NewGuid().ToString("N"))
                ?? throw new InvalidOperationException("The test could not be written to .semanticus/tests/suite.jsonl.");
            return stored;
        }

        public async Task<bool> DeleteTestDefinitionAsync(string id, string origin = "human")
        {
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model.");
            var scoped = await LoadScopedTestDefinitionsAsync(s);
            if (!scoped.Defs.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal))) return false;
            return TestSuiteStore.Remove(TestsDirFor(s), id);
        }

        /// <summary>Run history (Pro — the drift-trend substrate), fingerprint-filtered to the open model.
        /// Free callers get the teaching note, never an error: the HISTORY is gated, knowing of it is not.</summary>
        public Task<TestHistoryInfo> ListTestRunsAsync(int last = 20)
        {
            var s = _sessions.Current;
            if (s == null) return Task.FromResult(new TestHistoryInfo { Note = "No open model." });
            if (_entitlement == null || !_entitlement.IsPro)
                return Task.FromResult(new TestHistoryInfo { Note = "Run history and drift trends are Pro. run_tests itself is free and shows every verdict live." });
            var fp = VitalsFingerprintFor(s);
            var runs = TestSuiteStore.ReadRunLines(TestsDirFor(s))
                .Select(TestSuiteStore.Deserialize<TestRunRecord>)
                .Where(r => r != null)
                .Where(r => string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(r.ModelFingerprint) || string.Equals(fp, r.ModelFingerprint, StringComparison.Ordinal))
                .ToList();
            var take = last <= 0 ? 20 : last;
            return Task.FromResult(new TestHistoryInfo { Runs = runs.Skip(Math.Max(0, runs.Count - take)).ToArray() });
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

        private static List<RoleFilterInput> BuildSecurityInputs(Model m)
            => m.Roles.SelectMany(role => role.TablePermissions.Select(tp => new RoleFilterInput
            {
                Role = role.Name,
                Table = tp.Table?.Name,
                FilterExpression = tp.FilterExpression,
                ErrorMessage = tp.ErrorMessage,
            })).ToList();

        /// <summary>Execute the six probe queries for one relationship. Each result is nullable-per-probe:
        /// a failed probe leaves ITS field null (that check stays NotVerifiable) without discarding the
        /// counts that did land — partial evidence honestly beats none.</summary>
        private async Task<RelationshipProbeResult> ExecuteRelationshipProbesAsync(LiveConnection live, RelationshipCheckInput rel)
        {
            RelationshipProbeQueries q;
            try { q = RelationshipProbes.For(rel); }
            catch (ArgumentException) { return null; }   // blank endpoint — the analyzer routes it NotVerifiable
            var probe = new RelationshipProbeResult
            {
                OrphanRows = await ProbeScalarAsync(live, q.OrphanRows),
                BlankForeignKeys = await ProbeScalarAsync(live, q.BlankForeignKeys),
                DuplicateKeys = await ProbeScalarAsync(live, q.DuplicateKeys),
                BlankKeys = await ProbeScalarAsync(live, q.BlankKeys),
                ManyRowCount = await ProbeScalarAsync(live, q.ManyRowCount),
                OneRowCount = await ProbeScalarAsync(live, q.OneRowCount),
            };
            return probe;
        }

        private static async Task<long?> ProbeScalarAsync(LiveConnection live, string query)
        {
            try
            {
                var rs = await live.ExecuteAsync(query, 2, 120);
                if (!string.IsNullOrEmpty(rs.Error) || rs.Rows == null || rs.Rows.Length == 0 || rs.Rows[0].Length == 0) return null;
                var v = rs.Rows[0][0];
                // COUNTROWS over an empty FILTER is BLANK, not 0 — surfacing as null would leave the check
                // NotVerifiable when the true measurement is "zero rows". BLANK here IS zero.
                if (v == null || v is DBNull) return 0;
                return Convert.ToInt64(v);
            }
            catch { return null; }
        }

        /// <summary>Run one deterministic COUNTROWS / COUNT_BIG pair. A mismatch is not a failure here because
        /// the two connections cannot prove snapshot alignment; the pure judge keeps it NotVerifiable.</summary>
        private async Task ExecuteTableRowCountAsync(LiveConnection live, TableRowCountInput table, string origin,
            Dictionary<string, string> sqlTokens)
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

            var gate = GuardAgent(AgentCapability.QueryData, table.Server, table.Database, origin, isCommit: true,
                summary: $"run the ambient row-count check: read {table.Schema}.{table.Entity} from {table.Database} on {table.Server}",
                intentBasis: "querydata", consumeGrant: false);
            if (gate != null) { table.SourceError = gate; return; }

            var connection = ConnectionRegistry.FindByEndpoint(table.Server, table.Database);
            string token;
            try
            {
                var tokenKey = string.Join("\u001f", connection?.AuthMode ?? "azcli", connection?.TenantId ?? "");
                if (!sqlTokens.TryGetValue(tokenKey, out token))
                {
                    token = await EntraToken.AcquireSqlAsync(connection?.AuthMode, null, CancellationToken.None, connection?.TenantId).ConfigureAwait(false);
                    sqlTokens[tokenKey] = token;
                }
            }
            catch (Exception ex)
            {
                table.SourceError = "could not acquire a SQL token: " + ScrubSchemaError(ex.Message);
                return;
            }

            var sql = $"SELECT COUNT_BIG(*) AS [semanticus_table_rows] FROM {SqlIdentifier(table.Schema)}.{SqlIdentifier(table.Entity)}";
            var source = await FabricSqlQuery.ExecuteAsync(table.Server, table.Database, token, sql, 2, 120, CancellationToken.None).ConfigureAwait(false);
            table.SourceObservedUtc = DateTime.UtcNow.ToString("o");
            if (!TryReadCount(source, out var sourceCount, out var sourceError)) table.SourceError = sourceError;
            else table.SourceCount = sourceCount;
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
                    else plan.Request.MeasureRef = measure.Name;
                }
                else if (!string.IsNullOrEmpty(d.TargetIdentity))
                {
                    var measure = identities.Resolve(d.TargetIdentity, snapshots);
                    if (measure == null) plan.Missing = true;
                    else
                    {
                        plan.Request.MeasureRef = measure.Ref;
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
        private static List<ReconcileOutcome> BuildUnmappedMeasureOutcomes(Model m, List<ReconcilePlan> plans)
        {
            var mappedTags = plans.Select(p => p.Def.TargetTag)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToHashSet(StringComparer.Ordinal);
            // TargetRef is display-only once a stable tag exists. Using a stale ref from a missing tagged
            // target would hide a newly-created same-name impostor from coverage, defeating the tag binding.
            var mappedRefs = plans.Where(p => string.IsNullOrWhiteSpace(p.Def.TargetTag)).Select(p => p.Def.TargetRef)
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outcomes = new List<ReconcileOutcome>();
            foreach (var measure in m.Tables.SelectMany(t => t.Measures))
            {
                var targetRef = ObjectRefs.For(measure);
                if ((!string.IsNullOrWhiteSpace(measure.LineageTag) && mappedTags.Contains(measure.LineageTag))
                    || mappedRefs.Contains(targetRef)) continue;
                outcomes.Add(new ReconcileOutcome
                {
                    DefId = "unmapped:" + targetRef,
                    Title = measure.Name,
                    TargetRef = targetRef,
                    Verdict = Verdict.NotVerifiable,
                    Message = "No human-accepted source SQL mapping exists for this measure.",
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
                if (plan.Missing || string.IsNullOrWhiteSpace(plan.Request?.MeasureRef)) continue;
                if (!byName.TryGetValue(plan.Request.MeasureRef, out var baseMeasure)) continue;
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
            var bound = plans.Where(p => !p.Missing && !string.IsNullOrWhiteSpace(p.Request?.MeasureRef))
                .Select(p => p.Request.MeasureRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bound.Count < 2) return;   // nothing to cascade between
            var byName = m.Tables.SelectMany(t => t.Measures)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                if (plan.Missing || string.IsNullOrWhiteSpace(plan.Request?.MeasureRef)) continue;
                if (!byName.TryGetValue(plan.Request.MeasureRef, out var start)) continue;
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

        private async Task<ReconcileOutcome> RunReconcileDefAsync(ReconcilePlan plan, bool live, string origin)
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
            };
            if (plan.Missing)
            {
                o.Missing = true; o.Verdict = Verdict.NotVerifiable;
                o.Message = "the measure this test was bound to no longer exists (or was recreated with a new identity). Re-bind or delete the test";
                return o;
            }
            if (plan.BindError != null) { o.Verdict = Verdict.NotVerifiable; o.Message = plan.BindError; return o; }
            if (!live) { o.Verdict = Verdict.NotVerifiable; o.Message = "reconciliation needs a live connection. Connect (connect_xmla or connect_local) and re-run"; return o; }

            var r = await ReconcileMeasureAsync(plan.Request, origin);
            o.Dax = r.DaxQuery;
            o.Rows = BuildCompareRows(r.Cells, out var rowsTotal);
            o.RowsTotal = rowsTotal;
            o.Matches = r.Matches; o.Mismatches = r.Mismatches; o.Unverifiable = r.Unverifiable;
            o.DurationMs = r.DaxElapsedMs; o.SqlDurationMs = r.SqlElapsedMs;
            o.ToleranceNote = ComposeToleranceNote(r);
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
                    o.Message = string.IsNullOrEmpty(r.WorstExplanation) ? r.Summary : $"{r.Summary} Worst: {r.WorstKey}: {r.WorstExplanation}";
                    break;
                default:
                    o.Verdict = Verdict.NotVerifiable;
                    o.Message = r.Error ?? r.Summary ?? "the run could not verify enough to certify";
                    break;
            }
            return o;
        }

        private static string JoinNotes(params string[] notes)
        {
            var parts = notes.Where(n => !string.IsNullOrEmpty(n)).ToArray();
            return parts.Length == 0 ? null : string.Join(" ", parts);
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

        /// <summary>The effective match window + blank policy in analyst language, so a green is auditable at a
        /// glance. The loose-window warning is carried INTO the note (never a silent green at 5% tolerance).</summary>
        private static string ComposeToleranceNote(ReconcileRunResult r)
        {
            var blank = r.BlankPolicy switch
            {
                nameof(BlankPolicy.BlankIsZero) => "blanks read as zero",
                nameof(BlankPolicy.BlankIsNull) => "blanks read as no value",
                nameof(BlankPolicy.BlankIsDistinct) => "blanks kept distinct from zero and null",
                _ => null,
            };
            var note = $"match window: {FormatTolerance(r.ToleranceRelative)} relative to ground truth, "
                + $"{FormatTolerance(r.ToleranceAbsolute)} absolute floor{(blank == null ? "" : ", " + blank)}";
            if (r.SuspiciouslyLoose)
                note += ". Warning: this window is loose enough to hide a real error";
            return note;
        }

        private static string FormatTolerance(double v) =>
            v == 0 ? "0" : Math.Abs(v) < 0.0001 ? v.ToString("0.#e+0", System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

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

        // ---- OLS visibility (informational) --------------------------------------------------------------

        private const int OlsTileCap = 24;

        /// <summary>Static object-visibility per role (the list_roles read pattern). CL &lt; 1400 cannot express
        /// OLS, so the whole summary is null (absent), never a fabricated all-visible. Informational only:
        /// hiding nothing is a legitimate modelling choice, so OLS carries no verdict and never moves the grade.</summary>
        private static List<RoleOls> BuildOlsSummaries(Model m)
        {
            var list = new List<RoleOls>();
            foreach (var r in m.Roles)
            {
                if (r.MetadataPermission == null) return null;   // CL < 1400: the OLS indexers are null (reading would throw)
                var tiles = m.Tables
                    .Select(t => new RoleOlsTile { Table = t.Name, Hidden = r.MetadataPermission[t] == MetadataPermission.None })
                    .ToList();
                list.Add(new RoleOls
                {
                    Role = r.Name,
                    TablesTotal = tiles.Count,
                    TablesHidden = tiles.Count(t => t.Hidden),
                    ColumnsHidden = r.TablePermissions.Sum(tp => tp.Table.Columns.Count(c => tp.ColumnPermissions[c] == MetadataPermission.None)),
                    // Tiles are illustration, the counts are the truth: model order, capped so a wide model
                    // does not ship a wall of tiles over the wire.
                    Tiles = tiles.Take(OlsTileCap).ToArray(),
                });
            }
            return list;
        }

        // ---- E5: the opt-in clear-cache timing pass --------------------------------------------------------

        /// <summary>Times each BUDGETED test's measure with the ratified discipline: clear the storage-engine
        /// cache, run once, judge against the test's own budget (never a default — no budget, no judgement, and
        /// the Performance category stays dormant). A refused or failed cache clear degrades that measure to
        /// NotVerifiable rather than selling a warm number as cold. The run's QueryData gate was already answered
        /// for this suite run, so there is no second ask here.</summary>
        private static List<ReconcilePlan> BudgetedPlans(List<ReconcilePlan> plans) => plans
            .Where(p => !p.Missing && p.BindError == null && !string.IsNullOrWhiteSpace(p.Request?.MeasureRef)
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
                .GroupBy(p => p.Request.MeasureRef.Trim(), StringComparer.OrdinalIgnoreCase)
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
