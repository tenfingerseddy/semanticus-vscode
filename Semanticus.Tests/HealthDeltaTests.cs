using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Feature #4 "health delta everywhere" (docs/product-innovation-brainstorm.md §4): after each mutating op
    /// COMMITS, the engine computes a deterministic health delta — readiness grade movement (full rescan vs the
    /// memoized "before"), net-new BPA/lint findings on TOUCHED objects only, blast-radius count — and delivers
    /// it to BOTH doors (ChangeNotification.Health for the chip; the agent mailbox → MCP tool-result block).
    /// The locked semantics under test: PRO with a SOFT gate (free never throws, just gets no block);
    /// always-on with SUB-THRESHOLD SUPPRESSION (null unless the grade letter moved, a net-new Warning+ finding
    /// landed on a touched object, or blast radius &gt; 0); dry-runs never emit.
    /// </summary>
    public sealed class HealthDeltaTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        /// <summary>A model that scores CLEAN where it matters: one described+formatted measure, described table
        /// and column — so DESC/FMT/NAME rules all pass at baseline, and the deterministic worsening lever is the
        /// DESC-MEASURE &gt;50%-undescribed gate (2 of 3 undescribed caps the score at D). Verified empirically:
        /// baseline grades C (synonyms/DAC keep it off A/B); the SECOND bad measure moves the letter C→D.</summary>
        private static async Task<(LocalEngine engine, SessionManager sm, string m1)> FreshAsync(bool pro = true)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(pro));
            await engine.CreateModelAsync("HealthTest", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var m1 = await engine.CreateMeasureAsync(t, "Total Amount", "SUM(Sales[Amount])", "human");
            await engine.SetDescriptionAsync(m1, "The total transaction amount in dollars, summed across all rows.", "human");
            await engine.SetMeasureFormatAsync(m1, "#,0.00", "human");
            await engine.SetDescriptionAsync("table:Sales", "One row per sales transaction.", "human");
            await engine.SetDescriptionAsync("column:Sales/Amount", "The transaction amount in dollars.", "human");
            await engine.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            return (engine, sm, m1);
        }

        /// <summary>Run one mutation and capture the single ChangeNotification it broadcasts.</summary>
        private static async Task<ChangeNotification> OnNextChangeAsync(SessionManager sm, Func<Task> act)
        {
            ChangeNotification last = null;
            Action<ChangeNotification> handler = n => last = n;
            sm.Bus.Changed += handler;
            try { await act(); } finally { sm.Bus.Changed -= handler; }
            return last;
        }

        // (a) A worsening edit reports the block with ACTIONABLE rule-ids; a second one moves the grade LETTER
        //     (the >50%-undescribed gate) and the notification carries exactly the analyzer's own before→after.
        [Fact]
        public async Task Worsening_edit_reports_new_rule_ids_then_grade_movement()
        {
            var (engine, sm, _) = await FreshAsync();

            // Edit 1: one bad measure — no letter move yet, but net-new Warning+ findings on the touched object.
            var n1 = await OnNextChangeAsync(sm, () => engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent"));
            Assert.NotNull(n1?.Health);
            Assert.NotNull(n1.Health.New);
            Assert.Contains("DESC-MEASURE", n1.Health.New);          // High severity → crosses the Warning+ threshold
            Assert.Contains("FMT-MEASURE", n1.Health.New);           // net-new on the same touched object
            Assert.True((n1.Health.Findings ?? 0) >= 2);

            // Edit 2: the second undescribed measure trips the ">50% of visible measures undescribed" gate —
            // a DETERMINISTIC letter drop. Assert the payload equals the analyzer's own before/after so the test
            // pins consistency, not a hardcoded letter pair (re-tune FreshAsync if a future ruleset moves it).
            var before = (await engine.AiReadinessScanAsync()).Grade;
            var n2 = await OnNextChangeAsync(sm, () => engine.CreateMeasureAsync("table:Sales", "m3", "2", "agent"));
            var after = (await engine.AiReadinessScanAsync()).Grade;
            Assert.NotEqual(before, after);                           // the gate really moved the letter
            Assert.NotNull(n2?.Health);
            Assert.Equal(before + "->" + after, n2.Health.Grade);
        }

        // (b) A cosmetic edit (re-describing an already-described measure) is BELOW threshold: the notification
        //     still broadcasts, but carries no health block — and nothing lands in the agent mailbox.
        [Fact]
        public async Task Cosmetic_edit_emits_no_block()
        {
            var (engine, sm, m1) = await FreshAsync();
            var n = await OnNextChangeAsync(sm, () =>
                engine.SetDescriptionAsync(m1, "The grand total of transaction amounts in dollars across every row.", "agent"));
            Assert.NotNull(n);                       // the didChange itself still fires
            Assert.Null(n.Health);                   // …but health is suppressed (no grade move, no finding, no impact)
            Assert.Null(await engine.PullAgentHealthAsync());
        }

        // (c) FREE tier: the SOFT gate means the edit succeeds exactly as before — no block, no throw.
        [Fact]
        public async Task Free_tier_gets_no_block_and_never_throws()
        {
            var (engine, sm, _) = await FreshAsync(pro: false);
            var n = await OnNextChangeAsync(sm, () => engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent"));
            Assert.NotNull(n);
            Assert.Null(n.Health);
            Assert.Null(await engine.PullAgentHealthAsync());
        }

        // (d) Scoping: a finding introduced on the TOUCHED object is counted; the untouched object's
        //     pre-existing findings are not re-reported.
        [Fact]
        public async Task New_finding_counts_only_on_the_touched_object()
        {
            var (engine, sm, m1) = await FreshAsync();
            // m2 carries pre-existing findings (undescribed, unformatted) — the UNTOUCHED object from here on.
            await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
            await engine.PullAgentHealthAsync();     // drain m2's own block

            // Touch ONLY m1, introducing a deterministic DAX best-practice finding on it.
            var n = await OnNextChangeAsync(sm, () =>
                engine.SetDaxAsync(m1, "IFERROR(SUM(Sales[Amount]), 0)", "agent"));
            Assert.NotNull(n?.Health);
            Assert.NotNull(n.Health.New);
            Assert.Contains("BP-DAX-IFERROR", n.Health.New);          // net-new, on the touched object
            Assert.DoesNotContain("DESC-MEASURE", n.Health.New);      // m2's pre-existing findings don't re-report
            Assert.DoesNotContain("FMT-MEASURE", n.Health.New);
        }

        // Blast radius: a SEMANTIC edit to a referenced measure reports its downstream count; the same object's
        // cosmetic edit reports nothing (impact roots are semantic-prop deltas only).
        [Fact]
        public async Task Impact_counts_downstream_of_a_semantic_edit_only()
        {
            var (engine, sm, m1) = await FreshAsync();
            var m2 = await engine.CreateMeasureAsync("table:Sales", "Double Total", "[Total Amount] * 2", "agent");
            await engine.SetDescriptionAsync(m2, "Twice the total transaction amount, for banding comparisons.", "agent");
            await engine.SetMeasureFormatAsync(m2, "#,0.00", "agent");
            await engine.PullAgentHealthAsync();     // drain the create/describe blocks

            // Semantic edit to m1 (no new findings, no grade move) → the block appears PURELY via impact>0.
            var n = await OnNextChangeAsync(sm, () => engine.SetDaxAsync(m1, "SUM(Sales[Amount]) + 0", "agent"));
            Assert.NotNull(n?.Health);
            Assert.True((n.Health.Impact ?? 0) >= 1, "the dependent measure is downstream of the edit");

            // Cosmetic edit to the SAME referenced measure → no impact claimed, block suppressed.
            var n2 = await OnNextChangeAsync(sm, () =>
                engine.SetDescriptionAsync(m1, "The total transaction amount in dollars over all rows.", "agent"));
            Assert.Null(n2.Health);
        }

        // The chip's undo affordance is honest: undoing the letter-moving edit broadcasts the REVERSE movement.
        [Fact]
        public async Task Undo_reports_the_reverse_grade_movement()
        {
            var (engine, sm, _) = await FreshAsync();
            await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
            var before = (await engine.AiReadinessScanAsync()).Grade;
            await engine.CreateMeasureAsync("table:Sales", "m3", "2", "agent");   // trips the gate (see test a)
            var after = (await engine.AiReadinessScanAsync()).Grade;
            Assert.NotEqual(before, after);

            var n = await OnNextChangeAsync(sm, () => engine.UndoAsync("human"));
            Assert.NotNull(n?.Health);
            Assert.Equal(after + "->" + before, n.Health.Grade);      // the reverse of the movement it undoes
        }

        // (e) Dry-runs never emit: MutateAsync short-circuits rehearsals away from the tracked-commit path.
        [Fact]
        public async Task Dry_run_emits_no_health_delta()
        {
            var (engine, sm, _) = await FreshAsync();
            ChangeNotification seen = null;
            Action<ChangeNotification> handler = n => seen = n;
            sm.Bus.Changed += handler;
            try
            {
                var rpt = await engine.DryRunOpAsync("create_measure",
                    "{\"tableRef\":\"table:Sales\",\"name\":\"m9\",\"expression\":\"1\"}");
                Assert.True(rpt.WouldSucceed);
            }
            finally { sm.Bus.Changed -= handler; }
            Assert.Null(seen);                                        // no broadcast at all…
            Assert.Null(await engine.PullAgentHealthAsync());         // …and nothing in the agent mailbox
        }

        // The MCP success filter (McpHealthAppender): appends ONE terse block to a mutating agent call's result,
        // take-once (the next read-only call appends nothing), and a FAILED call's partial stash never leaks
        // onto the next result.
        [Fact]
        public async Task Mcp_filter_appends_the_block_once_and_never_leaks_across_calls()
        {
            var (engine, _, _) = await FreshAsync();

            // A "mutating tool call": the body commits an agent edit, then returns a normal result.
            var result = await McpHealthAppender.InvokeAsync(engine, async () =>
            {
                await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
                return new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "measure:Sales/m2" } } };
            });
            var block = result.Content.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault(t => t.StartsWith("health: {"));
            Assert.NotNull(block);
            Assert.Contains("DESC-MEASURE", block);
            Assert.Contains("\"new\":", block);                       // camelCase, terse JSON
            Assert.DoesNotContain("\"grade\":null", block);           // omit-when-unchanged, field by field

            // A read-only call right after: mailbox already drained — nothing appended.
            var read = await McpHealthAppender.InvokeAsync(engine, () =>
                new ValueTask<CallToolResult>(new CallToolResult { Content = new List<ContentBlock>() }));
            Assert.DoesNotContain(read.Content.OfType<TextContentBlock>(), b => b.Text.StartsWith("health:"));

            // A call that commits then FAILS: the exception propagates (same type — the agent's error contract
            // is untouched) CARRYING the drained health on its Data (C5: the chip already showed the delta at
            // commit time, so the agent must hear "failed but committed; health moved", not pure failure), and
            // the stash is consumed, so the following successful call carries no stale block.
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await McpHealthAppender.InvokeAsync(engine, async () =>
                {
                    await engine.CreateMeasureAsync("table:Sales", "m3", "2", "agent");
                    throw new InvalidOperationException("tool blew up after committing");
                }));
            var carried = thrown.Data[McpHealthAppender.HealthDataKey] as string;
            Assert.NotNull(carried);
            Assert.StartsWith("health:", carried);
            Assert.Contains("already committed", carried);   // the honest wording
            var next = await McpHealthAppender.InvokeAsync(engine, () =>
                new ValueTask<CallToolResult>(new CallToolResult { Content = new List<ContentBlock>() }));
            Assert.DoesNotContain(next.Content.OfType<TextContentBlock>(), b => b.Text.StartsWith("health:"));
        }

        // C5 end-to-end: composed exactly as Program.Mcp registers them (error boundary around the appender),
        // a commit-then-throw tool call returns an ERROR result that still carries BOTH the teaching failure
        // text AND the health block — the two doors tell one story.
        [Fact]
        public async Task Commit_then_throw_surfaces_health_on_the_error_result()
        {
            var (engine, _, _) = await FreshAsync();
            var result = await McpErrorBoundary.InvokeAsync("create_measure", () =>
                McpHealthAppender.InvokeAsync(engine, async () =>
                {
                    await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
                    throw new InvalidOperationException("post-commit validation blew up");
                }));
            Assert.True(result.IsError);
            var texts = result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToArray();
            Assert.Contains(texts, t => t.Contains("post-commit validation blew up"));     // the teaching error text is intact
            var health = Assert.Single(texts, t => t.StartsWith("health:"));
            Assert.Contains("DESC-MEASURE", health);
            Assert.Contains("already committed", health);                                   // the honest failure wording
        }

        // C1: correlation-true attribution — two "parallel" tool calls each drain exactly their OWN deltas
        // (the old drain-the-whole-queue design handed the first drainer both, merged, and the second none),
        // a multi-commit call merges honestly into one slot (no MailboxCap eviction), and take-once holds per id.
        [Fact]
        public async Task Parallel_calls_each_drain_their_own_deltas_by_correlation_id()
        {
            var (engine, _, _) = await FreshAsync();

            // Same-shaped names so every measure trips the identical finding set (DESC/FMT/…): per-call counts
            // must then satisfy b == 2×a exactly, whatever the ruleset's per-measure hit count is.
            using (HealthCorrelation.Begin("call-a"))
                await engine.CreateMeasureAsync("table:Sales", "Alpha Probe", "1", "agent");
            using (HealthCorrelation.Begin("call-b"))
            {
                await engine.CreateMeasureAsync("table:Sales", "Beta Probe", "2", "agent");
                await engine.CreateMeasureAsync("table:Sales", "Gamma Probe", "3", "agent");
            }

            // Drain B first — the old design would have handed it A's delta too.
            var b = await engine.PullAgentHealthAsync("call-b");
            var a = await engine.PullAgentHealthAsync("call-a");
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.True((a.Findings ?? 0) >= 2, "one bad measure: at least DESC-MEASURE + FMT-MEASURE");
            Assert.Equal(a.Findings * 2, b.Findings);          // b's two commits merged on enqueue — nothing lost, nothing borrowed
            Assert.Null(await engine.PullAgentHealthAsync("call-a"));   // take-once per id
            Assert.Null(await engine.PullAgentHealthAsync("call-b"));
            Assert.Null(await engine.PullAgentHealthAsync());           // and nothing leaked to the unscoped slot
        }

        // C1: a model swap mid-call must not lose (or mis-route) the delta — the mailbox lives on the ENGINE,
        // not the session, so the stash survives the swap and drains by the SAME call id.
        [Fact]
        public async Task Model_swap_mid_call_keeps_the_delta_drainable_by_its_call_id()
        {
            var (engine, _, _) = await FreshAsync();
            using (HealthCorrelation.Begin("call-swap"))
            {
                await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
                await engine.CreateModelAsync("Other", 1604);   // swaps SessionManager.Current
            }
            var h = await engine.PullAgentHealthAsync("call-swap");
            Assert.NotNull(h);
            Assert.Contains("DESC-MEASURE", h.New);
        }

        // C2: a baseline that throws on its BPA leg must not permanently kill BPA net-new detection — the
        // probe reseeds the missing memo on the next commit and detection RESUMES (it used to stay dead for
        // the whole session).
        [Fact]
        public async Task Baseline_bpa_throw_recovers_and_detection_resumes()
        {
            var (engine, sm, m1) = await FreshAsync();
            var throwBpa = true;
            var probe = new HealthDeltaProbe(
                m => throwBpa ? throw new InvalidOperationException("malformed embedded BPA rules") : LocalEngine.GetBpaRules(m),
                null);
            foreach (var installed in sm.Current!.Observers.ToArray()) sm.Current.UnregisterObserver(installed);
            sm.Current.RegisterObserver(probe);   // fault-injected probe REPLACES the engine-installed one

            // Commit 1: EnsureBaseline runs pre-mutation — readiness seeds, the BPA leg throws (memo = null);
            // the post-commit reseed retry throws too (still faulted).
            await engine.SetDescriptionAsync(m1, "Edited while the BPA rules were unreadable.", "agent");
            throwBpa = false;   // the transient fault clears

            // Commit 2: the probe reseeds the BPA memo post-commit (the C2 retry) — detection is armed again.
            await engine.SetDescriptionAsync(m1, "Edited right after the BPA rules recovered.", "agent");

            // Commit 3: introduce a BPA violation on the touched object (a lowercase-named measure trips the
            // standard naming rule) — it must be REPORTED (was: dead forever).
            string badRef = null;
            var n = await OnNextChangeAsync(sm, async () => badRef = await engine.CreateMeasureAsync("table:Sales", "lowercase probe", "1", "agent"));
            Assert.NotNull(n?.Health?.New);
            var bpaIds = await sm.Current!.ReadAsync(m =>
                Semanticus.Analysis.BpaAnalyzer.Analyze(m, LocalEngine.GetBpaRules(m), new HashSet<string> { badRef })
                    .Violations.Select(v => v.RuleId).ToArray());
            Assert.NotEmpty(bpaIds);                                    // the new measure really trips BPA
            Assert.Contains(n.Health.New, id => bpaIds.Contains(id));   // …and the delta carries the BPA finding
        }

        // C3: a MIXED commit — fix one DESC-MEASURE and introduce another in the SAME commit — keeps the rule's
        // model-wide count flat, which the old unconditional guard read as "a rename" and suppressed the REAL
        // new finding. The guard now applies only to renamed objects, so the new finding reports.
        [Fact]
        public async Task Mixed_fix_and_add_of_the_same_rule_reports_the_new_finding()
        {
            var (engine, sm, _) = await FreshAsync();
            var m2 = await engine.CreateMeasureAsync("table:Sales", "m2", "1", "agent");
            await engine.PullAgentHealthAsync();   // drain m2's own block

            var n = await OnNextChangeAsync(sm, () => sm.Current!.MutateAsync("agent", "mixed edit", m =>
            {
                m.Tables["Sales"].Measures["m2"].Description = "Now thoroughly described for the mixed-commit test.";
                m.Tables["Sales"].AddMeasure("m5", "2");   // same rule (DESC-MEASURE) newly violated — count stays flat
            }));
            Assert.NotNull(n?.Health);
            Assert.NotNull(n.Health.New);
            Assert.Contains("DESC-MEASURE", n.Health.New);   // the real new finding on m5 is NOT suppressed
        }

        // C3: a rename-ONLY commit moves finding keys without adding a defect — nothing is re-reported and the
        // chip stays quiet (impact 0: nothing depends on the measure). Suppression here rides the LineageTag
        // identity (the wrapper auto-mints tags at CL 1604); the count guard remains the tag-less fallback.
        [Fact]
        public async Task Renamed_only_commit_does_not_rereport_moved_findings()
        {
            var (engine, sm, _) = await FreshAsync();
            // A WELL-named measure (BPA-clean) that still carries readiness findings (undescribed, unformatted),
            // so the rename moves readiness keys only — the population the C3 guard governs.
            var m2 = await engine.CreateMeasureAsync("table:Sales", "Rename Probe", "1", "agent");
            await engine.PullAgentHealthAsync();   // drain the create's block

            var n = await OnNextChangeAsync(sm, () => engine.RenameObjectAsync(m2, "Rename Probe Renamed", "agent"));
            Assert.NotNull(n);
            Assert.Null(n.Health);   // moved keys suppressed by the rename guard; no grade move, no impact
        }

        // ITEM 1 (#82 follow-up), the BPA lane: finding identity keys on the LineageTag, so renaming a measure
        // that carries a Warning+ BPA violation does NOT re-report the moved violation as net-new. Under the old
        // name-path keys this lane had NO rename protection at all ("BPA renames can re-key — accepted edge"),
        // so this rename minted a false net-new Warning and lit the chip.
        [Fact]
        public async Task Renamed_bpa_violation_is_not_net_new()
        {
            var (engine, sm, _) = await FreshAsync();
            // Lowercase-named measure trips UPPERCASE_FIRST_LETTER_MEASURES_TABLES (severity 2 = Warning+).
            var m2 = await engine.CreateMeasureAsync("table:Sales", "lowercase probe", "1", "agent");
            await engine.PullAgentHealthAsync();   // drain the create's own block

            // Rename to another lowercase name: still violating, but the SAME finding — its tag key is unchanged.
            var n = await OnNextChangeAsync(sm, () => engine.RenameObjectAsync(m2, "lowercase probe renamed", "agent"));
            Assert.NotNull(n);
            Assert.Null(n.Health);   // no net-new, no grade move, no impact — the chip stays quiet
        }

        // ITEM 1 refinement (Codex P2 on #90): a renamed object's OLD name is REUSED by a genuinely-new object
        // with the same rule violation in the SAME commit. The baseline still holds the stale name-path alias
        // (RuleId␟measure:Sales/Reuse Probe, stored for the renamed object), which must NOT suppress the new
        // object's finding — identity for a TAGGED object is its tag alone, never the name alias.
        [Fact]
        public async Task Reused_name_after_rename_still_reports_the_new_object()
        {
            var (engine, sm, _) = await FreshAsync();
            await engine.CreateMeasureAsync("table:Sales", "Reuse Probe", "1", "agent");   // undescribed, unformatted
            await engine.PullAgentHealthAsync();   // drain the create's own block

            var n = await OnNextChangeAsync(sm, () => sm.Current!.MutateAsync("agent", "rename + reuse name", m =>
            {
                m.Tables["Sales"].Measures["Reuse Probe"].Name = "Reuse Probe Renamed";
                m.Tables["Sales"].AddMeasure("Reuse Probe", "2");   // NEW object under the OLD name, same violations
            }));
            Assert.NotNull(n?.Health);
            Assert.NotNull(n.Health.New);                    // the new object's findings must report…
            Assert.Contains("DESC-MEASURE", n.Health.New);   // …not be alias-suppressed as "already known"
        }

        // ITEM 1 (#82 follow-up), beyond the count guard: rename an object AND add a SAME-RULE violation in ONE
        // commit. The rule's model-wide count rises, so the count-based guard cannot suppress anything — only the
        // rename-stable LineageTag identity keeps the renamed object's moved findings out of "net-new". Exactly
        // one bad measure's worth of findings must report (the added one), not two.
        [Fact]
        public async Task Rename_plus_same_rule_add_reports_only_the_new_object()
        {
            var (engine, sm, _) = await FreshAsync();
            // Reference: one same-shaped bad measure's finding count (the per-measure hit count of the ruleset).
            using (HealthCorrelation.Begin("ref"))
                await engine.CreateMeasureAsync("table:Sales", "Ref Probe", "1", "agent");
            var one = await engine.PullAgentHealthAsync("ref");
            Assert.True((one?.Findings ?? 0) >= 2, "one bad measure: at least DESC-MEASURE + FMT-MEASURE");

            await engine.CreateMeasureAsync("table:Sales", "Tagged Probe", "2", "agent");
            await engine.PullAgentHealthAsync();   // drain

            var n = await OnNextChangeAsync(sm, () => sm.Current!.MutateAsync("agent", "rename + add", m =>
            {
                m.Tables["Sales"].Measures["Tagged Probe"].Name = "Tagged Probe Renamed";
                m.Tables["Sales"].AddMeasure("Added Probe", "3");   // same rules newly violated — counts RISE
            }));
            Assert.NotNull(n?.Health);
            Assert.Contains("DESC-MEASURE", n.Health.New);           // the genuinely-new object's finding reports…
            Assert.Equal(one.Findings, n.Health.Findings);           // …and ONLY its findings — the renamed one moved, not "new"
        }

        // C6/S2: a mega-batch commit whose semantic roots exceed the OLD 64-root cap must still report the real
        // blast radius — the multi-source BFS walks ALL roots, so a leaf-heavy prefix can never truncate the
        // dependent-bearing root out of the count.
        [Fact]
        public async Task Mega_batch_commit_never_truncates_the_blast_radius()
        {
            var (engine, sm, m1) = await FreshAsync();
            var m2 = await engine.CreateMeasureAsync("table:Sales", "Double Total", "[Total Amount] * 2", "agent");
            await engine.SetDescriptionAsync(m2, "Twice the total transaction amount, for banding comparisons.", "agent");
            await engine.SetMeasureFormatAsync(m2, "#,0.00", "agent");
            await engine.PullAgentHealthAsync();   // drain

            var n = await OnNextChangeAsync(sm, () => sm.Current!.MutateAsync("agent", "mega batch", m =>
            {
                for (var i = 0; i < 70; i++)                       // 70 leaf roots FIRST in delta order…
                    m.Tables["Sales"].AddMeasure($"leaf{i}", "1");
                m.Tables["Sales"].Measures["Total Amount"].Expression = "SUM(Sales[Amount]) + 0";   // …the dependent-bearing root LAST
            }));
            Assert.NotNull(n?.Health);
            Assert.True((n.Health.Impact ?? 0) >= 1, "the dependent measure downstream of the 71st root must be counted");
        }

        // P6: the probe is installed on every session and checks the entitlement LAZILY — a license activated
        // mid-session (headless engine: no reopen) starts reporting on the very next edit.
        [Fact]
        public async Task Mid_session_pro_activation_starts_reporting()
        {
            var sm = new SessionManager();
            var toggle = new ToggleEntitlement { IsPro = false };
            var engine = new LocalEngine(sm, toggle);
            await engine.CreateModelAsync("HealthTest", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");

            var free = await OnNextChangeAsync(sm, () => engine.CreateMeasureAsync(t, "m2", "1", "agent"));
            Assert.Null(free!.Health);                              // free: no scan, no block
            Assert.Null(await engine.PullAgentHealthAsync());

            toggle.IsPro = true;                                    // headless mid-session activation
            var pro = await OnNextChangeAsync(sm, () => engine.CreateMeasureAsync(t, "m3", "2", "agent"));
            Assert.NotNull(pro?.Health);                            // reporting starts on the next edit
            Assert.Contains("DESC-MEASURE", pro.Health.New);
            Assert.NotNull(await engine.PullAgentHealthAsync());    // and the agent mailbox got it too
        }

        private sealed class ToggleEntitlement : IEntitlement
        {
            public bool IsPro { get; set; }
            public EntitlementInfo Info => new EntitlementInfo { Tier = IsPro ? "pro" : "free" };
        }

        // The scoped BPA overload is faithful: for the touched object it reports exactly the full scan's rule
        // hits, and model-scope rules never enter a scoped card whose scope doesn't include the model itself.
        [Fact]
        public async Task Scoped_bpa_scan_matches_the_full_scan_for_the_touched_object()
        {
            var (engine, sm, _) = await FreshAsync();
            var m2 = await engine.CreateMeasureAsync("table:Sales", "m2", "IFERROR(1/0, 0)", "agent");

            var (full, scoped) = await sm.Current.ReadAsync(m =>
            {
                var rules = LocalEngine.GetBpaRules(m);
                return (Semanticus.Analysis.BpaAnalyzer.Analyze(m, rules),
                        Semanticus.Analysis.BpaAnalyzer.Analyze(m, rules, new HashSet<string> { m2 }));
            });

            var fullOnM2 = full.Violations.Where(v => v.ObjectRef == m2).Select(v => v.RuleId).OrderBy(x => x).ToArray();
            var scopedIds = scoped.Violations.Select(v => v.RuleId).OrderBy(x => x).ToArray();
            Assert.NotEmpty(scopedIds);                               // the IFERROR-style rules really fire
            Assert.Equal(fullOnM2, scopedIds);                        // same hits for the touched object…
            Assert.All(scoped.Violations, v => Assert.Equal(m2, v.ObjectRef));   // …and ONLY the touched object
        }
    }
}
