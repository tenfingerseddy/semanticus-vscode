using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// [T193] A loopback endpoint is a local Analysis Services instance , Power BI Desktop or a local SSAS , and it has
    /// no Entra tenant, so an Entra credential minted for it can never succeed. The three INTAKE paths must classify the
    /// address through the ONE shared classifier (LiveDeploy.IsLocalEndpoint, handed the caller's RAW argument so it
    /// parses the connection-string aliases itself AND keeps a URI's authority) and REFUSE before any intent ticket is
    /// minted and before any credential is built.
    ///
    /// Why each half of this file exists, so nobody trims it:
    /// - "It threw" alone cannot tell a pre-intent refusal from a post-mint one, so every refused call asserts the five
    ///   nonmutation facts: no intent burned (all three counters, each read by its OWN peek), no timeline event, no
    ///   registry record, no session change (same model and same live connection, by reference), no credential attempted.
    /// - Every one of those is a NEGATIVE, and a negative over an unwired seam is indistinguishable from a pass. So the
    ///   remote control proves the credential hook fires on the path that is NOT refused, and the peek control proves
    ///   each peek reads its own field.
    /// - The refusal keys on the parsed host, never on a "localhost" substring: the spoof control pins that a host that
    ///   merely CONTAINS a loopback spelling stays remote. Widening that way would skip the token on a real cloud write.
    ///
    /// This file runs entirely offline. It says nothing about live XMLA, Entra sign-in, or Power BI Desktop.
    /// </summary>
    [Collection("restore-root")]   // the registry, the timeline and the label inference share one static root
    public sealed class LoopbackIntakeRefusalTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public LoopbackIntakeRefusalTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-loopback-intake-" + Guid.NewGuid().ToString("N"));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            // Both hooks are STATIC. One left set leaks into every later test in the assembly, so clear them here as
            // well as in each test's own finally.
            EntraToken.CredentialBuiltForTests = null;
            EntraToken.SavedAccountForTests = null;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // Thrown from the credential hook by the positive controls, so the call stops at the FIRST statement of
        // BuildCredentialWith , before credential construction, GetTokenAsync, Azure CLI, TOM, or any socket.
        private sealed class CredentialSentinelException : Exception
        {
            public CredentialSentinelException() : base("T193 credential sentinel") { }
        }

        // Everything a refused call must leave untouched, captured before it runs.
        private sealed class Probe
        {
            public long Live, Session, Auth;
            public string[] History;
            public string[] Registry;
            public Session Model;
            public LiveConnection LiveConn;
            public readonly List<string> CredentialModes = new List<string>();
            public readonly List<ActivityEvent> Activity = new List<ActivityEvent>();
            public Action<ActivityEvent> Handler;   // kept so the unsubscribe removes the SAME delegate instance
            public int SavedAccountReads;
        }

        private Probe Capture(LocalEngine engine, SessionManager sessions)
        {
            var p = new Probe
            {
                Live = engine.PeekLiveIntentForTest(),
                Session = engine.PeekSessionIntentForTest(),
                Auth = engine.PeekAuthIntentForTest(),
                History = HistoryKeys(engine),
                Registry = ConnectionRegistry.List().Select(r => r.Id).ToArray(),
                Model = sessions.Current,
                LiveConn = sessions.CurrentContext.Live,
            };
            EntraToken.CredentialBuiltForTests = mode => { lock (p.CredentialModes) p.CredentialModes.Add(mode); };
            // Free extra pin on an existing seam: the refusal must also sit ahead of the saved-account disk read the
            // open paths do before building anything. Only the interactive family consults it, so it is silent for azcli.
            EntraToken.SavedAccountForTests = (mode, tenant) => { p.SavedAccountReads++; return null; };
            p.Handler = p.Activity.Add;
            sessions.Bus.Activity += p.Handler;
            return p;
        }

        private void AssertNothingHappened(LocalEngine engine, SessionManager sessions, Probe p)
        {
            // 5. No credential was even ATTEMPTED. The hook is the first statement of BuildCredentialWith, so an empty
            //    log means the switch was never reached , not merely that construction failed for some other reason.
            Assert.Empty(p.CredentialModes);
            Assert.Equal(0, p.SavedAccountReads);
            // 1. No intent burned. EXACT equality, not "did not go backwards": the counters are monotonic, so only
            //    equality separates a pre-mint refusal from a post-mint one. Each counter has its own peek.
            Assert.Equal(p.Live, engine.PeekLiveIntentForTest());
            Assert.Equal(p.Session, engine.PeekSessionIntentForTest());
            Assert.Equal(p.Auth, engine.PeekAuthIntentForTest());
            // 2. No timeline event: a refusal wrongly logged as a failed open would surface here.
            Assert.Equal(p.History, HistoryKeys(engine));
            // 3. No registry record under either kind.
            Assert.Equal(p.Registry, ConnectionRegistry.List().Select(r => r.Id).ToArray());
            // 4. No session change: the SAME model and the SAME live connection, compared by reference and read
            //    without a swap.
            Assert.Same(p.Model, sessions.Current);
            Assert.Same(p.LiveConn, sessions.CurrentContext.Live);
            Assert.Empty(p.Activity);
        }

        private static string[] HistoryKeys(LocalEngine engine) =>
            engine.ListConnectionHistoryAsync().GetAwaiter().GetResult()
                .Select(e => e.WhenUtc + "|" + e.Kind + "|" + e.Endpoint + "|" + e.Ok).ToArray();

        // A session with a model open and a stub live connection bound, so "no session change" compares two real
        // references rather than two nulls. The stub is bound BEFORE the counters are captured, so its own mint is
        // outside every assertion.
        private static async Task<(SessionManager Sessions, LocalEngine Engine)> BoundAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/bound", "Bound"));
            return (sessions, engine);
        }

        // L-ids are loopback and must be REFUSED on all three intake paths. Bare forms, both IPv4 127/8 boundaries, a
        // noncanonical 127.0.0.2, the IPv6 spellings, the SSAS "." shorthand, and EVERY accepted ConnectionInput address
        // alias wrapping a loopback host , the aliases are why the classifier has to do its own parse, because a bare
        // string comparison against the raw argument sees none of them. Do not renumber.
        public static IEnumerable<object[]> LoopbackCases() => new[]
        {
            new object[] { "L01", "localhost:51234" },
            new object[] { "L02", "127.0.0.1:51234" },
            new object[] { "L03", "127.0.0.2:51234" },
            new object[] { "L04", "127.0.0.0:51234" },
            new object[] { "L05", "127.255.255.255:51234" },
            new object[] { "L06", "::1" },
            new object[] { "L07", "[::1]:51234" },
            new object[] { "L08", "." },
            new object[] { "L09", "Data Source=localhost:51234" },
            new object[] { "L10", "DataSource=127.0.0.2:51234" },
            new object[] { "L11", "Server=127.0.0.1" },
            new object[] { "L12", "Address=localhost" },
            new object[] { "L13", "Addr=127.0.0.2:51234" },
            new object[] { "L14", "Network Address=localhost:51234" },
            new object[] { "L15", "DATA SOURCE=127.0.0.2:51234" },
            new object[] { "L16", "Data Source=\"localhost:51234\";Initial Catalog=Sales" },
            new object[] { "L17", "Data Source='localhost:51234';Initial Catalog=Sales" },
        };

        [Theory]
        [MemberData(nameof(LoopbackCases))]
        public async Task Open_live_refuses_a_loopback_endpoint_before_anything_is_minted_or_built(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                // Both non-vacuous auth modes. NOT 'token' (no credential is built there at all, so the credential
                // assertion would be vacuously true) and NOT 'serviceprincipal' (ClientSecret throws on a bare machine
                // for a reason that has nothing to do with this refusal).
                foreach (var mode in new[] { "azcli", "interactive" })
                {
                    var p = Capture(engine, sessions);
                    try
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => engine.OpenLiveAsync(endpoint, "Sales", mode, null, null));
                        Assert.Contains("loopback", refused.Message, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("open_local", refused.Message, StringComparison.Ordinal);
                    }
                    finally { Unhook(sessions, p); }
                    AssertNothingHappened(engine, sessions, p);
                }
                _ = id;
            }
        }

        [Theory]
        [MemberData(nameof(LoopbackCases))]
        public async Task Connect_xmla_refuses_a_loopback_endpoint_before_anything_is_minted_or_built(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                foreach (var mode in new[] { "azcli", "interactive" })
                {
                    var p = Capture(engine, sessions);
                    try
                    {
                        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => engine.ConnectXmlaAsync(endpoint, "Sales", mode, null));
                        Assert.Contains("loopback", refused.Message, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("connect_local", refused.Message, StringComparison.Ordinal);
                    }
                    finally { Unhook(sessions, p); }
                    AssertNothingHappened(engine, sessions, p);
                }
                _ = id;
            }
        }

        [Theory]
        [MemberData(nameof(LoopbackCases))]
        public async Task Remember_xmla_refuses_a_loopback_endpoint_before_it_reaches_disk(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var p = Capture(engine, sessions);
                try
                {
                    var refused = await Assert.ThrowsAsync<ArgumentException>(
                        () => engine.RememberXmlaConnectionAsync(endpoint, "Sales", "Local model", "azcli"));
                    Assert.Contains("loopback", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("open_local", refused.Message, StringComparison.Ordinal);
                }
                finally { Unhook(sessions, p); }
                // remember_xmla_connection mints nothing today; the counter assertions are regression cover that the
                // refusal did not START minting one. Its load-bearing facts are the empty registry and the silent bus.
                AssertNothingHappened(engine, sessions, p);
                Assert.DoesNotContain(ConnectionRegistry.List(), r => LiveDeploy.IsLocalEndpoint(r.Endpoint));
                _ = id;
            }
        }

        // R-ids must NOT be refused. The first two are host-suffix loopback spoofs, the third is remote by the scheme
        // rule (its PATH contains "localhost"), and the last two are the same spoofs wrapped in an address alias, so a
        // parse-then-substring fix cannot pass either. 128.0.0.1 is the adjacent non-loopback IPv4 control.
        public static IEnumerable<object[]> RemoteCases() => new[]
        {
            new object[] { "R01", "powerbi://api.powerbi.com/v1.0/myorg/ws" },
            new object[] { "R02", "localhost.evil.com" },
            new object[] { "R03", "127.0.0.1.evil.com" },
            new object[] { "R04", "powerbi://api.powerbi.com/v1.0/myorg/prod-localhost-mirror" },
            new object[] { "R05", "128.0.0.1:51234" },
            new object[] { "R06", "Data Source=localhost.evil.com" },
            new object[] { "R07", "Server=powerbi://api.powerbi.com/v1.0/myorg/ws" },
            // [T193 final correction] UNAMBIGUOUS QUOTED VALUES STAY ACCEPTED. The quote-aware parse that closes R26-R40
            // must not turn ordinary quoting into a refusal, so both quote dialects and a quoted value holding the
            // characters people actually quote for (spaces, '&') are pinned as still-accepted remote coordinates.
            new object[] { "R08", "Data Source=\"powerbi://api.powerbi.com/v1.0/myorg/ws\";Initial Catalog=Sales" },
            new object[] { "R09", "Server='powerbi://api.powerbi.com/v1.0/myorg/ws'" },
            new object[] { "R10", "Data Source=\"powerbi://api.powerbi.com/v1.0/myorg/Sales & Marketing\"" },
        };

        [Theory]
        [MemberData(nameof(RemoteCases))]
        public async Task Open_live_does_not_refuse_a_remote_endpoint_and_stops_on_the_credential_sentinel(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var built = new List<string>();
                EntraToken.CredentialBuiltForTests = mode => { built.Add(mode); throw new CredentialSentinelException(); };
                try
                {
                    await Assert.ThrowsAsync<CredentialSentinelException>(
                        () => engine.OpenLiveAsync(endpoint, "Sales", null, null, null));
                }
                finally { EntraToken.CredentialBuiltForTests = null; }

                // Exactly one attempt, in the default mode, and the sentinel is what came back , so the hook is wired,
                // it fires on the path that is NOT refused, and the call stopped before Azure CLI, TOM or the network.
                // An eventual auth failure is NOT this control's success condition.
                Assert.Equal(new[] { "azcli" }, built);
                _ = id;
            }
        }

        [Theory]
        [MemberData(nameof(RemoteCases))]
        public async Task Connect_xmla_does_not_refuse_a_remote_endpoint_and_stops_on_the_credential_sentinel(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var built = new List<string>();
                EntraToken.CredentialBuiltForTests = mode => { built.Add(mode); throw new CredentialSentinelException(); };
                try
                {
                    await Assert.ThrowsAsync<CredentialSentinelException>(
                        () => engine.ConnectXmlaAsync(endpoint, "Sales", null, null));
                }
                finally { EntraToken.CredentialBuiltForTests = null; }

                Assert.Equal(new[] { "azcli" }, built);
                _ = id;
            }
        }

        [Theory]
        [MemberData(nameof(RemoteCases))]
        public async Task Remember_xmla_still_stores_a_remote_endpoint(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var record = await engine.RememberXmlaConnectionAsync(endpoint, "Sales", "Published model", "azcli");

                Assert.Equal("xmla", record.Kind);
                Assert.False(LiveDeploy.IsLocalEndpoint(record.Endpoint));
                Assert.Contains(ConnectionRegistry.List(), r => r.Id == record.Id);
                _ = id;
            }
        }

        // [T193 correction] URI-AUTHORITY SPOOFS. These are the exact forms pinned REMOTE as R21-R25 in
        // LocalEndpointTests: a ';' inside a URI authority makes "localhost" the USER-INFO and the real host something
        // else entirely, so the address is remote and must not be met with a loopback refusal.
        //
        // The regression they close. XmlaAuthHint.SafeEndpoint drops everything from the first ';' wholesale, which is
        // right for a connection-string tail and lossy for a URI authority: ConnectionInput.Parse reduces
        // "http://localhost;@evil.example/x" to "http://localhost". So an intake refusal handed coords.Endpoint
        // classifies a REMOTE address as loopback and refuses it, while LiveDeploy.IsLocalEndpoint , which preserves a
        // URI's authority for exactly this reason , calls the same string remote. Two classifiers disagreeing on one
        // string is the defect; the callers must therefore hand the RAW argument to the ONE shared classifier, which
        // does its own parse and so still covers every connection-string address alias.
        public static IEnumerable<object[]> BareAuthoritySpoofCases() => new[]
        {
            new object[] { "R21", "http://localhost;@evil.example/x" },
            new object[] { "R22", "http://localhost:80;@evil.example/x" },
            new object[] { "R23", "http://[::1];@evil.example/x" },
            new object[] { "R24", "http://[::1]:80;@evil.example/x" },
            new object[] { "R25", "http://localhost;@evil.example/x?q=1" },
        };

        // [T193 final correction] THE SAME SPOOF INSIDE A QUOTED CONNECTION-STRING VALUE. The bare forms above are only
        // half the intake surface: the connection-string dialect QUOTES a value that contains the ';' separator, and
        // ConnectionInput.Parse used to split the string on ';' before it unquoted anything. The value was therefore cut
        // at the in-value ';', the FRAGMENT ("http://localhost, closing quote and real host gone) became the address,
        // and HasAmbiguousAuthority , which only ever sees ONE address token , never saw the ';' that made the input
        // ambiguous. The coordinate that came back was Safe, was neither the address the caller wrote nor a reduction of
        // it, and could be connected to and persisted. So every accepted address alias is pinned here with the spoof
        // inside a quoted value, in both quote dialects, with the doubled-quote escape, and in the malformed spellings a
        // fail-closed parser must also refuse rather than half-read. All of these are string-work only: no host here is
        // ever resolved or contacted, and none of them carries a credential.
        public static IEnumerable<object[]> QuotedAuthoritySpoofCases() => new[]
        {
            new object[] { "R26", "Data Source=\"http://localhost;@evil.example/x\"" },
            new object[] { "R27", "DataSource='http://localhost;@evil.example/x'" },
            new object[] { "R28", "Server=\"http://localhost:80;@evil.example/x\"" },
            new object[] { "R29", "Address=\"http://[::1];@evil.example/x\"" },
            new object[] { "R30", "Addr='http://[::1]:80;@evil.example/x'" },
            new object[] { "R31", "Network Address=\"http://localhost;@evil.example/x?q=1\"" },
            new object[] { "R32", "DATA SOURCE=\"http://localhost;@evil.example/x\"" },
            new object[] { "R33", "Data Source = \"http://localhost;@evil.example/x\" ;Initial Catalog=Sales" },
            new object[] { "R34", "Initial Catalog=Sales;Data Source=\"http://localhost;@evil.example/x\"" },
            // The escaped-quote spellings: the doubled quote is INSIDE the value, so a parser that stopped at the first
            // quote it saw would end the value early and hand on a fragment exactly as the ';' split did.
            new object[] { "R35", "Data Source=\"http://local\"\"host;@evil.example/x\"" },
            new object[] { "R36", "DataSource='http://localhost;@evil.example/x''y'" },
            // Malformed: the quoted value is never closed, so where it ends (and therefore which host it names) is not
            // knowable. FAIL CLOSED before any lossy splitting rather than guess an end.
            new object[] { "R37", "Data Source=\"http://localhost;@evil.example/x" },
            new object[] { "R38", "Data Source='http://localhost;@evil.example/x;Initial Catalog=Sales" },
            // Malformed the other way: the value opens quoted, closes, and then carries trailing bytes. Two readings
            // again (is the tail part of the address or not), so it is refused rather than half-read.
            new object[] { "R39", "Data Source=\"http://localhost\"@evil.example;Initial Catalog=Sales" },
            // Two address keys naming DIFFERENT hosts: first-wins here, last-wins in the ADO.NET dialect, so the
            // coordinate connected to would depend on who read the string. Ambiguous, so refused.
            new object[] { "R40", "Data Source=powerbi://api.powerbi.com/v1.0/myorg/ws;Server=\"http://evil.example\"" },
            // The other half of what a quoted ';' costs. These are not authority spoofs: the ';' sits AFTER the
            // authority, so the host is not in doubt. The truncation is. Once the value is read as the dialect writes
            // it, the ';' is data, and XmlaAuthHint.SafeEndpoint would still cut the address there and hand on a
            // shorter path , on a powerbi:// address, a different WORKSPACE than the caller named. Same class of loss,
            // same answer: refused rather than truncated.
            new object[] { "R41", "Data Source=\"powerbi://api.powerbi.com/v1.0/myorg/ws;2\"" },
            new object[] { "R42", "Data Source='powerbi://api.powerbi.com/v1.0/myorg/ws;Initial Catalog=Other'" },
        };

        public static IEnumerable<object[]> AuthoritySpoofCases() =>
            BareAuthoritySpoofCases().Concat(QuotedAuthoritySpoofCases());

        [Theory]
        [MemberData(nameof(AuthoritySpoofCases))]
        public void The_shared_classifier_calls_a_uri_authority_spoof_remote(string id, string endpoint)
        {
            // The contract the intake refusal must agree with, restated here so this file fails on its own when the
            // agreement breaks rather than only through LocalEndpointTests.
            Assert.False(LiveDeploy.IsLocalEndpoint(endpoint));
            _ = id;
        }

        // ---- THE DESTINATION PROOF -------------------------------------------------------------------------------
        // Classifying the raw authority (above) is only half a guarantee: a call that is allowed through then CONNECTS
        // to and PERSISTS ConnectionInput.Parse(...).Endpoint, not the raw string. If those two name different hosts,
        // the intake gate approved one destination and the connection used another, which is a worse defect than the
        // misclassification it replaced. So the invariant is agreement, asserted over the WHOLE corpus this file and
        // LocalEndpointTests already pin , every loopback form, every remote form, all six address aliases, the quoted
        // values, the 127/8 boundaries and the embedded-catalog shapes , not just over the spoofs:
        //
        //     a coordinate is either REFUSED (Safe == false, never connected and never written), or it classifies
        //     exactly as the raw argument the caller gave.
        //
        // Nothing here opens a socket or builds a credential; Parse and IsLocalEndpoint are pure string work.
        public static IEnumerable<object[]> EveryClassifiedCase() =>
            LoopbackCases().Concat(RemoteCases()).Concat(AuthoritySpoofCases());

        [Theory]
        [MemberData(nameof(EveryClassifiedCase))]
        public void The_parsed_destination_agrees_with_the_classified_address(string id, string endpoint)
        {
            var coords = ConnectionInput.Parse(endpoint, null);
            if (!coords.Safe)
            {
                // Refused. A refused coordinate is never a connectable address, so there is no destination to disagree.
                Assert.Equal(ConnectionInput.Redacted, coords.Endpoint);
                return;
            }
            Assert.Equal(LiveDeploy.IsLocalEndpoint(endpoint), LiveDeploy.IsLocalEndpoint(coords.Endpoint));
            _ = id;
        }

        [Theory]
        [MemberData(nameof(AuthoritySpoofCases))]
        public void A_uri_authority_spoof_is_refused_rather_than_retargeted(string id, string endpoint)
        {
            // The specific half of the invariant above, stated so a failure names the finding. SafeEndpoint drops
            // everything from the first ';' wholesale, which is right for a connection-string tail and, for a URI whose
            // ';' sits inside the AUTHORITY, silently retargets: the leading token becomes user-info, the real host is
            // thrown away, and the coordinate left behind is loopback. Neither reading may be acted on silently , not
            // the truncated loopback one (the caller's own authority says otherwise) and not the remote one (that is
            // the host the spoof was built to hide, and an Entra token would be minted for it). So the normalisation
            // boundary fails closed and the operation is refused before any credential, ticket or write.
            var coords = ConnectionInput.Parse(endpoint, null);
            Assert.False(coords.Safe);
            Assert.Equal(ConnectionInput.Redacted, coords.Endpoint);
            Assert.False(LiveDeploy.IsLocalEndpoint(coords.Endpoint));
            _ = id;
        }

        [Theory]
        [MemberData(nameof(AuthoritySpoofCases))]
        public async Task Open_live_refuses_a_uri_authority_spoof_before_any_credential(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var p = Capture(engine, sessions);
                try
                {
                    var refused = await Assert.ThrowsAsync<ArgumentException>(
                        () => engine.OpenLiveAsync(endpoint, "Sales", null, null, null));
                    // Not the loopback message: this address is NOT classified loopback, and saying so would be the
                    // old defect wearing a new exception type. It is refused because it names two destinations.
                    Assert.DoesNotContain("loopback", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("safe address", refused.Message, StringComparison.OrdinalIgnoreCase);
                    // The refusal must never echo the input back (the authority holds a would-be user-info token).
                    Assert.DoesNotContain("evil.example", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
                finally { Unhook(sessions, p); }

                AssertNothingHappened(engine, sessions, p);
                _ = id;
            }
        }


        [Theory]
        [MemberData(nameof(AuthoritySpoofCases))]
        public async Task Connect_xmla_refuses_a_uri_authority_spoof_before_any_credential(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                var p = Capture(engine, sessions);
                try
                {
                    var refused = await Assert.ThrowsAsync<ArgumentException>(
                        () => engine.ConnectXmlaAsync(endpoint, "Sales", null, null));
                    Assert.DoesNotContain("loopback", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("safe address", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("evil.example", refused.Message, StringComparison.OrdinalIgnoreCase);
                }
                finally { Unhook(sessions, p); }

                AssertNothingHappened(engine, sessions, p);
                _ = id;
            }
        }


        [Theory]
        [MemberData(nameof(AuthoritySpoofCases))]
        public async Task Remember_xmla_refuses_a_spoof_and_writes_no_coordinate(string id, string endpoint)
        {
            var (sessions, engine) = await BoundAsync();
            using (sessions)
            using (engine)
            {
                // remember_xmla_connection is the one intake path whose subject is not what we will CONNECT to but what
                // we will WRITE DOWN, and the hub later routes a stored row on its kind and its stored endpoint
                // (decision 3). The coordinate this input would otherwise persist is the truncated loopback one, so
                // letting it through would create exactly the new loopback row kinded "xmla" that T193 exists to
                // prevent. It is now refused at the normalisation boundary instead, before Remember and before the
                // activity event, and the SAVED COORDINATE PROOF is that the registry is byte-identical afterwards -
                // not merely free of loopback rows, but unchanged.
                var before = ConnectionRegistry.List().Select(r => r.Id + "|" + r.Endpoint).ToArray();

                var refused = await Assert.ThrowsAsync<ArgumentException>(
                    () => engine.RememberXmlaConnectionAsync(endpoint, "Sales", "Spoof", "azcli"));
                Assert.Contains("safe address", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("evil.example", refused.Message, StringComparison.OrdinalIgnoreCase);

                Assert.Equal(before, ConnectionRegistry.List().Select(r => r.Id + "|" + r.Endpoint).ToArray());
                Assert.DoesNotContain(ConnectionRegistry.List(), r => LiveDeploy.IsLocalEndpoint(r.Endpoint));
                _ = id;
            }
        }

        [Fact]
        public void Each_intent_peek_reads_its_own_counter()
        {
            // Without this, a copy-paste that made all three peeks read _liveIntent would satisfy every assertion in
            // this file. Each mint seam already exists; each moves a known subset by exactly one.
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);

            var live0 = engine.PeekLiveIntentForTest();
            var session0 = engine.PeekSessionIntentForTest();
            var auth0 = engine.PeekAuthIntentForTest();

            engine.MintLiveIntentForTest();
            Assert.Equal(live0 + 1, engine.PeekLiveIntentForTest());
            Assert.Equal(session0, engine.PeekSessionIntentForTest());
            Assert.Equal(auth0, engine.PeekAuthIntentForTest());

            engine.MintSessionSwapIntentsForTest();
            Assert.Equal(live0 + 2, engine.PeekLiveIntentForTest());
            Assert.Equal(session0 + 1, engine.PeekSessionIntentForTest());
            Assert.Equal(auth0, engine.PeekAuthIntentForTest());

            engine.MintAuthIntentForTest();
            Assert.Equal(live0 + 2, engine.PeekLiveIntentForTest());
            Assert.Equal(session0 + 1, engine.PeekSessionIntentForTest());
            Assert.Equal(auth0 + 1, engine.PeekAuthIntentForTest());
        }

        private static void Unhook(SessionManager sessions, Probe p)
        {
            EntraToken.CredentialBuiltForTests = null;
            EntraToken.SavedAccountForTests = null;
            sessions.Bus.Activity -= p.Handler;
        }
    }
}
