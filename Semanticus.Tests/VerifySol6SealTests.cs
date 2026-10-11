using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;
using Xunit.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier attacks on m11/sol6-seal (Sol join 6, area B item 2): Sol's exact scenario through the assistant's own door under
    /// Locked, the person's single undo of the same batch, cascades the lane's list does not name (a culture's linguistic schema
    /// replaced straight in TOM, a level added, moved and deleted, a calculation item added, moved and deleted, an aggregation's
    /// base table and base column clearing each other), each set there and back in one batch, and ordinary edits that must add
    /// no seal step.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifySol6SealTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _safeRoot;

        public VerifySol6SealTests(ITestOutputHelper output, RestoreRootFixture fixture)
        {
            _out = output;
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vs6-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private const string Json1 = "{\"Version\":\"1.0.0\",\"Language\":\"fr-FR\",\"DynamicImprovement\":\"HighConfidence\"}";
        private const string Xml1 = "<LinguisticSchema Language=\"fr-FR\" xmlns=\"http://schemas.microsoft.com/sqlserver/2016/linguisticschema\"></LinguisticSchema>";

        /// <summary>Sol's model: table T with integer columns A (the key) and B, plus what the cascade candidates need.</summary>
        private string Model()
        {
            var db = new TOM.Database { Name = "S6", ID = "S6", CompatibilityLevel = 1604, Model = new TOM.Model { Culture = "en-US", DiscourageImplicitMeasures = true } };
            var t = new TOM.Table { Name = "T", LineageTag = "t-t" };
            t.Columns.Add(new TOM.DataColumn { Name = "A", DataType = TOM.DataType.Int64, SourceColumn = "A", IsKey = true, LineageTag = "c-a" });
            t.Columns.Add(new TOM.DataColumn { Name = "B", DataType = TOM.DataType.Int64, SourceColumn = "B", LineageTag = "c-b" });
            t.Columns.Add(new TOM.DataColumn { Name = "C", DataType = TOM.DataType.Int64, SourceColumn = "C", LineageTag = "c-c" });
            t.Measures.Add(new TOM.Measure { Name = "M", Expression = "1", LineageTag = "m-m" });
            t.Hierarchies.Add(new TOM.Hierarchy { Name = "H", LineageTag = "h-h", Levels = { new TOM.Level { Name = "A", Column = t.Columns["A"], Ordinal = 0, LineageTag = "l-a" }, new TOM.Level { Name = "B", Column = t.Columns["B"], Ordinal = 1, LineageTag = "l-b" } } });
            t.Partitions.Add(new TOM.Partition { Name = "P", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(t);
            var agg = new TOM.Table { Name = "Agg", LineageTag = "t-agg" };
            agg.Columns.Add(new TOM.DataColumn { Name = "A", DataType = TOM.DataType.Int64, SourceColumn = "A", LineageTag = "c-agga", AlternateOf = new TOM.AlternateOf { BaseColumn = t.Columns["A"], Summarization = TOM.SummarizationType.GroupBy } });
            agg.Partitions.Add(new TOM.Partition { Name = "PA", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(agg);
            var cg = new TOM.Table { Name = "Scenario", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 0 } };
            cg.Columns.Add(new TOM.DataColumn { Name = "Scenario", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cgn" });
            cg.Partitions.Add(new TOM.Partition { Name = "Scenario", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Actual", Expression = "SELECTEDMEASURE()", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Plan", Expression = "SELECTEDMEASURE() * 2", Ordinal = -1 });
            db.Model.Tables.Add(cg);
            db.Model.Cultures.Add(new TOM.Culture { Name = "fr-FR", LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = Json1 } });
            var dir = Path.Combine(_root, "m-" + Guid.NewGuid().ToString("N").Substring(0, 4));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private async Task<string> WholeAsync(SessionManager sessions)
        {
            return await sessions.Current.ReadAsync(m =>
            {
                var db = m.Database.TOMDatabase;
                var json = TOM.JsonSerializer.SerializeDatabase(db, new TOM.SerializeOptions { IgnoreTimestamps = true });
                var dir = Path.Combine(_root, "tmdl-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    TOM.TmdlSerializer.SerializeDatabaseToFolder(db, dir);
                    var tmdl = string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                        .Select(f => "== " + Path.GetRelativePath(dir, f) + "\n" + File.ReadAllText(f)));
                    return StripAudit(json + "\n#### TMDL\n" + tmdl);
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });
        }

        // The Verified Edits trail is append-only by design, so it is not part of the bar.
        private static string StripAudit(string whole)
        {
            var cut = whole.IndexOf("\n#### TMDL", StringComparison.Ordinal);
            var node = System.Text.Json.Nodes.JsonNode.Parse(whole.Substring(0, cut));
            if (node?["model"]?["annotations"] is System.Text.Json.Nodes.JsonArray anns)
            {
                foreach (var a in anns.ToList()) if (((string)a?["name"])?.StartsWith("Semanticus_VerifiedEdits", StringComparison.Ordinal) == true) anns.Remove(a);
                if (anns.Count == 0) ((System.Text.Json.Nodes.JsonObject)node["model"]).Remove("annotations");
            }
            var tmdl = string.Join("\n", whole.Substring(cut).Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("annotation Semanticus_VerifiedEdits", StringComparison.Ordinal)));
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n" + tmdl;
        }

        private static string Diff(string a, string b)
        {
            var n = Math.Min(a.Length, b.Length);
            var at = 0;
            while (at < n && a[at] == b[at]) at++;
            string Cut(string x) => x.Substring(Math.Max(0, at - 90), Math.Min(220, x.Length - Math.Max(0, at - 90))).Replace("\n", " ");
            return "was [" + Cut(a) + "] now [" + Cut(b) + "]";
        }

        private static async Task<int> StepsAsync(SessionManager s) => await s.Current.ReadAsync(_ => s.Current.UndoStateNow().Steps);
        private static async Task<string> KeysAsync(SessionManager s) => await s.Current.ReadAsync(m => "A=" + m.Tables["T"].Columns["A"].IsKey + " B=" + m.Tables["T"].Columns["B"].IsKey + " C=" + m.Tables["T"].Columns["C"].IsKey);

        // ---- (1) Sol's exact scenario through the real assistant door ------------------------------------------------------

        [Theory]
        [InlineData("column:T/B", "column:T/A", null)]
        [InlineData("column:T/B", "column:T/C", "column:T/A")]
        [InlineData("column:T/C", "column:T/B", "column:T/A")]
        public async Task Locked_assistant_dry_run_of_set_properties_that_moves_the_key_away_and_back_keeps_A_the_key(string r1, string r2, string r3)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Model());
            AgentPolicyStore.SetPreset("locked", "human");
            var before = await WholeAsync(sessions);
            var undoBefore = await sessions.Current.ReadAsync(_ => JsonSerializer.Serialize(sessions.Current.UndoStateNow()));
            var rev = sessions.Current.Revision;
            var refs = new[] { r1, r2, r3 }.Where(x => x != null).ToArray();
            var report = await AssistantEngine.ForAssistant(engine).DryRunOpAsync("set_properties", JsonSerializer.Serialize(new { objRefs = refs, propertyName = "IsKey", value = "true" }));
            _out.WriteLine(JsonSerializer.Serialize(report));
            Assert.True(report.WouldSucceed, report.Error);
            Assert.Equal("A=True B=False C=False", await KeysAsync(sessions));
            var after = await WholeAsync(sessions);
            Assert.True(before == after, "the rehearsal changed the model: " + Diff(before, after));
            Assert.Equal(undoBefore, await sessions.Current.ReadAsync(_ => JsonSerializer.Serialize(sessions.Current.UndoStateNow())));
            Assert.Equal(rev, sessions.Current.Revision);
            Assert.Empty(await engine.ListPendingApprovalsAsync());
        }

        /// <summary>The person's door: ONE press of Undo takes back the whole batch (Sol's probe pressed it once).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("column:T/B", "column:T/A", null)]
        [InlineData("column:T/B", "column:T/C", "column:T/A")]
        public async Task The_persons_one_undo_of_a_batch_that_moves_the_key_away_and_back_keeps_A_the_key(string r1, string r2, string r3)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Model());
            var before = await WholeAsync(sessions);
            var s0 = await StepsAsync(sessions);
            await engine.SetObjectPropertiesAsync(new[] { r1, r2, r3 }.Where(x => x != null).ToArray(), "IsKey", "true", "human");
            Assert.Equal("A=True B=False C=False", await KeysAsync(sessions));
            var steps = await StepsAsync(sessions) - s0;
            _out.WriteLine("steps for the batch: " + steps);
            var withEdit = await WholeAsync(sessions);
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            Assert.Equal("A=True B=False C=False", await KeysAsync(sessions));
            var now = await WholeAsync(sessions);
            Assert.True(before == now, "undo: " + Diff(before, now));
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            now = await WholeAsync(sessions);
            Assert.True(withEdit == now, "redo: " + Diff(withEdit, now));
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            now = await WholeAsync(sessions);
            Assert.True(before == now, "undo again: " + Diff(before, now));
            Assert.Equal(1, steps);
        }

        // ---- (2) cascades the lane's list does not name, there and back in one batch ----------------------------------------

        // Content-equal: arrays of named objects sorted by name, so only sibling ORDER is forgiven (TE2's undelete appends at the end,
        // a known and logged note from m11/undo-seal), never a value such as an ordinal.
        private static string ContentOnly(string whole)
        {
            var json = whole.Substring(0, whole.IndexOf("\n#### TMDL", StringComparison.Ordinal));
            static System.Text.Json.Nodes.JsonNode Sort(System.Text.Json.Nodes.JsonNode n)
            {
                if (n is System.Text.Json.Nodes.JsonObject o) { foreach (var kv in o.ToList()) if (kv.Value != null) o[kv.Key] = Sort(kv.Value.DeepClone()); return o; }
                if (n is System.Text.Json.Nodes.JsonArray a)
                {
                    var items = a.Select(x => x == null ? null : Sort(x.DeepClone())).ToList();
                    if (items.All(x => x is System.Text.Json.Nodes.JsonObject ob && ob["name"] != null)) items = items.OrderBy(x => (string)x["name"], StringComparer.Ordinal).ToList();
                    return new System.Text.Json.Nodes.JsonArray(items.ToArray());
                }
                return n;
            }
            return Sort(System.Text.Json.Nodes.JsonNode.Parse(json)).ToJsonString();
        }

        private async Task<string> ThereAndBackAsync(string label, Action<Model> batch, bool expectNetNil = true, bool strict = true)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Model());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            var before = await WholeAsync(sessions);
            var s0 = await StepsAsync(sessions);
            await sessions.Current.MutateAsync("human", label, batch);
            var after = await WholeAsync(sessions);
            var steps = await StepsAsync(sessions) - s0;
            _out.WriteLine(label + ": " + steps + " step(s), net nil " + (after == before) + ", sealed: " + string.Join(", ", settings.Sealed));
            if (expectNetNil && after != before) _out.WriteLine(label + " NOTE: the batch did not end where it began: " + Diff(before, after));
            if (steps <= 0) return after == before ? null : label + ": changed the model and left no undo step";
            bool Same(string want, string got)
            {
                if (want == got) return true;
                if (strict || ContentOnly(want) != ContentOnly(got)) return false;
                _out.WriteLine(label + ": content equal, sibling order differs (TE2 undelete appends)");
                return true;
            }
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            var now = await WholeAsync(sessions);
            if (!Same(before, now)) return label + " undo: " + Diff(before, now);
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            now = await WholeAsync(sessions);
            if (!Same(after, now)) return label + " redo: " + Diff(after, now);
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            now = await WholeAsync(sessions);
            if (!Same(before, now)) return label + " undo again: " + Diff(before, now);
            if (settings.Failures.Count > 0) return label + ": a seal step failed: " + string.Join("; ", settings.Failures);
            return null;
        }

        [Fact]
        public async Task A_cultures_linguistic_schema_replaced_in_TOM_and_put_back_in_one_batch_takes_back_exactly()
        {
            var fails = new List<string>();
            void Add(string f) { if (f != null) fails.Add(f); }
            Add(await ThereAndBackAsync("json, xml, json", m => { var c = m.Cultures["fr-FR"]; c.Content = Xml1; c.Content = Json1; }));
            Add(await ThereAndBackAsync("json, empty, json", m => { var c = m.Cultures["fr-FR"]; c.Content = ""; c.Content = Json1; }));
            Add(await ThereAndBackAsync("json, empty, xml, empty, json", m => { var c = m.Cultures["fr-FR"]; c.Content = ""; c.Content = Xml1; c.Content = null; c.Content = Json1; }));
            foreach (var f in fails) _out.WriteLine("FAIL " + f);
            Assert.Empty(fails);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_level_added_moved_and_deleted_in_one_batch_takes_back_exactly()
        {
            var fails = new List<string>();
            void Add(string f) { if (f != null) fails.Add(f); }
            Add(await ThereAndBackAsync("add C, move to 0, delete", m => { var h = m.Tables["T"].Hierarchies["H"]; var l = h.AddLevel(m.Tables["T"].Columns["C"]); l.Ordinal = 0; l.Delete(); }));
            Add(await ThereAndBackAsync("move B to 0, delete A, undelete-free re-add A at 0", m =>
            {
                var h = m.Tables["T"].Hierarchies["H"];
                h.Levels["B"].Ordinal = 0; h.Levels["A"].Delete(); var a = h.AddLevel(m.Tables["T"].Columns["A"], "A"); a.Ordinal = 0;
            }, expectNetNil: false, strict: false));
            Add(await ThereAndBackAsync("delete A, re-add A, move to 0", m =>
            {
                var h = m.Tables["T"].Hierarchies["H"];
                h.Levels["A"].Delete(); var a = h.AddLevel(m.Tables["T"].Columns["A"], "A"); a.Ordinal = 0;
            }, expectNetNil: false, strict: false));
            foreach (var f in fails) _out.WriteLine("FAIL " + f);
            Assert.Empty(fails);
        }

        [Fact]
        public async Task A_calculation_item_added_moved_and_deleted_in_one_batch_takes_back_exactly()
        {
            var fails = new List<string>();
            void Add(string f) { if (f != null) fails.Add(f); }
            Add(await ThereAndBackAsync("add X, move to 0, delete", m => { var g = (CalculationGroupTable)m.Tables["Scenario"]; var x = g.AddCalculationItem("X", "SELECTEDMEASURE()"); x.Ordinal = 0; x.Delete(); }, expectNetNil: false));
            Add(await ThereAndBackAsync("move Plan to 0, move back to -1", m => { var g = (CalculationGroupTable)m.Tables["Scenario"]; g.CalculationItems["Plan"].Ordinal = 0; g.CalculationItems["Plan"].Ordinal = -1; }, expectNetNil: false));
            foreach (var f in fails) _out.WriteLine("FAIL " + f);
            Assert.Empty(fails);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_aggregations_base_table_and_base_column_clearing_each_other_there_and_back_takes_back_exactly()
        {
            var fails = new List<string>();
            void Add(string f) { if (f != null) fails.Add(f); }
            Add(await ThereAndBackAsync("base table, base column back, GroupBy back", m =>
            {
                var alt = m.Tables["Agg"].Columns["A"].AlternateOf;
                alt.BaseTable = m.Tables["T"]; alt.BaseColumn = m.Tables["T"].Columns["A"]; alt.Summarization = SummarizationType.GroupBy;
            }));
            Add(await ThereAndBackAsync("base column B, base column A", m =>
            {
                var alt = m.Tables["Agg"].Columns["A"].AlternateOf;
                alt.BaseColumn = m.Tables["T"].Columns["B"]; alt.BaseColumn = m.Tables["T"].Columns["A"];
            }));
            Add(await ThereAndBackAsync("key moved inside the same batch as the base column", m =>
            {
                var alt = m.Tables["Agg"].Columns["A"].AlternateOf;
                m.Tables["T"].Columns["B"].IsKey = true; alt.BaseTable = m.Tables["T"]; m.Tables["T"].Columns["A"].IsKey = true; alt.BaseColumn = m.Tables["T"].Columns["A"]; alt.Summarization = SummarizationType.GroupBy;
            }));
            foreach (var f in fails) _out.WriteLine("FAIL " + f);
            Assert.Empty(fails);
        }

        // ---- (3) ordinary edits add no seal step ---------------------------------------------------------------------------

        [Fact]
        public async Task Ordinary_edits_add_one_step_each_and_no_seal()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(Model());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            var edits = new (string Label, Func<Task> Do)[]
            {
                ("description", () => engine.SetDescriptionAsync("measure:T/M", "One.", "human")),
                ("format string", () => engine.SetObjectPropertiesAsync(new[] { "measure:T/M" }, "FormatString", "0.0", "human")),
                ("hide two columns", () => engine.SetObjectPropertiesAsync(new[] { "column:T/B", "column:T/C" }, "IsHidden", "true", "human")),
                ("rename a measure", () => engine.SetObjectPropertiesAsync(new[] { "measure:T/M" }, "Name", "M2", "human")),
                ("hide the key column", () => engine.SetObjectPropertiesAsync(new[] { "column:T/A" }, "IsHidden", "true", "human")),
                ("key set on the key column again", () => engine.SetObjectPropertiesAsync(new[] { "column:T/A" }, "IsKey", "true", "human")),
            };
            var bad = new List<string>();
            foreach (var (label, run) in edits)
            {
                settings.Clear();
                var s0 = await StepsAsync(sessions);
                await run();
                var d = await StepsAsync(sessions) - s0;
                _out.WriteLine(label + ": " + d + " step(s), sealed: " + string.Join(", ", settings.Sealed));
                if (d > 1) bad.Add(label + ": " + d + " steps");
                if (settings.Sealed.Count > 0) bad.Add(label + ": sealed " + string.Join(", ", settings.Sealed));
            }
            Assert.Empty(bad);
        }
    }
}
