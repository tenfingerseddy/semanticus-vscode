using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's words for a start plan (lane sf-words, design-spec-first-start-v2.md section 6, L10). Each Fact below
    /// was red before the code that makes it pass was written.
    ///
    /// Two engines per test. The first holds the plan (an empty model saved in a scratch folder, the schema read offline). The
    /// second is the PREVIEW: an ordinary engine with the Contoso model open, which the plan's words are read over through the
    /// <see cref="LocalEngine.StartPlanPreviewReaderForTest"/> seam. The preview is also where the stored lines are applied at
    /// the end of the rename test, with the Plan's own apply, to prove a line written for a renamed object lands on it.
    /// </summary>
    [Collection("restore-root")]
    public sealed class StartPlanWordsTests : IDisposable
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private sealed class Reader : LocalEngine.IStartPreviewReader
        {
            public Func<Session> Of;
            public Task<T> ReadAsync<T>(Func<Model, T> read) => Of().ReadAsync(read);
        }

        private readonly string _root;
        private readonly string _home;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public StartPlanWordsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _home = Path.Combine(_root, "home", ".semanticus");
            Directory.CreateDirectory(_home);
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _home);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            try { Directory.Delete(_root, true); } catch { }
        }

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
            Tables = new[]
            {
                new FabricTable
                {
                    Schema = "dbo", Name = "Sales", KeyColumns = new[] { "OrderId" },
                    Columns = new[]
                    {
                        new FabricColumn { Name = "OrderId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                        new FabricColumn { Name = "Amount", DataType = "Decimal", SqlType = "decimal", Ordinal = 2 },
                    },
                },
            },
            ForeignKeys = Array.Empty<FabricForeignKey>(),
        };

        private sealed class Rig
        {
            public LocalEngine Engine;
            public DecisionFixture Preview;
            public Reader Reader;
            public StartPlanView View;
        }

        /// <summary>A plan on an engine of the given tier, and a Contoso preview behind it (Sales, Customer, Product, one measure).</summary>
        private async Task<Rig> RigAsync(bool pro = true, Func<DecisionFixture, Task> shape = null)
        {
            var preview = await ChangeCauseTests.PlanModelAsync("Preview" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
            _owned.Add(preview);
            if (shape != null) await shape(preview);
            var mgr = new SessionManager();
            var engine = new LocalEngine(mgr, new Fake(pro), _root);
            _owned.Add(engine);
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Schema());
            var folder = Path.Combine(_root, "dest" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = folder, Name = "Words Model",
            }, "human");
            var reader = new Reader { Of = () => preview.Sessions.Current };
            engine.StartPlanPreviewReaderForTest = reader;
            return new Rig { Engine = engine, Preview = preview, Reader = reader, View = view };
        }

        private static StartWord W(string kind, string objRef, string text) => new StartWord { Kind = kind, Ref = objRef, Text = text };

        private static async Task<List<StartWordItem>> AllItemsAsync(LocalEngine e, int max = 40)
        {
            var all = new List<StartWordItem>();
            for (var offset = 0; ;)
            {
                var page = await e.GetStartWordsAsync(max, offset);
                all.AddRange(page.Items);
                offset += page.Items.Count;
                if (page.Items.Count == 0 || offset >= page.Total) return all;
            }
        }

        // ---------------------------------------------------------------- written words become assistant lines

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Written_words_become_assistant_lines_and_move_the_digest_and_void_an_approval()
        {
            var rig = await RigAsync();
            var open = await AllItemsAsync(rig.Engine);
            Assert.Contains(open, i => i.Ref == "measure:Sales/Total Sales" && i.Kind == "description");

            // The person approved this exact plan; the assistant's words change it, so the approval must not survive.
            var before = rig.View.Digest;
            rig.Engine.StartPlanDocForTestMutate(d => d.Approval = new StartApproval { Digest = before, Utc = DateTime.UtcNow });

            var result = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("description", "measure:Sales/Total Sales", "What customers paid for orders, before tax."),
                W("description", "table:Sales", "One row for each order line."),
            }, "agent");

            Assert.Equal(2, result.Written);
            Assert.Empty(result.Refused);
            var doc = rig.Engine.StartPlanDocForTest();
            var lines = doc.Lines.Where(l => l.DecidedBy == StartDecidedBy.Assistant).ToList();
            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => { Assert.True(l.Kept); Assert.Equal("set_description", l.Item.Kind); Assert.Equal("description", l.Item.Target); Assert.Equal(l.Word, l.Item.After); });
            Assert.Contains(lines, l => l.Item.ObjectRef == "measure:Sales/Total Sales" && l.Word == "What customers paid for orders, before tax.");

            // Both doors render the Words section with the assistant's two lines, and the digest moved so the approval is void.
            var words = result.View.Sections.Single(s => s.Id == StartSectionId.Words);
            Assert.Equal(2, words.Counts.Assistant);
            Assert.NotEqual(before, result.View.Digest);
            Assert.Null(doc.Approval);
            Assert.False(result.View.CanBuild);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_later_word_for_the_same_object_replaces_the_earlier_one_and_never_stacks()
        {
            var rig = await RigAsync();
            await rig.Engine.WriteStartWordsAsync(new[] { W("description", "measure:Sales/Total Sales", "First try.") }, "agent");
            await rig.Engine.WriteStartWordsAsync(new[] { W("description", "measure:Sales/Total Sales", "Second try.") }, "agent");
            var line = Assert.Single(rig.Engine.StartPlanDocForTest().Lines.Where(l => l.Item.ObjectRef == "measure:Sales/Total Sales"));
            Assert.Equal("Second try.", line.Word);
            Assert.Equal("Second try.", line.Item.After);
        }

        // ---------------------------------------------------------------- the queue shrinks

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_queue_shrinks_as_words_are_written_and_the_plans_own_count_follows()
        {
            var rig = await RigAsync();
            var first = await rig.Engine.GetStartWordsAsync(40, 0);
            Assert.True(first.Total >= 3, "the model needs at least three open words: " + first.Total);

            await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("description", "measure:Sales/Total Sales", "What customers paid for orders, before tax."),
                W("description", "table:Customer", "The people who buy from us."),
            }, "agent");

            var after = await rig.Engine.GetStartWordsAsync(first.Total, 0);
            Assert.Equal(first.Total - 2, after.Total);
            Assert.DoesNotContain(after.Items, i => i.Kind == "description" && (i.Ref == "measure:Sales/Total Sales" || i.Ref == "table:Customer"));
            var words = rig.Engine.StartPlanDocForTest().Words;
            Assert.Equal(first.Total - 2, words.Open);
            Assert.Equal(first.Total, words.Total);   // two written plus the ones still open
        }

        // ---------------------------------------------------------------- a renamed object's format still lands

        [Fact]
        public async Task A_renamed_objects_format_still_lands_whichever_name_the_assistant_used()
        {
            var rig = await RigAsync(shape: async p =>
            {
                await p.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )", "human");
                await p.Engine.CreateMeasureAsync("table:Sales", "Spread Pct", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 2 )", "human");
            });
            var open = await AllItemsAsync(rig.Engine);
            Assert.Contains(open, i => i.Kind == "format" && i.Ref == "measure:Sales/Margin Pct");

            // One batch: the format is written under the NEW name (what the assistant will see once the rename is in the
            // preview), another under the OLD name of an object that is renamed too, in the other order.
            var result = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("format", "measure:Sales/Profit Margin", "0.0%"),
                W("display_name", "measure:Sales/Margin Pct", "Profit Margin"),
                W("display_name", "measure:Sales/Spread Pct", "Price Spread"),
                W("format", "measure:Sales/Spread Pct", "0.00%"),
            }, "agent");
            Assert.Equal(4, result.Written);
            Assert.Empty(result.Refused);

            // Lines are stored against the ref the object has BEFORE the plan's renames, because words run first in the build.
            var lines = rig.Engine.StartPlanDocForTest().Lines.Where(l => l.DecidedBy == StartDecidedBy.Assistant).ToList();
            Assert.Equal(2, lines.Count(l => l.Item.Kind == "set_measure_format"));
            Assert.All(lines.Where(l => l.Item.Kind == "set_measure_format"), l => Assert.Contains(l.Item.ObjectRef, new[] { "measure:Sales/Margin Pct", "measure:Sales/Spread Pct" }));

            // Apply the stored lines with the Plan's own apply (the same order the build runs them in): both formats land on the renamed measures.
            var engine = rig.Preview.Engine;
            ChangePlanView plan = null;
            foreach (var l in lines)
                plan = await engine.AddPlanItemAsync(l.Item.ObjectRef, l.Item.Kind, l.Item.After, l.Item.Title, null, null, "human");
            Assert.Equal(4, plan.Items.Length);
            foreach (var item in plan.Items)
                plan = await engine.SetPlanItemAsync(item.Id, null, true, "human");   // a rename waits for a tick; the build's single approval is this
            var report = await engine.ApplyPlanAsync(plan.Items.Select(i => i.Id).ToArray(), "human");
            Assert.True(report.AppliedCount == 4 && report.FailedCount == 0 && report.SkippedCount == 0,
                "plan had " + plan.Items.Length + ": " + string.Join(" | ", plan.Items.Select(i => i.Kind + " " + i.ObjectRef + " " + i.Status)) + ". applied " + report.AppliedCount + ", failed " + report.FailedCount + ", skipped " + report.SkippedCount + ": " + string.Join(" | ", report.Items.Select(i => i.Kind + " " + i.ObjectRef + " " + i.Status + " " + i.Note)));
            Assert.Equal("0.0%", await FormatOf(engine, "measure:Sales/Profit Margin"));
            Assert.Equal("0.00%", await FormatOf(engine, "measure:Sales/Price Spread"));
        }

        [Fact]
        public async Task Once_the_preview_shows_the_new_name_a_word_for_the_old_name_still_finds_it()
        {
            var rig = await RigAsync(shape: async p =>
                await p.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )", "human"));
            await rig.Engine.WriteStartWordsAsync(new[] { W("display_name", "measure:Sales/Margin Pct", "Profit Margin") }, "agent");
            // The next round of the plan makes the rename in the preview: the old ref no longer resolves there.
            await rig.Preview.Engine.RenameObjectAsync("measure:Sales/Margin Pct", "Profit Margin", "human");

            var byNew = await rig.Engine.WriteStartWordsAsync(new[] { W("format", "measure:Sales/Profit Margin", "0.0%") }, "agent");
            var byOld = await rig.Engine.WriteStartWordsAsync(new[] { W("description", "measure:Sales/Margin Pct", "Profit as a share of sales.") }, "agent");
            Assert.Equal(1, byNew.Written);
            Assert.Equal(1, byOld.Written);
            Assert.All(rig.Engine.StartPlanDocForTest().Lines, l => Assert.Equal("measure:Sales/Margin Pct", l.Item.ObjectRef));
        }

        private static async Task<string> FormatOf(IEngine engine, string objRef) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => p.Name == "FormatString")?.Value?.ToString() ?? "";

        // ---------------------------------------------------------------- a clash is refused, naming the clashing name

        [Fact]
        public async Task A_name_clash_is_refused_naming_the_clashing_name_and_the_rest_of_the_batch_is_written()
        {
            var rig = await RigAsync();
            var result = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("display_name", "measure:Sales/Total Sales", "Amount"),           // a column of the same table
                W("display_name", "table:Customer", "Product"),                      // another table
                W("display_name", "column:Customer/City", "Customer City"),          // free: no clash
                W("description", "table:Product", "The things we sell."),
            }, "agent");

            Assert.Equal(2, result.Written);
            var clash = Assert.Single(result.Refused.Where(r => r.StartsWith("measure:Sales/Total Sales")));
            Assert.Contains("Amount", clash);
            var tableClash = Assert.Single(result.Refused.Where(r => r.StartsWith("table:Customer")));
            Assert.Contains("Product", tableClash);
            var doc = rig.Engine.StartPlanDocForTest();
            Assert.DoesNotContain(doc.Lines, l => l.Item.ObjectRef == "measure:Sales/Total Sales");
            Assert.DoesNotContain(doc.Lines, l => l.Item.ObjectRef == "table:Customer");
            Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "table:Product" && l.Item.Kind == "set_description");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Two_objects_cannot_be_given_the_same_name_in_one_batch_or_across_batches()
        {
            var rig = await RigAsync(shape: async p =>
                await p.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human"));
            var first = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("display_name", "measure:Sales/Total Sales", "Revenue"),
                W("display_name", "measure:Sales/Order Count", "Revenue"),
            }, "agent");
            Assert.Equal(1, first.Written);
            Assert.Contains("Revenue", Assert.Single(first.Refused));
            // And later: the name is already taken by the plan's own rename.
            var again = await rig.Engine.WriteStartWordsAsync(new[] { W("display_name", "measure:Sales/Order Count", "revenue") }, "agent");
            Assert.Equal(0, again.Written);
            Assert.Contains("Revenue", Assert.Single(again.Refused), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- the other checks, and what stands

        [Fact]
        public async Task Formula_text_and_long_words_are_refused_per_item_and_do_not_stop_the_others()
        {
            var rig = await RigAsync();
            var result = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("description", "measure:Sales/Total Sales", "Adds up SUM ( Sales[Amount] ) for every order."),
                W("description", "table:Sales", new string('a', 201)),
                W("synonyms", "table:Customer", "buyers, [Customers]"),
                W("format", "measure:Sales/Total Sales", "0.0[x"),
                W("description", "table:Product", "The things we sell."),
                W("synonyms", "table:Product", "goods, items"),
                W("shout", "table:Product", "x"),
                W("description", "table:Nope", "x"),
            }, "agent");
            Assert.Equal(2, result.Written);
            Assert.Equal(6, result.Refused.Count);
            Assert.Contains(result.Refused, r => r.StartsWith("measure:Sales/Total Sales: It contains formula text"));
            Assert.Contains(result.Refused, r => r.StartsWith("table:Sales") && r.Contains("201 characters"));
            Assert.Contains(result.Refused, r => r.StartsWith("table:Customer") && r.Contains("formula text"));
            Assert.Contains(result.Refused, r => r.StartsWith("table:Nope") && r.Contains("not in the plan"));
            Assert.DoesNotContain(result.Refused, r => r.Contains("—"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task What_the_person_decided_dropped_or_pinned_is_never_written_over()
        {
            var rig = await RigAsync();
            var start = rig.Engine.StartPlanDocForTest();
            rig.Engine.StartPlanDocForTestMutate(d =>
            {
                d.Lines.Add(new StartLine { DecidedBy = StartDecidedBy.Person, Kept = true, Item = new ChangeItem { Kind = "set_description", ObjectRef = "table:Sales", Target = "description", After = "Mine." } });
                d.Lines.Add(new StartLine { DecidedBy = StartDecidedBy.Assistant, Kept = false, Word = "x", Item = new ChangeItem { Kind = "set_description", ObjectRef = "table:Product", Target = "description", After = "x" } });
                d.Pins["table:Customer|description"] = "Pinned.";
            });
            var result = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("description", "table:Sales", "The assistant's."),
                W("description", "table:Product", "Again."),
                W("description", "table:Customer", "Over the pin."),
            }, "agent");
            Assert.Equal(0, result.Written);
            Assert.Equal(3, result.Refused.Count);
            Assert.Contains(result.Refused, r => r.Contains("chose this yourself"));
            Assert.Contains(result.Refused, r => r.Contains("stays dropped"));
            Assert.Contains(result.Refused, r => r.Contains("pinned"));
            Assert.Equal("Mine.", rig.Engine.StartPlanDocForTest().Lines.Single(l => l.Item.ObjectRef == "table:Sales").Item.After);
        }

        // ---------------------------------------------------------------- Free can write words

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_can_write_words_with_no_jev_in_sight()
        {
            var rig = await RigAsync(pro: false);
            var page = await rig.Engine.GetStartWordsAsync(40, 0);
            Assert.True(page.Total > 0);
            var result = await rig.Engine.WriteStartWordsAsync(new[] { W("description", "measure:Sales/Total Sales", "What customers paid, before tax.") }, "agent");
            Assert.Equal(1, result.Written);
            Assert.Equal(StartDecideState.Off, result.View.Decide.State);   // Free stays off: the words are its own assistant's
            Assert.Equal(1, result.View.Sections.Single(s => s.Id == StartSectionId.Words).Counts.Assistant);
        }

        // ---------------------------------------------------------------- verifier attacks

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_page_never_passes_forty_thousand_characters_even_for_huge_formulas_and_the_pages_add_up()
        {
            var rig = await RigAsync(shape: async p =>
            {
                var heavy = "SUM ( Sales[Amount] )" + string.Concat(Enumerable.Repeat(" + 1", 1500));   // about 6,000 characters, a plus counted as six on the wire
                for (var i = 0; i < 24; i++)
                    await p.Engine.CreateMeasureAsync("table:Sales", "Heavy " + i, heavy, "human");
            });
            var json = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
            var seen = new List<string>();
            var total = -1;
            var pages = 0;
            for (var offset = 0; ;)
            {
                var page = await rig.Engine.GetStartWordsAsync(100, offset);
                var size = System.Text.Json.JsonSerializer.Serialize(page, json).Length;
                Assert.True(size <= LocalEngine.WordsPageChars, "page at offset " + offset + " is " + size + " characters");
                Assert.NotEmpty(page.Items);
                if (total < 0) total = page.Total;
                Assert.Equal(total, page.Total);
                seen.AddRange(page.Items.Select(i => i.Kind + "|" + i.Ref));
                offset += page.Items.Count;
                pages++;
                if (offset >= page.Total) break;
            }
            Assert.True(pages > 3, "the heavy measures should have needed several pages: " + pages);
            Assert.Equal(total, seen.Count);
            Assert.Equal(seen.Count, seen.Distinct().Count());
        }

        [Fact]
        public async Task Words_for_an_object_that_is_later_left_out_are_held_not_lost()
        {
            var rig = await RigAsync();
            var written = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("description", "table:Customer", "The people who buy from us."),
                W("description", "column:Customer/City", "Where the customer lives."),
                W("description", "table:Product", "The things we sell."),
            }, "agent");
            Assert.Equal(3, written.Written);

            // The person leaves the Customer table out: the preview no longer has it.
            var without = await ChangeCauseTests.PlanModelAsync("NoCustomer" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
            _owned.Add(without);
            await without.Engine.DeleteObjectAsync("table:Customer", "human");
            rig.Reader.Of = () => without.Sessions.Current;

            var page = await rig.Engine.GetStartWordsAsync(100, 0);
            Assert.DoesNotContain(page.Items, i => (i.Ref ?? "") == "table:Customer" || (i.Ref ?? "").StartsWith("column:Customer/", StringComparison.Ordinal));
            var doc = rig.Engine.StartPlanDocForTest();
            Assert.Equal(3, doc.Lines.Count(l => l.DecidedBy == StartDecidedBy.Assistant && l.Kept));   // none lost
            var present = await without.Sessions.Current.ReadAsync(m => StartPlanWords.PresentRefs(doc, m));
            Assert.Equal(2, doc.Lines.Count(l => l.DecidedBy == StartDecidedBy.Assistant && StartPlanWords.IsHeld(l, present)));
            Assert.Equal(1, doc.Words.Total - doc.Words.Open);   // only the Product line counts as written while the other two are held

            // Asking to write for the held object now is refused in plain words, and changes nothing.
            var again = await rig.Engine.WriteStartWordsAsync(new[] { W("description", "table:Customer", "Different.") }, "agent");
            Assert.Equal(0, again.Written);
            Assert.Contains("not in the plan", Assert.Single(again.Refused));

            // The table comes back: the same words are there, are not asked for again, and nothing was rewritten.
            rig.Reader.Of = () => rig.Preview.Sessions.Current;
            var back = await rig.Engine.GetStartWordsAsync(100, 0);
            Assert.DoesNotContain(back.Items, i => i.Kind == "description" && (i.Ref == "table:Customer" || i.Ref == "column:Customer/City"));
            Assert.Equal(3, rig.Engine.StartPlanDocForTest().Lines.Count(l => l.DecidedBy == StartDecidedBy.Assistant && l.Kept));
            Assert.Equal(3, rig.Engine.StartPlanDocForTest().Words.Total - rig.Engine.StartPlanDocForTest().Words.Open);
        }

        [Fact]
        public async Task Both_doors_writing_at_once_lose_nothing_and_cannot_both_take_one_name()
        {
            var rig = await RigAsync(shape: async p =>
                await p.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human"));
            for (var round = 0; round < 6; round++)
            {
                var gate = new ManualResetEventSlim(false);
                var human = Task.Run(async () =>
                {
                    gate.Wait();
                    return await rig.Engine.WriteStartWordsAsync(new[]
                    {
                        W("description", "table:Product", "Human " + round),
                        W("description", "table:Customer", "Human " + round),
                        W("display_name", round % 2 == 0 ? "measure:Sales/Total Sales" : "measure:Sales/Order Count", "Revenue " + round),
                    }, "human");
                });
                var agent = Task.Run(async () =>
                {
                    gate.Wait();
                    return await rig.Engine.WriteStartWordsAsync(new[]
                    {
                        W("description", "table:Product", "Agent " + round),
                        W("description", "column:Product/ProductKey", "Agent key " + round),
                        W("display_name", round % 2 == 0 ? "measure:Sales/Order Count" : "measure:Sales/Total Sales", "Revenue " + round),
                    }, "agent");
                });
                gate.Set();
                var results = await Task.WhenAll(human, agent);

                // The two wrote the same new name: exactly one of them got it.
                Assert.Equal(1, results.Sum(r => r.Refused.Count(x => x.Contains("Revenue " + round))));
                var doc = rig.Engine.StartPlanDocForTest();
                Assert.Equal(1, doc.Lines.Count(l => l.Item.Kind == "rename" && l.Item.After == "Revenue " + round));
                // One line per object and property, and nobody's line lost.
                var keys = doc.Lines.Select(l => l.Item.ObjectRef + "|" + l.Item.Target).ToList();
                Assert.Equal(keys.Count, keys.Distinct().Count());
                Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "table:Customer" && l.Word == "Human " + round);
                Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "column:Product/ProductKey" && l.Word == "Agent key " + round);
                var product = doc.Lines.Single(l => l.Item.ObjectRef == "table:Product" && l.Item.Target == "description");
                Assert.Contains(product.Word, new[] { "Human " + round, "Agent " + round });
                // Free the names again for the next round.
                rig.Engine.StartPlanDocForTestMutate(d => d.Lines.RemoveAll(l => l.Item.Kind == "rename"));
            }
        }

        // ---------------------------------------------------------------- the open-model queue is unchanged by the move

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_open_model_words_queue_reads_the_same_after_it_moved_into_its_own_file()
        {
            var rig = await RigAsync();
            var viaEngine = await rig.Preview.Engine.GetWordsQueueAsync();
            var viaCore = await rig.Preview.Sessions.Current.ReadAsync(m => LocalEngine.PageWordsQueue(m, viaEngine.Revision, 0, 0));
            Assert.Equal(viaEngine.Total, viaCore.Total);
            Assert.Equal(viaEngine.Items.Select(i => i.Kind + "|" + i.ObjectRef), viaCore.Items.Select(i => i.Kind + "|" + i.ObjectRef));
            var items = await rig.Preview.Sessions.Current.ReadAsync(m => LocalEngine.ReadWordsQueueItems(m, out _));
            Assert.Equal(viaEngine.Total, items.Count);
        }


        // ---------------------------------------------------------------- verifier attacks (v-sf-words, 1 Oct)

        [Theory]
        [InlineData(null)]
        [InlineData("model")]
        [InlineData("model:")]
        [InlineData("QUEUE")]
        public async Task Verify_ai_instructions_written_for_the_model_close_their_item_and_are_not_held(string modelRef)
        {
            // DAC-AI-INSTRUCTIONS only fires on a model with a linguistic schema, so the preview gets one.
            var rig = await RigAsync(shape: async p => await p.Engine.CreateLinguisticSchemaAsync(null, "human"));
            var open = await AllItemsAsync(rig.Engine, 100);
            var ask = Assert.Single(open.Where(i => i.Kind == "instructions"));
            if (modelRef == "QUEUE") modelRef = ask.Ref;   // the ref the words list itself handed the assistant
            var result = await rig.Engine.WriteStartWordsAsync(new[] { W("instructions", modelRef, "Answer in plain words. Sales means the Amount column.") }, "agent");
            Assert.Equal(1, result.Written);

            // The item the assistant answered is no longer asked for.
            var after = await AllItemsAsync(rig.Engine, 100);
            Assert.False(after.Any(i => i.Kind == "instructions"), "still asked for after it was written: queue ref " + ask.Ref + ", written as " + (modelRef ?? "(none)"));
            // And the line is a word the build will write, not one it holds back for an object that is gone.
            var doc = rig.Engine.StartPlanDocForTest();
            var line = Assert.Single(doc.Lines.Where(l => l.Item.Kind == "set_ai_instructions"));
            var present = await rig.Preview.Sessions.Current.ReadAsync(m => StartPlanWords.PresentRefs(doc, m));
            Assert.False(StartPlanWords.IsHeld(line, present), "the model's AI instructions line is held as if its object were gone: ref " + line.Item.ObjectRef);
            Assert.Equal(1, doc.Words.Total - doc.Words.Open);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("table:Sales")]
        [InlineData("measure:Sales/Total Sales")]
        public async Task AI_instructions_are_refused_for_an_object_that_is_not_the_model(string objRef)
        {
            var rig = await RigAsync(shape: async p => await p.Engine.CreateLinguisticSchemaAsync(null, "human"));
            var result = await rig.Engine.WriteStartWordsAsync(new[] { W("instructions", objRef, "Answer in plain words.") }, "agent");
            Assert.Equal(0, result.Written);
            Assert.Contains(result.Refused, r => r.Contains("belong to the model"));
            Assert.DoesNotContain(rig.Engine.StartPlanDocForTest().Lines, l => l.Item.Kind == "set_ai_instructions");
        }

        [Fact]
        public async Task A_plan_file_that_cannot_be_saved_is_not_reported_as_written_and_a_retry_saves_it()
        {
            var rig = await RigAsync();
            var id = rig.Engine.StartPlanDocForTest().Id;
            var file = Path.Combine(StartPlanStore.Dir(), id + ".json");
            File.Delete(file);
            Directory.CreateDirectory(file);   // a folder where the file goes: the move onto it fails
            var words = new[] { W("description", "table:Sales", "One row for each order line.") };

            var failed = await rig.Engine.WriteStartWordsAsync(words, "agent");
            Assert.Equal(0, failed.Written);
            Assert.Contains(failed.Refused, r => r.Contains("were not saved"));

            Directory.Delete(file);
            var retried = await rig.Engine.WriteStartWordsAsync(words, "agent");
            Assert.Equal(1, retried.Written);
            Assert.Empty(retried.Refused);
            Assert.True(File.Exists(file));
            Assert.Contains("One row for each order line.", File.ReadAllText(file));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("model")]
        [InlineData("Model")]
        [InlineData("model:")]
        [InlineData("model:Words Model")]
        [InlineData("obj:Model")]
        [InlineData("QUEUE")]
        public async Task Verify_ai_instructions_written_under_any_model_ref_survive_a_reload_and_are_built_onto_the_model(string modelRef)
        {
            var rig = await RigAsync(shape: async p => await p.Engine.CreateLinguisticSchemaAsync(null, "human"));
            if (modelRef == "QUEUE") modelRef = Assert.Single((await AllItemsAsync(rig.Engine, 100)).Where(i => i.Kind == "instructions")).Ref;
            const string text = "Answer in plain words. Sales means the Amount column.";
            Assert.Equal(1, (await rig.Engine.WriteStartWordsAsync(new[] { W("instructions", modelRef, text) }, "agent")).Written);

            // The plan as a restarted engine would see it: read back from disk, not the live copy.
            var disk = StartPlanStore.Read(rig.Engine.StartPlanDocForTest().Id);
            var line = Assert.Single(disk.Lines.Where(l => l.Item.Kind == "set_ai_instructions"));
            Assert.Equal(StartPlanWords.ModelRef, line.Item.ObjectRef);
            var present = await rig.Preview.Sessions.Current.ReadAsync(m => StartPlanWords.PresentRefs(disk, m));
            Assert.False(StartPlanWords.IsHeld(line, present), "held after reload: " + line.Item.ObjectRef);

            // Built through the executor every plan line goes through (ApplyOneItem), on the preview model.
            var apply = typeof(LocalEngine).GetMethod("ApplyOneItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var s = rig.Preview.Sessions.Current;
            await s.MutateAsync("engine", "build the start plan", m => apply.Invoke(rig.Preview.Engine, new object[] { m, line.Item, s, null }));
            Assert.Equal(text, (await rig.Preview.Engine.GetAiInstructionsAsync(null)).Instructions);
        }

        [Fact]
        public async Task Verify_words_for_a_renamed_table_that_is_left_out_and_back_are_held_then_kept()
        {
            var rig = await RigAsync();
            var written = await rig.Engine.WriteStartWordsAsync(new[]
            {
                W("display_name", "table:Customer", "Buyers"),
                W("description", "column:Buyers/City", "Where the buyer lives."),
                W("synonyms", "table:Buyers", "clients, shoppers"),
            }, "agent");
            Assert.Equal(3, written.Written);
            Assert.Empty(written.Refused);

            var without = await ChangeCauseTests.PlanModelAsync("NoCust" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bim");
            _owned.Add(without);
            await without.Engine.DeleteObjectAsync("table:Customer", "human");
            rig.Reader.Of = () => without.Sessions.Current;
            await rig.Engine.GetStartWordsAsync(100, 0);
            // A later batch about other objects writes nothing over the held lines.
            await rig.Engine.WriteStartWordsAsync(new[] { W("description", "table:Product", "The things we sell.") }, "agent");
            var doc = rig.Engine.StartPlanDocForTest();
            Assert.Equal(4, doc.Lines.Count(l => l.DecidedBy == StartDecidedBy.Assistant && l.Kept));

            rig.Reader.Of = () => rig.Preview.Sessions.Current;
            var back = await AllItemsAsync(rig.Engine, 100);
            Assert.DoesNotContain(back, i => i.Kind == "description" && i.Ref == "column:Customer/City");
            doc = rig.Engine.StartPlanDocForTest();
            Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "column:Customer/City" && l.Word == "Where the buyer lives.");
            Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "table:Customer" && l.Item.Kind == "rename" && l.Word == "Buyers");
            var present = await rig.Preview.Sessions.Current.ReadAsync(m => StartPlanWords.PresentRefs(doc, m));
            Assert.DoesNotContain(doc.Lines, l => StartPlanWords.IsHeld(l, present));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_both_doors_racing_on_one_name_in_two_cases_and_reads_in_between_lose_nothing()
        {
            var rig = await RigAsync(shape: async p =>
                await p.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human"));
            for (var round = 0; round < 20; round++)
            {
                var gate = new ManualResetEventSlim(false);
                var r = round;
                var human = Task.Run(async () => { gate.Wait(); return await rig.Engine.WriteStartWordsAsync(new[]
                {
                    W("display_name", "measure:Sales/Total Sales", "Takings " + r),
                    W("description", "table:Product", "H" + r),
                }, "human"); });
                var agent = Task.Run(async () => { gate.Wait(); return await rig.Engine.WriteStartWordsAsync(new[]
                {
                    W("display_name", "measure:Sales/Order Count", "TAKINGS " + r),
                    W("description", "table:Customer", "A" + r),
                }, "agent"); });
                var reader = Task.Run(async () => { gate.Wait(); return await rig.Engine.GetStartWordsAsync(40, 0); });
                gate.Set();
                var results = await Task.WhenAll(human, agent);
                await reader;
                var doc = rig.Engine.StartPlanDocForTest();
                Assert.Equal(1, doc.Lines.Count(l => l.Item.Kind == "rename" && string.Equals(l.Item.After, "Takings " + r, StringComparison.OrdinalIgnoreCase)));
                Assert.Equal(1, results.Sum(x => x.Refused.Count));
                Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "table:Product" && l.Word == "H" + r);
                Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "table:Customer" && l.Word == "A" + r);
                var keys = doc.Lines.Select(l => l.Item.ObjectRef + "|" + l.Item.Target).ToList();
                Assert.Equal(keys.Count, keys.Distinct().Count());
                // What is on disk is what is in memory: a restart would lose nothing.
                var onDisk = StartPlanStore.Read(doc.Id);
                Assert.Equal(doc.Lines.Count, onDisk?.Lines?.Count ?? -1);
                rig.Engine.StartPlanDocForTestMutate(d => d.Lines.RemoveAll(l => l.Item.Kind == "rename"));
            }
        }
    }
}
