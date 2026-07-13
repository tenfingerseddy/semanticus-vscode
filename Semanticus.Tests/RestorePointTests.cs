using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Tests
{
    /// <summary>
    /// Redirects the restore-point store away from the developer's real home for the duration of the tests. Any test
    /// that COMMITS a workspace push writes a restore point, so this fixture is shared by every such class — without it
    /// the suite would litter ~/.semanticus/restore with snapshots of fake models.
    /// </summary>
    public sealed class RestoreRootFixture : IDisposable
    {
        public string Root { get; }
        public string RealApprovalPath { get; }
        private readonly byte[] _realApprovalBefore;
        public RestoreRootFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "sem-restore-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            RealApprovalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus", "agent-approvals.json");
            _realApprovalBefore = ReadRealApproval();
            RestorePointStore.RootOverride = Root;
            AgentPolicyStore.RootOverride = Root;
            ApprovalLedger.RootOverride = Root;
            ConnectionRegistry.RootOverride = Root;
        }
        public byte[] ReadRealApproval() => File.Exists(RealApprovalPath) ? File.ReadAllBytes(RealApprovalPath) : null;
        public void Dispose()
        {
            RestorePointStore.RootOverride = null;
            RestorePointStore.DeleteFileOverride = null;
            AgentPolicyStore.RootOverride = null;
            ApprovalLedger.RootOverride = null;
            ConnectionRegistry.RootOverride = null;
            if (!Same(_realApprovalBefore, ReadRealApproval()))
                throw new InvalidOperationException($"The test suite changed the developer approval ledger at {RealApprovalPath}.");
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }

        private static bool Same(byte[] left, byte[] right) =>
            left == null ? right == null : right != null && left.SequenceEqual(right);
    }

    [CollectionDefinition("restore-root")]
    public sealed class RestoreRootCollection : ICollectionFixture<RestoreRootFixture> { }

    [Collection("restore-root")]
    public sealed class ApprovalIsolationTests
    {
        private readonly RestoreRootFixture _fixture;
        public ApprovalIsolationTests(RestoreRootFixture fixture) => _fixture = fixture;

        [Fact]
        public void A_fixture_approval_never_changes_the_developer_ledger()
        {
            var before = _fixture.ReadRealApproval();
            var request = ApprovalLedger.Request(AgentCapability.QueryData, "prod", "fixture-isolation", "fixture request", "db on srv");
            Assert.Equal(_fixture.Root, ApprovalLedger.RootOverride);
            Assert.Equal(before ?? Array.Empty<byte>(), _fixture.ReadRealApproval() ?? Array.Empty<byte>());
            ApprovalLedger.Deny(request.Id, "human");
        }
    }

    /// <summary>
    /// The pre-push restore point and rollback_push. A live DELETE is permanent — RemoveExplicit removes 11 object
    /// kinds and a redeploy can only ever add back measures / calculated columns / calculated tables / named
    /// expressions — so Kane's rule is NO RESTORE POINT, NO DELETE. Everything runs offline behind the
    /// WorkspaceSnapshotHook / WorkspacePushHook seams; no endpoint is touched.
    /// </summary>
    [Collection("restore-root")]
    public sealed class RestorePointTests
    {
        private readonly RestoreRootFixture _fx;
        public RestorePointTests(RestoreRootFixture fx) => _fx = fx;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static TOM.Database Db(Action<TOM.Model> build = null)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            t.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-amount" });
            db.Model.Tables.Add(t);
            build?.Invoke(db.Model);
            return db;
        }

        private static void Measure(TOM.Model m, string name, string expr, string tag)
            => m.Tables["Sales"].Measures.Add(new TOM.Measure { Name = name, Expression = expr, LineageTag = tag });

        private static RestorePointPurgeResult PurgeById(string id)
        {
            var preview = RestorePointStore.Purge(id: id);
            Assert.Null(preview.Error);
            Assert.False(preview.Confirmed);
            Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
            return RestorePointStore.Purge(id: id, confirm: true, confirmToken: preview.ConfirmToken);
        }

        private static string WriteBim(TOM.Database db)
        {
            var p = Path.Combine(Path.GetTempPath(), $"sem-rp-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        private static DeployReport OkReport(int total, string[] deleted = null, string[] synced = null) => new DeployReport
        {
            Committed = true, TotalChanges = total, Deleted = deleted?.Length ?? 0,
            DeletedRefs = deleted ?? Array.Empty<string>(), SyncedRefs = synced ?? Array.Empty<string>()
        };

        private static ModelRef Ws() => new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };

        private static string MeasureExprIn(string bimPath, string name)
            => TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bimPath), null, AS.CompatibilityMode.PowerBI)
                  .Model.Tables["Sales"].Measures.Find(name)?.Expression;

        // ---- The restore point must be the state BEFORE the push. ModelCompare.Apply merges into snapshot B in
        // place, so serializing it after the merge would persist the POST-push state and make rollback a silent no-op.
        [Fact]
        public async Task Restore_point_captures_the_pre_push_state_not_the_merged_one()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));   // source wants Total = 2
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // live has Total = 1
                engine.WorkspacePushHook = (_, __) => OkReport(1, synced: new[] { "measure:Sales/Total" });

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: true, "human");
                Assert.True(r.Applied);
                Assert.False(string.IsNullOrEmpty(r.RestorePointId));
                Assert.Contains("rollback_push", r.Note);

                var rp = RestorePointStore.Find(r.RestorePointId);
                Assert.NotNull(rp);
                Assert.Equal("1", MeasureExprIn(rp.BimPath, "Total"));   // the PRE-push value, not the pushed 2
            }
            finally { File.Delete(src); }
        }

        // ---- NO RESTORE POINT, NO DELETE. Fail closed: nothing reaches the endpoint. ----
        [Fact]
        public async Task Delete_is_refused_when_the_restore_point_cannot_be_written()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var src = WriteBim(Db());                                             // source has no measures => Total is a Delete
            var blocker = Path.Combine(Path.GetTempPath(), "sem-rp-blocked-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(blocker, "not a directory");                        // the store cannot create a dir under a file
            var saved = RestorePointStore.RootOverride;
            try
            {
                RestorePointStore.RootOverride = blocker;
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(),
                    new[] { "measure:Sales/Total" }, commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);                                   // fail CLOSED — the endpoint was never touched
                Assert.Contains("Delete refused", r.Error);
                Assert.Contains("restore point", r.Error);
            }
            finally { RestorePointStore.RootOverride = saved; File.Delete(src); try { File.Delete(blocker); } catch { } }
        }

        // ---- ...but a push with NO deletes is recoverable by re-pushing, so a failed restore point only warns. ----
        [Fact]
        public async Task Push_without_deletes_still_proceeds_when_the_restore_point_fails_but_says_so()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            var blocker = Path.Combine(Path.GetTempPath(), "sem-rp-blocked-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(blocker, "not a directory");
            var saved = RestorePointStore.RootOverride;
            try
            {
                RestorePointStore.RootOverride = blocker;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
                engine.WorkspacePushHook = (_, __) => OkReport(1, synced: new[] { "measure:Sales/Total" });

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: true, "human");

                Assert.True(r.Applied);
                Assert.Null(r.RestorePointId);
                Assert.Contains("cannot be rolled back", r.Note);
            }
            finally { RestorePointStore.RootOverride = saved; File.Delete(src); try { File.Delete(blocker); } catch { } }
        }

        // ---- rollback dry run names what it would DELETE, including work added by someone else since the push. ----
        [Fact]
        public async Task Rollback_dry_run_reports_removals_and_writes_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));   // free: safety is never gated
            var rp = RestorePointStore.Write("powerbi://api.powerbi.com/v1.0/myorg/WS", "DS",
                TOM.JsonSerializer.SerializeDatabase(Db(m => Measure(m, "Total", "1", "tag-total"))),
                "apply_model_diff", "1 change(s), 0 delete(s)", new[] { "measure:Sales/Total" }, Array.Empty<string>());

            var pushed = false;
            // Live now: Total was changed to 2 by the push, and a colleague added Margin afterwards.
            engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => { Measure(m, "Total", "2", "tag-total"); Measure(m, "Margin", "9", "tag-margin"); }));
            engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(2); };

            var r = await engine.RollbackPushAsync(rp.Id, commit: false);

            Assert.False(r.Applied);
            Assert.False(pushed);
            Assert.Equal(1, r.Restored);
            Assert.Contains("measure:Sales/Total", r.RestoredRefs);
            Assert.Contains("measure:Sales/Margin", r.RemovedRefs);   // added since — surfaced BEFORE the user commits
            Assert.Contains("WILL be deleted", r.Note);
        }

        [Fact]
        public async Task Rollback_commit_restores_and_removes_and_is_free()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var rp = RestorePointStore.Write("powerbi://api.powerbi.com/v1.0/myorg/WS", "DS",
                TOM.JsonSerializer.SerializeDatabase(Db(m => Measure(m, "Total", "1", "tag-total"))),
                "apply_model_diff", "1 change(s)", new[] { "measure:Sales/Total" }, Array.Empty<string>());

            string pushedTotal = null;
            LiveDeleteTarget[] deletes = null;
            engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => { Measure(m, "Total", "2", "tag-total"); Measure(m, "Margin", "9", "tag-margin"); }));
            // Read the pushed .bim HERE — the engine deletes it in its cleanup before the call returns.
            engine.WorkspacePushHook = (bim, dels) => { pushedTotal = MeasureExprIn(bim, "Total"); deletes = dels?.ToArray(); return OkReport(2, deleted: new[] { "measure:Sales/Margin" }); };

            var r = await engine.RollbackPushAsync(rp.Id, commit: true);

            Assert.True(r.Applied);
            Assert.Equal("1", pushedTotal);                                    // the restored expression is what we push
            Assert.Equal("measure:Sales/Margin", Assert.Single(deletes).Ref);  // the post-push addition is removed
            Assert.Contains("Rolled", r.Note);
        }

        [Fact]
        public async Task Rollback_of_an_unchanged_target_is_a_clean_no_op()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var rp = RestorePointStore.Write("powerbi://api.powerbi.com/v1.0/myorg/WS", "DS",
                TOM.JsonSerializer.SerializeDatabase(Db(m => Measure(m, "Total", "1", "tag-total"))),
                "apply_model_diff", "", Array.Empty<string>(), Array.Empty<string>());

            var pushed = false;
            engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
            engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(0); };

            var r = await engine.RollbackPushAsync(rp.Id, commit: true);

            Assert.False(r.Applied);
            Assert.False(pushed);
            Assert.Contains("already matches", r.Note);
        }

        [Fact]
        public async Task Rollback_of_an_unknown_id_fails_loudly()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var r = await engine.RollbackPushAsync("nope", commit: true);
            Assert.False(r.Applied);
            Assert.Contains("No restore point", r.Error);
        }

        // ---- A manifest whose snapshot has vanished must be VISIBLE, not silently absent: a restore point you can't
        // restore from is exactly the thing a user needs told before they rely on it. ----
        [Fact]
        public void A_restore_point_whose_snapshot_is_gone_is_listed_with_no_path_and_refuses_to_restore()
        {
            var rp = RestorePointStore.Write("powerbi://x/orphan", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            File.Delete(rp.BimPath);

            var found = RestorePointStore.Find(rp.Id);
            Assert.NotNull(found);
            Assert.Null(found.BimPath);
        }

        [Fact]
        public void Retention_keeps_the_newest_per_target_and_never_prunes_across_targets()
        {
            var json = TOM.JsonSerializer.SerializeDatabase(Db());
            for (var i = 0; i < RestorePointStore.MaxPerTarget + 3; i++)
                RestorePointStore.Write("powerbi://x/retention", "DS", json, "apply_model_diff", "", null, null);
            RestorePointStore.Write("powerbi://x/retention", "OTHER", json, "apply_model_diff", "", null, null);

            Assert.Equal(RestorePointStore.MaxPerTarget, RestorePointStore.List("powerbi://x/retention", "DS").Count);
            Assert.Single(RestorePointStore.List("powerbi://x/retention", "OTHER"));   // a busy target never evicts another's
        }

        [Fact]
        public void Purge_by_id_removes_exactly_one()
        {
            var json = TOM.JsonSerializer.SerializeDatabase(Db());
            var a = RestorePointStore.Write("powerbi://x/purge", "DS", json, "apply_model_diff", "", null, null);
            RestorePointStore.Write("powerbi://x/purge", "DS", json, "apply_model_diff", "", null, null);

            var purged = PurgeById(a.Id);
            Assert.True(purged.Confirmed);
            Assert.True(purged.Completed);
            Assert.Equal(1, purged.Removed);
            Assert.Null(RestorePointStore.Find(a.Id));
            Assert.Single(RestorePointStore.List("powerbi://x/purge", "DS"));
        }

        // ---- An empty purge must be refused in the STORE, so every door behaves the same. A rule enforced on the MCP
        // path but not the RPC path is not a rule — that is the shape of five of the last wave's defects. ----
        [Fact]
        public void Purge_with_no_arguments_is_refused_not_silently_zero()
        {
            var before = RestorePointStore.Write("powerbi://x/empty-purge", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var refused = RestorePointStore.Purge();
            Assert.NotNull(refused.Error);
            Assert.False(refused.Confirmed);
            Assert.Equal(0, refused.Removed);
            Assert.NotNull(RestorePointStore.Find(before.Id));   // nothing was deleted
        }

        [Fact]
        public void Purge_requires_one_non_negative_selector_and_never_broadens_two_selectors()
        {
            var a = RestorePointStore.Write("powerbi://x/selectors", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);

            var negative = RestorePointStore.Purge(olderThanDays: -1, confirm: true, confirmToken: "anything");
            Assert.Contains("zero or greater", negative.Error);
            var combined = RestorePointStore.Purge(id: a.Id, olderThanDays: 0, confirm: true, confirmToken: "anything");
            Assert.Contains("exactly one selector", combined.Error);

            Assert.NotNull(RestorePointStore.Find(a.Id));
        }

        [Fact]
        public void Purge_preview_is_pure_and_confirmation_is_bound_to_the_exact_candidate_set()
        {
            var a = RestorePointStore.Write("powerbi://x/token", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var preview = RestorePointStore.Purge(id: a.Id);

            Assert.Equal(1, preview.Matched);
            Assert.Equal(a.Id, Assert.Single(preview.Candidates).Id);
            Assert.NotNull(RestorePointStore.Find(a.Id));

            var refused = RestorePointStore.Purge(id: a.Id, confirm: true, confirmToken: "PURGE-wrong");
            Assert.Contains("does not match", refused.Error);
            Assert.Equal(0, refused.Removed);
            Assert.NotNull(RestorePointStore.Find(a.Id));

            var agePreview = RestorePointStore.Purge(olderThanDays: 0);
            var later = RestorePointStore.Write("powerbi://x/token-later", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var stale = RestorePointStore.Purge(olderThanDays: 0, confirm: true, confirmToken: agePreview.ConfirmToken);
            Assert.Contains("does not match", stale.Error);
            Assert.NotNull(RestorePointStore.Find(a.Id));
            Assert.NotNull(RestorePointStore.Find(later.Id));

            var done = RestorePointStore.Purge(id: a.Id, confirm: true, confirmToken: preview.ConfirmToken);
            Assert.True(done.Completed);
            Assert.Equal(1, done.Removed);
            Assert.Null(RestorePointStore.Find(a.Id));
        }

        [Fact]
        public async Task Purge_derives_paths_from_validated_siblings_not_manifest_id_or_bim_path()
        {
            var rp = RestorePointStore.Write("powerbi://x/tamper", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var manifestPath = Path.ChangeExtension(rp.BimPath, ".json");
            var victim = Path.Combine(_fx.Root, "must-survive.txt");
            File.WriteAllText(victim, "keep me");

            var manifest = JsonSerializer.Deserialize<RestorePointRecord>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            manifest.Id = "..\\..\\must-survive";
            manifest.BimPath = victim;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

            var listed = Assert.Single(RestorePointStore.List("powerbi://x/tamper", "DS"));
            Assert.Equal(rp.Id, listed.Id);
            Assert.Null(listed.BimPath);
            Assert.Contains("manifest id", listed.IntegrityError);

            var done = PurgeById(rp.Id);
            Assert.Equal(1, done.Removed);
            Assert.Equal("keep me", File.ReadAllText(victim));

            var targetTamper = RestorePointStore.Write("powerbi://x/target-tamper", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var targetManifestPath = Path.ChangeExtension(targetTamper.BimPath, ".json");
            var targetManifest = JsonSerializer.Deserialize<RestorePointRecord>(File.ReadAllText(targetManifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            targetManifest.Endpoint = "powerbi://x/redirected";
            File.WriteAllText(targetManifestPath, JsonSerializer.Serialize(targetManifest,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

            var invalidTarget = RestorePointStore.Find(targetTamper.Id);
            Assert.NotNull(invalidTarget);
            Assert.Null(invalidTarget.BimPath);
            Assert.Contains("manifest target", invalidTarget.IntegrityError);
            using (var engine = new LocalEngine(new SessionManager(), new Fake(pro: false)))
                Assert.Contains("integrity check", (await engine.RollbackPushAsync(targetTamper.Id, commit: false)).Error);
            Assert.Equal(1, PurgeById(targetTamper.Id).Removed);
            Assert.False(File.Exists(targetTamper.BimPath));
        }

        [Fact]
        public void Malformed_and_oversized_manifests_remain_visible_and_purgeable()
        {
            var malformed = RestorePointStore.Write("powerbi://x/malformed", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var malformedManifest = Path.ChangeExtension(malformed.BimPath, ".json");
            File.WriteAllText(malformedManifest, "{ definitely-not-json");

            var malformedListed = Assert.Single(RestorePointStore.List("powerbi://x/malformed", "DS"));
            Assert.Equal(malformed.Id, malformedListed.Id);
            Assert.Null(malformedListed.BimPath);
            Assert.Contains("not valid JSON", malformedListed.IntegrityError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, PurgeById(malformed.Id).Removed);
            Assert.False(File.Exists(malformed.BimPath));
            Assert.False(File.Exists(malformedManifest));

            var oversized = RestorePointStore.Write("powerbi://x/oversized", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var oversizedManifest = Path.ChangeExtension(oversized.BimPath, ".json");
            File.WriteAllText(oversizedManifest, new string('x', 1024 * 1024 + 1));

            var oversizedListed = Assert.Single(RestorePointStore.List("powerbi://x/oversized", "DS"));
            Assert.Equal(oversized.Id, oversizedListed.Id);
            Assert.Null(oversizedListed.BimPath);
            Assert.Contains("size limit", oversizedListed.IntegrityError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, PurgeById(oversized.Id).Removed);
            Assert.False(File.Exists(oversized.BimPath));
            Assert.False(File.Exists(oversizedManifest));
        }

        [Fact]
        public void Reparse_point_snapshot_is_refused_for_rollback_but_purge_deletes_only_the_link()
        {
            if (OperatingSystem.IsWindows()) return; // Ubuntu CI exercises the real symlink; Windows creation is privilege-dependent.

            var rp = RestorePointStore.Write("powerbi://x/reparse", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var external = Path.Combine(_fx.Root, "external-snapshot.bim");
            File.WriteAllText(external, "must survive");
            File.Delete(rp.BimPath);
            File.CreateSymbolicLink(rp.BimPath, external);

            var listed = Assert.Single(RestorePointStore.List("powerbi://x/reparse", "DS"));
            Assert.Equal(rp.Id, listed.Id);
            Assert.Null(listed.BimPath);
            Assert.Contains("reparse point", listed.IntegrityError, StringComparison.OrdinalIgnoreCase);

            File.Delete(external); // a broken link must remain visible and removable too
            Assert.True((File.GetAttributes(rp.BimPath) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(1, PurgeById(rp.Id).Removed);
            Assert.ThrowsAny<IOException>(() => File.GetAttributes(rp.BimPath));
        }

        [Fact]
        public void Purge_reports_a_delete_failure_and_does_not_count_it_as_removed()
        {
            var rp = RestorePointStore.Write("powerbi://x/delete-fail", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var preview = RestorePointStore.Purge(id: rp.Id);
            RestorePointStore.DeleteFileOverride = path =>
            {
                if (path.EndsWith(".bim", StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated lock");
                File.Delete(path);
            };
            try
            {
                var failed = RestorePointStore.Purge(id: rp.Id, confirm: true, confirmToken: preview.ConfirmToken);
                Assert.True(failed.Confirmed);
                Assert.False(failed.Completed);
                Assert.Equal(0, failed.Removed);
                Assert.Equal(rp.Id, Assert.Single(failed.FailedIds));
                Assert.NotNull(failed.Error);
                Assert.NotNull(RestorePointStore.Find(rp.Id));
            }
            finally { RestorePointStore.DeleteFileOverride = null; }
        }

        [Fact]
        public async Task Purge_public_doors_share_preview_token_activity_and_never_broadcast_a_model_change()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: false));
            var rpc = new EngineRpcTarget(engine);
            var rp = RestorePointStore.Write("powerbi://x/public-purge", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            var activities = new System.Collections.Generic.List<ActivityEvent>();
            var changes = 0;
            sessions.Bus.Activity += activities.Add;
            sessions.Bus.Changed += _ => changes++;

            var preview = await McpTools.PurgeRestorePoints(engine, id: rp.Id);
            Assert.Equal(1, preview.Matched);
            Assert.NotNull(RestorePointStore.Find(rp.Id));
            Assert.Equal("agent", Assert.Single(activities).Origin);
            Assert.Equal("purge_restore_points", activities[0].Kind);
            Assert.True(activities[0].Ok);

            activities.Clear();
            var done = await rpc.purgeRestorePoints(id: rp.Id, confirm: true, confirmToken: preview.ConfirmToken);
            Assert.True(done.Completed);
            Assert.Equal(1, done.Removed);
            Assert.Null(RestorePointStore.Find(rp.Id));
            Assert.Equal("human", Assert.Single(activities).Origin);
            Assert.True(activities[0].Ok);
            Assert.Equal(0, changes);
        }

        // ---- A removal the endpoint REFUSED must reach FailedRefs. `Removed` counts what live actually deleted, so a
        // caller comparing the dry run's plan against the commit's result would otherwise read a partial as a success.
        [Fact]
        public async Task Rollback_surfaces_a_refused_removal_in_FailedRefs_not_only_the_note()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var rp = RestorePointStore.Write("powerbi://api.powerbi.com/v1.0/myorg/WS", "DS",
                TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);

            engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Margin", "9", "tag-margin")));
            engine.WorkspacePushHook = (_, __) => new DeployReport
            {
                Committed = true, TotalChanges = 0, DeletedRefs = Array.Empty<string>(),
                DeletesRefused = new[] { "measure:Sales/Margin" }, SyncedRefs = Array.Empty<string>(),
            };

            var r = await engine.RollbackPushAsync(rp.Id, commit: true);

            Assert.True(r.Applied);
            Assert.Equal(0, r.Removed);                                        // live deleted nothing
            Assert.Contains(r.FailedRefs, f => f.Contains("measure:Sales/Margin") && f.Contains("refused"));
        }

        // ---- A snapshot that vanishes between the manifest read and the load fails with the reason, not a raw IO error.
        [Fact]
        public async Task Rollback_reports_a_vanished_snapshot_clearly()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var rp = RestorePointStore.Write("powerbi://x/vanish", "DS", TOM.JsonSerializer.SerializeDatabase(Db()), "apply_model_diff", "", null, null);
            File.Delete(rp.BimPath);

            var r = await engine.RollbackPushAsync(rp.Id, commit: true);

            Assert.False(r.Applied);
            Assert.Contains("missing from disk", r.Error);
        }
    }
}
