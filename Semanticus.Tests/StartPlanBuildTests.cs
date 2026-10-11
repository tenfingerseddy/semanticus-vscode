using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The start plan's build (design-spec-first-start-v2.md, lane sf-build): one undo step, undo and redo that leave the
    /// consent marker alone, a mismatch that commits nothing, no build without a live approval, grades that equal the
    /// preview, and a preview that equals the build on J8 with renames, a link off, an excluded table and a pinned value.
    ///
    /// THE MATERIALIZER IN THESE TESTS IS A STAND-IN. The real one is the manifest's spec plus the shared line executor
    /// (lanes sf-manifest and sf-exec). These tests prove the TRANSACTION around it (the step, the compare, the
    /// preconditions, the hooks), and <see cref="Standin"/> builds what a manifest says so the preview and the build have
    /// something real to disagree about. The tests that need the real one are named in the lane report.
    /// </summary>
    public sealed class StartPlanBuildTests
    {
        // ---------------------------------------------------------------- the stand-in and the fixture

        /// <summary>What a manifest says, built straight onto the model with the wrapper. Identities are the relationship
        /// names, so two builds of one document are byte-equal (S3). A table the manifest excludes is never made, and a link
        /// to it is never made either.</summary>
        internal static void Standin(LocalEngine engine, Model m, Session s, StartPlanDoc doc)
        {
            var kept = doc.Manifest.Tables.Where(t => t.Include).ToList();
            foreach (var t in kept)
            {
                var table = m.AddTable(t.Name);
                table.LineageTag = t.Identity;
                foreach (var c in t.Columns.Where(c => c.Include))
                {
                    var col = table.AddDataColumn(c.Name, c.SourceColumn ?? c.Name, null, DataType.String);
                    col.LineageTag = c.Identity ?? (t.Identity + "/" + c.Name);
                    if (c.Hidden) col.IsHidden = true;
                }
            }
            foreach (var l in doc.Manifest.Links.Where(l => !l.Unresolved))
            {
                var from = m.Tables.FirstOrDefault(t => t.Name == l.FromTable)?.Columns.FirstOrDefault(c => c.Name == l.FromColumns[0]);
                var to = m.Tables.FirstOrDefault(t => t.Name == l.ToTable)?.Columns.FirstOrDefault(c => c.Name == l.ToColumns[0]);
                if (from == null || to == null) continue;
                var rel = m.AddRelationship();
                // The TOM name is the relationship's identity (the wrapper's Name is display text), and S3 allocates it once. It is
                // set through the undoable seam, so undo then redo of the build brings the same name back.
                TabularEditor.TOMWrapper.Utils.NamedRelationship.Rename(rel, l.Identity);
                rel.FromColumn = from;
                rel.ToColumn = to;
                rel.IsActive = l.Active;
            }
            foreach (var bm in doc.Manifest.BaseMeasures)
                m.Tables.First(t => t.Name == bm.Table).AddMeasure(bm.Name, $"SUM ( '{bm.Table}'[{bm.Column}] )").LineageTag = bm.Identity ?? "m-" + bm.Name;
            // A pinned value is a decided value the loop must keep: here, a table description.
            foreach (var pin in doc.Pins)
                if (pin.Key.StartsWith("table:", StringComparison.Ordinal))
                {
                    var table = m.Tables.FirstOrDefault(t => t.Name == pin.Key.Substring(6));
                    if (table != null) table.Description = pin.Value;
                }
        }

        private static StartPlanDoc SmallDoc()
        {
            return new StartPlanDoc
            {
                Id = "sp-test",
                Destination = new StartDestination { Name = "Contoso" },
                IdentitySeed = "seed-1",
                Manifest = new StructureManifest
                {
                    Tables =
                    {
                        new ManifestTable { Name = "Sales", Identity = "t-sales", Columns = { new ManifestColumn { Name = "Amount" }, new ManifestColumn { Name = "CustomerKey" } } },
                        new ManifestTable { Name = "Customer", Identity = "t-cust", Columns = { new ManifestColumn { Name = "CustomerKey" }, new ManifestColumn { Name = "City" } } },
                    },
                    Links = { new ManifestLink { Identity = "rel-1", Active = true, FromTable = "Sales", FromColumns = new[] { "CustomerKey" }, ToTable = "Customer", ToColumns = new[] { "CustomerKey" } } },
                    BaseMeasures = { new ManifestBaseMeasure { Table = "Sales", Column = "Amount", Pick = "sum", Name = "Total Amount", DecidedBy = StartDecidedBy.Rule } },
                },
            };
        }

        /// <summary>A person's empty model, saved, with permission given (so the consent marker is an undo step already on
        /// the stack), and a scratch clone of it opened the way sf-core opens one.</summary>
        private sealed class Rig : IDisposable
        {
            public DecisionFixture F = new DecisionFixture(TestEntitlements.Pro);
            public LocalEngine Engine => F.Engine;
            public Session Person;
            public Session Scratch;
            public string Folder;
            public readonly List<ChangeNotification> Commits = new List<ChangeNotification>();

            public async Task InitAsync()
            {
                await Engine.CreateModelAsync("Contoso", 1604);
                Folder = Path.Combine(F.Workspace, "Contoso.SemanticModel");
                await Engine.SaveAsync(Folder, "tmdl", false, "human");
                await F.ConsentAsync();                 // writes the marker: one undo step before the build
                await Engine.SaveAsync(null, "tmdl", false, "human");   // the marker is in the files the scratch is cloned from
                Person = F.Sessions.Current;
                Scratch = await F.Sessions.BuildOpenAsync(Person.SourcePath);
                F.Sessions.Bus.Changed += n => { if (n.IsCommit && n.SessionId == Person.Id) lock (Commits) Commits.Add(n); };
            }

            public async Task<StartPlanDoc> PlanAsync(StartPlanDoc doc, bool approve = true, bool real = false)
            {
                // The scratch is rebuilt for every preview, as the decide loop rebuilds it per round: a second preview on
                // the same scratch would build the plan on top of the first.
                if (Scratch != null) { try { Scratch.Dispose(); } catch { } Scratch = await F.Sessions.BuildOpenAsync(Person.SourcePath); }
                Engine.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { Materialize = real ? null : Standin };
                doc.Preview = await Engine.PreviewStartPlanAsync(Scratch, doc);
                if (approve) doc.Approval = new StartApproval { Digest = StartPlanBuild.DigestOf(doc), Utc = DateTime.UtcNow };
                return doc;
            }

            public Task<StartPlanBuildResult> BuildAsync(StartPlanDoc doc, string digest = null, string origin = "human") =>
                Engine.ApplyStartPlanAsync(Person, doc, digest ?? StartPlanBuild.DigestOf(doc), origin);

            public Task<int> TablesAsync() => Person.ReadAsync(m => m.Tables.Count());
            public Task<int> UndoStepsAsync() => Person.RunAsync(() => Person.UndoStateNow().Steps);
            public Task<int> AuditCountAsync() => Person.ReadAsync(m => VerifiedEditsStore.Load(m).Records.Length);
            public Task<string> MarkerAsync() => Person.ReadAsync(m => m.GetAnnotation(LocalEngine.ConsentIdAnnotation));

            public void Dispose() { try { Scratch?.Dispose(); } catch { } F.Dispose(); }
        }

        // ---------------------------------------------------------------- the build is one undo step

        [Fact]
        public async Task The_build_adds_exactly_one_undo_step()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var before = await rig.UndoStepsAsync();
            var revBefore = rig.Person.Revision;

            var r = await rig.BuildAsync(doc);

            Assert.True(r.Built, r.Refused);
            Assert.Equal(before + 1, await rig.UndoStepsAsync());
            Assert.Equal(revBefore + 1, rig.Person.Revision);
            Assert.Equal(2, await rig.TablesAsync());
            var commit = Assert.Single(rig.Commits, c => c.Label == "build model from start plan");
            Assert.Equal(ChangeCauseKinds.StartPlan, commit.Cause?.Kind);
            Assert.Equal(ChangeBy.Semanticus, commit.By);
        }

        /// <summary>History groups the build under a parent row of its own kind, never under "item", which is a Plan line.</summary>
        [Fact]
        public async Task History_shows_the_build_under_a_start_plan_parent_not_a_plan_item()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            Assert.True((await rig.BuildAsync(doc)).Built);

            var rows = (await rig.Engine.GetPlanHistoryAsync()).Rows;
            var parent = Assert.Single(rows, r => r.Cause?.Kind == ChangeCauseKinds.StartPlan && r.Kind != "edit");
            Assert.Equal("start_plan", parent.Kind);
            Assert.Equal("start_plan:" + doc.Id, parent.Id);
            Assert.DoesNotContain(rows, r => r.Kind == "item");
        }

        [Fact]
        public async Task Undo_leaves_no_tables_with_the_marker_and_permission_in_force_and_redo_restores()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var marker = await rig.MarkerAsync();
            Assert.Null(marker);   // permission is kept on this machine, never written into the model (K19)
            Assert.True((await rig.Engine.GetConsentAsync()).Granted);
            Assert.True((await rig.BuildAsync(doc)).Built);
            var builtText = await rig.Person.RunAsync(() => StartPlanProjection.Of(rig.Person));

            await rig.Engine.UndoAsync("human");

            Assert.Equal(0, await rig.TablesAsync());
            Assert.Equal(marker, await rig.MarkerAsync());
            Assert.True((await rig.Engine.GetConsentAsync()).Granted, "taking the build back must not take the permission back");

            await rig.Engine.RedoAsync("human");

            Assert.Equal(2, await rig.TablesAsync());
            Assert.Equal(marker, await rig.MarkerAsync());
            var redoneText = await rig.Person.RunAsync(() => StartPlanProjection.Of(rig.Person));
            // Everything comes back, the relationship names included: the manifest's identity is set through the undoable seam
            // (NamedRelationship), because TE2's own redo of an add re-creates the link from the JSON it took when it was added.
            Assert.Equal(builtText, redoneText);
        }

        // ---------------------------------------------------------------- a mismatch commits nothing

        [Fact]
        public async Task A_forced_mismatch_commits_nothing()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            rig.Engine.StartPlanBuildSeamsForTest.TamperProjection = p => p + "\nX\n";
            var rev = rig.Person.Revision;
            var steps = await rig.UndoStepsAsync();
            var audit = await rig.AuditCountAsync();
            int commitsBefore; lock (rig.Commits) commitsBefore = rig.Commits.Count;
            var saved = await rig.Person.ReadAsync(m => rig.Person.HasUnsavedEdits);

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("not the one you were shown", r.Refused);
            Assert.Equal(rev, rig.Person.Revision);
            Assert.Equal(steps, await rig.UndoStepsAsync());
            Assert.Equal(0, await rig.TablesAsync());
            Assert.Equal(audit, await rig.AuditCountAsync());
            lock (rig.Commits) Assert.Equal(commitsBefore, rig.Commits.Count);
            Assert.Equal(saved, await rig.Person.ReadAsync(m => rig.Person.HasUnsavedEdits));
        }

        [Fact]
        public async Task A_materializer_that_fails_half_way_leaves_nothing_and_the_next_build_still_works()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var rev = rig.Person.Revision; var steps = await rig.UndoStepsAsync();
            rig.Engine.StartPlanBuildSeamsForTest.Materialize = (e, m, s, d) => { m.AddTable("Half"); throw new InvalidOperationException("boom"); };

            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.BuildAsync(doc));

            Assert.Equal(rev, rig.Person.Revision);
            Assert.Equal(steps, await rig.UndoStepsAsync());
            Assert.Equal(0, await rig.TablesAsync());
            rig.Engine.StartPlanBuildSeamsForTest.Materialize = Standin;
            Assert.True((await rig.BuildAsync(doc)).Built);
            Assert.Equal(2, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_build_that_passed_leaves_one_audit_record_and_a_refused_one_leaves_none()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var audit = await rig.AuditCountAsync();
            Assert.True((await rig.BuildAsync(doc)).Built);
            Assert.Equal(audit + 1, await rig.AuditCountAsync());
            var rec = (await rig.Person.ReadAsync(m => VerifiedEditsStore.Load(m).Records)).Last();
            Assert.Equal("start_plan_build", rec.Op);
        }

        // ---------------------------------------------------------------- approval and baseline

        [Fact]
        public async Task With_no_approval_the_build_is_refused()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc(), approve: false);
            var steps = await rig.UndoStepsAsync();

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("not been approved", r.Refused);
            Assert.Equal(0, await rig.TablesAsync());
            Assert.Equal(steps, await rig.UndoStepsAsync());
        }

        [Fact]
        public async Task After_an_edit_following_approval_the_build_is_refused()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var approved = doc.Approval.Digest;

            doc.Pins["table:Sales"] = "Every sale we invoiced.";   // any edit at all

            var r = await rig.BuildAsync(doc, digest: approved);
            Assert.False(r.Built);
            Assert.Contains("changed", r.Refused);
            Assert.Equal(0, await rig.TablesAsync());
            // The caller naming the NEW digest does not help either: the approval is still the old one.
            var r2 = await rig.BuildAsync(doc);
            Assert.False(r2.Built);
            Assert.Contains("approved", r2.Refused);
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_digest_the_caller_names_must_be_the_approved_one()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            var r = await rig.BuildAsync(doc, digest: "0000");
            Assert.False(r.Built);
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public void The_digest_is_the_view_builders_and_moves_for_each_thing_the_person_approves()
        {
            var d0 = StartPlanBuild.DigestOf(SmallDoc());
            Assert.Equal(StartPlanViewBuilder.Digest(SmallDoc()), d0);   // ONE function: the view, the approval and the build agree
            string Moved(Action<StartPlanDoc> edit) { var b = SmallDoc(); edit(b); return StartPlanBuild.DigestOf(b); }
            Assert.Equal(d0, Moved(_ => { }));
            Assert.NotEqual(d0, Moved(b => b.Manifest.Tables[0].Include = false));
            Assert.NotEqual(d0, Moved(b => b.Manifest.Links[0].Active = false));
            Assert.NotEqual(d0, Moved(b => b.Manifest.Tables[0].Name = "Orders"));
            Assert.NotEqual(d0, Moved(b => b.Pins["table:Sales"] = "x"));
            Assert.NotEqual(d0, Moved(b => b.Answers["fy"] = "6/30"));
            Assert.NotEqual(d0, Moved(b => b.IdentitySeed = "seed-2"));
            Assert.NotEqual(d0, Moved(b => b.Lines.Add(new StartLine { DecidedBy = StartDecidedBy.Assistant, Word = "words", Item = new ChangeItem { Kind = "set_description", ObjectRef = "table:Sales", After = "words" } })));
            // Not part of what is built, so not part of the digest.
            Assert.Equal(d0, Moved(b => { b.Decide.Round = 4; b.Decide.State = StartDecideState.Stable; b.UpdatedUtc = DateTime.UtcNow; }));
            Assert.Equal(d0, Moved(b => b.Approval = new StartApproval { Digest = "anything" }));
            Assert.Equal(d0, Moved(b => b.Preview = new StartPreview { ProjectionDigest = "q", AiReadiness = 9 }));
        }

        [Fact]
        public async Task A_stale_preview_never_matches_what_is_built_so_the_build_refuses_it()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            doc.Manifest.Tables[1].Columns.Add(new ManifestColumn { Name = "Region" });   // edited after the preview was made
            doc.Approval = new StartApproval { Digest = StartPlanBuild.DigestOf(doc) };   // approved as edited, preview not rerun

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("not the one you were shown", r.Refused);
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_model_that_gained_tables_since_the_baseline_is_not_built_into()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            await rig.Engine.CreateTableAsync("Stray", "human");   // the person (or an assistant) edits the empty model

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("already has tables", r.Refused);
            Assert.Equal(1, await rig.TablesAsync());
        }

        /// <summary>The baseline is not a revision number: permission is written into the model after the plan begins, as a
        /// tracked edit, so a revision check would refuse every Pro plan. What guards the model instead is the compare, which
        /// sees an edit that left no table behind (here a model annotation) as the difference it is.</summary>
        [Fact]
        public async Task An_edit_that_adds_no_table_after_the_preview_is_caught_by_the_compare_and_commits_nothing()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            await rig.Person.MutateAsync("human", "someone edited the empty model", m => m.SetAnnotation("Stray", "1"));
            var rev = rig.Person.Revision; var steps = await rig.UndoStepsAsync();

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("not the one you were shown", r.Refused);
            Assert.Equal(rev, rig.Person.Revision);
            Assert.Equal(steps, await rig.UndoStepsAsync());
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_parked_plan_and_a_plan_with_no_preview_are_not_built()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            doc.Parked = true;
            Assert.Contains("parked", (await rig.BuildAsync(doc)).Refused);
            doc.Parked = false;
            doc.Preview = new StartPreview();
            doc.Approval = new StartApproval { Digest = StartPlanBuild.DigestOf(doc) };
            Assert.Contains("preview", (await rig.BuildAsync(doc)).Refused);
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_kept_line_whose_needed_line_was_dropped_is_not_built()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = SmallDoc();
            var measure = new ChangeItem { Kind = "create_measure", ObjectRef = "measure:Sales/Total Amount", ObjectName = "Total Amount", Source = "jev", JevItem = "k1", Title = "Add Total Amount" };
            var hide = new ChangeItem { Kind = "set_hidden", ObjectRef = "column:Sales/Amount", ObjectName = "Amount", Source = "jev", JevItem = "k1", Title = "Hide Amount", Needs = JevFamily.NeedsKey(measure.Kind, measure.ObjectRef) };
            doc.Lines.Add(new StartLine { Item = measure, DecidedBy = StartDecidedBy.Jev, Kept = false });
            doc.Lines.Add(new StartLine { Item = hide, DecidedBy = StartDecidedBy.Jev, Kept = true });
            await rig.PlanAsync(doc);

            var r = await rig.BuildAsync(doc);

            Assert.False(r.Built);
            Assert.Contains("Total Amount", r.Refused);
            Assert.Contains("needs", r.Refused);
            Assert.Equal(0, await rig.TablesAsync());
            // With the measure kept again the same plan is whole.
            doc.Lines[0].Kept = true;
            await rig.PlanAsync(doc);
            Assert.True((await rig.BuildAsync(doc)).Built);
        }

        // ---------------------------------------------------------------- the preview equals the build

        [Fact]
        public async Task Grades_after_the_build_equal_the_preview()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());
            Assert.NotNull(doc.Preview.AiReadiness);
            Assert.NotNull(doc.Preview.BestPractice);

            Assert.True((await rig.BuildAsync(doc)).Built);

            var after = await rig.Person.ReadAsync(m => StartPlanBuild.GradesOf(m));
            Assert.Equal(doc.Preview.AiReadiness, after.AiReadiness);
            Assert.Equal(doc.Preview.BestPractice, after.BestPractice);
        }

        [Fact]
        public async Task The_same_document_previewed_twice_gives_the_same_digest()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = SmallDoc();
            Engine(rig).StartPlanBuildSeamsForTest = new StartPlanBuildSeams { Materialize = Standin };
            var a = await rig.Engine.PreviewStartPlanAsync(rig.Scratch, doc);
            using var second = await rig.F.Sessions.BuildOpenAsync(rig.Person.SourcePath);
            var b = await rig.Engine.PreviewStartPlanAsync(second, doc);
            Assert.Equal(a.ProjectionDigest, b.ProjectionDigest);
            static LocalEngine Engine(Rig r) => r.Engine;
        }

        /// <summary>J8 (7 tables, 9 foreign keys) with a rename, a link turned off, an excluded table and a pinned value:
        /// the preview scratch and the person's model give equal projections, and the build says so by committing.</summary>
        [Fact]
        public async Task J8_with_renames_a_link_off_an_excluded_table_and_a_pinned_value_gives_equal_projections()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var schema = SqlDraftBuildTests.J8Schema();
            var doc = new StartPlanDoc { Id = "sp-j8", IdentitySeed = "j8", Schema = schema, Manifest = new StructureManifest() };
            doc.Manifest.Tables = schema.Tables.Select(t => new ManifestTable
            {
                Name = t.Name, SourceSchema = t.Schema, SourceName = t.Name, Identity = "t-" + t.Name,
                Columns = t.Columns.Select(c => new ManifestColumn { Name = c.Name, SourceColumn = c.Name, DataType = c.DataType, IsKey = t.KeyColumns.Contains(c.Name) }).ToList(),
            }).ToList();
            var n = 0;
            doc.Manifest.Links = schema.ForeignKeys.Select(fk => new ManifestLink
            { Identity = "rel-" + (++n), Active = true, FromTable = fk.FromTable, FromColumns = new[] { fk.FromColumn }, ToTable = fk.ToTable, ToColumns = new[] { fk.ToColumn } }).ToList();
            // The person's edits: a table and a column renamed, one link off, one table out, one value pinned.
            doc.Manifest.Tables.Single(t => t.Name == "dim_staff").Name = "Staff";
            doc.Manifest.Tables.Single(t => t.Name == "Staff").Columns[0].Name = "Staff Number";
            doc.Manifest.Links.Single(l => l.FromTable == "fact_invoice" && l.FromColumns[0] == "DUE_DT_KEY").Active = false;
            doc.Manifest.Tables.Single(t => t.Name == "ref_codes").Include = false;
            doc.Pins["table:dim_project"] = "One row per project.";
            // Links follow the renames and the exclusion, as the manifest's own edit does.
            foreach (var l in doc.Manifest.Links.Where(l => l.ToTable == "dim_staff")) { l.ToTable = "Staff"; l.ToColumns = new[] { "Staff Number" }; }
            await rig.PlanAsync(doc);

            var r = await rig.BuildAsync(doc);

            Assert.True(r.Built, r.Refused);
            Assert.Equal(6, await rig.TablesAsync());   // 7 tables, one excluded
            var names = await rig.Person.ReadAsync(m => m.Tables.Select(t => t.Name).OrderBy(x => x).ToArray());
            Assert.Contains("Staff", names);
            Assert.DoesNotContain("ref_codes", names);
            Assert.Equal("One row per project.", await rig.Person.ReadAsync(m => m.Tables.Single(t => t.Name == "dim_project").Description));
            var off = await rig.Person.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>().Single(x => x.ID == doc.Manifest.Links.Single(l => l.FromColumns[0] == "DUE_DT_KEY").Identity).IsActive);
            Assert.False(off);
        }

        private static StartPlanDoc RealJ8Doc(string seed = "j8-seed")
        {
            var schema = SqlDraftBuildTests.J8Schema();
            var overrides = new ManifestOverrides
            {
                Tables = new[]
                {
                    new ManifestTableOverride { Table = "dim_staff", Name = "Staff" },       // a rename
                    new ManifestTableOverride { Table = "ref_codes", Include = false },      // an excluded table
                },
                Columns = new[] { new ManifestColumnOverride { Table = "dim_staff", Column = "EMP_NO", Name = "Staff Number" } },
                Links = new[] { new ManifestLinkOverride { FromTable = "fact_timesheet", FromColumn = "WORK_DT_KEY", ToTable = "dim_date", Active = false } },   // a link off
            };
            return new StartPlanDoc
            {
                Id = "sp-j8-real", IdentitySeed = seed, Schema = schema,
                Manifest = StructureManifest.Derive(schema, overrides, null, seed, "contoso-sql.example.com", "ContosoConsulting", "import"),
            };
        }

        /// <summary>The same J8 edits through the REAL manifest and the REAL structure build (ToSpec into BuildSpecInto), with
        /// the identities the manifest allocates from the seed (S3). The preview scratch and the person's model give equal
        /// projections, the relationship names are the manifest's own, and a second preview of the same document repeats.</summary>
        [Fact]
        public async Task J8_through_the_real_manifest_gives_equal_projections_with_the_manifests_own_identities()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = RealJ8Doc();
            await rig.PlanAsync(doc, real: true);

            var r = await rig.BuildAsync(doc);

            Assert.True(r.Built, r.Refused);
            Assert.Equal(6, await rig.TablesAsync());
            var names = await rig.Person.ReadAsync(m => m.Tables.Select(t => t.Name).ToArray());
            Assert.Contains("Staff", names);
            Assert.DoesNotContain("ref_codes", names);
            Assert.DoesNotContain("dim_staff", names);
            Assert.Contains("Staff Number", await rig.Person.ReadAsync(m => m.Tables.Single(t => t.Name == "Staff").Columns.Select(c => c.Name).ToArray()));
            var built = await rig.Person.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>().Select(x => x.ID).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            var expected = doc.Manifest.Links.Where(l => !l.Unresolved).Select(l => l.Identity).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(built);
            Assert.Equal(expected, built);
            var off = await rig.Person.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>()
                .Single(x => x.FromColumn.Table.Name == "fact_timesheet" && x.FromColumn.Name == "WORK_DT_KEY").IsActive);
            Assert.False(off);
            // The same document, previewed again on a fresh scratch, repeats byte for byte.
            using var again = await rig.F.Sessions.BuildOpenAsync(rig.Person.SourcePath);
            var second = await rig.Engine.PreviewStartPlanAsync(again, doc);
            Assert.Equal(doc.Preview.ProjectionDigest, second.ProjectionDigest);
        }

        /// <summary>A line as the decide loop makes it: Before is what the object holds now (a rename's Before is the current
        /// name), because the executor re-reads it at the write and skips a line whose object moved since it was made.</summary>
        private static StartLine Line(string kind, string objectRef, string after, string before = null, string by = StartDecidedBy.Assistant, bool kept = true) => new StartLine
        {
            DecidedBy = by, Kept = kept,
            Item = new ChangeItem { Kind = kind, ObjectRef = objectRef, ObjectName = objectRef, Before = before, After = after, Source = "deterministic", Title = kind + " " + objectRef },
        };

        /// <summary>The kept lines go through the Plan's own executor, in one batch with the structure. A description written to
        /// a column that the same list renames lands on that column (S5: the apply order puts renames after the edits that name
        /// the old ref); and the document is left exactly as it was, because the executor writes statuses onto the lines it is
        /// given, and a preview must never do that to the person's plan.</summary>
        [Fact]
        public async Task Kept_lines_land_in_the_same_step_through_the_executor_and_never_mark_the_document()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = RealJ8Doc();
            doc.Lines.Add(Line("set_description", "column:dim_client/CLNT_NM", "What the client is called."));
            doc.Lines.Add(Line("rename", "column:dim_client/CLNT_NM", "Client Name", before: "CLNT_NM"));
            doc.Lines.Add(Line("set_description", "table:dim_project", "One row per project."));
            doc.Lines.Add(Line("set_description", "table:dim_client", "dropped words", kept: false));
            await rig.PlanAsync(doc, real: true);
            var statusesBefore = doc.Lines.Select(l => (l.Item.Status, l.Item.Note)).ToArray();
            var steps = await rig.UndoStepsAsync();

            var r = await rig.BuildAsync(doc);

            Assert.True(r.Built, r.Refused);
            Assert.Equal(steps + 1, await rig.UndoStepsAsync());
            var col = await rig.Person.ReadAsync(m => m.Tables.Single(t => t.Name == "dim_client").Columns.Single(c => c.Name == "Client Name").Description);
            Assert.Equal("What the client is called.", col);
            Assert.Equal("One row per project.", await rig.Person.ReadAsync(m => m.Tables.Single(t => t.Name == "dim_project").Description));
            Assert.True(string.IsNullOrEmpty(await rig.Person.ReadAsync(m => m.Tables.Single(t => t.Name == "dim_client").Description)), "a dropped line must not land");
            Assert.Equal(statusesBefore, doc.Lines.Select(l => (l.Item.Status, l.Item.Note)).ToArray());
            await rig.Engine.UndoAsync("human");
            Assert.Equal(0, await rig.TablesAsync());
        }

        [Fact]
        public async Task A_kept_line_that_cannot_land_refuses_the_preview_and_names_it()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = RealJ8Doc();
            // A line the plan's own rules made (a hand-made line on a missing object is HELD and skipped, which the wiring tests prove): this one cannot land.
            doc.Lines.Add(Line("set_description", "table:No_Such_Table", "words", by: StartDecidedBy.Rule));

            var ex = await Assert.ThrowsAsync<StartPlanBuildRefusedException>(() => rig.PlanAsync(doc, real: true));

            Assert.Contains("nothing was built", ex.Message);
            Assert.Contains("No_Such_Table", ex.Message);
            Assert.Equal(0, await rig.TablesAsync());
        }

        // ---------------------------------------------------------------- a save racing the build

        [Fact]
        public async Task A_save_racing_the_build_leaves_files_that_are_whole_whichever_came_first()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());

            var build = rig.BuildAsync(doc);
            var save = rig.Engine.SaveAsync(null, "tmdl", false, "human");
            await Task.WhenAll(build, save);
            Assert.True(build.Result.Built, build.Result.Refused);

            // Save once more so the files hold the final model, then read them back in a fresh session.
            await rig.Engine.SaveAsync(null, "tmdl", false, "human");
            using var reopened = await rig.F.Sessions.BuildOpenAsync(rig.Person.SourcePath);
            Assert.Equal(2, await reopened.ReadAsync(m => m.Tables.Count()));
            Assert.Equal(1, await reopened.ReadAsync(m => m.Relationships.Count()));
            Assert.Equal(await rig.MarkerAsync(), await reopened.ReadAsync(m => m.GetAnnotation(LocalEngine.ConsentIdAnnotation)));
        }

        /// <summary>The danger in a save that overlaps a build is not a broken file, it is a model that reads CLEAN while its
        /// files lack the build: a save serializes in one turn and marks the model saved in a later one, so a commit between
        /// them would be marked saved without ever being written. Tried with the save and the build started at several
        /// offsets, in both orders. After the dust settles the files and the dirty flag must agree.</summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 10)]
        [InlineData(10, 0)]
        [InlineData(0, 40)]
        [InlineData(40, 0)]
        [InlineData(0, 120)]
        [InlineData(120, 0)]
        public async Task A_save_racing_the_build_never_leaves_a_clean_model_whose_files_lack_the_build(int saveDelayMs, int buildDelayMs)
        {
            using var rig = new Rig(); await rig.InitAsync();
            var doc = await rig.PlanAsync(SmallDoc());

            var save = Task.Run(async () => { await Task.Delay(saveDelayMs); await rig.Engine.SaveAsync(null, "tmdl", false, "human"); });
            var build = Task.Run(async () => { await Task.Delay(buildDelayMs); return await rig.BuildAsync(doc); });
            await Task.WhenAll(save, build);
            Assert.True(build.Result.Built, build.Result.Refused);

            using var reopened = await rig.F.Sessions.BuildOpenAsync(rig.Person.SourcePath);
            var onDisk = await reopened.ReadAsync(m => m.Tables.Count());
            var dirty = rig.Person.HasUnsavedChanges;
            Assert.True(onDisk == 2 || dirty, $"the files hold {onDisk} tables and the model reads clean: the build was marked saved without being written");
            Assert.Equal(2, await rig.TablesAsync());
        }

        // ---------------------------------------------------------------- the projection

        [Fact]
        public void The_projection_leaves_out_the_tool_stamps_and_the_engines_own_markers_and_nothing_else()
        {
            var a = Path.Combine(Path.GetTempPath(), "sfpa" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var b = Path.Combine(Path.GetTempPath(), "sfpb" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(a); Directory.CreateDirectory(b); Directory.CreateDirectory(Path.Combine(a, "tables")); Directory.CreateDirectory(Path.Combine(b, "tables"));
                var body = "model Model\n\tculture: en-US\n\n\tannotation Keep = yes\n\n\tannotation Semanticus_ConsentId = abc\n\n";
                File.WriteAllText(Path.Combine(a, "model.tmdl"), body + "\tannotation __TEdtr = 1\n\n\tannotation TabularEditor_SerializeOptions = {}\n\n");
                File.WriteAllText(Path.Combine(b, "model.tmdl"), body.Replace("\n", "\r\n"));   // no stamps, other line ends
                File.WriteAllText(Path.Combine(a, "tables", "T.tmdl"), "table T\n");
                File.WriteAllText(Path.Combine(b, "tables", "T.tmdl"), "table T\n");

                Assert.Equal(StartPlanProjection.OfFolder(a), StartPlanProjection.OfFolder(b));
                Assert.DoesNotContain("Semanticus_ConsentId", StartPlanProjection.OfFolder(a));   // the engine's own marker is not part of the model a person is shown (join 6, S1)
                Assert.Contains("annotation Keep = yes", StartPlanProjection.OfFolder(a));
                Assert.DoesNotContain("__TEdtr", StartPlanProjection.OfFolder(a));

                File.WriteAllText(Path.Combine(b, "tables", "T.tmdl"), "table T2\n");   // a real difference is never ignored
                Assert.NotEqual(StartPlanProjection.OfFolder(a), StartPlanProjection.OfFolder(b));
            }
            finally
            {
                try { Directory.Delete(a, true); } catch { }
                try { Directory.Delete(b, true); } catch { }
            }
        }

        [Fact]
        public async Task Taking_the_projection_changes_nothing_about_the_model_it_reads()
        {
            using var rig = new Rig(); await rig.InitAsync();
            var rev = rig.Person.Revision; var steps = await rig.UndoStepsAsync();
            var unsaved = await rig.Person.RunAsync(() => rig.Person.HasUnsavedEdits);
            var stamp = await rig.Person.ReadAsync(m => m.GetAnnotation("__TEdtr"));

            var one = await rig.Person.RunAsync(() => StartPlanProjection.Of(rig.Person));
            var two = await rig.Person.RunAsync(() => StartPlanProjection.Of(rig.Person));

            Assert.Equal(one, two);
            Assert.Equal(rev, rig.Person.Revision);
            Assert.Equal(steps, await rig.UndoStepsAsync());
            Assert.Equal(unsaved, await rig.Person.RunAsync(() => rig.Person.HasUnsavedEdits));
            Assert.Equal(stamp, await rig.Person.ReadAsync(m => m.GetAnnotation("__TEdtr")));   // a stamp the model did not carry is not left on it
        }
    }
    /// <summary>The build verb end to end through the engine (the start, the scratch, the preview, the approval, the build),
    /// on the assistant's door as well as the person's: either door may run the build of an approved plan, and neither may
    /// run it without one.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanBuildVerbTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origHome;
        private readonly List<LocalEngine> _engines = new List<LocalEngine>();

        public StartPlanBuildVerbTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "spb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home", ".semanticus"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            foreach (var e in _engines) { try { e.Dispose(); } catch { } }
            try { Directory.Delete(_root, true); } catch { }
        }

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "OrderId" }, Columns = new[]
                {
                    new FabricColumn { Name = "OrderId", DataType = "Int64", Ordinal = 1 }, new FabricColumn { Name = "ProductId", DataType = "Int64", Ordinal = 2 },
                    new FabricColumn { Name = "Amount", DataType = "Decimal", Ordinal = 3 } } },
                new FabricTable { Schema = "dbo", Name = "Product", KeyColumns = new[] { "ProductId" }, Columns = new[]
                {
                    new FabricColumn { Name = "ProductId", DataType = "Int64", Ordinal = 1 }, new FabricColumn { Name = "Name", DataType = "String", Ordinal = 2 } } },
            },
            ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "ProductId", ToSchema = "dbo", ToTable = "Product", ToColumn = "ProductId" } },
        };

        /// <summary>A started plan, previewed on its own scratch, with the stand-in materializer.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions, string Digest)> PreviewedAsync()
        {
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, TestEntitlements.Pro, _root);
            _engines.Add(e);
            e.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(Schema());
            await e.StartModelFromSqlAsync(new StartModelRequest { Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = Path.Combine(_root, "dest"), Name = "Sales Model" }, "human");
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { Materialize = StartPlanBuildTests.Standin };
            var doc = e.StartPlanDocForTest();
            var scratch = await e.OpenStartScratchAsync();
            var preview = await e.PreviewStartPlanAsync(scratch.Session, doc);
            e.StartPlanDocForTestMutate(d => d.Preview = preview);
            return (e, sessions, StartPlanViewBuilder.Digest(e.StartPlanDocForTest()));
        }

        [Theory]
        [InlineData("human")]
        [InlineData("agent")]
        public async Task An_approved_plan_builds_on_either_door_in_one_step_and_the_plan_is_finished(string origin)
        {
            var (e, sessions, digest) = await PreviewedAsync();
            var steps = sessions.Current.UndoStateNow().Steps;
            e.StartPlanDocForTestMutate(d => d.Approval = new StartApproval { Digest = digest, Utc = DateTime.UtcNow });
            var heard = new List<StartPlanView>();
            sessions.Bus.StartPlanChanged += heard.Add;

            var r = await e.BuildStartPlanAsync(digest, origin);

            Assert.True(r.Built, r.Refused);
            // The plan is KEPT, marked built, so an undone build can be built again: both doors hear "built", and a read says so (never "no plan").
            Assert.True(Assert.Single(heard, v => v.Built).Built, "a finished build tells both doors the plan was built");
            Assert.True(r.View.Exists && r.View.Built && !r.View.CanBuild);
            Assert.NotNull(e.StartPlanDocForTest().BuiltUtc);
            var read = await e.GetStartPlanAsync();
            Assert.True(read.Exists && read.Built);
            Assert.False(e.StartScratchOpenForTest());
            Assert.Equal(2, await sessions.Current.ReadAsync(m => m.Tables.Count()));
            Assert.Equal(steps + 1, sessions.Current.UndoStateNow().Steps);
            await e.UndoAsync(origin);
            Assert.Equal(0, await sessions.Current.ReadAsync(m => m.Tables.Count()));
        }

        [Theory]
        [InlineData("human")]
        [InlineData("agent")]
        public async Task Without_an_approval_neither_door_builds_and_the_plan_stays(string origin)
        {
            var (e, sessions, digest) = await PreviewedAsync();
            var heard = new List<StartPlanView>();
            sessions.Bus.StartPlanChanged += heard.Add;

            var r = await e.BuildStartPlanAsync(digest, origin);

            Assert.False(r.Built);
            Assert.Contains("not been approved", r.Refused);
            Assert.True(r.View.Exists);
            Assert.Empty(heard);   // a refusal moved nothing, so it tells nobody
            Assert.NotNull(e.StartPlanDocForTest());
            Assert.Equal(0, await sessions.Current.ReadAsync(m => m.Tables.Count()));
        }

        [Fact]
        public async Task An_edit_after_approval_voids_it_on_the_verb_too()
        {
            var (e, sessions, digest) = await PreviewedAsync();
            e.StartPlanDocForTestMutate(d => d.Approval = new StartApproval { Digest = digest, Utc = DateTime.UtcNow });
            e.StartPlanDocForTestMutate(d => d.Pins["table:Sales"] = "Every sale.");

            var r = await e.BuildStartPlanAsync(digest, "agent");

            Assert.False(r.Built);
            Assert.Equal(0, await sessions.Current.ReadAsync(m => m.Tables.Count()));
            Assert.NotNull(e.StartPlanDocForTest());
        }

        [Fact]
        public async Task A_parked_plan_is_not_built_and_there_is_nothing_to_build_with_no_plan()
        {
            var (e, sessions, digest) = await PreviewedAsync();
            e.StartPlanDocForTestMutate(d => d.Approval = new StartApproval { Digest = digest, Utc = DateTime.UtcNow });
            await e.CreateModelAsync("Another", 1604, discardUnsaved: true);   // another model is open: the plan parks

            var parked = await e.BuildStartPlanAsync(digest, "human");
            Assert.False(parked.Built);
            Assert.Contains("Another model is open", parked.Refused);

            await e.DiscardStartPlanAsync("human");
            var none = await e.BuildStartPlanAsync(digest, "human");
            Assert.False(none.Built);
            Assert.Contains("no start plan", none.Refused);
        }
    }
}
