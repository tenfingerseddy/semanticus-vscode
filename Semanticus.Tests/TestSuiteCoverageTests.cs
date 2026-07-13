using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TestSuiteCoverageTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Unmapped_model_measures_are_notverifiable_instead_of_disappearing()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-suite-coverage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake());
                await engine.OpenAsync(model);
                var measures = await engine.ListMeasuresAsync();

                var run = await engine.RunTestSuiteAsync(false, "human");

                Assert.NotEmpty(measures);
                Assert.Equal(0, run.DefinitionCount);
                Assert.Equal(measures.Select(m => m.Ref).OrderBy(x => x),
                    run.Reconciles.Select(o => o.TargetRef).OrderBy(x => x));
                Assert.All(run.Reconciles, outcome =>
                {
                    Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
                    Assert.False(outcome.Missing);
                    Assert.Contains("No human-accepted source SQL mapping", outcome.Message);
                });
                var correctness = run.Health.Categories.Single(c => c.Category == "Correctness");
                Assert.False(correctness.HasChecks);
                Assert.Equal(measures.Length, correctness.Checked);
                Assert.Equal(measures.Length, correctness.NotVerifiable);
                Assert.True(run.Health.CoveragePct < 100.0);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
