using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Tool text an agent can actually read. A [Description] only reaches the wire when its method is an
    /// McpServerTool; on any other method it is dead text that drifts from the real contract while looking like one
    /// (an automated commit once cut three tools to one-liners and left their full text stranded on helpers).</summary>
    public sealed class McpDescriptionOrphanTests
    {
        private static bool IsTool(MethodInfo m) =>
            m.GetCustomAttributes(true).Any(a => a.GetType().Name == "McpServerToolAttribute");

        private static MethodInfo[] McpToolsMethods() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name.StartsWith("McpTools", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .ToArray();

        [Fact]
        public void Every_method_description_in_McpTools_sits_on_a_tool()
        {
            var methods = McpToolsMethods();
            Assert.True(methods.Count(IsTool) >= 300, "the tool surface did not load, so this walk proved nothing");
            var orphans = methods
                .Where(m => !IsTool(m) && m.GetCustomAttributes(typeof(DescriptionAttribute), false).Length > 0)
                .Select(m => m.DeclaringType.Name + "." + m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(orphans.Length == 0,
                "these McpTools methods carry a [Description] no agent ever sees: " + string.Join(", ", orphans));
        }

        [Fact]
        public void Every_parameter_description_in_McpTools_sits_on_a_tool()
        {
            var orphans = McpToolsMethods()
                .Where(m => !IsTool(m))
                .SelectMany(m => m.GetParameters()
                    .Where(p => p.GetCustomAttributes(typeof(DescriptionAttribute), false).Length > 0)
                    .Select(p => m.DeclaringType.Name + "." + m.Name + "(" + p.Name + ")"))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(orphans.Length == 0,
                "these McpTools parameters carry a [Description] no agent ever sees: " + string.Join(", ", orphans));
        }

        /// <summary>Kane, 24 Sep: every DAX write is checked whatever the switch says, so the switch is named for what
        /// it does (it adds an audit record). The wire names stay, so no assistant or script breaks, and both
        /// descriptions say so.</summary>
        [Theory]
        [InlineData("get_verified_mode")]
        [InlineData("set_verified_mode")]
        public void Record_verified_edits_is_named_for_what_it_does(string tool)
        {
            var m = McpToolsMethods().Single(x => x.GetCustomAttributes(true)
                .Any(a => a.GetType().Name == "McpServerToolAttribute"
                          && (string)a.GetType().GetProperty("Name")?.GetValue(a) == tool));
            var all = string.Join(" ", new[] { m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "" }
                .Concat(m.GetParameters().Select(p => p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")));
            Assert.Contains("Record verified edits", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Verified Mode", all, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("keeps its older name", all, StringComparison.Ordinal);
        }
    }
}
