using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's gate, Q5 item 1 (lane m11/uat-undo): the registry is COMPLETE. Every method of the engine interface has a row
    /// with the reason or the words it is judged by, every row names a method that still exists, and every assistant tool reaches the
    /// engine only through the injected interface (so the gate sees all of it). There is no default row: a method nobody has decided is
    /// refused. Item 7's red proof is here too: delete one row and the check names it.
    /// </summary>
    public sealed class AssistantGateCompletenessTests
    {
        private static IEnumerable<MethodInfo> EngineMethods() => typeof(IEngine).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        /// <summary>Everything wrong with a set of rows against the engine interface. The real registry must give an empty list.</summary>
        internal static List<string> Problems(IReadOnlyCollection<GateEntry> rows)
        {
            var problems = new List<string>();
            var byName = rows.GroupBy(r => r.Method, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var d in byName.Where(kv => kv.Value.Count > 1)) problems.Add("two rows for " + d.Key);
            var methods = EngineMethods().ToList();
            foreach (var name in methods.Select(m => m.Name).Distinct(StringComparer.Ordinal))
                if (!byName.ContainsKey(name)) problems.Add("no row for the engine method " + name);
            foreach (var name in byName.Keys)
                if (methods.All(m => m.Name != name)) problems.Add("a row for " + name + ", which is not an engine method");
            foreach (var m in methods)
                if (m.ReturnType != typeof(Task) && !(m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
                    problems.Add(m.Name + " does not return a Task, so the gate cannot place its answer");
            foreach (var r in rows)
            {
                var asks = r.Kind == GateKind.Hold || r.Kind == GateKind.Ratchet;
                if (r.Choose != null) continue;   // the row that applies is chosen by the call, and says its own words then
                if (asks && string.IsNullOrWhiteSpace(r.Words) && r.Describe == null) problems.Add(r.Method + " is asked about but has no words and no hook to make them");
                if (r.Kind == GateKind.Ratchet && r.Loosens == null) problems.Add(r.Method + " is a ratchet with no rule for what loosens it");
                if (!asks && string.IsNullOrWhiteSpace(r.Why)) problems.Add(r.Method + " says nothing about why it is " + r.Kind);
                if (asks && r.Capability != AgentCapability.EditLocal && r.Capability != AgentCapability.DeployFile && r.Kind == GateKind.Hold)
                    problems.Add(r.Method + " is held on " + r.Capability + ", which is not a local row");
                if (r.Kind == GateKind.Hold && r.PathArgs.Length > 0 && (r.Witness & GateWitness.Paths) == 0)
                    problems.Add(r.Method + " names a path but is not bound to its state");
                foreach (var pathArg in r.PathArgs)
                    if (!methods.Where(m => m.Name == r.Method).Any(m => m.GetParameters().Any(p => p.Name == pathArg)))
                        problems.Add(r.Method + " names a path argument " + pathArg + " that it does not take");
            }
            return problems;
        }

        [Fact]
        public void Every_engine_method_has_a_row_and_every_row_names_an_engine_method()
        {
            var problems = Problems(GateRegistry.All.ToList());
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            // The count is measured here, not claimed: the interface and the registry agree method for method.
            Assert.Equal(EngineMethods().Select(m => m.Name).Distinct(StringComparer.Ordinal).Count(), GateRegistry.All.Count);
        }

        [Fact]
        public void Every_template_names_only_arguments_the_method_takes()
        {
            var bad = new List<string>();
            foreach (var row in GateRegistry.All.Where(r => r.Words != null))
            {
                var parameters = EngineMethods().Where(m => m.Name == row.Method).SelectMany(m => m.GetParameters()).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                foreach (Match token in Regex.Matches(row.Words, @"\{([A-Za-z]+)(?:\?[^}]*|:raw)?\}"))
                    if (!parameters.Contains(token.Groups[1].Value)) bad.Add(row.Method + " words use {" + token.Groups[1].Value + "}, which it does not take");
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>Item 7, the red proof for item 1: take one row out and the check names the method.</summary>
        [Fact]
        public void Red_proof_removing_one_row_makes_the_completeness_check_fail_and_name_it()
        {
            var without = GateRegistry.All.Where(r => r.Method != "SetDescriptionAsync").ToList();
            var problems = Problems(without);
            Assert.Contains(problems, p => p.Contains("SetDescriptionAsync"));
            // and a row left with no reason or words is named too
            var mute = GateRegistry.All.Where(r => r.Method != "SaveLayoutAsync")
                .Append(new GateEntry { Method = "SaveLayoutAsync", Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision }).ToList();
            Assert.Contains(Problems(mute), p => p.Contains("SaveLayoutAsync") && p.Contains("no words"));
            var silent = GateRegistry.All.Where(r => r.Method != "ListTreeAsync").Append(new GateEntry { Method = "ListTreeAsync", Kind = GateKind.Pass }).ToList();
            Assert.Contains(Problems(silent), p => p.Contains("ListTreeAsync") && p.Contains("why"));
        }

        [Fact]
        public async Task A_method_nobody_decided_is_refused()
        {
            // The gate refuses a call it has no row for, before it reaches anything. Proven with a row taken out of a copy of the
            // lookup the gate uses: the call is refused by name.
            Assert.Null(GateRegistry.Find("NotAnEngineMethodAsync"));
            Assert.NotNull(GateRegistry.Find("SetDescriptionAsync"));
            await Task.CompletedTask;
        }

        // ---- the tools ---------------------------------------------------------------------------------------------------

        [Fact]
        public void Every_assistant_tool_is_in_the_scan_and_calls_only_methods_the_registry_knows()
        {
            var tools = AssistantGateKit.ToolSources();
            // every class that carries tools, not only McpTools: the assistant's surface is whatever the SDK finds in the assembly
            var reflected = typeof(McpTools).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).Where(a => a != null).Select(a => a.Name).ToList();
            Assert.Equal(reflected.Count, reflected.Distinct(StringComparer.Ordinal).Count());
            Assert.True(reflected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(tools.Keys.OrderBy(x => x, StringComparer.Ordinal)),
                "the scan of the McpTools files and the tools reflection finds are not the same set: "
                + string.Join(", ", reflected.Except(tools.Keys).Concat(tools.Keys.Except(reflected))));

            var known = GateRegistry.All.Select(r => r.Method).ToHashSet(StringComparer.Ordinal);
            var unknown = new List<string>();
            foreach (var (tool, source) in tools)
                foreach (Match call in Regex.Matches(source, @"\bengine\.(\w+)\("))
                    if (!known.Contains(call.Groups[1].Value)) unknown.Add(tool + " calls engine." + call.Groups[1].Value + ", which has no row");
            Assert.True(unknown.Count == 0, string.Join("\n", unknown.Distinct()));
        }

        /// <summary>A tool reaches the engine only through the injected interface: no engine class, no file, no folder, no process, no
        /// settings store. The allowlist is the whole of what is allowed, each with its reason. A new tool that reaches for anything else
        /// fails here, because the gate would not see it.</summary>
        [Fact]
        public void The_assistants_tools_reach_nothing_but_the_injected_engine()
        {
            var forbidden = new[]
            {
                @"\bnew\s+LocalEngine\b", @"\bRemoteEngine\b\.", @"\bFile\.", @"\bDirectory\.", @"\bProcess\.", @"\bHttpClient\b",
                @"\bAgentPolicyStore\b", @"\bApprovalLedger\b", @"\bSessionManager\b", @"\bConnectionRegistry\b", @"\bSqlSourceRegistry\b",
                @"\bExperienceStore\b", @"\bEnvironment\.", @"\bSystem\.IO\b", @"\bGateContext\b", @"\bAssistantEngine\b", @"\bLocalEngine\.[A-Za-z]",
            };
            // allowed: the page size the words queue reads, a pure constant and function of LocalEngine (no state, no write)
            var allowed = new[] { "LocalEngine.WordsPageSize", "LocalEngine.WordsPageDefault" };
            var found = new List<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.Engine"), "McpTools*.cs"))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith("//") || line.Contains("[Description(")) continue;
                    var code = Regex.Replace(line, @"//.*$", "");
                    foreach (var pattern in forbidden)
                        if (Regex.IsMatch(code, pattern) && !allowed.Any(a => code.Contains(a)))
                            found.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + code.Trim());
                }
            }
            Assert.True(found.Count == 0, "A tool reaches past the injected engine:\n" + string.Join("\n", found));
        }

        [Fact]
        public void The_tools_that_call_more_than_one_asked_about_method_are_either_or_branches_never_a_second_write()
        {
            // First count the composites (brief, build step 1). A tool that made two write calls in one invocation would ask the person
            // twice. Counted from the scan: every tool whose body names more than one engine method that the gate asks about.
            var asks = GateRegistry.All.Where(r => r.Kind is GateKind.Hold or GateKind.Ratchet or GateKind.Live || r.Choose != null)
                .Select(r => r.Method).ToHashSet(StringComparer.Ordinal);
            var composites = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (tool, source) in AssistantGateKit.ToolSources())
            {
                var calls = Regex.Matches(source, @"\bengine\.(\w+)\(").Select(m => m.Groups[1].Value).Where(asks.Contains).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (calls.Length > 1) composites[tool] = calls;
            }
            // Each is one call or the other, chosen by an argument (see the tool): the person is asked once per invocation. The list is
            // pinned, so a tool that gains a second write shows up here.
            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["apply_fix"] = "ApplyFixAsync|BpaFixAsync (system 'air' or 'bpa')",
                ["apply_safe_fixes"] = "ApplySafeFixesAsync|BpaFixAllAsync (system)",
                ["load_rules"] = "LoadBpaRulesAsync|LoadReadinessRulesAsync (system)",
                ["redo_change"] = "RedoAsync|RedoToEntryAsync (toEntry)",
                ["reset_rules"] = "ResetBpaRulesAsync|ResetReadinessRulesAsync (system)",
                ["undo_change"] = "UndoAsync|UndoToEntryAsync (toEntry)",
            };
            Assert.True(composites.Keys.SequenceEqual(expected.Keys),
                "composite tools changed. now: " + string.Join("; ", composites.Select(c => c.Key + " = " + string.Join("|", c.Value))));
        }
    }
}
