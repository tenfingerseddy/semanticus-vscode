using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4 (P1): a rename made in the SAME write batch as the formulas it should follow used to leave them on the old names. The wrapper
    /// postpones its formula dependency tree to the end of a batch, so a rename in that batch found nothing to rewrite. The start plan's build is one batch (structure,
    /// then the assistant's renames), and only a plan that had been decided (Pro) had its renames folded into the structure first, so a Free build kept every measure on
    /// the source names. The seam is <see cref="Session.Rename"/>; these tests pin it for every kind of formula the wrapper keeps a dependency for.
    /// </summary>
    public sealed class RenameInOneBatchTests
    {
        private static async Task<(LocalEngine Engine, SessionManager Sessions, Session S)> ModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("RenameBatch", 1604);
            return (engine, sessions, sessions.Require());
        }

        // Every kind of formula in one small model: a measure, a measure's format string expression, a KPI, a calculation item, a role's row filter and a calculated column.
        private static void Build(Model m)
        {
            var fact = m.AddTable("fact_sales");
            fact.AddDataColumn("AMT", "AMT", null, DataType.Decimal);
            fact.AddDataColumn("DT", "DT", null, DataType.DateTime);
            var total = fact.AddMeasure("Total AMT", "SUM ( 'fact_sales'[AMT] )");
            fact.AddMeasure("Share", "DIVIDE ( [Total AMT], CALCULATE ( [Total AMT], ALL ( 'fact_sales' ) ) )").FormatStringExpression = "IF ( [Total AMT] > 0, \"0.0%\", \"0%\" )";
            var kpi = total.AddKPI();
            kpi.TargetExpression = "[Total AMT] * 2";
            kpi.StatusExpression = "IF ( [Total AMT] > 0, 1, 0 )";
            kpi.TrendExpression = "[Total AMT] - 1";
            fact.AddCalculatedColumn("Doubled", "'fact_sales'[AMT] * 2");
            var group = m.AddCalculationGroup("Time calc");
            group.AddCalculationItem("Selected", "CALCULATE ( SELECTEDMEASURE (), 'fact_sales'[DT] > 0 )").FormatStringExpression = "FORMAT ( SUM ( 'fact_sales'[AMT] ), \"0\" )";
            m.AddRole("Readers").RowLevelSecurity[fact] = "'fact_sales'[AMT] > 0";
        }

        private static string[] Formulas(Model m)
        {
            var fact = m.Tables.Single(t => t.Name == "Facts" || t.Name == "fact_sales");
            var list = fact.Measures.Select(x => x.Name + "=" + x.Expression + "|" + x.FormatStringExpression).ToList();
            var kpi = fact.Measures.Select(x => x.KPI).First(k => k != null);
            list.Add("kpi=" + kpi.TargetExpression + "|" + kpi.StatusExpression + "|" + kpi.TrendExpression);
            list.Add("col=" + fact.Columns.OfType<CalculatedColumn>().Single().Expression);
            var item = m.CalculationGroups.Single().CalculationItems.Single();
            list.Add("item=" + item.Expression + "|" + item.FormatStringExpression);
            list.Add("rls=" + m.Roles.Single().RowLevelSecurity[fact]);
            return list.ToArray();
        }

        private static readonly string[] Old = { "fact_sales", "AMT", "Total AMT", "DT" };

        [Fact]
        public async Task A_rename_in_the_batch_that_made_the_formulas_rewrites_every_kind_of_formula()
        {
            var (engine, _, s) = await ModelAsync();
            using (engine)
            {
                await s.MutateAsync("human", "build and rename in one batch", m =>
                {
                    Build(m);
                    var fact = m.Tables["fact_sales"];
                    s.Rename(fact.Columns["AMT"], "Amount");
                    s.Rename(fact.Columns["DT"], "Day");
                    s.Rename(fact.Measures["Total AMT"], "Total Amount");
                    s.Rename(fact, "Facts");
                });
                var formulas = await s.ReadAsync(Formulas);
                var stale = formulas.Where(f => f.Contains("fact_sales") || f.Contains("[AMT]") || f.Contains("Total AMT") || f.Contains("[DT]")).ToArray();
                Assert.True(stale.Length == 0, "formulas still on the old names:\n" + string.Join("\n", stale));
                Assert.Contains(formulas, f => f.StartsWith("Total Amount=SUM ( 'Facts'[Amount] )"));
                Assert.Contains(formulas, f => f.StartsWith("kpi=[Total Amount] * 2|IF ( [Total Amount] > 0, 1, 0 )|[Total Amount] - 1"));
                Assert.Contains(formulas, f => f == "item=CALCULATE ( SELECTEDMEASURE (), 'Facts'[Day] > 0 )|FORMAT ( SUM ( 'Facts'[Amount] ), \"0\" )");
                Assert.Contains(formulas, f => f == "rls='Facts'[Amount] > 0");
            }
        }

        [Fact]
        public async Task The_same_renames_in_a_later_batch_give_the_same_formulas()
        {
            var (engine, _, s) = await ModelAsync();
            using (engine)
            {
                await s.MutateAsync("human", "build", Build);
                await s.MutateAsync("human", "rename", m =>
                {
                    var fact = m.Tables["fact_sales"];
                    s.Rename(fact.Columns["AMT"], "Amount");
                    s.Rename(fact.Columns["DT"], "Day");
                    s.Rename(fact.Measures["Total AMT"], "Total Amount");
                    s.Rename(fact, "Facts");
                });
                var later = await s.ReadAsync(Formulas);

                var (engine2, _, s2) = await ModelAsync();
                using (engine2)
                {
                    await s2.MutateAsync("human", "one batch", m =>
                    {
                        Build(m);
                        var fact = m.Tables["fact_sales"];
                        s2.Rename(fact.Columns["AMT"], "Amount");
                        s2.Rename(fact.Columns["DT"], "Day");
                        s2.Rename(fact.Measures["Total AMT"], "Total Amount");
                        s2.Rename(fact, "Facts");
                    });
                    Assert.Equal(later, await s2.ReadAsync(Formulas));
                }
            }
        }
    }
}
