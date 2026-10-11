// verify: lane sf-consent (L8). The start plan's grant is bound to the digest of the plan the person was shown, so it covers
// exactly the tables they saw. The sample flag is NOT in that digest (StartPlanViewBuilder.Digest has no field for it, and the
// view does not carry it), and GrantStartDisclosureAsync records whatever the engine's flag is at the moment OK is pressed.
// So a sample read between the look and the OK is granted on a digest the person saw with no sample in it, and sample facts
// then go to Jev. The stand-in host in StartPlanConsentTests hides this: its digest adds "|sample=" itself. These tests stay
// red until the grant binds the sample setting the person saw.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class VerifySfConsentSampleGrantTests : IDisposable
    {
        private sealed class CountingOracle : IDecisionOracle
        {
            public bool Available => true;
            private int _count;
            public int Count => Volatile.Read(ref _count);
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref _count);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "test oracle never decides"));
            }
        }

        private readonly string _root;
        private readonly string _origHome;
        private readonly string _origRegistry;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly CountingOracle _oracle = new CountingOracle();

        public VerifySfConsentSampleGrantTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            _origRegistry = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
        }

        public void Dispose()
        {
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _origRegistry;
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
        };

        private async Task<(LocalEngine Engine, StartPlanView View)> StartAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, _root);
            _owned.Add(engine);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Schema());
            engine.UseDecisionsForTest(new DecisionService(_oracle, new DecisionBudget()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
                Folder = Path.Combine(_root, "proj-" + Guid.NewGuid().ToString("N").Substring(0, 6)), Name = "Sales Model",
            }, "human");
            Assert.True(view.Exists);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            return (engine, view);
        }

        private async Task<DecisionOutcome> AskWithSampleFactsAsync(LocalEngine engine)
        {
            var doc = engine.StartPlanHost.Current();
            var ask = StartAskScope.For(doc, new[] { "Sales" }, sampleFacts: true);
            var fence = await engine.StartAskFenceAsync("scratch-v", 1, () => ("scratch-v", 1), ask);
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Total Amount"),
                new KeyValuePair<string, object>("measure_formula_facts", DecisionFixture.SampleFormulaFacts("SUM")),
                new KeyValuePair<string, object>("table_name", "Sales"),
                new KeyValuePair<string, object>("table_columns", new List<object>
                {
                    new Dictionary<string, object> { { "name", "Amount" }, { "data_type", "Double" } },
                }),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            });
            var questions = MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true);
            return await engine.Decisions.AskAsync(point.Name, snapshot, questions, isPro: true, enabled: true, fence: fence, ct: CancellationToken.None);
        }

        [Fact]
        public async Task A_grant_on_a_digest_seen_before_the_sample_was_read_does_not_let_sample_facts_go()
        {
            var (engine, view) = await StartAsync();
            var seen = view.Digest;                               // the person looks: the sample is off

            engine.NoteStartSampleRead(view.Id, true);            // a sample of the source is read before they press OK

            try { await engine.GrantStartDisclosureAsync(seen, "human"); }
            catch (InvalidOperationException) { /* refusing as stale is one right answer */ }

            var granted = engine.StartPlanDocForTest().Disclosure;
            Assert.False(granted?.SampleFacts == true,
                "the person agreed on a plan they saw with no sample, and the grant now covers facts from a sample of rows");

            var outcome = await AskWithSampleFactsAsync(engine);
            Assert.False(outcome.Asked, "an ask carrying sample facts went to Jev on a grant the person gave for no sample");
            Assert.Equal(0, _oracle.Count);
        }
    }
}
