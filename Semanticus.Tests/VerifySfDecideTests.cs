// The verifier's attacks on lane m11/sf-decide (L9, the start plan's decide loop). Each test names the attack it runs:
// consent (an excluded table never reaches Jev), words that a later round or an edit must never overwrite or lose, a pick Jev keeps
// flipping, an edit that lands while an ask is in flight, Needs follow after a real round, and no ask before the start plan's own grant.
// The rig mirrors StartPlanDecideTests: a scratch built on a second engine from the manifest's spec, kept lines applied by the Plan's executor.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>An oracle that answers like the lane's policy oracle, writes down the full text of every request, and can turn one
    /// object's answer around on every new request about it (a Jev that keeps changing its mind).</summary>
    internal sealed class VerifyTextOracle : IDecisionOracle
    {
        private readonly object _gate = new object();
        public readonly List<string> Texts = new List<string>();
        public int Asks;
        /// <summary>The object whose summarize answer flips between sum and average on each new request that asks about it. Null: none.</summary>
        public string FlipRef;
        private int _flips;
        public Func<DecisionRequest, CancellationToken, Task> BeforeAnswer;
        public bool Available => true;

        public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            if (BeforeAnswer != null) await BeforeAnswer(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = request.PointName + " " + JsonSerializer.Serialize(request.Snapshot) + " " + JsonSerializer.Serialize(request.Questions.Select(q => q.Name + " " + q.Instructions)); }
            catch { text = request.PointName + " " + string.Join(" ", request.Snapshot.Select(kv => kv.Key + "=" + kv.Value)) + " " + string.Join(" ", request.Questions.Select(q => q.Name)); }
            lock (_gate) { Asks++; Texts.Add(text); }
            var body = new StringBuilder("{");
            var first = true;
            foreach (var q in request.Questions)
            {
                var colon = q.Name.IndexOf(':');
                var plain = colon > 0 ? q.Name.Substring(0, colon) : q.Name;
                var objectRef = colon > 0 ? q.Name.Substring(colon + 1) : "";
                var (choice, confidence) = Choose(plain, objectRef, q);
                if (plain == "summarize_by" && FlipRef != null && string.Equals(objectRef, FlipRef, StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    lock (_gate) n = ++_flips;
                    choice = n % 2 == 1 ? "sum" : "average";
                    if (!q.HasOption(choice)) choice = q.Options[0].Id;
                    confidence = 0.95;
                }
                if (choice == null) continue;
                if (!first) body.Append(',');
                first = false;
                body.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                    .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            body.Append('}');
            return DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, "jev-test-1", 900, 40);
        }

        private static (string Choice, double Confidence) Choose(string question, string objectRef, DecisionQuestion q)
        {
            var name = objectRef.Substring(objectRef.LastIndexOf('/') + 1).ToLowerInvariant();
            string Pick(params string[] wanted) => wanted.FirstOrDefault(w => q.HasOption(w)) ?? q.Options[0].Id;
            bool Has(params string[] parts) => parts.Any(p => name.Contains(p));
            switch (question)
            {
                case "summarize_by":
                    if (name.EndsWith("key") || name.EndsWith("id") || name.EndsWith("code") || name.EndsWith("_no") || name.EndsWith("year")) return (Pick("none"), 0.95);
                    if (Has("price", "rate", "pct", "percent", "score")) return (Pick("average"), 0.95);
                    return (Pick("sum"), 0.95);
                case "format_family":
                    if (Has("rate", "pct", "percent", "margin")) return (Pick("percent"), 0.93);
                    if (Has("qty", "quantity", "count", "number", "units", "hrs")) return (Pick("whole_number"), 0.93);
                    if (Has("amount", "price", "cost", "revenue", "total", "sales", "amt")) return (Pick("currency"), 0.93);
                    return (Pick("decimal"), 0.93);
                case "display_folder": return (Pick(DecisionRegistry.NoFolder), 0.9);
                case "ai_schema": return (Pick("include"), 0.93);
                case "data_category": return (Pick("none"), 0.93);
                default: return (Pick("none", "no"), 0.9);
            }
        }
    }

    public sealed class VerifySfDecideTests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private sealed class Host : IStartScratchHost
        {
            public async Task<StartScratch> BuildAsync(StartPlanDoc doc, CancellationToken ct)
            {
                var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                System.IO.Directory.CreateDirectory(home);
                var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, TestEntitlements.Pro, home);
                try
                {
                    await engine.CreateModelAsync("Uplift", 1604);
                    await engine.SetSpecAsync(JsonSerializer.Serialize(doc.Manifest.ToSpec(), Camel), "human");
                    await engine.BuildModelFromSpecAsync("human");
                    var session = sessions.Current;
                    var refused = new List<(StartLine, string)>();
                    var lines = doc.Lines.Where(l => l.Kept).ToList();
                    if (lines.Count > 0)
                    {
                        var plan = sessions.CurrentContext.Plans.GetOrStart();
                        var byItem = new List<(ChangeItem Item, StartLine Line)>();
                        foreach (var l in lines)
                        {
                            var item = StartPlanDecide.ToChangeItem(l);
                            plan.Add(item, session.Revision, "");
                            byItem.Add((item, l));
                        }
                        var report = await engine.ApplyPlanAsync(null, "human");
                        foreach (var r in report.Items.Where(i => i.Status != "applied"))
                        {
                            var mine = byItem.FirstOrDefault(b => b.Item.Id == r.Id).Line;
                            if (mine != null) refused.Add((mine, r.Note));
                        }
                    }
                    return new StartScratch
                    {
                        Session = session, Refused = refused,
                        OnDispose = () => { try { engine.Dispose(); } catch { } try { sessions.Dispose(); } catch { } try { System.IO.Directory.Delete(home, true); } catch { } },
                    };
                }
                catch
                {
                    try { engine.Dispose(); sessions.Dispose(); } catch { }
                    throw;
                }
            }
        }

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public LocalEngine Engine => F.Engine;
            public Host Host = new Host();
            public StartPlanDoc Doc;
            public static async Task<Rig> CreateAsync(FabricSchema schema, IDecisionOracle oracle)
            {
                var rig = new Rig { F = new DecisionFixture(TestEntitlements.Pro), Doc = StartPlanDecideTests.DocFor(schema) };
                await rig.F.Engine.CreateModelAsync("Uplift", 1604);
                await rig.F.SaveAsAsync("Uplift.bim");
                await rig.F.ConsentAsync();
                rig.F.Engine.UseDecisionsForTest(new DecisionService(oracle));
                return rig;
            }
            public Task<bool> DecideAsync() => Engine.DecideStartPlanAsync(Doc, Host, CancellationToken.None);
            public void Dispose() => F.Dispose();
        }

        private static string Describe(StartPlanDoc doc) => doc.Decide.State + " round " + doc.Decide.Round + ": " + doc.Decide.Reason;

        /// <summary>ATTACK consent: a table the person left out is never named to Jev, neither on the first decide nor on one after it was
        /// left out mid-way (the book, the progress memory and the service cache must not carry it back in).</summary>
        [Fact]
        public async Task Verify_a_table_left_out_is_never_named_in_any_request()
        {
            var banned = new[] { "dim_staff", "GRADE_CD", "STD_RATE", "TERM_FLG", "EMP_NM", "HIRE_DT_KEY" };
            bool Mentions(string t) => banned.Any(b => t.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);

            // Left out before the first decide.
            var oracle = new VerifyTextOracle();
            using (var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle))
            {
                Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human").Ok);
                Assert.True(await rig.DecideAsync());
                Assert.True(oracle.Texts.Count > 0, Describe(rig.Doc));
                var leak = oracle.Texts.Where(Mentions).ToList();
                Assert.True(leak.Count == 0, leak.Count + " requests name the left-out table: " + string.Join(" || ", leak.Take(2)));
            }

            // Left out after a decide that saw it: only the requests after the edit count.
            var later = new VerifyTextOracle();
            using (var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), later))
            {
                Assert.True(await rig.DecideAsync());
                Assert.Contains(later.Texts, Mentions);   // the table was there and was asked about
                var before = later.Texts.Count;
                Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human").Ok);
                Assert.True(await rig.DecideAsync());
                var leak = later.Texts.Skip(before).Where(Mentions).ToList();
                Assert.True(leak.Count == 0, leak.Count + " requests after the edit name the left-out table: " + string.Join(" || ", leak.Take(2)));
                Assert.DoesNotContain(rig.Doc.Lines, l => (l.Item.ObjectRef ?? "").IndexOf("dim_staff", StringComparison.OrdinalIgnoreCase) >= 0);
                Assert.True(rig.Doc.Decide.State == StartDecideState.Stable, Describe(rig.Doc));
            }
        }

        private static StartLine AssistantWords(StartPlanDoc doc, string table, string column)
        {
            var bm = doc.Manifest.BaseMeasures.Single(b => b.Table == table && b.Column == column);
            var line = doc.Lines.Single(l => l.Item.ObjectRef == StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name) && l.Item.Kind == "set_description");
            line.Item.After = "How much was invoiced, written by the assistant.";
            line.DecidedBy = StartDecidedBy.Assistant;
            line.Word = line.Item.After;
            line.Kept = true;
            line.Item.Status = "approved";
            return line;
        }

        /// <summary>ATTACK S10 and words: after the assistant writes words and the person pins a value, an unrelated rename sends the loop round
        /// again until it settles, and neither the assistant's words nor the person's pin is overwritten by any later round.</summary>
        [Fact]
        public async Task Verify_words_and_a_pin_survive_every_later_round_after_an_unrelated_rename()
        {
            var oracle = new VerifyTextOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            Assert.True(rig.Doc.Decide.State == StartDecideState.Stable, Describe(rig.Doc));

            var words = AssistantWords(rig.Doc, "fact_invoice", "INV_AMT");
            var wordsKey = StartPlanDecide.LineKey(words);
            var fmt = rig.Doc.Lines.First(l => l.Kept && l.Item.Kind == "set_measure_format" && l.DecidedBy == StartDecidedBy.Jev);
            var fmtKey = StartPlanDecide.LineKey(fmt);
            var pin = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "pin", Target = fmtKey, Value = "#,0.00" }, "human");
            Assert.True(pin.Ok, pin.Refused);
            Assert.True(await rig.DecideAsync());
            var written = rig.Doc.Words.Total - rig.Doc.Words.Open;

            var rename = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "rename", Target = "column:dim_client/CLNT_NM", Value = "Client Name" }, "human");
            Assert.True(rename.Ok, rename.Refused);
            Assert.True(await rig.DecideAsync());
            Assert.True(rig.Doc.Decide.State == StartDecideState.Stable, Describe(rig.Doc));

            var w = rig.Doc.Lines.Single(l => StartPlanDecide.LineKey(l) == wordsKey);
            Assert.Equal((StartDecidedBy.Assistant, "How much was invoiced, written by the assistant.", true), (w.DecidedBy, w.Item.After, w.Kept));
            Assert.Equal(w.Item.After, w.Word);
            var p = rig.Doc.Lines.Single(l => StartPlanDecide.LineKey(l) == fmtKey);
            Assert.Equal((StartDecidedBy.Person, "#,0.00", true), (p.DecidedBy, p.Item.After, p.Kept));
            Assert.Equal(written, rig.Doc.Words.Total - rig.Doc.Words.Open);
        }

        private static StartLine ColumnWords(string table, string column, string text) => new StartLine
        {
            Item = new ChangeItem
            {
                ObjectRef = "column:" + table + "/" + column, ObjectName = column, Kind = "set_description", Target = "description",
                After = text, Source = "assistant", Status = "approved", Title = "Describe " + column,
            },
            DecidedBy = StartDecidedBy.Assistant, Word = text, Kept = true, Round = 0,
        };

        /// <summary>ATTACK words held, not lost (design L10's attack, made by this lane's edit and merge): the assistant's words on a column of a
        /// table the person leaves out and brings back are still there. Both ways a table goes: the edit, and a round run while it is out.</summary>
        [Fact]
        public async Task Verify_assistant_words_on_a_table_left_out_and_brought_back_are_held_not_lost()
        {
            const string text = "The staff member's full name, written by the assistant.";
            // 1. Through edit_start_plan alone.
            var doc = StartPlanDecideTests.DocFor(SqlDraftBuildTests.J8Schema());
            doc.Lines.Add(ColumnWords("dim_staff", "EMP_NM", text));
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human").Ok);
            Assert.True(doc.Lines.Any(l => l.Word == text), "the edit threw the assistant's words away");
        }

        /// <summary>The same attack through a decide round alone: the table is left out by another writer (the flag set directly), a round runs,
        /// the table comes back, and the assistant's words on it must still be there.</summary>
        [Fact]
        public async Task Verify_assistant_words_survive_a_round_run_while_their_table_is_out()
        {
            const string text = "The staff member's full name, written by the assistant.";
            var oracle = new VerifyTextOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            rig.Doc.Lines.Add(ColumnWords("dim_staff", "EMP_NM", text));
            rig.Doc.Manifest.Tables.Single(t => t.Name == "dim_staff").Include = false;
            Assert.True(await rig.DecideAsync());
            rig.Doc.Manifest.Tables.Single(t => t.Name == "dim_staff").Include = true;
            Assert.True(await rig.DecideAsync());
            Assert.True(rig.Doc.Lines.Any(l => l.Word == text), "a decide round threw the assistant's words away");
        }

        /// <summary>ATTACK oscillation: Jev answers sum, then average, then sum... for one column on every new request about it. The loop must end
        /// within its cap and must not call the plan settled on that pick without saying so (Needs you, or Incomplete).</summary>
        [Fact]
        public async Task Verify_a_pick_Jev_keeps_flipping_ends_the_loop_and_is_not_called_settled_quietly()
        {
            var oracle = new VerifyTextOracle { FlipRef = "column:fact_invoice/TAX_AMT" };
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            Assert.InRange(rig.Doc.Decide.Round, 1, LocalEngine.StartDecideMaxRounds);
            var state = rig.Doc.Decide.State;
            Assert.True(state == StartDecideState.Stable || state == StartDecideState.Incomplete, Describe(rig.Doc));
            var bm = rig.Doc.Manifest.BaseMeasures.FirstOrDefault(b => b.Table == "fact_invoice" && b.Column == "TAX_AMT");
            var needs = StartPlanDecide.NeedsYou(rig.Doc);
            var said = state == StartDecideState.Incomplete
                       || needs.Any(n => n.Text.IndexOf("TAX", StringComparison.OrdinalIgnoreCase) >= 0 || n.Id.IndexOf("TAX", StringComparison.OrdinalIgnoreCase) >= 0);
            var flips = oracle.Texts.Count(t => t.IndexOf("TAX_AMT", StringComparison.OrdinalIgnoreCase) >= 0 && t.StartsWith(DecisionRegistry.ColumnSummarize, StringComparison.Ordinal));
            Assert.True(flips <= 1 || said, "Jev was asked about TAX_AMT " + flips + " times and flipped; the plan ends " + Describe(rig.Doc)
                + " with pick " + (bm == null ? "(no measure)" : bm.Pick + " by " + bm.DecidedBy) + " and nothing in Needs you about it");
        }

        // ------------------------------------------------------------------ round 2 re-check: lost work, once more

        /// <summary>ATTACK lost work (re-check): the person renames a table, then leaves it out, a round runs, then brings it back. The assistant's
        /// words on its column are still on the plan, kept. Then the same with a column renamed and that column left out.</summary>
        [Fact]
        public async Task Verify_assistant_words_survive_a_rename_then_a_leave_out_and_come_back()
        {
            const string text = "The staff member's full name, written by the assistant.";
            var oracle = new VerifyTextOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            rig.Doc.Lines.Add(ColumnWords("dim_staff", "EMP_NM", text));
            Assert.True(await rig.DecideAsync());

            var ren = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "rename", Target = "table:dim_staff", Value = "Staff" }, "human");
            Assert.True(ren.Ok, ren.Refused);
            Assert.True(await rig.DecideAsync());
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "Staff", Value = "false" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.True(rig.Doc.Lines.Any(l => l.Word == text), "lost while the renamed table was out: " + Describe(rig.Doc));
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_table", Target = "Staff", Value = "true" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            var w = rig.Doc.Lines.SingleOrDefault(l => l.Word == text);
            Assert.True(w != null && w.Kept && w.DecidedBy == StartDecidedBy.Assistant, "the words did not come back with the renamed table");

            var renCol = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "rename", Target = "column:Staff/EMP_NM", Value = "Employee Name" }, "human");
            Assert.True(renCol.Ok, renCol.Refused);
            Assert.True(await rig.DecideAsync());
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_column", Target = "Staff/Employee Name", Value = "false" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_column", Target = "Staff/Employee Name", Value = "true" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            w = rig.Doc.Lines.SingleOrDefault(l => l.Word == text);
            Assert.True(w != null && w.Kept, "the words did not come back with the renamed column");
            Assert.True(rig.Doc.Decide.State == StartDecideState.Stable, Describe(rig.Doc));
        }

        /// <summary>ATTACK lost work (re-check, the measure case): the assistant writes words on a base measure; the person leaves that measure's column
        /// out (which takes the measure with it), a round runs, and the person brings the column back, which brings the same measure back. The words must
        /// be held, as they are for a table left out (or a measure dropped by drop_line). DEFECT on m11/sf-decide at 5c48f42e: include_column false
        /// deletes them through RemoveFollowers ("removed" knock-on) and the measure comes back without them. Left failing on purpose.</summary>
        [Fact]
        public async Task Verify_assistant_words_on_a_base_measure_whose_column_is_left_out_and_brought_back_are_held_not_lost()
        {
            var oracle = new VerifyTextOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), oracle);
            Assert.True(await rig.DecideAsync());
            var words = AssistantWords(rig.Doc, "fact_invoice", "INV_AMT");
            var text = words.Word;
            var measureRef = words.Item.ObjectRef;
            Assert.True(await rig.DecideAsync());
            Assert.True(rig.Doc.Lines.Any(l => l.Word == text));

            var outEdit = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_column", Target = "fact_invoice/INV_AMT", Value = "false" }, "human");
            Assert.True(outEdit.Ok, outEdit.Refused);
            var told = outEdit.KnockOns.Any(k => string.Equals(k.Ref, measureRef, StringComparison.OrdinalIgnoreCase) && k.Key.StartsWith("set_description", StringComparison.Ordinal));
            Assert.True(await rig.DecideAsync());
            var heldWhileOut = rig.Doc.Lines.Any(l => l.Word == text);
            Assert.True(StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "include_column", Target = "fact_invoice/INV_AMT", Value = "true" }, "human").Ok);
            Assert.True(await rig.DecideAsync());
            var back = rig.Doc.Manifest.BaseMeasures.FirstOrDefault(b => b.Table == "fact_invoice" && b.Column == "INV_AMT");
            Assert.True(rig.Doc.Lines.Any(l => l.Word == text),
                "the assistant's words on " + measureRef + " are gone: held while out=" + heldWhileOut + ", the edit said so=" + told
                + ", the measure is back as " + (back == null ? "(none)" : StarterMeasuresFamily.MeasureRef(back.Table, back.Name) + " " + back.Pick));
        }
    }

    /// <summary>The verb, both doors, over the REAL scratch host: cancel on edit mid-ask, Needs follow after a real round, no ask before
    /// the start plan's own grant.</summary>
    [Collection("restore-root")]
    public sealed class VerifySfDecideVerbTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly DecisionFixture _f;

        public VerifySfDecideVerbTests()
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, System.IO.Path.Combine(_root, "home", ".semanticus"));
            _f = new DecisionFixture(TestEntitlements.Pro);
            _f.Engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            _f.Dispose();
            try { System.IO.Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine Engine => _f.Engine;

        private async Task StartAsync() =>
            await Engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", Name = "Uplift",
                Folder = System.IO.Path.Combine(_root, "dest"),
            }, "human");

        private async Task<StartPlanDoc> DecidedAsync(VerifyTextOracle oracle)
        {
            await StartAsync();
            await _f.ConsentAsync();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(doc => doc.Decide = new StartDecide { State = StartDecideState.Deciding });
            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();
            var doc = Engine.StartPlanDocForTest();
            Assert.True(doc.Decide.State == StartDecideState.Stable, doc.Decide.State + ": " + doc.Decide.Reason);
            return doc;
        }

        /// <summary>ATTACK cancel on edit: an edit lands while Jev is mid-answer. The loop stops, the edit is kept (no round writes over it), the
        /// next loop settles, and a person's pin made after it survives the loop that pin starts.</summary>
        [Fact]
        public async Task Verify_an_edit_mid_ask_stops_the_loop_keeps_the_edit_and_a_later_pin_survives()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = 1;
            var oracle = new VerifyTextOracle
            {
                BeforeAnswer = async (r, ct) =>
                {
                    if (Volatile.Read(ref block) == 1) { reached.TrySetResult(true); await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                },
            };
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(doc => doc.Decide = new StartDecide { State = StartDecideState.Deciding });
            await Engine.KickStartDecideOnPlanAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(120));

            var link = Engine.StartPlanDocForTest().Manifest.Links.First(l => l.Active);
            Volatile.Write(ref block, 0);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            gate.TrySetResult(true);
            await Engine.StartDecideSettledForTest();

            var doc = Engine.StartPlanDocForTest();
            Assert.False(doc.Manifest.Links.Single(l => l.Identity == link.Identity).Active);
            Assert.True(doc.Decide.State == StartDecideState.Stable, doc.Decide.State + ": " + doc.Decide.Reason);
            Assert.NotEmpty(doc.Lines);

            var fmt = doc.Lines.First(l => l.Kept && l.Item.Kind == "set_measure_format" && l.DecidedBy == StartDecidedBy.Jev);
            var key = StartPlanDecide.LineKey(fmt);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = key, Value = "#,0.00" }, "human");
            await Engine.StartDecideSettledForTest();
            var after = Engine.StartPlanDocForTest();
            var pinned = after.Lines.Single(l => StartPlanDecide.LineKey(l) == key);
            Assert.Equal((StartDecidedBy.Person, "#,0.00", true), (pinned.DecidedBy, pinned.Item.After, pinned.Kept));
            Assert.True(after.Decide.State == StartDecideState.Stable, after.Decide.State + ": " + after.Decide.Reason);
        }

        /// <summary>ATTACK S12 after a real round: dropping a base measure drops its followers; the loop then runs over a scratch without that
        /// measure; keeping one follower afterwards must still bring the measure and its pair back (the knock-on promise).</summary>
        [Fact]
        public async Task Verify_keeping_a_follower_after_a_round_still_brings_its_measure_back()
        {
            var doc = await DecidedAsync(new VerifyTextOracle());
            var bm = doc.Manifest.BaseMeasures.First(b => b.Pick != StarterMeasureTemplates.None
                                                           && doc.Lines.Count(l => StartPlanDecide.NeedsOf(l) == StartPlanDecide.MeasureNeedsKey(b)) >= 2);
            var measureRef = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name);
            var follower = doc.Lines.First(l => StartPlanDecide.NeedsOf(l) == StartPlanDecide.MeasureNeedsKey(bm));
            var followerKey = StartPlanDecide.LineKey(follower);

            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = measureRef }, "human");
            await Engine.StartDecideSettledForTest();
            var dropped = Engine.StartPlanDocForTest();
            Assert.Equal(StarterMeasureTemplates.None, dropped.Manifest.BaseMeasures.Single(b => b.Identity == bm.Identity).Pick);

            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "keep_line", Target = followerKey }, "human");
            await Engine.StartDecideSettledForTest();
            var back = Engine.StartPlanDocForTest();
            Assert.Equal(bm.Pick, back.Manifest.BaseMeasures.Single(b => b.Identity == bm.Identity).Pick);
        }

        /// <summary>ATTACK consent before the grant (S2, the fence this lane stubs for sf-consent): the model's own Jev permission is given, the start
        /// plan's disclosure is not, and an edit restarts the loop. Nothing about the source may reach Jev until the person sees and grants it.
        /// DEFECT on m11/sf-decide at 3db5e4fa: 60 requests go out. Left failing on purpose.</summary>
        [Fact]
        public async Task Verify_no_ask_reaches_Jev_before_the_start_plan_grant_even_through_an_edit()
        {
            await StartAsync();
            await _f.ConsentAsync();   // the model's own permission: NOT the start plan's disclosure
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            var doc = Engine.StartPlanDocForTest();
            Assert.Equal(StartDecideState.WaitingGrant, doc.Decide.State);
            Assert.True(doc.Disclosure == null || doc.Disclosure.IncludedTables == null || doc.Disclosure.IncludedTables.Length == 0);

            var link = doc.Manifest.Links.First(l => l.Active);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            await Engine.StartDecideSettledForTest();
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests went to Jev before the start plan was granted");
        }

        // ------------------------------------------------------------------ round 2 re-check: consent from every path

        private static string[] AllSourceTables(StartPlanDoc d) => d.Manifest.Tables.Where(t => t.Include).Select(StartPlanDecide.SourceNameOf).ToArray();

        /// <summary>ATTACK consent, the kick and the unpark: the plan says deciding and carries a grant, but for ANOTHER source. Neither a direct kick nor
        /// the unpark read may send anything.</summary>
        [Fact]
        public async Task Verify_a_kick_or_an_unpark_with_a_grant_for_another_source_sends_nothing()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d =>
            {
                d.Decide = new StartDecide { State = StartDecideState.Deciding };
                d.Disclosure = new StartDisclosure { SourceKey = "other-sql.example.com/OtherDb", IncludedTables = AllSourceTables(d), SampleFacts = false, Utc = DateTime.UtcNow };
            });
            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests after a kick under a grant for another source");

            Engine.StartPlanDocForTestMutate(d => d.Parked = true);
            await Engine.GetStartPlanAsync();   // unparks (the model is open) and would restart the loop
            await Engine.StartDecideSettledForTest();
            Assert.False(Engine.StartPlanDocForTest().Parked);
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests after an unpark under a grant for another source");

            // And an edit under that grant waits for the person, with the reason, and asks nothing.
            var link = Engine.StartPlanDocForTest().Manifest.Links.First(l => l.Active);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            await Engine.StartDecideSettledForTest();
            var doc = Engine.StartPlanDocForTest();
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests after an edit under a grant for another source");
            Assert.Equal(StartDecideState.WaitingGrant, doc.Decide.State);
            Assert.Contains("different source", doc.Decide.Reason);
        }

        /// <summary>ATTACK consent, a table added by an edit while an ask is in flight: the grant left dim_staff out; the person puts it back mid-ask.
        /// Nothing about dim_staff is ever sent, no ask leaves after the edit, and the plan waits for a new grant.</summary>
        [Fact]
        public async Task Verify_a_table_put_back_mid_ask_stops_every_later_ask_and_is_never_named()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = 1; var after = 0; var late = 0;
            var oracle = new VerifyTextOracle
            {
                BeforeAnswer = async (r, ct) =>
                {
                    if (Volatile.Read(ref after) == 1) Interlocked.Increment(ref late);
                    if (Volatile.Read(ref block) == 1) { reached.TrySetResult(true); await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                },
            };
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d =>
            {
                d.Manifest.Tables.Single(t => t.Name == "dim_staff").Include = false;
                d.Decide = new StartDecide { State = StartDecideState.Deciding };   // the grant this stands in for names every table but dim_staff
            });
            Assert.DoesNotContain(Engine.StartPlanDocForTest().Disclosure.IncludedTables, n => n.IndexOf("dim_staff", StringComparison.OrdinalIgnoreCase) >= 0);
            await Engine.KickStartDecideOnPlanAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(120));

            Volatile.Write(ref block, 0);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human");
            Volatile.Write(ref after, 1);
            gate.TrySetResult(true);
            await Engine.StartDecideSettledForTest();
            await Engine.KickStartDecideOnPlanAsync();   // a stray kick changes nothing either
            await Engine.StartDecideSettledForTest();

            var doc = Engine.StartPlanDocForTest();
            Assert.True(late == 0, late + " asks left after dim_staff was put back without a new grant");
            Assert.DoesNotContain(oracle.Texts, t => t.IndexOf("dim_staff", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Equal(StartDecideState.WaitingGrant, doc.Decide.State);
            Assert.Contains("added after you agreed", doc.Decide.Reason);
        }

        private static Semanticus.Engine.Facts.SourceSampleResult SampleOf(StartPlanDoc doc) => new Semanticus.Engine.Facts.SourceSampleResult
        {
            Server = "contoso-sql.example.com", Database = "ContosoConsulting", MeasuredAtUtc = DateTime.UtcNow.ToString("o"), RowLimit = 20000,
            Tables = doc.Manifest.Tables.Select(t => new Semanticus.Engine.Facts.SourceTableFacts { Schema = t.SourceSchema, Name = t.SourceName, Rows = 1234 }).ToArray(),
        };

        /// <summary>ATTACK consent, the sample switched on after a grant that said no sample: a kick and an edit send nothing, and the plan says why.</summary>
        [Fact]
        public async Task Verify_a_sample_switched_on_after_the_grant_sends_nothing_by_kick_or_edit()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d => d.Decide = new StartDecide { State = StartDecideState.Deciding });   // granted with no sample
            Assert.False(Engine.StartPlanDocForTest().Disclosure.SampleFacts);
            var doc = Engine.StartPlanDocForTest();
            Engine.SetStartPlanSample(doc.Id, SampleOf(doc));
            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests after a kick with the sample switched on past the grant");

            var link = doc.Manifest.Links.First(l => l.Active);
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            await Engine.StartDecideSettledForTest();
            var after = Engine.StartPlanDocForTest();
            Assert.True(oracle.Asks == 0, oracle.Asks + " requests after an edit with the sample switched on past the grant");
            Assert.Equal(StartDecideState.WaitingGrant, after.Decide.State);
            Assert.Contains("switched on", after.Decide.Reason);
        }

        /// <summary>ATTACK consent, the sample switched on WHILE the loop runs (no edit, so nothing cancels it): the round in flight may finish its asks
        /// (they carry no sample facts), but no ask is sent from a scratch that carries the sample's facts, and the plan does not end as settled.</summary>
        [Fact]
        public async Task Verify_a_sample_switched_on_mid_loop_sends_nothing_from_a_scratch_with_its_facts()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = 1;
            var byRound = new System.Collections.Concurrent.ConcurrentBag<int>();
            var oracle = new VerifyTextOracle
            {
                BeforeAnswer = async (r, ct) =>
                {
                    byRound.Add(Engine.StartDecideRoundsForTest);
                    if (Volatile.Read(ref block) == 1) { reached.TrySetResult(true); await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                },
            };
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d => d.Decide = new StartDecide { State = StartDecideState.Deciding });   // granted with no sample
            await Engine.KickStartDecideOnPlanAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(120));
            var doc = Engine.StartPlanDocForTest();
            Engine.SetStartPlanSample(doc.Id, SampleOf(doc));
            Volatile.Write(ref block, 0);
            gate.TrySetResult(true);
            await Engine.StartDecideSettledForTest();

            var end = Engine.StartPlanDocForTest();
            var later = byRound.Count(n => n >= 2);
            Assert.True(later == 0, later + " asks were sent in round 2 or later, from a scratch carrying sample facts the grant did not cover; ended " + end.Decide.State + ": " + end.Decide.Reason);
            Assert.NotEqual(StartDecideState.Stable, end.Decide.State);
        }

        /// <summary>ATTACK consent, a withdraw mid-loop (the model's own permission taken back while an ask waits): no ask leaves after it.</summary>
        [Fact]
        public async Task Verify_a_permission_withdrawn_mid_loop_stops_every_later_ask()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = 1; var after = 0; var late = 0;
            var oracle = new VerifyTextOracle
            {
                BeforeAnswer = async (r, ct) =>
                {
                    if (Volatile.Read(ref after) == 1) Interlocked.Increment(ref late);
                    if (Volatile.Read(ref block) == 1) { reached.TrySetResult(true); await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                },
            };
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d => d.Decide = new StartDecide { State = StartDecideState.Deciding });
            await Engine.KickStartDecideOnPlanAsync();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(120));
            Volatile.Write(ref block, 0);
            await Engine.WithdrawConsentAsync("human");
            Volatile.Write(ref after, 1);
            gate.TrySetResult(true);
            await Engine.StartDecideSettledForTest();
            var end = Engine.StartPlanDocForTest();
            Assert.True(late == 0, late + " asks left after the permission was withdrawn; ended " + end.Decide.State + ": " + end.Decide.Reason);
            Assert.NotEqual(StartDecideState.Stable, end.Decide.State);
        }

        /// <summary>ATTACK lost work, a restart: the assistant's words on a table the person left out are on disk, held; a new engine reads the plan,
        /// the person brings the table back, a round runs on the new engine, and the words are there and kept.</summary>
        [Fact]
        public async Task Verify_held_words_survive_an_engine_restart_and_come_back_with_their_table()
        {
            const string text = "The staff member's full name, written by the assistant.";
            await StartAsync();
            await _f.ConsentAsync();
            var oracle = new VerifyTextOracle();
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            Engine.StartPlanDocForTestMutate(d =>
            {
                d.Decide = new StartDecide { State = StartDecideState.Deciding };
                d.Lines.Add(new StartLine
                {
                    Item = new ChangeItem { ObjectRef = "column:dim_staff/EMP_NM", ObjectName = "EMP_NM", Kind = "set_description", Target = "description", After = text, Source = "assistant", Status = "approved" },
                    DecidedBy = StartDecidedBy.Assistant, Word = text, Kept = true,
                });
            });
            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();
            await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "false" }, "human");
            await Engine.StartDecideSettledForTest();
            var before = Engine.StartPlanDocForTest();
            Assert.Single(before.Lines, l => l.Word == text);
            var modelPath = before.Destination.ModelPath;

            // A new process on the same state: the plan comes back from disk.
            using var sessions = new SessionManager();
            using var second = new LocalEngine(sessions, TestEntitlements.Pro, _f.Workspace);
            second.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            await second.OpenAsync(modelPath);
            second.UseDecisionsForTest(new DecisionService(new VerifyTextOracle()));
            await second.GetStartPlanAsync();
            await second.StartDecideSettledForTest();
            Assert.Single(second.StartPlanDocForTest().Lines, l => l.Word == text);
            await second.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "dim_staff", Value = "true" }, "human");
            await second.StartDecideSettledForTest();
            var back = second.StartPlanDocForTest();
            var w = back.Lines.SingleOrDefault(l => l.Word == text);
            Assert.True(w != null && w.Kept && w.DecidedBy == StartDecidedBy.Assistant, "the held words did not survive the restart: " + back.Decide.State + ": " + back.Decide.Reason);
        }
    }
}
