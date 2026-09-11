using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The apply_plan set_dax VERIFY GATE — the contract the Pro moat rests on, proved deterministically here.
    ///
    /// Why this file exists (T198 / F-004 / F-023): the same three assertions live in
    /// <c>Semanticus.AirSmoke/Program.cs</c> inside a lane that only opens when a running
    /// local Power BI Desktop is discovered. No CI runner has one, so not one of them had ever executed in CI, and
    /// where the lane DOES run its failing set varies with whichever model a developer happens to have attached.
    /// These tests move the contract onto the injected live seams (<see cref="LiveConnection.ForTest"/> +
    /// <c>SetLiveConnectionForTest</c>), so every rung is proved on a machine with no XMLA endpoint at all.
    ///
    /// The rungs, in the shared evidence ladder's order (DaxBench.ClassifyEquivalenceEvidence):
    ///   opt-in           — an empty verify matrix is "proposed", never auto-approved;
    ///   no live / error  — nothing ran to completion: skipped/unverified;
    ///   degraded_mismatch— attach-mode divergence: an OBSERVATION, not a conviction (must not read "failed");
    ///   failed           — a full-fidelity results-changing rewrite, skipped even at the grand total;
    ///   zero / truncated — no requested-grid rows or incomplete coverage: skipped/unverified;
    ///   degraded         — attach-mode match: fidelity-gated, skipped (never rides the apply-with-label path);
    ///   thin             — grand-total-only match: applies but is labelled unverified, not "verified";
    ///   proven           — a matching NON-EMPTY grid: applies AND is labelled verified.
    ///
    /// "Attach mode" = a file-opened editing session with a query connection attached. The session has no
    /// <see cref="Session.LiveOrigin"/>, so <c>BuildQuerySpecAsync</c> cannot vouch that the connection executes
    /// against the model being edited and hands PlanEval the untrusted marker, which degrades fidelity.
    ///
    /// Every case drives the REAL door — AddPlanItem, SetPlanItem (approve), ApplyPlan — and asserts the measure's
    /// DAX afterwards, so a rung that reports the right label while shipping the wrong body still fails.
    /// LIVE Power BI Desktop, XMLA, Fabric, tenant and Windows paths are NOT exercised here and stay unverified.
    /// </summary>
    public sealed class PlanVerifyGateTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        // The stub endpoint/database. TRUSTED cases mirror these onto Session.LiveOrigin so TrustSessionSpec
        // matches on BOTH canonical endpoint and database; attach-mode cases leave LiveOrigin null.
        private const string Endpoint = "localhost:57531";
        private const string Database = "T198VerifyGate";

        // The grid column for the proven rung. AdventureWorks has NO 'Date'[Year] — its year column is
        // 'Date'[Calendar Year] — and Fixture_assumptions_hold below pins that, so a fixture swap fails loudly
        // here instead of quietly proving a rewrite against a column that does not exist.
        private const string YearColumn = "'Date'[Calendar Year]";
        private const string YearColumnResultName = "Date[Calendar Year]";

        private const string HostTable = "Internet Sales";
        private const string TargetMeasure = "T198_VerifyTarget";

        // ---------------------------------------------------------------- harness

        private sealed class Gate : IDisposable
        {
            public SessionManager Sessions;
            public LocalEngine Engine;
            public string MeasureRef;
            public readonly List<string> Queries = new List<string>();

            public void Dispose() { Engine?.Dispose(); Sessions?.Dispose(); }
        }

        /// <summary>Open the fixture and put one measure with body "1" on a real table.</summary>
        private static async Task<Gate> OpenAsync()
        {
            var sessions = new SessionManager();
            var g = new Gate { Sessions = sessions, Engine = new LocalEngine(sessions, new Pro()) };
            try
            {
                await g.Engine.OpenAsync(TestModels.FindBim());
                var table = (await g.Engine.ListTreeAsync(null)).Single(t => t.Kind == "table" && t.Name == HostTable);
                g.MeasureRef = await g.Engine.CreateMeasureAsync(table.Ref, TargetMeasure, "1", "human");
                return g;
            }
            catch { g.Dispose(); throw; }
        }

        /// <summary>Attach the query-executing stub. <paramref name="trusted"/> also stamps the session's live
        /// origin to match, which is what lets the spec carry the model's real facts (full fidelity).</summary>
        private static void Attach(Gate g, bool trusted, Func<string, ResultSet> respond)
        {
            var live = LiveConnection.ForTest("xmla", Endpoint, Database, query =>
            {
                g.Queries.Add(query);
                return respond(query);
            });
            if (trusted) g.Sessions.Current.LiveOrigin = new LiveOrigin(Endpoint, Database, null);
            g.Engine.SetLiveConnectionForTest(live);
        }

        private static ResultSet GrandTotal(object a, object b) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
            Rows = new[] { new object[] { a, b } },
            RowCount = 1,
        };

        private static ResultSet ByYear(params (object year, object a, object b)[] rows) => new ResultSet
        {
            Columns = new[]
            {
                new ColumnDef { Name = YearColumnResultName },
                new ColumnDef { Name = "__A" },
                new ColumnDef { Name = "__B" },
            },
            Rows = rows.Select(r => new object[] { r.year, r.a, r.b }).ToArray(),
            RowCount = rows.Length,
        };

        private static ResultSet EmptyGrandTotal() => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
            Rows = Array.Empty<object[]>(),
            RowCount = 0,
        };

        /// <summary>Stage a gated set_dax rewrite and return the single plan item (still un-approved).</summary>
        private static async Task<ChangeItem> StageAsync(Gate g, string after, string[] grid)
        {
            var plan = await g.Engine.AddPlanItemAsync(g.MeasureRef, "set_dax", after, "rewrite", grid, null, "agent");
            return plan.Items.Single(i => i.Kind == "set_dax");
        }

        /// <summary>Stage → explicit human approve → apply. The whole door, not just the classifier.</summary>
        private static async Task<ChangeItem> StageApproveApplyAsync(Gate g, string after, string[] grid)
        {
            var item = await StageAsync(g, after, grid);
            // An EMPTY matrix is the opt-in kind, so it lands "proposed" and the approve below is the consent;
            // a non-empty matrix carries a real per-context proof and is approved on authoring. Pinning the
            // expected status per grid keeps the approve from papering over a change in which kinds are opt-in.
            Assert.Equal(grid.Length == 0 ? "proposed" : "approved", item.Status);
            await g.Engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var report = await g.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
            return report.Items.Single(i => i.Kind == "set_dax");
        }

        private static Task<string> DaxAsync(Gate g) => g.Engine.GetDaxAsync(g.MeasureRef);

        // ---------------------------------------------------------------- fixture assumptions

        /// <summary>
        /// Every case below is only as honest as the fixture. Pin the two facts they lean on: the grid column
        /// used by the proven rung EXISTS (under its real name), and the model has ZERO calculation groups — a
        /// calc group would make <c>ModelHasCalcGroups</c> true, which degrades fidelity on the trusted path and
        /// would silently turn the "proven" and "thin" cases into "degraded" ones.
        /// </summary>
        [Fact]
        public async Task Fixture_assumptions_hold_year_column_exists_and_there_are_no_calculation_groups()
        {
            using var g = await OpenAsync();

            var roots = await g.Engine.ListTreeAsync(null);
            Assert.DoesNotContain(roots, n => n.Kind == "calcgroup");

            var date = Assert.Single(roots, n => n.Kind == "table" && n.Name == "Date");
            var columns = await g.Engine.ListTreeAsync(date.Ref);
            Assert.Contains(columns, c => c.Kind == "column" && c.Name == "Calendar Year");
            // The name the T198 brief assumed. Asserting its ABSENCE keeps the correction from silently rotting
            // back: if the fixture ever gains a plain [Year], YearColumn above must be revisited deliberately.
            Assert.DoesNotContain(columns, c => c.Kind == "column" && c.Name == "Year");

            Assert.Equal(HostTable, Assert.Single(roots, n => n.Kind == "table" && n.Name == HostTable).Name);
        }

        // ---------------------------------------------------------------- opt-in

        /// <summary>
        /// F-004's first check: an EMPTY verify matrix proves only the grand total, so the item is opt-in. It
        /// lands "proposed", and an apply that names it without an approve changes nothing at all.
        /// </summary>
        [Fact]
        public async Task An_empty_matrix_set_dax_is_opt_in_and_an_unapproved_apply_changes_nothing()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, _ => GrandTotal(1L, 1L));

            var item = await StageAsync(g, "2", Array.Empty<string>());
            Assert.Equal("proposed", item.Status);
            Assert.NotEqual("approved", item.Status);

            var report = await g.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

            Assert.Equal(0, report.AppliedCount);
            Assert.Empty(report.Items);                       // un-approved items are never widened into the target set
            Assert.Equal("1", await DaxAsync(g));
            Assert.Empty(g.Queries);                          // nothing was even proved: no consent, no proof
        }

        /// <summary>A NON-EMPTY matrix is not an opt-in kind — it carries a real per-context proof, so authoring
        /// it approves it. Pinned so the opt-in rung above is known to be about the EMPTY matrix specifically.</summary>
        [Fact]
        public async Task A_non_empty_matrix_set_dax_is_not_opt_in()
        {
            using var g = await OpenAsync();

            var item = await StageAsync(g, "2", new[] { YearColumn });

            Assert.Equal("approved", item.Status);
        }

        // ---------------------------------------------------------------- no live connection

        /// <summary>
        /// Nothing to prove against. This is the UNAVAILABLE shape: the rewrite is skipped and labelled
        /// unverified — never applied, and never reported as a failed proof (nothing ran).
        /// </summary>
        [Fact]
        public async Task Without_a_live_connection_a_gated_rewrite_is_skipped_as_unverified()
        {
            using var g = await OpenAsync();   // no Attach: context.Live stays null

            var applied = await StageApproveApplyAsync(g, "2", Array.Empty<string>());

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("unverified", applied.VerifyState);
            Assert.NotEqual("failed", applied.VerifyState);
            // Pin the shared classifier's run-failure wording. A hand-written no-live branch would drift from the
            // same evidence ladder used by every comparison that did reach the executor.
            Assert.Contains("equivalence check failed to run: Not connected.", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
            Assert.Empty(g.Queries);                          // classifier ran, but no executor existed to issue evidence queries
        }

        [Fact]
        public async Task An_errored_comparison_is_skipped_as_unverified_by_the_shared_classifier()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, _ => new ResultSet { Error = "invented comparison failure" });

            var applied = await StageApproveApplyAsync(g, "2", new[] { YearColumn });

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("unverified", applied.VerifyState);
            Assert.Contains("equivalence check failed to run: invented comparison failure", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
            Assert.Single(g.Queries);
        }

        [Fact]
        public async Task A_comparison_with_zero_requested_grid_rows_is_skipped_as_unverified()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, q => q.Contains(YearColumn, StringComparison.Ordinal)
                ? ByYear()
                : EmptyGrandTotal());

            var applied = await StageApproveApplyAsync(g, "1 + 0", new[] { YearColumn });

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("unverified", applied.VerifyState);
            Assert.Contains("compared 0 rows", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
            Assert.Equal(2, g.Queries.Count);
        }

        [Fact]
        public async Task A_truncated_comparison_is_skipped_as_unverified()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, q =>
            {
                if (!q.Contains(YearColumn, StringComparison.Ordinal)) return GrandTotal(11L, 11L);
                var result = ByYear((2031L, 11L, 11L));
                result.Truncated = true;
                return result;
            });

            var applied = await StageApproveApplyAsync(g, "1 + 0", new[] { YearColumn });

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("unverified", applied.VerifyState);
            Assert.Contains("coverage incomplete", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
            Assert.Equal(2, g.Queries.Count);
        }

        // ---------------------------------------------------------------- failed (full fidelity)

        /// <summary>
        /// F-004's second check: a results-changing rewrite is SKIPPED even when only the grand total was
        /// compared. A divergence under FULL fidelity is a conviction — "failed", not "degraded_mismatch".
        /// </summary>
        [Fact]
        public async Task A_results_changing_rewrite_is_skipped_and_failed_even_at_the_grand_total()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, _ => GrandTotal(1L, 2L));

            var applied = await StageApproveApplyAsync(g, "2", Array.Empty<string>());

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("failed", applied.VerifyState);
            Assert.Contains("changes results", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
            // An empty grid is the grand-total shape ALONE — one query, no redundant re-runs.
            Assert.Single(g.Queries);
        }

        // ---------------------------------------------------------------- thin (grand-total-only match)

        /// <summary>
        /// F-004's third check: a grand-total-only match APPLIES (the human opted in) but is labelled honestly.
        /// The over-claim this guards is "verified" — a single grand-total row is not a per-context proof.
        /// </summary>
        [Fact]
        public async Task A_grand_total_only_match_applies_but_is_labelled_unverified_not_verified()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, _ => GrandTotal(1L, 1L));

            var applied = await StageApproveApplyAsync(g, "1 + 0", Array.Empty<string>());

            Assert.Equal("applied", applied.Status);
            Assert.Equal("unverified", applied.VerifyState);
            Assert.NotEqual("verified", applied.VerifyState);
            Assert.Contains("grand-total match only", applied.Note);
            Assert.Equal("1 + 0", await DaxAsync(g));
            Assert.Single(g.Queries);
        }

        // ---------------------------------------------------------------- proven (the positive control)

        /// <summary>
        /// The positive end-to-end case, and the control the other rungs need: matching LiveOrigin plus a
        /// NON-EMPTY matching grid applies AND earns VerifyState "verified". Without this, every assertion above
        /// would still pass if the gate refused everything.
        /// </summary>
        [Fact]
        public async Task A_matching_non_empty_grid_applies_and_is_labelled_verified()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, q => q.Contains(YearColumn, StringComparison.Ordinal)
                ? ByYear((2011L, 10L, 10L), (2012L, 20L, 20L), (2013L, 30L, 30L))
                : GrandTotal(60L, 60L));

            var applied = await StageApproveApplyAsync(g, "1 + 0", new[] { YearColumn });

            Assert.Equal("applied", applied.Status);
            Assert.Equal("verified", applied.VerifyState);
            Assert.Equal("1 + 0", await DaxAsync(g));
            // A one-column grid is that column PLUS the grand total — the subtotal shape that catches a
            // wrong-scope rewrite agreeing at every leaf. Two shapes, two queries, deduped to no more.
            Assert.Equal(2, g.Queries.Count);
            Assert.Contains(g.Queries, q => q.Contains(YearColumn, StringComparison.Ordinal));
            Assert.Contains(g.Queries, q => !q.Contains(YearColumn, StringComparison.Ordinal));
        }

        /// <summary>A non-empty grid whose LEAF rows disagree fails, even though the grand total matches — the
        /// wrong-scope class the multi-shape proof exists to catch. Pins that "verified" above was earned.</summary>
        [Fact]
        public async Task A_non_empty_grid_that_disagrees_per_context_fails_though_the_grand_total_matches()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: true, q => q.Contains(YearColumn, StringComparison.Ordinal)
                ? ByYear((2011L, 10L, 25L), (2012L, 20L, 5L))
                : GrandTotal(30L, 30L));

            var applied = await StageApproveApplyAsync(g, "1 + 0", new[] { YearColumn });

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("failed", applied.VerifyState);
            Assert.Equal("1", await DaxAsync(g));
        }

        // ---------------------------------------------------------------- attach-mode degraded

        /// <summary>
        /// Attach mode (no LiveOrigin): the connection cannot be matched to the edited model, so calc-group
        /// presence and measure identity on the CONNECTED model are unknowable. Values matched, but under
        /// reduced fidelity — so the item is GATED, not shipped with a label. This is the rung that must never
        /// ride the thin-grid apply-with-label path above.
        /// </summary>
        [Fact]
        public async Task An_attach_mode_match_is_degraded_and_skipped_not_applied()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: false, _ => GrandTotal(1L, 1L));

            var applied = await StageApproveApplyAsync(g, "1 + 0", Array.Empty<string>());

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("degraded", applied.VerifyState);
            Assert.NotEqual("unverified", applied.VerifyState);   // NOT the thin rung: fidelity outranks it
            Assert.Contains("REDUCED fidelity", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
        }

        /// <summary>
        /// Attach mode with a divergence. The surrogate itself can cause it, so this is an OBSERVATION, not a
        /// conviction: it blocks exactly like failed but must never be reported as a proven behavior change.
        /// The <c>NotEqual("failed")</c> is the whole point of the rung.
        /// </summary>
        [Fact]
        public async Task An_attach_mode_mismatch_is_degraded_mismatch_never_failed()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: false, _ => GrandTotal(1L, 2L));

            var applied = await StageApproveApplyAsync(g, "2", Array.Empty<string>());

            Assert.Equal("skipped", applied.Status);
            Assert.Equal("degraded_mismatch", applied.VerifyState);
            Assert.NotEqual("failed", applied.VerifyState);
            Assert.Contains("not authoritative", applied.Note);
            Assert.Equal("1", await DaxAsync(g));
        }

        /// <summary>
        /// The degraded rungs are fidelity-gated, not walls: the audited override still ships one, and the
        /// verdict it ships past stays honest. (The referee's measurement never changes — only what ships.)
        /// </summary>
        [Fact]
        public async Task An_audited_override_ships_a_degraded_rewrite_and_keeps_the_honest_verdict()
        {
            using var g = await OpenAsync();
            Attach(g, trusted: false, _ => GrandTotal(1L, 1L));

            var item = await StageAsync(g, "1 + 0", Array.Empty<string>());
            await g.Engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var report = await g.Engine.ApplyPlanAsync(new[] { item.Id }, "human",
                new[] { item.Id }, "accepting the fidelity caveat for this test");
            var applied = report.Items.Single(i => i.Kind == "set_dax");

            Assert.Equal("applied", applied.Status);
            Assert.Equal("degraded", applied.VerifyState);   // honest verdict kept
            Assert.Contains("override accepted", applied.Note);
            Assert.Equal("1 + 0", await DaxAsync(g));
        }
    }
}
