using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// What the assistant-gate tests share (lane m11/uat-undo, the Q5 proof): a rich model on disk, a snapshot of everything a write
    /// could leave a trace in (the model as TOM writes it, as TMDL, both undo stacks, the revision, the model's folder, the engine's
    /// home), and the arguments a "nonsense" call is made with.
    /// </summary>
    /// <summary>The scratch a gate test runs in: a folder, the engine's home, the policy and approvals kept apart from the real ones.
    /// The whole assembly runs serial, so swapping the home variable is race-free.</summary>
    public abstract class GateFixtureBase : IDisposable
    {
        protected readonly string Root;
        protected readonly string Home;
        private readonly string _safeRoot;
        private readonly string _oldHome;

        protected GateFixtureBase(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            Root = Path.Combine(Path.GetTempPath(), "agg-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(Root);
            Home = Path.Combine(Root, "home");
            Directory.CreateDirectory(Home);
            _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Home);
            AgentPolicyStore.RootOverride = Path.Combine(Root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(Root, "policy");
        }

        public virtual void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _oldHome);
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            GateSeams.DisabledForTest.Clear();
            try { Directory.Delete(Root, true); } catch { }
        }

        protected async Task<(LocalEngine engine, SessionManager sessions, string path)> OpenAsync(string tag)
        {
            var path = AssistantGateKit.WriteModel(Root, tag);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            return (engine, sessions, path);
        }

        protected static IEngine Gate(LocalEngine engine) => AssistantEngine.For(engine);

        protected static async Task<ApprovalRecord> OnlyPendingAsync(LocalEngine engine) => System.Linq.Enumerable.Single(await engine.ListPendingApprovalsAsync());

        /// <summary>The person says yes to the one question waiting.</summary>
        protected static async Task ApproveAsync(LocalEngine engine) =>
            await engine.ApproveAgentActionAsync((await OnlyPendingAsync(engine)).Id, "human");

        protected static async Task ClearQuestionsAsync(LocalEngine engine)
        {
            foreach (var p in await engine.ListPendingApprovalsAsync()) await engine.DenyAgentActionAsync(p.Id, "human");
        }

        protected static async Task<InvalidOperationException> RefusedAsync(Func<Task> call)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(call);
            return ex;
        }
    }

    internal static class AssistantGateKit
    {
        public const string Locked = "locked";

        /// <summary>A model with every family a write can reach: tables, a key column, a calculation group, a role, a perspective, a
        /// culture, a hierarchy, a relationship, partitions and two measures.</summary>
        public static string WriteModel(string root, string tag)
        {
            var dir = Path.Combine(root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Gate") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-pi" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Hierarchies.Add(new TOM.Hierarchy { Name = "Geo", LineageTag = "h-geo", Levels = { new TOM.Level { Name = "Region", Column = sales.Columns["Region"], Ordinal = 0, LineageTag = "l-re" }, new TOM.Level { Name = "Code", Column = sales.Columns["Code"], Ordinal = 1, LineageTag = "l-co" } } });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductId", DataType = TOM.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            var cg = new TOM.Table { Name = "Scenario", LineageTag = "t-cg", CalculationGroup = new TOM.CalculationGroup { Precedence = 0 } };
            cg.Columns.Add(new TOM.DataColumn { Name = "Scenario", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-cgn" });
            cg.Partitions.Add(new TOM.Partition { Name = "Scenario", Source = new TOM.CalculationGroupSource() });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Actual", Expression = "SELECTEDMEASURE()", Ordinal = 0 });
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Plan", Expression = "SELECTEDMEASURE() * 2", Ordinal = 1 });
            db.Model.Tables.Add(cg);
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            role.TablePermissions.Add(new TOM.TablePermission { Table = sales, FilterExpression = "[Region] = \"A\"" });
            db.Model.Roles.Add(role);
            db.Model.Perspectives.Add(new TOM.Perspective { Name = "View" });
            var culture = new TOM.Culture { Name = "fr-FR" };
            culture.ObjectTranslations.SetTranslation(sales.Columns["Region"], TOM.TranslatedProperty.Caption, "Zone");
            db.Model.Cultures.Add(culture);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        // ---- what a trace can show ----------------------------------------------------------------------------------

        /// <summary>The model as TOM writes it, the model as TMDL, both undo stacks, the unsaved flag, History length and revision.</summary>
        public static async Task<string> ModelTraceAsync(LocalEngine engine, SessionManager sessions)
        {
            var s = sessions.Current;
            var json = await s.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase, new TOM.SerializeOptions { IgnoreTimestamps = true }));
            var tmdl = await engine.ScriptObjectsAsync(await s.ReadAsync(m => m.Tables.Select(t => "table:" + t.Name).ToArray()), "tmdl");
            var undo = await s.ReadAsync(_ => s.UndoStateNow());
            return json + "\n" + tmdl + "\n" + JsonSerializer.Serialize(undo) + "\nunsaved=" + s.HasUnsavedEdits
                + "\nhistory=" + sessions.Ledger.ForModel(s.Id).Count + "\nrevision=" + s.Revision;
        }

        /// <summary>Every file under a folder, by relative path, with the hash of its bytes. Byte-equal means this is equal.</summary>
        public static Dictionary<string, string> Files(string dir)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) return map;
            using var sha = SHA256.Create();
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { map[Path.GetRelativePath(dir, f)] = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f))); }
                catch (IOException) { map[Path.GetRelativePath(dir, f)] = "unreadable"; }
            }
            return map;
        }

        public static string[] Differences(Dictionary<string, string> before, Dictionary<string, string> after) =>
            before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        // ---- the arguments of a call ---------------------------------------------------------------------------------

        /// <summary>Arguments for a call that means nothing: text "nonsense", a one-item list, every flag that makes a call write turned
        /// on. A few methods only count as a write when an argument has a shape (a script with a block, a diff aimed at a file); those are
        /// the overrides below.</summary>
        public static object[] NonsenseArgs(MethodInfo method, string scratchFile)
        {
            var ps = method.GetParameters();
            var args = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++) args[i] = ValueFor(method.Name, ps[i], scratchFile);
            return args;
        }

        private static object ValueFor(string method, ParameterInfo p, string scratchFile)
        {
            var key = method + "." + p.Name;
            switch (key)
            {
                case "CompleteAssistantRequestAsync.text": return "[{\"op\":\"nonsense\"}]";
                case "ApplyDiffAsync.right": return new ModelRef { Kind = "file", Path = scratchFile };
                case "ApplyDiffAsync.left": return new ModelRef { Kind = "session" };
                case "ModelPlanActionAsync.action": return "start";
                case "UpgradeWorkflowAsync.dryRun": return false;
                case "SetWorkflowEnforcementAsync.mode": return "hard";
                case "SetWorkflowBindingAsync.mode": return "hard";
                case "SetWorkflowBindingAsync.requireNames": return new[] { "nonsense" };
                case "SetWorkflowActivationAsync.set": return "on";
                case "SaveAsync.path": return scratchFile;
                case "ExportVpaxAsync.path": return scratchFile;
                case "SaveSpecAsync.path": return scratchFile;
            }
            var t = p.ParameterType;
            if (t == typeof(string)) return string.Equals(p.Name, "origin", StringComparison.Ordinal) ? "agent" : "nonsense";
            if (t == typeof(string[])) return new[] { "nonsense" };
            if (t == typeof(bool)) return !string.Equals(p.Name, "dryRun", StringComparison.Ordinal);
            if (t == typeof(int)) return 1;
            if (t == typeof(long)) return 1L;
            if (t == typeof(double)) return 1d;
            if (t == typeof(CancellationToken)) return default(CancellationToken);
            if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
            if (Nullable.GetUnderlyingType(t) != null) return null;
            if (t.IsValueType) return Activator.CreateInstance(t);
            return null;
        }

        /// <summary>Call an engine method with the arguments given and await its Task, whatever its result type.</summary>
        public static async Task InvokeAsync(IEngine engine, MethodInfo method, object[] args)
        {
            try { await ((Task)method.Invoke(engine, args)!).ConfigureAwait(false); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        }

        // ---- the source of the assistant's tools -----------------------------------------------------------------------

        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("The repository root (Semanticus.sln) was not found above " + AppContext.BaseDirectory);
        }

        /// <summary>Every method of the assistant's tools (a tool, or a helper a tool calls) in the engine's McpTools files, by method name,
        /// with its signature and body cut out whole (strings and comments skipped, brackets matched).</summary>
        public static Dictionary<string, string> MethodSources()
        {
            var methods = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "Semanticus.Engine"), "McpTools*.cs").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?:public|internal|private)\s+static\s+(?:async\s+)?[\w<>\[\],\.\? ]+?\s+(\w+)\s*\("))
                {
                    var open = m.Index + m.Length - 1;
                    var close = MatchClose(text, open, '(', ')');
                    if (close < 0) continue;
                    var body = BodyEnd(text, close + 1);
                    if (body < 0) continue;
                    var name = m.Groups[1].Value;
                    var chunk = text.Substring(m.Index, body - m.Index + 1);
                    methods[name] = methods.TryGetValue(name, out var earlier) ? earlier + "\n" + chunk : chunk;
                }
            }
            return methods;
        }

        private static int SkipString(string s, int i)
        {
            var verbatim = i > 0 && s[i - 1] == '@' || i > 1 && (s.Substring(i - 2, 2) == "$@" || s.Substring(i - 2, 2) == "@$");
            i++;
            while (i < s.Length)
            {
                var c = s[i];
                if (verbatim)
                {
                    if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; } return i + 1; }
                }
                else
                {
                    if (c == '\\') { i += 2; continue; }
                    if (c == '"') return i + 1;
                }
                i++;
            }
            return i;
        }

        private static int MatchClose(string s, int i, char open, char close)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"') { i = SkipString(s, i); continue; }
                if (c == '\'') { var j = s.IndexOf('\'', i + 1 + (i + 1 < s.Length && s[i + 1] == '\\' ? 1 : 0)); i = j < 0 ? s.Length : j + 1; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { var j = s.IndexOf('\n', i); i = j < 0 ? s.Length : j; continue; }
                if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return i; }
                i++;
            }
            return -1;
        }

        /// <summary>From just after a signature: the end of a block body, or of an expression body.</summary>
        private static int BodyEnd(string s, int i)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s.Substring(i, Math.Min(400, s.Length - i)), @"^(?:\s|//[^\n]*\n)*(=>|\{)");
            if (!m.Success) return -1;
            var at = i + m.Length - 1;
            if (m.Groups[1].Value == "{") return MatchClose(s, at, '{', '}');
            var depth = 0;
            for (var k = at + 2; k < s.Length; k++)
            {
                var c = s[k];
                if (c == '"') { k = SkipString(s, k) - 1; continue; }
                if (c == '\'') { var j = s.IndexOf('\'', k + 1 + (k + 1 < s.Length && s[k + 1] == '\\' ? 1 : 0)); k = j < 0 ? s.Length : j; continue; }
                if (c == '/' && k + 1 < s.Length && s[k + 1] == '/') { var j = s.IndexOf('\n', k); k = j < 0 ? s.Length : j; continue; }
                if (c == '(' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '}' || c == ']') depth--;
                else if (c == ';' && depth == 0) return k;
            }
            return -1;
        }

        /// <summary>The assistant's tools by tool name, each with the text of its method and of every helper it calls (a call that hands
        /// the engine on), so what the tool does to the engine is all in one string.</summary>
        public static Dictionary<string, string> ToolSources()
        {
            var methods = MethodSources();
            string WithHelpers(string name, HashSet<string> seen)
            {
                if (!methods.TryGetValue(name, out var text) || !seen.Add(name)) return "";
                var all = text;
                foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(text, @"\b([A-Z]\w*)\s*\(\s*engine\b"))
                    if (call.Groups[1].Value != name) all += "\n" + WithHelpers(call.Groups[1].Value, seen);
                return all;
            }
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "Semanticus.Engine"), "McpTools*.cs").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match mark in System.Text.RegularExpressions.Regex.Matches(text, @"\[McpServerTool\(\s*Name\s*=\s*""(\w+)"""))
                {
                    var sig = System.Text.RegularExpressions.Regex.Match(text.Substring(mark.Index), @"public\s+static\s+(?:async\s+)?[\w<>\[\],\.\? ]+?\s+(\w+)\s*\(");
                    if (!sig.Success) continue;
                    tools[mark.Groups[1].Value] = WithHelpers(sig.Groups[1].Value, new HashSet<string>(StringComparer.Ordinal));
                }
            }
            return tools;
        }
    }
}
