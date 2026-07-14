using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>T111 anchors the published-report list at both public doors without requiring a live tenant.
    /// Invalid input must fail before authentication and leave the shared model session untouched.</summary>
    public sealed class CloudReportPublicDoorTests
    {
        [Fact]
        public async Task List_reports_public_doors_reject_a_missing_workspace_before_auth_and_preserve_the_session()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("Report list contract", 1604);
            var rpc = new EngineRpcTarget(engine);
            var originalSession = sessions.Current;
            var revision = originalSession.Revision;
            var modelChanges = 0;
            var activities = new List<ActivityEvent>();
            sessions.Bus.Changed += _ => modelChanges++;
            sessions.Bus.Activity += activities.Add;

            var mcpFailure = await Assert.ThrowsAsync<ArgumentException>(() =>
                McpTools.ListReports(engine, " ", authMode: "token"));
            Assert.Contains("workspace id", mcpFailure.Message, StringComparison.OrdinalIgnoreCase);

            var rpcFailure = await Assert.ThrowsAsync<ArgumentException>(() =>
                rpc.listReports("\t", authMode: "token"));
            Assert.Contains("workspace id", rpcFailure.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Same(originalSession, sessions.Current);
            Assert.Equal(revision, sessions.Current.Revision);
            Assert.Equal(0, modelChanges);
            Assert.Empty(activities);
        }
    }
}
