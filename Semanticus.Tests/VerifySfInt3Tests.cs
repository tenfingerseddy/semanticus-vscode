// Verifier, re-check of m11/sf-int fix round 3 (1d7078d5): consent for the sample of rows (D1) and a person's keep reaching the build (D2).
//
// D1. Facts worked out from a sample of rows reach Jev only under a stored grant that covered them. Attacked here on the paths the fix's own tests do not
// walk: the assistant's own get_model_facts asked first (so its snapshot, which carries the sample, is the newest one), the model's permission withdrawn and
// given again (the old grant must end), a grant that did not cover the sample, and a second start on the same source. Every arm that must withhold also
// proves Jev WAS asked, and the paths where the person allowed it prove the facts DO flow, so a gate that shuts everything cannot pass.
//
// D2. Every way a person accepts an unsure format lands in the build on either door; an assistant's pin of one does not; and no line says it was kept by the
// person while the build left it out. The dump goes to the file named by SEMANTICUS_VSFI3_DUMP when it is set.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifySfInt3Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly StringBuilder _dump = new StringBuilder();

        public VerifySfInt3Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "v3-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
        }

        public void Dispose()
        {
            var path = Environment.GetEnvironmentVariable("SEMANTICUS_VSFI3_DUMP");
            if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, _dump.ToString());
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Say(string line) => _dump.AppendLine(line);

        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";

        /// <summary>Anything measured in a request. The built model has no connection, so every measured fact in it can only come from the sample of rows:
        /// a column's measured block (blankShare and its siblings are sent only when the column was measured), a table's row count, or the sampled flag.</summary>
        private static readonly Regex SampleDerived = new Regex(
            "\"(blank_share|blankShare|distinct_share|wholeNumbers|whole_numbers|uniqueOnEveryRow|unique_on_every_row)\"\\s*:|\"(table_rows|row_count)\"\\s*:\\s*[0-9]|\"sampled\"\\s*:\\s*true",
            RegexOptions.Compiled);

        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
        }

        private (LocalEngine Engine, SessionManager Sessions, StartPlanDecideTests.PolicyOracle Oracle) NewEngine(bool pro, string workspace)
        {
            AgentPolicyStore.SetPreset("open", "human");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, workspace);
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            var oracle = new StartPlanDecideTests.PolicyOracle();
            engine.UseDecisionsForTest(new DecisionService(oracle));
            return (engine, sessions, oracle);
        }

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        private static string NewName(string r)
        {
            var raw = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var h = Humanise(r);
            return string.Equals(h, raw, StringComparison.OrdinalIgnoreCase) ? h + " Value" : h;
        }

        private static StartWord[] WordsFor(StartWordsPage page) => page.Items.Select(i => new StartWord
        {
            Ref = i.Ref, Kind = i.Kind,
            Text = i.Kind == "synonyms" ? "alias one, alias two"
                 : i.Kind == "display_name" ? NewName(i.Ref)
                 : i.Kind == "instructions" ? "This model answers questions about consulting work: hours, projects, clients and invoices."
                 : "What " + Humanise(i.Ref) + " means for the business, in plain words.",
        }).ToArray();

        private static async Task WordsAsync(LocalEngine engine)
        {
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await engine.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                await engine.WriteStartWordsAsync(WordsFor(page), "agent");
            }
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
        }

        public enum Answer { Allow, NotNow }

        /// <summary>A Pro start on the J8 schema to a built, saved model. The person gives the model's permission and then Allow or Not now on the source card.</summary>
        private async Task<(StartPlanView View, string ModelPath)> BuildAsync(LocalEngine engine, Answer answer, string folder = null)
        {
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = folder ?? Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5)), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            Assert.True(view.SampleFacts, "no sample was read, so this proves nothing");
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = answer == Answer.Allow
                ? await engine.GrantStartDisclosureAsync(view.Digest, "human")
                : await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "grant", Value = "Not now" }, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);
            view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var modelPath = engine.StartPlanDocForTest().Destination.ModelPath;
            await engine.SaveAsync(null, "tmdl", false, "human");
            return (view, modelPath);
        }

        private static async Task RunPlanAsync(LocalEngine engine, StartPlanDecideTests.PolicyOracle oracle)
        {
            oracle.Phase = 2;
            await engine.GetModelPlanAsync();
            await engine.JevSettledForTest();
            await engine.GetModelPlanAsync();
        }

        private static int Sampled(StartPlanDecideTests.PolicyOracle o, int from = 0) => o.Snapshots.Skip(from).Count(x => SampleDerived.IsMatch(x.SnapshotJson ?? ""));

        private static async Task<string> FirstTableWithMeasuresAsync(SessionManager sessions) =>
            await sessions.Current.ReadAsync(m => m.Tables.First(t => t.Measures.Any()).Name);

        // ================================================================== D1: the assistant's own facts read first

        /// <summary>The assistant asks get_model_facts right after the build, before the Plan's first run, and again after an edit, so the snapshot that carries
        /// the sample is the newest one for each revision. The Plan's runs must still send nothing from the sample after Not now, and must send it after Allow.</summary>
        [Theory]
        [InlineData(Answer.NotNow)]
        [InlineData(Answer.Allow)]
        public async Task Verify3_the_assistants_own_get_model_facts_never_carries_the_sample_into_a_Jev_request(Answer answer)
        {
            var (engine, sessions, oracle) = NewEngine(true, Path.Combine(_root, "ws-a"));
            await BuildAsync(engine, answer);
            var before = oracle.Asks;
            var mine = await McpTools.GetModelFacts(engine);
            Say($"D1 get_model_facts ({answer}): assistant's view data={mine?.Data}");
            await RunPlanAsync(engine, oracle);
            var firstAsks = oracle.Asks - before;

            var table = await FirstTableWithMeasuresAsync(sessions);
            await engine.CreateMeasureAsync("table:" + table, "Verify Probe Hours", "1", "agent");
            await McpTools.GetModelFacts(engine);
            await RunPlanAsync(engine, oracle);
            var jevView = engine.TryGetJevAskFacts();
            Say($"D1 get_model_facts ({answer}): asks after the build {oracle.Asks - before} (first run {firstAsks}); requests with sample facts {Sampled(oracle)}; Jev's snapshot now {jevView?.Data}");
            Assert.True(firstAsks > 0, "the Plan asked Jev nothing, so this proves nothing");
            if (answer == Answer.Allow)
                Assert.True(Sampled(oracle) > 0, "the person allowed the sample and no request carried it: the gate shut everything");
            else
            {
                Assert.True(Sampled(oracle) == 0, Sampled(oracle) + " requests carried sample facts after Not now, once the assistant had read the facts itself");
                Assert.NotEqual(Semanticus.Engine.Facts.ModelFactsData.SourceSample, jevView?.Data ?? Semanticus.Engine.Facts.ModelFactsData.None);
            }
        }

        // ================================================================== D1: the model's permission withdrawn and given again

        /// <summary>Allow, build, the Plan runs with the sample (control). The person withdraws the model's permission, then gives it again. The source grant was
        /// for the old permission and is over: the next runs, on this engine after an edit and on a fresh engine after a restart, send nothing from the sample.</summary>
        [Fact]
        public async Task Verify3_withdrawing_the_models_permission_ends_the_samples_grant_even_once_it_is_given_again()
        {
            var ws = Path.Combine(_root, "ws-w");
            var (engine, sessions, oracle) = NewEngine(true, ws);
            var (_, modelPath) = await BuildAsync(engine, Answer.Allow);
            await RunPlanAsync(engine, oracle);
            var control = Sampled(oracle);
            Assert.True(control > 0, "the person allowed the sample and no request carried it, so the rest proves nothing");

            await engine.WithdrawConsentAsync("human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var mark = oracle.Snapshots.Count;
            var asksMark = oracle.Asks;
            var table = await FirstTableWithMeasuresAsync(sessions);
            await engine.CreateMeasureAsync("table:" + table, "Verify Probe Hours", "1", "agent");
            await RunPlanAsync(engine, oracle);
            var jevView = engine.TryGetJevAskFacts();
            Say($"D1 withdraw: control {control}; same engine after give-again: asks {oracle.Asks - asksMark}, with sample {Sampled(oracle, mark)}; Jev's snapshot {jevView?.Data}");
            Assert.True(Sampled(oracle, mark) == 0, Sampled(oracle, mark) + " requests carried sample facts after the permission was withdrawn and given again");
            Assert.NotEqual(Semanticus.Engine.Facts.ModelFactsData.SourceSample, jevView?.Data ?? Semanticus.Engine.Facts.ModelFactsData.None);
            await engine.SaveAsync(null, "tmdl", false, "human");
            engine.Dispose(); sessions.Dispose();

            var (second, secondSessions, oracle2) = NewEngine(true, ws);
            await second.OpenAsync(modelPath);
            await RunPlanAsync(second, oracle2);
            Say($"D1 withdraw, after a restart: asks {oracle2.Asks}, with sample {Sampled(oracle2)}");
            Assert.True(oracle2.Asks > 0, "Jev was never asked after the restart, so this proves nothing");
            Assert.True(Sampled(oracle2) == 0, Sampled(oracle2) + " requests carried sample facts after a restart, on a grant the withdrawal ended");
        }

        // ================================================================== D1: a grant that did not cover the sample

        /// <summary>The stored grant for this plan names the tables but not the sample (the record is rewritten to say so, as the gate's own test does, because a
        /// person cannot give one while a sample is on). This engine had already seen it covered; the next run after an edit, and a fresh engine, send nothing.</summary>
        [Fact]
        public async Task Verify3_a_grant_that_did_not_cover_the_sample_sends_nothing_from_it_even_on_an_engine_that_saw_it_covered()
        {
            var ws = Path.Combine(_root, "ws-g");
            var (engine, sessions, oracle) = NewEngine(true, ws);
            var (_, modelPath) = await BuildAsync(engine, Answer.Allow);
            await RunPlanAsync(engine, oracle);
            Assert.True(Sampled(oracle) > 0, "the control sent no sample facts");

            var planId = engine.LoadSourceFacts(sessions.Current).PlanId;
            var scope = await engine.DecisionConsentScopeAsync();
            var consent = engine.Decisions.ReadConsent(scope);
            var granted = StartDisclosureStore.Read(scope, planId, consent.Generation);
            StartDisclosureStore.Grant(scope, planId, new DisclosedScope(granted.SourceKey, granted.IncludedTables, false), consent.Generation, DateTime.UtcNow);

            var mark = oracle.Snapshots.Count;
            var table = await FirstTableWithMeasuresAsync(sessions);
            await engine.CreateMeasureAsync("table:" + table, "Verify Probe Hours", "1", "agent");
            await RunPlanAsync(engine, oracle);
            var jevView = engine.TryGetJevAskFacts();
            Say($"D1 no-sample grant, same engine: with sample {Sampled(oracle, mark)}; Jev's snapshot {jevView?.Data}");
            Assert.True(Sampled(oracle, mark) == 0, Sampled(oracle, mark) + " requests carried sample facts under a grant that did not name the sample");
            Assert.NotEqual(Semanticus.Engine.Facts.ModelFactsData.SourceSample, jevView?.Data ?? Semanticus.Engine.Facts.ModelFactsData.None);
            await engine.SaveAsync(null, "tmdl", false, "human");
            engine.Dispose(); sessions.Dispose();

            var (second, _, oracle2) = NewEngine(true, ws);
            await second.OpenAsync(modelPath);
            await RunPlanAsync(second, oracle2);
            Say($"D1 no-sample grant, after a restart: asks {oracle2.Asks}, with sample {Sampled(oracle2)}");
            Assert.True(oracle2.Asks > 0, "Jev was never asked after the restart, so this proves nothing");
            Assert.True(Sampled(oracle2) == 0, Sampled(oracle2) + " requests carried sample facts after a restart, under a grant that did not name the sample");
        }

        // ================================================================== D1: a second start on the same source

        /// <summary>Two starts on the same source, one engine, two models. One the person allowed, one they said Not now to. Each model's Plan sends the sample
        /// only for the model whose start the person allowed, in either order, and opening the other model again does not carry the grant across.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Verify3_a_second_start_on_the_same_source_keeps_each_models_answer_about_the_sample(bool allowFirst)
        {
            var (engine, sessions, oracle) = NewEngine(true, Path.Combine(_root, "ws-2"));
            var (_, first) = await BuildAsync(engine, allowFirst ? Answer.Allow : Answer.NotNow);
            var (_, second) = await BuildAsync(engine, allowFirst ? Answer.NotNow : Answer.Allow);
            var mark = oracle.Snapshots.Count;
            var asks = oracle.Asks;
            await RunPlanAsync(engine, oracle);
            var onSecond = (Asks: oracle.Asks - asks, Sample: Sampled(oracle, mark));

            await engine.OpenAsync(first);
            mark = oracle.Snapshots.Count; asks = oracle.Asks;
            await RunPlanAsync(engine, oracle);
            var onFirst = (Asks: oracle.Asks - asks, Sample: Sampled(oracle, mark));
            Say($"D1 second start (allow first {allowFirst}): second model asks {onSecond.Asks} sample {onSecond.Sample}; first model reopened asks {onFirst.Asks} sample {onFirst.Sample}");

            var (allowed, declined) = allowFirst ? (onFirst, onSecond) : (onSecond, onFirst);
            Assert.True(declined.Asks > 0, "the model the person said Not now for was never asked about, so this proves nothing");
            Assert.True(declined.Sample == 0, declined.Sample + " requests carried sample facts on the model whose start the person said Not now to");
            Assert.True(allowed.Asks == 0 || allowed.Sample > 0, "the model the person allowed was asked about without its sample: the gate shut everything");
        }

        // ================================================================== D2: every person accept path, built on either door

        public enum Path2 { Answer, KeepLine, Pin, DropThenKeep }

        private async Task<(LocalEngine Engine, SessionManager Sessions, StartNeed Need, StartLine Line)> UnsureFormatAsync()
        {
            var (engine, sessions, _) = NewEngine(true, Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N").Substring(0, 5)));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 5)), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            view = await engine.GetStartPlanAsync();
            var fy = view.NeedsYou.SingleOrDefault(x => x.Kind == "fiscal_year");
            if (fy != null) view = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = "fiscal_year", Value = fy.Recommended }, "human");
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await engine.GrantStartDisclosureAsync(view.Digest, "human");
            await engine.StartDecideSettledForTest();
            await WordsAsync(engine);
            view = await engine.GetStartPlanAsync();
            var need = view.NeedsYou.FirstOrDefault(n => n.Kind == "unsure_line" && n.Text.StartsWith("Format as"));
            Assert.True(need != null, "no unsure format line to answer: " + string.Join(" | ", view.NeedsYou.Select(n => n.Kind)));
            return (engine, sessions, need, LineOf(engine, need.Id.Substring(need.Id.IndexOf(':') + 1)));
        }

        private static StartLine LineOf(LocalEngine engine, string key) =>
            engine.StartPlanDocForTest().Lines.SingleOrDefault(l => string.Equals(StartPlanDecide.LineKey(l), key, StringComparison.OrdinalIgnoreCase));

        private static async Task<string> FormatOfAsync(SessionManager sessions, string objectRef) =>
            await sessions.Current.ReadAsync(m =>
                m.AllMeasures.Where(x => string.Equals(ObjectRefs.For(x), objectRef, StringComparison.OrdinalIgnoreCase)).Select(x => x.FormatString)
                 .Concat(m.AllColumns.Where(c => string.Equals(ObjectRefs.For(c), objectRef, StringComparison.OrdinalIgnoreCase)).Select(c => c.FormatString))
                 .FirstOrDefault() ?? "(object not found)");

        private static async Task SettleAsync(LocalEngine engine)
        {
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
        }

        [Theory]
        [InlineData(Path2.Answer, false)] [InlineData(Path2.Answer, true)]
        [InlineData(Path2.KeepLine, false)] [InlineData(Path2.KeepLine, true)]
        [InlineData(Path2.Pin, false)] [InlineData(Path2.Pin, true)]
        [InlineData(Path2.DropThenKeep, false)] [InlineData(Path2.DropThenKeep, true)]
        public async Task Verify3_every_way_a_person_accepts_an_unsure_format_is_built_on_either_door(Path2 path, bool assistantBuilds)
        {
            var (engine, sessions, need, line) = await UnsureFormatAsync();
            var key = StartPlanDecide.LineKey(line);
            var expect = line.Item.After;
            switch (path)
            {
                case Path2.Answer:
                    await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human"); break;
                case Path2.KeepLine:
                    await engine.EditStartPlanAsync(new StartPlanEdit { Op = "keep_line", Target = key }, "human"); break;
                case Path2.Pin:
                    expect = "#,##0.00";
                    await engine.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = key, Value = expect }, "human"); break;
                case Path2.DropThenKeep:
                    await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = key }, "human");
                    await SettleAsync(engine);
                    await engine.EditStartPlanAsync(new StartPlanEdit { Op = "keep_line", Target = key }, "human"); break;
            }
            await SettleAsync(engine);
            var kept = LineOf(engine, key);
            Assert.True(kept != null && kept.Kept && kept.DecidedBy == StartDecidedBy.Person, "the person's accept did not land on the line: " + kept?.Why);
            var view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = assistantBuilds ? await McpTools.BuildStartPlan(engine, view.Digest) : await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var fmt = await FormatOfAsync(sessions, kept.Item.ObjectRef);
            Say($"D2 {path} built by {(assistantBuilds ? "assistant" : "person")}: {kept.Item.ObjectRef} '{fmt}' expected '{expect}', why [{kept.Why}] held [{kept.Held}]");
            Assert.Equal(expect, fmt);
        }

        /// <summary>An assistant's pin of an unsure format (its own, and over the person's keep) is not a person's tick: the build leaves the format out, holds the
        /// line with a reason, and the line never says the person kept or pinned it.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Verify3_an_assistants_pin_of_an_unsure_format_is_never_built_and_never_reads_as_yours(bool personKeptFirst)
        {
            var (engine, sessions, need, line) = await UnsureFormatAsync();
            var key = StartPlanDecide.LineKey(line);
            if (personKeptFirst)
            {
                await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
                await SettleAsync(engine);
            }
            await McpTools.EditStartPlan(engine, "pin", key, "#,##0.00");
            await SettleAsync(engine);
            var pinned = LineOf(engine, key);
            Assert.Equal(StartDecidedBy.Assistant, pinned.DecidedBy);
            // Word boundaries (join 6): "Pinned: your assistant chose this value" is true and contains the letters "Pinned: you"; the line's why now stays that
            // instead of being overwritten with a Left out sentence, so the check is for the PERSON being named, not for a prefix.
            Assert.DoesNotMatch(new Regex(@"\bby you\b", RegexOptions.IgnoreCase), pinned.Why ?? "");
            Assert.DoesNotMatch(new Regex(@"\bPinned: you\b", RegexOptions.IgnoreCase), pinned.Why ?? "");
            var view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var fmt = await FormatOfAsync(sessions, pinned.Item.ObjectRef);
            var after = LineOf(engine, key);
            Say($"D2 assistant pin (person first {personKeptFirst}): '{fmt}', why [{after?.Why}] held [{after?.Held}]");
            Assert.True(string.IsNullOrEmpty(fmt), "a format only the assistant pinned went into the model: '" + fmt + "'");
            Assert.False(string.IsNullOrWhiteSpace(after?.Held), "the line was neither built nor held with a reason");
            // Join 6, J3 (v-sf-int-3 note b): the reason is said in the start plan's own words. The Plan's "nobody ticked the line ... tick it on
            // the Plan" is untrue of a person who kept the line on THIS plan, and "Left out" is untrue of a line that is kept.
            Assert.DoesNotContain("nobody ticked", after.Held, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("on the Plan", after.Held, StringComparison.Ordinal);
            Assert.DoesNotContain("tick", after.Held, StringComparison.OrdinalIgnoreCase);
            Assert.False(StartPlanDecide.IsLeftOut(after), "a kept line's why starts with Left out: " + after.Why);
            Assert.DoesNotContain("Left out", after.Why ?? "", StringComparison.Ordinal);
        }

        // ================================================================== D2: "Kept by you" on a line the build left out (join note 2)

        /// <summary>The person keeps Jev's unsure format, which is stored against the NEW name of a time measure the assistant renamed. Then the person drops that
        /// rename. The measure is built under its old name and the kept line's spelling no longer leads anywhere the manifest can explain. Whatever the plan does
        /// with the line, it must not read "Kept by you" while the build leaves the format out.</summary>
        [Fact]
        public async Task Verify3_a_kept_format_whose_rename_the_person_dropped_is_built_or_marked_never_shown_kept_and_skipped()
        {
            var (engine, sessions, need, line) = await UnsureFormatAsync();
            var key = StartPlanDecide.LineKey(line);
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            await SettleAsync(engine);
            var doc = engine.StartPlanDocForTest();
            var objectRef = line.Item.ObjectRef;
            var slash = objectRef.LastIndexOf('/');
            var rename = doc.Lines.FirstOrDefault(l => l.Kept && string.Equals(l.Item.Kind, "rename", StringComparison.OrdinalIgnoreCase)
                                                        && string.Equals((l.Item.After ?? "").Trim(), objectRef.Substring(slash + 1), StringComparison.OrdinalIgnoreCase)
                                                        && l.Item.ObjectRef.StartsWith(objectRef.Substring(0, slash + 1), StringComparison.OrdinalIgnoreCase));
            Say($"D2 rename-drop: kept line {key}; the rename that names it: {(rename == null ? "(none: the line is on a name nobody renamed)" : StartPlanDecide.LineKey(rename) + " by " + rename.DecidedBy)}");
            Assert.True(rename != null, "the unsure format is not on a renamed measure, so this case cannot be reached this way");
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = StartPlanDecide.LineKey(rename) }, "human");
            await SettleAsync(engine);

            var view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var after = engine.StartPlanDocForTest().Lines.Where(l => string.Equals(l.Item.Kind, line.Item.Kind, StringComparison.OrdinalIgnoreCase)
                                                                     && (string.Equals(l.Item.ObjectRef, objectRef, StringComparison.OrdinalIgnoreCase)
                                                                         || string.Equals(l.Item.ObjectRef, rename.Item.ObjectRef, StringComparison.OrdinalIgnoreCase))).ToList();
            var oldFmt = await FormatOfAsync(sessions, rename.Item.ObjectRef);
            var newFmt = await FormatOfAsync(sessions, objectRef);
            foreach (var l in after) Say($"   after the build: {StartPlanDecide.LineKey(l)} kept={l.Kept} by {l.DecidedBy} held=[{l.Held}] why=[{l.Why}] after={l.Item.After}");
            Say($"   built model: {rename.Item.ObjectRef} format '{oldFmt}'; {objectRef} format '{newFmt}'");
            var shownKeptNotBuilt = after.Where(l => l.Kept && l.DecidedBy == StartDecidedBy.Person && string.IsNullOrWhiteSpace(l.Held)
                                                      && !string.Equals(oldFmt, l.Item.After, StringComparison.Ordinal) && !string.Equals(newFmt, l.Item.After, StringComparison.Ordinal)).ToList();
            Assert.True(shownKeptNotBuilt.Count == 0, "a line the person kept reads as kept, with no mark, and the build left it out: "
                + string.Join("; ", shownKeptNotBuilt.Select(l => StartPlanDecide.LineKey(l) + " [" + l.Why + "]")) + $" (model: old name '{oldFmt}', new name '{newFmt}')");
        }

        /// <summary>The person keeps Jev's unsure format on a time measure, then changes the pick of the base measure it comes from, so the time measure is replaced
        /// by another. A line the build leaves out must not read "Kept by you" with no mark.</summary>
        [Fact]
        public async Task Verify3_a_kept_format_on_a_time_measure_whose_base_pick_changed_is_never_shown_kept_and_skipped()
        {
            var (engine, sessions, need, line) = await UnsureFormatAsync();
            var key = StartPlanDecide.LineKey(line);
            await engine.EditStartPlanAsync(new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            await SettleAsync(engine);
            var doc = engine.StartPlanDocForTest();
            var objectRef = line.Item.ObjectRef;
            var back = StartPlanWords.Back(doc, objectRef);
            var name = back.Substring(back.LastIndexOf('/') + 1);
            var bm = doc.Manifest.BaseMeasures.Where(b => name.StartsWith(b.Name + " ", StringComparison.OrdinalIgnoreCase) || string.Equals(name, b.Name, StringComparison.OrdinalIgnoreCase))
                                              .OrderByDescending(b => b.Name.Length).FirstOrDefault();
            Say($"D2 pick change: kept line {key} (before renames {back}); base measure {(bm == null ? "(none)" : bm.Table + "/" + bm.Column + " " + bm.Pick + " '" + bm.Name + "'")}");
            Assert.True(bm != null, "no base measure behind the kept line, so this case cannot be reached this way");
            var pick = bm.Pick == "average" ? "max" : "average";
            var edited = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "set_pick", Target = bm.Table + "/" + bm.Column, Value = pick }, "human");
            await SettleAsync(engine);

            var view = await engine.GetStartPlanAsync();
            view = await engine.ApproveStartPlanAsync(view.Digest);
            var built = await engine.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var lines = engine.StartPlanDocForTest().Lines.Where(l => l.Kept && l.DecidedBy == StartDecidedBy.Person && LocalEngine.IsJevFormatLine(l.Item)).ToList();
            var bad = new List<string>();
            foreach (var l in lines)
            {
                var a = await FormatOfAsync(sessions, l.Item.ObjectRef);
                var b = await FormatOfAsync(sessions, StartPlanWords.Back(engine.StartPlanDocForTest(), l.Item.ObjectRef));
                var c = await FormatOfAsync(sessions, StartPlanWords.Forward(engine.StartPlanDocForTest(), l.Item.ObjectRef));
                Say($"   after the build: {StartPlanDecide.LineKey(l)} held=[{l.Held}] why=[{l.Why}] after={l.Item.After}; model '{a}' / '{b}' / '{c}'");
                if (string.IsNullOrWhiteSpace(l.Held) && a != l.Item.After && b != l.Item.After && c != l.Item.After) bad.Add(StartPlanDecide.LineKey(l) + " [" + l.Why + "]");
            }
            Assert.True(bad.Count == 0, "a line the person kept reads as kept, with no mark, and the build left it out: " + string.Join("; ", bad));
        }
    }
}
