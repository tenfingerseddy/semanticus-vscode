using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// C2.2 (D-014 / D-017 / D-021): a cancelled or stuck sign-in must stop waiting, name what happened,
    /// and leave a support id. An expired live token retries once in silence, then speaks plainly.
    /// No live tenant and no browser: the wait is a fake Task, the query is a ForTest stub.
    /// </summary>
    public sealed class SignInWaitTests : IDisposable
    {
        private readonly Action<string> _prevLog;

        public SignInWaitTests()
        {
            _prevLog = SignInWait.LogForTests;
        }

        public void Dispose()
        {
            SignInWait.LogForTests = _prevLog;
        }

        [Fact]
        public async Task Cancelled_token_names_the_cancel_and_stops()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var logs = CaptureLog();

            var ex = await Assert.ThrowsAsync<SignInException>(
                () => SignInWait.RunAsync(_ => Task.FromResult(1), cts.Token, TimeSpan.FromSeconds(5)));

            Assert.Equal(SignInWait.Outcome.Cancelled, ex.Outcome);
            Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ex.SupportId, ex.Message);
            Assert.Contains("cancelled", string.Join("\n", logs), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ex.SupportId, string.Join("\n", logs));
        }

        [Fact]
        public async Task Timeout_names_timeout_and_stops_without_waiting_the_full_work()
        {
            var logs = CaptureLog();
            var started = DateTime.UtcNow;

            var ex = await Assert.ThrowsAsync<SignInException>(() => SignInWait.RunAsync(async ct =>
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    return await tcs.Task;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(40)));

            Assert.True((DateTime.UtcNow - started).TotalSeconds < 2, "timeout must not wait for the 10 minute page guard");
            Assert.Equal(SignInWait.Outcome.TimedOut, ex.Outcome);
            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ex.SupportId, ex.Message);
            Assert.Contains("timed out", string.Join("\n", logs), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Success_returns_the_value_and_logs_ok()
        {
            var logs = CaptureLog();
            var n = await SignInWait.RunAsync(_ => Task.FromResult(7), CancellationToken.None, TimeSpan.FromSeconds(1));
            Assert.Equal(7, n);
            Assert.Contains("ok", string.Join("\n", logs), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Failure_scrubs_secrets_and_keeps_an_id()
        {
            const string leak = "Authentication failed for all authenticators. RootActivityId f789b9a8-cb54-4353-a2f5-413e75b74075";
            var logs = CaptureLog();

            var ex = await Assert.ThrowsAsync<SignInException>(
                () => SignInWait.RunAsync<int>(_ => throw new InvalidOperationException(leak), CancellationToken.None, TimeSpan.FromSeconds(1)));

            Assert.Equal(SignInWait.Outcome.Failed, ex.Outcome);
            Assert.DoesNotContain("f789b9a8", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RootActivityId", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Support id:", ex.Message);
            Assert.All(logs, line =>
            {
                Assert.DoesNotContain("f789b9a8", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("RootActivityId", line, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains(ex.SupportId, string.Join("\n", logs));
        }

        [Fact]
        public async Task User_cancel_exception_is_not_called_a_timeout()
        {
            var ex = await Assert.ThrowsAsync<SignInException>(
                () => SignInWait.RunAsync<int>(
                    _ => throw new MsalStubException("authentication_canceled: user canceled the sign-in"),
                    CancellationToken.None, TimeSpan.FromSeconds(5)));

            Assert.Equal(SignInWait.Outcome.Cancelled, ex.Outcome);
            Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class MsalStubException : Exception { public MsalStubException(string m) : base(m) { } }

        [Fact]
        public void Support_id_in_the_message_matches_the_log()
        {
            var logs = CaptureLog();
            var id = SignInWait.NewId();
            SignInWait.Log(SignInWait.Outcome.Cancelled, id);
            var line = Assert.Single(logs);
            Assert.Contains(id, line);
            Assert.Equal(XmlaAuthHint.CancelledHint(id), new SignInException(SignInWait.Outcome.Cancelled, id, XmlaAuthHint.CancelledHint(id)).Message);
        }

        [Fact]
        public async Task Expired_token_is_rewritten_when_silent_renew_fails()
        {
            using var engine = new LocalEngine(new SessionManager());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Sales",
                _ => new ResultSet
                {
                    Error = "Authentication failed for all authenticators. Technical Details: RootActivityId: f789b9a8-cb54-4353-a2f5-413e75b74075",
                    AuthFailed = true,
                }));
            engine.SilentQueryRenewForTests = () => Task.FromResult(false);

            var rs = await engine.RunDaxAsync("EVALUATE ROW(\"v\", 1)", 10);

            Assert.True(rs.AuthFailed);
            Assert.DoesNotContain("RootActivityId", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("f789b9a8", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expired", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sign in again", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Support id:", rs.Error ?? "");
        }

        [Fact]
        public async Task Expired_token_retries_silently_once()
        {
            var n = 0;
            using var engine = new LocalEngine(new SessionManager());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Sales", _ =>
            {
                n++;
                if (n == 1)
                    return new ResultSet
                    {
                        Error = "Authentication failed for all authenticators. Technical Details: RootActivityId: f789b9a8-cb54-4353-a2f5-413e75b74075",
                        AuthFailed = true,
                    };
                return new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "v" } },
                    Rows = new[] { new object[] { 1 } },
                    RowCount = 1,
                };
            }));
            engine.SilentQueryRenewForTests = () => Task.FromResult(true);

            var rs = await engine.RunDaxAsync("EVALUATE ROW(\"v\", 1)", 10);

            Assert.True(string.IsNullOrEmpty(rs.Error));
            Assert.False(rs.AuthFailed);
            Assert.Equal(2, n);
            Assert.Equal(1, rs.RowCount);
        }

        [Fact]
        public async Task Expired_token_on_preview_asks_to_sign_in_again()
        {
            using var engine = new LocalEngine(new SessionManager());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Sales",
                _ => new ResultSet
                {
                    Error = "Authentication failed for all authenticators. Technical Details: RootActivityId: f789b9a8-cb54-4353-a2f5-413e75b74075",
                    AuthFailed = true,
                }));
            engine.SilentQueryRenewForTests = () => Task.FromResult(false);

            var rs = await engine.PreviewTableAsync("Sales", 10);

            Assert.True(rs.AuthFailed);
            Assert.DoesNotContain("RootActivityId", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sign in again", rs.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Support id:", rs.Error ?? "");
        }

        [Fact]
        public async Task Cancelled_review_signin_keeps_cancelled_wording()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-c22-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var p = Path.Combine(dir, "model.bim");
                File.WriteAllText(p, TinyBim());
                using var engine = new LocalEngine(new SessionManager());
                const string id = "ab12cd34";
                engine.WorkspaceTokenExportForTests = _ => throw new SignInException(
                    SignInWait.Outcome.Cancelled, id, XmlaAuthHint.CancelledHint(id));

                var ex = await Assert.ThrowsAsync<SignInException>(() => engine.CompareModelsAsync(
                    new ModelRef { Kind = "file", Path = p },
                    new ModelRef { Kind = "workspace", Endpoint = "powerbi://example/ws", Database = "Sales" },
                    origin: "human"));

                Assert.Equal(SignInWait.Outcome.Cancelled, ex.Outcome);
                Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(id, ex.Message);
                Assert.DoesNotContain("different tenant", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task Timed_out_review_signin_keeps_timeout_wording()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-c22-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var p = Path.Combine(dir, "model.bim");
                File.WriteAllText(p, TinyBim());
                using var engine = new LocalEngine(new SessionManager());
                const string id = "ef56gh78";
                engine.WorkspaceTokenExportForTests = _ => throw new SignInException(
                    SignInWait.Outcome.TimedOut, id, XmlaAuthHint.TimedOutHint(id));

                var ex = await Assert.ThrowsAsync<SignInException>(() => engine.CompareModelsAsync(
                    new ModelRef { Kind = "file", Path = p },
                    new ModelRef { Kind = "workspace", Endpoint = "powerbi://example/ws", Database = "Sales" },
                    origin: "human"));

                Assert.Equal(SignInWait.Outcome.TimedOut, ex.Outcome);
                Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(id, ex.Message);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string TinyBim()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            db.Model.Tables.Add(t);
            return TOM.JsonSerializer.SerializeDatabase(db);
        }

        private static List<string> CaptureLog()
        {
            var logs = new List<string>();
            SignInWait.LogForTests = logs.Add;
            return logs;
        }
    }
}
