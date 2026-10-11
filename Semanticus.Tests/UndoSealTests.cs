using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// m11/undo-seal: every edit takes back EXACTLY, whatever TE2 writes straight into TOM outside its own undo (ModelSeal).
    /// The class sweep sets a new value on every settable scalar property of every object kind in a rich model, then undoes,
    /// redoes and undoes again, and compares the whole model (JSON and TMDL) at each end. The rehearsals (dry_run, the Plan's
    /// rehearse-the-safe-fixes) are rolled-back edits, so they are held to the same byte-equal bar.
    /// </summary>
    public sealed class UndoSealTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;

        public UndoSealTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "usl-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        // ---- fixtures ----------------------------------------------------------------------------------------------------

        /// <summary>A model with one of everything TE2 wraps that a person edits: a key column, a calculation group whose items
        /// carry no order, a hierarchy, partitions, a relationship, a calculated column, a KPI, a field-parameter-like table,
        /// a role with table, row-filter and column permissions, a perspective with members, a culture with translations,
        /// a named expression, a data source, annotations and extended properties.</summary>
        private string RichModel()
        {
            var dir = Path.Combine(_root, "rich");
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Rich") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            db.Model.Annotations.Add(new TOM.Annotation { Name = "owner", Value = "finance" });
            db.Model.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ext", Value = "{\"a\":1}" });
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "Src", Kind = TOM.ExpressionKind.M, Expression = "let Source = 1 in Source" });
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales", Description = "facts" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re", DisplayFolder = "Geo" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-pi" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am", FormatString = "0.00" });
            sales.Columns.Add(new TOM.CalculatedColumn { Name = "Double", DataType = TOM.DataType.Decimal, Expression = "Sales[Amount] * 2", LineageTag = "c-db" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total", FormatString = "0", DisplayFolder = "Totals" });
            var margin = new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" };
            margin.KPI = new TOM.KPI { TargetExpression = "2", StatusExpression = "1", StatusGraphic = "Traffic Light" };
            sales.Measures.Add(margin);
            sales.Hierarchies.Add(new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo", Levels = { new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 0, LineageTag = "l-re" }, new TOM.Level { Name = "Code", Column = sales.Columns["Code"], Ordinal = 1, LineageTag = "l-co" } } });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            sales.Annotations.Add(new TOM.Annotation { Name = "note", Value = "x" });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Columns.Add(new TOM.DataColumn { Name = "Sort", DataType = TOM.DataType.Int64, SourceColumn = "Sort", LineageTag = "c-ps" });
            product.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            var cg = new TOM.Table { Name = "Scenario", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 0 } };
            cg.Columns.Add(new TOM.DataColumn { Name = "Scenario", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cgn" });
            cg.Partitions.Add(new TOM.Partition { Name = "Scenario", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Actual", Expression = "SELECTEDMEASURE()", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Plan", Expression = "SELECTEDMEASURE() * 2", Ordinal = -1 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Budget", Expression = "SELECTEDMEASURE() * 3", Ordinal = -1 });
            db.Model.Tables.Add(cg);
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            var tp = new TOM.TablePermission { Table = sales, FilterExpression = "[Region] = \"A\"" };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = sales.Columns["Amount"], MetadataPermission = TOM.MetadataPermission.None });
            role.TablePermissions.Add(tp);
            role.Members.Add(new TOM.WindowsModelRoleMember { MemberName = "CORP\\boss" });
            db.Model.Roles.Add(role);
            var view = new TOM.Perspective { Name = "View" };
            var pt = new TOM.PerspectiveTable { Table = sales };
            pt.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Column = sales.Columns["Amount"] });
            pt.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Measure = sales.Measures["Total"] });
            view.PerspectiveTables.Add(pt);
            db.Model.Perspectives.Add(view);
            var culture = new TOM.Culture { Name = "fr-FR" };
            culture.ObjectTranslations.SetTranslation(sales.Columns["Region"], TOM.TranslatedProperty.Caption, "Zone");
            culture.ObjectTranslations.SetTranslation(sales.Measures["Total"], TOM.TranslatedProperty.Caption, "Somme");
            db.Model.Cultures.Add(culture);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private string AdventureWorks()
        {
            var dir = Path.Combine(_root, "aw");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), path);
            return path;
        }

        // ---- the byte-equal bar ------------------------------------------------------------------------------------------

        /// <summary>The whole model as TOM writes it: JSON, and TMDL (every file, in name order).</summary>
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
                    return json + "\n#### TMDL\n" + tmdl;
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });
        }

        private static string FirstDifference(string a, string b)
        {
            var n = Math.Min(a.Length, b.Length);
            var at = 0;
            while (at < n && a[at] == b[at]) at++;
            string Cut(string x) => x.Substring(Math.Max(0, at - 70), Math.Min(160, x.Length - Math.Max(0, at - 70))).Replace("\n", " ");
            return "was [" + Cut(a) + "] now [" + Cut(b) + "]";
        }

        // ---- the class sweep ---------------------------------------------------------------------------------------------

        private sealed class SweepResult
        {
            public readonly HashSet<string> Kinds = new();
            public readonly List<string> Failures = new();
            public int Properties, Changed, Refused, Unchanged;
            public readonly List<string> Attempted = new();
            public readonly HashSet<string> PropertyNames = new();
            public readonly HashSet<string> Sealed = new();
            public readonly List<string> RefusedLabels = new();
        }

        private static bool IsSweepable(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t == typeof(bool) || t == typeof(string) || t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t.IsEnum;
        }

        private static IEnumerable<object> Candidates(Type t, object cur, string propName)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(bool)) { yield return !(cur is bool b && b); yield break; }
            if (t.IsEnum)
            {
                foreach (var v in Enum.GetValues(t)) if (!Equals(v, cur)) yield return v;
                yield break;
            }
            if (t == typeof(string))
            {
                var c = cur as string ?? "";
                yield return c + "x";
                yield return "Zz";
                yield break;
            }
            if (t == typeof(int)) { var c = cur is int i ? i : 0; yield return c + 1; yield return 0; yield return 7; yield break; }
            if (t == typeof(long)) { var c = cur is long i ? i : 0; yield return c + 1; yield return 7L; yield break; }
            if (t == typeof(double)) { var c = cur is double i ? i : 0; yield return c + 1; yield break; }
            if (t == typeof(decimal)) { var c = cur is decimal i ? i : 0; yield return c + 1; yield break; }
        }

        /// <summary>For each object kind and each settable scalar property it exposes: set a new value (one edit), undo, redo, undo,
        /// and compare the whole model at each end. A set the wrapper refuses must leave the model as it was too.</summary>
        private async Task<SweepResult> SweepAsync(string path, int instancesPerKind, bool seal = true, Func<string, bool> only = null, bool thereAndBack = false)
        {
            var result = new SweepResult();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            try
            {
                async Task OpenAsync()
                {
                    await engine.OpenAsync(path);
                    await sessions.Current.ReadAsync(m => { var s = ModelSeal.For(m); s.Enabled = seal; s.Detect = true; s.Clear(); return 0; });
                }
                await OpenAsync();
                var targets = await sessions.Current.ReadAsync(m =>
                {
                    var list = new List<(TabularObject Obj, PropertyInfo Prop, string Kind)>();
                    foreach (var group in ModelSeal.WrapperObjects(m).GroupBy(o => o.GetType()))
                    {
                        foreach (var obj in group.Take(instancesPerKind))
                            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (!p.CanWrite || !p.SetMethod.IsPublic || p.GetIndexParameters().Length != 0 || !IsSweepable(p.PropertyType)) continue;
                                if (p.GetCustomAttribute<System.ComponentModel.BrowsableAttribute>()?.Browsable == false) continue;
                                list.Add((obj, p, group.Key.Name));
                            }
                    }
                    return list;
                });
                foreach (var (obj, prop, kind) in targets)
                {
                    var label = kind + "." + prop.Name;
                    if (only != null && !only(label)) continue;
                    if (Environment.GetEnvironmentVariable("SEAL_ONLY") is { Length: > 0 } narrow && !narrow.Split(',').Any(label.Contains)) continue;   // a debugging aid
                    result.Kinds.Add(kind);
                    result.Properties++;
                    result.Attempted.Add(label);
                    result.PropertyNames.Add(prop.Name);
                    var before = await WholeModelAsync(sessions, _root);
                    var stepsBefore = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
                    var cur = await sessions.Current.ReadAsync(_ => prop.GetValue(obj));
                    string afterSet = null;
                    var applied = false;
                    string refusedTrace = null;
                    string lastError = null;
                    foreach (var candidate in Candidates(prop.PropertyType, cur, prop.Name))
                    {
                        // The engine's own door first (it has writes of its own, a calculation item's order), then the bare wrapper
                        // setter for what the engine will not take: both are edits a person or an assistant can make.
                        // "There and back": one batch sets the new value and then sets the old one again, so the batch ends where it began
                        // and only the steps TE2 recorded on the way are left to take it back.
                        if (thereAndBack)
                        {
                            try { await sessions.Current.MutateAsync("human", "sweep " + label, m => { prop.SetValue(obj, candidate); prop.SetValue(obj, cur); }); }
                            catch (Exception ex2)
                            {
                                lastError = ex2.Message.Length > 70 ? ex2.Message.Substring(0, 70) : ex2.Message;
                                var back2 = await WholeModelAsync(sessions, _root);
                                if (back2 != before) { refusedTrace = "refused set left a trace: " + FirstDifference(before, back2); break; }
                                continue;
                            }
                            applied = true;
                            break;
                        }
                        var viaEngine = await sessions.Current.ReadAsync(m => { try { var r = ObjectRefs.For(obj); return r != null && ReferenceEquals(ObjectRefs.Resolve(m, r), obj) ? r : null; } catch { return null; } });
                        if (viaEngine != null)
                        {
                            try
                            {
                                var text = candidate is Enum e ? e.ToString() : Convert.ToString(candidate, System.Globalization.CultureInfo.InvariantCulture);
                                await engine.SetObjectPropertyAsync(viaEngine, prop.Name, text, "human");
                                applied = true; break;
                            }
 catch (Exception ex0)
                            {
                                lastError = ex0.Message.Length > 70 ? ex0.Message.Substring(0, 70) : ex0.Message;
                                var back = await WholeModelAsync(sessions, _root);
                                if (back != before) { refusedTrace = "refused set (engine) left a trace: " + FirstDifference(before, back); break; }
                            }
                        }
                        try
                        {
                            await sessions.Current.MutateAsync("human", "sweep " + label, m => prop.SetValue(obj, candidate));
                        }
 catch (Exception ex1)
                        {
                            lastError = ex1.Message.Length > 70 ? ex1.Message.Substring(0, 70) : ex1.Message;
                            var back = await WholeModelAsync(sessions, _root);
                            if (back != before) { refusedTrace = "refused set left a trace: " + FirstDifference(before, back); break; }
                            continue;
                        }
                        applied = true;
                        break;
                    }
                    if (refusedTrace != null) { result.Failures.Add(label + ": " + refusedTrace); break; }
                    if (!applied) { result.Refused++; result.RefusedLabels.Add(label + " [" + lastError + "]"); continue; }
                    afterSet = await WholeModelAsync(sessions, _root);
                    var stepsAfter = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
                    if (afterSet == before && !(thereAndBack && stepsAfter > stepsBefore))
                    {
                        result.Unchanged++;
                        if (stepsAfter > stepsBefore) await engine.UndoAsync("human");
                        continue;
                    }
                    if (stepsAfter == stepsBefore) { result.Failures.Add(label + " changed the model and left no undo step"); break; }
                    result.Changed++;
                    string problem = null;
                    var historyText = Environment.GetEnvironmentVariable("SEAL_DEBUG") == null ? "" : await sessions.Current.ReadAsync(m => ModelSeal.History(m));
                    if (historyText.Length > 0) _out.WriteLine("HISTORY " + label + ":\n" + historyText);
                    await engine.UndoAsync("human");
                    var u1 = await WholeModelAsync(sessions, _root);
                    if (u1 != before) problem = "undo: " + FirstDifference(before, u1);
                    else
                    {
                        await engine.RedoAsync("human");
                        var r1 = await WholeModelAsync(sessions, _root);
                        if (r1 != afterSet) problem = "redo: " + FirstDifference(afterSet, r1);
                        else
                        {
                            await engine.UndoAsync("human");
                            var u2 = await WholeModelAsync(sessions, _root);
                            if (u2 != before) problem = "undo again: " + FirstDifference(before, u2);
                        }
                    }
                    if (problem != null)
                    {
                        result.Failures.Add(label + " " + problem);
                        // The model no longer matches the baseline, so every later comparison would blame this one: stop and report.
                        break;
                    }
                }
                var gate = await sessions.Current.ReadAsync(m => ModelSeal.For(m));
                result.Sealed.UnionWith(gate.Sealed);
                foreach (var f in gate.Failures) result.Failures.Add("a seal step could not put a value back: " + f);
                foreach (var u in gate.Unrestorable) result.Failures.Add("unrestorable structure: " + u);
                return result;
            }
            finally { engine.Dispose(); }
        }

        /// <summary>Run the sweep, continuing past a failing property: the model no longer matches its baseline after one, so the
        /// sweep reopens it (a fresh session; the targets are found again) and carries on past the property that failed.</summary>
        private async Task<SweepResult> FullSweepAsync(string path, int instancesPerKind, bool seal = true, bool thereAndBack = false)
        {
            var total = new SweepResult();
            var done = new HashSet<string>();
            for (var round = 0; round < 80; round++)
            {
                var r = await SweepAsync(path, instancesPerKind, seal, label => !done.Contains(label), thereAndBack);
                total.Kinds.UnionWith(r.Kinds); total.Sealed.UnionWith(r.Sealed); total.PropertyNames.UnionWith(r.PropertyNames);
                total.RefusedLabels.AddRange(r.RefusedLabels); total.Properties += r.Properties; total.Changed += r.Changed; total.Refused += r.Refused; total.Unchanged += r.Unchanged;
                total.Failures.AddRange(r.Failures);
                total.Attempted.AddRange(r.Attempted);
                if (r.Failures.Count == 0 || r.Attempted.Count == 0) return total;
                foreach (var l in r.Attempted) done.Add(l);
            }
            return total;
        }

        private string Report(string what, SweepResult r) =>
            $"{what}: {r.Kinds.Count} object kinds, {r.Attempted.Distinct().Count()} kind x property pairs, {r.Changed} changed the model and were undone/redone/undone byte-equal, {r.Unchanged} changed nothing, {r.Refused} refused by the wrapper";

        // ---- tests -------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_seal_reads_every_kind_of_object_the_rich_model_holds()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var coverage = await sessions.Current.ReadAsync(m => ModelSeal.Coverage(m));
            _out.WriteLine("TOM types read: " + coverage.Count + "; values per object: " + string.Join(", ", coverage.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value.Objects} ({k.Value.Values})")));
            foreach (var needed in new[] { "Model", "Table", "DataColumn", "CalculatedColumn", "Measure", "KPI", "Hierarchy", "Level", "MPartitionSource", "Partition", "SingleColumnRelationship",
                "CalculationGroup", "CalculationItem", "ModelRole", "TablePermission", "ColumnPermission", "Perspective", "PerspectiveTable", "PerspectiveColumn", "PerspectiveMeasure", "Culture",
                "ObjectTranslation", "NamedExpression", "WindowsModelRoleMember" })
                Assert.True(coverage.ContainsKey(needed), "the seal never reads a " + needed);
        }

        /// <summary>Lean suite: the default tier sweeps the first settable property of six object kinds, plus every IsKey and Ordinal on every instance
        /// (the values TE2 writes outside its own undo, which only the seal takes back). Skip the seal and these fail. The full
        /// sweep of every property of every kind, in the rich model and AdventureWorks, is the sweep tier.</summary>
        [Fact]
        public async Task Every_settable_property_of_a_sample_of_kinds_takes_back_exactly_and_redoes_exactly_X_sample()
        {
            var kinds = new HashSet<string> { "Model", "Table", "DataColumn", "Measure", "Level", "CalculationItem" };
            var seen = new HashSet<string>();
            bool Pick(string label)
            {
                var dot = label.IndexOf('.');
                var kind = label.Substring(0, dot);
                var prop = label.Substring(dot + 1);
                if (prop == "IsKey" || prop == "Ordinal") return true;
                return kinds.Contains(kind) && seen.Add(kind);
            }
            var r = await SweepAsync(RichModel(), instancesPerKind: 99, only: Pick);
            _out.WriteLine(Report("rich, sample", r));
            foreach (var f in r.Failures) _out.WriteLine("FAIL " + f);
            Assert.Empty(r.Failures);
            Assert.Contains(r.Attempted, l => l.EndsWith(".IsKey"));
            Assert.Contains(r.Attempted, l => l.EndsWith(".Ordinal"));
            Assert.True(r.Changed >= 6, "the sample reached too little: " + Report("rich, sample", r));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_settable_property_of_every_kind_in_a_rich_model_takes_back_exactly_and_redoes_exactly()
        {
            var r = await FullSweepAsync(RichModel(), instancesPerKind: 99);
            _out.WriteLine(Report("rich", r));
            _out.WriteLine("kinds: " + string.Join(", ", r.Kinds.OrderBy(k => k)));
            _out.WriteLine("refused: " + string.Join("; ", r.RefusedLabels));
            _out.WriteLine("values the seal had to keep (TE2 wrote them outside its undo): " + string.Join(", ", r.Sealed.OrderBy(k => k)));
            foreach (var f in r.Failures) _out.WriteLine("FAIL " + f);
            Assert.True(r.Kinds.Count >= 20, "the sweep reached only " + r.Kinds.Count + " kinds: " + string.Join(", ", r.Kinds));
            Assert.Empty(r.Failures);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_settable_property_of_every_kind_in_AdventureWorks_takes_back_exactly_and_redoes_exactly()
        {
            var r = await FullSweepAsync(AdventureWorks(), instancesPerKind: 1);
            _out.WriteLine(Report("AdventureWorks", r));
            _out.WriteLine("kinds: " + string.Join(", ", r.Kinds.OrderBy(k => k)));
            _out.WriteLine("refused: " + string.Join("; ", r.RefusedLabels));
            _out.WriteLine("values the seal had to keep (TE2 wrote them outside its undo): " + string.Join(", ", r.Sealed.OrderBy(k => k)));
            foreach (var f in r.Failures) _out.WriteLine("FAIL " + f);
            Assert.Empty(r.Failures);
        }

        /// <summary>The class sweep of the cascade defect: every settable property of every kind, set to a new value and set back in ONE
        /// batch, so the batch nets to nothing. TE2's recorded steps alone must not be trusted to take it back.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_settable_property_of_every_kind_set_and_set_back_in_one_batch_takes_back_exactly_and_redoes_exactly()
        {
            var r = await FullSweepAsync(RichModel(), instancesPerKind: 99, thereAndBack: true);
            _out.WriteLine(Report("rich, there and back", r));
            foreach (var f in r.Failures) _out.WriteLine("FAIL " + f);
            Assert.Empty(r.Failures);
            Assert.True(r.Changed > 100, "the sweep reached too little: " + Report("rich, there and back", r));
        }

        /// <summary>The red proof: with the seal switched off the same sweep must name the writes TE2 makes outside its undo.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_the_seal_off_the_sweep_names_IsKey_and_Ordinal()
        {
            var r = await FullSweepAsync(RichModel(), instancesPerKind: 99, seal: false);
            _out.WriteLine(Report("rich, seal off", r));
            _out.WriteLine(string.Join("\n", r.Failures));
            Assert.Contains(r.Failures, f => f.Contains("IsKey"));
            Assert.Contains(r.Failures, f => f.Contains("Ordinal"));
        }

        // ---- rehearsals: a rolled-back edit leaves nothing behind -------------------------------------------------------

        private async Task<(string Model, string Undo)> StateAsync(SessionManager sessions) =>
            (await WholeModelAsync(sessions, _root),
             System.Text.Json.JsonSerializer.Serialize(await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())) + " rev " + sessions.Current.Revision);

        [Theory]
        [InlineData("column:Product/Name", "IsKey", "true")]
        public Task A_dry_run_of_set_property_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were_sample(string objRef, string property, string value) => A_dry_run_of_set_property_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were(objRef, property, value);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("column:Product/Name", "IsKey", "true")]
        [InlineData("calcitem:Scenario/Plan", "Ordinal", "0")]
        [InlineData("column:Sales/Region", "IsKey", "true")]
        [InlineData("measure:Sales/Total", "FormatString", "0.0")]
        public async Task A_dry_run_of_set_property_leaves_the_model_the_stacks_and_the_revision_exactly_as_they_were(string objRef, string property, string value)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            await engine.SetDescriptionAsync("measure:Sales/Margin", "words", "human");   // a step behind and a redo ahead
            await engine.SetDescriptionAsync("measure:Sales/Margin", "later words", "human");
            await engine.UndoAsync("human");
            var before = await StateAsync(sessions);
            var report = await engine.DryRunOpAsync("set_property", System.Text.Json.JsonSerializer.Serialize(new { objRef, propertyName = property, value }));
            Assert.True(report.WouldSucceed, report.Error);
            Assert.NotEmpty(report.Mutations);   // it really rehearsed a write
            var after = await StateAsync(sessions);
            Assert.True(before.Model == after.Model, "the rehearsal left the model changed: " + FirstDifference(before.Model, after.Model));
            Assert.Equal(before.Undo, after.Undo);
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Product"].Columns["ProductId"].IsKey), "the old key is gone");
        }

        // ---- the cascade class: a setter that writes siblings outside TE2's undo, with a batch that nets to nothing -------------

        /// <summary>Sol, join 6: one batch sets B.IsKey then A.IsKey. The batch ends where it started, but TE2 recorded that A was
        /// false before its step (B's setter had already cleared it), so TE2's own undo leaves NEITHER column the key. The seal
        /// used to drop its step because nothing differed at the end.</summary>
        [Fact]
        public async Task A_dry_run_of_set_properties_that_moves_the_key_and_moves_it_back_leaves_the_original_key()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var before = await StateAsync(sessions);
            var report = await engine.DryRunOpAsync("set_properties", "{\"objRefs\":[\"column:Product/Name\",\"column:Product/ProductId\"],\"propertyName\":\"IsKey\",\"value\":\"true\"}");
            Assert.True(report.WouldSucceed, report.Error);
            var after = await StateAsync(sessions);
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Product"].Columns["ProductId"].IsKey && !m.Tables["Product"].Columns["Name"].IsKey), "the rehearsal erased the original key");
            Assert.True(before.Model == after.Model, "the rehearsal left the model changed: " + FirstDifference(before.Model, after.Model));
            Assert.Equal(before.Undo, after.Undo);
        }

        private static string WithoutAudit(string whole)
        {
            var cut = whole.IndexOf("\n#### TMDL", StringComparison.Ordinal);
            var node = System.Text.Json.Nodes.JsonNode.Parse(whole.Substring(0, cut));
            if (node?["model"]?["annotations"] is System.Text.Json.Nodes.JsonArray anns)
                foreach (var a in anns.ToList()) if (((string)a?["name"])?.StartsWith("Semanticus_VerifiedEdits", StringComparison.Ordinal) == true) anns.Remove(a);
            var tmdl = string.Join("\n", whole.Substring(cut).Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("annotation Semanticus_VerifiedEdits", StringComparison.Ordinal)));
            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n" + tmdl;
        }

        [Fact]
        public async Task The_persons_undo_of_a_batch_that_moves_the_key_and_moves_it_back_leaves_the_original_key()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var before = WithoutAudit(await WholeModelAsync(sessions, _root));
            var s0 = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
            await engine.SetObjectPropertiesAsync(new[] { "column:Product/Name", "column:Product/ProductId" }, "IsKey", "true", "human");
            // The Verified Edits trail is append-only by design, so it is not part of the bar.
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables["Product"].Columns["ProductId"].IsKey && !m.Tables["Product"].Columns["Name"].IsKey), "the batch should net to nothing");
            var steps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps) - s0;
            var withEdit = WithoutAudit(await WholeModelAsync(sessions, _root));
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            var now = WithoutAudit(await WholeModelAsync(sessions, _root));
            Assert.True(before == now, "undo erased the original key: " + FirstDifference(before, now));
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            now = WithoutAudit(await WholeModelAsync(sessions, _root));
            Assert.True(withEdit == now, "redo lost the key: " + FirstDifference(withEdit, now));
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            now = WithoutAudit(await WholeModelAsync(sessions, _root));
            Assert.True(before == now, "undo again erased the original key: " + FirstDifference(before, now));
        }

        /// <summary>The rich model plus a second calculation group whose items already carry an order (0, 1).</summary>
        private string WithOrderedGroup(string path)
        {
            var db = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(path));
            var cg2 = new TOM.Table { Name = "Units", LineageTag = "t-cg2", CalculationGroup = new TOM.CalculationGroup { Precedence = 1 } };
            cg2.Columns.Add(new TOM.DataColumn { Name = "Units", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cg2n" });
            cg2.Partitions.Add(new TOM.Partition { Name = "Units", Source = new TOM.CalculationGroupSource() });
            cg2.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Thousands", Expression = "SELECTEDMEASURE() / 1000", Ordinal = 0 });
            cg2.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Ones", Expression = "SELECTEDMEASURE()", Ordinal = 1 });
            db.Model.Tables.Add(cg2);
            var dir = Path.Combine(_root, "ordered");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "model.bim");
            File.WriteAllText(file, TOM.JsonSerializer.SerializeDatabase(db));
            return file;
        }

        /// <summary>The class: for each value a setter's cascade writes outside TE2's undo (the key column, a calculation item's
        /// ordinal), batches that set it on siblings in every order, many ending where they began, must take back, redo and take
        /// back byte-equal, and a rehearsal of a set_properties of them must leave model, stacks and revision as they were.</summary>
        private async Task<int> SweepCascadeAsync(string what, IReadOnlyList<(string Table, string Member, string Value)> steps, int maxLength, int sampleEvery, bool orderedGroup = false, int minBatches = 20, bool needNetNil = true)
        {
            var path = RichModel();
            if (orderedGroup) path = WithOrderedGroup(path);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            ModelSeal.Settings settings = null;
            // A failed batch leaves the model damaged, which would make every later batch fail too. Start over on a fresh session.
            async Task FreshAsync()
            {
                engine?.Dispose();
                sessions = new SessionManager();
                engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(path);
                settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            }
            await FreshAsync();
            var failures = new List<string>();
            int ran = 0, netNil = 0, noStep = 0, id = 0;
            IEnumerable<int[]> Sequences()
            {
                for (var len = 2; len <= maxLength; len++)
                {
                    var idx = new int[len];
                    while (true)
                    {
                        if (id++ % sampleEvery == 0) yield return (int[])idx.Clone();
                        var k = len - 1;
                        while (k >= 0 && ++idx[k] == steps.Count) { idx[k] = 0; k--; }
                        if (k < 0) break;
                    }
                }
            }
            try
            {
                foreach (var seq in Sequences())
                {
                    var label = what + " [" + string.Join(", ", seq.Select(i => steps[i].Table + "/" + steps[i].Member + "=" + steps[i].Value)) + "]";
                    var s0 = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
                    var before = await WholeModelAsync(sessions, _root);
                    await sessions.Current.MutateAsync("human", "cascade batch", m =>
                    {
                        foreach (var i in seq) Apply(m, steps[i]);
                    });
                    var after = await WholeModelAsync(sessions, _root);
                    if (after == before) netNil++;
                    ran++;
                    var n = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps) - s0;
                    if (n == 0) { if (after != before) failures.Add(label + ": changed the model and left no undo step"); noStep++; continue; }   // every set was already so
                    string problem = n != 1 ? label + ": the batch made " + n + " undo steps, not one" : null;
                    for (var i = 0; i < n; i++) await engine.UndoAsync("human");
                    var u = await WholeModelAsync(sessions, _root);
                    if (problem == null && u != before) problem = label + " undo: " + FirstDifference(before, u);
                    if (problem == null)
                    {
                        for (var i = 0; i < n; i++) await engine.RedoAsync("human");
                        var r = await WholeModelAsync(sessions, _root);
                        if (r != after) problem = label + " redo: " + FirstDifference(after, r);
                        else
                        {
                            for (var i = 0; i < n; i++) await engine.UndoAsync("human");
                            var u2 = await WholeModelAsync(sessions, _root);
                            if (u2 != before) problem = label + " undo again: " + FirstDifference(before, u2);
                        }
                    }
                    if (problem != null) { failures.Add(problem); await FreshAsync(); }
                }
                _out.WriteLine(what + ": " + ran + " batches, " + netNil + " ended where they began, " + noStep + " changed nothing, " + failures.Count + " failed");
                foreach (var f in failures.Take(12)) _out.WriteLine("FAIL " + f);
                Assert.True(ran >= minBatches && (netNil > 0 || !needNetNil), "the sweep did not reach the case: " + ran + " batches, " + netNil + " net nil");
                Assert.Empty(failures);
                Assert.Empty(settings.Failures);
                return ran;
            }
            finally { engine.Dispose(); }

            static void Apply(Model m, (string Table, string Member, string Value) st)
            {
                var t = m.Tables[st.Table];
                if (st.Member.StartsWith("calcitem:")) ((CalculationGroupTable)t).CalculationItems[st.Member.Substring(9)].Ordinal = int.Parse(st.Value);
                else if (st.Member.StartsWith("level:")) t.Hierarchies["Geo"].Levels[st.Member.Substring(6)].Ordinal = int.Parse(st.Value);
                else t.Columns[st.Member].IsKey = bool.Parse(st.Value);
            }
        }

        /// <summary>Lean suite: two key candidates, set and unset, in every batch of up to three, one batch in six (the full six-step,
        /// 252-batch sweep is the sweep tier). The batch that moves the key and moves it back is also its own two tests below.</summary>
        [Fact]
        public async Task Batches_that_set_IsKey_on_siblings_in_any_order_take_back_redo_and_take_back_exactly_X_sample()
        {
            var steps = new List<(string, string, string)>();
            foreach (var c in new[] { "ProductId", "Name" }) { steps.Add(("Product", c, "true")); steps.Add(("Product", c, "false")); }
            Assert.True(await SweepCascadeAsync("IsKey (sample)", steps, maxLength: 3, sampleEvery: 6, minBatches: 8, needNetNil: false) >= 8);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Batches_that_set_IsKey_on_siblings_in_any_order_take_back_redo_and_take_back_exactly()
        {
            var steps = new List<(string, string, string)>();
            foreach (var c in new[] { "ProductId", "Name", "Sort" }) { steps.Add(("Product", c, "true")); steps.Add(("Product", c, "false")); }
            Assert.True(await SweepCascadeAsync("IsKey", steps, maxLength: 3, sampleEvery: 1) > 20, "the sweep ran too few batches");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Batches_that_set_a_calculation_items_ordinal_in_any_order_take_back_redo_and_take_back_exactly()
        {
            var steps = new List<(string, string, string)>();
            foreach (var i in new[] { "Actual", "Plan", "Budget" }) foreach (var v in new[] { "-1", "0", "1", "2" }) steps.Add(("Scenario", "calcitem:" + i, v));
            Assert.True(await SweepCascadeAsync("Ordinal", steps, maxLength: 3, sampleEvery: 3) > 20, "the sweep ran too few batches");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Batches_that_set_the_ordinal_of_items_that_already_have_one_take_back_redo_and_take_back_exactly()
        {
            var steps = new List<(string, string, string)>();
            foreach (var i in new[] { "Thousands", "Ones" }) foreach (var v in new[] { "-1", "0", "1" }) steps.Add(("Units", "calcitem:" + i, v));
            Assert.True(await SweepCascadeAsync("Ordinal (ordered group)", steps, maxLength: 4, sampleEvery: 1, orderedGroup: true) > 20, "the sweep ran too few batches");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Batches_that_set_a_levels_ordinal_in_any_order_take_back_redo_and_take_back_exactly()
        {
            var steps = new List<(string, string, string)>();
            foreach (var l in new[] { "Region", "Code" }) foreach (var v in new[] { "0", "1" }) steps.Add(("Sales", "level:" + l, v));
            Assert.True(await SweepCascadeAsync("Level ordinal", steps, maxLength: 4, sampleEvery: 1) > 20, "the sweep ran too few batches");
        }

        /// <summary>The rehearsal form of the class: a dry run of set_properties on every ordering of the siblings leaves the model,
        /// both stacks and the revision as they were.</summary>
        [Fact]
        public async Task A_dry_run_of_set_properties_on_siblings_in_any_order_leaves_everything_as_it_was()
        {
            var path = RichModel();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            (string Model, string Undo) before = default;
            async Task FreshAsync()
            {
                engine?.Dispose();
                sessions = new SessionManager();
                engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(path);
                await engine.SetDescriptionAsync("measure:Sales/Margin", "words", "human");   // a step behind and a redo ahead
                await engine.SetDescriptionAsync("measure:Sales/Margin", "later words", "human");
                await engine.UndoAsync("human");
                before = await StateAsync(sessions);
            }
            await FreshAsync();
            var failures = new List<string>();
            var cols = new[] { "ProductId", "Name", "Sort" };
            var cases = new List<(string[] Refs, string Prop, string Value)>();
            for (var a = 0; a < 3; a++) for (var b = 0; b < 3; b++)
            {
                cases.Add((new[] { "column:Product/" + cols[a], "column:Product/" + cols[b] }, "IsKey", "true"));
                for (var c = 0; c < 3; c++) cases.Add((new[] { "column:Product/" + cols[a], "column:Product/" + cols[b], "column:Product/" + cols[c] }, "IsKey", "true"));
            }
            var items = new[] { "Actual", "Plan", "Budget" };
            foreach (var v in new[] { "-1", "0", "1", "2" })
                for (var a = 0; a < 3; a++) for (var b = 0; b < 3; b++)
                    cases.Add((new[] { "calcitem:Scenario/" + items[a], "calcitem:Scenario/" + items[b] }, "Ordinal", v));
            foreach (var (refs0, prop, value) in cases)
            {
                var refs = refs0.Distinct().ToArray();   // one object may appear once per batch
                var label = prop + "=" + value + " on " + string.Join(", ", refs.Select(x => x.Substring(x.IndexOf('/') + 1)));
                var report = await engine.DryRunOpAsync("set_properties", System.Text.Json.JsonSerializer.Serialize(new { objRefs = refs, propertyName = prop, value }));
                if (!report.WouldSucceed) { failures.Add(label + " refused: " + report.Error); continue; }
                var after = await StateAsync(sessions);
                if (before.Model != after.Model) { failures.Add(label + ": model " + FirstDifference(before.Model, after.Model)); await FreshAsync(); }
                else if (before.Undo != after.Undo) { failures.Add(label + ": stacks or revision changed"); await FreshAsync(); }
            }
            engine.Dispose();
            _out.WriteLine(cases.Count + " rehearsals, " + failures.Count + " failed");
            foreach (var f in failures.Take(12)) _out.WriteLine("FAIL " + f);
            Assert.Empty(failures);
        }

        /// <summary>A model with a safe fix to rehearse: an unhidden foreign key and a geographic column with no data category.</summary>
        private async Task<string> GappyModelPathAsync()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Contoso", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            var dir = Path.Combine(_root, "gappy");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "gappy.bim");
            await engine.SaveAsync(path, "BIM", true, "human");
            return path;
        }

        /// <summary>Lean suite: the model with safe fixes to rehearse. The rich model and AdventureWorks are the sweep tier.</summary>
        [Fact]
        public async Task The_Plans_rehearsal_of_the_safe_fixes_leaves_the_gappy_model_byte_equal_X_sample() => await PlanRehearsalAsync(new[] { await GappyModelPathAsync() });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_Plans_rehearsal_of_the_safe_fixes_leaves_the_model_byte_equal() => await PlanRehearsalAsync(new[] { await GappyModelPathAsync(), RichModel(), AdventureWorks() });

        private async Task PlanRehearsalAsync(string[] paths)
        {
            foreach (var path in paths)
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(path);
                var before = await StateAsync(sessions);
                var plan = await engine.GetModelPlanAsync();
                await engine.PlanProjectionForTest;   // the rehearsal runs behind the first look
                var again = await engine.GetModelPlanAsync();
                _out.WriteLine(Path.GetFileName(path) + ": worth " + again.Worth?.From + " -> " + again.Worth?.To);
                Assert.False(string.IsNullOrEmpty(again.Worth?.To), "the rehearsal never produced a grade, so it proves nothing");
                if (Path.GetFileName(path) == "gappy.bim") Assert.Contains(again.Items, i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));   // there were safe fixes to rehearse
                var after = await StateAsync(sessions);
                Assert.True(before.Model == after.Model, "the Plan's rehearsal left the model changed: " + FirstDifference(before.Model, after.Model));
                Assert.Equal(before.Undo, after.Undo);
            }
        }

        // ---- the generic detector ----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_detector_names_an_object_a_batch_adds_or_removes_outside_undo_and_stays_quiet_for_TE2s_own_adds_and_removes()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });

            // TE2's own structure: every one of these has an undo step of its own, so none may be named, and taking all of
            // them back and doing them again must be exact.
            var start = await WholeModelAsync(sessions, _root);
            var startSteps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
            await engine.CreateMeasureAsync("table:Sales", "Extra", "1", "human");
            await engine.CreateHierarchyAsync("table:Sales", "Geo2", new[] { "Code", "Region" }, "human");
            await engine.CreateCalculationItemAsync("table:Scenario", "Forecast", "SELECTEDMEASURE() * 4", "human");
            await engine.DeleteObjectsAsync(new[] { "calcitem:Scenario/Plan" }, "human");
            await engine.DeleteObjectsAsync(new[] { "column:Sales/Code" }, "human");
            await engine.RenameObjectAsync("table:Product", "Item", "human");
            await engine.CreateTableAsync("Extra Table", "human");
            await engine.DeleteObjectsAsync(new[] { "measure:Sales/Total" }, "human");
            Assert.Empty(settings.Unrestorable);
            Assert.Empty(settings.Failures);
            var end = await WholeModelAsync(sessions, _root);
            var steps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps) - startSteps;
            _out.WriteLine("structure edits: 8 operations, " + steps + " undo steps");
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            var undone = await WholeModelAsync(sessions, _root);
            Assert.True(ContentOnly(start) == ContentOnly(undone), "undoing the structure edits left a trace: " + FirstDifference(ContentOnly(start), ContentOnly(undone)));
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            var redone = await WholeModelAsync(sessions, _root);
            Assert.True(ContentOnly(end) == ContentOnly(redone), "redoing the structure edits was not exact: " + FirstDifference(ContentOnly(end), ContentOnly(redone)));
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            settings.Clear();

            // An object made straight in TOM, and one dropped straight from it, with no step: named.
            await sessions.Current.MutateAsync("human", "sneak", m =>
            {
                var db = m.Database.TOMDatabase;
                db.Model.Perspectives.Add(new TOM.Perspective { Name = "Sneaky" });
                db.Model.Roles.Remove("Managers");
            });
            Assert.Contains(settings.Unrestorable, u => u.Contains("added") && u.Contains("Perspective") && u.Contains("Sneaky"));
            Assert.Contains(settings.Unrestorable, u => u.Contains("removed") && u.Contains("Managers"));
        }

        /// <summary>The model's JSON with every list of named things put in name order. TE2 takes a deleted object back at the END of its
        /// list, so after an undo the content is the same and the order of siblings is not: that is a separate, known limit of TE2's
        /// own undo (reported to the join), and these checks are about content.</summary>
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

        [Theory]
        [InlineData("create measure")]
        [InlineData("delete column")]
        [InlineData("rename table")]
        public Task Each_structure_edit_alone_takes_back_exactly_and_redoes_exactly_sample(string op) => Each_structure_edit_alone_takes_back_exactly_and_redoes_exactly(op);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("create measure")]
        [InlineData("create hierarchy")]
        [InlineData("create calculation item")]
        [InlineData("delete calculation item")]
        [InlineData("delete column")]
        [InlineData("delete column in a level")]
        [InlineData("rename table")]
        [InlineData("create table")]
        [InlineData("delete measure in a perspective")]
        [InlineData("delete table")]
        [InlineData("delete perspective")]
        [InlineData("delete role")]
        [InlineData("delete relationship")]
        public async Task Each_structure_edit_alone_takes_back_exactly_and_redoes_exactly(string op)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(RichModel());
            var settings = await sessions.Current.ReadAsync(m => { var st = ModelSeal.For(m); st.Detect = true; st.Clear(); return st; });
            var start = await WholeModelAsync(sessions, _root);
            var startSteps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
            switch (op)
            {
                case "create measure": await engine.CreateMeasureAsync("table:Sales", "Extra", "1", "human"); break;
                case "create hierarchy": await engine.CreateHierarchyAsync("table:Sales", "Geo2", new[] { "Code", "Region" }, "human"); break;
                case "create calculation item": await engine.CreateCalculationItemAsync("table:Scenario", "Forecast", "SELECTEDMEASURE() * 4", "human"); break;
                case "delete calculation item": await engine.DeleteObjectsAsync(new[] { "calcitem:Scenario/Plan" }, "human"); break;
                case "delete column": await engine.DeleteObjectsAsync(new[] { "column:Product/Sort" }, "human"); break;
                case "delete column in a level": await engine.DeleteObjectsAsync(new[] { "column:Sales/Code" }, "human"); break;
                case "rename table": await engine.RenameObjectAsync("table:Product", "Item", "human"); break;
                case "create table": await engine.CreateTableAsync("Extra Table", "human"); break;
                case "delete measure in a perspective": await engine.DeleteObjectsAsync(new[] { "measure:Sales/Total" }, "human"); break;
                case "delete table": await engine.DeleteObjectsAsync(new[] { "table:Product" }, "human"); break;
                case "delete perspective": await engine.DeleteObjectsAsync(new[] { "perspective:View" }, "human"); break;
                case "delete role": await engine.DeleteObjectsAsync(new[] { "role:Managers" }, "human"); break;
                case "delete relationship": await engine.DeleteObjectsAsync(new[] { await sessions.Current.ReadAsync(m => ObjectRefs.For(m.Relationships.First())) }, "human"); break;
                default: throw new InvalidOperationException(op);
            }
            Assert.Empty(settings.Unrestorable);
            Assert.Empty(settings.Failures);
            var end = await WholeModelAsync(sessions, _root);
            Assert.NotEqual(start, end);
            var steps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps) - startSteps;
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            var undone = await WholeModelAsync(sessions, _root);
            Assert.True(ContentOnly(start) == ContentOnly(undone), op + ": undo left a trace: " + FirstDifference(ContentOnly(start), ContentOnly(undone)));
            for (var i = 0; i < steps; i++) await engine.RedoAsync("human");
            var redone = await WholeModelAsync(sessions, _root);
            Assert.True(ContentOnly(end) == ContentOnly(redone), op + ": redo was not exact: " + FirstDifference(ContentOnly(end), ContentOnly(redone)));
            for (var i = 0; i < steps; i++) await engine.UndoAsync("human");
            Assert.True(ContentOnly(start) == ContentOnly(await WholeModelAsync(sessions, _root)), op + ": the second undo was not exact");
        }

        // ---- what the seal costs -----------------------------------------------------------------------------------------

        private string BigModel(int tables, int columnsPerTable, int measuresPerTable)
        {
            var dir = Path.Combine(_root, "big");
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Big") { CompatibilityLevel = 1604, Model = new TOM.Model() };
            for (var t = 0; t < tables; t++)
            {
                var table = new TOM.Table { Name = "T" + t, LineageTag = "t" + t };
                for (var c = 0; c < columnsPerTable; c++)
                    table.Columns.Add(new TOM.DataColumn { Name = "C" + c, DataType = TOM.DataType.String, SourceColumn = "C" + c, LineageTag = $"c{t}-{c}", Description = "d" });
                for (var m = 0; m < measuresPerTable; m++)
                    table.Measures.Add(new TOM.Measure { Name = "M" + m, Expression = "1", LineageTag = $"m{t}-{m}" });
                table.Partitions.Add(new TOM.Partition { Name = "P" + t, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                db.Model.Tables.Add(table);
            }
            var path = Path.Combine(dir, "big.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        /// <summary>The seal reads every value of every object at the start of an edit and again at the end. On the biggest model
        /// the suite has (AdventureWorks), and on a synthetic one several times that size, time a one-property edit with the
        /// seal on and off, and report the difference. The numbers are written to the test output and to seal-cost.txt.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_seals_cost_on_the_largest_fixture_is_measured_and_stays_under_a_second()
        {
            var lines = new List<string>();
            foreach (var (name, path) in new[] { ("AdventureWorks", AdventureWorks()), ("synthetic 300 tables x 40 columns x 5 measures", BigModel(300, 40, 5)) })
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(path);
                var counts = await sessions.Current.ReadAsync(m => ModelSeal.Coverage(m));
                var objects = counts.Values.Sum(v => v.Objects);
                var target = await sessions.Current.ReadAsync(m => m.Tables.First().Measures.FirstOrDefault()?.Name != null ? "measure:" + m.Tables.First().Name + "/" + m.Tables.First().Measures.First().Name : "table:" + m.Tables.First().Name);
                async Task<double> TimeAsync(bool on, int rounds)
                {
                    await sessions.Current.ReadAsync(m => { ModelSeal.For(m).Enabled = on; return 0; });
                    var times = new List<double>();
                    for (var i = 0; i < rounds; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        await engine.SetDescriptionAsync(target, "words " + on + i, "human");
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    times.Sort();
                    return times[times.Count / 2];
                }
                await TimeAsync(true, 3);
                var off = await TimeAsync(false, 9);
                var onMs = await TimeAsync(true, 9);
                var line = $"{name}: {objects} objects read, edit with the seal off {off:F1} ms, on {onMs:F1} ms, the seal costs {onMs - off:F1} ms per edit (median of 9)";
                lines.Add(line);
                _out.WriteLine(line);
                Assert.True(onMs - off < 1000, line);
            }
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "seal-cost.txt"), lines);
        }
    }
}
