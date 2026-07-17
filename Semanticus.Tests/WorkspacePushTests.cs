using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Tests
{
    /// <summary>
    /// apply_diff to a WORKSPACE (published-model) target — the ALM-Toolkit-style selective push. The live XMLA
    /// legs (snapshot + SaveChanges) are behind test seams (WorkspaceSnapshotHook / WorkspacePushHook), so the
    /// merge / validate / drift-guard / entitlement-gate / audit legs are all exercised OFFLINE — no real endpoint,
    /// no live write. The actual live delete removal is pinned separately against LiveDeploy.RemoveExplicit (in-memory
    /// TOM), and the whole-model deploy_live "absence never deletes" guarantee is pinned against SyncModels.
    /// </summary>
    [Collection("restore-root")]   // a committed push writes a restore point — keep it out of the developer's real home
    public sealed class WorkspacePushTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A tiny model with a Sales table + one M partition + an Amount column. `build` adds/edits measures.
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

        // A one-table model whose "Sales" table carries a SPECIFIC lineage tag — for the retag/republish scenario
        // (same name, different tag on source vs live).
        private static TOM.Database DbTagged(string tableTag)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = tableTag };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(t);
            return db;
        }

        private static string WriteBim(TOM.Database db)
        {
            var p = Path.Combine(Path.GetTempPath(), $"sem-wspush-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        // `synced` simulates what SyncSessionToLive actually carried live (rep.SyncedRefs) — the caller reconciles the
        // local merge against it, so an update test must echo the refs it expects to have reached the model.
        private static DeployReport OkReport(int total, string[] deleted = null, string[] synced = null) => new DeployReport
        {
            Committed = true, TotalChanges = total, Deleted = deleted?.Length ?? 0,
            DeletedRefs = deleted ?? Array.Empty<string>(), SyncedRefs = synced ?? Array.Empty<string>()
        };

        private static ModelRef Ws() => new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };

        // Build an identity-carrying delete target (the live-delete channel resolves by identity, not the ref). For a
        // relationship, name = the structural signature. Ref is reporting-only.
        private static LiveDeleteTarget Tgt(string kind, string name, string tag = null, string table = null, string tableTag = null)
            => new LiveDeleteTarget { Kind = kind, Ref = table == null ? $"{kind}:{name}" : $"{kind}:{table}/{name}", Tag = tag, TableTag = tableTag, Name = name, Table = table };

        private static string MeasureExprInBim(string bimPath, string name)
        {
            var m = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bimPath), null, AS.CompatibilityMode.PowerBI).Model;
            return m.Tables["Sales"].Measures.Find(name)?.Expression;
        }

        // ---- (a) preview (commit=false) writes nothing and is free ----
        [Fact]
        public async Task Preview_is_free_and_pushes_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // A: Total = 1
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: false, "human");
                Assert.False(r.Applied);
                Assert.False(pushed);                                   // nothing pushed
                Assert.Contains("Preview", r.Note);
                Assert.Contains("DS", r.Note);
            }
            finally { File.Delete(src); }
        }

        // ---- (b) multi-object commit without Pro is refused by the entitlement gate (before any live write) ----
        [Fact]
        public async Task Multi_object_commit_is_refused_on_free()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => { Measure(m, "A", "2", "tag-a"); Measure(m, "B", "2", "tag-b"); }));
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => { Measure(m, "A", "1", "tag-a"); Measure(m, "B", "1", "tag-b"); }));
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(2); };

                await Assert.ThrowsAsync<EntitlementException>(() =>
                    engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: true, "human"));
                Assert.False(pushed);   // the refusal happened before any live write
            }
            finally { File.Delete(src); }
        }

        // Deletes gate the SAME as any object: two Delete refs on free is refused too.
        [Fact]
        public async Task Multi_delete_commit_is_refused_on_free()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db());   // source has NO measures → A's two measures read as Delete
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => { Measure(m, "A", "1", "tag-a"); Measure(m, "B", "1", "tag-b"); }));
                engine.WorkspacePushHook = (_, __) => OkReport(2);
                await Assert.ThrowsAsync<EntitlementException>(() =>
                    engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: true, "human"));
            }
            finally { File.Delete(src); }
        }

        // ---- (c) a single-object commit is allowed free ----
        [Fact]
        public async Task Single_object_commit_is_free()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // Update on measure:Sales/Total
                string[] pushedDeletes = null; var pushed = false;
                engine.WorkspacePushHook = (_, dels) => { pushed = true; pushedDeletes = dels?.Select(d => d.Ref).ToArray() ?? Array.Empty<string>(); return OkReport(1, synced: new[] { "measure:Sales/Total" }); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");
                Assert.True(r.Applied, r.Error);
                Assert.True(pushed);
                Assert.Empty(pushedDeletes);   // an Update carries no delete
            }
            finally { File.Delete(src); }
        }

        // ---- (d) drift guard refuses when the target changed under you; proceeds + records overridden with a reason ----
        [Fact]
        public async Task Drift_guard_refuses_without_override()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                var call = 0; var pushed = false;
                // A: Total = 1 (source wants 2 → applicable Update). B: Total = 99 (someone changed it → drift).
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(Db(m => Measure(m, "Total", call == 1 ? "1" : "99", "tag-total"))); };
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");
                Assert.False(r.Applied);
                Assert.False(pushed);                                   // nothing written
                Assert.Contains("Drift guard", r.Error);
                Assert.Contains("measure:Sales/Total", r.Error);
            }
            finally { File.Delete(src); }
        }

        [Fact]
        public async Task Drift_override_proceeds_and_records_overridden()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await engine.OpenAsync(TestModels.FindBim());   // an open session so the audit record has somewhere to land
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                var call = 0; var pushed = false;
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(Db(m => Measure(m, "Total", call == 1 ? "1" : "99", "tag-total"))); };
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1, synced: new[] { "measure:Sales/Total" }); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human", overrideReason: "hotfix approved by owner");
                Assert.True(r.Applied, r.Error);
                Assert.True(pushed);

                var chain = await engine.ListVerifiedEditsAsync();
                var rec = chain.Records.LastOrDefault(x => x.Op == "apply_model_diff");
                Assert.NotNull(rec);
                Assert.Equal("overridden", rec.Verdict);
                Assert.Equal("hotfix approved by owner", rec.OverrideReason);
            }
            finally { File.Delete(src); }
        }

        // An unforced (non-drifted) push records normally (Verdict=deployed).
        [Fact]
        public async Task Clean_push_records_deployed()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await engine.OpenAsync(TestModels.FindBim());
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // A==B, no drift
                engine.WorkspacePushHook = (_, __) => OkReport(1, synced: new[] { "measure:Sales/Total" });
                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");
                Assert.True(r.Applied, r.Error);
                var rec = (await engine.ListVerifiedEditsAsync()).Records.LastOrDefault(x => x.Op == "apply_model_diff");
                Assert.NotNull(rec);
                Assert.Equal("deployed", rec.Verdict);
                Assert.Null(rec.OverrideReason);
            }
            finally { File.Delete(src); }
        }

        // REGRESSION: a selective push must NOT revert an unrelated object a colleague changed on the live target
        // between the diff and the commit. Source changes X; snapshot A has X and Y; Y changes on live (B) before the
        // commit; we push ONLY X. The pushed model must carry the NEW (live) Y — merged into B, not the stale A —
        // while X is updated. Against the pre-fix code (merge into A, push A) the pushed model carried A's stale Y and
        // this assertion FAILS; that is the whole point of the test.
        [Fact]
        public async Task Unselected_concurrent_edit_is_preserved_not_reverted()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => { Measure(m, "X", "2", "tag-x"); Measure(m, "Y", "1", "tag-y"); }));   // source: X=2, Y=1
            try
            {
                var call = 0;
                // A: X=1, Y=1 (what we diffed). B (current live): X=1, Y=9 — a colleague changed Y since A.
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(
                    call == 1 ? Db(m => { Measure(m, "X", "1", "tag-x"); Measure(m, "Y", "1", "tag-y"); })
                              : Db(m => { Measure(m, "X", "1", "tag-x"); Measure(m, "Y", "9", "tag-y"); })); };
                string xExpr = null, yExpr = null;
                engine.WorkspacePushHook = (bim, _) => { xExpr = MeasureExprInBim(bim, "X"); yExpr = MeasureExprInBim(bim, "Y"); return OkReport(1, synced: new[] { "measure:Sales/X" }); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/X" }, commit: true, "human");   // select ONLY X

                Assert.True(r.Applied, r.Error);
                Assert.Equal("2", xExpr);   // X updated to the source value
                Assert.Equal("9", yExpr);   // Y PRESERVED at the live value — NOT reverted to A's stale "1"
            }
            finally { File.Delete(src); }
        }

        // A selected ref that converged on B (a colleague made the same change) drops out as a NO-OP — reported, not
        // counted as applied, and never pushed. Reached via the override path (the convergence itself is drift on the
        // selected ref, so the guard flags it; the override lets us proceed to discover it's already reconciled).
        [Fact]
        public async Task Selected_ref_equal_on_B_is_a_reported_noop()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await engine.OpenAsync(TestModels.FindBim());   // reached via the override path, which now REQUIRES a session to record the accountable override (A2)
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));   // source wants Total=2
            try
            {
                var call = 0; bool pushed = false; string[] pushedRefs = null;
                // A: Total=1 (applicable Update). B: Total=2 (a colleague already set exactly what we wanted).
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(Db(m => Measure(m, "Total", call == 1 ? "1" : "2", "tag-total"))); };
                engine.WorkspacePushHook = (bim, _) => { pushed = true; pushedRefs = new[] { MeasureExprInBim(bim, "Total") }; return new DeployReport { Committed = false, TotalChanges = 0 }; };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human", overrideReason: "aware it changed");

                Assert.True(r.Applied, r.Error);
                Assert.Equal(0, r.Count);                                   // nothing applied — it converged
                Assert.Contains("no-op", r.Note);
                Assert.Contains("measure:Sales/Total", r.Note);
                Assert.True(pushed);
                Assert.Equal("2", pushedRefs[0]);                          // the pushed model already matches live (no revert)
            }
            finally { File.Delete(src); }
        }

        [Fact]
        public async Task Concurrent_pushes_own_separate_staging_directories()
        {
            using var first = new LocalEngine(new SessionManager(), new Fake(pro: false));
            using var second = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src1 = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            var src2 = WriteBim(Db(m => Measure(m, "Total", "3", "tag-total")));
            string path1 = null, path2 = null;
            try
            {
                first.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
                second.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
                first.WorkspacePushHook = (bim, _) => { path1 = bim; Assert.True(File.Exists(bim)); return OkReport(1, synced: new[] { "measure:Sales/Total" }); };
                second.WorkspacePushHook = (bim, _) => { path2 = bim; Assert.True(File.Exists(bim)); return OkReport(1, synced: new[] { "measure:Sales/Total" }); };

                var results = await Task.WhenAll(
                    first.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src1 }, Ws(), new[] { "measure:Sales/Total" }, true, "human"),
                    second.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src2 }, Ws(), new[] { "measure:Sales/Total" }, true, "human"));

                Assert.All(results, r => Assert.True(r.Applied, r.Error));
                Assert.NotEqual(Path.GetDirectoryName(path1), Path.GetDirectoryName(path2));
            }
            finally { File.Delete(src1); File.Delete(src2); }
        }

        // A SaveChanges failure (surfaced via rep.Error) is never reported as success.
        [Fact]
        public async Task Push_error_is_reported_not_swallowed()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));
                engine.WorkspacePushHook = (_, __) => new DeployReport { Committed = false, Error = "SaveChanges failed — nothing was committed: dependent object" };
                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");
                Assert.False(r.Applied);
                Assert.Contains("SaveChanges failed", r.Error);
            }
            finally { File.Delete(src); }
        }

        // A selected Delete ref reaches the push as an explicit delete ref (it is not merged into the model).
        [Fact]
        public async Task Selected_delete_is_pushed_as_an_explicit_delete_ref()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db());   // source has no "Drop" → A's "Drop" reads as Delete
            try
            {
                string[] pushedDeletes = null;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Drop", "1", "tag-drop")));
                engine.WorkspacePushHook = (_, dels) => { pushedDeletes = dels?.Select(d => d.Ref).ToArray() ?? Array.Empty<string>(); return OkReport(1, pushedDeletes); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Drop" }, commit: true, "human");
                Assert.True(r.Applied, r.Error);
                Assert.Contains("measure:Sales/Drop", pushedDeletes);
            }
            finally { File.Delete(src); }
        }

        // ---- (f) a gitref target returns its own distinct error ----
        [Fact]
        public async Task Gitref_target_gives_its_own_error()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "session" },
                new ModelRef { Kind = "gitref", GitRef = "HEAD" }, null, commit: true, "human");
            Assert.False(r.Applied);
            Assert.Contains("git ref is history", r.Error);
            Assert.DoesNotContain("Deploy tab", r.Error ?? "");   // the old lumped message is gone
        }

        // ---- LiveDeploy.RemoveExplicit: the real live-delete removal, in-memory (offline) ----
        [Fact]
        public void RemoveExplicit_removes_only_the_named_ref()
        {
            var live = Db(m => { Measure(m, "Keep", "1", "tag-keep"); Measure(m, "Drop", "2", "tag-drop"); }).Model;
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(live, new[] { Tgt("measure", "Drop", tag: "tag-drop", table: "Sales", tableTag: "tag-sales") }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Contains("measure:Sales/Drop", rep.DeletedRefs);
            Assert.Null(live.Tables["Sales"].Measures.Find("Drop"));      // removed
            Assert.NotNull(live.Tables["Sales"].Measures.Find("Keep"));   // an unselected object is NEVER removed
        }

        [Fact]
        public void RemoveExplicit_removes_a_table_and_a_relationship()
        {
            // A Date table + a Sales[Amount] -> Date[Amount] relationship, plus a second table we DELETE. Both the
            // table and the relationship are removed in one call; an untouched table is left intact.
            var m = Db(mm => Measure(mm, "M", "1", "tag-m")).Model;
            var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
            date.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-date-amount" });
            m.Tables.Add(date);
            var drop = new TOM.Table { Name = "Drop", LineageTag = "tag-drop-t" };
            drop.Columns.Add(new TOM.DataColumn { Name = "K", DataType = TOM.DataType.Int64, LineageTag = "tag-drop-k" });
            m.Tables.Add(drop);
            m.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-guid",
                FromColumn = m.Tables["Sales"].Columns["Amount"],
                ToColumn = m.Tables["Date"].Columns["Amount"],
            });

            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("table", "Drop", tag: "tag-drop-t"), Tgt("relationship", "Sales[Amount]->Date[Amount]") }, apply: true, rep);

            Assert.Equal(2, rep.Deleted);
            Assert.Contains("table:Drop", rep.DeletedRefs);
            Assert.Contains("relationship:Sales[Amount]->Date[Amount]", rep.DeletedRefs);
            Assert.Null(m.Tables.Find("Drop"));                              // table removed
            Assert.Empty(m.Relationships);                                   // relationship removed
            Assert.NotNull(m.Tables.Find("Sales"));                          // an untouched table is left intact
            Assert.NotNull(m.Tables.Find("Date"));
        }

        [Fact]
        public void RemoveExplicit_already_absent_is_a_reported_noop_not_a_throw()
        {
            var live = Db(m => Measure(m, "Keep", "1", "tag-keep")).Model;
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(live, new[] { Tgt("measure", "Ghost", table: "Sales", tableTag: "tag-sales") }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);                                  // nothing removed
            Assert.Contains("measure:Sales/Ghost", rep.DeletesAlreadyAbsent);
            Assert.Empty(rep.DeletesRefused);                             // genuinely gone (no impostor) — benign, not a refusal
            Assert.NotNull(live.Tables["Sales"].Measures.Find("Keep"));
        }

        // ---- TOCTOU: the live-delete channel resolves by IDENTITY. SyncSessionToLive loads a THIRD live state neither
        // drift snapshot saw; a name-keyed delete could hit a same-named impostor there. These pin the identity rule. ----

        // (1) A table whose carried lineage tag no longer resolves, but a DIFFERENT table now bears the same name ⇒
        // REFUSED (not deleted), reported distinctly. Pre-fix (name-keyed) it deleted the impostor.
        [Fact]
        public void Delete_table_by_stale_tag_with_a_same_named_impostor_is_refused()
        {
            var m = Db().Model;                                            // has "Sales" (tag-sales)
            m.Tables.Add(new TOM.Table { Name = "Sales_Old", LineageTag = "current-tag" });   // a DIFFERENT object now named Sales_Old
            var rep = new DeployReport();
            // The ticked delete targeted the OLD Sales_Old (lineage "STALE"), which is gone.
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("table", "Sales_Old", tag: "STALE") }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("table:Sales_Old", rep.DeletesRefused);        // refused — a real signal, not a silent no-op
            Assert.Empty(rep.DeletesAlreadyAbsent);
            Assert.NotNull(m.Tables.Find("Sales_Old"));                    // the impostor is NOT deleted
        }

        // (2) Same for a child: the measure's carried tag is gone, but a same-named measure exists ⇒ REFUSED, untouched.
        [Fact]
        public void Delete_child_by_stale_tag_with_a_same_named_impostor_is_refused()
        {
            var m = Db(mm => Measure(mm, "Margin", "1", "current-m")).Model;   // live Margin has tag "current-m"
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("measure", "Margin", tag: "STALE-M", table: "Sales", tableTag: "tag-sales") }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("measure:Sales/Margin", rep.DeletesRefused);
            Assert.NotNull(m.Tables["Sales"].Measures.Find("Margin"));    // the same-named measure is NOT deleted
        }

        // (3) A genuinely tag-less kind (a role — a rename is Delete+Create) still resolves by name.
        [Fact]
        public void Delete_tagless_role_still_resolves_by_name()
        {
            var m = Db().Model;
            m.Roles.Add(new TOM.ModelRole { Name = "Reader" });
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("role", "Reader") }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Null(m.Roles.Find("Reader"));
        }

        // (4) CAPABILITY GAIN: a matching tag deletes the object even if it was RENAMED on live since the diff — where a
        // name-keyed delete would have silently no-op'd. Target name "OldName" but live measure is "NewName" (same tag).
        [Fact]
        public void Delete_by_tag_hits_the_object_even_after_a_live_rename()
        {
            var m = Db(mm => Measure(mm, "NewName", "1", "keep-tag")).Model;   // renamed on live; tag unchanged
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("measure", "OldName", tag: "keep-tag", table: "Sales", tableTag: "tag-sales") }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Null(m.Tables["Sales"].Measures.Find("NewName"));      // the tag found the renamed object
        }

        // (5) Genuinely absent (identity gone AND no same-named object) is a benign no-op — distinct from a refusal.
        [Fact]
        public void Delete_absent_is_a_benign_noop_distinct_from_a_refusal()
        {
            var m = Db(mm => Measure(mm, "Keep", "1", "tag-keep")).Model;   // no "Ghost"
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("measure", "Ghost", tag: "ghost-tag", table: "Sales", tableTag: "tag-sales") }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("measure:Sales/Ghost", rep.DeletesAlreadyAbsent);
            Assert.Empty(rep.DeletesRefused);
        }

        // ---- ASYMMETRY: our snapshot's object was UNTAGGED but the name-resolved live object IS tagged ⇒ it isn't the
        // object we diffed (recreated/retagged). Refuse — but where BOTH sides are untagged (TE2 models), name
        // resolution proceeds (no capability loss). ----

        // Table: untagged target, live table tagged ⇒ Refused.
        [Fact]
        public void Untagged_target_resolving_to_a_tagged_live_table_is_refused()
        {
            var m = Db().Model;                                            // live "Sales" carries tag "tag-sales"
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("table", "Sales", tag: null) }, apply: true, rep);   // our side had no tag
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("table:Sales", rep.DeletesRefused);
            Assert.NotNull(m.Tables.Find("Sales"));                        // untouched
        }

        // Owning table (child path): untagged owning-table ref, live table tagged ⇒ Refused, nothing inside it touched.
        [Fact]
        public void Untagged_owning_table_resolving_to_a_tagged_live_table_is_refused()
        {
            var m = Db().Model;                                            // "Sales" (tag-sales) has column "Amount"
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("column", "Amount", table: "Sales", tableTag: null) }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("column:Sales/Amount", rep.DeletesRefused);
            Assert.NotNull(m.Tables["Sales"].Columns.Find("Amount"));
        }

        // Child: untagged owning table on BOTH sides, but the live child IS tagged ⇒ Refused, child untouched.
        [Fact]
        public void Untagged_target_resolving_to_a_tagged_live_child_is_refused()
        {
            var m = new TOM.Model();
            var t = new TOM.Table { Name = "T" };                          // UNtagged table (both sides)
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = "1", LineageTag = "M9" });   // but the measure IS tagged
            m.Tables.Add(t);
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("measure", "Total", table: "T", tableTag: null) }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("measure:T/Total", rep.DeletesRefused);
            Assert.NotNull(m.Tables["T"].Measures.Find("Total"));          // untouched
        }

        // CAPABILITY: both sides genuinely untagged (a TE2 model) ⇒ name resolution still deletes. This protects the
        // untagged-model path from an over-broad "forbid name resolution for tagged kinds" rule.
        [Fact]
        public void Both_sides_untagged_still_deletes()
        {
            var m = new TOM.Model();
            var t = new TOM.Table { Name = "T" };                          // untagged table
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = "1" });   // untagged measure
            m.Tables.Add(t);
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("measure", "Total", table: "T", tableTag: null) }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Null(m.Tables["T"].Measures.Find("Total"));            // deleted by name — no capability loss
        }

        // CL < 1540: LineageTag is unsupported and reads as "" (verified: does NOT throw). Every object is untagged, the
        // asymmetry guard can never fire, and a delete resolves by name. This must be a DELIBERATE degrade, not a crash.
        [Fact]
        public void Delete_on_a_below_1540_model_resolves_by_name_and_does_not_throw()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1500, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "T" };
            t.Measures.Add(new TOM.Measure { Name = "M", Expression = "1" });
            db.Model.Tables.Add(t);
            Assert.True(string.IsNullOrEmpty(t.LineageTag));                  // CL<1540 ⇒ no tag, and reading it didn't throw
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(db.Model, new[] { Tgt("measure", "M", table: "T", tableTag: null) }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);                                    // resolved by name
            Assert.Empty(rep.DeletesRefused);
            Assert.Null(db.Model.Tables["T"].Measures.Find("M"));
        }

        // ---- ABORT: a refused delete stops the WHOLE push (nothing committed), never commits a partial selection. ----

        // {refused delete + valid update} ⇒ the detection pass aborts before SaveChanges: nothing saved, Committed=false,
        // Error names the refused ref. The live update did NOT happen.
        [Fact]
        public void Refused_delete_aborts_the_whole_push_and_saves_nothing()
        {
            var live = Db(m => Measure(m, "Revenue", "1", "tag-rev")).Model;   // Revenue=1 (an update target)
            live.Tables.Add(new TOM.Table { Name = "Old", LineageTag = "current" });   // impostor — a DIFFERENT object now named "Old"
            var src = Db(m => Measure(m, "Revenue", "2", "tag-rev")).Model;    // wants Revenue=2
            var deletes = new[] { Tgt("table", "Old", tag: "STALE") };        // the diffed "Old" (tag STALE) is gone → Refused
            bool saved = false;
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "e", "d", deletes, () => saved = true, _ => { });
            Assert.False(saved);                                             // the metadata update did NOT commit
            Assert.False(rep.Committed);
            Assert.Contains("table:Old", rep.DeletesRefused);
            Assert.Contains("table:Old", rep.Error ?? "");
        }

        // {absent delete + valid update} ⇒ absent is benign; the update commits, the absent delete is reported.
        [Fact]
        public void Absent_delete_does_not_abort_the_push()
        {
            var live = Db(m => Measure(m, "Revenue", "1", "tag-rev")).Model;   // no "Old" table at all
            var src = Db(m => Measure(m, "Revenue", "2", "tag-rev")).Model;
            var deletes = new[] { Tgt("table", "Old", tag: "gone") };         // genuinely absent
            bool saved = false;
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "e", "d", deletes, () => saved = true, _ => { });
            Assert.True(saved);                                              // committed — absent did NOT abort
            Assert.True(rep.Committed);
            Assert.Contains("table:Old", rep.DeletesAlreadyAbsent);
            Assert.Empty(rep.DeletesRefused);
        }

        // ---- STRICT identity (selective push): src is a live snapshot, so a non-empty tag miss = republished-under-us. ----

        // (Defect 1) Expression UPDATE must not mutate a retagged live expression under strict. src FxRate tag A; live
        // FxRate tag B ⇒ not mutated, reported unmatched. Pre-fix the sync path found it by name and overwrote tag B.
        [Fact]
        public void Strict_expression_update_does_not_mutate_a_retagged_live_expression()
        {
            var src = Db().Model; src.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "2", LineageTag = "A" });
            var live = Db().Model; live.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "B" });
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Equal("1", live.Expressions.Find("FxRate").Expression);          // NOT mutated (wrong object)
            Assert.Contains(rep.Unmatched, u => u.Contains("expression:FxRate"));
            Assert.DoesNotContain("expression:FxRate", rep.SyncedRefs);
        }

        // (Defect 3) The strict-SPECIFIC case: source tag A, live tag now EMPTY (stripped / replaced under us). STRICT
        // refuses (terminal — does NOT name-match), so the live object is not mutated. (The lenient counterpart below
        // takes the exact same inputs and DOES update — that is the whole point of the provenance split.)
        [Fact]
        public void Strict_measure_whose_live_tag_was_stripped_is_not_mutated()
        {
            var src = Db(m => Measure(m, "Total", "2", "A")).Model;                // source tag A
            var live = Db(m => Measure(m, "Total", "1", null)).Model;              // live UNTAGGED (replaced/stripped)
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Equal("1", live.Tables["Sales"].Measures.Find("Total").Expression);   // NOT mutated (strict-terminal)
            Assert.Contains(rep.Unmatched, u => u.Contains("measure:Sales/Total"));
        }

        // The LENIENT counterpart (same inputs): deploy_live name-matches the untagged live object and updates it.
        // Strict and lenient MUST diverge here — this is the split.
        [Fact]
        public void Lenient_tagged_source_into_untagged_live_still_updates()
        {
            var src = Db(m => Measure(m, "Total", "2", "A")).Model;
            var live = Db(m => Measure(m, "Total", "1", null)).Model;
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Equal("2", live.Tables["Sales"].Measures.Find("Total").Expression);   // lenient: name-matched → updated
        }

        // (Defect 3 regression pin — the one the coordinator most wants) deploy_live is LENIENT: an UNTAGGED session
        // deploying into a TAGGED live model MUST still update by name. Strictness must not break this.
        [Fact]
        public void Lenient_deploy_live_untagged_session_into_tagged_live_still_updates()
        {
            var src = Db(m => Measure(m, "Total", "2", null)).Model;               // untagged (file-opened session)
            src.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "2" });   // untagged expression
            var live = Db(m => Measure(m, "Total", "1", "M9")).Model;              // tagged live
            live.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "E9" });
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Equal("2", live.Tables["Sales"].Measures.Find("Total").Expression);   // updated by name — no regression
            Assert.Equal("2", live.Expressions.Find("FxRate").Expression);              // updated by name — no regression
            Assert.Contains("measure:Sales/Total", rep.SyncedRefs);
        }

        // ---- Round 7: REPORT HONESTY (cannot damage a model; a misdescribing result misleads an agent). ----

        // (Defect 1) A newly ADDED object must NOT also appear in LiveOnly. Was broken for expressions (add path never
        // registered in matchedExprs); confirmation for measure + calc table (their add paths already register).
        [Fact]
        public void New_expression_is_not_also_reported_live_only()
        {
            var src = Db().Model; src.Expressions.Add(new TOM.NamedExpression { Name = "P", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "PE" });
            var live = Db().Model;                                                 // no expression P
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Contains("expression:P", rep.SyncedRefs);
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("namedExpression:P"));   // not simultaneously live-only
        }

        [Fact]
        public void New_measure_is_not_also_reported_live_only()
        {
            var src = Db(m => Measure(m, "NewM", "1", "NM")).Model;
            var live = Db().Model;                                                 // no NewM
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Contains("measure:Sales/NewM", rep.SyncedRefs);
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("NewM"));
        }

        [Fact]
        public void New_calc_table_is_not_also_reported_live_only()
        {
            var src = Db().Model;
            var ct = new TOM.Table { Name = "Calc", LineageTag = "CT" };
            ct.Partitions.Add(new TOM.Partition { Name = "Calc", Source = new TOM.CalculatedPartitionSource { Expression = "ROW(\"x\", 1)" } });
            src.Tables.Add(ct);
            var live = Db().Model;                                                 // no Calc table
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Contains("table:Calc", rep.SyncedRefs);
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("Calc"));
        }

        // (Defect 2) A live-only CHILD ref must follow the table's FINAL name after a rename.
        [Fact]
        public void Live_only_child_ref_follows_a_table_rename()
        {
            var src = new TOM.Model(); var _s = new TOM.Database("s") { CompatibilityLevel = 1600, Model = src };
            var st = new TOM.Table { Name = "New", LineageTag = "T" };            // source renamed Old->New (same tag)
            st.Columns.Add(new TOM.DataColumn { Name = "K", DataType = TOM.DataType.Int64, LineageTag = "K" });
            src.Tables.Add(st);
            var live = new TOM.Model(); var _l = new TOM.Database("l") { CompatibilityLevel = 1600, Model = live };
            var lt = new TOM.Table { Name = "Old", LineageTag = "T" };
            lt.Columns.Add(new TOM.DataColumn { Name = "K", DataType = TOM.DataType.Int64, LineageTag = "K" });
            lt.Measures.Add(new TOM.Measure { Name = "ServerOnly", Expression = "1", LineageTag = "SO" });   // live-only child
            live.Tables.Add(lt);

            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Equal("New", live.Tables["New"].Name);                        // rename applied
            Assert.Contains("measure:New/ServerOnly", rep.LiveOnly);             // ref follows the NEW name
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("Old/ServerOnly"));
        }

        // (Defect 2 — the one that matters) A COLLIDING rename is skipped; the live-only child ref must name the OLD
        // table, because that is what live actually still contains.
        [Fact]
        public void Live_only_child_ref_reflects_a_skipped_colliding_rename()
        {
            var src = new TOM.Model(); var _s = new TOM.Database("s") { CompatibilityLevel = 1600, Model = src };
            var st = new TOM.Table { Name = "New", LineageTag = "T" };
            st.Columns.Add(new TOM.DataColumn { Name = "K", DataType = TOM.DataType.Int64, LineageTag = "K" });
            src.Tables.Add(st);
            var live = new TOM.Model(); var _l = new TOM.Database("l") { CompatibilityLevel = 1600, Model = live };
            var lt = new TOM.Table { Name = "Old", LineageTag = "T" };
            lt.Columns.Add(new TOM.DataColumn { Name = "K", DataType = TOM.DataType.Int64, LineageTag = "K" });
            lt.Measures.Add(new TOM.Measure { Name = "ServerOnly", Expression = "1", LineageTag = "SO" });
            live.Tables.Add(lt);
            live.Tables.Add(new TOM.Table { Name = "New", LineageTag = "OTHER" });   // COLLISION: "New" already exists live

            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            Assert.Equal("Old", live.Tables["Old"].Name);                        // rename SKIPPED (collision)
            Assert.Contains(rep.Conflicts, c => c.Contains("already exists"));    // reported
            Assert.Contains("measure:Old/ServerOnly", rep.LiveOnly);             // ref names REALITY (still Old)
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("New/ServerOnly"));
        }

        // (Round 6, defect 1) The UPDATE path mirrors the DELETE path's asymmetry: under STRICT, an UNTAGGED source
        // object that name-resolves to a TAGGED live object is REFUSED (the live object was replaced/retagged under us).
        [Fact]
        public void Strict_untagged_source_measure_into_a_tagged_live_measure_is_refused()
        {
            var src = Db(m => Measure(m, "Total", "2", null)).Model;               // untagged source measure
            var live = Db(m => Measure(m, "Total", "1", "M9")).Model;              // tagged live measure
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Equal("1", live.Tables["Sales"].Measures.Find("Total").Expression);   // NOT mutated
            Assert.Contains(rep.Unmatched, u => u.Contains("measure:Sales/Total"));
        }

        // CAPABILITY (the one that matters): strict + BOTH untagged ⇒ still updates by name.
        [Fact]
        public void Strict_untagged_source_into_untagged_live_still_updates()
        {
            var src = Db(m => Measure(m, "Total", "2", null)).Model;
            var live = Db(m => Measure(m, "Total", "1", null)).Model;
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Equal("2", live.Tables["Sales"].Measures.Find("Total").Expression);   // updated (both untagged)
        }

        // Same asymmetry for a TABLE.
        [Fact]
        public void Strict_untagged_source_table_into_a_tagged_live_table_is_refused()
        {
            var src = new TOM.Model(); src.Tables.Add(new TOM.Table { Name = "T", Description = "changed" });   // untagged
            var live = new TOM.Model(); var _ = new TOM.Database("t") { CompatibilityLevel = 1600, Model = live };
            live.Tables.Add(new TOM.Table { Name = "T", LineageTag = "T2" });      // tagged live
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Contains(rep.Unmatched, u => u.Contains("table:T"));
            Assert.True(string.IsNullOrEmpty(live.Tables["T"].Description));       // NOT mutated
        }

        // Same asymmetry for an EXPRESSION.
        [Fact]
        public void Strict_untagged_source_expression_into_a_tagged_live_expression_is_refused()
        {
            var src = Db().Model; src.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "2" });   // untagged
            var live = Db().Model; live.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "E9" });   // tagged
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.Equal("1", live.Expressions.Find("FxRate").Expression);         // NOT mutated
            Assert.Contains(rep.Unmatched, u => u.Contains("expression:FxRate"));
        }

        // (Round 6, defect 2) A tag-matched NamedExpression rename is APPLIED (not a silent name-overwrite), reported as
        // what actually happened, and NOT double-reported as live-only.
        [Fact]
        public void Tag_matched_expression_is_renamed_not_duplicated_or_left_stale()
        {
            var src = Db().Model; src.Expressions.Add(new TOM.NamedExpression { Name = "NewParam", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "E1" });
            var live = Db().Model; live.Expressions.Add(new TOM.NamedExpression { Name = "OldParam", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "E1" });
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: true);
            Assert.NotNull(live.Expressions.Find("NewParam"));                     // renamed
            Assert.Null(live.Expressions.Find("OldParam"));                        // old name gone — not duplicated, not stale
            Assert.Single(live.Expressions);
            Assert.Contains("expression:NewParam", rep.SyncedRefs);               // reports what actually happened
            Assert.DoesNotContain(rep.LiveOnly, l => l.Contains("OldParam"));      // NOT double-reported live-only
        }

        // (Round 6, defect 3) UNKNOWN/detached compatibility level ⇒ safe-degrade: a tagged new measure is created
        // UNTAGGED (not attempted). Deliberate — the unsafe default could throw across the door.
        [Fact]
        public void New_measure_on_a_detached_unknown_cl_model_is_created_untagged()
        {
            var src = Db(m => Measure(m, "New", "1", "A")).Model;                  // tagged source
            var live = new TOM.Model();                                           // DETACHED — unknown CL (0)
            var lt = new TOM.Table { Name = "Sales" };
            lt.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            lt.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64 });
            live.Tables.Add(lt);
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);
            var nm = live.Tables["Sales"].Measures.Find("New");
            Assert.NotNull(nm);                                                    // created
            Assert.True(string.IsNullOrEmpty(nm.LineageTag));                     // untagged (unknown CL ⇒ don't write)
        }

        // (Defect 4) Pushing a TAGGED new measure into a CL < 1540 target creates it UNTAGGED — a lossless degrade, no
        // throw (setting LineageTag below 1540 throws CompatibilityViolationException; guarded on the TARGET level).
        [Fact]
        public void Push_a_tagged_new_measure_into_a_below_1540_target_creates_it_untagged_no_throw()
        {
            var src = Db(m => Measure(m, "New", "1", "A")).Model;                  // tagged source (CL 1600)
            var live = new TOM.Model();
            var _ = new TOM.Database("t") { CompatibilityLevel = 1500, Model = live };
            var lt = new TOM.Table { Name = "Sales" };                            // untagged (CL 1500 can't tag)
            lt.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            lt.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64 });
            live.Tables.Add(lt);
            var rep = LiveDeploy.SyncModels(src, live, apply: true, identityStrict: false);   // must not throw
            var nm = live.Tables["Sales"].Measures.Find("New");
            Assert.NotNull(nm);                                                    // created
            Assert.True(string.IsNullOrEmpty(nm.LineageTag));                     // untagged (tag write skipped)
        }

        // ---- RETAG / REPUBLISH refused end-to-end (same name, different tag ⇒ delete-then-recreate would drop data). ----

        [Fact]
        public async Task Retag_selection_is_refused_and_nothing_is_pushed()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(DbTagged("T1"));                              // source Sales tag T1
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(DbTagged("T2"));   // LIVE Sales carries a DIFFERENT tag
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "table:Sales" }, commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);                                       // never reached the push — refused first
                Assert.Contains(r.FailedRefs, f => f.Contains("table:Sales") && f.Contains("republish"));
            }
            finally { File.Delete(src); }
        }

        [Fact]
        public async Task Retag_preview_discloses_the_refusal_distinctly()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(DbTagged("T1"));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(DbTagged("T2"));
                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "table:Sales" }, commit: false, "human");
                Assert.False(r.Applied);
                Assert.Contains("REFUSED as a likely republish", r.Note);
                Assert.Contains("table:Sales", r.Note);
            }
            finally { File.Delete(src); }
        }

        // NamedExpression IS taggable — an expression delete with a differing tag is TAG-authoritative and REFUSED, not
        // silently name-matched (the confirmed "expression treated as tag-less" gap).
        [Fact]
        public void Expression_delete_by_stale_tag_with_a_same_named_impostor_is_refused()
        {
            var m = Db().Model;
            m.Expressions.Add(new TOM.NamedExpression { Name = "Foo", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "E2" });   // live tag E2
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { new LiveDeleteTarget { Kind = "expression", Ref = "expression:Foo", Tag = "E1", Name = "Foo" } }, apply: true, rep);
            Assert.Equal(0, rep.Deleted);
            Assert.Contains("expression:Foo", rep.DeletesRefused);
            Assert.NotNull(m.Expressions.Find("Foo"));                      // NOT name-matched away
        }

        // The whole-model deploy_live path passes NO delete refs — a live-only object is left untouched (absence
        // never deletes), and RemoveExplicit(null) is a pure no-op.
        [Fact]
        public void WholeModel_sync_never_deletes_a_live_only_object()
        {
            var src = Db(m => Measure(m, "Keep", "1", "tag-keep")).Model;                        // src lacks "Drop"
            var live = Db(m => { Measure(m, "Keep", "1", "tag-keep"); Measure(m, "Drop", "2", "tag-drop"); }).Model;
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.NotNull(live.Tables["Sales"].Measures.Find("Drop"));   // NOT removed by a whole-model sync
            Assert.Equal(0, rep.Deleted);

            var rep2 = new DeployReport();
            LiveDeploy.RemoveExplicit(live, null, apply: true, rep2);     // no delete refs = no-op
            Assert.Equal(0, rep2.Deleted);
            Assert.NotNull(live.Tables["Sales"].Measures.Find("Drop"));
        }

        // REGRESSION (finding #1): a DELETE-ONLY selection must NOT drag every unselected create/update into the push.
        // ModelCompare.Apply treats an empty selection set as "apply ALL" — so passing new HashSet(pushRefsB) with
        // pushRefsB empty (only a delete selected) merged the WHOLE diff. Source updates X and drops Y; we tick ONLY
        // Y's delete. The pushed model must keep the live X (not the source X). Against the pre-fix code the merged bim
        // carries X="2" and this FAILS; that is the whole point of the test.
        [Fact]
        public async Task Delete_only_push_does_not_merge_unselected_changes()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "X", "2", "tag-x")));   // source: X=2, no Y
            try
            {
                // A == B (no drift): live has X=1 and Y=1. Diff = X Update (1→2) + Y Delete. Select ONLY Y's delete.
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => { Measure(m, "X", "1", "tag-x"); Measure(m, "Y", "1", "tag-y"); }));
                string xExpr = null; string[] pushedDeletes = null;
                engine.WorkspacePushHook = (bim, dels) => { xExpr = MeasureExprInBim(bim, "X"); pushedDeletes = dels?.Select(d => d.Ref).ToArray() ?? Array.Empty<string>(); return OkReport(1, deleted: pushedDeletes); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Y" }, commit: true, "human");

                Assert.True(r.Applied, r.Error);
                Assert.Contains("measure:Sales/Y", pushedDeletes);           // the ticked delete went through
                Assert.Equal("1", xExpr);                                   // X PRESERVED at the live value — the unselected update was NOT merged
            }
            finally { File.Delete(src); }
        }

        // REGRESSION (finding #2): a table whose name contains '/' must resolve to exactly itself, never an unrelated
        // object. Live has BOTH "Finance/Sales" and "Sales"; deleting the "Finance/Sales" table (by identity) removes
        // exactly that table and leaves "Sales" untouched. (The delete channel now resolves by identity, not by parsing
        // the ref — the ref is reporting-only.)
        [Fact]
        public void Delete_ref_with_slash_in_table_name_targets_the_right_object()
        {
            var m = Db().Model;                                             // has a table "Sales" (tag-sales)
            m.Tables.Add(new TOM.Table { Name = "Finance/Sales", LineageTag = "tag-fin-sales" });
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { Tgt("table", "Finance/Sales", tag: "tag-fin-sales") }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Contains("table:Finance/Sales", rep.DeletedRefs);
            Assert.Null(m.Tables.Find("Finance/Sales"));                    // the CORRECT table removed
            Assert.NotNull(m.Tables.Find("Sales"));                         // the unrelated same-suffix table is untouched
        }

        // Finding #3 (unit): SyncModels reports the SOURCE-keyed refs it actually synced, so a caller can reconcile a
        // local merge against what truly reached the live model.
        [Fact]
        public void SyncModels_reports_the_refs_it_synced()
        {
            var src = Db(m => Measure(m, "Total", "2", "tag-total")).Model;
            var live = Db(m => Measure(m, "Total", "1", "tag-total")).Model;   // Total 1→2 = an Update that syncs
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains("measure:Sales/Total", rep.SyncedRefs);
        }

        // Finding #3 (end-to-end): a merged object of a type a metadata deploy does NOT carry (e.g. a role) must be
        // reported in FailedRefs, never counted as a live success — the result describes the LIVE model, not the temp
        // .bim. Against the pre-fix code the role read as Applied; here it is honestly reported as not-synced.
        [Fact]
        public async Task Push_of_a_type_the_deploy_does_not_carry_is_reported_failed()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => m.Roles.Add(new TOM.ModelRole { Name = "Reader" })));   // source adds a role
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db());   // A==B, no role live → role:Reader is a Create
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };   // real SyncModels never syncs a role → SyncedRefs empty

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "role:Reader" }, commit: true, "human");

                Assert.True(pushed);                                        // it was attempted
                Assert.False(r.Applied);                                    // but nothing reached live
                Assert.Equal(0, r.Count);
                Assert.DoesNotContain("role:Reader", r.AppliedRefs ?? Array.Empty<string>());
                Assert.Contains(r.FailedRefs, f => f.Contains("role:Reader"));
            }
            finally { File.Delete(src); }
        }

        // Finding #4: a commit where the selection matches no pending difference returns "nothing to push" BEFORE any
        // live work (no snapshot-B round-trip, no temp .bim, no push) and never reads as a committed success. Against
        // the pre-fix code it wrote a temp .bim and called the push, returning Applied=true — this FAILS pre-fix.
        [Fact]
        public async Task No_op_push_returns_nothing_to_push_without_live_work()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            var src = WriteBim(Db(m => Measure(m, "Total", "1", "tag-total")));   // source Total=1
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // live Total=1 → Equal
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(0); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);                                       // no live work happened
                Assert.Equal(0, r.Count);
                Assert.Contains("Nothing to push", r.Note);
            }
            finally { File.Delete(src); }
        }

        // A1 (SEVERE): a PARTIAL commit — metadata committed, but the calc-table recalc failed (rep.Committed==true WITH
        // an Error) — must NOT report "nothing applied". The metadata IS live: report the synced refs, set Applied
        // truthfully, WRITE the audit record, and surface the recalc error. Against the pre-fix `if (rep?.Error != null)`
        // early-return it read Applied=false / Count=0 and skipped the audit entirely — this FAILS pre-fix.
        [Fact]
        public async Task Partial_commit_reports_synced_refs_writes_audit_and_surfaces_recalc_error()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await engine.OpenAsync(TestModels.FindBim());
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(Db(m => Measure(m, "Total", "1", "tag-total")));   // A==B, no drift
                engine.WorkspacePushHook = (_, __) => new DeployReport
                {
                    Committed = true, TotalChanges = 1, SyncedRefs = new[] { "measure:Sales/Total" },
                    Error = "Metadata committed, but the Calculate recalc of new table(s) [Calc] failed — they exist but stay empty until refreshed: boom"
                };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human");

                Assert.True(r.Applied, r.Error);                            // metadata IS live — never "nothing applied"
                Assert.Equal(1, r.Count);
                Assert.Contains("measure:Sales/Total", r.AppliedRefs);
                Assert.Contains("Calculate recalc", r.Error ?? "");         // recalc warning surfaced prominently
                Assert.Contains("Calculate recalc", r.Note ?? "");
                var rec = (await engine.ListVerifiedEditsAsync()).Records.LastOrDefault(x => x.Op == "apply_model_diff");
                Assert.NotNull(rec);                                        // the audit record EXISTS (pre-fix: skipped)
                Assert.Equal("deployed", rec.Verdict);
            }
            finally { File.Delete(src); }
        }

        // A2: an accountable drift override with NO open session to record the reason must REFUSE — never push production
        // un-accountably. Against the pre-fix code (audit written AFTER the push, gated on a session, swallowed) the
        // override pushed and the record was silently dropped — this FAILS pre-fix (it pushed).
        [Fact]
        public async Task Drift_override_without_a_session_is_refused_before_the_push()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));   // NO OpenAsync → no session
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                var call = 0; var pushed = false;
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(Db(m => Measure(m, "Total", call == 1 ? "1" : "99", "tag-total"))); };
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human", overrideReason: "hotfix approved");

                Assert.False(r.Applied);
                Assert.False(pushed);                                       // refused BEFORE any live write
                Assert.Contains("needs an open session", r.Error);
            }
            finally { File.Delete(src); }
        }

        // A2: the override record is written BEFORE the push (matches deploy_live). Prove ordering: the push hook inspects
        // the audit chain and finds the "overridden" record already present when it runs.
        [Fact]
        public async Task Drift_override_record_is_written_before_the_push()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await engine.OpenAsync(TestModels.FindBim());
            var src = WriteBim(Db(m => Measure(m, "Total", "2", "tag-total")));
            try
            {
                var call = 0; bool recordedBeforePush = false;
                engine.WorkspaceSnapshotHook = () => { call++; return Task.FromResult(Db(m => Measure(m, "Total", call == 1 ? "1" : "99", "tag-total"))); };
                engine.WorkspacePushHook = (_, __) =>
                {
                    // At push time the accountable "overridden" record must ALREADY exist.
                    recordedBeforePush = engine.ListVerifiedEditsAsync().GetAwaiter().GetResult()
                        .Records.Any(x => x.Op == "apply_model_diff" && x.Verdict == "overridden");
                    return OkReport(1, synced: new[] { "measure:Sales/Total" });
                };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    Ws(), new[] { "measure:Sales/Total" }, commit: true, "human", overrideReason: "hotfix approved");

                Assert.True(r.Applied, r.Error);
                Assert.True(recordedBeforePush);   // recorded BEFORE the push — you cannot ship an override without its reason
            }
            finally { File.Delete(src); }
        }

        // ---- ROUND 6 · BLOCKER 1: a same-name CROSS-TABLE re-point whose Create can't stage refuses the Delete ----

        // A Sales fact with two candidate FROM columns + two dims (Customer, CustomerV2), each with an Id. The single
        // relationship named "r" joins Sales[fromCol] -> toTable[Id]. Re-pointing to a DIFFERENT table (or moving BOTH
        // endpoints) keeps the NAME but changes the endpoint signature, so IsEndpointRepoint misses and only the shared
        // name couples the pair. Both dims + both keys exist on every snapshot so the re-pointed Create's endpoints RESOLVE
        // in snapshot B — the Create fails ONLY on the duplicate relationship name (the exact staging collision).
        private static TOM.Database RepointDb(string fromCol, string toTable, string relName)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            void AddDim(string name, string tag)
            {
                var t = new TOM.Table { Name = name, LineageTag = tag };
                t.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
                t.Columns.Add(new TOM.DataColumn { Name = "Id", SourceColumn = "Id", DataType = TOM.DataType.Int64, LineageTag = tag + "-id" });
                db.Model.Tables.Add(t);
            }
            AddDim("Customer", "tag-cust");
            AddDim("CustomerV2", "tag-custv2");
            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
            sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", SourceColumn = "CustomerKey", DataType = TOM.DataType.Int64, LineageTag = "tag-ck" });
            sales.Columns.Add(new TOM.DataColumn { Name = "OtherKey", SourceColumn = "OtherKey", DataType = TOM.DataType.Int64, LineageTag = "tag-ok" });
            db.Model.Tables.Add(sales);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = relName,
                FromColumn = sales.Columns[fromCol], ToColumn = db.Model.Tables[toTable].Columns["Id"],
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });
            return db;
        }

        [Fact]
        public async Task Cross_table_relationship_repoint_refuses_the_delete_when_the_same_name_create_cannot_stage()
        {
            // The exact BLOCKER-1 (round 6) sequence: live has r: Sales[CustomerKey]->Customer[Id]; source keeps the NAME r
            // but re-points the TO endpoint to CustomerV2. ModelCompare emits Create(new sig)+Delete(old sig); the endpoint
            // predicate can't pair a cross-table move, so ONLY the shared name couples them. Staging merges the Create into
            // snapshot B, where the old r still lives -> the merged Create collides on the DUPLICATE NAME and lands in
            // outcome.Failed. Without the name coupling the Delete would be unlinked and commit alone, leaving the live model
            // with NO relationship. With it, the staging guard refuses the Delete and aborts the whole push (nothing written).
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));   // Create + Delete = multi-object -> Pro
            var src = WriteBim(RepointDb("CustomerKey", "CustomerV2", "r"));
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(RepointDb("CustomerKey", "Customer", "r"));   // live: r -> Customer[Id]
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(),
                    new[] { "relationship:Sales[CustomerKey]->CustomerV2[Id]", "relationship:Sales[CustomerKey]->Customer[Id]" },
                    commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);                                                                 // aborted BEFORE any live write — live keeps the old relationship
                Assert.Contains("staged", r.Error ?? "");                                             // "...could not be staged..."
                Assert.Contains(r.FailedRefs, f => f.Contains("Sales[CustomerKey]->Customer[Id]") && f.Contains("replacement"));
            }
            finally { File.Delete(src); }
        }

        [Fact]
        public async Task Both_endpoints_move_relationship_repoint_refuses_the_delete_when_the_same_name_create_cannot_stage()
        {
            // The both-endpoints-move variant: source keeps the NAME r but moves the FROM column (CustomerKey->OtherKey) AND
            // the TO table (Customer->CustomerV2). No endpoint part is shared, so only the name couples the pair. Same
            // staging collision on the duplicate name -> the Create can't stage -> the coupled Delete is refused and the
            // whole push aborts. Nothing reaches live; the original relationship survives.
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var src = WriteBim(RepointDb("OtherKey", "CustomerV2", "r"));
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(RepointDb("CustomerKey", "Customer", "r"));
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(),
                    new[] { "relationship:Sales[OtherKey]->CustomerV2[Id]", "relationship:Sales[CustomerKey]->Customer[Id]" },
                    commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);
                Assert.Contains("staged", r.Error ?? "");
                Assert.Contains(r.FailedRefs, f => f.Contains("Sales[CustomerKey]->Customer[Id]") && f.Contains("replacement"));
            }
            finally { File.Delete(src); }
        }

        [Fact]
        public async Task A_case_only_name_difference_still_couples_the_repoint_and_refuses_the_delete()
        {
            // AS/TOM collection keys are case-insensitive: source "r" collides at staging with live "R" exactly like an
            // exact-case pair, so SameRelName must couple them case-insensitively too. Ordinal comparison here would leave
            // the Delete unlinked and commit it alone: live loses "R" with no replacement.
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var src = WriteBim(RepointDb("CustomerKey", "CustomerV2", "r"));
            try
            {
                var pushed = false;
                engine.WorkspaceSnapshotHook = () => Task.FromResult(RepointDb("CustomerKey", "Customer", "R"));   // live: "R", source: "r"
                engine.WorkspacePushHook = (_, __) => { pushed = true; return OkReport(1); };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(),
                    new[] { "relationship:Sales[CustomerKey]->CustomerV2[Id]", "relationship:Sales[CustomerKey]->Customer[Id]" },
                    commit: true, "human");

                Assert.False(r.Applied);
                Assert.False(pushed);                                                                 // live keeps "R"; nothing written
                Assert.Contains("staged", r.Error ?? "");
                Assert.Contains(r.FailedRefs, f => f.Contains("Sales[CustomerKey]->Customer[Id]") && f.Contains("replacement"));
            }
            finally { File.Delete(src); }
        }
    }
}
