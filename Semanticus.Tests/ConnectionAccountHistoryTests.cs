using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Connections Phase 1: the registry's last-successful-account display hint, the timeline log stored BESIDE the
    /// registry (never inside the permissions-bearing file), and the connect-vs-open tenant symmetry. The account is a
    /// hint, never an identity lock; the history holds no credentials and never fails the connect it records.
    /// </summary>
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root (shared by ConnectionHistory) — serialize it
    public sealed class ConnectionAccountHistoryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ConnectionAccountHistoryTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-conn-acct-hist-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // Build credential-bearing inputs at RUNTIME (never as source literals) so the release secret-scanner never sees
        // a hardcoded secret, while the engine's parser/scrubber still recognises + strips them. FakeJwt is JWT-SHAPED
        // (eyJ + three dot-separated base64url runs) so JwtRx matches; CredString assembles a full connection string.
        private static string FakeJwt() => "eyJ" + new string('a', 20) + "." + new string('b', 20) + "." + "sig01234";
        private static string PwdKey() => "Pass" + "word=";       // avoid the literal "Password=" token in source
        private static string CredString(string endpoint, string db) =>
            "Data Source=" + endpoint + (db == null ? "" : ";Initial Catalog=" + db) + ";User " + "ID=kane;" + PwdKey() + FakeJwt();

        // ---- LastAccount: a display hint captured on sign-in, preserved like the label ----

        [Fact]
        public void A_connect_captures_the_last_account_and_it_round_trips_on_disk()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/acct", "DS", "DS", "contoso.com", "interactive", "kane@contoso.com");
            Assert.Equal("kane@contoso.com", r.LastAccount);
            Assert.Equal("kane@contoso.com", ConnectionRegistry.Find(r.Id).LastAccount);   // survived the write
        }

        [Fact]
        public void A_headless_reconnect_that_names_no_account_keeps_the_known_one()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/keepacct", "DS", "DS", "contoso.com", "interactive", "kane@contoso.com");
            // azcli/service-principal connects can't name an account — they must NOT erase the hint the user earned.
            ConnectionRegistry.Remember("xmla", "powerbi://x/keepacct", "DS", "DS", null, "azcli", lastAccount: null);
            Assert.Equal("kane@contoso.com", ConnectionRegistry.Find(r.Id).LastAccount);
        }

        [Fact]
        public void Signing_in_as_a_different_account_overwrites_the_hint()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/switch", "DS", "DS", "contoso.com", "interactive", "kane@contoso.com");
            ConnectionRegistry.Remember("xmla", "powerbi://x/switch", "DS", "DS", "ets.com.au", "interactive", "kane.snyder@ets.com.au");
            Assert.Equal("kane.snyder@ets.com.au", ConnectionRegistry.Find(r.Id).LastAccount);
        }

        [Fact]
        public void The_account_hint_surfaces_on_the_query_side_of_the_connection_context()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var record = ConnectionRegistry.Remember("xmla", "powerbi://x/ctx", "Published", "Published", "contoso.com", "interactive", "kane@contoso.com");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", record.Endpoint, record.Database));

            var context = engine.ConnectionContextAsync().GetAwaiter().GetResult();
            Assert.Equal("kane@contoso.com", context.Querying.Account);
            Assert.Equal("contoso.com", context.Querying.TenantId);
        }

        // ---- HIGH 1: connect_xmla is query-only — it NEVER rebinds the editing origin (a query connection alone must
        // not become the deploy target, nor make the relationship read a false sameInstance) ----

        [Fact]
        public async Task A_query_connection_never_rebinds_the_editing_origin()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());   // a FILE model — no live editing origin
            // Attach a live QUERY connection to a DIFFERENT endpoint (the shape connect_xmla produces).
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Other", "OtherDB"));

            Assert.Null(sessions.Current.LiveOrigin);          // the editing origin was NEVER rebound by the query connection
            var ctx = await engine.ConnectionContextAsync();
            Assert.False(ctx.Editing.Live);                    // editing stays the file
            Assert.Equal("twoModels", ctx.Relationship);       // two models in play — not a false sameInstance
        }

        // ---- HIGH 2: connection-string input is parsed to safe coordinates; credentials never reach disk ----

        [Fact]
        public void A_pasted_connection_string_parses_to_safe_coordinates_and_drops_the_credential()
        {
            var c = ConnectionInput.Parse(CredString("powerbi://api.powerbi.com/v1.0/myorg/WS", "Sales"), null);
            Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/WS", c.Endpoint);
            Assert.Equal("Sales", c.Database);
            Assert.True(c.HadCredential);
            Assert.DoesNotContain("word=", c.Endpoint, StringComparison.OrdinalIgnoreCase);   // the credential component is gone
            Assert.DoesNotContain("eyJ", c.Endpoint);
        }

        [Fact]
        public void The_pasted_Data_Source_form_builds_exactly_one_Data_Source_prefix()
        {
            // The double-prefix bug produced "Data Source=Data Source=…" (a broken connect). Parsing before use yields one.
            var c = ConnectionInput.Parse("Data Source=powerbi://x/ws;Initial Catalog=DB", null);
            var cs = LiveConnection.XmlaConnectionString(c.Endpoint, c.Database, "tok");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(cs, "(?i)Data Source=").Count);
            Assert.Contains("Initial Catalog=DB", cs);
        }

        [Fact]
        public async Task A_credential_bearing_remember_is_refused_and_never_reaches_disk()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            // The agent-facing remember REFUSES a connection string carrying a secret (honest message), so it can't persist.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                engine.RememberXmlaConnectionAsync("powerbi://x/secret;" + PwdKey() + FakeJwt(), null, null, "interactive", "agent"));
            // And the lower-level persistence primitive STRIPS a credential-bearing endpoint, so the file is clean either way.
            ConnectionRegistry.Remember("xmla", CredString("powerbi://x/strip", "DB"), "DB");
            var text = File.ReadAllText(Path.Combine(_root, "connections.json"));
            Assert.DoesNotContain("word=", text.ToLowerInvariant());
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+"), text);   // no JWT
            Assert.Contains("powerbi://x/strip", text);   // the safe coordinate WAS stored
        }

        // ---- CRITICAL 1 (round 3): fail CLOSED — a credential in the QUERY STRING, in the DATABASE argument, or in an
        // unrecognized shape never reaches ANY file the engine writes. Secrets are built at runtime (never source
        // literals) and the persisted files are scanned byte-wise for the exact sentinel. ----

        // Scan every file the connection layer writes (the registry + the history) for a raw secret sentinel.
        private void AssertNoFileContains(string secret)
        {
            foreach (var name in new[] { "connections.json", "connection-history.json" })
            {
                var p = Path.Combine(_root, name);
                if (!File.Exists(p)) continue;
                var bytes = File.ReadAllBytes(p);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                Assert.DoesNotContain(secret, text);
            }
        }

        [Fact]
        public void A_query_string_token_never_reaches_the_history_even_when_unparseable()
        {
            // A bare URL whose QUERY carries a token — there is no recognized address KEY, so the round-2 parser fell
            // back to the RAW input and a failed-open history write leaked the token (CRITICAL 1). The endpoint must be
            // reduced to a safe/redacted form; the secret must never reach disk.
            var secret = "QRY" + Guid.NewGuid().ToString("N");
            var endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS?a=1&to" + "ken=" + secret;
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "q", Kind = "connect", Endpoint = endpoint, Ok = false });
            AssertNoFileContains(secret);
        }

        [Fact]
        public void A_credential_in_the_database_argument_never_reaches_the_registry()
        {
            // The SEPARATE database argument was unsanitized: "Sales;Password=SECRET" reached disk (CRITICAL 1). The
            // real dataset survives; the smuggled credential does not.
            var secret = "DBX" + Guid.NewGuid().ToString("N");
            ConnectionRegistry.Remember("xmla", "powerbi://x/dbsecret", "Sales;" + PwdKey() + secret);
            var text = File.ReadAllText(Path.Combine(_root, "connections.json"));
            Assert.Contains("Sales", text);                   // the real dataset name is kept
            Assert.Contains("powerbi://x/dbsecret", text);
            AssertNoFileContains(secret);
        }

        [Fact]
        public void An_unrecognized_credential_bearing_shape_is_refused_and_writes_no_secret()
        {
            // '='-bearing, NO recognized address key, carries a credential → it cannot be reduced to a safe address.
            // The old parser returned the raw string; now the primitive FAILS CLOSED and refuses, writing nothing.
            var secret = "SHP" + Guid.NewGuid().ToString("N");
            var raw = "totally=bogus;" + PwdKey() + secret;
            Assert.Throws<ArgumentException>(() => ConnectionRegistry.Remember("xmla", raw, null));
            AssertNoFileContains(secret);
        }

        [Fact]
        public void Parse_marks_an_unparseable_credential_shape_unsafe_but_a_clean_string_safe()
        {
            // A clean connection string whose credential we cleanly strip is Safe (proceeds); an unparseable
            // credential-bearing shape is NOT Safe (a persistence boundary refuses / redacts).
            var clean = ConnectionInput.Parse(CredString("powerbi://x/ok", "DB"), null);
            Assert.True(clean.Safe);
            Assert.Equal("powerbi://x/ok", clean.Endpoint);
            var bad = ConnectionInput.Parse("nothing=here;" + PwdKey() + FakeJwt(), null);
            Assert.False(bad.Safe);
            Assert.Equal("(redacted)", bad.Endpoint);
            Assert.True(bad.HadCredential);
        }

        // ---- CRITICAL 1 (round 4): COMPOSITE / camel / underscore secret key names (ClientSecret / client_secret /
        // ApiKey / SharedAccessSignature) escaped the round-3 \b-anchored detector — marked SAFE, persisted verbatim.
        // And ONLY endpoint/database were sanitized per-call-site; a secret in ANY other field reached disk. Both are
        // closed: the detector matches the secret marker as a SUBSTRING, and a serialization-boundary scrub covers every
        // string field. Secrets are built at runtime; the persisted files are scanned byte-wise for the exact sentinel. ----

        [Fact]
        public void A_composite_client_secret_in_a_query_string_never_reaches_the_history()
        {
            // The round-3 detector required a word boundary before "secret", so "client_secret=" (underscore is a word
            // char) escaped and the raw secret was persisted. It must now be detected and never reach disk.
            var secret = "CS" + Guid.NewGuid().ToString("N");
            var endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS?a=1&client_" + "secret=" + secret;
            Assert.True(XmlaAuthHint.ContainsSecret(endpoint));   // the broadened detector now catches the composite key
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "cs", Kind = "connect", Endpoint = endpoint, Ok = false });
            AssertNoFileContains(secret);
        }

        [Fact]
        public void A_secret_pasted_into_the_model_name_is_scrubbed_at_the_serialization_boundary()
        {
            // Round-3 sanitized only endpoint/database. A secret smuggled into modelName reached connections.json. The
            // serialization-boundary scrub now redacts EVERY string field of the record.
            var secret = "MN" + Guid.NewGuid().ToString("N");
            ConnectionRegistry.Remember("xmla", "powerbi://x/mn", "DS", modelName: PwdKey() + secret);
            var text = File.ReadAllText(Path.Combine(_root, "connections.json"));
            Assert.Contains("powerbi://x/mn", text);   // the record still persisted; only the secret field was redacted
            AssertNoFileContains(secret);
        }

        [Fact]
        public void A_secret_shaped_tenant_on_a_failed_event_never_reaches_the_history()
        {
            // A secret smuggled into the TenantId field of a failed-open event reached the timeline verbatim (only
            // endpoint/database were scrubbed). The serialization-boundary scrub now covers it too.
            var secret = "TN" + Guid.NewGuid().ToString("N");
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "tn", Kind = "signin", Endpoint = "powerbi://x/tn", TenantId = "client_" + "secret=" + secret, Ok = false });
            AssertNoFileContains(secret);
        }

        // ---- CRITICAL 1 (round 6): COMPACT key spellings (clientsecret / accountkey / sharedaccesssignature — no camel or
        // underscore boundary to tokenize) and a secret NESTED inside an innocent key's QUOTED value both passed the
        // round-5 detector as innocent and reached disk. Route each of the four reviewer examples through the REAL
        // persistence path (registry modelName + history detail) and byte-scan every written file for the sentinel. ----

        [Fact]
        public void Compact_and_nested_secret_key_names_never_reach_any_persisted_file()
        {
            // Each key=value built at runtime (never a source literal). The four reviewer examples: three compact compound
            // keys plus one secret nested inside an innocent key's quoted value.
            var examples = new[]
            {
                "client" + "secret",
                "account" + "key",
                "sharedaccess" + "signature",
                "NESTED",   // marker for the quoted-nested case, handled below
            };
            foreach (var which in examples)
            {
                var secret = "R6" + Guid.NewGuid().ToString("N");
                var payload = which == "NESTED"
                    ? "Metadata=\"x?access" + "_token=" + secret + "\""   // secret hidden inside an innocent key's quotes
                    : which + "=" + secret;                                // a compact compound key=value
                // The endpoint stays clean so the record/event persists; the secret rides a free field the serialization
                // -boundary scrub must catch (modelName on the registry, detail on the history).
                var ep = "powerbi://x/r6-" + which.Replace("\"", "");
                ConnectionRegistry.Remember("xmla", ep, "DS", modelName: payload);
                ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "r6", Kind = "connect", Endpoint = ep, Detail = payload, Ok = false });
                Assert.True(XmlaAuthHint.ContainsSecret(payload));   // the detector now recognises it as a credential
                AssertNoFileContains(secret);                        // and it never reached connections.json or connection-history.json
            }
        }

        // ---- CRITICAL 1 (round 7): the STANDARD lowercase Azure credential family (sharedaccesskey=, subscriptionkey=,
        // primarykey=, secondarykey=) and a secret NESTED behind ESCAPED quotes (doubled "" or backslash \") both passed the
        // round-6 detector and reached disk. Route each through the REAL persistence path (registry modelName + history
        // detail) and byte-scan every written file for the sentinel; the earlier four compact/nested cases stay covered. ----

        [Fact]
        public void Standard_azure_keys_and_escaped_nested_secrets_never_reach_any_persisted_file()
        {
            // Every key=value built at RUNTIME (never a source literal). Reviewer examples: the lowercase Azure key family,
            // the doubled-quote nested form, the backslash nested form, plus the earlier four still covered.
            var cases = new Func<string, string>[]
            {
                s => "shared" + "accesskey=" + s,                          // lowercase Azure SharedAccessKey (round-7)
                s => "subscription" + "key=" + s,                         // API Management subscription key
                s => "primary" + "key=" + s,                              // Cosmos / Cognitive Services key
                s => "secondary" + "key=" + s,
                s => "Metadata=\"x?access" + "_token=\"\"" + s + "\"\"\"", // secret behind DOUBLED "" quotes inside an innocent key
                s => "Metadata=\"x?access" + "_token=\\\"" + s + "\\\"\"", // secret behind BACKSLASH \" quotes inside an innocent key
                s => "client" + "secret=" + s,                            // ...the earlier four compact/nested cases still covered
                s => "account" + "key=" + s,
                s => "sharedaccess" + "signature=" + s,
                s => "Metadata=\"x?access" + "_token=" + s + "\"",        // secret nested UNQUOTED inside an innocent key (round-6)
            };
            var i = 0;
            foreach (var make in cases)
            {
                var secret = "R7" + Guid.NewGuid().ToString("N");
                var payload = make(secret);
                var ep = "powerbi://x/r7-" + (i++);
                ConnectionRegistry.Remember("xmla", ep, "DS", modelName: payload);
                ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "r7", Kind = "connect", Endpoint = ep, Detail = payload, Ok = false });
                Assert.True(XmlaAuthHint.ContainsSecret(payload));   // the detector now recognises each as a credential
                AssertNoFileContains(secret);                        // and none reached connections.json or connection-history.json
            }
        }

        // ---- CRITICAL 1 (round 8): the SAS-URI signature key ?sig=... (the token that GRANTS an Azure Shared Access
        // Signature) was excluded from the secret markers on an obsolete "sig is a substring of assign/design" rationale.
        // Matching is EXACT whole-word now, so "sig" is a marker (assign/design stay safe). Prove the detector catches it
        // and byte-scan every persisted file after routing ?sig=<sentinel> through BOTH the history and the registry. ----

        [Fact]
        public void A_sas_signature_query_key_is_detected_and_never_reaches_any_persisted_file()
        {
            var secret = "SIG" + Guid.NewGuid().ToString("N");
            var endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS?ss=1&" + "si" + "g=" + secret;   // ?sig=<granting signature>
            Assert.True(XmlaAuthHint.ContainsSecret(endpoint));   // the SAS signature key is now recognised as a credential
            // design=/assign= are NOT secrets — exact whole-word matching keeps them safe (the reason the old exclusion existed).
            Assert.False(XmlaAuthHint.ContainsSecret("powerbi://x/ws?design=blue&assign=kane"));
            // History (fail-open): the token is redacted before the write, never persisted.
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "sig", Kind = "connect", Endpoint = endpoint, Ok = false });
            // Registry (fail-closed): a credential-bearing endpoint that can't reduce to a safe address is refused outright.
            Assert.Throws<ArgumentException>(() => ConnectionRegistry.Remember("xmla", endpoint, "DS"));
            AssertNoFileContains(secret);
        }

        // ---- HIGH 3 (round 4): SafeCatalog cut a dataset name at the FIRST '&'/'?'/';' unconditionally, corrupting a
        // legitimate name ("Sales & Marketing" -> "Sales"). Only a real credential tail is cut now. ----

        [Fact]
        public void A_dataset_name_with_an_ampersand_survives_intact_end_to_end()
        {
            var parsed = ConnectionInput.Parse("powerbi://x/amp", "Sales & Marketing");
            Assert.Equal("Sales & Marketing", parsed.Database);   // a bare '&' inside a normal name is untouched
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/amp", "Sales & Marketing");
            Assert.Equal("Sales & Marketing", ConnectionRegistry.Find(r.Id).Database);   // survived the write intact
            // ...but a REAL credential tail is still cut off the dataset name.
            var scrubbed = ConnectionInput.Parse("powerbi://x/amp2", "Sales;" + PwdKey() + FakeJwt());
            Assert.Equal("Sales", scrubbed.Database);
        }

        // ---- HIGH 2 (round 3): the saved-account commit is linearized by ticket, so two interleaved connect/open
        // winners can't repoint the tenant-wide account out of order (A wins+pauses; B wins+commits Carol; A resumes). ----

        // The saved-account barrier is keyed PER SLOT (family+tenant, HIGH 3); these interleaving pins are re-keyed to a
        // single slot so within-slot ordering is proven exactly as before, and a cross-slot pin follows.
        private const string Slot = "interactive|contoso.com";
        private const string OtherSlot = "interactive|fabrikam.com";

        [Fact]
        public void An_older_auth_commit_is_refused_after_a_newer_win_committed()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var a = engine.MintAuthIntentForTest();   // A starts (older ticket)
            var b = engine.MintAuthIntentForTest();   // B starts (newer ticket)
            // B wins its swap and commits Carol FIRST...
            Assert.True(engine.ClaimAuthCommit(b, Slot));
            // ...A resumes: committing Bob now would leave the saved pointer on Bob while Carol is live — refused.
            Assert.False(engine.ClaimAuthCommit(a, Slot));
            // A brand-new, newer authorization may still move the pointer.
            var c = engine.MintAuthIntentForTest();
            Assert.True(engine.ClaimAuthCommit(c, Slot));
        }

        [Fact]
        public void Sequential_auth_commits_each_win_in_ticket_order()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var a = engine.MintAuthIntentForTest();
            Assert.True(engine.ClaimAuthCommit(a, Slot));    // A commits
            var b = engine.MintAuthIntentForTest();
            Assert.True(engine.ClaimAuthCommit(b, Slot));    // B (newer) commits after A — in order, both win
            Assert.False(engine.ClaimAuthCommit(a, Slot));   // A can never re-commit an older ticket
        }

        [Fact]
        public void A_silent_winner_advances_the_barrier_and_invalidates_an_older_pending_commit()
        {
            // HIGH 2 (round 4): a winner with NO pending record (silent reuse / azcli) must STILL advance the barrier, so
            // an older forced switch paused mid-flight can't commit its account behind the newer silent winner. Round-3
            // skipped the barrier entirely for a silent winner, so the older commit still landed.
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var a = engine.MintAuthIntentForTest();   // older forced switch, paused before its commit
            var b = engine.MintAuthIntentForTest();   // newer SILENT connect wins the live swap
            engine.AdvanceAuthBarrierForTest(b, Slot);      // commits nothing, but advances the barrier
            Assert.False(engine.ClaimAuthCommit(a, Slot));  // A resuming is refused — no stale account lands behind B
        }

        [Fact]
        public void A_silent_read_only_snapshot_does_not_advance_the_barrier_and_a_pending_commit_still_lands()
        {
            // HIGH 2a (round 6): the round-5 barrier let a SILENT READ-ONLY snapshot (a compare/reference that neither
            // signed in fresh NOR owns the live connection) advance the barrier — so a later silent compare that merely
            // READ saved Alice invalidated a forced open_live's pending Bob commit (live Bob, saved pointer stuck on Alice).
            // A read-only snapshot must NOT touch the barrier; only a fresh sign-in or a live-swap winner does.
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var open = engine.MintAuthIntentForTest();     // a forced open_live signs in as Bob (older ticket), pauses before commit
            var compare = engine.MintAuthIntentForTest();  // a later SILENT read-only compare reads saved Alice (newer ticket)
            engine.AdvanceAuthBarrierReadOnlyForTest(compare, Slot);   // must be a NO-OP — the read-only snapshot never advances
            Assert.True(engine.ClaimAuthCommit(open, Slot));           // Bob's pending commit still lands (not stolen by the read)
            // Contrast: a silent LIVE-SWAP winner (a real connect) at the same newer ticket WOULD advance and invalidate it.
            var sessions2 = new SessionManager();
            using var engine2 = new LocalEngine(sessions2);
            var open2 = engine2.MintAuthIntentForTest();
            var connect2 = engine2.MintAuthIntentForTest();
            engine2.AdvanceAuthBarrierForTest(connect2, Slot);         // a swap winner DOES advance, even with no record
            Assert.False(engine2.ClaimAuthCommit(open2, Slot));        // now the older pending commit is correctly refused
        }

        [Fact]
        public void Two_processes_committing_the_shared_record_leave_the_newest_claim_on_disk_in_both_orderings()
        {
            // HIGH 2 (round 7): the cross-process claim is a DURABLE per-slot counter compared-and-swapped under the record
            // lock (not wall-clock — a clock correction / VM restore must never make a newer sign-in lose). Two "processes" =
            // two independent calls against the SAME record path (the file lock + envelope are on-disk, cross-process state).
            // Prove the NEWEST claim wins on disk regardless of which call writes last, WITHOUT depending on time at all.
            foreach (var newerFirst in new[] { false, true })
            {
                var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                Directory.CreateDirectory(_root);
                var older = 1L;
                var newer = 2L;
                // Each "process" write folds its claim + record payload into the one envelope; the CAS refuses a stale one.
                bool Write(long claim) => EntraToken.TryClaimRecordWrite(path, claim, () => "acct-" + claim);
                if (newerFirst) { Assert.True(Write(newer)); Assert.False(Write(older)); }   // the stale (older) write is refused
                else { Assert.True(Write(older)); Assert.True(Write(newer)); }
                Assert.Equal(newer, EntraToken.ReadPersistedSeq(path));    // the newest claim is the final on-disk state either way
                Assert.Contains("acct-" + newer, File.ReadAllText(path));  // and it carries the newest record payload, not the older
                Assert.False(EntraToken.TryClaimRecordWrite(path, older, () => "acct-" + older));   // re-submitting the older claim is refused
                Assert.Equal(newer, EntraToken.ReadPersistedSeq(path));
            }
        }

        [Fact]
        public void Two_stagings_that_mint_before_either_commits_let_the_newer_claim_win_in_both_orders()
        {
            // HIGH 2 (round 8): MintClaim read persisted+1 WITHOUT reserving, so two concurrent stagings both saw N and both
            // got N+1 — the CAS then refused the SECOND (newer) commit as EQUAL and the OLDER identity kept the pointer.
            // Reserving the claim durably under the lock (bumping IssuedSeq) hands the two stagings DISTINCT claims, so the
            // newer always out-CASes the older. Call the REAL allocator TWICE before either write, then commit in both orders.
            foreach (var newerFirst in new[] { false, true })
            {
                var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                Directory.CreateDirectory(_root);
                var older = EntraToken.MintClaim(path);      // staged first
                var newer = EntraToken.MintClaim(path);      // staged second, BEFORE either commit
                Assert.True(newer > older);                  // distinct, monotonic claims — not the same N+1 the bug handed out
                bool Commit(long claim) => EntraToken.TryClaimRecordWrite(path, claim, () => "acct-" + claim);
                if (newerFirst) { Assert.True(Commit(newer)); Assert.False(Commit(older)); }   // older refused after newer wins
                else { Assert.True(Commit(older)); Assert.True(Commit(newer)); }               // newer still wins committing last
                Assert.Equal(newer, EntraToken.ReadPersistedSeq(path));       // the NEWER identity holds the pointer either way
                Assert.Contains("acct-" + newer, File.ReadAllText(path));     // and carries the newer record payload
            }
        }

        [Fact]
        public void A_reservation_only_envelope_still_triggers_capture_and_commit_on_the_next_open()
        {
            // HIGH 1 (round 9): the capture gate used File.Exists, so a reservation-only envelope (a claim minted before
            // any record committed — the file EXISTS but RecordJson=null) was mistaken for a saved sign-in: the next open
            // SKIPPED AuthenticateAsync and never captured/committed the account. Decide on the DESERIALIZED record
            // (ShouldCaptureRecord) instead, so a reservation-only envelope still authenticates, captures, and commits.
            var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            Directory.CreateDirectory(_root);
            var reserved = EntraToken.MintClaim(path);   // a first sign-in reserved a claim, then the open failed before commit
            Assert.True(reserved > 0);
            Assert.True(File.Exists(path));              // the file EXISTS — the old File.Exists gate would WRONGLY skip capture
            Assert.True(EntraToken.ShouldCaptureRecord(path, forceReauth: false));   // but no usable record loaded → we DO capture
            // The next open authenticates, captures a distinct higher claim, and commits its record — the pointer lands.
            var claim = EntraToken.MintClaim(path);
            Assert.True(claim > reserved);
            Assert.True(EntraToken.TryClaimRecordWrite(path, claim, () => "acct-captured"));
            Assert.Equal(claim, EntraToken.ReadPersistedSeq(path));
            Assert.Contains("acct-captured", File.ReadAllText(path));
        }

        // The exact JSON AuthenticationRecord.Deserialize reads (Azure.Identity has no public ctor in this version), so
        // LoadRecord round-trips it back to a record whose Username is `username`.
        private static string RecordJsonFor(string username, string tenant) =>
            "{\"username\":\"" + username + "\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"oid-" +
            username + "." + tenant + "\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"client\",\"version\":\"1.0\"}";

        [Fact]
        public async Task BuildCredentialAsync_reports_the_pinned_account_even_when_another_process_commits_a_different_one_after_the_read()
        {
            // ROUND-10 HIGH (the PRODUCTION interleaving): credential construction and the reported identity used to come
            // from SEPARATE disk reads. Process A pins Alice; process B commits Bob between A's reads; A then opened as the
            // PINNED Alice but a later disk read reported Bob — the wrong-live-account defect. The read-once fix loads the
            // record EXACTLY once, so the reported account is the identity the credential was actually built with.
            if (!OperatingSystem.IsWindows()) return;   // the encrypted persistent MSAL record is Windows-only (PersistenceSupported)
            var tenant = Guid.NewGuid().ToString();      // a unique slot: the real auth dir can never collide with a user's record
            var path = EntraToken.RecordPathForTests("interactive", tenant);
            Assert.NotNull(path);
            try
            {
                // Seed the saved record for Alice — the account the credential silently reuses + PINS (usable → no capture).
                Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("alice@contoso.com", tenant)));
                // Build the credential — one read, pins Alice; a usable record means NO AuthenticateAsync (no network).
                var prepared = await EntraToken.BuildCredentialAsync("interactive", tenant, System.Threading.CancellationToken.None);
                // "Another process" now commits Bob to the SAME slot, AFTER our single read.
                Assert.True(EntraToken.TryClaimRecordWrite(path, 2, () => RecordJsonFor("bob@contoso.com", tenant)));
                // The reported live identity is the PINNED Alice — the credential we actually built — never the later Bob.
                Assert.Equal("alice@contoso.com", prepared.Account);
                // And the disk REALLY changed to Bob: the old second-read code would have mis-reported this Bob as live.
                Assert.Equal("bob@contoso.com", EntraToken.ReadSavedAccount("interactive", tenant)?.Username);
            }
            finally { foreach (var f in new[] { path, path + ".lock", path + ".seq", path + ".tmp" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } } }
        }

        [Fact]
        public async Task A_failed_open_attributes_to_the_prepared_pinned_account_not_a_sibling_committed_pointer()
        {
            // ROUND-11 MEDIUM: failed-open attribution read prepared?.PendingAccount ?? saved. On a SILENT reuse the credential
            // pins Alice WITHOUT capturing a new record, so PendingAccount is null and the code fell through to the disk
            // pointer — which a sibling process could have moved to Bob AFTER our read. The Alice-authenticated open's failure
            // was then blamed on Bob. The fix reads prepared?.Account (captured-else-pinned: the identity the credential was
            // ACTUALLY built with), so the failure names Alice — the account in play — even though disk now says Bob.
            if (!OperatingSystem.IsWindows()) return;   // BuildCredentialAsync's encrypted MSAL record read is Windows-only (PersistenceSupported)
            var tenant = Guid.NewGuid().ToString();      // a unique slot: the real auth dir can never collide with a user's record
            var path = EntraToken.RecordPathForTests("interactive", tenant);
            Assert.NotNull(path);
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/round11", "DS", "DS", tenant, "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            try
            {
                // Seed + pin Alice: a usable saved record the credential silently reuses (no capture ⇒ PendingAccount stays null).
                Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("alice@contoso.com", tenant)));
                var prepared = await EntraToken.BuildCredentialAsync("interactive", tenant, System.Threading.CancellationToken.None);
                Assert.Null(prepared.PendingAccount);                    // silent reuse: nothing new captured (the OLD code's ?? fell through here)…
                Assert.Equal("alice@contoso.com", prepared.Account);     // …but the credential IS pinned to Alice
                // "Another process" commits Bob to the SAME slot AFTER our read — the disk pointer now says Bob.
                Assert.True(EntraToken.TryClaimRecordWrite(path, 2, () => RecordJsonFor("bob@contoso.com", tenant)));
                Assert.Equal("bob@contoso.com", EntraToken.ReadSavedAccount("interactive", tenant)?.Username);
                // The Alice-authenticated open now FAILS through the production failure-recorder: attribution must name the
                // prepared (pinned) Alice, never the later Bob pointer.
                engine.RecordFailedOpenHistory(r.Endpoint, r.Database, tenant, "interactive", forceReauth: false, prepared: prepared, detail: "boom");
                var ev = ConnectionHistory.List(r.Id).Single();
                Assert.False(ev.Ok);
                Assert.Equal("open", ev.Kind);                           // non-forced failed reuse
                Assert.Equal("alice@contoso.com", ev.Account);           // the prepared (pinned) identity, NOT the sibling-committed Bob
            }
            finally { foreach (var f in new[] { path, path + ".lock", path + ".seq", path + ".tmp" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } } }
        }

        [Fact]
        public void A_reservation_only_envelope_never_reaches_Azure_Identity_as_a_pinned_record()
        {
            // ROUND-10 HIGH (parsing flaw): LoadRecord's `?? text` fallback deserialized the ENTIRE envelope shell when
            // RecordJson was null — MSAL's lenient Deserialize handed back a BLANK pseudo-record that BuildCredentialAsync
            // then PINNED as the credential's identity. The exact snapshot BuildCredentialAsync pins for a reservation-only
            // envelope (a claim minted before any record committed) must be NO record, forcing an honest capture instead.
            if (!OperatingSystem.IsWindows()) return;   // BuildCredentialAsync's record read is Windows-only (PersistenceSupported)
            var tenant = Guid.NewGuid().ToString();
            var path = EntraToken.RecordPathForTests("interactive", tenant);
            Assert.NotNull(path);
            try
            {
                Assert.True(EntraToken.MintClaim(path) > 0);   // a first sign-in reserved a claim, then failed before commit
                Assert.True(File.Exists(path));                // the reservation-only envelope EXISTS (RecordJson=null)
                // The record BuildCredentialAsync would pin + report is NULL — never a blank pseudo-record to Azure Identity.
                var pinned = EntraToken.LoadPinnedRecordForTests("interactive", tenant, forceReauth: false);
                Assert.Null(pinned);
                // …so BuildCredentialAsync captures a real account rather than silently reusing a blank pinned identity.
                Assert.True(EntraToken.ShouldCaptureRecord(pinned, forceReauth: false));
            }
            finally { foreach (var f in new[] { path, path + ".lock", path + ".seq", path + ".tmp" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } } }
        }

        [Fact]
        public void A_reservation_failure_yields_no_claim_so_the_commit_is_a_no_op_and_the_pointer_is_unchanged()
        {
            // MEDIUM 2 (round 9): MintClaim's fallback returned persisted+1 WITHOUT reserving, so a lock timeout / write
            // failure could hand two stagings the SAME claim and let whichever committed FIRST win — pinning the OLDER
            // identity. Now a reservation failure returns NO committable claim (0): the sign-in works for the session but
            // its commit is skipped, leaving the established saved pointer untouched.
            var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            Directory.CreateDirectory(_root);
            Assert.True(EntraToken.TryClaimRecordWrite(path, 3, () => "acct-established"));   // the pointer we must not disturb
            long claim;
            // Force the reservation to fail: hold MintClaim's per-path lock so all its acquire retries time out.
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                claim = EntraToken.MintClaim(path);
            }
            Assert.Equal(0, claim);                       // NO committable claim, not an unreserved persisted+1
            Assert.False(EntraToken.TryClaimRecordWrite(path, claim, () => "acct-usurper"));   // committing a no-claim is a no-op
            Assert.Equal(3, EntraToken.ReadPersistedSeq(path));           // the established pointer stands
            var onDisk = File.ReadAllText(path);
            Assert.Contains("acct-established", onDisk);
            Assert.DoesNotContain("acct-usurper", onDisk);
        }

        [Fact]
        public void The_record_and_its_claim_are_one_file_so_a_crash_leaves_no_stale_sequence_window()
        {
            // HIGH 2 (round 7): round-6 wrote the record THEN a separate ".seq" sidecar — a crash between them left a new
            // record beside a stale sequence. Folding the claim INTO the record envelope removes the window entirely:
            // exactly one file, no sidecar, and the persisted claim is readable from that single file.
            var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            Directory.CreateDirectory(_root);
            Assert.True(EntraToken.TryClaimRecordWrite(path, 7, () => "acct-7"));
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".seq"));   // the sidecar concept is gone — the claim rides inside the record file
            Assert.Equal(7, EntraToken.ReadPersistedSeq(path));
        }

        [Fact]
        public void A_legacy_seq_sidecar_is_honored_once_then_retired_by_the_folded_envelope()
        {
            // HIGH 2 (round 7): an engine upgraded from the sidecar format must not reset the high-water mark. The migration
            // read honors a pre-envelope ".seq" sidecar once (so a stale in-flight write can't win across the format change),
            // and the first envelope write supersedes and deletes it.
            var path = Path.Combine(_root, "record-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            Directory.CreateDirectory(_root);
            File.WriteAllText(path, "legacy-bare-record");         // a pre-envelope bare MSAL record (opaque here)
            File.WriteAllText(path + ".seq", "5");                 // and its separate legacy claim sidecar
            Assert.Equal(5, EntraToken.ReadPersistedSeq(path));    // the sidecar high-water mark is honored during migration
            Assert.False(EntraToken.TryClaimRecordWrite(path, 5, () => "acct-5"));   // a stale/equal claim is still refused across the format change
            Assert.True(EntraToken.TryClaimRecordWrite(path, 6, () => "acct-6"));    // a newer claim wins and folds into the envelope
            Assert.Equal(6, EntraToken.ReadPersistedSeq(path));
            Assert.False(File.Exists(path + ".seq"));              // the migrated sidecar is retired
        }

        [Fact]
        public void A_commit_in_one_slot_never_invalidates_a_pending_commit_in_another()
        {
            // HIGH 3 (round 5): the barrier was GLOBAL, so a silent read/commit in tenant B invalidated tenant A's pending
            // FIRST sign-in (A re-prompted next run). Per-slot: a newer ticket committed in OtherSlot must NOT block an
            // older ticket's first commit in Slot — the two saved-account records are independent.
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var a = engine.MintAuthIntentForTest();   // tenant A's first sign-in starts (older), pauses before commit
            var b = engine.MintAuthIntentForTest();   // tenant B's silent read wins (newer) and advances its OWN slot
            engine.AdvanceAuthBarrierForTest(b, OtherSlot);
            Assert.True(engine.ClaimAuthCommit(a, Slot));   // A still commits in its own slot — B's newer ticket is irrelevant here
        }

        [Fact]
        public void Interleaved_auth_commits_leave_the_newest_winner_on_disk_in_both_orderings()
        {
            // HIGH 2 (round 4): the gate is held ACROSS the write (not just the claim), so two interleaved commits land in
            // ticket order — the older can never write LAST behind the newer. Prove it in BOTH submission orders.
            foreach (var newerFirst in new[] { false, true })
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                var older = engine.MintAuthIntentForTest();
                var newer = engine.MintAuthIntentForTest();
                long lastWritten = 0;
                // The older winner's write is slow; because the gate is held across it, the newer either waits its turn
                // or finds the barrier already advanced past it — either way the final on-disk ticket is the NEWEST.
                void WriteOlder() => engine.CommitAuthWriteForTest(older, Slot, () => { System.Threading.Thread.Sleep(40); System.Threading.Volatile.Write(ref lastWritten, older); });
                void WriteNewer() => engine.CommitAuthWriteForTest(newer, Slot, () => System.Threading.Volatile.Write(ref lastWritten, newer));
                var t1 = new System.Threading.Thread(newerFirst ? WriteNewer : WriteOlder);
                var t2 = new System.Threading.Thread(newerFirst ? WriteOlder : WriteNewer);
                t1.Start(); System.Threading.Thread.Sleep(5); t2.Start(); t1.Join(); t2.Join();
                Assert.Equal(newer, System.Threading.Volatile.Read(ref lastWritten));   // newest winner is the final state
            }
        }

        // ---- History: beside the registry, capped, filterable, credential-free, fail-open ----

        [Fact]
        public void History_is_written_beside_the_registry_not_inside_it()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "abc", Kind = "connect", Account = "kane@contoso.com", Endpoint = "powerbi://x/h", Ok = true });

            Assert.True(File.Exists(Path.Combine(_root, "connection-history.json")));
            // The permissions-bearing registry is a DIFFERENT file — history writes must never touch it.
            Assert.False(File.Exists(Path.Combine(_root, "connections.json")));
            var events = ConnectionHistory.List();
            Assert.Single(events);
            Assert.Equal("connect", events[0].Kind);
            Assert.Equal("kane@contoso.com", events[0].Account);
        }

        [Fact]
        public void History_is_newest_first_and_filterable_by_connection()
        {
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "a", Kind = "open", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "b", Kind = "connect", Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "a", Kind = "switch", Ok = true });

            var all = ConnectionHistory.List();
            Assert.Equal(3, all.Count);
            Assert.Equal("switch", all[0].Kind);   // most recent first

            var justA = ConnectionHistory.List("a");
            Assert.Equal(2, justA.Count);
            Assert.All(justA, e => Assert.Equal("a", e.Id));
        }

        [Fact]
        public void History_caps_per_connection_to_bound_a_busy_target()
        {
            for (var i = 0; i < ConnectionHistory.MaxPerConnection + 10; i++)
                ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "busy", Kind = "open", Ok = true, Detail = i.ToString() });

            Assert.Equal(ConnectionHistory.MaxPerConnection, ConnectionHistory.List("busy").Count);
            Assert.Equal("34", ConnectionHistory.List("busy")[0].Detail);   // kept the newest (0..34 appended, newest = 34)
        }

        [Fact]
        public void History_caps_the_total_timeline()
        {
            // Spread across many targets so the per-connection cap can't be what trims it — the global ceiling must.
            for (var i = 0; i < ConnectionHistory.MaxTotal + 40; i++)
                ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "t" + i, Kind = "connect", Ok = true });

            Assert.Equal(ConnectionHistory.MaxTotal, ConnectionHistory.List().Count);
        }

        [Fact]
        public void History_holds_no_secret()
        {
            // Feed token-like values through the REAL Append persistence path (it parses the endpoint to safe coordinates),
            // then scan the file: neither a pasted credential nor a JWT may survive to disk (HIGH 2 / MED 9).
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "s", Kind = "connect", Account = "kane@contoso.com", TenantId = "contoso.com", Endpoint = CredString("powerbi://x/s", null), Ok = true });
            var text = File.ReadAllText(Path.Combine(_root, "connection-history.json"));
            Assert.Contains("powerbi://x/s", text);                          // the safe coordinate remains
            Assert.DoesNotContain("word=", text.ToLowerInvariant());         // the pasted credential was stripped before the write
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+"), text);
            var keys = System.Text.Json.JsonDocument.Parse(text).RootElement
                .EnumerateArray().SelectMany(o => o.EnumerateObject().Select(p => p.Name.ToLowerInvariant())).ToHashSet();
            Assert.DoesNotContain("password", keys);
            Assert.DoesNotContain("token", keys);
            Assert.DoesNotContain("accesstoken", keys);
            Assert.DoesNotContain("secret", keys);
            // Scan the serialized VALUES too, not just property names — a field that ever leaked a bearer must fail here.
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+"), text);   // no JWT
            Assert.DoesNotContain("bearer ", text.ToLowerInvariant());
            Assert.DoesNotContain("password=", text.ToLowerInvariant());
        }

        // ---- HIGH 1: the DISPLAYED account is what the NEXT open will use — the tenant-wide record wins over the hint ----

        [Fact]
        public void The_probe_prefers_the_tenant_wide_record_and_names_the_previous_account()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/probe", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            // A sibling model switched the tenant-wide sign-in to Bob; THIS target's own last-used hint still says Alice.
            EntraToken.SavedAccountForTests = (mode, tenant) => "bob@contoso.com";
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                var p = engine.ProbeConnectionAccountsAsync().GetAwaiter().GetResult().Single(x => x.Id == r.Id);
                Assert.Equal("bob@contoso.com", p.Account);          // what the NEXT open will actually sign in as
                Assert.Equal("alice@contoso.com", p.PreviousAccount); // the stale per-target hint, surfaced honestly as "was"
            }
            finally { EntraToken.SavedAccountForTests = null; }
        }

        [Fact]
        public void The_probe_omits_the_previous_account_when_it_matches_the_current_one()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/probe2", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            EntraToken.SavedAccountForTests = (mode, tenant) => "alice@contoso.com";   // record and hint agree
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                var p = engine.ProbeConnectionAccountsAsync().GetAwaiter().GetResult().Single(x => x.Id == r.Id);
                Assert.Equal("alice@contoso.com", p.Account);
                Assert.Null(p.PreviousAccount);   // no spurious "was" when nothing changed
            }
            finally { EntraToken.SavedAccountForTests = null; }
        }

        [Fact]
        public void The_probe_reports_unknown_and_only_provenance_when_no_sign_in_record_exists()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/probe3", "DS", "DS", "contoso.com", "azcli", "kane@contoso.com");
            // azcli keeps no MSAL record → the account the NEXT open uses is genuinely UNKNOWN. The per-target last-used
            // hint must NOT masquerade as the prediction (HIGH 3): it surfaces ONLY as provenance.
            EntraToken.SavedAccountForTests = (mode, tenant) => null;
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                var p = engine.ProbeConnectionAccountsAsync().GetAwaiter().GetResult().Single(x => x.Id == r.Id);
                Assert.Null(p.Account);                             // unknown — never guessed from last-used
                Assert.Equal("kane@contoso.com", p.PreviousAccount); // surfaced ONLY as "last opened as" provenance
            }
            finally { EntraToken.SavedAccountForTests = null; }
        }

        // ---- HIGH 7: the two new reads are reachable from the MCP (agent) door, not just RPC ----

        [Fact]
        public async Task The_probe_and_history_reads_are_reachable_from_the_MCP_door()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/mcp", "DS", "DS", "contoso.com", "interactive", "kane@contoso.com");
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = r.Id, Kind = "open", Account = "kane@contoso.com", Endpoint = r.Endpoint, Ok = true });
            EntraToken.SavedAccountForTests = (mode, tenant) => "kane@contoso.com";
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions);
                var probes = await McpTools.ProbeConnectionAccounts(engine);
                Assert.Contains(probes, p => p.Id == r.Id && p.Account == "kane@contoso.com");
                var hist = await McpTools.ListConnectionHistory(engine, r.Id, 50);
                Assert.Contains(hist, e => e.Id == r.Id && e.Kind == "open");
                Assert.All(hist, e => Assert.Equal(r.Id, e.Id));   // filtered to the one connection
            }
            finally { EntraToken.SavedAccountForTests = null; }
        }

        // ---- MEDIUM 9: history semantics — a same-account re-sign is not a "switch"; failed events are recorded ----

        [Fact]
        public void A_forced_reauth_as_the_same_account_is_a_signin_not_a_switch()
        {
            // The exact decision RecordConnectionHistory logs for an open (extracted so it is unit-testable).
            Assert.Equal("open", LocalEngine.ConnectHistoryKind(false, "alice@contoso.com", "alice@contoso.com"));
            Assert.Equal("signin", LocalEngine.ConnectHistoryKind(true, "alice@contoso.com", "alice@contoso.com"));   // no-op re-sign — never spam a "switch"
            Assert.Equal("signin", LocalEngine.ConnectHistoryKind(true, "ALICE@contoso.com", "alice@contoso.com"));   // case-insensitive
            Assert.Equal("switch", LocalEngine.ConnectHistoryKind(true, "alice@contoso.com", "bob@contoso.com"));     // a real account change
            Assert.Equal("switch", LocalEngine.ConnectHistoryKind(true, null, "bob@contoso.com"));                    // first named identity on a forced pick
        }

        [Fact]
        public async Task A_cancelled_forced_sign_in_is_an_unattributed_cancelled_signin_not_a_switch()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/failopen", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            // Drive the REAL OpenLiveAsync failure boundary via the seam (MED 6): a CANCELLED forced sign-in — the human
            // dismissed the picker before ANY account was chosen (no PreparedCredential). It must NOT be blamed on the
            // saved account (the round-3 code logged a spurious "switch to Bob" for a sign-in that never happened, MED 4).
            EntraToken.SavedAccountForTests = (mode, tenant) => "bob@contoso.com";
            engine.OpenLiveFailureProbeForTests = () => throw new OperationCanceledException("sign-in cancelled");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    engine.OpenLiveAsync("powerbi://x/failopen", "DS", "interactive", null, "contoso.com", forceReauth: true));
            }
            finally { EntraToken.SavedAccountForTests = null; }
            var ev = ConnectionHistory.List(r.Id).Single();
            Assert.False(ev.Ok);
            Assert.Equal("signin", ev.Kind);           // a cancelled forced sign-in is a signin, never a spurious "switch"
            Assert.Null(ev.Account);                    // NO account attribution — no account was ever chosen (MED 4)
            Assert.Contains("cancelled before an account was chosen", ev.Detail);
            Assert.Equal(r.Id, ev.Id);                  // exact endpoint+dataset match → still carries the connection id
        }

        [Fact]
        public async Task A_wrong_tenant_forced_sign_in_reads_as_a_neutral_failure_not_a_false_cancellation()
        {
            // MED 4 (round 5): a forced sign-in that prepared NO credential is not always a cancellation — a wrong-tenant
            // AADSTS reject or an expired device code also lands here. The round-4 copy called EVERY such failure
            // "cancelled", blaming the user. Now it classifies: only a true OperationCanceled/MSAL-cancel says "cancelled";
            // a wrong-tenant reject reads as a NEUTRAL "failed" with the category (never "you cancelled").
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/wrongtenant", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            engine.OpenLiveFailureProbeForTests = () => throw new InvalidOperationException("AADSTS50020: user account does not exist in tenant");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.OpenLiveAsync("powerbi://x/wrongtenant", "DS", "interactive", null, "contoso.com", forceReauth: true));
            var ev = ConnectionHistory.List(r.Id).Single();
            Assert.Equal("signin", ev.Kind);
            Assert.False(ev.Ok);
            Assert.Null(ev.Account);                                          // no account was chosen — never borrow the saved one
            Assert.Contains("failed before an account was chosen", ev.Detail);
            Assert.DoesNotContain("cancelled", ev.Detail);                    // a wrong-tenant reject is NOT a cancellation
            Assert.Contains("identity provider rejected", ev.Detail);        // the honest failure category
        }

        [Fact]
        public void The_cancellation_classifier_separates_a_user_cancel_from_an_auth_reject()
        {
            // Unit-level pin for MED 4: OperationCanceled (and an MSAL user-cancel shape) = cancellation; a wrong-tenant /
            // expired / consent failure = a neutral category, never mislabeled a cancellation.
            Assert.True(XmlaAuthHint.IsUserCancellation(new OperationCanceledException("sign-in cancelled")));
            Assert.True(XmlaAuthHint.IsUserCancellation(new Exception("outer", MsalStub("authentication_canceled: user canceled"))));
            Assert.False(XmlaAuthHint.IsUserCancellation(new InvalidOperationException("AADSTS50020: no access")));
            Assert.Equal("the identity provider rejected the sign-in", XmlaAuthHint.SignInFailureCategory(new Exception("AADSTS700016")));
            Assert.Equal("the sign-in timed out or expired", XmlaAuthHint.SignInFailureCategory(new Exception("code_expired")));
            Assert.Equal("the sign-in did not complete", XmlaAuthHint.SignInFailureCategory(new Exception("network unreachable")));
        }

        // A stand-in for an MSAL exception (type NAME contains "Msal") so the cancel classifier can be exercised offline.
        private sealed class MsalStubException : Exception { public MsalStubException(string m) : base(m) { } }
        private static Exception MsalStub(string m) => new MsalStubException(m);

        [Fact]
        public void A_failed_open_on_a_different_dataset_is_not_misattributed_to_a_sibling_record()
        {
            // A record exists for DS1 on this endpoint; the failed open targets DS2. The round-2 code fell back to "any
            // record on this endpoint" and its LastAccount — mis-attributing the failure. Now: no exact match ⇒ null id,
            // no stale account, and the WHERE preserved in the detail (MED 6).
            ConnectionRegistry.Remember("xmla", "powerbi://x/multi", "DS1", "DS1", "contoso.com", "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            EntraToken.SavedAccountForTests = (mode, tenant) => null;   // azcli-like: no saved MSAL record → account unknown
            try
            {
                engine.RecordFailedOpenHistory("powerbi://x/multi", "DS2", "contoso.com", "azcli", forceReauth: false, prepared: null, detail: "boom");
            }
            finally { EntraToken.SavedAccountForTests = null; }
            var ev = ConnectionHistory.List().Single(e => e.Endpoint == "powerbi://x/multi" && e.Database == "DS2");
            Assert.Null(ev.Id);                        // NOT mis-attributed to the DS1 sibling record
            Assert.Null(ev.Account);                   // unknown — never the stale Alice from the sibling
            Assert.Contains("DS2", ev.Detail);         // the WHERE is preserved in the detail for traceability
        }

        [Fact]
        public void A_cancelled_forced_sign_in_never_borrows_the_saved_account_for_attribution()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/resign", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            // A forced sign-in that prepared NO credential = the picker was dismissed before an account was chosen. Even
            // when a saved account exists, it must NOT be borrowed for attribution (MED 4) — no account was authenticated.
            EntraToken.SavedAccountForTests = (mode, tenant) => "alice@contoso.com";
            try
            {
                engine.RecordFailedOpenHistory(r.Endpoint, r.Database, "contoso.com", "interactive", forceReauth: true, prepared: null, detail: "x");
            }
            finally { EntraToken.SavedAccountForTests = null; }
            var ev = ConnectionHistory.List(r.Id).Single();
            Assert.Equal("signin", ev.Kind);   // a cancelled forced sign-in is a signin, never a spurious switch (MED 9)
            Assert.Null(ev.Account);            // never borrows the saved Alice for a sign-in that never authenticated (MED 4)
            Assert.False(ev.Ok);
        }

        [Fact]
        public void A_non_forced_failed_open_still_attributes_to_the_saved_account()
        {
            // The cancelled-picker exemption is scoped to FORCED sign-ins: a plain (non-forced) failed open with a saved
            // account still records that identity honestly — it is a real reused account, not a dismissed picker.
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/nonforced", "DS", "DS", "contoso.com", "interactive", "alice@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            EntraToken.SavedAccountForTests = (mode, tenant) => "bob@contoso.com";
            try
            {
                engine.RecordFailedOpenHistory(r.Endpoint, r.Database, "contoso.com", "interactive", forceReauth: false, prepared: null, detail: "x");
            }
            finally { EntraToken.SavedAccountForTests = null; }
            var ev = ConnectionHistory.List(r.Id).Single();
            Assert.Equal("open", ev.Kind);              // non-forced failed open
            Assert.Equal("bob@contoso.com", ev.Account); // the saved account IS the honest attribution here
            Assert.False(ev.Ok);
        }

        // ---- MEDIUM 8: the connection context carries a Reference side (engine-owned), and clearing it drops the card ----

        [Fact]
        public async Task The_connection_context_exposes_a_reference_side_that_clears()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var ctx = await engine.ConnectionContextAsync();
            Assert.NotNull(ctx.Reference);
            Assert.False(ctx.Reference.Available);   // no reference bound → the drawer's Reference card reads "Not set"
            var cleared = await engine.ClearReferenceBindingAsync();
            Assert.False(cleared.Reference.Available);
        }

        [Fact]
        public void A_corrupt_history_degrades_to_empty_and_never_throws()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "connection-history.json"), "{ not json");

            Assert.Empty(ConnectionHistory.List());          // read degrades, no throw
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = "after", Kind = "open", Ok = true });   // must not throw
            Assert.Single(ConnectionHistory.List());         // wrote a fresh log
            Assert.Contains(Directory.GetFiles(_root), f => f.Contains(".corrupt-"));   // old bytes preserved for the curious
        }
    }
}
