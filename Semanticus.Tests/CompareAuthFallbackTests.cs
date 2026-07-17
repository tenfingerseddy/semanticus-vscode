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

        // ---- CRITICAL 1a (round 5): a QUOTED credential value with embedded spaces must be redacted WHOLE — the round-4
        // value pattern stopped at the first space, so it leaked everything after that space. ----
        [Fact]
        public void A_quoted_credential_value_with_spaces_is_redacted_whole_leaving_no_tail()
        {
            // Built at runtime (never a source literal) so the release secret-scanner never sees a hardcoded secret. The
            // double-quoted form is a Password value, the single-quoted form a ClientSecret value, each with spaces.
            var v1 = "alpha beta gamma";
            var v2 = "one two three";
            var dq = "Pass" + "word=\"" + v1 + "\"";
            var sq = "Client" + "Secret='" + v2 + "'";
            foreach (var (raw, tailWord) in new[] { (dq, "gamma"), (sq, "three") })
            {
                var scrubbed = XmlaAuthHint.Scrub(raw);
                Assert.DoesNotContain(tailWord, scrubbed);          // the value TAIL no longer leaks past the first space
                Assert.DoesNotContain("beta", XmlaAuthHint.Scrub(dq));
                Assert.Contains("=***", scrubbed);                  // the whole quoted value became ***
                Assert.True(XmlaAuthHint.ContainsSecret(raw));      // and the detector sees it as a secret
            }
        }

        // ---- HIGH 2 (round 5): the round-4 SUBSTRING key match false-fired on innocent names ("Turkey" contains "key",
        // "Secretariat" contains "secret"). Word-level matching leaves them intact while still catching composite keys. ----
        [Fact]
        public void An_innocent_key_that_merely_contains_a_marker_word_is_not_treated_as_a_secret()
        {
            foreach (var innocent in new[] { "Turkey=2026", "Monkey=Business", "Secretariat=1973", "DonkeyKong=8" })
            {
                Assert.False(XmlaAuthHint.ContainsSecret(innocent));          // not a credential
                Assert.Equal(innocent, XmlaAuthHint.Scrub(innocent));         // survives scrub intact (value not redacted)
                Assert.Equal(-1, XmlaAuthHint.SuspectKeyValueIndex(innocent));// and is never cut as a "credential tail"
            }
            // ...but real composite secret keys are STILL caught (word boundary hits 'secret' / 'key' / 'token').
            foreach (var real in new[] { "Client" + "Secret=x", "Api" + "Key=y", "access" + "_token=z", "shared" + "AccessSignature=w" })
                Assert.True(XmlaAuthHint.ContainsSecret(real));
        }

        // ---- CRITICAL 1 (round 6): COMPACT key spellings (no camel/underscore boundary to tokenize) and a secret NESTED
        // inside an innocent key's QUOTED value both slipped past the round-5 word-level detector — marked Safe, passed
        // through the scrubber verbatim. Now: a curated compact-compound list PLUS recursion into a quoted value. Innocent
        // names that merely END with a marker ("Turkey"/"Monkey") or START with one ("Secretariat") still survive. ----
        [Fact]
        public void Compact_and_nested_secret_keys_are_detected_and_scrubbed()
        {
            // Built at runtime (never source literals) so the release secret-scanner never sees a hardcoded secret.
            var sentinel = "SENT" + Guid.NewGuid().ToString("N");
            var compact = new[]
            {
                "client" + "secret=" + sentinel,           // no boundary to tokenize -> round-5 saw one word "clientsecret"
                "account" + "key=" + sentinel,
                "sharedaccess" + "signature=" + sentinel,
            };
            foreach (var raw in compact)
            {
                Assert.True(XmlaAuthHint.ContainsSecret(raw));                 // the compact-compound list now catches it
                Assert.DoesNotContain(sentinel, XmlaAuthHint.Scrub(raw));      // and the value is redacted
                Assert.Contains("=***", XmlaAuthHint.Scrub(raw));
            }
            // A secret NESTED inside an innocent key's quoted value: the outer Metadata="…" pair looks innocent, but the
            // access_token hides inside the quotes. The scrubber recurses into the quoted content and, when a credential is
            // found there, redacts the WHOLE outer value (round-7: full redaction, not a fragile partial re-escape).
            var nested = "Metadata=\"x?access" + "_token=" + sentinel + "\"";
            Assert.True(XmlaAuthHint.ContainsSecret(nested));
            var scrubbedNested = XmlaAuthHint.Scrub(nested);
            Assert.DoesNotContain(sentinel, scrubbedNested);
            Assert.Equal("Metadata=***", scrubbedNested);                     // the whole credential-bearing quoted value is redacted
            Assert.Equal(0, XmlaAuthHint.SuspectKeyValueIndex(nested));        // and a dataset-name cut starts at the outer key
            // The curated list is EXACT-whole-key, so Turkey/Monkey/Secretariat (which end/start with a marker word) survive.
            foreach (var innocent in new[] { "Turkey=2026", "Monkey=Business", "Secretariat=1973" })
                Assert.False(XmlaAuthHint.ContainsSecret(innocent));
        }

        // ---- HIGH 3 (round 6): an INVALID explicit tenant (a typo like "contoso") silently became a home-tenant sign-in,
        // leaving the registry's stored tenant inconsistent with the account. Now RequireTenant distinguishes OMITTED
        // (null -> home tenant, unchanged) from INVALID (refuse with a sanitized error that never echoes the input). ----
        [Fact]
        public void RequireTenant_allows_omitted_and_valid_but_refuses_an_invalid_tenant()
        {
            Assert.Null(XmlaAuthHint.RequireTenant(null));                     // omitted -> the account's home tenant
            Assert.Null(XmlaAuthHint.RequireTenant("   "));                    // blank -> omitted
            Assert.Equal("contoso.onmicrosoft.com", XmlaAuthHint.RequireTenant("Contoso.OnMicrosoft.com"));   // valid domain, lowercased
            Assert.Equal("72f988bf-86f1-41af-91ab-2d7cd011db47", XmlaAuthHint.RequireTenant("72F988BF-86F1-41AF-91AB-2D7CD011DB47"));
            // An INVALID non-empty tenant is REFUSED (never silently defaulted) with a sanitized message. Use a distinctive
            // input NOT present in the fixed teaching copy, so "not echoed" is unambiguous (the message's own example domain
            // legitimately contains "contoso", so the reviewer's "contoso" typo can't test echo-suppression cleanly).
            var ex = Assert.Throws<ArgumentException>(() => XmlaAuthHint.RequireTenant("myworkspaceZZ"));
            Assert.Contains("does not look like a tenant", ex.Message);
            Assert.DoesNotContain("myworkspaceZZ", ex.Message);              // the input is never echoed back
            // A secret-shaped tenant is likewise refused, and its value never surfaces in the error.
            var secret = "SECRETVAL" + Guid.NewGuid().ToString("N");
            var ex2 = Assert.Throws<ArgumentException>(() => XmlaAuthHint.RequireTenant("Pass" + "word=" + secret));
            Assert.DoesNotContain(secret, ex2.Message);
        }

        // The invalid tenant is refused at the OPEN intake too (open_live), before any sign-in or snapshot — the export seam
        // is never reached, so the registry is never left inconsistent with the auth (HIGH 3).
        [Fact]
        public async Task Open_live_refuses_an_invalid_tenant_before_it_signs_in()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var signInReached = false;
            engine.OpenLiveFailureProbeForTests = () => { signInReached = true; return Task.CompletedTask; };
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => engine.OpenLiveAsync(Endpoint, "DS", "interactive", null, "myworkspaceZZ", forceReauth: false));
            Assert.Contains("does not look like a tenant", ex.Message);
            Assert.DoesNotContain("myworkspaceZZ", ex.Message);   // the typo is never echoed back
            Assert.False(signInReached);                          // refused at intake, before the sign-in/snapshot boundary
        }

        // ---- CRITICAL 1b (round 5): a tenant is result-bound (SessionInfo.CurrentTenant via LiveOrigin); a secret-shaped
        // tenant must be scrubbed to null at intake. Only a real GUID / domain / alias survives. ----
        [Fact]
        public void SafeTenant_keeps_only_a_real_tenant_shape_and_scrubs_anything_else()
        {
            Assert.Equal("contoso.com", XmlaAuthHint.SafeTenant("Contoso.com"));                 // domain (lowercased)
            Assert.Equal("contoso.onmicrosoft.com", XmlaAuthHint.SafeTenant("contoso.onmicrosoft.com"));
            Assert.Equal("72f988bf-86f1-41af-91ab-2d7cd011db47", XmlaAuthHint.SafeTenant("72F988BF-86F1-41AF-91AB-2D7CD011DB47"));
            Assert.Equal("common", XmlaAuthHint.SafeTenant("common"));
            // A bare word is not a tenant; a secret-shaped value with an '=' or a space is scrubbed to null (never surfaced).
            Assert.Null(XmlaAuthHint.SafeTenant("contoso"));
            Assert.Null(XmlaAuthHint.SafeTenant("Pass" + "word=secret-value"));
            Assert.Null(XmlaAuthHint.SafeTenant("a tenant with spaces"));
            Assert.Null(XmlaAuthHint.SafeTenant(null));
        }

        // ---- HIGH 3a (round 5): a read-only compare snapshot must NOT mint a LIVE intent — otherwise a concurrent slow
        // connect sees _liveIntent move and falsely supersedes its own swap even though no newer CONNECTION happened. ----
        [Fact]
        public async Task A_compare_snapshot_does_not_supersede_a_concurrent_connects_live_swap()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(Snap(Db("1")));   // the compare read succeeds silently
            // A connect mints its live intent at op START (before the compare runs).
            var connectTicket = engine.MintLiveIntentForTest();
            // A read-only compare runs while that connect is still in flight.
            var diff = await engine.CompareModelsAsync(FileRef(Db("2")), Ws(), origin: "human");
            Assert.True(string.IsNullOrEmpty(diff.Error));
            // The compare must not have bumped the LIVE intent, so the connect's swap with its original ticket still WINS.
            // Pre-fix (compare minted a live intent) this returned false — the connect falsely reported itself superseded.
            Assert.True(engine.TrySwapLiveForTest(LiveConnection.ForTest("xmla", Endpoint, "DS"), connectTicket));
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
