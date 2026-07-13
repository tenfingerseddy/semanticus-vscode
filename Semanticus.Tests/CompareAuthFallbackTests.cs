using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Issue #156 (+ the review of PR #163): a Compare/apply/cherry-pick against an XMLA target with an empty token
    /// cache must NOT surface AMO's raw "Authentication failed for all authenticators / RootActivityId". Instead:
    ///   • a HUMAN routes through the SAME interactive sign-in connect_xmla uses, then retries EXACTLY once;
    ///   • an AGENT-origin call NEVER pops UI on ANY entry path (compare, apply, cherry_pick) — teaching refusal;
    ///   • the sign-in-still-fails error teaches the fix and leaks NO secret (endpoint scrubbed, mode a fixed label);
    ///   • the interview probes classify auth off a TYPED marker, so a DAX error keeps its "fix the DAX" advice.
    /// Driven through the WorkspaceTokenExportForTests seam — no XMLA endpoint is touched.
    /// </summary>
    [Collection("restore-root")]   // the commit-path test writes a restore point; keep it off the real home
    public sealed class CompareAuthFallbackTests : IDisposable
    {
        private readonly string _root;

        public CompareAuthFallbackTests(RestoreRootFixture _)
        {
            // Joins the restore-root fixture and gives every test its own filesystem boundary. Workspace snapshot
            // cleanup deletes the snapshot's parent directory by contract, so a .bim directly under %TEMP% would
            // delete the shared temp root and make the rest of the class depend on execution order.
            _root = Path.Combine(Path.GetTempPath(), "sem-156-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private const string AmoColdAuthError = "Authentication failed for all authenticators. Technical Details: RootActivityId: 00000000-0000-0000-0000-000000000000 Date (UTC): 2026-07-10";

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static TOM.Database Db(string totalExpr)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = totalExpr, LineageTag = "m-total" });
            db.Model.Tables.Add(t);
            return db;
        }
        // A live-snapshot .bim on disk that the seam can hand back as if it had been exported from the endpoint.
        private LiveModelExport.Snapshot Snap(TOM.Database db)
        {
            var dir = Path.Combine(_root, "snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "model.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return new LiveModelExport.Snapshot { BimPath = p, DatabaseName = "DS", DatabaseCount = 1, DatabaseNames = new[] { "DS" } };
        }
        private ModelRef FileRef(TOM.Database db)
        {
            var dir = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "model.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return new ModelRef { Kind = "file", Path = p };
        }
        private static ModelRef Ws(string authMode = null) => new() { Kind = "workspace", Endpoint = Endpoint, Database = "DS", AuthMode = authMode };

        // ---- HUMAN compare: a cold auth failure signs in interactively and retries ONCE, succeeding ----
        [Fact]
        public async Task Human_compare_with_no_token_falls_back_to_interactive_signin_and_succeeds()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode =>
            {
                modes.Add(mode);
                if (mode == "interactive") return Task.FromResult(Snap(Db("2")));
                throw new InvalidOperationException(AmoColdAuthError);
            };

            var diff = await engine.CompareModelsAsync(FileRef(Db("1")), Ws(), origin: "human");

            Assert.True(string.IsNullOrEmpty(diff.Error));
            Assert.Equal(new[] { "azcli", "interactive" }, modes);   // first attempt, then EXACTLY one interactive retry
            Assert.True(diff.Updated >= 1);
        }

        // ---- AGENT compare: the cold failure NEVER pops UI — teaching refusal, no interactive retry ----
        [Fact]
        public async Task Agent_compare_with_no_token_is_refused_and_never_opens_interactive_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(AmoColdAuthError); };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.CompareModelsAsync(FileRef(Db("1")), Ws(), origin: "agent"));

            Assert.Contains("connect_xmla", ex.Message);
            Assert.Contains("Not signed in", ex.Message);
            Assert.DoesNotContain("RootActivityId", ex.Message);
            Assert.Equal(new[] { "azcli" }, modes);                  // ONLY the first attempt — no browser for an agent
        }

        // ---- AGENT via APPLY (source = workspace): origin is threaded, no interactive sign-in ----
        [Fact]
        public async Task Agent_apply_with_workspace_source_is_refused_and_never_opens_interactive_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(AmoColdAuthError); };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.ApplyDiffAsync(Ws(), FileRef(Db("1")), null, commit: false, origin: "agent"));

            Assert.Contains("connect_xmla", ex.Message);
            Assert.Equal(new[] { "azcli" }, modes);                  // agent origin survived the apply path — no UI
        }

        // ---- AGENT via CHERRY_PICK (source = workspace): origin is threaded, no interactive sign-in ----
        [Fact]
        public async Task Agent_cherrypick_with_workspace_source_is_refused_and_never_opens_interactive_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(AmoColdAuthError); };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.CherryPickAsync(Ws(), new[] { "measure:Sales/Total" }, includeDependencies: false, commit: false, origin: "agent"));

            Assert.Contains("connect_xmla", ex.Message);
            Assert.Equal(new[] { "azcli" }, modes);
        }

        // ---- HUMAN via APPLY (source = workspace): origin threaded the OTHER way — the fallback DOES fire ----
        [Fact]
        public async Task Human_apply_with_workspace_source_falls_back_to_interactive()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode =>
            {
                modes.Add(mode);
                if (mode == "interactive") return Task.FromResult(Snap(Db("2")));
                throw new InvalidOperationException(AmoColdAuthError);
            };

            var r = await engine.ApplyDiffAsync(Ws(), FileRef(Db("1")), null, commit: false, origin: "human");

            Assert.True(string.IsNullOrEmpty(r.Error));
            Assert.Equal(new[] { "azcli", "interactive" }, modes);   // human apply reached the interactive fallback
        }

        // ---- HUMAN: when the interactive sign-in ALSO fails, the error teaches (never the raw AMO string) ----
        [Fact]
        public async Task Human_compare_teaches_when_signin_still_fails()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            engine.WorkspaceTokenExportForTests = _ => throw new InvalidOperationException(AmoColdAuthError);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.CompareModelsAsync(FileRef(Db("1")), Ws(), origin: "human"));

            Assert.Contains("Run Connect", ex.Message);
            Assert.Contains("different tenant", ex.Message);
            Assert.Contains("interactive", ex.Message);              // echoes the auth mode it actually tried
            Assert.DoesNotContain("RootActivityId", ex.Message);
        }

        // ---- serviceprincipal has NO interactive stand-in: teach WITHOUT claiming a sign-in was attempted ----
        [Fact]
        public async Task Human_compare_serviceprincipal_teaches_without_claiming_a_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(AmoColdAuthError); };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.CompareModelsAsync(FileRef(Db("1")), Ws("serviceprincipal"), origin: "human"));

            Assert.Contains("does not open an interactive sign-in", ex.Message);   // honest: no browser was opened
            Assert.DoesNotContain("tried to sign you in", ex.Message);
            Assert.Equal(new[] { "serviceprincipal" }, modes);                     // one attempt, no browser fallback
        }

        // ---- SECURITY (P1-2): a connection-string / token in the endpoint or mode NEVER survives into the message ----
        [Fact]
        public void Teaching_copy_scrubs_secrets_from_endpoint_and_mode()
        {
            const string secretEp = "powerbi://api.powerbi.com/v1.0/myorg/WS;Password=SUPERSECRET;Initial Catalog=DS";
            const string secretMode = "azcli;access_token=eyJTOKENLEAK";
            foreach (var msg in new[]
            {
                XmlaAuthHint.TeachingErrorAfterSignIn(secretEp, secretMode, "t"),
                XmlaAuthHint.TeachingErrorNoInteractive(secretEp, secretMode, "t"),
                XmlaAuthHint.AgentRefusal(secretEp, secretMode, "t"),
            })
            {
                Assert.DoesNotContain("SUPERSECRET", msg);
                Assert.DoesNotContain("eyJTOKENLEAK", msg);
                Assert.DoesNotContain("Password=", msg);   // the whole connection-string tail is dropped
            }
            // The endpoint address itself is preserved (it is not a secret and helps the user).
            Assert.Contains("powerbi://api.powerbi.com/v1.0/myorg/WS", XmlaAuthHint.SafeEndpoint(secretEp));
            // An unknown/garbage mode is mapped to a fixed label, not echoed raw.
            Assert.Equal("unknown", XmlaAuthHint.SafeMode(secretMode));

            // The scrubber covers '&'-separated query tokens AND a bare JWT (non key=value form).
            const string jwt = "eyJhbGciOiJodHRw.eyJhdWQiOiJhcGk.SflKxwRJSMeKKF2QT4fwpMeJ";
            Assert.DoesNotContain("SECRET2", XmlaAuthHint.Scrub("Data Source=x?access_token=SECRET2&other=1"));
            Assert.DoesNotContain(jwt, XmlaAuthHint.Scrub("connect failed with bearer " + jwt + " rejected"));
            Assert.DoesNotContain(jwt, XmlaAuthHint.SafeEndpoint("powerbi://host/ws?token=" + jwt));

            // An OPAQUE bearer/token (not key=value, not a JWT) followed by whitespace and a high-entropy run.
            Assert.DoesNotContain("abc123def456ghi", XmlaAuthHint.Scrub("connect failed: bearer token abc123def456ghi rejected"));
            // A JWT broken across whitespace (as some logs render it) is still redacted.
            Assert.DoesNotContain("SflKxwRJSMeKKF2QT4fwpMeJ", XmlaAuthHint.Scrub("auth header eyJhbGciOiJodHRw eyJhdWQiOiJhcGk SflKxwRJSMeKKF2QT4fwpMeJ was rejected"));
        }

        // ---- P1-3: the auth classifier is NARROW — a DAX error is never misread as a sign-in failure ----
        [Fact]
        public void Auth_classifier_is_narrow_and_the_interview_keeps_fix_the_dax_for_dax_errors()
        {
            // Real XMLA/Entra auth signals classify as auth.
            Assert.True(XmlaAuthHint.LooksLikeAuthFailure(AmoColdAuthError));
            Assert.True(XmlaAuthHint.LooksLikeAuthFailure("AADSTS50076: multi-factor authentication required"));
            // DAX / query errors that share generic words must NOT classify as auth.
            Assert.False(XmlaAuthHint.LooksLikeAuthFailure("The syntax for 'SUMX' is incorrect"));
            Assert.False(XmlaAuthHint.LooksLikeAuthFailure("Unauthorized"));                     // a DAX ERROR("Unauthorized")
            Assert.False(XmlaAuthHint.LooksLikeAuthFailure("Column [Token] with the value 'Expired' cannot be found"));

            // The interview scorer branches on the TYPED flag, not the message: an errored probe with AuthFailed=false
            // keeps its "fix the DAX" advice; with AuthFailed=true it becomes the sign-in probe hint.
            var q = new InterviewQuestion();
            var dax = InterviewScoring.ScoreValue(q, new ResultSet { Error = "Unauthorized", AuthFailed = false });
            Assert.Contains("fix the DAX", dax.Detail);
            var authy = InterviewScoring.ScoreValue(q, new ResultSet { Error = "whatever", AuthFailed = true });
            Assert.Contains("not signed in", authy.Detail);
            Assert.DoesNotContain("fix the DAX", authy.Detail);

            // Same for the paraphrase (equivalence) probe.
            var eqDax = InterviewScoring.ScoreParaphrase(q, new EquivalenceResult { Error = "The syntax for 'X' is incorrect", AuthFailed = false });
            Assert.Contains("the comparison failed to run", eqDax.Item2);
            var eqAuth = InterviewScoring.ScoreParaphrase(q, new EquivalenceResult { Error = "x", AuthFailed = true });
            Assert.Contains("not signed in", eqAuth.Item2);
        }

        // ---- P1 (regression fix): the Studio Reference Model picker is a HUMAN door and offers an XMLA source, so a
        // human browsing a not-signed-in workspace gets the interactive sign-in (NOT the old agent refusal) ----
        [Fact]
        public async Task Human_reference_tree_with_workspace_ref_attempts_signin_and_succeeds()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode =>
            {
                modes.Add(mode);
                if (mode == "interactive") return Task.FromResult(Snap(Db("1")));
                throw new InvalidOperationException(AmoColdAuthError);
            };

            var nodes = await engine.ListReferenceTreeAsync(Ws(), origin: "human");

            Assert.Contains(nodes, n => n.Ref == "measure:Sales/Total");
            Assert.Equal(new[] { "azcli", "interactive" }, modes);   // the human got the sign-in, not a refusal
        }

        // ---- ...and an AGENT browsing the reference tree is still refused with no interactive call ----
        [Fact]
        public async Task Agent_reference_tree_with_workspace_ref_is_refused_and_never_opens_interactive_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var modes = new List<string>();
            engine.WorkspaceTokenExportForTests = mode => { modes.Add(mode); throw new InvalidOperationException(AmoColdAuthError); };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.ListReferenceTreeAsync(Ws(), origin: "agent"));

            Assert.Contains("connect_xmla", ex.Message);
            Assert.Equal(new[] { "azcli" }, modes);
        }

        // ---- P1-4: two concurrent cold compares must NOT open two overlapping interactive sign-ins ----
        [Fact]
        public async Task Concurrent_cold_compares_single_flight_the_interactive_signin()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var inInteractive = 0;
            var maxConcurrent = 0;
            engine.WorkspaceTokenExportForTests = async mode =>
            {
                if (mode != "interactive") throw new InvalidOperationException(AmoColdAuthError);   // azcli rejected
                var now = Interlocked.Increment(ref inInteractive);
                InterlockedMax(ref maxConcurrent, now);
                await Task.Delay(60);           // hold the "browser" open long enough for a racing call to overlap
                Interlocked.Decrement(ref inInteractive);
                return Snap(Db("1"));
            };

            var a = engine.CompareModelsAsync(FileRef(Db("1")), Ws(), origin: "human");
            var b = engine.CompareModelsAsync(FileRef(Db("1")), Ws(), origin: "human");
            await Task.WhenAll(a, b);

            Assert.True(string.IsNullOrEmpty((await a).Error) && string.IsNullOrEmpty((await b).Error));
            Assert.Equal(1, maxConcurrent);     // the single-flight gate serialized the two interactive sign-ins
        }

        // ---- P2-5: a committed apply whose snapshot fell back to interactive PUSHES with that same proven mode,
        // never re-acquiring the rejected azcli token ----
        [Fact]
        public async Task Apply_commit_reuses_the_effective_mode_the_snapshot_proved()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            engine.WorkspaceTokenExportForTests = mode =>
            {
                if (mode == "interactive") return Task.FromResult(Snap(Db("1")));   // live has Total = 1
                throw new InvalidOperationException(AmoColdAuthError);              // azcli rejected
            };
            string pushedWithMode = null;
            engine.PushAuthModeForTests = m => pushedWithMode = m;
            engine.WorkspacePushHook = (_, __) => new DeployReport { Committed = true, TotalChanges = 1, SyncedRefs = new[] { "measure:Sales/Total" } };

            var src = FileRef(Db("2"));   // source wants Total = 2
            var r = await engine.ApplyDiffAsync(src, Ws(), new[] { "measure:Sales/Total" }, commit: true, origin: "human");

            Assert.True(r.Applied);
            Assert.Equal("interactive", pushedWithMode);   // the write reused the credential the snapshot proved, not azcli
        }

        // Interlocked "max" helper (no built-in): CAS the running maximum upward.
        private static void InterlockedMax(ref int target, int value)
        {
            int cur;
            while (value > (cur = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, value, cur) == cur) break;
        }
    }
}
