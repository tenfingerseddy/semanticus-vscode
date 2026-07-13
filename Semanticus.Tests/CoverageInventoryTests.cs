using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The release oracle follows the compiled MCP surface, not source text or the current checkout.
    /// The inventory is copied beside the test assembly, so this stays hermetic under detached HEAD, arbitrary
    /// CWD, and clean-package execution.</summary>
    public sealed class CoverageInventoryTests
    {
        private static string[] CompiledToolNames() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttributes(true)
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string?)a!.GetType().GetProperty("Name")?.GetValue(a))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void Every_compiled_MCP_operation_is_release_classified_once()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "mcp-surface-inventory.json");
            Assert.True(File.Exists(path), $"Coverage inventory was not copied beside the test assembly: {path}");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var inventoryNames = root.GetProperty("operations")
                .EnumerateArray()
                .Select(o => o.GetProperty("operation").GetString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            var compiledNames = CompiledToolNames();

            Assert.Equal(compiledNames.Length, compiledNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(inventoryNames.Length, inventoryNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(compiledNames, inventoryNames);
            Assert.Equal(compiledNames.Length, root.GetProperty("summary").GetProperty("mcpOperations").GetInt32());
            Assert.Equal(0, root.GetProperty("summary").GetProperty("unclassifiedOperations").GetInt32());

            var allowedClasses = new HashSet<string>(StringComparer.Ordinal)
            {
                "supported", "supervised", "dry-run-only", "experimental", "deferred"
            };
            foreach (var operation in root.GetProperty("operations").EnumerateArray())
            {
                var name = operation.GetProperty("operation").GetString();
                var family = operation.GetProperty("family").GetString();
                var releaseClass = operation.GetProperty("releaseClass").GetString();
                Assert.False(string.IsNullOrWhiteSpace(family), $"{name} has no coverage family.");
                Assert.NotEqual("unclassified", family);
                Assert.True(releaseClass != null && allowedClasses.Contains(releaseClass),
                    $"{name} has unknown release class '{releaseClass}'.");
            }
        }

        [Fact]
        public void Coverage_references_come_only_from_executable_test_sources()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "mcp-surface-inventory.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var operation in document.RootElement.GetProperty("operations").EnumerateArray())
            foreach (var evidence in operation.GetProperty("referenceEvidence").EnumerateArray())
            {
                var source = evidence.GetString()!;
                Assert.True(source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase),
                    $"Non-executable artifact '{source}' was counted as coverage evidence.");
            }
        }
    }
}
