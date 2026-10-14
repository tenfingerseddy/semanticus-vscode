using System.Linq;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// LiveDeploy.SyncModels — the server-free diff/apply core behind deploy_live. Born from a LIVE bug
    /// (2026-07-03): a partition's M edited in the session deployed NOWHERE and wasn't even reported — the
    /// sync only carried names/DAX/visibility-style metadata. The contract pinned here: partition source
    /// expressions (M / calc-table DAX / legacy query) and shared M expressions/parameters sync like measure
    /// DAX does; anything structural (new/removed partitions, a source-type change) is REPORTED, never
    /// applied and never silently dropped; dry-run mutates nothing; the audit-trail annotations ride along
    /// only with a real change. All offline — two in-memory TOM trees, no server.
    /// </summary>
    public sealed class LiveDeploySyncTests
    {
        private static TOM.Model NewModel(string mExpr = "let Source = 1 in Source")
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = mExpr } });
            t.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-amount" });
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM ( Sales[Amount] )", LineageTag = "tag-total" });
            db.Model.Tables.Add(t);
            return db.Model;
        }

        // Kane, 13 Oct: publishing a model raised to 1701 with the Plan's calendar failed on the Date table, and nothing could push the level.
        // The level goes up first and the calendar follows; a lower source level changes nothing.
        private static TOM.Model WithCalendar(TOM.Model m, int level)
        {
            m.Database.CompatibilityLevel = level;
            var t = m.Tables["Sales"];
            var cal = new TOM.Calendar { Name = "Gregorian", LineageTag = "cal-1" };
            cal.CalendarColumnGroups.Add(new TOM.TimeUnitColumnAssociation(TOM.TimeUnit.Date) { PrimaryColumn = t.Columns["Amount"] });
            t.Calendars.Add(cal);
            return m;
        }

        [Fact]
        public void A_higher_compatibility_level_and_its_calendar_reach_the_live_model_in_one_push()
        {
            var src = WithCalendar(NewModel(), 1701);
            var live = NewModel();
            var raised = new System.Collections.Generic.List<int>();
            var dry = LiveDeploy.SyncAndApply(src, live, commit: false, "ep", "db", null, () => { }, _ => { }, raiseCompatibility: raised.Add);
            Assert.Empty(raised);                                  // a preview raises nothing
            Assert.Equal(1600, live.Database.CompatibilityLevel);
            Assert.Contains("compatibility", dry.SyncedRefs);
            Assert.Contains("table:Sales", dry.SyncedRefs);
            Assert.Empty(dry.Unmatched);

            var saved = 0;
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null, () => saved++, _ => { }, raiseCompatibility: n => { raised.Add(n); live.Database.CompatibilityLevel = n; });
            Assert.Equal(new[] { 1701 }, raised);
            Assert.Equal(1701, live.Database.CompatibilityLevel);
            Assert.Null(rep.Error); Assert.True(rep.Committed); Assert.Equal(1, saved);
            Assert.Contains("compatibility", rep.SyncedRefs);
            Assert.Contains("table:Sales", rep.SyncedRefs);
            Assert.Empty(rep.Unmatched);
            Assert.Single(live.Tables["Sales"].Calendars);
            Assert.Same(live.Tables["Sales"].Columns["Amount"], ((TOM.TimeUnitColumnAssociation)live.Tables["Sales"].Calendars["Gregorian"].CalendarColumnGroups[0]).PrimaryColumn);
        }

        [Fact]
        public void A_refused_level_raise_writes_nothing_and_a_lower_source_level_never_lowers_the_live_one()
        {
            var src = NewModel(); var live = NewModel(); live.Database.CompatibilityLevel = 1701; src.Database.CompatibilityLevel = 1600;
            var raised = new System.Collections.Generic.List<int>();
            var lowered = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null, () => { }, _ => { }, raiseCompatibility: raised.Add);
            Assert.Empty(raised); Assert.Equal(1701, live.Database.CompatibilityLevel); Assert.DoesNotContain("compatibility", lowered.SyncedRefs ?? System.Array.Empty<string>());

            var up = WithCalendar(NewModel(), 1701); var low = NewModel();
            var saved = 0;
            var refused = LiveDeploy.SyncAndApply(up, low, commit: true, "ep", "db", null, () => saved++, _ => { }, raiseCompatibility: _ => throw new System.InvalidOperationException("not allowed"));
            Assert.False(refused.Committed); Assert.Equal(0, saved);
            Assert.Contains("compatibility level from 1600 to 1701", refused.Error);
            // Nothing was sent (saved == 0). The in-memory copy keeps what the sync staged here because a detached model cannot undo; the attached model the person's push uses is undone.
        }

        // Verifier (v-publish-shell, 13 Oct): the level raise is its own live write and Power BI cannot undo it. When the model save
        // that follows it fails, the report must not say nothing was written: the live model now sits at the higher level.
        [Fact]
        public void Verifier_a_raise_that_landed_is_reported_when_the_save_after_it_fails()
        {
            var src = WithCalendar(NewModel(), 1701); var live = NewModel();
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null,
                () => throw new System.InvalidOperationException("boom"), _ => { },
                raiseCompatibility: n => live.Database.CompatibilityLevel = n);
            Assert.Equal(1701, live.Database.CompatibilityLevel);   // the raise is live
            Assert.False(rep.Committed);
            Assert.Contains("1701", rep.Error);                     // the person is told the level went up
            Assert.DoesNotContain("Nothing was committed", rep.Error);
        }

        // A delete the push refuses stops it before anything is sent, so the level raise (which cannot be undone) is never sent.
        [Fact]
        public void A_refused_delete_stops_the_push_before_the_level_is_raised()
        {
            var src = WithCalendar(NewModel(), 1701); var live = NewModel();
            live.Tables["Sales"].LineageTag = "tag-live";
            var raised = new System.Collections.Generic.List<int>(); var saved = 0;
            var impostor = new LiveDeleteTarget { Kind = "table", Ref = "table:Sales", Tag = "tag-gone", Name = "Sales" };
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", new[] { impostor }, () => saved++, _ => { }, raiseCompatibility: raised.Add);
            Assert.False(rep.Committed); Assert.Equal(0, saved);
            Assert.Contains("table:Sales", rep.DeletesRefused);
            Assert.Empty(raised);                                  // the raise was never sent
        }

        // ---- roles: create, update (members, permission, RLS filter) now reach live like any other object ----
        private static TOM.Model WithRole(TOM.Model m, string filter, TOM.ModelPermission perm, params string[] members)
        {
            var role = new TOM.ModelRole { Name = "Readers", ModelPermission = perm };
            foreach (var name in members) role.Members.Add(new TOM.ExternalModelRoleMember { MemberName = name, IdentityProvider = "AzureAD", MemberType = TOM.RoleMemberType.User });
            if (filter != null) role.TablePermissions.Add(new TOM.TablePermission { Table = m.Tables["Sales"], FilterExpression = filter });
            m.Roles.Add(role);
            return m;
        }

        [Fact]
        public void A_new_role_with_a_member_and_a_filter_is_created_on_commit()
        {
            var src = WithRole(NewModel(), "[Amount] > 0", TOM.ModelPermission.Read, "a@contoso.com");
            var live = NewModel();
            var dry = LiveDeploy.SyncModels(src, live, apply: false);
            Assert.Equal(1, dry.Roles);
            Assert.Equal(0, live.Roles.Count);   // dry-run writes nothing
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Roles);
            Assert.Contains("role:Readers", rep.SyncedRefs);
            var role = live.Roles["Readers"];
            Assert.Equal(TOM.ModelPermission.Read, role.ModelPermission);
            Assert.Equal("a@contoso.com", Assert.Single(role.Members).MemberName);
            Assert.Equal("[Amount] > 0", role.TablePermissions["Sales"].FilterExpression);
        }

        // hero17 P6: a renamed role is a create plus a ticked delete of the old name. The delete used to be skipped because a live-only
        // role was never listed, so the old role stayed live. The dry run counts it, and the commit removes it.
        [Fact]
        public void A_ticked_delete_of_a_live_only_role_removes_it_and_the_preview_counts_it()
        {
            var src = NewModel();
            src.Roles.Add(new TOM.ModelRole { Name = "US Stores", ModelPermission = TOM.ModelPermission.Read });
            TOM.Model Live() { var l = NewModel(); l.Roles.Add(new TOM.ModelRole { Name = "Australia Stores", ModelPermission = TOM.ModelPermission.Read }); return l; }
            var del = new[] { "role:Australia Stores" };

            var dryLive = Live();
            var dry = LiveDeploy.SyncAndApply(src, dryLive, commit: false, "ep", "db", null, () => { }, _ => { }, explicitDeleteRefs: del);
            Assert.Equal(1, dry.Deleted);
            Assert.Equal(2, dry.TotalChanges);   // the new role and the removal
            Assert.NotNull(dryLive.Roles.Find("Australia Stores"));   // a preview writes nothing

            var live = Live();
            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null, () => { }, _ => { }, explicitDeleteRefs: del);
            Assert.Contains("role:Australia Stores", rep.DeletedRefs);
            Assert.Null(live.Roles.Find("Australia Stores"));
            Assert.NotNull(live.Roles.Find("US Stores"));
        }

        [Fact]
        public void A_member_saved_with_the_default_provider_goes_live_as_AzureAD()
        {
            var src = NewModel();
            var role = new TOM.ModelRole { Name = "Readers", ModelPermission = TOM.ModelPermission.Read };
            role.Members.Add(new TOM.ExternalModelRoleMember { MemberName = "a@contoso.com", IdentityProvider = "default" });
            src.Roles.Add(role);
            var live = NewModel();
            LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal("AzureAD", ((TOM.ExternalModelRoleMember)Assert.Single(live.Roles["Readers"].Members)).IdentityProvider);
        }

        [Fact]
        public void A_changed_role_updates_members_permission_and_filter_in_place()
        {
            var src = WithRole(NewModel(), "[Amount] > 5", TOM.ModelPermission.ReadRefresh, "a@contoso.com", "c@contoso.com");
            var live = WithRole(NewModel(), "[Amount] > 0", TOM.ModelPermission.Read, "a@contoso.com", "b@contoso.com");
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Roles);
            Assert.Contains("role:Readers", rep.SyncedRefs);
            var role = live.Roles["Readers"];
            Assert.Equal(TOM.ModelPermission.ReadRefresh, role.ModelPermission);
            Assert.Equal(new[] { "a@contoso.com", "c@contoso.com" }, role.Members.Select(x => x.MemberName).OrderBy(x => x).ToArray());
            Assert.Equal("[Amount] > 5", role.TablePermissions["Sales"].FilterExpression);
        }

        [Fact]
        public void A_role_that_differs_only_by_a_server_member_id_is_no_change()
        {
            var src = WithRole(NewModel(), "[Amount] > 0", TOM.ModelPermission.Read, "a@contoso.com");
            var live = NewModel();
            var withId = new TOM.ModelRole { Name = "Readers", ModelPermission = TOM.ModelPermission.Read };
            withId.Members.Add(new TOM.ExternalModelRoleMember { MemberName = "a@contoso.com", IdentityProvider = "AzureAD", MemberType = TOM.RoleMemberType.User, MemberID = "11111111-1111-1111-1111-111111111111" });
            withId.TablePermissions.Add(new TOM.TablePermission { Table = live.Tables["Sales"], FilterExpression = "[Amount] > 0" });
            live.Roles.Add(withId);
            var rep = LiveDeploy.SyncModels(src, live, apply: false);
            Assert.Equal(0, rep.Roles);
            Assert.Equal(0, rep.TotalChanges);
        }

        [Fact]
        public void A_live_only_role_is_left_alone_unless_named_for_delete()
        {
            var src = NewModel();
            var live = WithRole(NewModel(), null, TOM.ModelPermission.Read, "a@contoso.com");
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(0, rep.Roles);
            Assert.Equal(1, live.Roles.Count);
        }

        // VERIFIER (v-role-sync, 12 Oct): a role that HIDES a whole table (table-level object security) must go live
        // hiding it. The sync copies the RLS filter and column permissions but not TablePermission.MetadataPermission,
        // so the role is created with the table visible while the publish reports role:Readers as applied.
        [Fact]
        public void A_new_role_that_hides_a_table_goes_live_hiding_it()
        {
            var src = NewModel();
            var role = new TOM.ModelRole { Name = "Readers", ModelPermission = TOM.ModelPermission.Read };
            role.TablePermissions.Add(new TOM.TablePermission { Table = src.Tables["Sales"], MetadataPermission = TOM.MetadataPermission.None });
            src.Roles.Add(role);
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains("role:Readers", rep.SyncedRefs);
            Assert.Equal(TOM.MetadataPermission.None, live.Roles["Readers"].TablePermissions["Sales"].MetadataPermission);
        }

        [Fact]
        public void A_role_whose_column_permission_names_a_column_not_live_is_unmatched_and_not_written()
        {
            var src = NewModel();
            // IsNullable = false is authored metadata the push cannot create, so the column is held back and is not live.
            src.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "Margin", DataType = TOM.DataType.Int64, LineageTag = "tag-margin", IsNullable = false });
            var role = new TOM.ModelRole { Name = "Readers", ModelPermission = TOM.ModelPermission.Read };
            var tp = new TOM.TablePermission { Table = src.Tables["Sales"] };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = src.Tables["Sales"].Columns["Margin"], MetadataPermission = TOM.MetadataPermission.None });
            role.TablePermissions.Add(tp);
            src.Roles.Add(role);
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains(rep.Unmatched, u => u.StartsWith("role:Readers") && u.Contains("Margin"));
            Assert.DoesNotContain("role:Readers", rep.SyncedRefs);
            Assert.Equal(0, live.Roles.Count);
        }

        // VERIFIER (v-fix-role-sync, 12 Oct): a column the same deploy creates must still carry the role's column permission.
        [Fact]
        public void A_role_whose_column_permission_names_a_column_created_by_the_same_deploy_goes_live()
        {
            var src = NewModel();
            src.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "Margin", DataType = TOM.DataType.Int64, LineageTag = "tag-margin" });
            var role = new TOM.ModelRole { Name = "Readers", ModelPermission = TOM.ModelPermission.Read };
            var tp = new TOM.TablePermission { Table = src.Tables["Sales"] };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = src.Tables["Sales"].Columns["Margin"], MetadataPermission = TOM.MetadataPermission.None });
            role.TablePermissions.Add(tp);
            src.Roles.Add(role);
            var dry = LiveDeploy.SyncModels(src, NewModel(), apply: false);
            Assert.DoesNotContain(dry.Unmatched, u => u.StartsWith("role:Readers"));
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.DoesNotContain(rep.Unmatched, u => u.StartsWith("role:Readers"));
            Assert.Contains("role:Readers", rep.SyncedRefs);
            Assert.Equal(TOM.MetadataPermission.None, live.Roles["Readers"].TablePermissions["Sales"].ColumnPermissions["Margin"].MetadataPermission);
        }

        [Fact]
        public void Identical_models_report_zero_changes()
        {
            var rep = LiveDeploy.SyncModels(NewModel(), NewModel(), apply: false);
            Assert.Equal(0, rep.TotalChanges);
            Assert.Empty(rep.Unmatched);
            Assert.Empty(rep.LiveOnly);
        }

        [Fact]
        public void Partition_m_change_is_counted_on_dry_run_and_mutates_nothing()
        {
            var src = NewModel("let Source = 2 in Source");
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: false);
            Assert.Equal(1, rep.Partitions);
            Assert.Equal(1, rep.TotalChanges);
            Assert.Contains("expression partition:Sales/Sales", rep.Changes);
            Assert.Equal("let Source = 1 in Source",
                ((TOM.MPartitionSource)live.Tables["Sales"].Partitions["Sales"].Source).Expression);   // untouched
        }

        [Fact]
        public void Partition_m_change_is_applied_on_commit()
        {
            var src = NewModel("let Source = 2 in Source");
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Partitions);
            Assert.Equal("let Source = 2 in Source",
                ((TOM.MPartitionSource)live.Tables["Sales"].Partitions["Sales"].Source).Expression);
        }

        [Fact]
        public void Calc_table_partition_dax_is_synced()
        {
            static TOM.Model WithCalc(string dax)
            {
                var m = NewModel();
                var t = new TOM.Table { Name = "Calc", LineageTag = "tag-calc" };
                t.Partitions.Add(new TOM.Partition { Name = "Calc", Source = new TOM.CalculatedPartitionSource { Expression = dax } });
                m.Tables.Add(t);
                return m;
            }
            var src = WithCalc("FILTER ( Sales, Sales[Amount] > 0 )");
            var live = WithCalc("Sales");
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Partitions);
            Assert.Equal("FILTER ( Sales, Sales[Amount] > 0 )",
                ((TOM.CalculatedPartitionSource)live.Tables["Calc"].Partitions["Calc"].Source).Expression);
        }

        [Fact]
        public void New_session_calc_table_is_created_and_queued_for_recalc_on_commit()
        {
            // A brand-new CALCULATED table (self-contained DAX) is created live + queued for a Calculate pass.
            var src = NewModel();
            var ct = new TOM.Table { Name = "TopProducts", LineageTag = "tag-top", Description = "top sellers" };
            ct.Partitions.Add(new TOM.Partition { Name = "TopProducts", Source = new TOM.CalculatedPartitionSource { Expression = "TOPN ( 10, Sales )" } });
            src.Tables.Add(ct);
            var live = NewModel();   // has no TopProducts

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.Added);
            Assert.Contains("add calcTable:TopProducts", rep.Changes);
            Assert.Contains("TopProducts", rep.CalcTablesAdded);   // the server-side caller Calculate-recalcs these
            Assert.DoesNotContain(rep.Unmatched, u => u.StartsWith("table:TopProducts"));
            var created = live.Tables.Find("TopProducts");
            Assert.NotNull(created);
            Assert.Equal("tag-top", created.LineageTag);           // rename-safe on later deploys
            Assert.Equal("top sellers", created.Description);
            Assert.Equal("TOPN ( 10, Sales )", ((TOM.CalculatedPartitionSource)created.Partitions["TopProducts"].Source).Expression);
        }

        [Fact]
        public void New_session_calc_table_is_reported_on_dry_run_and_mutates_nothing()
        {
            var src = NewModel();
            var ct = new TOM.Table { Name = "TopProducts" };
            ct.Partitions.Add(new TOM.Partition { Name = "TopProducts", Source = new TOM.CalculatedPartitionSource { Expression = "Sales" } });
            src.Tables.Add(ct);
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: false);

            Assert.Equal(1, rep.Added);
            Assert.Contains("add calcTable:TopProducts", rep.Changes);
            Assert.Null(live.Tables.Find("TopProducts"));   // dry-run created nothing
        }

        [Fact]
        public void New_session_import_table_is_reported_not_created()
        {
            // A data-bearing (M/import) new table needs a source binding we don't carry — reported, never created.
            var src = NewModel();
            var it = new TOM.Table { Name = "Imported" };
            it.Partitions.Add(new TOM.Partition { Name = "Imported", Source = new TOM.MPartitionSource { Expression = "let x = 1 in x" } });
            src.Tables.Add(it);
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:Imported"));
            Assert.Null(live.Tables.Find("Imported"));   // data table not deployable here
            Assert.Empty(rep.CalcTablesAdded);
        }

        [Fact]
        public void New_session_partition_is_reported_not_applied()
        {
            var src = NewModel();
            src.Tables["Sales"].Partitions.Add(new TOM.Partition { Name = "Sales 2024", Source = new TOM.MPartitionSource { Expression = "let x = 2024 in x" } });
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains(rep.Unmatched, u => u.StartsWith("partition:Sales/Sales 2024"));
            Assert.Single(live.Tables["Sales"].Partitions);   // nothing structural was added
        }

        [Fact]
        public void Live_only_partition_is_reported_and_left_untouched()
        {
            var src = NewModel();
            var live = NewModel();
            live.Tables["Sales"].Partitions.Add(new TOM.Partition { Name = "Sales Archive", Source = new TOM.MPartitionSource { Expression = "let x = 0 in x" } });
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains("partition:Sales/Sales Archive", rep.LiveOnly);
            Assert.Equal(2, live.Tables["Sales"].Partitions.Count);
        }

        [Fact]
        public void Partition_source_type_mismatch_is_reported_not_thrown()
        {
            var src = NewModel();                       // M source
            var live = NewModel();
            live.Tables["Sales"].Partitions["Sales"].Source = new TOM.CalculatedPartitionSource { Expression = "Sales" };
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(0, rep.Partitions);
            Assert.Contains(rep.Unmatched, u => u.StartsWith("source-type-mismatch partition:Sales/Sales"));
        }

        [Fact]
        public void Named_expression_update_add_and_live_only_are_all_handled()
        {
            var src = NewModel();
            src.Expressions.Add(new TOM.NamedExpression { Name = "Param1", Kind = TOM.ExpressionKind.M, Expression = "\"changed\"" });
            src.Expressions.Add(new TOM.NamedExpression { Name = "Param2", Kind = TOM.ExpressionKind.M, Expression = "\"new\"" });
            var live = NewModel();
            live.Expressions.Add(new TOM.NamedExpression { Name = "Param1", Kind = TOM.ExpressionKind.M, Expression = "\"original\"" });
            live.Expressions.Add(new TOM.NamedExpression { Name = "Param3", Kind = TOM.ExpressionKind.M, Expression = "\"live only\"" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.NamedExpressions);
            Assert.Equal(1, rep.Added);
            Assert.Equal("\"changed\"", live.Expressions["Param1"].Expression);   // updated
            Assert.Equal("\"new\"", live.Expressions["Param2"].Expression);       // added
            Assert.Contains("namedExpression:Param3", rep.LiveOnly);              // reported, untouched
            Assert.Contains("add namedExpression:Param2", rep.Changes);
        }

        [Fact]
        public void Refresh_policy_difference_is_reported_not_applied()
        {
            var src = NewModel();
            src.Tables["Sales"].RefreshPolicy = new TOM.BasicRefreshPolicy
            {
                IncrementalPeriods = 3,
                IncrementalGranularity = TOM.RefreshGranularityType.Month,
                RollingWindowPeriods = 5,
                RollingWindowGranularity = TOM.RefreshGranularityType.Year,
                SourceExpression = "let x = 1 in x"
            };
            var live = NewModel();
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            // TABLE-ref-keyed entry, so the selective push's reconciliation maps it onto the diff's table Update item.
            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:Sales (refresh policy"));
            Assert.Null(live.Tables["Sales"].RefreshPolicy);           // never applied here
            Assert.DoesNotContain("table:Sales", rep.SyncedRefs);      // the ref never claims an apply that dropped the policy
        }

        [Fact]
        public void Audit_annotations_ride_along_only_with_a_real_change()
        {
            // With a real change → the Semanticus_VerifiedEdits annotations travel into the live tree.
            var src = NewModel("let Source = 2 in Source");
            src.Annotations.Add(new TOM.Annotation { Name = "Semanticus_VerifiedEdits", Value = "chain" });
            var live = NewModel();
            LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal("chain", live.Annotations["Semanticus_VerifiedEdits"].Value);

            // With NO real change → audit state alone never triggers (or rides) a deploy.
            var src2 = NewModel();
            src2.Annotations.Add(new TOM.Annotation { Name = "Semanticus_VerifiedEdits", Value = "chain" });
            var live2 = NewModel();
            var rep2 = LiveDeploy.SyncModels(src2, live2, apply: true);
            Assert.Equal(0, rep2.TotalChanges);
            Assert.False(live2.Annotations.Contains("Semanticus_VerifiedEdits"));
        }

        [Fact]
        public void Table_detail_rows_reach_live_but_a_foreign_annotation_is_never_written()   // #135, revised for deploy MAJOR-4
        {
            var src = NewModel();
            // Foreign (non-owned, non-audit) annotations are NOT ours to deploy: a stale session value must never
            // overwrite the live one, and it is never counted as Metadata. Only the generic non-annotation supplement
            // (here, the detail-rows definition) deploys.
            src.Annotations.Add(new TOM.Annotation { Name = "Owner", Value = "Finance" });
            src.Tables["Sales"].Annotations.Add(new TOM.Annotation { Name = "ToolHint", Value = "keep" });
            src.Tables["Sales"].DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "SELECTCOLUMNS ( Sales, \"Amount\", Sales[Amount] )" };
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.Metadata);   // the table detail-rows shell only — the two foreign annotations do NOT count
            Assert.False(live.Annotations.Contains("Owner"));                 // foreign — never written
            Assert.False(live.Tables["Sales"].Annotations.Contains("ToolHint"));   // foreign — never written
            Assert.Equal(src.Tables["Sales"].DefaultDetailRowsDefinition.Expression, live.Tables["Sales"].DefaultDetailRowsDefinition.Expression);
            Assert.DoesNotContain("model", rep.SyncedRefs);   // no model-level change (foreign annotation isn't one)
            Assert.Contains("table:Sales", rep.SyncedRefs);
        }

        [Fact]
        public void A_foreign_annotation_present_live_is_left_untouched_on_deploy()   // deploy MAJOR-4
        {
            // Live carries another tool's state; the session carries a STALE value for the same annotation. The deploy
            // must not overwrite it (and must not count it) — foreign annotations are inert in both directions.
            var src = NewModel(); src.Annotations.Add(new TOM.Annotation { Name = "SomeOtherTool_State", Value = "stale" });
            var live = NewModel(); live.Annotations.Add(new TOM.Annotation { Name = "SomeOtherTool_State", Value = "live-truth" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(0, rep.Metadata);
            Assert.Equal(0, rep.Annotations);
            Assert.Equal("live-truth", live.Annotations["SomeOtherTool_State"].Value);   // not clobbered by the stale session value
        }

        [Fact]
        public void Calc_group_selection_expressions_reach_live()   // #135
        {
            var src = CalcModel(0, ("YTD", "SELECTEDMEASURE ()", 0, null));
            src.Database.CompatibilityLevel = 1605;
            src.Tables["TI"].CalculationGroup.NoSelectionExpression = new TOM.CalculationGroupExpression
            {
                Expression = "SELECTEDMEASURE ()",
                FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "\"0.0%\"" }
            };
            src.Tables["TI"].CalculationGroup.MultipleOrEmptySelectionExpression = new TOM.CalculationGroupExpression { Expression = "BLANK ()" };
            var live = CalcModel(0, ("YTD", "SELECTEDMEASURE ()", 0, null));
            live.Database.CompatibilityLevel = 1605;

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.Metadata);
            Assert.Equal("SELECTEDMEASURE ()", live.Tables["TI"].CalculationGroup.NoSelectionExpression.Expression);
            Assert.Equal("\"0.0%\"", live.Tables["TI"].CalculationGroup.NoSelectionExpression.FormatStringDefinition.Expression);
            Assert.Equal("BLANK ()", live.Tables["TI"].CalculationGroup.MultipleOrEmptySelectionExpression.Expression);
            Assert.Contains("table:TI", rep.SyncedRefs);
        }

        [Fact]
        public void Unsupported_table_shell_metadata_is_named_and_the_ref_is_withheld()   // #135
        {
            var src = NewModel(); src.Tables["Sales"].IsPrivate = true;
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Unmatched, x => x.StartsWith("table:Sales") && x.Contains("table-level metadata"));
            Assert.DoesNotContain("table:Sales", rep.SyncedRefs);
            Assert.False(live.Tables["Sales"].IsPrivate);
        }

        [Fact]
        public void Table_data_category_reaches_live_but_foreign_child_annotations_are_not_written()   // #135, revised for deploy MAJOR-4
        {
            var src = NewModel();
            src.Tables["Sales"].DataCategory = "Fact";
            // Owned child annotations (BPA ignore rules) DO deploy on their own channel; a foreign one never does.
            src.Tables["Sales"].Columns["Amount"].Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"R\"]}" });
            src.Tables["Sales"].Measures["Total"].Annotations.Add(new TOM.Annotation { Name = "MeasureHint", Value = "trusted" });
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.DataCategories);
            Assert.Equal(0, rep.Metadata);          // no generic supplement carries annotations any more
            Assert.Equal(1, rep.Annotations);       // the owned BPA-ignore annotation deployed on the owned channel
            Assert.Equal("Fact", live.Tables["Sales"].DataCategory);
            Assert.Equal("{\"RuleIDs\":[\"R\"]}", live.Tables["Sales"].Columns["Amount"].Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
            Assert.False(live.Tables["Sales"].Measures["Total"].Annotations.Contains("MeasureHint"));   // foreign — never written
            Assert.Contains("table:Sales", rep.SyncedRefs);           // DataCategory change
            Assert.Contains("column:Sales/Amount", rep.SyncedRefs);   // owned-annotation deploy claims the ref
            Assert.DoesNotContain("measure:Sales/Total", rep.SyncedRefs);   // nothing deployed on it
            Assert.Empty(rep.Unmatched);
        }

        [Fact]
        public void Unsupported_column_metadata_is_named_and_the_ref_is_withheld()   // #135
        {
            var src = NewModel(); src.Tables["Sales"].Columns["Amount"].IsDefaultImage = true;   // the key and label marks now go (LiveDeployStructureTests)
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Unmatched, x => x.StartsWith("column:Sales/Amount") && x.Contains("column metadata"));
            Assert.DoesNotContain("column:Sales/Amount", rep.SyncedRefs);
            Assert.False(live.Tables["Sales"].Columns["Amount"].IsDefaultImage);
        }

        [Fact]
        public void New_objects_carry_owned_annotations_but_not_foreign_ones()   // #135, revised for deploy MAJOR-4
        {
            var src = NewModel();
            var column = new TOM.CalculatedColumn { Name = "Tax", Expression = "Sales[Amount] * 0.1", LineageTag = "tag-tax" };
            column.Annotations.Add(new TOM.Annotation { Name = "ColumnHint", Value = "derived" });                 // foreign
            column.Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"C\"]}" });   // owned
            src.Tables["Sales"].Columns.Add(column);
            var measure = new TOM.Measure { Name = "Average", Expression = "AVERAGE ( Sales[Amount] )", LineageTag = "tag-average" };
            measure.Annotations.Add(new TOM.Annotation { Name = "MeasureHint", Value = "reviewed" });              // foreign
            src.Tables["Sales"].Measures.Add(measure);
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.False(live.Tables["Sales"].Columns["Tax"].Annotations.Contains("ColumnHint"));                  // foreign — never carried
            Assert.Equal("{\"RuleIDs\":[\"C\"]}", live.Tables["Sales"].Columns["Tax"].Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);   // owned — carried on the new object
            Assert.False(live.Tables["Sales"].Measures["Average"].Annotations.Contains("MeasureHint"));            // foreign — never carried
            Assert.Contains("column:Sales/Tax", rep.SyncedRefs);
            Assert.Contains("measure:Sales/Average", rep.SyncedRefs);
            Assert.Empty(rep.Unmatched);
        }

        [Fact]
        public void New_child_with_unsupported_metadata_is_not_partially_created_or_claimed()   // #135
        {
            var src = NewModel();
            src.Tables["Sales"].Columns.Add(new TOM.CalculatedColumn
            {
                Name = "KeyCopy", Expression = "Sales[Amount]", IsKey = true, LineageTag = "tag-key-copy"
            });
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Null(live.Tables["Sales"].Columns.Find("KeyCopy"));
            Assert.Contains(rep.Unmatched, x => x.StartsWith("column:Sales/KeyCopy") && x.Contains("cannot create"));
            Assert.DoesNotContain("column:Sales/KeyCopy", rep.SyncedRefs);
        }

        // ================================================================================================
        // #124 finding 2 — SyncModels walked columns/measures/partitions ONLY, so a calc-group table's ITEMS and its
        // Precedence never reached live: a calc-item create/update was merged locally but DROPPED by the push
        // (reconciled to Failed), and a RENAME (Create(new)+Delete(old)) deleted the old live item while nothing
        // created the new one — a push that deletes without creating (data loss). Now create/update/ordinal/
        // format-string/description + calc-group Precedence sync; live-only items are reported, never dropped.
        // ================================================================================================
        private static TOM.Model CalcModel(int precedence, params (string name, string expr, int ordinal, string fmt)[] items)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "TI", LineageTag = "tag-ti" };
            t.CalculationGroup = new TOM.CalculationGroup { Precedence = precedence };
            foreach (var (name, expr, ordinal, fmt) in items)
            {
                var ci = new TOM.CalculationItem { Name = name, Expression = expr, Ordinal = ordinal };
                if (!string.IsNullOrEmpty(fmt)) ci.FormatStringDefinition = new TOM.FormatStringDefinition { Expression = fmt };
                t.CalculationGroup.CalculationItems.Add(ci);
            }
            db.Model.Tables.Add(t);
            return db.Model;
        }
        private static TOM.CalculationItem Item(TOM.Model m, string name) => m.Tables["TI"].CalculationGroup.CalculationItems.Find(name);

        [Fact]
        public void CalcItem_expression_change_is_synced_on_commit()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )", 0, null));
            var live = CalcModel(0, ("YTD", "SELECTEDMEASURE ()", 0, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Expressions);
            Assert.Contains("calculationitem:TI/YTD", rep.SyncedRefs);   // reconciles against the diff's source-keyed ref
            Assert.Equal("CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )", Item(live, "YTD").Expression);
        }

        [Fact]
        public void CalcItem_new_is_added_on_commit()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "a", 0, null), ("QTD", "b", 1, null));
            var live = CalcModel(0, ("YTD", "a", 0, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Added);
            Assert.Contains("add calcItem:TI/QTD", rep.Changes);
            Assert.Contains("calculationitem:TI/QTD", rep.SyncedRefs);
            Assert.NotNull(Item(live, "QTD"));
        }

        [Fact]
        public void CalcItem_ordinal_change_is_synced()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "a", 5, null));
            var live = CalcModel(0, ("YTD", "a", 2, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.CalcGroup);
            Assert.Equal(5, Item(live, "YTD").Ordinal);
        }

        [Fact]
        public void CalcItem_format_string_change_is_synced()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "a", 0, "\"0.0%\""));
            var live = CalcModel(0, ("YTD", "a", 0, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Formats);
            Assert.Equal("\"0.0%\"", Item(live, "YTD").FormatStringDefinition.Expression);
        }

        [Fact]
        public void CalcItem_live_only_is_reported_and_left_untouched()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "a", 0, null));
            var live = CalcModel(0, ("YTD", "a", 0, null), ("Extra", "c", 1, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains("calculationitem:TI/Extra", rep.LiveOnly);
            Assert.NotNull(Item(live, "Extra"));   // absence never deletes in a whole-model deploy
        }

        [Fact]
        public void CalcGroup_precedence_is_synced_on_commit()   // finding 2
        {
            var src = CalcModel(10, ("YTD", "a", 0, null));
            var live = CalcModel(0, ("YTD", "a", 0, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.CalcGroup);
            Assert.Contains("table:TI", rep.SyncedRefs);
            Assert.Equal(10, live.Tables["TI"].CalculationGroup.Precedence);
        }

        [Fact]
        public void CalcItem_change_is_counted_on_dry_run_and_mutates_nothing()   // finding 2
        {
            var src = CalcModel(0, ("YTD", "b", 0, null));
            var live = CalcModel(0, ("YTD", "a", 0, null));
            var rep = LiveDeploy.SyncModels(src, live, apply: false);
            Assert.Equal(1, rep.TotalChanges);
            Assert.Equal("a", Item(live, "YTD").Expression);   // dry-run mutates nothing
        }

        // The headline: a calc-item RENAME through the FULL push (SyncAndApply). Diff emits Create(new)+Delete(old);
        // ModelCompare.Apply merges the Create into snapshot B (so the pushed model holds BOTH names), the explicit
        // channel deletes the old live item, and SyncModels must CREATE the new one. Pre-fix the create was dropped →
        // the push deleted OldName without creating NewName (data loss). Now the push is a true rename.
        [Fact]
        public void CalcItem_rename_is_not_destructive_through_the_full_push()   // finding 2 (SyncAndApply)
        {
            var pushed = CalcModel(0, ("OldName", "a", 0, null), ("NewName", "a", 0, null));   // = live B + the merged Create
            var live = CalcModel(0, ("OldName", "a", 0, null));
            var delOld = new LiveDeleteTarget { Kind = "calculationitem", Ref = "calculationitem:TI/OldName", TableTag = "tag-ti", Name = "OldName", Table = "TI" };

            var rep = LiveDeploy.SyncAndApply(pushed, live, commit: true, "ep", "db", new[] { delOld },
                saveChanges: () => { }, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.True(rep.Committed);
            Assert.NotNull(Item(live, "NewName"));   // CREATED (pre-fix: SyncModels never carried it → gone)
            Assert.Null(Item(live, "OldName"));      // removed via the explicit delete channel
        }

        // ================================================================================================
        // Delta review of the finding-1/2 fixes — the false-success family INSIDE the fix:
        //   (a) a combined table update (precedence/description + refresh policy) claimed the table ref in SyncedRefs
        //       while the policy part silently stayed behind → the reconciler confirmed it fully Applied;
        //   (b) calc-item ordinal assignment wasn't collision-safe against PRESERVED live-only items (ordinal
        //       uniqueness is validated by the SERVER only — a duplicate sails through TOM and kills the atomic
        //       SaveChanges), and an A↔B swap relied on TOM deferring validation through the transient duplicate;
        //   (c) the enumerated calc-item copy could drift from the whole-object diff on a future TOM bump and
        //       silently drop authored metadata while claiming the ref (residual guard; surface is complete TODAY —
        //       CalculationItem has no annotations/extended properties in AMO 19.114, verified by reflection).
        // ================================================================================================

        // (a) The combined-update false success: precedence syncs and used to CLAIM table:TI in SyncedRefs while the
        // policy stayed behind. The ref must be WITHHELD — the applied parts still count/log; the claim must not.
        [Fact]
        public void Combined_precedence_and_policy_update_withholds_the_table_ref()
        {
            var src = CalcModel(10, ("YTD", "a", 0, null));
            src.Tables["TI"].RefreshPolicy = new TOM.BasicRefreshPolicy { IncrementalPeriods = 3, IncrementalGranularity = TOM.RefreshGranularityType.Month };
            var live = CalcModel(0, ("YTD", "a", 0, null));   // precedence differs AND the policy is missing

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.CalcGroup);                                        // precedence still deploys…
            Assert.Equal(10, live.Tables["TI"].CalculationGroup.Precedence);
            Assert.Contains(rep.Changes, c => c.StartsWith("precedence table:TI"));
            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:TI (refresh policy"));
            Assert.DoesNotContain("table:TI", rep.SyncedRefs);                     // …but the ref does NOT claim a full apply
        }

        // (b) A source CREATE whose ordinal duplicates a PRESERVED live-only item is refused (named), never pushed
        // into a doomed SaveChanges. The save callback proves the committed state carries no >=0 duplicate ordinals.
        [Fact]
        public void CalcItem_create_colliding_with_a_live_only_ordinal_is_refused_not_duplicated()
        {
            var src = CalcModel(0, ("A", "1", 0, null), ("B", "2", 1, null));
            var live = CalcModel(0, ("A", "1", 0, null), ("Extra", "9", 1, null));   // Extra is live-only, holds ordinal 1

            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null,
                saveChanges: () => AssertNoDuplicateOrdinals(live), recalcCalcTables: _ => { }, identityStrict: true);

            Assert.Null(Item(live, "B"));                                                          // NOT created
            Assert.Contains(rep.Unmatched, u => u.StartsWith("calculationitem:TI/B (ordinal 1 would duplicate live-only calculation item 'Extra'"));
            Assert.DoesNotContain("calculationitem:TI/B", rep.SyncedRefs);                         // never claimed
            Assert.Equal(1, Item(live, "Extra").Ordinal);                                          // the live-only item is untouched
        }

        // (b) A matched item's ordinal CHANGE colliding with a live-only item is refused the same way; the item's other
        // props (none here) don't rescue the claim — the ref is withheld with a named reason.
        [Fact]
        public void CalcItem_ordinal_change_colliding_with_a_live_only_ordinal_is_refused_and_ref_withheld()
        {
            var src = CalcModel(0, ("A", "1", 1, null));
            var live = CalcModel(0, ("A", "1", 0, null), ("Extra", "9", 1, null));

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(0, Item(live, "A").Ordinal);                                              // ordinal NOT applied
            Assert.Contains(rep.Unmatched, u => u.StartsWith("calculationitem:TI/A (ordinal 1 would duplicate live-only calculation item 'Extra'"));
            Assert.DoesNotContain("calculationitem:TI/A", rep.SyncedRefs);
            Assert.Equal(0, rep.CalcGroup);                                                        // refused ≠ counted
        }

        // (b) An A↔B ordinal SWAP applies two-phase (park at disjoint temps, then finals) — the committed state is
        // exact and duplicate-free without relying on TOM deferring uniqueness validation through the transient state.
        [Fact]
        public void CalcItem_ordinal_swap_applies_two_phase_with_a_duplicate_free_final_state()
        {
            var src = CalcModel(0, ("A", "1", 1, null), ("B", "2", 0, null));   // swapped vs live
            var live = CalcModel(0, ("A", "1", 0, null), ("B", "2", 1, null));

            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null,
                saveChanges: () => AssertNoDuplicateOrdinals(live), recalcCalcTables: _ => { }, identityStrict: true);

            Assert.True(rep.Committed);
            Assert.Equal(2, rep.CalcGroup);
            Assert.Equal(1, Item(live, "A").Ordinal);
            Assert.Equal(0, Item(live, "B").Ordinal);
        }

        // (b) Ordinal -1 is EXEMPT from the collision guard: it is TOM's "no explicit order" default (omitted from
        // serialization) and any number of items legally share it — refusing it would block ordinary unordered models.
        [Fact]
        public void CalcItem_unset_ordinal_minus_one_is_not_a_collision()
        {
            var src = CalcModel(0, ("A", "1", -1, null), ("B", "2", -1, null));
            var live = CalcModel(0, ("A", "1", -1, null), ("Extra", "9", -1, null));   // live-only Extra also unset

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.NotNull(Item(live, "B"));                                           // created despite the shared -1
            Assert.DoesNotContain(rep.Unmatched, u => u.Contains("would duplicate"));
        }

        private static void AssertNoDuplicateOrdinals(TOM.Model live)
        {
            var ordinals = live.Tables["TI"].CalculationGroup.CalculationItems.Cast<TOM.CalculationItem>()
                .Select(c => c.Ordinal).Where(o => o >= 0).ToList();
            Assert.Equal(ordinals.Count, ordinals.Distinct().Count());
        }

        // (c) The full authored surface round-trips through the FULL push and the claim is truthful: everything the
        // whole-object diff saw (description / expression / ordinal / format string) lands live, and only then is the
        // ref claimed. (CalculationItem's complete authored surface in AMO 19.114 — verified by reflection.)
        [Fact]
        public void CalcItem_full_authored_surface_roundtrips_through_the_full_push()
        {
            var src = CalcModel(0, ("YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'D'[D] ) )", 3, "\"0.0%\""));
            Item(src, "YTD").Description = "year to date";
            var live = CalcModel(0, ("YTD", "SELECTEDMEASURE ()", 0, null));

            var rep = LiveDeploy.SyncAndApply(src, live, commit: true, "ep", "db", null,
                saveChanges: () => { }, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.True(rep.Committed);
            var lci = Item(live, "YTD");
            Assert.Equal("CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'D'[D] ) )", lci.Expression);
            Assert.Equal("year to date", lci.Description);
            Assert.Equal(3, lci.Ordinal);
            Assert.Equal("\"0.0%\"", lci.FormatStringDefinition.Expression);
            Assert.Contains("calculationitem:TI/YTD", rep.SyncedRefs);   // claimed only because ALL of it deployed
        }

        // (c) A CREATE carries the full surface too (it clones the source item, so any member a future AMO adds rides
        // along by construction — a hand-built constructor can never silently drop authored metadata again).
        [Fact]
        public void CalcItem_create_clones_the_full_authored_surface()
        {
            var src = CalcModel(0, ("A", "1", 0, null), ("New", "2", 5, "\"#,0\""));
            Item(src, "New").Description = "described";
            var live = CalcModel(0, ("A", "1", 0, null));

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            var nci = Item(live, "New");
            Assert.NotNull(nci);
            Assert.Equal("2", nci.Expression);
            Assert.Equal(5, nci.Ordinal);
            Assert.Equal("described", nci.Description);
            Assert.Equal("\"#,0\"", nci.FormatStringDefinition.Expression);
            Assert.Contains("calculationitem:TI/New", rep.SyncedRefs);
        }

        // (c) The residual drift detector itself, unit-tested at the JSON level with a SYNTHETIC future member (no such
        // member exists on CalculationItem in AMO 19.114 — this is the guard that fires when a TOM bump adds one).
        [Theory]
        [InlineData("{\"name\":\"A\",\"expression\":\"1\",\"ordinal\":2}", "{\"name\":\"A\",\"expression\":\"9\",\"ordinal\":7}", true)]                        // carried members only ⇒ no residual (the setters handle them)
        [InlineData("{\"name\":\"A\",\"expression\":\"1\",\"annotations\":[{\"name\":\"x\",\"value\":\"1\"}]}", "{\"name\":\"A\",\"expression\":\"1\"}", false)] // a synthetic FUTURE authored member ⇒ residual detected
        [InlineData("{\"name\":\"A\",\"expression\":\"1\"}", "{\"name\":\"A\",\"expression\":\"1\",\"modifiedTime\":\"2026-01-01T00:00:00Z\",\"state\":\"Ready\",\"errorMessage\":\"\"}", true)] // server-runtime members ⇒ never a false residual
        [InlineData("{\"name\":\"A\",\"expression\":\"1\",\"formatStringDefinition\":{\"expression\":\"\\\"0.0%\\\"\"}}", "{\"name\":\"A\",\"expression\":\"1\"}", true)]                        // a fully-carried format string ⇒ no residual
        public void CalcItem_residual_detector_flags_only_uncarried_authored_members(string a, string b, bool equal)
            => Assert.Equal(equal, LiveDeploy.CalcItemResidualEqual(a, b));

        // The non-Basic RefreshPolicy hardening (same subtype ⇒ UNEQUAL, in both ModelCompare and LiveDeploy) is
        // UNREACHABLE in AMO 19.114: RefreshPolicy is abstract with internal ctors and BasicRefreshPolicy is its only
        // subtype — no non-Basic instance can be constructed, in tests or anywhere else. This pin holds that PREMISE:
        // when a TOM bump ships a second subtype, it fails and forces the field-wise comparison to be written.
        [Fact]
        public void RefreshPolicy_subtype_catalog_is_pinned()
        {
            var subtypes = typeof(TOM.RefreshPolicy).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(TOM.RefreshPolicy))).Select(t => t.Name).OrderBy(n => n).ToArray();
            Assert.True(typeof(TOM.RefreshPolicy).IsAbstract);
            Assert.Equal(new[] { "BasicRefreshPolicy" }, subtypes);   // a second subtype ⇒ RefreshPolicyEqual needs real field-wise handling
        }
    }
}
