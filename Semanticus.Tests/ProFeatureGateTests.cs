using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Coverage of EVERY classified entry point, not a sample. For all 142 Pro operations the free tier is
    /// refused with the one feature sentence, on all three paths a call can take: the MCP tool, the RPC entry, and
    /// the IEngine method both doors share. Pro reaches the implementation on all three.
    ///
    /// A map-membership test plus one operation per feature proves the map exists, not that it is enforced, so this
    /// walks the whole surface by reflection: a new Pro operation with no gate fails here by name.</summary>
    public sealed class ProFeatureGateTests : IDisposable
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "semanticus-gate-" + Guid.NewGuid().ToString("N"));

        public ProFeatureGateTests() => Directory.CreateDirectory(_workspace);
        public void Dispose() { try { Directory.Delete(_workspace, true); } catch { } }

        private LocalEngine Engine(bool pro) => new LocalEngine(new SessionManager(), new Fake(pro), _workspace);

        // ---- reflection plumbing -------------------------------------------------------------------------------

        private static object Blank(ParameterInfo p)
        {
            if (p.HasDefaultValue) return p.DefaultValue;
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }

        private static object[] ArgsFor(MethodInfo m, object firstEngineArg)
        {
            var ps = m.GetParameters();
            var a = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++)
                a[i] = i == 0 && firstEngineArg != null ? firstEngineArg : Blank(ps[i]);
            return a;
        }

        /// <summary>Invoke and settle, returning the exception the caller would see. Reflection wraps a synchronous
        /// throw in TargetInvocationException and an async throw lands on the Task, so both are unwrapped here.</summary>
        private static async Task<Exception> CallAsync(MethodInfo m, object target, object[] args)
        {
            object result;
            try { result = m.Invoke(target, args); }
            catch (TargetInvocationException tie) { return tie.InnerException ?? tie; }
            catch (Exception ex) { return ex; }

            try
            {
                if (result is Task t) await t.ConfigureAwait(false);
                else if (result is ValueTask vt) await vt.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) { return ex; }
        }

        private static MethodInfo[] McpToolMethods() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttributes(true).Any(a => a.GetType().Name == "McpServerToolAttribute"))
                .ToArray();

        private static string ToolName(MethodInfo m)
        {
            var a = m.GetCustomAttributes(true).First(x => x.GetType().Name == "McpServerToolAttribute");
            return (string)a.GetType().GetProperty("Name")?.GetValue(a);
        }

        private static void AssertRefusal(Exception ex, string what, string expectedTab, List<string> failures)
        {
            if (ex is EntitlementException e)
            {
                if (!e.Message.Contains("is a Semanticus Pro feature."))
                    failures.Add(what + ": refused, but not with the feature template: " + e.Message);
                else if (!e.Message.StartsWith(expectedTab + " is a Semanticus Pro feature.", StringComparison.Ordinal))
                    failures.Add(what + ": refused naming the wrong tab (wanted '" + expectedTab + "'): " + e.Message);
                else if (!e.Message.EndsWith("Unlock Pro from Pro Plans and Support.", StringComparison.Ordinal))
                    failures.Add(what + ": refused without the unlock sentence: " + e.Message);
                return;
            }
            failures.Add(what + ": free was NOT refused. Got " + (ex == null ? "no exception at all" : ex.GetType().Name + ": " + ex.Message));
        }

        // ---- door 1: the MCP tool ------------------------------------------------------------------------------

        [Fact]
        public async Task Free_is_refused_every_Pro_operation_through_the_MCP_tool()
        {
            using var engine = Engine(pro: false);
            var failures = new List<string>();
            foreach (var m in McpToolMethods().OrderBy(ToolName, StringComparer.Ordinal))
            {
                var name = ToolName(m);
                if (!FeatureMap.TryOperation(name, out _, out var tab)) continue;
                var ex = await CallAsync(m, null, ArgsFor(m, engine));
                AssertRefusal(ex, "MCP " + name, tab, failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public async Task Pro_reaches_the_implementation_through_the_MCP_tool()
        {
            using var engine = Engine(pro: true);
            var failures = new List<string>();
            foreach (var m in McpToolMethods().OrderBy(ToolName, StringComparer.Ordinal))
            {
                var name = ToolName(m);
                if (!FeatureMap.TryOperation(name, out _, out _)) continue;
                var ex = await CallAsync(m, null, ArgsFor(m, engine));
                if (ex is EntitlementException e) failures.Add("MCP " + name + " refused Pro: " + e.Message);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ---- door 2: the RPC entry -----------------------------------------------------------------------------

        [Fact]
        public async Task Free_is_refused_every_Pro_operation_through_EngineRpcTarget()
        {
            using var engine = Engine(pro: false);
            var target = new EngineRpcTarget(engine);
            var failures = new List<string>();
            foreach (var m in ProFeatureMapTests.RpcEntries())
            {
                if (!FeatureMap.TryRpcMethod(m.Name, out _, out var tab)) continue;
                var ex = await CallAsync(m, target, ArgsFor(m, null));
                AssertRefusal(ex, "RPC " + m.Name, tab, failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public async Task Pro_reaches_the_implementation_through_EngineRpcTarget()
        {
            using var engine = Engine(pro: true);
            var target = new EngineRpcTarget(engine);
            var failures = new List<string>();
            foreach (var m in ProFeatureMapTests.RpcEntries())
            {
                if (!FeatureMap.TryRpcMethod(m.Name, out _, out _)) continue;
                var ex = await CallAsync(m, target, ArgsFor(m, null));
                if (ex is EntitlementException e) failures.Add("RPC " + m.Name + " refused Pro: " + e.Message);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ---- the shared implementation both doors reach ---------------------------------------------------------

        [Fact]
        public async Task Free_is_refused_every_Pro_operation_on_the_engine_itself()
        {
            using var engine = Engine(pro: false);
            var failures = new List<string>();
            foreach (var m in ProFeatureMapTests.EngineMethods())
            {
                if (!FeatureMap.TryEngineMethod(m.Name, out _, out var tab)) continue;
                var ex = await CallAsync(m, engine, ArgsFor(m, null));
                AssertRefusal(ex, "IEngine " + m.Name, tab, failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public async Task Pro_reaches_the_implementation_on_the_engine_itself()
        {
            using var engine = Engine(pro: true);
            var failures = new List<string>();
            foreach (var m in ProFeatureMapTests.EngineMethods())
            {
                if (!FeatureMap.TryEngineMethod(m.Name, out _, out _)) continue;
                var ex = await CallAsync(m, engine, ArgsFor(m, null));
                if (ex is EntitlementException e) failures.Add("IEngine " + m.Name + " refused Pro: " + e.Message);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ---- the refusal happens BEFORE any protected work ------------------------------------------------------

        /// <summary>Refusing late is the bug Astra found: run_tests reached its gate after evaluating the suite and
        /// apply_model_diff after preview and fence work. A whole free sweep of all 142 operations must leave no
        /// session open and write nothing at all into the workspace, so nothing was computed, connected or saved.</summary>
        [Fact]
        public async Task A_whole_free_sweep_opens_no_session_and_writes_no_file()
        {
            using var engine = Engine(pro: false);
            foreach (var m in ProFeatureMapTests.EngineMethods())
                if (FeatureMap.TryEngineMethod(m.Name, out _, out _))
                    await CallAsync(m, engine, ArgsFor(m, null));

            var info = await engine.SessionInfoAsync();
            Assert.True(info == null || string.IsNullOrEmpty(info.ModelName),
                "A refused sweep opened a model session: " + info?.ModelName);
            var written = Directory.GetFileSystemEntries(_workspace, "*", SearchOption.AllDirectories);
            Assert.True(written.Length == 0,
                "A refused sweep wrote into the workspace: " + string.Join(", ", written));
        }

        // ---- reconnect and unknown entitlement -------------------------------------------------------------------

        /// <summary>An engine handed no entitlement at all is the "unknown" state a reconnect passes through. It must
        /// resolve to free and refuse, never to Pro by accident.</summary>
        [Fact]
        public async Task An_absent_entitlement_is_treated_as_free_and_refuses()
        {
            using var engine = new LocalEngine(new SessionManager(), null, _workspace);
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => engine.ListWorkflowsAsync());
            Assert.Contains("is a Semanticus Pro feature.", ex.Message);
        }

        /// <summary>A licence activation restarts the engine, so the same operation that was refused now runs. The
        /// grant is read from one function, so this is the whole of "reconnect".</summary>
        [Fact]
        public async Task After_activation_the_same_operation_is_allowed()
        {
            using (var free = Engine(pro: false))
                await Assert.ThrowsAsync<EntitlementException>(() => free.ListWorkflowsAsync());
            using var pro = Engine(pro: true);
            var ex = await CallAsync(typeof(IEngine).GetMethod(nameof(IEngine.ListWorkflowsAsync)), pro, Array.Empty<object>());
            Assert.False(ex is EntitlementException, "Pro was still refused after activation: " + ex?.Message);
        }

        /// <summary>The grace window and the dev escape carry all four features, so neither drops a paying or a
        /// building user onto the free gate.</summary>
        [Fact]
        public void Grace_and_dev_Pro_carry_all_four_features()
        {
            var dev = LicenseEntitlement.DevPro();
            Assert.Equal(4, FeatureGrants.For(dev).Count);
            foreach (var f in ProFeatures.All) Assert.True(FeatureGrants.Grants(dev, f), f.ToString());
        }

        [Fact]
        public void Free_grants_nothing()
        {
            var free = new Fake(false);
            Assert.Empty(FeatureGrants.For(free));
            foreach (var f in ProFeatures.All) Assert.False(FeatureGrants.Grants(free, f), f.ToString());
        }
    }
}
