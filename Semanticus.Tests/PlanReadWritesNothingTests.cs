// A read of the Plan never writes the Plan's file (UAT preflight S00, F-291).
//
// plan-memory.json sits in the model's .semanticus folder and travels with the model into the person's git
// repository. UAT found that one read-only get_model_plan call rewrote the whole file (100 lines in, 11 out) while
// the plan said "Nothing on the plan changed.", and that the Studio did the same about 20 seconds after it showed
// the Plan. So looking at the Plan left the person's tree dirty.
//
// What these tests hold:
//   a read of an unchanged plan leaves the file byte for byte as it was, whichever door asks and whatever the
//     file's format, including a file some other tool wrote;
//   a real change still writes, and it keeps the file's own format, so the diff is the change and nothing else.

using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanReadWritesNothingTests
    {
        /// <summary>A saved model with gaps, so the plan has rows, and one asked task, so the file holds the
        /// fields a task leaves empty.</summary>
        private static async Task<DecisionFixture> ModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Agree the Margin + wording with finance" });
            return f;
        }

        private static string MemoryFile(DecisionFixture f) =>
            Directory.GetFiles(f.Workspace, PlanMemory.FileName, SearchOption.AllDirectories).Single();

        /// <summary>Everything the Studio and the agent read when the Plan is on screen.</summary>
        private static async Task ReadLikeThePageAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.GetPlanHistoryAsync();
            await f.Engine.GetPlanHistoryAsync(null, null, null, 1);
            await f.Engine.GetModelPlanAsync();
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Reading_an_unchanged_plan_leaves_the_file_byte_for_byte_as_it_was()
        {
            using var f = await ModelAsync("PlanReadOnly.bim");
            // A decision about a row that is not on the plan now. Its "how long has it been gone" count used to
            // tick on every read, which is a write on every read.
            Assert.True(f.Engine.SeedPlanMemoryForTest(state =>
                state.Residue["cleanup/no-longer-here"] = new PlanRowResidue { Note = "keep for the audit" }));
            // The first read draws the plan for the first time. That is a real change and it is written.
            await f.Engine.GetModelPlanAsync();

            var file = MemoryFile(f);
            var bytes = File.ReadAllBytes(file);
            var stamp = File.GetLastWriteTimeUtc(file);

            await ReadLikeThePageAsync(f);

            Assert.Equal(Convert.ToBase64String(bytes), Convert.ToBase64String(File.ReadAllBytes(file)));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Reading_a_file_in_another_tools_format_leaves_it_exactly_as_it_was()
        {
            using var f = await ModelAsync("PlanForeign.bim");
            await f.Engine.GetModelPlanAsync();
            var file = MemoryFile(f);
            // The shape a person's own tool leaves: four-space indent, no empty fields, and a literal "+" in the
            // times where the engine writes an escape. This is the UAT fixture's shape.
            File.WriteAllText(file, Foreign(File.ReadAllText(file)));
            var bytes = File.ReadAllBytes(file);

            await ReadLikeThePageAsync(f);

            Assert.Equal(Convert.ToBase64String(bytes), Convert.ToBase64String(File.ReadAllBytes(file)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_real_change_still_writes_and_keeps_the_files_format_so_the_diff_is_only_the_change()
        {
            using var f = await ModelAsync("PlanRealChange.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var file = MemoryFile(f);
            File.WriteAllText(file, Foreign(File.ReadAllText(file)));
            var before = File.ReadAllText(file);

            var target = plan.Items.First(i => i.Status == ModelPlanStatuses.Ready);
            var after = await f.Engine.SkipModelPlanItemAsync(target.Id, "not until the quarter closes", "human");

            // The change was written and is read back.
            Assert.Contains(after.Items, i => i.Id == target.Id && i.SkipReason == "not until the quarter closes");
            var text = File.ReadAllText(file);
            Assert.NotEqual(before, text);
            Assert.Contains("not until the quarter closes", text, StringComparison.Ordinal);

            // In the file's own format: four-space indent kept, no escapes brought in, no empty fields added.
            Assert.DoesNotContain("\\u002B", text, StringComparison.Ordinal);
            Assert.DoesNotContain(": null", text, StringComparison.Ordinal);
            Assert.Contains("\n    \"models\"", text, StringComparison.Ordinal);

            // And minimal: every line that was there is still there, bar two. One said the skips were empty; the
            // other closed the last History entry and now carries the comma before the skip's own entry.
            var kept = text.Split('\n').ToList();
            var lost = before.Split('\n').Where(l => !kept.Remove(l)).ToArray();
            Assert.True(lost.Length <= 2, "lines lost: " + string.Join(" | ", lost));
        }

        [Fact]
        public void A_write_that_changes_nothing_is_not_a_write()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-planmem-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, PlanMemory.FileName);
                Assert.True(PlanMemory.Mutate(file, "disk:abc", s => s.Skips.Add(new PlanSkip
                {
                    ItemId = "a/b", Reason = "later", When = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                })));
                File.WriteAllText(file, Foreign(File.ReadAllText(file)));
                var bytes = File.ReadAllBytes(file);
                var stamp = File.GetLastWriteTimeUtc(file);

                Assert.True(PlanMemory.Mutate(file, "disk:abc", _ => { }));

                Assert.Equal(Convert.ToBase64String(bytes), Convert.ToBase64String(File.ReadAllBytes(file)));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
                Assert.Equal("later", PlanMemory.Read(file, "disk:abc").Find("a/b").Reason);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>The same content in another tool's format: null fields dropped, "+" and other characters
        /// left unescaped, four-space indent, LF line ends and a final newline.</summary>
        private static string Foreign(string engineText)
        {
            var node = JsonNode.Parse(engineText);
            DropNulls(node);
            var text = node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).Replace("\r\n", "\n");
            var lines = text.Split('\n').Select(l =>
            {
                var pad = l.Length - l.TrimStart(' ').Length;
                return new string(' ', pad * 2) + l.Substring(pad);
            });
            return string.Join("\n", lines) + "\n";
        }

        private static void DropNulls(JsonNode node)
        {
            if (node is JsonObject o)
            {
                foreach (var key in o.Where(p => p.Value == null).Select(p => p.Key).ToList()) o.Remove(key);
                foreach (var p in o) DropNulls(p.Value);
            }
            else if (node is JsonArray a)
                foreach (var item in a) DropNulls(item);
        }
    }
}
