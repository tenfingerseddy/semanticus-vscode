using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat7-engine, item 1 (Sol UAT-J7-S4a-004, H6 still there). A person enabled a Date refresh policy, removed it, undid the
    /// removal, undid the enable, saved and restarted the engine: History struck "set" but showed "remove incremental refresh policy" as
    /// active. The join 6 repair (UAT U3) guessed that an earlier session's undone row is held when the model "still holds" the words the
    /// row wrote. That guess is wrong whenever a LATER undone row wrote the same state back (a removal writes "no policy", and after both
    /// undos the model has no policy). Class: every History row's undone flag after a restart must match what the reopened model holds, for
    /// EVERY kind of entry, so this sweeps the kinds the engine records (property, object add, object remove, policy, role, relationship,
    /// perspective, calculation group, column) through the same four shapes, and checks each row against the reopened model, not against
    /// the ledger's own opinion of itself.
    /// </summary>
    public sealed class HistoryRestartSweepTests
    {
        private sealed class Kind
        {
            public string Name = "";
            /// <summary>Edits made, saved and left behind in an earlier session before the kind's own change.</summary>
            public Func<LocalEngine, Task>? Prepare;
            /// <summary>The kind's change. Returns the ref of what it made, when it made one.</summary>
            public Func<LocalEngine, Task<string?>> Apply = null!;
            /// <summary>The change that takes Apply back by a second edit (the UAT shape), or null when the kind has none.</summary>
            public Func<LocalEngine, string?, Task>? Inverse;
            /// <summary>True when the reopened model has the change Apply made.</summary>
            public Func<TabularEditor.TOMWrapper.Model, bool> Holds = null!;
        }

        private static IEnumerable<Kind> Kinds()
        {
            yield return new Kind
            {
                Name = "property",
                Apply = async e => { await e.SetDescriptionAsync("measure:Sales/M1", "first words", "human"); return null; },
                Inverse = async (e, _) => await e.SetDescriptionAsync("measure:Sales/M1", "", "human"),
                Holds = m => m.Tables["Sales"].Measures["M1"].Description == "first words",
            };
            yield return new Kind
            {
                Name = "object add",
                Apply = async e => await e.CreateMeasureAsync("table:Sales", "Added", "1", "human"),
                Inverse = async (e, r) => await e.DeleteObjectAsync(r!, "human"),
                Holds = m => m.Tables["Sales"].Measures.Any(x => x.Name == "Added"),
            };
            yield return new Kind
            {
                Name = "object remove",
                Apply = async e => { await e.DeleteObjectAsync("measure:Sales/M2", "human"); return null; },
                Holds = m => !m.Tables["Sales"].Measures.Any(x => x.Name == "M2"),
            };
            yield return new Kind
            {
                Name = "column add",
                Apply = async e => await e.CreateColumnAsync("table:Sales", "Extra", "String", "Extra", "human"),
                Inverse = async (e, r) => await e.DeleteObjectAsync(r!, "human"),
                Holds = m => m.Tables["Sales"].Columns.Any(x => x.Name == "Extra"),
            };
            yield return new Kind
            {
                Name = "refresh policy enable",
                Apply = async e => { await e.SetIncrementalRefreshPolicyAsync("table:Sales", "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: false, "human"); return null; },
                Inverse = async (e, _) => await e.RemoveIncrementalRefreshPolicyAsync("table:Sales", "human"),
                Holds = m => m.Tables["Sales"].EnableRefreshPolicy,
            };
            yield return new Kind
            {
                Name = "refresh policy remove",
                Prepare = async e => await e.SetIncrementalRefreshPolicyAsync("table:Sales", "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, autoWire: false, "human"),
                Apply = async e => { await e.RemoveIncrementalRefreshPolicyAsync("table:Sales", "human"); return null; },
                Holds = m => !m.Tables["Sales"].EnableRefreshPolicy,
            };
            yield return new Kind
            {
                Name = "role",
                Apply = async e => { await e.CreateRoleAsync("Readers", "read", "human"); return null; },
                Inverse = async (e, _) => await e.DeleteRoleAsync("Readers", "human"),
                Holds = m => m.Roles.Any(r => r.Name == "Readers"),
            };
            yield return new Kind
            {
                Name = "role permission",
                Prepare = async e => await e.CreateRoleAsync("Readers", "read", "human"),
                Apply = async e => { await e.SetRolePermissionAsync("Readers", "readRefresh", "human"); return null; },
                Inverse = async (e, _) => await e.SetRolePermissionAsync("Readers", "read", "human"),
                Holds = m => m.Roles["Readers"].ModelPermission == TabularEditor.TOMWrapper.ModelPermission.ReadRefresh,
            };
            yield return new Kind
            {
                Name = "relationship",
                Apply = async e => await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human"),
                Inverse = async (e, r) => await e.DeleteObjectAsync(r!, "human"),
                Holds = m => m.Relationships.Any(),
            };
            yield return new Kind
            {
                Name = "perspective",
                Apply = async e => await e.CreatePerspectiveAsync("Short", "human"),
                Inverse = async (e, r) => await e.DeleteObjectAsync(r!, "human"),
                Holds = m => m.Perspectives.Any(p => p.Name == "Short"),
            };
            yield return new Kind
            {
                Name = "calculation group",
                Apply = async e => await e.CreateCalculationGroupAsync("Time", "human"),
                Inverse = async (e, r) => await e.DeleteObjectAsync(r!, "human"),
                Holds = m => m.Tables.Any(t => t.Name == "Time"),
            };
        }

        public static IEnumerable<object[]> KindNames() => Kinds().Select(k => new object[] { k.Name });

        private static string WriteSeed(string dir)
        {
            var db = new TOM.Database("Sweep") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = Table.SelectRows(x, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in Source" } });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-amount" });
            sales.Columns.Add(new TOM.DataColumn { Name = "OrderDate", DataType = TOM.DataType.DateTime, SourceColumn = "OrderDate", LineageTag = "c-od" });
            sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey", LineageTag = "c-ck" });
            for (var i = 1; i <= 3; i++)
                sales.Measures.Add(new TOM.Measure { Name = "M" + i, Expression = "SUM(Sales[Amount]) * " + i, LineageTag = "m-" + i });
            db.Model.Tables.Add(sales);
            var customer = new TOM.Table { Name = "Customer", LineageTag = "tag-cust" };
            customer.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey", LineageTag = "cu-ck", IsKey = true });
            db.Model.Tables.Add(customer);
            // A refresh policy needs the two parameters its filter reads.
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeStart", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2020, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]", LineageTag = "e-rs" });
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeEnd", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2021, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]", LineageTag = "e-re" });
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private static async Task Restart(SessionManager sessions, LocalEngine engine)
        {
            await sessions.Ledger.FlushAsync();
            engine.Dispose();
        }

        /// <summary>One run of a kind: the setup, the shape, a restart, and what History says next to what the reopened file holds.</summary>
        private static async Task<(List<PlanHistoryRow> Rows, bool Holds)> RunAsync(Kind kind, Func<LocalEngine, Func<Task<string?>>, Func<string?, Task>?, Task> shape)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = WriteSeed(dir);
                var before = new HashSet<string>(StringComparer.Ordinal);
                var first = new SessionManager();
                var engine = new LocalEngine(first, TestEntitlements.Pro);
                if (kind.Prepare != null)
                {
                    await engine.OpenAsync(model);
                    await kind.Prepare(engine);
                    await engine.SaveAsync(null!, null!, true, "human");
                    await Restart(first, engine);
                    first = new SessionManager();
                    engine = new LocalEngine(first, TestEntitlements.Pro);
                    await engine.OpenAsync(model);
                    foreach (var r in (await engine.GetPlanHistoryAsync()).Rows) before.Add(r.Id);
                }
                else await engine.OpenAsync(model);
                await shape(engine, () => kind.Apply(engine), kind.Inverse == null ? null : r => kind.Inverse(engine, r));
                await Restart(first, engine);

                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                var rows = (await next.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit" && !before.Contains(r.Id)).OrderBy(r => r.When).ToList();
                var holds = await again.Current.ReadAsync(m => kind.Holds(m));
                return (rows, holds);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        public static IEnumerable<object[]> KindNamesSample_A_change_taken_back_then() { var all = KindNames().ToArray(); return new[] { all[0] }; }
        [Theory]
        [MemberData(nameof(KindNamesSample_A_change_taken_back_then))]
        public Task A_change_taken_back_then_saved_reads_undone_after_a_restart_sample(string name) => A_change_taken_back_then_saved_reads_undone_after_a_restart(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KindNames))]
        public async Task A_change_taken_back_then_saved_reads_undone_after_a_restart(string name)
        {
            var kind = Kinds().Single(k => k.Name == name);
            var (rows, holds) = await RunAsync(kind, async (e, apply, _) =>
            {
                await apply();
                await e.UndoAsync("human");
                await e.SaveAsync(null!, null!, true, "human");
            });
            var row = Assert.Single(rows);
            Assert.True(row.BeforeThisSession);
            Assert.False(holds, "the file written after the undo does not hold the change");
            Assert.True(row.Undone, name + ": the reopened model lacks the change, so its row must read undone");
        }

        public static IEnumerable<object[]> KindNamesSample_A_change_saved_then_take() { var all = KindNames().ToArray(); return new[] { all[2] }; }
        [Theory]
        [MemberData(nameof(KindNamesSample_A_change_saved_then_take))]
        public Task A_change_saved_then_taken_back_without_a_save_still_reads_active_after_a_restart_sample(string name) => A_change_saved_then_taken_back_without_a_save_still_reads_active_after_a_restart(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KindNames))]
        public async Task A_change_saved_then_taken_back_without_a_save_still_reads_active_after_a_restart(string name)
        {
            var kind = Kinds().Single(k => k.Name == name);
            var (rows, holds) = await RunAsync(kind, async (e, apply, _) =>
            {
                await apply();
                await e.SaveAsync(null!, null!, true, "human");
                await e.UndoAsync("human");
            });
            var row = Assert.Single(rows);
            Assert.True(holds, "the file was saved before the undo, so it holds the change");
            Assert.False(row.Undone, name + ": the reopened model has the change, so its row must not be struck");
        }

        public static IEnumerable<object[]> KindNamesSample_A_change_saved_taken_bac() { var all = KindNames().ToArray(); return new[] { all[4] }; }
        [Theory]
        [MemberData(nameof(KindNamesSample_A_change_saved_taken_bac))]
        public Task A_change_saved_taken_back_and_saved_again_reads_undone_after_a_restart_sample(string name) => A_change_saved_taken_back_and_saved_again_reads_undone_after_a_restart(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KindNames))]
        public async Task A_change_saved_taken_back_and_saved_again_reads_undone_after_a_restart(string name)
        {
            var kind = Kinds().Single(k => k.Name == name);
            var (rows, holds) = await RunAsync(kind, async (e, apply, _) =>
            {
                await apply();
                await e.SaveAsync(null!, null!, true, "human");
                await e.UndoAsync("human");
                await e.SaveAsync(null!, null!, true, "human");
            });
            var row = Assert.Single(rows);
            Assert.False(holds);
            Assert.True(row.Undone, name + ": the second save wrote the model without the change");
        }

        public static IEnumerable<object[]> KindNamesSample_A_change_and_its_inverse() { var all = KindNames().ToArray(); return new[] { all[8] }; }
        [Theory]
        [MemberData(nameof(KindNamesSample_A_change_and_its_inverse))]
        public Task A_change_and_its_inverse_both_taken_back_then_saved_read_undone_after_a_restart_sample(string name) => A_change_and_its_inverse_both_taken_back_then_saved_read_undone_after_a_restart(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KindNames))]
        public async Task A_change_and_its_inverse_both_taken_back_then_saved_read_undone_after_a_restart(string name)
        {
            // The UAT shape: the later row writes the state the model has once BOTH are undone, so "the model holds what this row wrote" is
            // true of a row the model does not have.
            var kind = Kinds().Single(k => k.Name == name);
            if (kind.Inverse == null) { Assert.Null(kind.Inverse); return; }
            var (rows, holds) = await RunAsync(kind, async (e, apply, inverse) =>
            {
                var r = await apply();
                await inverse!(r);
                await e.UndoAsync("human");
                await e.UndoAsync("human");
                await e.SaveAsync(null!, null!, true, "human");
            });
            Assert.Equal(2, rows.Count);
            Assert.False(holds, "both edits were taken back, so the file does not hold the change");
            Assert.All(rows, r => Assert.True(r.Undone, name + ": '" + r.Title + "' is not in the reopened model, so it must read undone"));
        }

        public static IEnumerable<object[]> KindNamesSample_Only_the_inverse_taken_b() { var all = KindNames().ToArray(); return new[] { all[10] }; }
        [Theory]
        [MemberData(nameof(KindNamesSample_Only_the_inverse_taken_b))]
        public Task Only_the_inverse_taken_back_then_saved_leaves_the_change_active_and_the_inverse_undone_sample(string name) => Only_the_inverse_taken_back_then_saved_leaves_the_change_active_and_the_inverse_undone(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KindNames))]
        public async Task Only_the_inverse_taken_back_then_saved_leaves_the_change_active_and_the_inverse_undone(string name)
        {
            var kind = Kinds().Single(k => k.Name == name);
            if (kind.Inverse == null) { Assert.Null(kind.Inverse); return; }
            var (rows, holds) = await RunAsync(kind, async (e, apply, inverse) =>
            {
                var r = await apply();
                await inverse!(r);
                await e.UndoAsync("human");
                await e.SaveAsync(null!, null!, true, "human");
            });
            Assert.Equal(2, rows.Count);
            Assert.True(holds, "only the inverse was taken back, so the file holds the change");
            Assert.False(rows[0].Undone, name + ": the change is in the reopened model");
            Assert.True(rows[1].Undone, name + ": the inverse is not in the reopened model");
        }
    }
}
