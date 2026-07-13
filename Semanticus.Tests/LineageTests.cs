using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lineage &amp; Impact (Phase 1 — OFFLINE / TOM-only): the model-internal lineage graph, forward Impact, and the
    /// reverse safe-to-remove sweep ("Measure Killer"). These drive the public <see cref="IEngine"/> ops BOTH doors
    /// share. They pin the plan's Verification cases (docs/lineage-impact-plan.md): a measure→measure→column chain
    /// resolves transitively in impact; a relationship key is NOT flagged unused; an orphan measure IS flagged safe;
    /// and the graph has no dangling edge endpoints.
    /// </summary>
    public sealed class LineageTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;
        private string _table = null!;

        public async Task InitializeAsync()
        {
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions);
            await _engine.OpenAsync(TestModels.FindBim());
            _table = (await _engine.ListMeasuresAsync()).First().Table;   // a real table to hang objects on
        }

        public Task DisposeAsync() { _engine.Dispose(); return Task.CompletedTask; }

        [Fact]
        public async Task Impact_resolves_a_measure_to_measure_to_column_chain_transitively()
        {
            var t = "'" + _table.Replace("'", "''") + "'";
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "Lin_Col", "1", "agent");
            var aRef = await _engine.CreateMeasureAsync("table:" + _table, "Lin_A", $"SUM({t}[Lin_Col])", "agent");
            var bRef = await _engine.CreateMeasureAsync("table:" + _table, "Lin_B", "[Lin_A] * 2", "agent");

            // Impact of the column reaches BOTH the measure that uses it (depth 1) AND the measure that uses that
            // measure (depth 2) — the full transitive chain (correction #6: don't prune intermediate measures).
            var impact = await _engine.ImpactOfAsync(colRef);
            Assert.Contains(impact.Impacted, n => n.Ref == aRef);
            Assert.Contains(impact.Impacted, n => n.Ref == bRef);

            var a = impact.Impacted.First(n => n.Ref == aRef);
            var b = impact.Impacted.First(n => n.Ref == bRef);
            Assert.True(a.Depth < b.Depth, "the direct user must be shallower than the transitive one");
            Assert.True(impact.Measures >= 2);
            Assert.Equal(colRef, impact.Root);
        }

        [Fact]
        public async Task Unused_flags_an_orphan_measure_as_safe()
        {
            var msRef = await _engine.CreateMeasureAsync("table:" + _table, "Lin_Orphan", "1", "agent");

            var res = await _engine.UnusedObjectsAsync();
            var item = res.Items.FirstOrDefault(i => i.Ref == msRef);
            Assert.NotNull(item);
            Assert.Equal("safe", item!.Verdict);
            Assert.Equal(0, item.RefCount);
            Assert.False(string.IsNullOrEmpty(res.Caveat));    // the model-only honesty caveat is ALWAYS present
        }

        [Fact]
        public async Task Unused_does_NOT_flag_a_measure_referenced_by_a_visible_measure()
        {
            var usedRef = await _engine.CreateMeasureAsync("table:" + _table, "Lin_Used", "1", "agent");
            await _engine.CreateMeasureAsync("table:" + _table, "Lin_User", "[Lin_Used] + 1", "agent");

            var res = await _engine.UnusedObjectsAsync();
            Assert.DoesNotContain(res.Items, i => i.Ref == usedRef);    // a visible referencer ⇒ never "safe to remove"
        }

        [Fact]
        public async Task Unused_does_NOT_flag_a_relationship_key_column()
        {
            // A column that is a relationship endpoint is structurally in use even with no DAX references — it must
            // never appear in the safe-to-remove list (the exact false-positive the plan calls out).
            var graph = await _engine.GetLineageAsync();
            var relEdge = graph.Edges.FirstOrDefault(e => e.Kind == "relationship");
            Assert.NotNull(relEdge);   // AdventureWorks has relationships

            var res = await _engine.UnusedObjectsAsync();
            Assert.DoesNotContain(res.Items, i => i.Ref == relEdge!.From);   // FK column — structurally used
            Assert.DoesNotContain(res.Items, i => i.Ref == relEdge.To);      // PK column — structurally used
        }

        [Fact]
        public async Task Lineage_graph_edges_never_dangle_and_carry_the_dependency_chain()
        {
            var t = "'" + _table.Replace("'", "''") + "'";
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "Lin_GCol", "1", "agent");
            await _engine.CreateMeasureAsync("table:" + _table, "Lin_GMeas", $"SUM({t}[Lin_GCol])", "agent");

            var g = await _engine.GetLineageAsync();
            var nodeRefs = new HashSet<string>(g.Nodes.Select(n => n.Ref));

            // Every edge endpoint is a real node (no dangling refs).
            Assert.All(g.Edges, e => { Assert.Contains(e.From, nodeRefs); Assert.Contains(e.To, nodeRefs); });

            // The measure→column DAX dependency edge is present, and tables contain their children.
            var measRef = g.Nodes.First(n => n.Name == "Lin_GMeas").Ref;
            Assert.Contains(g.Edges, e => e.Kind == "dependsOn" && e.From == measRef && e.To == colRef);
            Assert.Contains(g.Edges, e => e.Kind == "contains");
            Assert.False(string.IsNullOrEmpty(g.Caveat));
        }

        [Fact]
        public async Task ImpactOf_throws_on_an_unknown_ref()
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _engine.ImpactOfAsync("measure:NoSuchTable/NoSuchMeasure"));
        }

        // ---- conservative safe-to-remove guards (adversarial-review fixes) ---------------------------------------

        [Fact]
        public async Task Unused_does_NOT_flag_a_measure_used_only_by_a_calculation_item()
        {
            // A calc item references the measure; CalculationItem is not IHideableObject so TOM's AnyVisible can't
            // see it. The sweep must still treat the measure as in use (never a removal candidate).
            await _engine.SetCompatibilityLevelAsync(1500, "agent");      // calc groups need CL >= 1470
            var msRef = await _engine.CreateMeasureAsync("table:" + _table, "Lin_CIUsed", "1", "agent");
            var cgRef = await _engine.CreateCalculationGroupAsync("Lin_CG", "agent");
            await _engine.CreateCalculationItemAsync(cgRef, "Lin_CI", "[Lin_CIUsed] + 1", "agent");

            var res = await _engine.UnusedObjectsAsync();
            Assert.DoesNotContain(res.Items, i => i.Ref == msRef);
        }

        [Fact]
        public async Task Unused_does_NOT_flag_an_OLS_secured_column_as_safe()
        {
            // Object-Level Security is metadata, never in the DAX ReferencedBy graph. A column secured by OLS (and
            // used by nothing else) must NOT be reported safe — deleting it silently drops a security control.
            await _engine.SetCompatibilityLevelAsync(1500, "agent");      // OLS needs CL >= 1400
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "Lin_OlsCol", "1", "agent");
            await _engine.CreateRoleAsync("Lin_OlsRole", "Read", "agent");
            await _engine.SetColumnObjectPermissionAsync("Lin_OlsRole", colRef, "None", "agent");

            var res = await _engine.UnusedObjectsAsync();
            Assert.DoesNotContain(res.Items, i => i.Ref == colRef);
        }

        [Fact]
        public async Task Unused_treats_a_visible_measure_on_a_hidden_table_as_in_use()
        {
            // The common hidden "_Measures" home-table convention: a measure is visible iff !IsHidden, regardless of
            // its table. A measure referenced only by a VISIBLE measure on a HIDDEN table must not be demoted to dead.
            var tRef = await _engine.CreateTableAsync("Lin_HiddenHome", "agent");
            await _engine.SetObjectPropertyAsync(tRef, "IsHidden", "true", "agent");
            var baseRef = await _engine.CreateMeasureAsync(tRef, "Lin_HBase", "1", "agent");
            await _engine.CreateMeasureAsync(tRef, "Lin_HUser", "[Lin_HBase] + 1", "agent");   // visible measure, hidden table

            var res = await _engine.UnusedObjectsAsync();
            Assert.DoesNotContain(res.Items, i => i.Ref == baseRef);    // referenced by a (visible) measure ⇒ in use
        }

        [Fact]
        public async Task ImpactOf_a_table_includes_its_own_measures()
        {
            // A table root must expand into its children (correction: Table.ReferencedBy alone misses them).
            var impact = await _engine.ImpactOfAsync("table:" + _table);
            Assert.NotEmpty(impact.Impacted);
            Assert.True(impact.Measures > 0, "a table's impact should include the measures it contains");
        }

        [Fact]
        public async Task AnalyzeReports_excludes_a_column_used_only_by_a_local_report_from_safe()
        {
            // The headline gap-closer: a descriptive column no measure references is model-only "safe", but a report
            // displays it — so a report-aware sweep must drop it off the safe list.
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "Lin_RptCol", "1", "agent");
            var baseline = await _engine.UnusedObjectsAsync();
            Assert.Contains(baseline.Items, i => i.Ref == colRef && i.Verdict == "safe");   // model-only: looks safe

            var entity = _table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"Lin_RptCol\" } } } ] } } } } }";

            var root = Path.Combine(Path.GetTempPath(), "sem_rpt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = Path.Combine(root, "R.Report", "definition");
            var visualDir = Path.Combine(def, "pages", "p1", "visuals", "v1");
            try
            {
                Directory.CreateDirectory(visualDir);
                File.WriteAllText(Path.Combine(def, "report.json"), "{}");
                File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);

                var res = await _engine.AnalyzeReportsAsync(new[] { root });
                Assert.Equal(1, res.ReportsRead);
                Assert.Contains(res.ModelFieldsUsed, r => r == colRef);
                Assert.DoesNotContain(res.Unused.Items, i => i.Ref == colRef);   // report-aware: no longer "safe"
            }
            finally { try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task AnalyzeReports_surfaces_page_and_visual_level_field_usage()
        {
            // The page+visual drill: a report's usage rolls up per visual, not just per report.
            var colRef = await _engine.CreateCalculatedColumnAsync("table:" + _table, "Lin_VisCol", "1", "agent");
            var entity = _table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"visualType\": \"lineChart\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"Lin_VisCol\" } } } ] } } } } }";

            var root = Path.Combine(Path.GetTempPath(), "sem_rptv_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = Path.Combine(root, "R.Report", "definition");
            var visualDir = Path.Combine(def, "pages", "Overview", "visuals", "vA");
            try
            {
                Directory.CreateDirectory(visualDir);
                File.WriteAllText(Path.Combine(def, "report.json"), "{}");
                File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);

                var res = await _engine.AnalyzeReportsAsync(new[] { root });
                var rpt = Assert.Single(res.Reports);
                var vis = Assert.Single(rpt.Visuals);
                Assert.Equal("Overview", vis.Page);
                Assert.Equal("vA", vis.Visual);
                Assert.Equal("lineChart", vis.VisualType);
                Assert.Contains(vis.UsedRefs, r => r == colRef);
            }
            finally { try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task ImpactOf_a_relationship_is_not_empty()
        {
            // A relationship root previously returned an empty (misleading "safe to change") set; it must surface its
            // endpoint columns and their dependants.
            var graph = await _engine.GetModelGraphAsync();
            var rel = graph.Relationships.FirstOrDefault();
            Assert.NotNull(rel);
            var impact = await _engine.ImpactOfAsync("relationship:" + rel!.Name);
            Assert.NotEmpty(impact.Impacted);
        }
    }
}
