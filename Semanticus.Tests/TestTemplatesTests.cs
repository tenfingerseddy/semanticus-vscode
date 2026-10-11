// Proofs for the lean-suite helpers in TestTemplates.cs (lane 0): the sweep trait convention and the template-open path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TestCostTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public TestCostTests(ModelTemplates templates) => _templates = templates;

        /// <summary>Every [Trait("Cost", x)] in this assembly, as (where, x). Read from the attribute data, so no test is run or built.</summary>
        private static readonly Lazy<List<(string Where, string Value)>> Traits = new Lazy<List<(string Where, string Value)>>(ReadCostTraits);
        private static List<(string Where, string Value)> CostTraits() => Traits.Value;

        private static List<(string Where, string Value)> ReadCostTraits()
        {
            var found = new List<(string, string)>();
            void Read(MemberInfo m, string where)
            {
                foreach (var a in m.GetCustomAttributesData().Where(x => x.AttributeType.Name == "TraitAttribute"))
                    if ((string)a.ConstructorArguments[0].Value == TestCost.Key)
                        found.Add((where, (string)a.ConstructorArguments[1].Value));
            }
            foreach (var t in typeof(TestCostTests).Assembly.GetTypes())
            {
                Read(t, t.FullName);
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    Read(m, t.FullName + "." + m.Name);
            }
            return found;
        }

        // CI's default filter is Cost!=sweep, the nightly one is Cost=sweep. A value that is not exactly "sweep" ("Sweep", "sweeps")
        // matches neither the sweep filter nor excludes the test from the default one, so the test would be slow for every pull
        // request and never run nightly. Cheap, and it needs no engine.
        [Fact]
        public void Every_Cost_trait_is_spelt_sweep()
        {
            var bad = CostTraits().Where(x => x.Value != TestCost.Sweep).ToArray();
            Assert.True(bad.Length == 0, "a Cost trait that is not exactly \"" + TestCost.Sweep + "\": "
                + string.Join(", ", bad.Select(b => b.Where + "=" + b.Value)));
        }

        // The convention has to be in use: the full plan commutation gate (about sixteen minutes) is the first sweep test.
        [Fact]
        public void The_commutation_gate_is_a_sweep_test()
        {
            Assert.Contains(CostTraits(), x => x.Where.EndsWith("PlanCommutationTests.Full_commutation_gate_with_the_coverage_bar", StringComparison.Ordinal));
        }

        private static async Task BuildSmallAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("TemplateProof", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
        }

        // One build per key however many tests open it, and each open is its own file: an edit in one copy is not in the next.
        // Three engines and a build, which took 8 to 10 s on a machine at load 25, so it is a sweep test: the default run is for
        // tests of about half a second, and every class that uses ModelTemplates exercises it on each pull request anyway.
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_template_builds_once_per_key_and_each_copy_is_its_own_file()
        {
            using var a = new DecisionFixture(TestEntitlements.Pro);
            using var b = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(a, "small", BuildSmallAsync, "Small.bim");
            await a.Engine.CreateMeasureAsync("table:Sales", "Edited In The First Copy", "1", "human");
            await _templates.OpenCopyAsync(b, "small", BuildSmallAsync, "Small.bim");

            Assert.Equal(1, _templates.Builds);
            var names = (await b.Sessions.Require().ReadAsync(m => m.Tables.SelectMany(t => t.Measures).Select(x => x.Name).ToArray()));
            Assert.Equal(new[] { "Total Sales" }, names);
        }
    }
}
