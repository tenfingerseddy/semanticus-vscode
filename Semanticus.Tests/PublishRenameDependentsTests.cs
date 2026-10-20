using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// UAT 18 Oct (P1, blocker): one Publish press sent a column rename but not the row filter that follows it, so the role on
    /// the service pointed at a column that no longer existed. A rename and everything that names the column must land in the
    /// same push. Offline, raw TOM.
    /// </summary>
    [Collection("restore-root")]
    public sealed class PublishRenameDependentsTests
    {
        private readonly RestoreRootFixture _fx;
        public PublishRenameDependentsTests(RestoreRootFixture fx) => _fx = fx;

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static TOM.Database Db(bool renamed)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = Build(renamed) };
            foreach (var t in db.Model.Tables.ToList())
                t.Partitions.Add(new TOM.Partition { Name = t.Name, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            return db;
        }

        private static TOM.Column Add(TOM.Table t, string name, string tag, string source = null)
        {
            var c = new TOM.DataColumn { Name = name, DataType = TOM.DataType.String, SourceColumn = source ?? name, LineageTag = tag };
            t.Columns.Add(c);
            return c;
        }

        // The same model before and after the person renamed three columns: Store[Region], Store[AuditNote], Product[CategorySortOrder].
        private static TOM.Model Build(bool renamed)
        {
            var m = new TOM.Model();
            var store = new TOM.Table { Name = "Store", LineageTag = "t-store" };
            Add(store, "StoreKey", "c-storekey");
            Add(store, renamed ? "Store Region" : "Region", "c-region", "Region");
            Add(store, renamed ? "Audit Note" : "AuditNote", "c-audit", "AuditNote");
            m.Tables.Add(store);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            var category = Add(product, "Category", "c-category");
            var order = Add(product, renamed ? "Category Sort Order" : "CategorySortOrder", "c-order", "CategorySortOrder");
            category.SortByColumn = order;
            m.Tables.Add(product);
            var date = new TOM.Table { Name = "Date", LineageTag = "t-date" };
            var monthNumber = Add(date, renamed ? "Month Number" : "MonthNumber", "c-mn", "MonthNumber");
            var monthName = Add(date, renamed ? "Month Name" : "MonthName", "c-mname", "MonthName");
            var yearMonth = Add(date, renamed ? "Year Month" : "YearMonth", "c-ym", "YearMonth");
            var monthStart = Add(date, renamed ? "Month Start" : "MonthStart", "c-ms", "MonthStart");
            monthName.SortByColumn = monthNumber;
            if (renamed) yearMonth.SortByColumn = monthStart;   // new in this push
            if (renamed) { date.DataCategory = "Time"; monthNumber.IsKey = true; monthNumber.Description = "Month of the year, 1 to 12."; }
            m.Tables.Add(date);
            var role = new TOM.ModelRole { Name = "Regional Managers", ModelPermission = TOM.ModelPermission.Read };
            var perm = new TOM.TablePermission { Table = store, FilterExpression = renamed ? "Store[Store Region] = \"North\"" : "Store[Region] = \"North\"" };
            role.TablePermissions.Add(perm);
            perm.ColumnPermissions.Add(new TOM.ColumnPermission { Column = store.Columns.Find(renamed ? "Audit Note" : "AuditNote"), MetadataPermission = TOM.MetadataPermission.None });
            m.Roles.Add(role);
            return m;
        }

        [Fact]
        public void A_column_rename_and_the_role_and_sort_by_that_follow_it_land_in_one_push()
        {
            var session = Build(renamed: true);
            var live = Build(renamed: false);

            var diff = ModelCompare.Diff(session, live, "copy", "service");
            var all = new HashSet<string>(diff.Items.Where(i => i.Action != "Equal").Select(i => i.Ref));
            var outcome = ModelCompare.Apply(session, live, diff, all);

            Assert.Empty(outcome.Failed);
            var role = live.Roles["Regional Managers"];
            var perm = role.TablePermissions["Store"];
            Assert.Equal("Store[Store Region] = \"North\"", perm.FilterExpression);
            Assert.Equal("Audit Note", Assert.Single(perm.ColumnPermissions.Cast<TOM.ColumnPermission>()).Name);
            Assert.NotNull(live.Tables["Store"].Columns.Find("Store Region"));
            Assert.Equal("Category Sort Order", live.Tables["Product"].Columns["Category"].SortByColumn?.Name);
            Assert.Same(live.Tables["Product"].Columns["Category Sort Order"], live.Tables["Product"].Columns["Category"].SortByColumn);
        }

        // Verifier v-fix-publish-role (18 Oct): Claude renamed the key columns of a relationship. A relationship is matched by its ends, so the compare read it as a new
        // relationship plus one "only on the service". The renamed columns carry it; it must not read as a change of its own, or stop a whole publish counting as whole.
        private static TOM.Model Keyed(bool renamed, TOM.CrossFilteringBehavior cross = TOM.CrossFilteringBehavior.OneDirection)
        {
            var m = new TOM.Model();
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            var from = Add(sales, renamed ? "Store Key" : "StoreKey", "c-sales-sk", "StoreKey");
            m.Tables.Add(sales);
            var store = new TOM.Table { Name = "Store", LineageTag = "t-store" };
            var to = Add(store, renamed ? "Store Key" : "StoreKey", "c-store-sk", "StoreKey");
            m.Tables.Add(store);
            m.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = from, ToColumn = to, FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One, CrossFilteringBehavior = cross });
            return m;
        }

        [Fact]
        public void A_relationship_whose_key_columns_were_only_renamed_is_carried_by_the_rename()
        {
            var diff = ModelCompare.Diff(Keyed(renamed: true), Keyed(renamed: false), "copy", "service");

            var rels = diff.Items.Where(i => i.ObjectType == "Relationship").ToList();
            Assert.Equal(2, rels.Count);   // matched by ends, so a rename still reads as new plus removed in the raw list
            Assert.All(rels, i => Assert.True(i.CarriedByRename, i.Action + " " + i.Name));
            Assert.All(diff.Items.Where(i => i.ObjectType == "Column" && i.Action != "Equal"), i => Assert.False(i.CarriedByRename));
        }

        // UAT recheck 19 Oct (D1): Claude renamed a key column, then accepted the key-names warning. That writes an ignore note onto the relationship.
        // The note is not a different relationship, so the rename still carries it, and the note itself must still go with the one press.
        private const string IgnoreNote = "BestPracticeAnalyzer_IgnoreRules";
        private static TOM.Model KeyedWithNote()
        {
            var m = Keyed(renamed: true);
            m.Relationships.Cast<TOM.Relationship>().Single().Annotations.Add(new TOM.Annotation { Name = IgnoreNote, Value = "{\"RuleIDs\":[\"SEM_RELATIONSHIP_KEY_NAMES_SHOULD_MATCH\"]}" });
            return m;
        }

        [Fact]
        public void A_renamed_relationship_that_also_gained_an_ignore_note_is_still_carried_by_the_rename()
        {
            var diff = ModelCompare.Diff(KeyedWithNote(), Keyed(renamed: false), "copy", "service");

            var rels = diff.Items.Where(i => i.ObjectType == "Relationship").ToList();
            Assert.Equal(2, rels.Count);
            Assert.All(rels, i => Assert.True(i.CarriedByRename, i.Action + " " + i.Name));
        }

        [Fact]
        public void The_ignore_note_on_a_renamed_relationship_lands_when_only_the_listed_rows_are_applied()
        {
            var session = KeyedWithNote();
            var live = Keyed(renamed: false);
            var diff = ModelCompare.Diff(session, live, "copy", "service");
            var listed = new HashSet<string>(diff.Items.Where(i => i.Action != "Equal" && !i.CarriedByRename).Select(i => i.Ref));   // what the page sends

            var outcome = ModelCompare.Apply(session, live, diff, listed);

            Assert.Empty(outcome.Failed);
            var rel = Assert.Single(live.Relationships.Cast<TOM.Relationship>());
            Assert.Equal("Store Key", ((TOM.SingleColumnRelationship)rel).FromColumn.Name);
            Assert.True(rel.Annotations.Contains(IgnoreNote));
        }

        [Fact]
        public async Task One_press_sends_a_key_rename_and_the_ignore_note_on_its_relationship_and_leaves_no_second_relationship()
        {
            static TOM.Database Wrap(TOM.Model m)
            {
                var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = m };
                foreach (var t in db.Model.Tables.ToList())
                    t.Partitions.Add(new TOM.Partition { Name = t.Name, Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                return db;
            }
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            var src = Path.Combine(Path.GetTempPath(), $"sem-keynote-{Guid.NewGuid():N}.bim");
            File.WriteAllText(src, TOM.JsonSerializer.SerializeDatabase(Wrap(KeyedWithNote())));
            var live = Wrap(Keyed(renamed: false));
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(live), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI));
                engine.WorkspacePushHook = (bim, _) =>
                {
                    var sent = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI);
                    var rep = LiveDeploy.SyncModels(sent.Model, live.Model, apply: true, identityStrict: true);
                    rep.Committed = rep.TotalChanges > 0;
                    return rep;
                };
                var left = new ModelRef { Kind = "file", Path = src };
                var right = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" };
                // What the page sends: every listed row. The relationship is carried by the rename, so it is not listed.
                var shown = ModelCompare.Diff(KeyedWithNote(), Keyed(renamed: false), "copy", "service").Items.Where(i => i.Action != "Equal" && !i.CarriedByRename).Select(i => i.Ref).ToArray();
                Assert.NotEmpty(shown);

                var r = await engine.ApplyDiffAsync(left, right, shown, commit: true, "human");

                Assert.Empty(r.FailedRefs);
                Assert.True(r.Applied);
                var rel = Assert.Single(live.Model.Relationships.Cast<TOM.Relationship>());
                Assert.Equal("Store Key", ((TOM.SingleColumnRelationship)rel).FromColumn.Name);
                Assert.True(rel.Annotations.Contains(IgnoreNote));
                Assert.DoesNotContain(ModelCompare.Diff(KeyedWithNote(), live.Model, "copy", "service").Items, i => (i.ObjectType == "Relationship" || i.ObjectType == "Column") && i.Action != "Equal" && !i.CarriedByRename);
            }
            finally { File.Delete(src); }
        }

        // Verifier v-fix-uat-recheck (19 Oct): the carried copy of notes must leave a note some other tool wrote on the service alone, and keep the service's relationship (its id).
        [Fact]
        public void Carrying_the_ignore_note_keeps_a_foreign_note_and_the_id_of_the_relationship_on_the_service()
        {
            var session = KeyedWithNote();
            var live = Keyed(renamed: false);
            var liveRel = live.Relationships.Cast<TOM.Relationship>().Single();
            liveRel.Name = "svc-id-1";
            liveRel.Annotations.Add(new TOM.Annotation { Name = "Client_ReviewNote", Value = "keep me" });
            var diff = ModelCompare.Diff(session, live, "copy", "service");
            Assert.All(diff.Items.Where(i => i.ObjectType == "Relationship"), i => Assert.True(i.CarriedByRename, i.Action + " " + i.Name));
            var listed = new HashSet<string>(diff.Items.Where(i => i.Action != "Equal" && !i.CarriedByRename).Select(i => i.Ref));

            var outcome = ModelCompare.Apply(session, live, diff, listed);

            Assert.Empty(outcome.Failed);
            var rel = Assert.Single(live.Relationships.Cast<TOM.Relationship>());
            Assert.Same(liveRel, rel);
            Assert.Equal("svc-id-1", rel.Name);
            Assert.Equal("keep me", rel.Annotations["Client_ReviewNote"].Value);
            Assert.True(rel.Annotations.Contains(IgnoreNote));
        }

        // Verifier v-fix-uat-recheck: a note next to a REAL change of the relationship does not let the rename hide it.
        [Fact]
        public void A_renamed_relationship_with_a_note_and_a_real_change_is_not_carried_by_the_rename()
        {
            var left = Keyed(renamed: true, TOM.CrossFilteringBehavior.BothDirections);
            left.Relationships.Cast<TOM.Relationship>().Single().Annotations.Add(new TOM.Annotation { Name = IgnoreNote, Value = "{}" });

            var diff = ModelCompare.Diff(left, Keyed(renamed: false), "copy", "service");

            Assert.Equal(2, diff.Items.Count(i => i.ObjectType == "Relationship"));
            Assert.All(diff.Items.Where(i => i.ObjectType == "Relationship"), i => Assert.False(i.CarriedByRename, i.Action + " " + i.Name));
        }

        // Verifier v-fix-uat-recheck: the old relationship really removed and a new one made on the same (renamed) key columns, with its own id and a note.
        // On those columns that is the same relationship (one column pair holds one relationship), so the rename may carry it. What must hold: one press
        // leaves exactly one relationship there, with the note, and nothing left to list.
        [Fact]
        public void A_relationship_removed_and_remade_on_the_renamed_keys_with_a_note_lands_as_one_relationship()
        {
            var session = KeyedWithNote();
            session.Relationships.Cast<TOM.Relationship>().Single().Name = "remade-id";
            var live = Keyed(renamed: false);
            var diff = ModelCompare.Diff(session, live, "copy", "service");
            var listed = new HashSet<string>(diff.Items.Where(i => i.Action != "Equal" && !i.CarriedByRename).Select(i => i.Ref));

            var outcome = ModelCompare.Apply(session, live, diff, listed);

            Assert.Empty(outcome.Failed);
            var rel = (TOM.SingleColumnRelationship)Assert.Single(live.Relationships.Cast<TOM.Relationship>());
            Assert.Equal(("Store Key", "Store Key"), (rel.FromColumn.Name, rel.ToColumn.Name));
            Assert.Equal(TOM.CrossFilteringBehavior.OneDirection, rel.CrossFilteringBehavior);
            Assert.True(rel.Annotations.Contains(IgnoreNote));
            Assert.DoesNotContain(ModelCompare.Diff(session, live, "copy", "service").Items, i => (i.ObjectType == "Relationship" || i.ObjectType == "Column") && i.Action != "Equal" && !i.CarriedByRename);
        }

        [Fact]
        public void A_renamed_relationship_that_also_changed_something_else_is_not_carried_by_the_rename()
        {
            var diff = ModelCompare.Diff(Keyed(renamed: true, TOM.CrossFilteringBehavior.BothDirections), Keyed(renamed: false), "copy", "service");

            Assert.All(diff.Items.Where(i => i.ObjectType == "Relationship"), i => Assert.False(i.CarriedByRename, i.Action + " " + i.Name));
        }

        // Verifier v-fix2-publish-role (19 Oct): the mark hides rows from the Publish page, so it must never take a REAL new or REAL removed relationship with it.
        // Sales, Store and Product, with the keys of Sales->Store renamed or not; each relationship is named for its ends ("store" is Sales.StoreKey->Store.StoreKey).
        private static TOM.Model Shaped(bool renamed, params string[] rels)
        {
            var m = new TOM.Model();
            var sk = renamed ? "Store Key" : "StoreKey";
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            var salesSk = Add(sales, sk, "c-sales-sk", "StoreKey");
            var salesPk = Add(sales, "ProductKey", "c-sales-pk");
            m.Tables.Add(sales);
            var store = new TOM.Table { Name = "Store", LineageTag = "t-store" };
            var storeSk = Add(store, sk, "c-store-sk", "StoreKey");
            var region = Add(store, "Region", "c-store-region");
            m.Tables.Add(store);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            var productPk = Add(product, "ProductKey", "c-product-pk");
            m.Tables.Add(product);
            var ends = new Dictionary<string, (TOM.Column from, TOM.Column to)> { ["store"] = (salesSk, storeSk), ["product"] = (salesPk, productPk), ["region"] = (salesSk, region) };
            foreach (var r in rels)
                m.Relationships.Add(new TOM.SingleColumnRelationship { Name = r, FromColumn = ends[r].from, ToColumn = ends[r].to, FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One });
            return m;
        }

        [Fact]
        public void A_really_new_relationship_beside_a_renamed_one_is_still_a_change_of_its_own()
        {
            var diff = ModelCompare.Diff(Shaped(true, "store", "product"), Shaped(false, "store"), "copy", "service");

            var created = Assert.Single(diff.Items, i => i.ObjectType == "Relationship" && i.Action == "Create" && i.Name.Contains("Product"));
            Assert.False(created.CarriedByRename);
            Assert.Equal(2, diff.Items.Count(i => i.ObjectType == "Relationship" && i.CarriedByRename));   // only the renamed pair
        }

        [Fact]
        public void A_really_removed_relationship_beside_a_renamed_one_is_still_a_change_of_its_own()
        {
            var diff = ModelCompare.Diff(Shaped(true, "store"), Shaped(false, "store", "product"), "copy", "service");

            var removed = Assert.Single(diff.Items, i => i.ObjectType == "Relationship" && i.Action == "Delete" && i.Name.Contains("Product"));
            Assert.False(removed.CarriedByRename);
            Assert.Equal(2, diff.Items.Count(i => i.ObjectType == "Relationship" && i.CarriedByRename));
        }

        [Fact]
        public void A_relationship_removed_from_the_renamed_keys_themselves_is_not_hidden()
        {
            var diff = ModelCompare.Diff(Shaped(true), Shaped(false, "store"), "copy", "service");

            var removed = Assert.Single(diff.Items, i => i.ObjectType == "Relationship");
            Assert.Equal("Delete", removed.Action);
            Assert.False(removed.CarriedByRename);
        }

        [Fact]
        public void A_relationship_added_on_the_renamed_keys_where_the_service_had_none_is_not_hidden()
        {
            var diff = ModelCompare.Diff(Shaped(true, "store"), Shaped(false), "copy", "service");

            var created = Assert.Single(diff.Items, i => i.ObjectType == "Relationship");
            Assert.Equal("Create", created.Action);
            Assert.False(created.CarriedByRename);
        }

        [Fact]
        public void A_renamed_key_whose_relationship_also_moved_to_another_column_is_not_hidden()
        {
            var diff = ModelCompare.Diff(Shaped(true, "region"), Shaped(false, "store"), "copy", "service");

            var rels = diff.Items.Where(i => i.ObjectType == "Relationship").ToList();
            Assert.Equal(new[] { "Create", "Delete" }, rels.Select(i => i.Action).OrderBy(a => a).ToArray());
            Assert.All(rels, i => Assert.False(i.CarriedByRename, i.Action + " " + i.Name));
        }

        [Fact]
        public void The_send_step_writes_the_role_with_the_renamed_columns_and_names_nothing_as_left_behind()
        {
            var merged = Build(renamed: true);   // the model the push staged: live plus the ticked changes
            var live = Build(renamed: false);

            var rep = LiveDeploy.SyncModels(merged, live, apply: true, identityStrict: true);

            Assert.Empty(rep.Unmatched);
            Assert.Contains("role:Regional Managers", rep.SyncedRefs);
            var perm = live.Roles["Regional Managers"].TablePermissions["Store"];
            Assert.Equal("Store[Store Region] = \"North\"", perm.FilterExpression);
            Assert.Equal("Audit Note", Assert.Single(perm.ColumnPermissions.Cast<TOM.ColumnPermission>()).Name);
            Assert.Equal("Category Sort Order", live.Tables["Product"].Columns["Category"].SortByColumn?.Name);
        }

        [Fact]
        public async Task One_press_sends_the_rename_and_the_role_and_leaves_nothing_behind()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            var src = Path.Combine(Path.GetTempPath(), $"sem-rename-{Guid.NewGuid():N}.bim");
            File.WriteAllText(src, TOM.JsonSerializer.SerializeDatabase(Db(renamed: true)));
            var live = Db(renamed: false);   // what the service holds, and what the send step writes into
            try
            {
                engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(live), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI));
                engine.WorkspacePushHook = (bim, _) =>
                {
                    var sent = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(bim), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI);
                    var rep = LiveDeploy.SyncModels(sent.Model, live.Model, apply: true, identityStrict: true);
                    rep.Committed = rep.TotalChanges > 0;
                    return rep;
                };

                var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src },
                    new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS", Database = "DS" }, null, commit: true, "human");

                Assert.Empty(r.FailedRefs);
                Assert.True(r.Applied);
                var perm = live.Model.Roles["Regional Managers"].TablePermissions["Store"];
                Assert.Equal("Store[Store Region] = \"North\"", perm.FilterExpression);
                Assert.Equal("Audit Note", Assert.Single(perm.ColumnPermissions.Cast<TOM.ColumnPermission>()).Name);
                Assert.Equal("Category Sort Order", live.Model.Tables["Product"].Columns["Category"].SortByColumn?.Name);
                Assert.Equal("Month Number", live.Model.Tables["Date"].Columns["Month Name"].SortByColumn?.Name);
                Assert.Equal("Month Start", live.Model.Tables["Date"].Columns["Year Month"].SortByColumn?.Name);
                Assert.Equal("Time", live.Model.Tables["Date"].DataCategory);
            }
            finally { File.Delete(src); }
        }
    }

    /// <summary>
    /// UAT 18 Oct (P2, P3): the window must be told when a publish lands (the footer read "Publish: Not set yet" until the model was reopened),
    /// and a publish must not leave the model reading unsaved when the person had nothing unsaved (the Plan said "Save the changes you have
    /// made" and the engine held the model ten minutes, because the publish's own record was left as unsaved work).
    /// </summary>
    [Collection("restore-root")]
    public sealed class PublishLandedStateTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;
        public PublishLandedStateTests(RestoreRootFixture fx)
        {
            _safeRoot = fx.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-landed-" + Guid.NewGuid().ToString("N"));
            ConnectionRegistry.RootOverride = _root;
        }
        public void Dispose() { ConnectionRegistry.RootOverride = _safeRoot; try { Directory.Delete(_root, true); } catch { } }

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // The open model is a published one (LiveOrigin set), saved and unchanged. The service copy differs from it in two places:
        // a column description the person ticks, and one they leave unticked.
        private static async Task<(LocalEngine engine, SessionManager sessions)> OpenPublishedAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            ConnectionRegistry.Remember("xmla", Endpoint, "DS", "DS", null, "interactive");
            sessions.Current.LiveOrigin = new LiveOrigin(Endpoint, "DS", null, "interactive");
            var live = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(TestModels.FindBim()), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI);
            live.Model.Tables["Product"].Columns["ProductSubcategoryKey"].Description = "Changed on the service.";
            live.Model.Tables["Product"].Columns["ProductKey"].Description = "Also changed on the service, and left out of the press.";
            var liveJson = TOM.JsonSerializer.SerializeDatabase(live);
            engine.WorkspaceSnapshotHook = () => Task.FromResult(TOM.JsonSerializer.DeserializeDatabase(liveJson, null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI));
            engine.WorkspacePushHook = (_, __) => new DeployReport { Committed = true, TotalChanges = 1, SyncedRefs = new[] { "column:Product/ProductSubcategoryKey" } };
            return (engine, sessions);
        }

        private static Task<ApplyDiffResult> PublishAsync(LocalEngine engine) =>
            engine.ApplyDiffAsync(new ModelRef { Kind = "session" }, new ModelRef { Kind = "workspace", Endpoint = Endpoint, Database = "DS" },
                new[] { "column:Product/ProductSubcategoryKey" }, commit: true, "human");

        [Fact]
        public async Task A_landed_publish_tells_the_window_so_the_footer_reads_the_new_target()
        {
            var (engine, sessions) = await OpenPublishedAsync();
            using (engine)
            {
                var told = 0;
                sessions.Bus.Changed += n => { if (n.Deltas.Length == 0) told++; };
                var r = await PublishAsync(engine);
                Assert.True(r.Applied, r.Error);
                Assert.True(told > 0, "the window re-reads the footer on a change notice; a publish sent none");
                Assert.False((await engine.ConnectionContextAsync()).Publishing.Implied, "the place the publish went to is the target from now on");
            }
        }

        [Fact]
        public async Task A_publish_that_leaves_a_row_unticked_does_not_turn_a_saved_model_into_an_unsaved_one()
        {
            var (engine, sessions) = await OpenPublishedAsync();
            using (engine)
            {
                var s = sessions.Current;
                Assert.False(await s.RunAsync(() => s.HasUnsavedEdits), "the model starts saved");
                var r = await PublishAsync(engine);
                Assert.True(r.Applied, r.Error);
                Assert.False(await s.RunAsync(() => s.HasUnsavedEdits), "the person changed nothing, so the Plan and the close guard have nothing to ask for");
            }
        }

        // Verifier v-fix-publish-role (18 Oct, live lane): Claude worked the Plan and saved, which on a folder that is not in git keeps a copy
        // ("Northstar Sales Working copy.bim"), as its instructions say. Then one press published everything. The engine still read the work as
        // unsaved (hasUnsavedEdits true), the footer said "copy kept, not published", and closing VS Code held the engine for the ten minute cap.
        // Every change is in the kept copy and on the service, so nothing is unsaved and nothing should hold the engine.
        [Fact]
        public async Task Work_kept_in_the_assistants_copy_and_published_does_not_hold_the_engine_on_close()
        {
            var (engine, sessions) = await OpenPublishedAsync();
            using (engine)
            {
                var s = sessions.Current;
                await engine.CreateMeasureAsync("table:Product", "Kept Copy Probe", "1", "agent");
                Directory.CreateDirectory(_root);
                var saved = await engine.SaveAsync(Path.Combine(_root, "Northstar Sales Working copy.bim"), "bim", false, "agent");
                Assert.True(saved.IsBackupCopy, "the assistant's save keeps a copy: " + saved.Path);
                var r = await PublishAsync(engine);
                Assert.True(r.Applied, r.Error);
                Assert.False(Program.KeepOwnerAlive(false, 0, s), "every change is in the kept copy and the publish landed, so a close must not hold the engine");
            }
        }

        // Verifier v-fix2-publish-role (19 Oct): the other side of the fix above. Work made AFTER the kept copy is in no copy and not saved, so a close must still
        // hold the engine for it, with or without a publish in between, and whether the newer edit is a change or an undo.
        [Fact]
        public async Task Work_made_after_the_kept_copy_still_holds_the_engine_on_close()
        {
            var (engine, sessions) = await OpenPublishedAsync();
            using (engine)
            {
                var s = sessions.Current;
                await engine.CreateMeasureAsync("table:Product", "Kept Copy Probe", "1", "agent");
                Directory.CreateDirectory(_root);
                Assert.True((await engine.SaveAsync(Path.Combine(_root, "Northstar Sales Working copy.bim"), "bim", false, "agent")).IsBackupCopy);
                Assert.False(Program.KeepOwnerAlive(false, 0, s), "every change is in the copy");

                await engine.CreateMeasureAsync("table:Product", "After The Copy", "2", "human");
                Assert.True(await s.RunAsync(() => s.HasUnsavedEdits));
                Assert.True(Program.KeepOwnerAlive(false, 0, s), "a change made after the copy is in no copy and not saved: the close must hold");

                Assert.True((await PublishAsync(engine)).Applied);
                Assert.True(Program.KeepOwnerAlive(false, 0, s), "a publish of one other row does not put the new measure anywhere");

                Assert.True((await engine.SaveAsync(Path.Combine(_root, "Northstar Sales Working copy.bim"), "bim", false, "agent")).IsBackupCopy);
                Assert.False(Program.KeepOwnerAlive(false, 0, s), "kept again: the copy holds it now");
                Assert.Contains("After The Copy", File.ReadAllText(Path.Combine(_root, "Northstar Sales Working copy.bim")));

                await engine.UndoAsync("human");
                Assert.True(Program.KeepOwnerAlive(false, 0, s), "an undo after the copy makes the model differ from the copy again");
            }
        }
    }
}
