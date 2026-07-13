using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Direct public MCP-door contracts for offline reads. A read may inspect a model or its sidecars,
    /// but it must never advance the shared revision or broadcast model/didChange.</summary>
    [Collection("restore-root")]
    public sealed class PublicReadMcpTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sem-public-reads-" + Guid.NewGuid().ToString("N"));
        private readonly string _safeApprovalRoot;

        public PublicReadMcpTests(RestoreRootFixture fixture)
        {
            _safeApprovalRoot = fixture.Root;
            ApprovalLedger.RootOverride = _dir;
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            ApprovalLedger.RootOverride = _safeApprovalRoot;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private sealed class Tier : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Tier(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task Named_expression_and_reference_tree_reads_are_exact_failure_honest_and_model_read_only()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Tier(pro: true));
            await engine.CreateModelAsync("PublicReads", 1604);
            const string m = "let Source = 42 in Source";
            await McpTools.CreateNamedExpression(engine, "Shared Source", m);
            var table = await McpTools.CreateTable(engine, "Sales");
            await McpTools.CreateMeasure(engine, table, "Total", "SUM(Sales[Amount])");
            await McpTools.CreateCalculatedColumn(engine, table, "Band", "\"All\"");

            var expression = await ReadOnlyAsync(sessions, () => McpTools.GetNamedExpression(engine, "shared source"));
            Assert.Equal(m, expression);

            await AssertReadOnlyFailureAsync<InvalidOperationException>(sessions,
                () => McpTools.GetNamedExpression(engine, "Missing"), "No shared expression named 'Missing'");

            var nodes = await ReadOnlyAsync(sessions, () => McpTools.GetReferenceTree(engine));
            Assert.Contains(nodes, node => node.Ref == table && node.Name == "Sales" && node.Kind == "table" && node.HasChildren);
            Assert.Contains(nodes, node => node.Name == "Total" && node.Kind == "measure" && !node.HasChildren);
            Assert.Contains(nodes, node => node.Name == "Band" && node.Kind == "calcColumn" && !node.HasChildren);

            var missingFile = Path.Combine(_dir, "missing.bim");
            await AssertReadOnlyFailureAsync<Exception>(sessions,
                () => McpTools.GetReferenceTree(engine, missingFile), "missing.bim");
        }

        [Fact]
        public async Task Dax_lint_returns_known_findings_through_the_public_door_without_model_changes()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Tier(pro: true));
            await engine.CreateModelAsync("LintRead", 1604);

            var result = await ReadOnlyAsync(sessions, () => McpTools.LintDax(engine, "IFERROR([Sales], 0)"));
            var finding = Assert.Single(result.Findings, item => item.RuleId == "avoid-iferror");
            Assert.Equal("performance", finding.Category);
            Assert.Equal("warning", finding.Severity);

            var clean = await ReadOnlyAsync(sessions, () => McpTools.LintDax(engine, "DIVIDE([Sales], [Units])"));
            Assert.Empty(clean.Findings);
        }

        [Fact]
        public async Task Pending_approval_list_returns_the_isolated_queue_without_model_changes()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Tier(pro: true));
            await engine.CreateModelAsync("ApprovalRead", 1604);
            var requested = ApprovalLedger.Request(AgentCapability.QueryData, "prod",
                ApprovalLedger.IntentHash("server", "dataset"), "Read rows for a test", "dataset on server");

            var approvals = await ReadOnlyAsync(sessions, () => McpTools.ListPendingApprovals(engine));

            var approval = Assert.Single(approvals);
            Assert.Equal(requested.Id, approval.Id);
            Assert.Equal("QueryData", approval.Capability);
            Assert.Equal("prod", approval.Label);
            Assert.Equal("Read rows for a test", approval.Summary);
            Assert.Null(approval.GrantedUtc);
            Assert.Equal(ApprovalLedger.RootOverride, _dir);
        }

        [Fact]
        public async Task Test_definitions_and_history_are_publicly_readable_with_an_honest_free_tier_boundary()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Tier(pro: true), _dir);
            await engine.CreateModelAsync("TestRead", 1604);
            sessions.Current.SourcePath = Path.Combine(_dir, "test-read.bim");
            var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.RowLevelAssertion,
                Title = "Regional access stays scoped",
                TargetRef = "role:Regional",
            }, "human");

            var suite = await ReadOnlyAsync(sessions, () => McpToolsTesting.ListTests(engine));
            var listed = Assert.Single(suite.Definitions);
            Assert.Equal(stored.Id, listed.Id);
            Assert.Equal("Regional access stays scoped", listed.Title);
            Assert.Equal(TestKinds.RowLevelAssertion, listed.Kind);
            Assert.Equal(0, suite.UnreadableLines);

            var run = await ReadOnlyAsync(sessions, () => McpToolsTesting.RunTests(engine, persist: true));
            Assert.True(run.Persisted);
            var history = await ReadOnlyAsync(sessions, () => McpToolsTesting.ListTestRuns(engine, last: 1));
            var listedRun = Assert.Single(history.Runs);
            Assert.Equal(run.RunId, listedRun.RunId);
            Assert.Equal(run.ModelFingerprint, listedRun.ModelFingerprint);

            using var freeSessions = new SessionManager();
            using var freeEngine = new LocalEngine(freeSessions, new Tier(pro: false), _dir);
            await freeEngine.CreateModelAsync("FreeHistory", 1604);
            freeSessions.Current.SourcePath = Path.Combine(_dir, "free-history.bim");
            var free = await ReadOnlyAsync(freeSessions, () => McpToolsTesting.ListTestRuns(freeEngine));
            Assert.Empty(free.Runs);
            Assert.Contains("history and drift trends are Pro", free.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("run_tests itself is free", free.Note, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<T> ReadOnlyAsync<T>(SessionManager sessions, Func<Task<T>> action)
        {
            var revision = sessions.Current.Revision;
            var seen = 0;
            void Handler(ChangeNotification notification) => Interlocked.Increment(ref seen);
            sessions.Bus.Changed += Handler;
            try
            {
                var result = await action();
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(0, Volatile.Read(ref seen));
                return result;
            }
            finally { sessions.Bus.Changed -= Handler; }
        }

        private static async Task AssertReadOnlyFailureAsync<TException>(
            SessionManager sessions, Func<Task> action, string message)
            where TException : Exception
        {
            var revision = sessions.Current.Revision;
            var seen = 0;
            void Handler(ChangeNotification notification) => Interlocked.Increment(ref seen);
            sessions.Bus.Changed += Handler;
            try
            {
                var error = await Assert.ThrowsAnyAsync<TException>(action);
                Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(0, Volatile.Read(ref seen));
            }
            finally { sessions.Bus.Changed -= Handler; }
        }
    }
}
