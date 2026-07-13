using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class HardMeasureWorkflowTests
    {
        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Featured_template_and_executable_share_the_v4_oracle_contract()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-hard-v4-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(workspace);
            using var engine = new LocalEngine(new SessionManager(), new Free(), workspace);
            try
            {
                var canonical = await engine.GetWorkflowAsync("verified-measure");
                var template = await engine.GetWorkflowTemplateAsync("hard-measure");
                Assert.Null(canonical.Error);
                Assert.Null(template.Error);
                Assert.Equal(4, canonical.Version);
                Assert.Equal(4, template.Version);
                Assert.Equal(new[] { "measure_goal", "measure_pattern" }, template.Slots.Select(x => x.Name));
                Assert.DoesNotContain(template.Slots, x => x.Name.Contains("control", StringComparison.OrdinalIgnoreCase));

                var values = JsonSerializer.Serialize(new
                {
                    measure_goal = "year-on-year revenue growth percentage",
                    measure_pattern = "year-over-year over the marked date table",
                });
                await engine.InstantiateWorkflowTemplateAsync("hard-measure", "hard-measure-v4-test", values, "human");
                var featured = await engine.GetWorkflowAsync("hard-measure-v4-test");

                Assert.Null(featured.Error);
                Assert.Equal(4, featured.Version);
                Assert.Equal(5, canonical.Steps.Length);
                Assert.Equal(StructuralSignature(canonical), StructuralSignature(featured));

                foreach (var workflow in new[] { canonical, featured })
                {
                    var instructions = string.Join("\n", workflow.Steps.Select(x => x.Instructions));
                    Assert.Contains("independent raw-row oracle", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("known-wrong", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("parent-crossed leaves", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("performance rewrite", instructions, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("control total", instructions, StringComparison.OrdinalIgnoreCase);

                    var equality = Assert.Single(workflow.Steps[3].Gate.Verify);
                    Assert.Equal("dax_equivalence", equality.Kind);
                    Assert.Equal("inputs.oracleDax.answered", equality.When);
                    Assert.Equal("oracleDax", equality.Probe);
                }
            }
            finally { Directory.Delete(workspace, true); }
        }

        private static string StructuralSignature(WorkflowDef workflow) => JsonSerializer.Serialize(
            workflow.Steps.Select(step => new
            {
                step.Number,
                step.Title,
                step.Ops,
                Gate = step.Gate == null ? null : new
                {
                    step.Gate.Strictness,
                    Inputs = step.Gate.Inputs.Select(input => new { input.Name, input.Type, input.Required }),
                    Verify = step.Gate.Verify.Select(check => new { check.Kind, check.When, check.Probe, check.Scope, check.Intent }),
                },
            }));
    }
}
