// Verifier's failing tests for lane m11/plan-rows, round 4, at a4ce5b9d (29 Sep): what is left of V8 and L9 once the
// default reads are held to 40,000 characters of the wire JSON (lane-reports/v-plan-rows-r4.md). Low items for the
// integrator, not the verdict: none of them writes anything to a model. Left failing on purpose; the fix makes them pass.
//
//   L10  a words page is cut to fit by the formula only. An object whose own description is longer than the page (the
//        DESC-LONG finding exists for exactly this object) still gets a page of its own at the description's length:
//        48,905 characters for a 48,000-character description, past the 40,000 the tool promises, and past the 50,000
//        Claude Code lets through uncounted once the description is a little longer.
//   L11  the short read keeps every row whole except its detail lists, and on a plan with many rows the rows alone pass
//        the limit. Each row's waitsFor names every row it waits for with its title; one starter-measure row per table
//        waits for every starter row before it, so the rows grow with the square of the tables. A fresh plan of a model
//        whose 16 tables each hang off one hub table (10 columns and 2 measures each) reads 49,359 characters, 22
//        tables 66,796, 34 tables 112,807; after Run the plan, 42 tables read 138,340 and 160 tables 1,186,396. The last
//        resort then drops the history first, so from 12 tables item = history carries no entry at all and says "item =
//        history reads it"; and a row's first detail page is over 40,000 too. The words queue stays under 40,000.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Lean suite (cut B): a small model written as a .bim and opened in ONE engine call, for a default test that would
    /// otherwise build it with a dozen writes. Columns are "Name:Type[:summarizeBy][:key]"; measures are (name, expression) pairs. Used by the
    /// plan-row witness tests that keep a model of their own; the larger ones open a ModelTemplates copy.</summary>
    internal static class PlanRowsBim
    {
        internal static Dictionary<string, object> Table(string name, string[] columns, params (string Name, string Expression, string Format)[] measures) => new()
        {
            ["name"] = name,
            ["columns"] = columns.Select(c =>
            {
                var p = c.Split(':');
                var d = new Dictionary<string, object>
                {
                    ["name"] = p[0], ["dataType"] = char.ToLowerInvariant(p[1][0]) + p[1].Substring(1), ["sourceColumn"] = p[0],
                };
                foreach (var option in p.Skip(2))
                    if (option == "key") d["isKey"] = true;
                    else d["summarizeBy"] = option;
                return (object)d;
            }).ToList(),
            ["measures"] = measures.Select(m =>
            {
                var d = new Dictionary<string, object> { ["name"] = m.Name, ["expression"] = m.Expression };
                if (m.Format != null) d["formatString"] = m.Format;
                return (object)d;
            }).ToList(),
            ["partitions"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = name, ["mode"] = "import",
                    ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "let\n    Source = #table({}, {})\nin\n    Source" },
                },
            },
        };

        internal static (string Name, string Expression, string Format) M(string name, string expression, string format = null) => (name, expression, format);

        internal static Dictionary<string, object> Rel(string fromTable, string fromColumn, string toTable, string toColumn) => new()
        {
            ["name"] = fromTable + "_" + fromColumn + "_" + toTable, ["fromTable"] = fromTable, ["fromColumn"] = fromColumn, ["toTable"] = toTable, ["toColumn"] = toColumn,
        };

        /// <summary>Write the model next to the fixture's workspace and open it; the model gets the null oracle like the build helpers did.</summary>
        internal static async Task<DecisionFixture> OpenAsync(string modelName, string file, object[] tables, object[] relationships = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = modelName, ["compatibilityLevel"] = 1604,
                ["model"] = new Dictionary<string, object> { ["culture"] = "en-US", ["tables"] = tables, ["relationships"] = relationships ?? Array.Empty<object>() },
            });
            var path = System.IO.Path.Combine(f.Workspace, file);
            await System.IO.File.WriteAllTextAsync(path, json);
            await f.Engine.OpenAsync(path);
            f.UseNull();
            return f;
        }

        /// <summary>Write a model to <paramref name="f"/>'s workspace and open it there (a template build step: a .bim opens in a few
        /// milliseconds where hundreds of engine calls take seconds).</summary>
        internal static async Task LoadAsync(DecisionFixture f, string modelName, string file, object[] tables)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = modelName, ["compatibilityLevel"] = 1604,
                ["model"] = new Dictionary<string, object> { ["culture"] = "en-US", ["tables"] = tables, ["relationships"] = Array.Empty<object>() },
            });
            var path = System.IO.Path.Combine(f.Workspace, file);
            await System.IO.File.WriteAllTextAsync(path, json);
            await f.Engine.OpenAsync(path);
        }

        /// <summary>The model ChangeCauseTests.PlanModelAsync builds, opened from a file: two safe-fix rows on the plan (an unhidden key, a summing key).</summary>
        internal static Task<DecisionFixture> PlanModelAsync(string file) => OpenAsync("Contoso", file,
            new object[]
            {
                Table("Sales", new[] { "Amount:Double", "CustomerKey:Int64", "ProductKey:Int64:sum" }, M("Total Sales", "SUM ( Sales[Amount] )")),
                Table("Customer", new[] { "CustomerKey:Int64", "City:String" }),
                Table("Product", new[] { "ProductKey:Int64" }),
            },
            new object[] { Rel("Sales", "CustomerKey", "Customer", "CustomerKey"), Rel("Sales", "ProductKey", "Product", "ProductKey") });
    }

    public sealed class VerifyPlanRowsRound4Tests
    {
        private const int DefaultReadLimit = 40_000;

        // What the MCP door sends is the tool's return value serialized with the SDK's own options (escapes counted).
        private static int WireChars(object o) => JsonSerializer.Serialize(o, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length;

        [Fact]
        public async Task L10_every_default_words_page_stays_at_40000_when_an_object_carries_a_description_longer_than_the_page()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("LongDescription", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human");
            // A description pasted in from a specification: plain words, no character the door escapes.
            var text = string.Concat(Enumerable.Repeat("Revenue recognised in the period after credits and before tax. ", 800)).Substring(0, 48_000);
            await f.Engine.SetDescriptionAsync("measure:Sales/Revenue", text, "human");

            var sizes = new List<(int Offset, int Chars, string Items)>();
            int? offset = 0;
            while (offset != null && sizes.Count < 50)
            {
                var page = await McpTools.GetWordsQueue(f.Engine, 0, offset.Value);      // the default page
                sizes.Add((offset.Value, WireChars(page), string.Join(", ", page.Items.Select(i => i.RuleId + " " + i.ObjectRef))));
                offset = page.Next;
            }
            var over = sizes.Where(s => s.Chars > DefaultReadLimit).ToList();
            Assert.True(over.Count == 0, over.Count + " of " + sizes.Count + " default words pages are over 40,000 characters: "
                + string.Join("; ", over.Select(s => "offset " + s.Offset + " (" + s.Items + ") " + s.Chars))
                + ". The page cuts a long formula to fit; it does not cut a long description.");
        }

        // A hub table and N tables that each hang off it: 10 columns (keys, amounts and counts, as a SQL source gives them)
        // and 2 measures each, so every table also gets a starter-measure row. Written as a .bim and opened in one call.
        private static string HubModelBim(int tables)
        {
            var types = new[] { "int64", "string", "decimal", "dateTime", "int64" };
            var list = new List<object>();
            var rels = new List<object>();
            for (var t = 0; t < tables; t++)
            {
                var name = $"T_{t:000}";
                var cols = Enumerable.Range(0, 10).Select(c =>
                {
                    var cn = $"{name}_C{c:00}_{(c % 3 == 0 ? "KEY" : c % 3 == 1 ? "AMT" : "CNT")}";
                    return (object)new Dictionary<string, object> { ["name"] = cn, ["dataType"] = types[c % 5], ["sourceColumn"] = cn };
                }).ToList();
                if (t > 0)
                {
                    cols.Add(new Dictionary<string, object> { ["name"] = $"{name}_T_000_KEY", ["dataType"] = "int64", ["sourceColumn"] = $"{name}_T_000_KEY" });
                    rels.Add(new Dictionary<string, object>
                    {
                        ["name"] = "r" + t, ["fromTable"] = name, ["fromColumn"] = $"{name}_T_000_KEY", ["toTable"] = "T_000", ["toColumn"] = "T_000_C00_KEY",
                    });
                }
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["columns"] = cols,
                    ["measures"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = name + "_M0", ["expression"] = $"SUM ( '{name}'[{name}_C02_CNT] )" },
                        new Dictionary<string, object> { ["name"] = name + "_M1", ["expression"] = $"COUNTROWS ( '{name}' )" },
                    },
                    ["partitions"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name, ["mode"] = "import",
                            ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "let\n    Source = #table({}, {})\nin\n    Source" },
                        },
                    },
                });
            }
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = "Hub" + tables, ["compatibilityLevel"] = 1604,
                ["model"] = new Dictionary<string, object> { ["culture"] = "en-US", ["tables"] = list, ["relationships"] = rels },
            });
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task L11_a_plan_with_many_rows_still_reads_at_40000_with_its_history_readable_page_by_page()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var path = System.IO.Path.Combine(f.Workspace, "Hub16.bim");
            await System.IO.File.WriteAllTextAsync(path, HubModelBim(16));
            await f.Engine.OpenAsync(path);
            f.UseNull();

            var first = await McpTools.GetModelPlan(f.Engine);
            var reads = new List<(string What, int Chars)> { ("the first read (" + first.Items.Length + " rows)", WireChars(first)) };
            foreach (var d in first.Detail ?? Array.Empty<PlanRowDetail>())
                if (d.Lines > 0)
                    reads.Add(("the first detail page of " + d.ItemId, WireChars(await McpTools.GetModelPlan(f.Engine, d.ItemId, 0, 0))));
            var history = await McpTools.GetModelPlan(f.Engine, "history", 0, 0);
            reads.Add(("the first history page", WireChars(history)));
            var entries = (await f.Engine.GetModelPlanAsync()).History?.Length ?? 0;

            var problems = reads.Where(r => r.Chars > DefaultReadLimit).Select(r => r.What + " is " + r.Chars + " characters").ToList();
            if (entries > 0 && (history.History?.Length ?? 0) == 0)
                problems.Add("the plan has " + entries + " history entries and item = history carries none (" + history.DetailNote + ")");
            Assert.True(problems.Count == 0, "on a fresh plan of 16 tables: " + string.Join("; ", problems)
                + ". The rows' waitsFor alone is " + WireChars(first.Items.Select(i => i.WaitsFor).ToArray()) + " characters.");
        }
    }
}
