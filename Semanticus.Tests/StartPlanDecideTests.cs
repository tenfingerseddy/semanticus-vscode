// The start plan's decide loop over a REAL hidden scratch model (lane sf-decide, design-spec-first-start-v2.md section 2.3).
//
// WHAT IS PROVED, AND HOW.
//   The loop converges, a second decide sends nothing and changes nothing, turning a link off re-asks only what changed, a rename that
//   changes a pick gets its new measure decided and its words reopened and then stays stable, Free sends nothing, the model the build will
//   make asks nothing more, and an oracle that stops mid-way is Incomplete with its reason. Every claim is COUNTED off the oracle, never argued
//   from the code.
//
// THE ORACLE IS RECORDED, THEN REPLAYED. A policy oracle answers by object name (a price is averaged, a key adds up to nothing, a discount is
// unsure) and writes down every request digest and answer. The replay is a RecordedDecisionOracle over that fixture: it answers by the
// complete request digest only, and a digest it never saw is "not decided". So the second run proves the loop is deterministic (it sends no
// request the first run did not send) and, because the replay oracle names its served version, the service's cache works exactly as it does
// in production, which is what "a second decide sends 0 asks" depends on.
//
// THE SCRATCH IS BUILT BY A TEST HOST. The real one is sf-core's (a clone of the person's empty model, the manifest built into it, the kept lines applied by
// the shared executor). Here the host builds each scratch on a SECOND engine from the manifest's spec and applies the kept lines through the
// plan's own executor, which is the code the shared executor is extracted from.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StartPlanDecideTests
    {
        internal static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // ------------------------------------------------------------------ sources

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        /// <summary>A synthetic source: one fact with 75 number columns and three keys, three dimensions of four columns. 90 columns in all, and
        /// 75 base measures to decide (every ninth is a rate, every eleventh a discount Jev is unsure of).</summary>
        internal static FabricSchema Synthetic()
        {
            var metrics = Enumerable.Range(1, 75).Select(i =>
                C(i % 9 == 0 ? "Rate " + i.ToString("00") : i % 11 == 0 ? "Discount " + i.ToString("00") : "Metric " + i.ToString("00"), "Double"));
            var fact = new[] { C("CustomerKey", "Int64"), C("ProductKey", "Int64"), C("StoreKey", "Int64") }.Concat(metrics).ToArray();
            FabricTable Dim(string name, string key, params string[] more) => new FabricTable
            {
                Schema = "dbo", Name = name, KeyColumns = new[] { key },
                Columns = new[] { C(key, "Int64") }.Concat(more.Select(m => C(m, "String"))).ToArray(),
            };
            return new FabricSchema
            {
                Server = "synthetic.example.com", Database = "Synthetic",
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "Orders", KeyColumns = Array.Empty<string>(), Columns = fact },
                    Dim("Customer", "CustomerKey", "Name", "Region", "Segment"),
                    Dim("Product", "ProductKey", "Name", "Category", "Brand"),
                    Dim("Store", "StoreKey", "StoreName", "City", "Country"),
                },
                ForeignKeys = new[] { Fk("Orders", "CustomerKey", "Customer", "CustomerKey"), Fk("Orders", "ProductKey", "Product", "ProductKey"), Fk("Orders", "StoreKey", "Store", "StoreKey") },
            };
        }

        // ------------------------------------------------------------------ the manifest of the test (stands in for sf-manifest's Derive and ToSpec)

        internal static StartPlanDoc DocFor(FabricSchema schema, string server = "contoso-sql.example.com", string database = "ContosoConsulting")
        {
            var doc = new StartPlanDoc
            {
                Id = "plan-test", IdentitySeed = "test-seed", Schema = schema,
                // The manifest the start makes: the source's own draft, the rule's starter measures as its base measures (DecidedBy rule).
                Manifest = StructureManifest.Derive(schema, null, null, "test-seed", server, database, "import"),
                Source = new StartSource { Server = server, Database = database, StorageMode = "import" },
            };
            // The person has granted the start plan's disclosure for everything it includes (no ask goes without one).
            doc.Disclosure = StartPlanDecide.DisclosureFor(doc, false);
            return doc;
        }

        /// <summary>The spec the manifest builds: the manifest's own ToSpec, with its time intelligence (the scratch has what the build will make).</summary>
        internal static ModelSpec SpecOf(StartPlanDoc doc) => doc.Manifest.ToSpec();

        // ------------------------------------------------------------------ the scratch host of the test

        internal sealed class ScratchHost : IStartScratchHost
        {
            public int Builds;
            /// <summary>What the LAST scratch could not apply (an earlier round's refusals are left out of the plan by then).</summary>
            public List<string> NotApplied = new List<string>();

            public async Task<StartScratch> BuildAsync(StartPlanDoc doc, CancellationToken ct)
            {
                Interlocked.Increment(ref Builds);
                var home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                System.IO.Directory.CreateDirectory(home);
                var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, TestEntitlements.Pro, home);
                try
                {
                    // Named as the person's model is (the real scratch is a clone of it): a model-level question carries the name.
                    await engine.CreateModelAsync("Uplift", 1604);
                    await engine.SetSpecAsync(JsonSerializer.Serialize(SpecOf(doc), Camel), "human");
                    await engine.BuildModelFromSpecAsync("human");
                    var session = sessions.Current;
                    var lines = doc.Lines.Where(l => l.Kept).ToList();
                    var refused = new List<(StartLine, string)>();
                    var notApplied = new List<string>();
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
                            notApplied.Add(r.Kind + " " + r.ObjectRef + ": " + r.Note);
                            var mine = byItem.FirstOrDefault(b => b.Item.Id == r.Id).Line;
                            if (mine != null) refused.Add((mine, r.Note));
                        }
                    }
                    NotApplied = notApplied;
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

        // ------------------------------------------------------------------ the oracle

        /// <summary>Answers by what the object is called, and writes every request and answer down so the run can be replayed.</summary>
        internal sealed class PolicyOracle : IDecisionOracle
        {
            private readonly object _gate = new object();
            private readonly Dictionary<string, (string Point, string Answers)> _recorded = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            public int Asks;
            /// <summary>After this many requests every one is answered with a lost connection. Null never stops.</summary>
            public int? StopAfter;
            public Func<DecisionRequest, CancellationToken, Task> BeforeAnswer;
            /// <summary>Every question asked, by point, so a test can say what was decided about an object.</summary>
            public readonly List<(string Point, string Name)> Questions = new List<(string, string)>();
            /// <summary>Every request, in order, with the phase a test set when it was sent (so a test can say which request a later phase added).</summary>
            public readonly List<(int Phase, string Digest, string Point, string[] QuestionNames)> Requests = new List<(int, string, string, string[])>();
            public int Phase = 1;
            /// <summary>Every request's snapshot as the wire carries it, with its questions' names, in order: a test that finds a request it did not expect can say
            /// how it differs from the earlier request about the same object.</summary>
            public readonly List<(int Phase, string[] QuestionNames, string SnapshotJson)> Snapshots = new List<(int, string[], string)>();
            public bool Available => true;

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                int n;
                lock (_gate) n = ++Asks;
                if (BeforeAnswer != null) await BeforeAnswer(request, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    foreach (var q in request.Questions) Questions.Add((request.PointName, q.Name));
                    Requests.Add((Phase, request.Digest, request.PointName, request.Questions.Select(q => q.Name).ToArray()));
                    try { Snapshots.Add((Phase, request.Questions.Select(q => q.Name).ToArray(), JsonSerializer.Serialize(request.Snapshot))); } catch { }
                }
                if (StopAfter != null && n > StopAfter.Value) return DecisionAnswers.NotDecided(request, JevDecisionOracle.UnreachableReason);
                var body = new StringBuilder("{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var plain = q.Name.Contains(':') ? q.Name.Substring(0, q.Name.IndexOf(':')) : q.Name;
                    var objectRef = q.Name.Contains(':') ? q.Name.Substring(q.Name.IndexOf(':') + 1) : ObjectOf(request);
                    var (choice, confidence) = Choose(plain, objectRef, q);
                    if (choice == null) continue;
                    if (!first) body.Append(',');
                    first = false;
                    body.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                        .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                body.Append('}');
                lock (_gate) _recorded[request.Digest] = (request.PointName, body.ToString());
                return DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, "jev-test-1", 900, 40);
            }

            private static string ObjectOf(DecisionRequest r)
            {
                string Text(string k) => r.Snapshot.TryGetValue(k, out var v) ? v as string : null;
                if (Text("table_name") != null && Text("column_name") != null) return "column:" + Text("table_name") + "/" + Text("column_name");
                if (Text("table_name") != null && Text("measure_name") != null) return "measure:" + Text("table_name") + "/" + Text("measure_name");
                return Text("object_name") ?? "";
            }

            private static (string Choice, double Confidence) Choose(string question, string objectRef, DecisionQuestion q)
            {
                var name = objectRef.Substring(objectRef.LastIndexOf('/') + 1).ToLowerInvariant();
                string Pick(params string[] wanted) => wanted.FirstOrDefault(w => q.HasOption(w)) ?? q.Options[0].Id;
                switch (question)
                {
                    case "summarize_by":
                        if (Regex.IsMatch(name, @"(key|id|code|year)$")) return (Pick("none"), 0.95);
                        if (name.Contains("discount")) return (Pick("sum"), 0.5);
                        if (Regex.IsMatch(name, @"price|rate|pct|percent|score")) return (Pick("average"), 0.95);
                        return (Pick("sum"), 0.95);
                    case "format_family":
                        if (Regex.IsMatch(name, @"rate|pct|percent|margin")) return (Pick("percent"), 0.93);
                        if (Regex.IsMatch(name, @"qty|quantity|count|number|units|hrs")) return (Pick("whole_number"), 0.93);
                        if (Regex.IsMatch(name, @"amount|price|cost|revenue|total|sales|amt")) return (Pick("currency"), 0.93);
                        return (Pick("decimal"), 0.93);
                    case "display_folder": return (Pick(DecisionRegistry.NoFolder), 0.9);
                    case "ai_schema": return (Pick("include"), 0.93);
                    case "data_category": return (Pick("none"), 0.93);
                    default: return (Pick("none", "no"), 0.9);
                }
            }

            /// <summary>The run, written down in the recorded oracle's own fixture shape.</summary>
            public string Fixture()
            {
                var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
                lock (_gate)
                    sb.Append(string.Join(",", _recorded.Select(e => "{\"digest\":" + JsonSerializer.Serialize(e.Key) + ",\"point\":" + JsonSerializer.Serialize(e.Value.Point)
                        + ",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":" + e.Value.Answers + "}")));
                return sb.Append("]}").ToString();
            }
        }

        // ------------------------------------------------------------------ the rig

        internal sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public LocalEngine Engine => F.Engine;
            public ScratchHost Host = new ScratchHost();
            public StartPlanDoc Doc;
            public IDecisionOracle Oracle;
            public int Asks => Oracle is PolicyOracle p ? p.Asks : ((RecordedDecisionOracle)Oracle).AskedDigests.Count;
            public List<string> Digests => Oracle is RecordedDecisionOracle r ? r.AskedDigests.ToList() : new List<string>();

            public static async Task<Rig> CreateAsync(FabricSchema schema, IDecisionOracle oracle, bool pro = true)
            {
                var rig = new Rig { F = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free), Doc = DocFor(schema), Oracle = oracle };
                // The person's own empty model, saved so it has an identity permission can be kept for.
                await rig.F.Engine.CreateModelAsync("Uplift", 1604);
                await rig.F.SaveAsAsync("Uplift.bim");
                if (pro) await rig.F.ConsentAsync();
                rig.F.Engine.UseDecisionsForTest(new DecisionService(oracle));
                return rig;
            }

            public async Task<bool> DecideAsync(CancellationToken ct = default) => await Engine.DecideStartPlanAsync(Doc, Host, ct);
            public void Dispose() => F.Dispose();
        }

        /// <summary>Record a full decide with the policy oracle, then return the fixture and the state it reached. The doc is the recorded run's, so the
        /// replay can be compared to it.</summary>
        private static async Task<(string Fixture, StartPlanDoc Doc, int Asks)> RecordAsync(FabricSchema schema)
        {
            var policy = new PolicyOracle();
            using var rig = await Rig.CreateAsync(schema, policy);
            Assert.True(await rig.DecideAsync());
            return (policy.Fixture(), rig.Doc, policy.Asks);
        }

        private static string Replay(string fixture) => fixture;

        private static async Task<Rig> ReplayRigAsync(FabricSchema schema, string fixture, bool pro = true) =>
            await Rig.CreateAsync(schema, new RecordedDecisionOracle(fixture, "jev-test-1"), pro);

        // ================================================================== the tests

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_loop_converges_on_J8_and_the_replay_reproduces_it_with_no_request_the_recording_did_not_send()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, recorded, recordedAsks) = await RecordAsync(schema);
            Assert.True(recorded.Decide.State == StartDecideState.Stable, recorded.Decide.State + ": " + recorded.Decide.Reason);
            Assert.InRange(recorded.Decide.Round, 2, StartDecideLoopCap);
            Assert.True(recordedAsks > 0);
            Assert.NotEmpty(recorded.Lines);
            Assert.NotEmpty(recorded.Manifest.BaseMeasures);
            Assert.All(recorded.Manifest.BaseMeasures, b => Assert.Contains(b.DecidedBy, new[] { StartDecidedBy.Jev, StartDecidedBy.Rule }));
            // S4: Jev's pick is the base measure, and the family's own create line is never kept.
            Assert.DoesNotContain(recorded.Lines, l => l.Item.Kind == "create_measure");

            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);
            Assert.Equal(StartPlanDecide.Snapshot(recorded), StartPlanDecide.Snapshot(rig.Doc));
            Assert.True(rig.Host.NotApplied.Count == 0, string.Join("\n", rig.Host.NotApplied) + "\nLINES:\n"
                + string.Join("\n", rig.Doc.Lines.Where(l => rig.Host.NotApplied.Any(n => n.Contains(l.Item.ObjectRef))).Select(l => $"{l.Item.Kind} {l.Item.ObjectRef} {l.Item.Target}={l.Item.After} by {l.DecidedBy} rule {l.Item.RuleId} round {l.Round}")));   // every kept line landed on the scratch it was made for
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S4_replay_the_column_stops_adding_up_but_its_pick_stays_a_base_measure_and_keeps_its_time_intelligence()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (_, doc, _) = await RecordAsync(schema);

            // The family sets the COLUMN to None and expresses its pick as a separate measure. Folding the None into the spec would have deleted
            // Total INV AMT and every year-to-date over it; the pick is kept apart from the column's setting.
            var amount = doc.Manifest.BaseMeasures.Single(b => b.Table == "fact_invoice" && b.Column == "INV_AMT");
            Assert.Equal(("sum", StartDecidedBy.Jev), (amount.Pick, amount.DecidedBy));
            var none = doc.Lines.Single(l => l.Item.ObjectRef == "column:fact_invoice/INV_AMT" && l.Item.Kind == "set_summarize_by");
            Assert.Equal("None", none.Item.After);
            Assert.DoesNotContain(doc.Lines, l => l.Item.Kind == "create_measure");   // the manifest makes the measure, not a line

            var spec = doc.Manifest.ToSpec();
            Assert.Contains(spec.Measures, m => m.Name == amount.Name && m.Table == "fact_invoice");
            Assert.NotEmpty(spec.TimeIntelligence);                                    // derived from the base that has its own active date link
            Assert.Contains(amount.Name, spec.TimeIntelligenceBaseMeasures);
            // A pick that adds up to nothing (an average, a count) never gets time intelligence.
            Assert.DoesNotContain(doc.Manifest.BaseMeasures.Where(b => b.Pick != "sum").Select(b => b.Name), n => spec.TimeIntelligenceBaseMeasures.Contains(n));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_second_decide_sends_nothing_and_changes_nothing()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, _, _) = await RecordAsync(schema);
            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            var asks = rig.Asks;
            var runs = rig.Engine.JevRunsStartedForTest;
            var before = StartPlanDecide.Snapshot(rig.Doc);
            Assert.True(asks > 0);

            Assert.True(await rig.DecideAsync());

            Assert.Equal(asks, rig.Asks);   // 0 asks
            Assert.Equal(before, StartPlanDecide.Snapshot(rig.Doc));   // 0 changes
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);
            Assert.True(rig.Engine.JevRunsStartedForTest > runs);   // it did look; it had nothing to ask
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Turning_a_link_off_re_asks_only_the_snapshots_that_changed()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, _, _) = await RecordAsync(schema);
            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            var before = rig.Digests;
            var link = rig.Doc.Manifest.Links.First(l => l.Active);

            var edit = StartPlanEditing.Apply(rig.Doc, new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            Assert.True(edit.Ok, edit.Refused);
            Assert.True(await rig.DecideAsync());

            var after = rig.Digests;
            var added = after.Skip(before.Count).ToList();
            // Nothing already asked is asked again, and what is asked is a fraction of the whole: only the snapshots the link moved.
            Assert.Empty(added.Intersect(before));
            Assert.Equal(added.Count, added.Distinct().Count());
            Assert.True(added.Count * 4 <= before.Count, $"{added.Count} new asks after one link, against {before.Count} for the whole plan");
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rename_that_changes_a_pick_gets_the_new_measure_decided_reopens_its_words_and_then_stays_stable()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            // INV_AMT is an amount (summed); renamed to a name the policy averages, so its pick moves from sum to average.
            var rename = new StartPlanEdit { Op = "rename", Target = "column:fact_invoice/INV_AMT", Value = "Invoice Rate" };

            // Record both halves with the policy oracle (the rename is a second decide on the same recorded run).
            var policy = new PolicyOracle();
            string fixture;
            using (var rec = await Rig.CreateAsync(schema, policy))
            {
                Assert.True(await rec.DecideAsync());
                AssistantWritesWordsFor(rec.Doc, "fact_invoice", "INV_AMT");
                Assert.True(await rec.DecideAsync());
                Assert.True(StartPlanEditing.Apply(rec.Doc, rename, "human").Ok);
                Assert.True(await rec.DecideAsync());
                fixture = policy.Fixture();
            }

            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            var old = rig.Doc.Manifest.BaseMeasures.Single(b => b.Table == "fact_invoice" && b.Column == "INV_AMT");
            Assert.Equal("sum", old.Pick);
            var oldRef = StarterMeasuresFamily.MeasureRef(old.Table, old.Name);
            Assert.Contains(rig.Doc.Lines, l => l.Item.ObjectRef == oldRef);   // the old measure was decided: it has lines

            // The assistant has written the words for the old measure (its description, over the engine's own).
            AssistantWritesWordsFor(rig.Doc, "fact_invoice", "INV_AMT");
            Assert.True(await rig.DecideAsync());
            var wordsWritten = rig.Doc.Words.Total - rig.Doc.Words.Open;
            Assert.Equal(1, wordsWritten);

            var edit = StartPlanEditing.Apply(rig.Doc, rename, "human");
            Assert.True(edit.Ok, edit.Refused);
            Assert.True(await rig.DecideAsync());

            var bm = rig.Doc.Manifest.BaseMeasures.Single(b => b.Table == "fact_invoice" && b.Column == "Invoice Rate");
            Assert.Equal("average", bm.Pick);
            var newRef = StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name);
            Assert.NotEqual(oldRef, newRef);
            Assert.DoesNotContain(rig.Doc.Lines, l => l.Item.ObjectRef == oldRef);   // the old measure's lines went with it
            var mine = rig.Doc.Lines.Where(l => l.Item.ObjectRef == newRef).Select(l => l.Item.Kind).ToList();
            Assert.Contains("set_measure_format", mine);   // its format, decided
            // Its Copilot decision was ASKED. (Its folder is asked only where the model has a folder to choose from, and this source's measures have none.)
            Assert.Contains(policy.Questions, q => q.Point == DecisionRegistry.AiSchema && q.Name.Contains(newRef));
            // Its words are open again: the assistant's words were for the measure that is gone, and nothing carries them to the new one.
            Assert.Equal(0, rig.Doc.Words.Total - rig.Doc.Words.Open);
            Assert.DoesNotContain(rig.Doc.Lines, l => l.Word != null);
            Assert.Contains(rig.Doc.Lines, l => l.Item.ObjectRef == newRef && l.Item.Kind == "set_description" && l.Word == null);   // only the engine's own text is there
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);

            // And then it stays stable: another decide asks nothing and changes nothing.
            var asks = rig.Asks;
            var snapshot = StartPlanDecide.Snapshot(rig.Doc);
            var words = (rig.Doc.Words.Open, rig.Doc.Words.Total);
            Assert.True(await rig.DecideAsync());
            Assert.Equal(asks, rig.Asks);
            Assert.Equal(snapshot, StartPlanDecide.Snapshot(rig.Doc));
            Assert.Equal(words, (rig.Doc.Words.Open, rig.Doc.Words.Total));
        }

        /// <summary>What write_start_words does to the plan: the measure's description becomes the assistant's line, carrying its words.</summary>
        private static void AssistantWritesWordsFor(StartPlanDoc doc, string table, string column)
        {
            var bm = doc.Manifest.BaseMeasures.Single(b => b.Table == table && b.Column == column);
            var line = doc.Lines.Single(l => l.Item.ObjectRef == StarterMeasuresFamily.MeasureRef(bm.Table, bm.Name) && l.Item.Kind == "set_description");
            line.Item.After = "How much was invoiced, written by the assistant.";
            line.DecidedBy = StartDecidedBy.Assistant;
            line.Word = line.Item.After;
        }

        [Fact]
        public async Task Free_sends_nothing_not_even_to_count()
        {
            var policy = new PolicyOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), policy, pro: false);
            var starters = rig.Doc.Manifest.BaseMeasures.Count;
            Assert.True(await rig.DecideAsync());
            Assert.Equal(0, rig.Engine.JevRunsStartedForTest);
            Assert.Equal(0, policy.Asks);
            Assert.Equal(StartDecideState.Off, rig.Doc.Decide.State);
            Assert.Empty(rig.Doc.Lines);
            Assert.Equal(starters, rig.Doc.Manifest.BaseMeasures.Count);   // Free keeps the rule's starters: it gets its own assistant's words and one build
            Assert.Equal(0, rig.Host.Builds);   // not even a scratch was built
        }

        [Fact]
        public async Task Pro_without_permission_waits_and_sends_nothing()
        {
            var policy = new PolicyOracle();
            using var rig = await Rig.CreateAsync(SqlDraftBuildTests.J8Schema(), policy);
            await rig.Engine.WithdrawConsentAsync("human");
            Assert.True(await rig.DecideAsync());
            Assert.Equal(StartDecideState.WaitingGrant, rig.Doc.Decide.State);
            Assert.Equal(0, policy.Asks);
            Assert.Equal(0, rig.Engine.JevRunsStartedForTest);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_model_the_build_makes_asks_nothing_more_and_has_nothing_left_to_plan()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, _, _) = await RecordAsync(schema);
            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            var asks = rig.Asks;
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);

            await BuildLikeTheRealBuildAsync(rig);

            // Now the Plan's own Jev run on the built model.
            await rig.Engine.GetModelPlanAsync();
            await rig.Engine.JevSettledForTest();
            var view = await rig.Engine.GetModelPlanAsync();

            // 0 lines: nothing Jev proposes on the built model is anything the decide had not already shown. What the decide could not prove (a format it
            // only suggests, a pick it was unsure of) stayed a dropped line in the plan and in Needs you; the Plan offers the same unticked suggestion again
            // and nothing else. Nothing arrives ticked, and nothing arrives that the person never saw.
            var jevLines = (await rig.Engine.GetPlanAsync()).Items.Where(i => i.Source == "jev" && (i.Status == "approved" || i.Status == "proposed")).ToList();
            string Key(string kind, string objectRef, string target, string after) => kind + "|" + objectRef + "|" + target + "|" + after;
            var shown = new HashSet<string>(rig.Doc.Lines.Select(l => Key(l.Item.Kind, l.Item.ObjectRef, l.Item.Target, l.Item.After)), StringComparer.OrdinalIgnoreCase);
            var ticked = jevLines.Where(i => i.Status == "approved").ToList();
            Assert.True(ticked.Count == 0, "Jev lines that arrive ticked on the built model: " + string.Join(" | ", ticked.Select(l => l.Kind + " " + l.ObjectRef + "=" + l.After)));
            var unseen = jevLines.Where(i => !shown.Contains(Key(i.Kind, i.ObjectRef, i.Target, i.After))).ToList();
            Assert.True(unseen.Count == 0, "Jev lines on the built model the decide never showed: " + string.Join(" | ", unseen.Select(l => l.Kind + " " + l.ObjectRef + " " + l.Target + "=" + l.After + " [" + l.Status + "]")));
            Assert.All(jevLines, i => Assert.True(i.Suggestion, "only an unproven suggestion may be left: " + i.Kind + " " + i.ObjectRef));
            Assert.DoesNotContain(view.Items, i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held);   // no Ready Semanticus row
            // 0 new asks from the families is proved by name in the next test, which sees every request. (Here the Plan's own ranking of its rows,
            // plan.rank, is one more request to this oracle, so the oracle's plain count is not the family count.)
            Assert.True(rig.Asks - asks <= 1, "the Plan sent " + (rig.Asks - asks) + " requests; only its own plan.rank may be new");
        }

        /// <summary>The same promise as above, said by which REQUESTS exist: the Plan's own run on the built model sends no request the decide did not
        /// send. Recorded (no cache, so every request of both phases is seen), so a failure names the request that is new.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_request_the_Plan_makes_on_the_built_model_is_one_the_decide_already_sent()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var policy = new PolicyOracle();
            using var rig = await Rig.CreateAsync(schema, policy);
            Assert.True(await rig.DecideAsync());
            policy.Phase = 2;

            await BuildLikeTheRealBuildAsync(rig);
            await rig.Engine.GetModelPlanAsync();
            await rig.Engine.JevSettledForTest();
            await rig.Engine.GetModelPlanAsync();

            var decided = new HashSet<string>(policy.Requests.Where(r => r.Phase == 1).Select(r => r.Digest), StringComparer.Ordinal);
            // The Plan's own ranking of its rows (plan.rank) is not a family and not a decision about the model: it orders what is on the page, and
            // there is something on the page (the words row, the Save row). Every FAMILY request is a decided one.
            var added = policy.Requests.Where(r => r.Phase == 2 && r.Point != DecisionRegistry.PlanRank && !decided.Contains(r.Digest)).ToList();
            Assert.True(added.Count == 0, "requests the Plan made that the decide did not: "
                + string.Join(" | ", added.Select(r => r.Point + " " + string.Join(",", r.QuestionNames))));
        }

        /// <summary>THE BUILD, as the real one makes it: the person's own model gets the manifest's spec, then the kept lines, through the plan's executor.</summary>
        internal static async Task BuildLikeTheRealBuildAsync(Rig rig)
        {
            await rig.Engine.SetSpecAsync(JsonSerializer.Serialize(SpecOf(rig.Doc), Camel), "human");
            await rig.Engine.BuildModelFromSpecAsync("human");
            var lines = rig.Doc.Lines.Where(l => l.Kept).ToList();
            var sessions = rig.F.Sessions;
            var plan = sessions.CurrentContext.Plans.GetOrStart();
            foreach (var l in lines) plan.Add(StartPlanDecide.ToChangeItem(l), sessions.Current.Revision, "");
            var report = await rig.Engine.ApplyPlanAsync(null, "human");
            Assert.Empty(report.Items.Where(i => i.Status != "applied"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_oracle_that_stops_part_way_leaves_the_plan_Incomplete_and_says_why()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var policy = new PolicyOracle { StopAfter = 3 };
            using var rig = await Rig.CreateAsync(schema, policy);

            Assert.True(await rig.DecideAsync());

            Assert.Equal(StartDecideState.Incomplete, rig.Doc.Decide.State);
            // Kane, 8 Oct (r1 join): a stop nobody can act on reads as the one calm sentence, as on the Plan and the Ask.
            Assert.Equal("Jev is not available right now.", rig.Doc.Decide.Reason);
            Assert.True(rig.Doc.Decide.Progress.Any(p => p.Unusable > 0 || p.Remaining > 0));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_decide_that_never_settles_stops_at_the_cap_and_says_so()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, _, _) = await RecordAsync(schema);
            using var rig = await ReplayRigAsync(schema, fixture);
            // One round is not enough to settle a plan that has things to decide: the cap is reached, and the state is not "stable".
            Assert.True(await rig.Engine.DecideStartPlanAsync(rig.Doc, rig.Host, CancellationToken.None, maxRounds: 1));
            Assert.Equal(StartDecideState.Incomplete, rig.Doc.Decide.State);
            Assert.Equal(StartDecideCopy.RoundCapReason, rig.Doc.Decide.Reason);
            Assert.Equal(1, rig.Doc.Decide.Round);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_edit_cancels_the_decide_in_flight_and_a_new_one_settles()
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var (fixture, _, _) = await RecordAsync(schema);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var policy = new PolicyOracle
            {
                BeforeAnswer = async (request, ct) => { reached.TrySetResult(true); await gate.Task.WaitAsync(ct).ConfigureAwait(false); },
            };
            using var rig = await Rig.CreateAsync(schema, policy);

            await rig.Engine.KickStartDecideAsync(rig.Doc, rig.Host);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(StartDecideState.Deciding, rig.Doc.Decide.State);
            var linesAtCancel = rig.Doc.Lines.Count;

            await rig.Engine.CancelStartDecideAsync();   // what an edit does first

            Assert.Equal(linesAtCancel, rig.Doc.Lines.Count);   // a cancelled round changes nothing
            Assert.Equal(0, rig.Doc.Lines.Count);
            Assert.Equal(StartDecideState.Deciding, rig.Doc.Decide.State);   // not "stable", not "incomplete": the edit that cancelled it starts the next one
            gate.SetResult(true);
            policy.BeforeAnswer = null;
            await (await rig.Engine.KickStartDecideAsync(rig.Doc, rig.Host));
            Assert.Equal(StartDecideState.Stable, rig.Doc.Decide.State);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task On_a_source_with_75_measures_and_90_columns_every_one_is_decided_and_the_plan_settles()
        {
            var schema = Synthetic();
            Assert.Equal(90, schema.Tables.Sum(t => t.Columns.Length));
            var (fixture, recorded, _) = await RecordAsync(schema);
            Assert.True(recorded.Decide.State == StartDecideState.Stable, recorded.Decide.State + ": " + recorded.Decide.Reason + " round " + recorded.Decide.Round
                        + " progress " + string.Join(", ", recorded.Decide.Progress.Select(p => p.Family + " unusable " + p.Unusable + " remaining " + p.Remaining)));

            using var rig = await ReplayRigAsync(schema, fixture);
            Assert.True(await rig.DecideAsync());
            Assert.Equal(StartPlanDecide.Snapshot(recorded), StartPlanDecide.Snapshot(rig.Doc));
            // 75 numbers, minus the ones Jev says add up to nothing: every one that is left has its measure, and each measure its format and folder.
            var measures = rig.Doc.Manifest.BaseMeasures;
            Assert.Equal(75, measures.Count);
            Assert.Equal(75, measures.Select(b => StarterMeasuresFamily.MeasureRef(b.Table, b.Name)).Distinct().Count());
            foreach (var b in measures)
            {
                var r = StarterMeasuresFamily.MeasureRef(b.Table, b.Name);
                Assert.Contains(rig.Doc.Lines, l => l.Item.ObjectRef == r && l.Item.Kind == "set_measure_format");
            }
            // The unsure ones are the rule's, said in Needs you.
            var unsure = measures.Where(b => b.Column.StartsWith("Discount")).ToList();
            Assert.NotEmpty(unsure);
            Assert.All(unsure, b => Assert.Equal(StartDecidedBy.Rule, b.DecidedBy));
            Assert.Equal(unsure.Count, StartPlanDecide.NeedsYou(rig.Doc).Count(n => n.Kind == StartNeedKind.UnsurePick));
        }

        private const int StartDecideLoopCap = LocalEngine.StartDecideMaxRounds;
    }

    /// <summary>edit_start_plan as the engine verb both doors call: persisted, Needs followed, knock-ons said, the approval cleared, the loop in
    /// flight stopped and the next one started.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanDecideVerbTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly DecisionFixture _f;

        public StartPlanDecideVerbTests()
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        private async Task<StartPlanView> StartAsync() =>
            await Engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "contoso-sql.example.com", Database = "ContosoConsulting", Name = "Uplift",
                Folder = System.IO.Path.Combine(_root, "dest"),
            }, "human");

        /// <summary>A plan with one base measure and its followers, as the loop leaves it, and an approval on its digest.</summary>
        private async Task<StartPlanView> StartWithMeasureAsync()
        {
            await StartAsync();
            Engine.StartPlanDocForTestMutate(doc =>
            {
                var table = doc.Manifest.Tables.First(t => t.Columns.Any(c => c.Name == "INV_AMT"));
                var bm = new ManifestBaseMeasure { Table = table.Name, Column = "INV_AMT", Pick = "sum", Name = "Total INV AMT", DecidedBy = StartDecidedBy.Jev, Identity = "bm-1" };
                doc.Manifest.BaseMeasures = new List<ManifestBaseMeasure> { bm };
                var need = JevFamily.NeedsKey("create_measure", "measure:" + table.Name + "/Total INV AMT");
                foreach (var (kind, target, after) in new[] { ("set_measure_format", "formatString", "#,0"), ("set_description", "description", "The total of INV_AMT.") })
                    doc.Lines.Add(StartPlanDecide.ToStartLine(new ChangeItem
                    {
                        ObjectRef = "measure:" + table.Name + "/Total INV AMT", ObjectName = "Total INV AMT", Kind = kind, Target = target, After = after, Source = "jev",
                        JevItem = "jev.column-summarize.table:" + table.Name, DecisionPoint = DecisionRegistry.ColumnSummarize, Status = "approved", Needs = need, Title = kind,
                    }, StartDecidedBy.Jev, 1));
                doc.Decide.State = StartDecideState.Stable;
            });
            var view = await Engine.GetStartPlanAsync();
            Engine.StartPlanDocForTestMutate(doc => doc.Approval = new StartApproval { Digest = view.Digest, Utc = DateTime.UtcNow });
            return await Engine.GetStartPlanAsync();
        }

        [Fact]
        public async Task An_edit_through_the_verb_follows_needs_says_why_clears_the_approval_and_is_kept()
        {
            var before = await StartWithMeasureAsync();
            Assert.NotNull(before.Approval);
            var table = Engine.StartPlanDocForTest().Manifest.BaseMeasures.Single().Table;

            var after = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = "measure:" + table + "/Total INV AMT" }, "human");

            Assert.NotEqual(before.Digest, after.Digest);
            Assert.Null(after.Approval);   // any edit voids the approval
            Assert.False(after.CanBuild);
            var doc = Engine.StartPlanDocForTest();
            Assert.Equal("none", doc.Manifest.BaseMeasures.Single().Pick);
            Assert.All(doc.Lines, l => { Assert.False(l.Kept); Assert.Equal(StartDecidedBy.Person, l.DecidedBy); Assert.Contains("you dropped it", l.Why); });
            // And it is on disk: a restart reads the same plan.
            Assert.Equal("none", StartPlanStore.Read(doc.Id).Manifest.BaseMeasures.Single().Pick);

            // Keeping one follower brings the measure back, through the same verb and on the assistant's door.
            var kept = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "keep_line", Target = "set_description|measure:" + table + "/Total INV AMT|description" }, "agent");
            var back = Engine.StartPlanDocForTest();
            Assert.Equal("sum", back.Manifest.BaseMeasures.Single().Pick);
            Assert.All(back.Lines, l => Assert.True(l.Kept));
            Assert.Equal(StartDecidedBy.Assistant, back.Lines.First(l => l.Item.Kind == "set_description").DecidedBy);
        }

        [Fact]
        public async Task A_refused_edit_says_why_in_plain_words_and_changes_nothing()
        {
            var before = await StartWithMeasureAsync();
            var docBefore = Engine.StartPlanDocForTest();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Engine.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "table:" + docBefore.Manifest.Tables[0].Name, Value = docBefore.Manifest.Tables[1].Name }, "human"));
            Assert.Contains("already called", ex.Message);
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_pick", Target = "nope/nope", Value = "sum" }, "human"));
            Assert.Contains("No column is called", ex2.Message);

            var after = await Engine.GetStartPlanAsync();
            Assert.Equal(before.Digest, after.Digest);
            Assert.NotNull(after.Approval);   // nothing changed, so the approval stands
            Assert.Equal(docBefore.Version, Engine.StartPlanDocForTest().Version);
        }

        private sealed class StallingHost : IStartScratchHost
        {
            public int Builds;
            public readonly TaskCompletionSource<bool> Reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<StartScratch> BuildAsync(StartPlanDoc doc, CancellationToken ct)
            {
                var n = Interlocked.Increment(ref Builds);
                if (n == 1) { Reached.TrySetResult(true); await Gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                throw new InvalidOperationException("the test host stops here");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_loop_runs_end_to_end_over_the_real_scratch_and_never_touches_the_persons_model()
        {
            await StartAsync();
            await _f.ConsentAsync();
            var policy = new StartPlanDecideTests.PolicyOracle();
            _f.Engine.UseDecisionsForTest(new DecisionService(policy));
            var session = _f.Sessions.Current;
            var revision = session.Revision;
            var unsavedBefore = session.HasUnsavedChanges;   // giving permission wrote the marker into the model: that is the person's, not the loop's
            var busEvents = 0;
            _f.Sessions.Bus.Changed += _ => Interlocked.Increment(ref busEvents);
            Engine.StartPlanDocForTestMutate(doc => doc.Decide = new StartDecide { State = StartDecideState.Deciding });

            await Engine.KickStartDecideOnPlanAsync();
            await Engine.StartDecideSettledForTest();

            var doc = Engine.StartPlanDocForTest();
            Assert.True(doc.Decide.State == StartDecideState.Stable, doc.Decide.State + ": " + doc.Decide.Reason);
            Assert.NotEmpty(doc.Lines);
            Assert.NotEmpty(doc.Manifest.BaseMeasures);
            Assert.True(policy.Asks > 0);
            // The person's empty model is exactly as it was: no edit, no event, no unsaved change beyond the permission marker, no table.
            Assert.Equal(revision, session.Revision);
            Assert.Equal(0, Volatile.Read(ref busEvents));
            Assert.Equal(unsavedBefore, session.HasUnsavedChanges);
            Assert.Equal(0, await session.ReadAsync(m => m.Tables.Count));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_edit_stops_the_loop_in_flight_applies_and_starts_the_next_one()
        {
            await StartWithMeasureAsync();
            await _f.ConsentAsync();
            _f.Engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            var host = new StallingHost();
            Engine.StartScratchHostForTest = host;
            Engine.StartPlanDocForTestMutate(doc => doc.Decide = new StartDecide { State = StartDecideState.Deciding, Round = 1 });

            await Engine.KickStartDecideOnPlanAsync();
            await host.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(1, host.Builds);

            var link = Engine.StartPlanDocForTest().Manifest.Links.First(l => l.Active);
            var view = await Engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_link", Target = link.Identity, Value = "off" }, "human");
            await Engine.StartDecideSettledForTest();

            Assert.Equal(2, host.Builds);   // the first loop was stopped, a second began
            var doc = Engine.StartPlanDocForTest();
            Assert.False(doc.Manifest.Links.Single(l => l.Identity == link.Identity).Active);   // the edit landed
            Assert.Null(doc.Approval);
            // The second loop failed (the host says so) and the plan says that, never "stable".
            Assert.Equal(StartDecideState.Incomplete, doc.Decide.State);
            Assert.Contains("the test host stops here", doc.Decide.Reason);
            Assert.NotEqual(StartDecideState.Stable, view.Decide.State);
        }
    }
}
