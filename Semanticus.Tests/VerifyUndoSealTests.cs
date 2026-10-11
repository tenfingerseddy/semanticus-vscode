using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Verifier attacks on m11/undo-seal (ModelSeal): edits the lane's sweep did not reach (references, renames that make
    /// FormulaFixup rewrite DAX elsewhere, relationships, data types, precedence, levels, perspectives after renames,
    /// translations, partitions, long batches, a work that throws part way, chains of edits that write outside TE2's undo),
    /// each held to undo / redo / undo byte-equal (JSON + TMDL), and their dry runs held to model + stacks + revision equal.
    /// </summary>
    public sealed class VerifyUndoSealTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;

        public VerifyUndoSealTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "vus-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private string RichModel(int compatibilityLevel = 1604)
        {
            var dir = Path.Combine(_root, "rich-" + Guid.NewGuid().ToString("N").Substring(0, 4));
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Rich") { CompatibilityLevel = compatibilityLevel, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "Src", Kind = TOM.ExpressionKind.M, Expression = "let Source = 1 in Source" });
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales", Description = "facts" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re", DisplayFolder = "Geo" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-pi" });
            sales.Columns.Add(new TOM.DataColumn { Name = "AltId", DataType = TOM.DataType.Int64, SourceColumn = "AltId", LineageTag = "c-ai" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am", FormatString = "0.00" });
            sales.Columns.Add(new TOM.CalculatedColumn { Name = "Double", DataType = TOM.DataType.Decimal, Expression = "Sales[Amount] * 2", LineageTag = "c-db" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total", FormatString = "0", DisplayFolder = "Totals" });
            sales.Measures.Add(new TOM.Measure { Name = "Ratio", Expression = "DIVIDE([Total], SUM(Sales[Amount]))", LineageTag = "m-ratio" });
            var margin = new TOM.Measure { Name = "Margin", Expression = "[Total] - 1", LineageTag = "m-margin" };
            margin.KPI = new TOM.KPI { TargetExpression = "[Total]", StatusExpression = "1", StatusGraphic = "Traffic Light" };
            sales.Measures.Add(margin);
            sales.Hierarchies.Add(new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo", Levels = { new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 0, LineageTag = "l-re" }, new TOM.Level { Name = "Code", Column = sales.Columns["Code"], Ordinal = 1, LineageTag = "l-co" } } });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Columns.Add(new TOM.DataColumn { Name = "Sort", DataType = TOM.DataType.Int64, SourceColumn = "Sort", LineageTag = "c-ps" });
            product.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r2", FromColumn = sales.Columns["AltId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One, IsActive = false });
            var cg = new TOM.Table { Name = "Scenario", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 0 } };
            cg.Columns.Add(new TOM.DataColumn { Name = "Scenario", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cgn" });
            cg.Partitions.Add(new TOM.Partition { Name = "Scenario", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Actual", Expression = "SELECTEDMEASURE()", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Plan", Expression = "SELECTEDMEASURE() * 2", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Budget", Expression = "SELECTEDMEASURE() * 3", Ordinal = -1 });
            db.Model.Tables.Add(cg);
            var cg2 = new TOM.Table { Name = "Units", LineageTag = "t-cg2", CalculationGroup = new TOM.CalculationGroup { Precedence = 1 } };
            cg2.Columns.Add(new TOM.DataColumn { Name = "Units", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cg2n" });
            cg2.Partitions.Add(new TOM.Partition { Name = "Units", Source = new TOM.CalculationGroupSource() });
            cg2.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Thousands", Expression = "SELECTEDMEASURE() / 1000", Ordinal = 0 });
            cg2.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Ones", Expression = "SELECTEDMEASURE()", Ordinal = 1 });
            db.Model.Tables.Add(cg2);
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            var tp = new TOM.TablePermission { Table = sales, FilterExpression = "Sales[Region] = \"A\"" };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = sales.Columns["Amount"], MetadataPermission = TOM.MetadataPermission.None });
            role.TablePermissions.Add(tp);
            db.Model.Roles.Add(role);
            var view = new TOM.Perspective { Name = "View" };
            var pt = new TOM.PerspectiveTable { Table = sales };
            pt.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Column = sales.Columns["Amount"] });
            pt.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Measure = sales.Measures["Total"] });
            pt.PerspectiveHierarchies.Add(new TOM.PerspectiveHierarchy { Hierarchy = sales.Hierarchies["Geo"] });
            view.PerspectiveTables.Add(pt);
            view.PerspectiveTables.Add(new TOM.PerspectiveTable { Table = product });
            db.Model.Perspectives.Add(view);
            var culture = new TOM.Culture { Name = "fr-FR" };
            culture.ObjectTranslations.SetTranslation(sales.Columns["Region"], TOM.TranslatedProperty.Caption, "Zone");
            culture.ObjectTranslations.SetTranslation(sales.Columns["Amount"], TOM.TranslatedProperty.Caption, "Montant");
            culture.ObjectTranslations.SetTranslation(sales.Measures["Total"], TOM.TranslatedProperty.Caption, "Somme");
            culture.ObjectTranslations.SetTranslation(sales, TOM.TranslatedProperty.Caption, "Ventes");
            db.Model.Cultures.Add(culture);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private static async Task<string> WholeModelAsync(SessionManager sessions, string scratch)
        {
            return await sessions.Current.ReadAsync(m =>
            {
                var db = m.Database.TOMDatabase;
                var json = TOM.JsonSerializer.SerializeDatabase(db, new TOM.SerializeOptions { IgnoreTimestamps = true });
                var dir = Path.Combine(scratch, "tmdl-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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

        // The Verified Edits trail is append-only by design (an audit record survives an undo), so it is not part of the bar.
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
            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n" + tmdl;
        }

        private static string FirstDifference(string a, string b)
        {
            var n = Math.Min(a.Length, b.Length);
            var at = 0;
            while (at < n && a[at] == b[at]) at++;
            string Cut(string x) => x.Substring(Math.Max(0, at - 90), Math.Min(220, x.Length - Math.Max(0, at - 90))).Replace("\n", " ");
            return "was [" + Cut(a) + "] now [" + Cut(b) + "]";
        }

        private static string ContentOnly(string whole)
        {
            var json = whole.Substring(0, whole.IndexOf("\n#### TMDL", StringComparison.Ordinal));
            static System.Text.Json.Nodes.JsonNode Sort(System.Text.Json.Nodes.JsonNode n)
            {
                if (n is System.Text.Json.Nodes.JsonObject o) { foreach (var kv in o.ToList()) if (kv.Value != null) o[kv.Key] = Sort(kv.Value.DeepClone()); return o; }
                if (n is System.Text.Json.Nodes.JsonArray a)
                {
                    var items = a.Select(x => x == null ? null : Sort(x.DeepClone())).ToList();
                    if (items.All(x => x is System.Text.Json.Nodes.JsonObject ob && (ob["name"] != null || ob["table"] != null || ob["column"] != null || ob["measure"] != null)))
                        items = items.OrderBy(x => (string)(x["name"] ?? x["table"] ?? x["column"] ?? x["measure"]), StringComparer.Ordinal).ToList();
                    return new System.Text.Json.Nodes.JsonArray(items.ToArray());
                }
                return n;
            }
            return Sort(System.Text.Json.Nodes.JsonNode.Parse(json)).ToJsonString();
        }

        private static async Task<int> StepsAsync(SessionManager s) => await s.Current.ReadAsync(_ => s.Current.UndoStateNow().Steps);

        /// <summary>Do the edit, then undo, redo, undo its steps, comparing the whole model at each end. Strict = byte-equal
        /// JSON + TMDL; otherwise content-equal (sibling order ignored: TE2's Undelete puts an object back at the list's end).</summary>
        private async Task<string> ExactAsync(string label, Func<LocalEngine, SessionManager, Task> edit, bool strict = true, string path = null)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path ?? RichModel());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            var before = await WholeModelAsync(sessions, _root);
            var s0 = await StepsAsync(sessions);
            await edit(engine, sessions);
            var after = await WholeModelAsync(sessions, _root);
            if (after == before) return label + ": the edit changed nothing (attack did not land)";
            var steps = await StepsAsync(sessions) - s0;
            _out.WriteLine(label + ": " + steps + " undo step(s); sealed: " + string.Join(", ", settings.Sealed.Distinct()));
            if (steps <= 0) return label + ": changed the model and left no undo step";
            string Cmp(string want, string got, string what)
            {
                if (want == got) return null;
                var c = ContentOnly(want) == ContentOnly(got);
                if (c && !strict) { _out.WriteLine(label + " " + what + ": content equal, sibling order differs: " + FirstDifference(want, got)); return null; }
                return label + " " + what + (c ? " (content equal, ORDER differs)" : "") + ": " + FirstDifference(want, got);
            }
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            var p = Cmp(before, await WholeModelAsync(sessions, _root), "undo");
            if (p != null) return p;
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            p = Cmp(after, await WholeModelAsync(sessions, _root), "redo");
            if (p != null) return p;
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            p = Cmp(before, await WholeModelAsync(sessions, _root), "undo again");
            if (p != null) return p;
            if (settings.Failures.Count > 0) return label + ": a seal step could not put a value back: " + string.Join("; ", settings.Failures);
            // The detector is a test aid; the bytes above are the bar. Its names are reported, not failed on.
            if (settings.Unrestorable.Count > 0) _out.WriteLine("DETECTOR " + label + ": " + settings.Unrestorable.Count + " named, e.g. " + settings.Unrestorable[0]);
            return null;
        }

        private static async Task<string> RelAsync(SessionManager s, string fromColumn) =>
            await s.Current.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>().First(r => r.FromColumn.Name == fromColumn).Name);

        private static async Task<string> RefAsync(SessionManager s, Func<Model, ITabularObject> pick) => await s.Current.ReadAsync(m => ObjectRefs.For(pick(m)));

        [Theory]
        [InlineData("sort by column")]
        [InlineData("rename a measure other measures and a KPI use")]
        public Task An_edit_the_sweep_did_not_reach_takes_back_and_redoes_exactly_sample(string op) => An_edit_the_sweep_did_not_reach_takes_back_and_redoes_exactly(op);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("sort by column")]
        [InlineData("relationship made active over an active one")]
        [InlineData("relationship made inactive")]
        [InlineData("relationship crossfilter both")]
        [InlineData("relationship cardinality")]
        [InlineData("data type of a column a measure sums")]
        [InlineData("data type of the key column")]
        [InlineData("rename a measure other measures and a KPI use")]
        [InlineData("rename a column used in DAX, a row filter, a level, a perspective and a translation")]
        [InlineData("rename the fact table")]
        [InlineData("rename a calculation group")]
        [InlineData("calculation group precedence")]
        [InlineData("calculation group precedence equal to another")]
        [InlineData("level ordinal")]
        [InlineData("rename a level")]
        [InlineData("rename a hierarchy in a perspective")]
        [InlineData("rename a perspective")]
        [InlineData("translation caption")]
        [InlineData("partition M")]
        [InlineData("partition mode")]
        [InlineData("calc item ordinal on an ordered group")]
        [InlineData("set_properties IsKey on two columns")]
        [InlineData("named expression")]
        [InlineData("mark date table")]
        [InlineData("compatibility level")]
        public async Task An_edit_the_sweep_did_not_reach_takes_back_and_redoes_exactly(string op)
        {
            var problem = await ExactAsync(op, async (e, s) =>
            {
                switch (op)
                {
                    case "sort by column": await e.SetObjectPropertyAsync("column:Product/Name", "SortByColumn", "Sort", "human"); break;
                    case "sort by column cleared":
                        await e.SetObjectPropertyAsync("column:Product/Name", "SortByColumn", "Sort", "human");
                        await e.SetObjectPropertyAsync("column:Product/Name", "SortByColumn", "", "human"); break;
                    case "relationship made active over an active one":
                        await e.SetRelationshipAsync(await RelAsync(s, "ProductId"), null, false, "human");
                        await e.SetRelationshipAsync(await RelAsync(s, "AltId"), null, true, "human"); break;
                    case "relationship made inactive": await e.SetRelationshipAsync(await RelAsync(s, "ProductId"), null, false, "human"); break;
                    case "relationship crossfilter both": await e.SetRelationshipAsync(await RelAsync(s, "ProductId"), "BothDirections", null, "human"); break;
                    case "relationship cardinality": await e.SetRelationshipCardinalityAsync(await RelAsync(s, "ProductId"), "One", "One", "human"); break;
                    case "data type of a column a measure sums": await e.SetColumnDataTypeAsync("column:Sales/Amount", "Int64", "human"); break;
                    case "data type of the key column": await e.SetColumnDataTypeAsync("column:Product/ProductId", "String", "human"); break;
                    case "rename a measure other measures and a KPI use": await e.RenameObjectAsync("measure:Sales/Total", "Grand Total", "human"); break;
                    case "rename a column used in DAX, a row filter, a level, a perspective and a translation": await e.RenameObjectAsync("column:Sales/Amount", "Amt", "human");
                        await e.RenameObjectAsync("column:Sales/Region", "Area", "human"); break;
                    case "rename the fact table": await e.RenameObjectAsync("table:Sales", "Fact Sales", "human"); break;
                    case "rename a calculation group": await e.RenameObjectAsync("table:Scenario", "Scenarios", "human"); break;
                    case "calculation group precedence": await e.SetCalcGroupPrecedenceAsync("table:Scenario", 5, "human"); break;
                    case "calculation group precedence equal to another": await e.SetCalcGroupPrecedenceAsync("table:Scenario", 1, "human"); break;
                    case "level ordinal": await e.SetObjectPropertyAsync(await RefAsync(s, m => m.Tables["Sales"].Hierarchies["Geo"].Levels["Code"]), "Ordinal", "0", "human"); break;
                    case "rename a level": await e.RenameObjectAsync(await RefAsync(s, m => m.Tables["Sales"].Hierarchies["Geo"].Levels["Code"]), "Code Level", "human"); break;
                    case "rename a hierarchy in a perspective": await e.RenameObjectAsync(await RefAsync(s, m => m.Tables["Sales"].Hierarchies["Geo"]), "Geography", "human"); break;
                    case "rename a perspective": await e.RenameObjectAsync("perspective:View", "Main View", "human"); break;
                    case "perspective member off then table rename":
                        await e.SetPerspectiveMemberAsync("perspective:View", "measure:Sales/Total", false, "human");
                        await e.RenameObjectAsync("table:Product", "Item", "human");
                        await e.SetPerspectiveMemberAsync("perspective:View", "table:Item", false, "human"); break;
                    case "translation caption":
                        await s.Current.MutateAsync("human", "translate", m => m.Tables["Sales"].Measures["Ratio"].TranslatedNames["fr-FR"] = "Ratio FR");
                        await s.Current.MutateAsync("human", "translate", m => m.Tables["Sales"].Columns["Region"].TranslatedNames["fr-FR"] = ""); break;
                    case "partition M": await e.SetPartitionMAsync(await RefAsync(s, m => m.Tables["Sales"].Partitions["P1"]), "let Source = 42 in Source", "human"); break;
                    case "partition mode": await e.SetObjectPropertyAsync(await RefAsync(s, m => m.Tables["Sales"].Partitions["P1"]), "Mode", "DirectQuery", "human"); break;
                    case "key column chain":
                        await e.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "human");
                        await e.SetObjectPropertyAsync("column:Product/Sort", "IsKey", "true", "human");
                        await e.SetObjectPropertyAsync("column:Product/Sort", "IsKey", "false", "human");
                        await e.SetObjectPropertyAsync("column:Product/ProductId", "IsKey", "true", "human"); break;
                    case "calculation item order chain":
                        await e.SetObjectPropertyAsync("calcitem:Scenario/Budget", "Ordinal", "0", "human");
                        await e.SetObjectPropertyAsync("calcitem:Scenario/Plan", "Ordinal", "0", "human");
                        await e.SetObjectPropertyAsync("calcitem:Scenario/Actual", "Ordinal", "2", "human"); break;
                    case "calc item ordinal on an ordered group": await e.SetObjectPropertyAsync("calcitem:Units/Ones", "Ordinal", "0", "human"); break;
                    case "set_properties IsKey on two columns": await e.SetObjectPropertiesAsync(new[] { "column:Product/Name", "column:Product/Sort" }, "IsKey", "true", "human"); break;
                    case "named expression": await e.UpdateNamedExpressionAsync("Src", "let Source = 9 in Source", "human"); break;
                    case "mark date table":
                        await e.CreateTableAsync("Calendar", "human");
                        await e.CreateColumnAsync("table:Calendar", "Day", "DateTime", null, "human");
                        await e.MarkDateTableAsync("table:Calendar", "Day", "human"); break;
                    case "compatibility level": await e.SetCompatibilityLevelAsync(1605, "human"); break;
                    default: throw new InvalidOperationException(op);
                }
            });
            Assert.True(problem == null, problem);
        }

        [Theory]
        [InlineData("key column chain")]
        public Task A_chain_of_edits_walks_back_and_forward_through_every_state_exactly_sample(string chain) => A_chain_of_edits_walks_back_and_forward_through_every_state_exactly(chain);

        /// <summary>A chain of edits that each write outside TE2's undo: every state on the way is met again exactly, one undo
        /// at a time back to the start, one redo at a time to the end, and back once more.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("key column chain")]
        [InlineData("calculation item order chain")]
        [InlineData("sort by column set then cleared")]
        [InlineData("perspective member off then table rename")]
        [InlineData("key then rename then delete the old key")]
        public async Task A_chain_of_edits_walks_back_and_forward_through_every_state_exactly(string chain)
        {
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.OpenAsync(RichModel());
            var edits = chain switch
            {
                "key column chain" => new Func<Task>[]
                {
                    () => e.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "human"),
                    () => e.SetObjectPropertyAsync("column:Product/Sort", "IsKey", "true", "human"),
                    () => e.SetObjectPropertyAsync("column:Product/Sort", "IsKey", "false", "human"),
                    () => e.SetObjectPropertyAsync("column:Product/ProductId", "IsKey", "true", "human"),
                },
                "calculation item order chain" => new Func<Task>[]
                {
                    () => e.SetObjectPropertyAsync("calcitem:Scenario/Budget", "Ordinal", "0", "human"),
                    () => e.SetObjectPropertyAsync("calcitem:Scenario/Plan", "Ordinal", "0", "human"),
                    () => e.SetObjectPropertyAsync("calcitem:Scenario/Actual", "Ordinal", "1", "human"),
                    () => e.DeleteObjectsAsync(new[] { "calcitem:Scenario/Plan" }, "human"),
                    () => e.CreateCalculationItemAsync("table:Scenario", "Forecast", "SELECTEDMEASURE() * 4", "human"),
                },
                "sort by column set then cleared" => new Func<Task>[]
                {
                    () => e.SetObjectPropertyAsync("column:Product/Name", "SortByColumn", "Sort", "human"),
                    () => e.SetObjectPropertyAsync("column:Product/Name", "SortByColumn", "", "human"),
                },
                "perspective member off then table rename" => new Func<Task>[]
                {
                    () => e.SetPerspectiveMemberAsync("perspective:View", "measure:Sales/Total", false, "human"),
                    () => e.RenameObjectAsync("table:Product", "Item", "human"),
                    () => e.SetPerspectiveMemberAsync("perspective:View", "table:Item", false, "human"),
                    () => e.RenameObjectAsync("perspective:View", "Main", "human"),
                },
                "key then rename then delete the old key" => new Func<Task>[]
                {
                    () => e.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "human"),
                    () => e.RenameObjectAsync("column:Product/ProductId", "Id", "human"),
                    () => e.DeleteObjectsAsync(new[] { "column:Product/Id" }, "human"),
                    () => e.SetObjectPropertyAsync("column:Product/Sort", "IsKey", "true", "human"),
                },
                _ => throw new InvalidOperationException(chain),
            };
            var states = new List<string> { await WholeModelAsync(sessions, _root) };
            var stepCounts = new List<int> { await StepsAsync(sessions) };
            foreach (var edit in edits) { await edit(); states.Add(await WholeModelAsync(sessions, _root)); stepCounts.Add(await StepsAsync(sessions)); }
            for (var i = 1; i < states.Count; i++) Assert.True(stepCounts[i] == stepCounts[i - 1] + 1, chain + ": edit " + i + " did not make exactly one undo step");
            void Check(int k, string got, string when)
            {
                if (states[k] == got) return;
                var c = ContentOnly(states[k]) == ContentOnly(got);
                _out.WriteLine(chain + " " + when + " state " + k + (c ? " content equal, order differs: " : " CONTENT differs: ") + FirstDifference(states[k], got));
                Assert.True(c, chain + " " + when + ": state " + k + " not met: " + FirstDifference(ContentOnly(states[k]), ContentOnly(got)));
            }
            for (var k = states.Count - 2; k >= 0; k--) { await e.UndoAsync("human"); Check(k, await WholeModelAsync(sessions, _root), "undo"); }
            for (var k = 1; k < states.Count; k++) { await e.RedoAsync("human"); Check(k, await WholeModelAsync(sessions, _root), "redo"); }
            for (var k = states.Count - 2; k >= 0; k--) { await e.UndoAsync("human"); Check(k, await WholeModelAsync(sessions, _root), "undo again"); }
        }

        // ---- a long batch, structure, a throw part way, the order TE2's Undelete leaves ------------------------------------

        [Fact]
        public async Task An_apply_tmdl_of_fifty_objects_takes_back_and_redoes_exactly()
        {
            var problem = await ExactAsync("apply_tmdl 50", async (e, s) =>
            {
                var script = await e.ScriptObjectsAsync(new[] { "table:Sales" }, "TMDL");
                var add = new StringBuilder();
                for (var i = 0; i < 25; i++) add.Append("\tmeasure 'Extra ").Append(i).Append("' = [Total] * ").Append(i).Append("\n\t\tformatString: 0.0\n\n");
                for (var i = 0; i < 25; i++) add.Append("\tcolumn 'Col ").Append(i).Append("'\n\t\tdataType: string\n\t\tsourceColumn: Col").Append(i).Append(i == 0 ? "\n\t\tisKey\n\n" : "\n\n");
                script = script.TrimEnd() + "\n\n" + add;
                _out.WriteLine(script.Substring(0, Math.Min(script.Length, 600)));
                var r = await e.ApplyTmdlScriptAsync(script, "human");
                _out.WriteLine("applied: " + string.Join(", ", r.Applied ?? Array.Empty<string>()) + " skipped: " + string.Join(", ", r.Skipped ?? Array.Empty<string>()));
                Assert.Equal(50, await s.Current.ReadAsync(m => m.Tables["Sales"].Measures.Count(x => x.Name.StartsWith("Extra")) + m.Tables["Sales"].Columns.Count(x => x.Name.StartsWith("Col "))));
            }, strict: false);
            Assert.True(problem == null, problem);
        }

        [Fact]
        public async Task A_work_that_throws_after_writing_outside_undo_leaves_the_model_and_stacks_as_they_were()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            await engine.SetDescriptionAsync("measure:Sales/Margin", "a", "human");
            await engine.SetDescriptionAsync("measure:Sales/Margin", "b", "human");
            await engine.UndoAsync("human");
            var before = await WholeModelAsync(sessions, _root);
            var undoBefore = System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision;
            await Assert.ThrowsAnyAsync<Exception>(() => sessions.Current.MutateAsync("human", "half", m =>
            {
                m.Tables["Product"].Columns["Name"].IsKey = true;            // clears ProductId's key outside undo
                ((CalculationGroupTable)m.Tables["Scenario"]).CalculationItems["Budget"].Ordinal = 0;
                m.Tables["Sales"].Measures["Total"].Name = "Renamed";         // FormulaFixup inside the same batch
                throw new InvalidOperationException("boom");
            }));
            var after = await WholeModelAsync(sessions, _root);
            Assert.True(before == after, "a work that threw left a trace: " + FirstDifference(before, after));
            var undoAfter = System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision;
            Assert.Equal(undoBefore, undoAfter);
            await engine.RedoAsync("human");   // the redo the person had must still be there and exact
            Assert.Equal("b", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description));
        }

        /// <summary>A value TE2's own undo already restored must not be set again: count TOM property writes during the undo.</summary>
        [Fact]
        public async Task Undo_of_a_value_TE2_restored_itself_is_not_written_twice()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            await engine.SetObjectPropertyAsync("column:Product/Name", "IsKey", "true", "human");
            var sealedOnEdit = settings.Sealed.ToList();
            _out.WriteLine("sealed on the edit: " + string.Join(", ", sealedOnEdit));
            Assert.Contains(sealedOnEdit, x => x.Contains("IsKey"));
            Assert.DoesNotContain(sealedOnEdit, x => x.StartsWith("put"));
            settings.Clear();
            await engine.UndoAsync("human");
            var puts = settings.Sealed.Where(x => x.StartsWith("put")).ToList();
            _out.WriteLine("writes the seal made on undo: " + string.Join(", ", puts));
            Assert.Single(puts);   // only the sibling's key: Name.IsKey was put back by TE2's own step
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Product"].Columns["ProductId"].IsKey && !m.Tables["Product"].Columns["Name"].IsKey));
            settings.Clear();
            await engine.SetDescriptionAsync("measure:Sales/Total", "plain", "human");
            Assert.Empty(settings.Sealed);   // an ordinary edit has no seal step
        }

        [Theory]
        [InlineData("rename_object", "{\"objRef\":\"measure:Sales/Total\",\"newName\":\"Grand\"}")]
        public Task A_dry_run_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were_sample(string op, string args) => A_dry_run_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were(op, args);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("set_relationship_active", "{\"relationshipName\":\"@ProductId\",\"isActive\":false}")]
        [InlineData("rename_object", "{\"objRef\":\"measure:Sales/Total\",\"newName\":\"Grand\"}")]
        [InlineData("rename_object", "{\"objRef\":\"table:Sales\",\"newName\":\"Fact\"}")]
        [InlineData("rename_object", "{\"objRef\":\"column:Sales/Amount\",\"newName\":\"Amt\"}")]
        [InlineData("set_column_data_type", "{\"columnRef\":\"column:Product/ProductId\",\"dataType\":\"String\"}")]
        [InlineData("set_sort_by_column", "{\"objRef\":\"column:Product/Name\",\"sortByColumnName\":\"Sort\"}")]
        [InlineData("set_calc_group_precedence", "{\"calcGroupRef\":\"table:Scenario\",\"precedence\":1}")]
        [InlineData("set_properties", "{\"objRefs\":[\"column:Product/Name\",\"column:Product/Sort\"],\"propertyName\":\"IsKey\",\"value\":\"true\"}")]
        [InlineData("set_property", "{\"objRef\":\"calcitem:Scenario/Actual\",\"propertyName\":\"Ordinal\",\"value\":\"2\"}")]
        [InlineData("delete_objects", "{\"objRefs\":[\"column:Product/ProductId\",\"calcitem:Scenario/Actual\",\"table:Units\"]}")]
        [InlineData("set_compatibility_level", "{\"level\":1605}")]
        public async Task A_dry_run_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were(string op, string args)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            await engine.SetDescriptionAsync("measure:Sales/Margin", "words", "human");
            await engine.SetDescriptionAsync("measure:Sales/Margin", "later words", "human");
            await engine.UndoAsync("human");
            var before = await WholeModelAsync(sessions, _root);
            var undoBefore = System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision;
            if (args.Contains("@ProductId")) args = args.Replace("@ProductId", await RelAsync(sessions, "ProductId"));
            var report = await engine.DryRunOpAsync(op, args);
            _out.WriteLine(op + ": would succeed " + report.WouldSucceed + " " + report.Error + "; mutations " + report.Mutations?.Length);
            Assert.True(report.WouldSucceed, report.Error);
            var after = await WholeModelAsync(sessions, _root);
            if (before != after) _out.WriteLine(op + ": bytes differ; content equal " + (ContentOnly(before) == ContentOnly(after)) + ": " + FirstDifference(before, after));
            Assert.True(ContentOnly(before) == ContentOnly(after), op + " rehearsal changed the model's content: " + FirstDifference(ContentOnly(before), ContentOnly(after)));
            if (!op.StartsWith("delete")) Assert.True(before == after, op + " rehearsal left the model changed: " + FirstDifference(before, after));
            Assert.Equal(undoBefore, System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision);
        }

        /// <summary>Item 5 of the brief: TE2's Undelete puts an object back at the END of its list. Show what moves.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("calcitem:Scenario/Actual")]
        [InlineData("column:Sales/Region")]
        [InlineData("measure:Sales/Total")]
        [InlineData("table:Sales")]
        [InlineData("relationship")]
        [InlineData("level")]
        public async Task NOTE_what_an_undone_delete_puts_back_in_a_different_place(string what)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var before = await WholeModelAsync(sessions, _root);
            var r = what == "relationship" ? "relationship:r1" : what;
            if (what == "relationship") r = await RefAsync(sessions, m => m.Relationships.First());
            if (what == "level") r = await RefAsync(sessions, m => m.Tables["Sales"].Hierarchies["Geo"].Levels["Region"]);
            var orderBefore = await sessions.Current.ReadAsync(m => string.Join("|", ((CalculationGroupTable)m.Tables["Scenario"]).CalculationItems.Select(c => c.Name + ":" + c.Ordinal))
                + " ; cols " + string.Join(",", m.Tables.FirstOrDefault(t => t.Name == "Sales")?.Columns.Select(c => c.Name) ?? Array.Empty<string>())
                + " ; levels " + string.Join(",", m.Tables["Sales"].Hierarchies["Geo"].Levels.Select(l => l.Name + ":" + l.Ordinal)));
            await engine.DeleteObjectsAsync(new[] { r }, "human");
            await engine.UndoAsync("human");
            var after = await WholeModelAsync(sessions, _root);
            var orderAfter = await sessions.Current.ReadAsync(m => string.Join("|", ((CalculationGroupTable)m.Tables["Scenario"]).CalculationItems.Select(c => c.Name + ":" + c.Ordinal))
                + " ; cols " + string.Join(",", m.Tables.FirstOrDefault(t => t.Name == "Sales")?.Columns.Select(c => c.Name) ?? Array.Empty<string>())
                + " ; levels " + string.Join(",", m.Tables["Sales"].Hierarchies["Geo"].Levels.Select(l => l.Name + ":" + l.Ordinal)));
            _out.WriteLine(what + ": before " + orderBefore);
            _out.WriteLine(what + ": after  " + orderAfter);
            _out.WriteLine(what + ": bytes equal " + (before == after) + ", content equal " + (ContentOnly(before) == ContentOnly(after)));
            if (before != after) _out.WriteLine(FirstDifference(before, after));
            Assert.True(ContentOnly(before) == ContentOnly(after), what + ": content changed: " + FirstDifference(ContentOnly(before), ContentOnly(after)));
        }

        [Theory]
        [InlineData("rich", "model", "DiscourageReportMeasures", "true")]
        public Task A_refused_set_through_the_real_tool_leaves_no_trace_sample(string fixture, string target, string property, string value) => A_refused_set_through_the_real_tool_leaves_no_trace(fixture, target, property, value);

        /// <summary>The sweep's "refused" setters, tried through the real set_property tool: say why each throws, and prove a refusal
        /// leaves the model byte-equal and the stacks unchanged.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("rich", "model", "DiscourageReportMeasures", "true")]
        [InlineData("rich", "model", "ValueFilterBehavior", "Independent")]
        [InlineData("rich", "model", "MetadataAccessPolicy", "1")]
        [InlineData("rich", "column:Sales/Region", "StringIndexingBehavior", "Hash")]
        [InlineData("rich", "table:Scenario", "MultipleOrEmptySelectionExpression", "1")]
        [InlineData("rich", "table:Sales", "DirectLakeIndexingBehavior", "Automatic")]
        [InlineData("rich", "partition", "Expression", "let x = 1 in x")]
        [InlineData("rich", "column:Sales/Double", "ExpressionContext", "x")]
        [InlineData("aw", "model", "DiscourageImplicitMeasures", "true")]
        [InlineData("aw", "model", "SourceQueryCulture", "fr-FR")]
        [InlineData("aw", "measure", "LineageTag", "zz")]
        [InlineData("aw", "measure", "FormatStringExpression", "\"0\"")]
        [InlineData("aw", "table", "ExcludeFromModelRefresh", "true")]
        [InlineData("aw", "table", "LineageTag", "zz")]
        public async Task A_refused_set_through_the_real_tool_leaves_no_trace(string fixture, string target, string property, string value)
        {
            var path = RichModel();
            if (fixture == "aw") { path = Path.Combine(_root, "aw-" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".bim"); File.Copy(TestModels.FindBim(), path); }
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            var objRef = target switch
            {
                "model" => await RefAsync(sessions, m => m),
                "partition" => await RefAsync(sessions, m => m.Tables["Sales"].Partitions["P1"]),
                "measure" => await RefAsync(sessions, m => m.AllMeasures.First()),
                "table" => await RefAsync(sessions, m => m.Tables.First()),
                _ => target,
            };
            var cl = await sessions.Current.ReadAsync(m => m.Database.CompatibilityLevel);
            var before = await WholeModelAsync(sessions, _root);
            var undoBefore = System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision;
            string outcome;
            try { var r = await engine.SetObjectPropertyAsync(objRef, property, value, "human"); outcome = "applied (changed " + r.Changed + ")"; }
            catch (Exception ex) { outcome = "refused: " + (ex.InnerException?.Message ?? ex.Message); }
            _out.WriteLine($"{fixture} CL{cl} {objRef}.{property}={value}: {outcome}");
            if (outcome.StartsWith("applied")) return;   // the tool takes it: an ordinary edit, covered by the sweep's bar
            var after = await WholeModelAsync(sessions, _root);
            Assert.True(before == after, "a refused set left a trace: " + FirstDifference(before, after));
            Assert.Equal(undoBefore, System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision);
        }

        [Theory]
        [InlineData("model", "ValueFilterBehavior", 1702)]
        public Task A_property_the_sweep_could_not_set_takes_back_exactly_at_a_level_that_allows_it_sample(string target, string property, int level) => A_property_the_sweep_could_not_set_takes_back_exactly_at_a_level_that_allows_it(target, property, level);

        /// <summary>The sweep's refused properties are compatibility-level gates: at a level that allows them, each takes back
        /// and redoes exactly through the real tool.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("model", "ValueFilterBehavior", 1702)]
        [InlineData("model", "SelectionExpressionBehavior", 1702)]
        [InlineData("model", "MetadataAccessPolicy", 1706)]
        [InlineData("model", "SourceQueryCulture", 1702)]
        [InlineData("model", "DisableAutoExists", 1702)]
        [InlineData("model", "MaxParallelismPerRefresh", 1702)]
        [InlineData("model", "DirectLakeBehavior", 1702)]
        [InlineData("column:Sales/Region", "StringIndexingBehavior", 1706)]
        [InlineData("column:Sales/Region", "EncodingHint", 1702)]
        [InlineData("column:Sales/Double", "StringIndexingBehavior", 1706)]
        [InlineData("table:Scenario", "MultipleOrEmptySelectionExpression", 1702)]
        [InlineData("table:Scenario", "NoSelectionExpression", 1702)]
        [InlineData("table:Sales", "ExcludeFromAutomaticAggregations", 1702)]
        [InlineData("table:Sales", "SystemManaged", 1702)]
        [InlineData("measure:Sales/Total", "FormatStringExpression", 1702)]
        [InlineData("measure:Sales/Total", "DetailRowsExpression", 1702)]
        [InlineData("measure:Sales/Total", "LineageTag", 1702)]
        [InlineData("hierarchy", "HideMembers", 1702)]
        public async Task A_property_the_sweep_could_not_set_takes_back_exactly_at_a_level_that_allows_it(string target, string property, int level)
        {
            string tried = null;
            var problem = await ExactAsync(target + "." + property, async (e, s) =>
            {
                var objRef = target switch
                {
                    "model" => await RefAsync(s, m => m),
                    "hierarchy" => await RefAsync(s, m => m.Tables["Sales"].Hierarchies["Geo"]),
                    _ => target,
                };
                var (type, cur) = await s.Current.ReadAsync(m =>
                {
                    var o = ObjectRefs.Resolve(m, objRef);
                    var pi = o.GetType().GetProperty(property);
                    return (pi.PropertyType, pi.GetValue(o));
                });
                var t = Nullable.GetUnderlyingType(type) ?? type;
                var candidates = t.IsEnum ? Enum.GetNames(t).Where(n => n != Convert.ToString(cur)).ToList()
                    : t == typeof(bool) ? new List<string> { (!(cur is bool b && b)).ToString().ToLowerInvariant() }
                    : t == typeof(int) || t == typeof(long) ? new List<string> { "3" }
                    : property == "SourceQueryCulture" ? new List<string> { "fr-FR" }
                    : new List<string> { "1 + 1" };
                Exception last = null;
                foreach (var c in candidates)
                {
                    try { await e.SetObjectPropertyAsync(objRef, property, c, "human"); tried = c; return; }
                    catch (Exception ex) { last = ex; }
                }
                throw new InvalidOperationException("no value was taken: " + (last?.InnerException?.Message ?? last?.Message));
            }, strict: true, path: RichModel(level));
            _out.WriteLine(target + "." + property + " = " + tried + ": " + (problem ?? "exact"));
            Assert.True(problem == null, problem);
        }

        // ---- cost ------------------------------------------------------------------------------------------------------

        private string BigModel(int tables, int columnsPerTable, int measuresPerTable)
        {
            var dir = Path.Combine(_root, "big");
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Big") { CompatibilityLevel = 1604, Model = new TOM.Model() };
            for (var t = 0; t < tables; t++)
            {
                var table = new TOM.Table { Name = "T" + t, LineageTag = "t" + t };
                for (var c = 0; c < columnsPerTable; c++)
                    table.Columns.Add(new TOM.DataColumn { Name = "C" + c, DataType = TOM.DataType.Int64, SourceColumn = "C" + c, LineageTag = $"c{t}-{c}", Description = "d" });
                for (var m = 0; m < measuresPerTable; m++)
                    table.Measures.Add(new TOM.Measure { Name = $"M{t}-{m}", Expression = $"SUM('T{t}'[C{m % columnsPerTable}]) + [M{t}-{(m + 1) % measuresPerTable}]".Replace(" + [M" + t + "-0]", ""), LineageTag = $"m{t}-{m}" });
                table.Partitions.Add(new TOM.Partition { Name = "P" + t, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                db.Model.Tables.Add(table);
            }
            var culture = new TOM.Culture { Name = "fr-FR" };
            foreach (var t in db.Model.Tables.Take(50)) foreach (var c in t.Columns) culture.ObjectTranslations.SetTranslation(c, TOM.TranslatedProperty.Caption, c.Name + " fr");
            db.Model.Cultures.Add(culture);
            var path = Path.Combine(dir, "big.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task COST_the_seal_on_common_and_bulk_edits()
        {
            if (Environment.GetEnvironmentVariable("VUS_COST") != "1") return;   // a measurement, run on purpose (VUS_COST=1), never a gate
            var tables = int.TryParse(Environment.GetEnvironmentVariable("VUS_TABLES"), out var n) ? n : 400;
            var path = BigModel(tables, 40, 10);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            var objects = await sessions.Current.ReadAsync(m => ModelSeal.Coverage(m).Values.Sum(v => v.Objects));
            var lines = new List<string> { $"model: {tables} tables x 40 columns x 10 measures, {objects} objects read by the seal" };
            async Task<double> Time(bool on, Func<int, Task> edit, int rounds)
            {
                await sessions.Current.ReadAsync(m => { ModelSeal.For(m).Enabled = on; return 0; });
                var t = new List<double>();
                for (var i = 0; i < rounds; i++) { var sw = Stopwatch.StartNew(); await edit(i); t.Add(sw.Elapsed.TotalMilliseconds); await engine.UndoAsync("human"); }
                t.Sort();
                return t[t.Count / 2];
            }
            var refs100 = await sessions.Current.ReadAsync(m => m.AllMeasures.Take(100).Select(x => ObjectRefs.For(x)).ToArray());
            var edits = new (string Name, Func<int, Task> Edit, int Rounds)[]
            {
                ("one description", i => engine.SetDescriptionAsync("measure:T0/M0-0", "w" + i, "human"), 7),
                ("100 descriptions in one batch (set_properties)", i => engine.SetObjectPropertiesAsync(refs100, "Description", "bulk " + i, "human"), 5),
                ("rename a column 10 measures use (FormulaFixup)", i => engine.RenameObjectAsync("column:T1/C1", "C1x" + i, "human"), 5),
                ("set IsKey (writes siblings outside undo)", i => engine.SetObjectPropertyAsync("column:T2/C" + (i % 5 + 1), "IsKey", "true", "human"), 5),
                ("apply_tmdl of a 60-object table script", async i =>
                {
                    var sb = new StringBuilder("table 'New ").Append(i).Append("'\n\n");
                    for (var k = 0; k < 30; k++) sb.Append("\tmeasure 'N").Append(k).Append("' = ").Append(k).Append("\n\n");
                    for (var k = 0; k < 30; k++) sb.Append("\tcolumn 'K").Append(k).Append("'\n\t\tdataType: int64\n\t\tsourceColumn: K").Append(k).Append("\n\n");
                    sb.Append("\tpartition 'New ").Append(i).Append("' = m\n\t\tmode: import\n\t\tsource = let Source = 1 in Source\n\n");
                    await engine.ApplyTmdlScriptAsync(sb.ToString(), "human");
                }, 3),
            };
            foreach (var (name, edit, rounds) in edits)
            {
                await Time(true, edit, 1);
                var off = await Time(false, edit, rounds);
                var on = await Time(true, edit, rounds);
                lines.Add($"{name}: off {off:F0} ms, on {on:F0} ms, the seal adds {on - off:F0} ms (median of {rounds})");
            }
            Assert.True(lines.Count >= edits.Length, "every edit was timed: " + lines.Count + " lines for " + edits.Length + " edits");
            foreach (var l in lines) _out.WriteLine(l);
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "seal-cost-verify.txt"), lines);
        }
    }
}
