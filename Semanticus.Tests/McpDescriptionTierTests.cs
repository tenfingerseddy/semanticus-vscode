using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The agent door's buying advice. Without this sweep the assistant keeps telling people the old line,
    /// so every Pro tool's description says which feature it belongs to, in the one phrase, and no free tool claims
    /// to be Pro or sells a bulk apply that is free now.</summary>
    public sealed class McpDescriptionTierTests
    {
        private const string Phrase = "Semanticus Pro feature";

        private static (string Tool, string Description)[] ToolDescriptions() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => (
                    attr: m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"),
                    desc: m.GetCustomAttributes(typeof(DescriptionAttribute), false)
                        .Cast<DescriptionAttribute>().FirstOrDefault()?.Description ?? ""))
                .Where(x => x.attr != null)
                .Select(x => ((string)x.attr.GetType().GetProperty("Name")?.GetValue(x.attr), x.desc))
                .ToArray();

        [Fact]
        public void Every_Pro_tool_says_which_feature_it_belongs_to()
        {
            var all = ToolDescriptions();
            Assert.True(all.Length >= 300, "the tool surface did not load, so this walk proved nothing");
            var silent = all
                .Where(d => FeatureMap.TryOperation(d.Tool, out _, out _))
                .Where(d => !d.Description.Contains(Phrase, StringComparison.Ordinal))
                .Select(d => d.Tool).ToArray();
            Assert.True(silent.Length == 0,
                "these Pro tools do not tell the agent they are Pro: " + string.Join(", ", silent));
        }

        [Fact]
        public void No_free_tool_claims_to_be_Pro()
        {
            var lying = ToolDescriptions()
                .Where(d => FeatureMap.FreeOperations.Contains(d.Tool))
                .Where(d => d.Description.Contains(Phrase, StringComparison.Ordinal)
                         || Regex.IsMatch(d.Description, @"\bis Pro\b|\(Pro\)|\bPRO\b"))
                .Select(d => d.Tool).ToArray();
            Assert.True(lying.Length == 0,
                "these tools are free and their description still says Pro: " + string.Join(", ", lying));
        }

        /// <summary>No Pro tool tells the agent it is free. A description may still point AT a free alternative,
        /// which is the whole point of the refusal sentence, so only self-claims are refused.</summary>
        [Fact]
        public void No_Pro_tool_claims_to_be_free()
        {
            var claims = new Regex(@"(?i)\b(?:free|FREE)\b\s*[,.;:]|\bis\s+FREE\b|\bRead-only and free\b|\bfree tier\b");
            var lying = ToolDescriptions()
                .Where(d => FeatureMap.TryOperation(d.Tool, out _, out _))
                .Where(d => claims.IsMatch(d.Description))
                .Select(d => d.Tool).ToArray();
            Assert.True(lying.Length == 0,
                "these tools are Pro and their description still says free: " + string.Join(", ", lying));
        }

        /// <summary>The retired line, phrase by phrase. Kane made every bulk apply free, so no description may still
        /// sell one, and get_entitlement must describe features rather than the bulk moat.</summary>
        [Theory]
        [InlineData("Pro unlocks the one-click BULK apply")]
        [InlineData("bulk primitive")]
        [InlineData("is the Pro value")]
        [InlineData("Trying is FREE")]
        [InlineData("paused-free")]
        [InlineData("Enforcement is what's paid")]
        [InlineData("all data-agent writes are Pro")]
        [InlineData("Removing MORE THAN ONE item at once is Pro")]
        [InlineData("committing MORE than one object at once is Pro")]
        [InlineData("the bulk lever, Pro")]
        public void The_retired_line_is_gone_from_the_agent_door(string retired)
        {
            var bad = ToolDescriptions()
                .Where(d => d.Description.Contains(retired, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Tool).ToArray();
            Assert.True(bad.Length == 0,
                "these descriptions still carry \"" + retired + "\": " + string.Join(", ", bad));
        }

        /// <summary>No em dashes in agent-facing copy, per the product copy style.</summary>
        [Fact]
        public void No_tool_description_uses_an_em_dash()
        {
            var bad = ToolDescriptions()
                .Where(d => FeatureMap.TryOperation(d.Tool, out _, out _))
                .Where(d => d.Description.Contains('—'))
                .Select(d => d.Tool).ToArray();
            Assert.True(bad.Length == 0, "these Pro descriptions use an em dash: " + string.Join(", ", bad));
        }

        /// <summary>get_entitlement is the agent's own map of what it may call, so it names every wire id it can
        /// report, and it names the paid things that have no wire id rather than leaving an agent to find out by
        /// being refused.</summary>
        [Fact]
        public void get_entitlement_describes_every_wire_id_and_what_has_none()
        {
            var d = ToolDescriptions().Single(x => x.Tool == "get_entitlement").Description;
            foreach (var f in ProFeatures.TabFeatures)
                Assert.Contains(ProFeatures.WireId(f), d, StringComparison.Ordinal);
            Assert.Contains("route_ask", d, StringComparison.Ordinal);
            Assert.Contains("no wire id", d, StringComparison.Ordinal);
            Assert.DoesNotContain("Pro unlocks the one-click BULK apply", d, StringComparison.Ordinal);
            Assert.DoesNotContain("modelCreate", d, StringComparison.Ordinal);
            Assert.DoesNotContain("publishedAdvanced", d, StringComparison.Ordinal);
        }

        /// <summary>The eight tabs Kane freed on 22 September, checked at the agent door rather than only on the
        /// map: an agent that still reads "Tests is a Semanticus Pro feature" will keep telling people to buy
        /// something they already have. One operation per freed tab, plus the phrase sweep above for the rest.</summary>
        [Theory]
        [InlineData("get_spec")] [InlineData("build_model_from_spec")]
        [InlineData("list_calendars")] [InlineData("daxlib_install")]
        [InlineData("get_partition_m")] [InlineData("set_partition_m")]
        [InlineData("get_doc_outline")] [InlineData("set_doc_section")]
        [InlineData("list_insights")] [InlineData("get_model_primer")]
        [InlineData("run_tests")] [InlineData("save_test")] [InlineData("delete_test")]
        [InlineData("save_evidence")] [InlineData("list_evidence")]
        [InlineData("git_push")] [InlineData("fabric_git_status")]
        [InlineData("cicd_publish")] [InlineData("create_data_agent")]
        public void A_tab_that_became_free_no_longer_sells_itself(string operation)
        {
            var d = ToolDescriptions().Single(x => x.Tool == operation).Description;
            Assert.DoesNotContain(Phrase, d, StringComparison.Ordinal);
            foreach (var retired in new[] { "Create in Model is", "Tests is a", "Saved reports is a", "Advanced publishing is a" })
                Assert.DoesNotContain(retired, d, StringComparison.Ordinal);
        }

        /// <summary>Hero 24 D12: an assistant saved a check by reading the shipped studio.js for the paramsJson fields. The description names them,
        /// so it can never fall behind the two request classes.</summary>
        [Fact]
        public void save_test_lists_every_paramsJson_field_of_both_kinds()
        {
            var d = ToolDescriptions().Single(x => x.Tool == "save_test").Description;
            string Camel(string n) => char.ToLowerInvariant(n[0]) + n.Substring(1);
            foreach (var type in new[] { typeof(Semanticus.Engine.ReconcileRequest), typeof(Semanticus.Engine.MeasureValueRequest) })
                foreach (var prop in type.GetProperties())
                    Assert.True(d.Contains(Camel(prop.Name), StringComparison.Ordinal), type.Name + "." + Camel(prop.Name) + " is not in the save_test description: " + d.Substring(0, Math.Min(400, d.Length)));
            Assert.DoesNotContain("\u2014", d, StringComparison.Ordinal);
        }

        /// <summary>The new shared free projection tells the agent it is the free way to see source usage.</summary>
        [Fact]
        public void list_sql_source_usage_is_described_as_free_and_read_only()
        {
            var d = ToolDescriptions().Single(x => x.Tool == "list_sql_source_usage").Description;
            Assert.Contains("free", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Phrase, d, StringComparison.Ordinal);
        }
    }
}
