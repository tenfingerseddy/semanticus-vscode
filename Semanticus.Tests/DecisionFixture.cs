// Shared scaffolding for the decision tests (Jev M0a).
//
// Three things every decision test needs and none of them should hand-roll:
//   a scratch per-user home, so consent never touches a real ~/.semanticus
//   a scratch workspace, so the per-model decision settings land somewhere disposable
//   a small model with one folder already in use, so the folder question has real options
//
// The fixture also carries the two-phase trick that keeps the recorded fixtures honest. A recorded oracle is
// keyed on the COMPLETE request digest, and the digest is built from the model, so a test cannot write the
// digest down by hand without freezing a copy of the model's shape in a string literal. Instead the test asks
// once with an EMPTY recorded oracle, reads the digest the engine actually produced, and builds the fixture
// around it. That also proves the digest is stable across two identical asks, which is the property the cache
// and the replay gate both rely on.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;

namespace Semanticus.Tests
{
    internal sealed class DecisionFixture : IDisposable
    {
        private readonly string _previousRoot;

        public DecisionFixture(Semanticus.Engine.Entitlement.IEntitlement entitlement = null)
        {
            // NOT "semanticus-<guid>": a temp directory with that prefix is treated as an EPHEMERAL anchor
            // (ExperienceLog.IsEphemeralAnchor), which is the rule for a live or local XMLA snapshot dir. A
            // model saved in one has no durable identity, and these tests need one.
            Home = Path.Combine(Path.GetTempPath(), "sem-decisions-" + Guid.NewGuid().ToString("N"));
            Workspace = Path.Combine(Home, "workspace");
            Directory.CreateDirectory(Workspace);

            _previousRoot = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = Path.Combine(Home, "user");

            Sessions = new SessionManager();
            Engine = new LocalEngine(Sessions, entitlement ?? TestEntitlements.Pro, Workspace);
            // The four Plan families ask a table at a time in these tests (m11/jev-batch, JevTableBatch.cs), whatever each family's own
            // switch says about shipping: a test that wants one object at a time sets its own (JevPerObjectModeTests).
            Engine.JevBatchedForTest = _ => true;
        }

        public string Home { get; }
        public string Workspace { get; }
        public SessionManager Sessions { get; }
        public LocalEngine Engine { get; }

        /// <summary>A model with one table, one filed measure (so the folder question has an option) and one
        /// unfiled, unformatted measure, which is the measure every proposal test asks about.
        ///
        /// The model is SAVED before it is returned, and that is load bearing rather than tidiness: permission
        /// is bound to the model it was given for, and an unsaved, unconnected model has no stable identity to
        /// bind to, so nothing can be agreed for it. Every test that needs consent therefore needs a model with
        /// a durable anchor.</summary>
        public async Task<string> BuildModelAsync(string fileName = "DecisionProof.bim")
        {
            await Engine.CreateModelAsync("DecisionProof", 1604);
            await Engine.CreateTableAsync("Sales", "human");
            await Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            var measure = await Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await SaveAsAsync(fileName);
            return measure;
        }

        /// <summary>Give the open model a durable on-disk identity inside this fixture's scratch workspace. It stands in for the
        /// person choosing the file in the Save as dialog, which is the one way a never-saved model replaces a .bim that already
        /// exists (several tests save a stranger's model over the same file on purpose).</summary>
        public Task SaveAsAsync(string fileName) =>
            Engine.SaveAsync(Path.Combine(Workspace, fileName), "bim", false, "human", true);

        /// <summary>Grant consent for exactly the fields this build declares.</summary>
        public Task<DecisionConsentInfo> ConsentAsync() =>
            Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");

        /// <summary>Point the engine at a recorded oracle and hand back both halves.</summary>
        public RecordedDecisionOracle UseRecorded(string fixtureJson, DecisionBudget budget = null)
        {
            var oracle = new RecordedDecisionOracle(fixtureJson, "jev-test-1");
            Engine.UseDecisionsForTest(new DecisionService(oracle, budget ?? new DecisionBudget()));
            return oracle;
        }

        /// <summary>Point the engine at a recorded oracle that also answers the BATCHED families' requests, from entries keyed by an
        /// object ref (ByObjectRecordedOracle). The per-object fixtures the family tests wrote keep their shape.</summary>
        public ByObjectRecordedOracle UseRecordedByObject(string fixtureJson, DecisionBudget budget = null)
        {
            var oracle = new ByObjectRecordedOracle(fixtureJson, "jev-test-1");
            Engine.UseDecisionsForTest(new DecisionService(oracle, budget ?? new DecisionBudget()));
            return oracle;
        }

        public RecordedDecisionOracle UseEmptyRecorded(DecisionBudget budget = null) =>
            UseRecorded("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[]}", budget);

        public NullDecisionOracle UseNull()
        {
            Engine.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance));
            return NullDecisionOracle.Instance;
        }

        /// <summary>Phase one: ask with an empty recorded oracle to learn the digest the engine really produces.</summary>
        public async Task<string> DiscoverDigestAsync(string measureRef)
        {
            var probe = UseEmptyRecorded();
            await Engine.ProposeMeasureDefaultsAsync(measureRef);
            if (probe.AskedDigests.Count != 1)
                throw new InvalidOperationException(
                    $"The probe expected exactly one ask and saw {probe.AskedDigests.Count}. Consent, Pro and the point's switch all have to be set up before the digest can be discovered.");
            return probe.AskedDigests[0];
        }

        /// <summary>A fixture answering the proof point's two questions at the confidences given.</summary>
        public static string ProofFixture(string digest, string folder, double folderConfidence,
                                          string formatFamily, double formatConfidence,
                                          int inputTokens = 900, int outputTokens = 40)
        {
            var sb = new StringBuilder();
            sb.Append("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
            sb.Append(JsonSerializer.Serialize(digest));
            sb.Append(",\"point\":\"").Append(DecisionRegistry.MeasureFolderAndFormat).Append('"');
            sb.Append(",\"usage\":{\"input_tokens\":").Append(inputTokens)
              .Append(",\"output_tokens\":").Append(outputTokens).Append('}');
            sb.Append(",\"answers\":{");
            // The wire carries the OPTION ID, and a model-derived folder's id is encoded rather than being the
            // folder name itself. Tests name the folder; the fixture encodes it the way the point does.
            sb.Append("\"display_folder\":{\"type\":\"choice\",\"choice\":")
              .Append(JsonSerializer.Serialize(MeasureDefaultsPoint.FolderOptionId(folder)))
              .Append(",\"confidence\":").Append(folderConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
            sb.Append(",\"format_family\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(formatFamily))
              .Append(",\"confidence\":").Append(formatConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
            sb.Append("}}]}");
            return sb.ToString();
        }

        /// <summary>A request that does not depend on a model, for the contract tests.</summary>
        public static DecisionRequest SampleRequest(string pointName = null)
        {
            var point = DecisionRegistry.Require(pointName ?? DecisionRegistry.MeasureFolderAndFormat);
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", "Total Sales"),
                new KeyValuePair<string, object>("measure_formula_facts", SampleFormulaFacts("SUM")),
                new KeyValuePair<string, object>("table_name", "Sales"),
                new KeyValuePair<string, object>("table_columns", new List<object>
                {
                    new Dictionary<string, object> { { "name", "Amount" }, { "data_type", "Double" } },
                }),
                new KeyValuePair<string, object>("sibling_measures", new List<object>()),
            });

            var questions = MeasureDefaultsPoint.Questions(new[] { "Volume" }, true, true);
            return new DecisionRequest(point.Name, point.SchemaVersion, snapshot, questions);
        }

        /// <summary>Formula facts shaped the way MeasureDefaultsPoint.FormulaFacts builds them, for tests that
        /// need a snapshot without a model.</summary>
        public static IReadOnlyDictionary<string, object> SampleFormulaFacts(params string[] functions) =>
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "functions", functions.Cast<object>().ToList() },
                { "returns_ratio", false },
                { "returns_count", false },
                { "returns_currency_like_sum", true },
                { "references_date_column", false },
                { "time_intelligence", false },
            };

        public void Dispose()
        {
            try { Engine.Dispose(); } catch { }
            try { Sessions.Dispose(); } catch { }
            ConnectionRegistry.RootOverride = _previousRoot;
            try { Directory.Delete(Home, recursive: true); } catch { }
        }
    }
}
