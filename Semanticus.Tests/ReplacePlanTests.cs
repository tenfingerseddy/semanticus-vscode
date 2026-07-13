using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Find &amp; Replace Phase 3 (bulk replace as a Change Plan) + the Phase 2 preview. Pins the engine-level
    /// guarantees both doors share: propose_replace emits the RIGHT KIND of item per MatchClass, NEVER emits a
    /// text edit over a DAX reference (the hard block, now at plan scale), rides apply_plan's EXISTING Pro gate
    /// (bulk = Pro, one-at-a-time = free), and a bulk apply is ONE undoable transaction.
    /// </summary>
    public sealed class ReplacePlanTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<(LocalEngine engine, string table, string tq)> OpenAsync(bool pro)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListMeasuresAsync()).First().Table;
            return (engine, table, "'" + table.Replace("'", "''") + "'");
        }

        private static Task<ChangePlanView> Propose(LocalEngine e, string find, string replace, string[] fields, string scope = null, bool caseSensitive = true)
            => e.ProposeReplaceAsync(new SearchOptions { Query = find, CaseSensitive = caseSensitive, Fields = fields, Scope = scope }, replace, 0, "human");

        // ---- item-kind routing ------------------------------------------------------------------------------

        [Fact]
        public async Task Propose_replace_builds_the_right_item_kind_per_match_class()
        {
            var (engine, table, tq) = await OpenAsync(pro: false);
            using (engine)
            {
                var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "RP_Tok", "1", "agent");
                var mRef = await engine.CreateMeasureAsync("table:" + table, "RP_Meas",
                    $"VAR y = \"RP_Tok literal\" // RP_Tok comment\nRETURN SUM ( {tq}[RP_Tok] ) + LEN ( y )", "agent");
                await engine.SetDescriptionAsync(mRef, "about RP_Tok values", "agent");
                await engine.SetObjectPropertyAsync(mRef, "DisplayFolder", "RP_Tok Folder", "agent");

                var view = await Propose(engine, "RP_Tok", "RP_New",
                    new[] { "name", "description", "displayFolder", "expression" }, "table:" + table);

                // ObjectName → a rename item, opt-in ("proposed"), with the blast radius in the rationale.
                var ren = Assert.Single(view.Items, i => i.Kind == "rename" && i.ObjectRef == colRef);
                Assert.Equal("proposed", ren.Status);
                Assert.Equal("RP_New", ren.After);
                Assert.Contains("reference", ren.Rationale, StringComparison.OrdinalIgnoreCase);

                // PlainText → pre-approved set items of the matching kind.
                var desc = Assert.Single(view.Items, i => i.Kind == "set_description" && i.ObjectRef == mRef);
                Assert.Equal("approved", desc.Status);
                Assert.Equal("about RP_New values", desc.After);
                var folder = Assert.Single(view.Items, i => i.Kind == "set_display_folder" && i.ObjectRef == mRef);
                Assert.Equal("approved", folder.Status);
                Assert.Equal("RP_New Folder", folder.After);

                // DAX literal/comment → ONE set_dax item, spliced span-wise: the reference span is untouched.
                var dax = Assert.Single(view.Items, i => i.Kind == "set_dax" && i.ObjectRef == mRef);
                Assert.Equal("proposed", dax.Status);                    // changes results → opt-in
                Assert.Contains("\"RP_New literal\"", dax.After);        // literal edited
                Assert.Contains("// RP_New comment", dax.After);         // comment edited
                Assert.Contains("[RP_Tok]", dax.After);                  // the reference is NOT poked
                Assert.Null(dax.VerifyGroupBy);                          // not equivalence-gated (it changes results by design)

                // The reference match is excluded and reported honestly on the plan note.
                Assert.NotNull(view.Note);
                Assert.Contains("reference", view.Note, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Propose_replace_never_emits_an_item_for_a_reference_only_expression()
        {
            var (engine, table, tq) = await OpenAsync(pro: false);
            using (engine)
            {
                await engine.CreateCalculatedColumnAsync("table:" + table, "RP_RefOnly", "1", "agent");
                var mRef = await engine.CreateMeasureAsync("table:" + table, "RP_RefMeas", $"SUM ( {tq}[RP_RefOnly] )", "agent");

                var view = await Propose(engine, "RP_RefOnly", "Nope", new[] { "expression" }, "table:" + table);

                Assert.DoesNotContain(view.Items, i => i.ObjectRef == mRef);   // the hard block, at plan scale
                Assert.NotNull(view.Note);
                Assert.Contains("reference", view.Note, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Propose_replace_surfaces_a_name_collision_as_a_skipped_item()
        {
            var (engine, table, _) = await OpenAsync(pro: false);
            using (engine)
            {
                await engine.CreateMeasureAsync("table:" + table, "RP_CollTarget", "1", "agent");
                var bRef = await engine.CreateMeasureAsync("table:" + table, "RP_CollSource", "2", "agent");

                var view = await Propose(engine, "RP_CollSource", "RP_CollTarget", new[] { "name" }, "table:" + table);

                var it = Assert.Single(view.Items, i => i.Kind == "rename" && i.ObjectRef == bRef);
                Assert.Equal("skipped", it.Status);                       // can never apply — surfaced, not silent
                Assert.Contains("already exists", it.Note);
            }
        }

        [Fact]
        public async Task Propose_replace_that_empties_a_field_is_surfaced_as_skipped()
        {
            var (engine, table, _) = await OpenAsync(pro: false);
            using (engine)
            {
                var mRef = await engine.CreateMeasureAsync("table:" + table, "RP_EmptyDesc", "1", "agent");
                await engine.SetDescriptionAsync(mRef, "RP_OnlyText", "agent");

                var view = await Propose(engine, "RP_OnlyText", "", new[] { "description" }, "table:" + table);

                var it = Assert.Single(view.Items, i => i.Kind == "set_description" && i.ObjectRef == mRef);
                Assert.Equal("skipped", it.Status);
                Assert.Contains("empties", it.Note);
            }
        }

        // ---- gate integrity: opt-in classes stay opt-in through value revisions ------------------------------

        [Fact]
        public async Task Revising_an_opt_in_item_never_self_approves()
        {
            var (engine, table, tq) = await OpenAsync(pro: false);
            using (engine)
            {
                var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "RP_OptTok", "1", "agent");
                await engine.CreateMeasureAsync("table:" + table, "RP_OptMeas",
                    $"VAR y = \"RP_OptTok x\" RETURN SUM ( {tq}[RP_OptTok] ) + LEN ( y )", "agent");

                var view = await Propose(engine, "RP_OptTok", "RP_OptNew", new[] { "name", "expression" }, "table:" + table);
                var ren = Assert.Single(view.Items, i => i.Kind == "rename" && i.ObjectRef == colRef);
                var dax = Assert.Single(view.Items, i => i.Kind == "set_dax");

                // Editing the value of a proposed opt-in item (a result-changing DAX splice) keeps it proposed.
                Assert.Equal("proposed", (await engine.SetPlanItemAsync(dax.Id, dax.After, null, "human")).Items.Single(x => x.Id == dax.Id).Status);

                // Approve the rename, then revise its value: the consent covered the OLD name, so the revision
                // demotes it back to proposed (fresh consent required).
                await engine.SetPlanItemAsync(ren.Id, null, true, "human");
                Assert.Equal("proposed", (await engine.SetPlanItemAsync(ren.Id, "RP_OptOther", null, "human")).Items.Single(x => x.Id == ren.Id).Status);

                // Approving WITH the value in one call is still an explicit consent for that value — approved.
                Assert.Equal("approved", (await engine.SetPlanItemAsync(ren.Id, "RP_OptThird", true, "human")).Items.Single(x => x.Id == ren.Id).Status);
            }
        }

        // ---- the Pro gate (the EXISTING apply_plan chokepoint — no new gate) --------------------------------

        [Fact]
        public async Task Free_tier_applies_one_replace_item_but_a_bulk_apply_is_refused()
        {
            var (engine, table, _) = await OpenAsync(pro: false);
            using (engine)
            {
                var aRef = await engine.CreateMeasureAsync("table:" + table, "RP_FreeA", "1", "agent");
                var bRef = await engine.CreateMeasureAsync("table:" + table, "RP_FreeB", "2", "agent");
                await engine.SetDescriptionAsync(aRef, "gate RP_Gate a", "agent");
                await engine.SetDescriptionAsync(bRef, "gate RP_Gate b", "agent");

                var view = await Propose(engine, "RP_Gate", "RP_Won", new[] { "description" }, "table:" + table);
                var approved = view.Items.Where(i => i.Status == "approved").Select(i => i.Id).ToArray();
                Assert.Equal(2, approved.Length);

                // Bulk (both items) is the Pro primitive — refused BEFORE any mutation on free.
                await Assert.ThrowsAsync<EntitlementException>(() => engine.ApplyPlanAsync(Array.Empty<string>(), "human"));
                var untouched = (await engine.GetObjectPropertiesAsync(aRef)).First(p => p.Name == "Description").Value;
                Assert.Equal("gate RP_Gate a", untouched);

                // One item at a time stays free, and each single apply is undoable.
                var one = await engine.ApplyPlanAsync(new[] { approved[0] }, "human");
                Assert.Equal(1, one.AppliedCount);
                await engine.UndoAsync("human");
                Assert.Equal("gate RP_Gate a", (await engine.GetObjectPropertiesAsync(aRef)).First(p => p.Name == "Description").Value);
            }
        }

        [Fact]
        public async Task Pro_tier_bulk_replace_applies_atomically_and_one_undo_reverts_everything()
        {
            var (engine, table, tq) = await OpenAsync(pro: true);
            using (engine)
            {
                var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "RP_BulkTok", "1", "agent");
                var mRef = await engine.CreateMeasureAsync("table:" + table, "RP_BulkMeas", $"SUM ( {tq}[RP_BulkTok] )", "agent");
                await engine.SetDescriptionAsync(mRef, "sums RP_BulkTok", "agent");

                var view = await Propose(engine, "RP_BulkTok", "RP_BulkNew", new[] { "name", "description" }, "table:" + table);
                var ren = Assert.Single(view.Items, i => i.Kind == "rename");
                Assert.Single(view.Items, i => i.Kind == "set_description" && i.Status == "approved");

                // Renames are opt-in: approve it explicitly, then apply the whole plan in one shot (Pro).
                await engine.SetPlanItemAsync(ren.Id, null, true, "human");
                var report = await engine.ApplyPlanAsync(null, "human");
                Assert.Equal(2, report.AppliedCount);
                Assert.Equal(0, report.FailedCount);

                // The rename went through FormulaFixup: the measure's DAX reference was rewritten.
                var expr = (await engine.ListMeasuresAsync()).First(m => m.Name == "RP_BulkMeas").Expression;
                Assert.Contains("[RP_BulkNew]", expr);
                Assert.DoesNotContain("[RP_BulkTok]", expr);
                Assert.Equal("sums RP_BulkNew", (await engine.GetObjectPropertiesAsync(mRef)).First(p => p.Name == "Description").Value);

                // ONE undo reverts the whole batch (rename + description).
                await engine.UndoAsync("human");
                var back = (await engine.ListMeasuresAsync()).First(m => m.Name == "RP_BulkMeas");
                Assert.Contains("[RP_BulkTok]", back.Expression);
                Assert.Equal("sums RP_BulkTok", (await engine.GetObjectPropertiesAsync(mRef)).First(p => p.Name == "Description").Value);
            }
        }

        // ---- Phase 2 preview (rehearse, never mutate) -------------------------------------------------------

        [Fact]
        public async Task Preview_reports_before_after_and_blast_radius_without_mutating()
        {
            var (engine, table, tq) = await OpenAsync(pro: false);
            using (engine)
            {
                var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "RP_PvTok", "1", "agent");
                await engine.CreateMeasureAsync("table:" + table, "RP_PvMeas", $"SUM ( {tq}[RP_PvTok] )", "agent");

                var pv = await engine.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = colRef, Field = "name", Find = "RP_PvTok", Replace = "RP_PvNew", Preview = true,
                }, "human");

                Assert.True(pv.Preview);
                Assert.False(pv.Changed);
                Assert.Null(pv.Blocked);
                Assert.Equal("RP_PvTok", pv.Before);
                Assert.Equal("RP_PvNew", pv.After);
                Assert.True(pv.References >= 1, $"expected the referencing measure counted; got {pv.References}");
                Assert.Contains((await engine.ListColumnsAsync()), c => c.Ref == colRef);   // nothing renamed
            }
        }

        [Fact]
        public async Task Preview_of_a_blocked_reference_replace_reports_blocked_instead_of_throwing()
        {
            var (engine, table, tq) = await OpenAsync(pro: false);
            using (engine)
            {
                await engine.CreateCalculatedColumnAsync("table:" + table, "RP_PvRef", "1", "agent");
                var mRef = await engine.CreateMeasureAsync("table:" + table, "RP_PvRefMeas", $"SUM ( {tq}[RP_PvRef] )", "agent");

                var pv = await engine.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = mRef, Field = "expression", Find = "RP_PvRef", Replace = "Nope", Preview = true,
                }, "human");

                Assert.True(pv.Preview);
                Assert.False(pv.Changed);
                Assert.NotNull(pv.Blocked);
                Assert.Contains("rename", pv.Blocked, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("[RP_PvRef]", (await engine.ListMeasuresAsync()).First(m => m.Name == "RP_PvRefMeas").Expression);
            }
        }

        // ---- the set_m item (M partitions need CL >= 1400, so this runs on a purpose-built fixture) ----------

        [Fact]
        public async Task Propose_replace_in_M_yields_an_opt_in_set_m_item_that_applies_and_undoes()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus-rpm-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            System.IO.File.WriteAllText(path, MFixtureBim);
            var sessions = new SessionManager();
            try
            {
                var engine = new LocalEngine(sessions, new Fake(pro: false));
                await engine.OpenAsync(path);

                var view = await engine.ProposeReplaceAsync(
                    new SearchOptions { Query = "RPM_Token", Fields = new[] { "mExpression" } }, "RPM_Swapped", 0, "human");

                var it = Assert.Single(view.Items, i => i.Kind == "set_m");
                Assert.Equal("proposed", it.Status);                      // literal M edit → opt-in
                Assert.Contains("RPM_Swapped", it.After);
                Assert.Contains("not auto-fixed", it.Note + " " + it.Rationale, StringComparison.OrdinalIgnoreCase);

                // Gate integrity: revising the M text never self-approves the opt-in item (it stays proposed).
                Assert.Equal("proposed", (await engine.SetPlanItemAsync(it.Id, it.After, null, "human")).Items.Single(x => x.Id == it.Id).Status);

                await engine.SetPlanItemAsync(it.Id, null, true, "human");
                var report = await engine.ApplyPlanAsync(new[] { it.Id }, "human");   // single item — free
                Assert.Equal(1, report.AppliedCount);
                Assert.Contains("RPM_Swapped", await engine.GetPartitionMAsync(it.ObjectRef));

                await engine.UndoAsync("human");
                Assert.Contains("RPM_Token", await engine.GetPartitionMAsync(it.ObjectRef));
            }
            finally { sessions.Dispose(); try { System.IO.File.Delete(path); } catch { } }
        }

        // A minimal PBI-V3 model (CL 1500 so M partitions are allowed) whose partition M mentions a token.
        private const string MFixtureBim = """{"name":"RpmModel","compatibilityLevel":1500,"model":{"defaultPowerBIDataSourceVersion":"powerBI_V3","tables":[{"name":"Facts","columns":[{"name":"Id","dataType":"int64","sourceColumn":"Id"}],"partitions":[{"name":"Facts","mode":"import","source":{"type":"m","expression":"let Source = #table(type table [Id = Int64.Type], {}) /* mentions RPM_Token here */ in Source"}}],"measures":[{"name":"RpmMeasure","expression":"1"}]}]}}""";
    }
}
