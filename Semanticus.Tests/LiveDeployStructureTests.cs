using System.Linq;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// LiveDeploy.SyncModels — the STRUCTURAL classes added 2026-07-09 after a live client model (ETS
    /// SM_Veg_Work_Required) scored A/100 offline but only B/80.6 in the service: the sort-by column, the
    /// Calendar hierarchy, the date-table mark and the finding waivers had never deployed, and there was no
    /// signal that they hadn't. deploy_live reported them nowhere — the same failure shape as the 2026-07-03
    /// partition-M bug. Pinned here: table DataCategory, SortByColumn, hierarchies, relationships, annotations,
    /// and new DATA tables/columns all deploy; a new data table is announced as EMPTY; nothing is ever deleted;
    /// dry-run mutates nothing. All offline — two in-memory TOM trees, no server.
    /// </summary>
    public sealed class LiveDeployStructureTests
    {
        /// <summary>A two-table star: Date (with a Month Name that sorts by Month Number, and a hierarchy) and
        /// Sales, joined Sales[DateKey] -> Date[Date].</summary>
        private static TOM.Model NewStar(bool markDateTable = false, bool withSortBy = false,
                                         bool withHierarchy = false, bool withRelationship = false)
        {
            // 1604+: TOM validates Mode=DirectLake against the database CL even in memory — a Direct Lake
            // model in the wild is always at or above this.
            var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };

            var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
            if (markDateTable) date.DataCategory = "Time";
            date.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            var dCol = new TOM.DataColumn { Name = "Date", SourceColumn = "Date", DataType = TOM.DataType.DateTime, LineageTag = "tag-d" };
            var mNum = new TOM.DataColumn { Name = "Month Number", SourceColumn = "MonthNo", DataType = TOM.DataType.Int64, LineageTag = "tag-mn" };
            var mName = new TOM.DataColumn { Name = "Month Name", SourceColumn = "MonthName", DataType = TOM.DataType.String, LineageTag = "tag-mnm" };
            date.Columns.Add(dCol); date.Columns.Add(mNum); date.Columns.Add(mName);
            if (withSortBy) mName.SortByColumn = mNum;
            if (withHierarchy)
            {
                var h = new TOM.Hierarchy { Name = "Calendar", LineageTag = "tag-hier" };
                h.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 0, Column = mName });
                date.Hierarchies.Add(h);
            }
            db.Model.Tables.Add(date);

            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            var key = new TOM.DataColumn { Name = "DateKey", SourceColumn = "DateKey", DataType = TOM.DataType.DateTime, LineageTag = "tag-key" };
            sales.Columns.Add(key);
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", SourceColumn = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-amt" });
            db.Model.Tables.Add(sales);

            if (withRelationship)
                db.Model.Relationships.Add(new TOM.SingleColumnRelationship
                {
                    Name = "rel-1",
                    FromColumn = key,
                    ToColumn = dCol,
                    FromCardinality = TOM.RelationshipEndCardinality.Many,
                    ToCardinality = TOM.RelationshipEndCardinality.One
                });
            return db.Model;
        }

        // ---- table DataCategory (the date-table mark) --------------------------------------------------

        [Fact]
        public void Date_table_mark_deploys_and_dry_run_leaves_live_unmarked()
        {
            var dry = LiveDeploy.SyncModels(NewStar(markDateTable: true), NewStar(), apply: false);
            Assert.Equal(1, dry.DataCategories);
            Assert.Contains("dataCategory table:Date", dry.Changes);

            var live = NewStar();
            LiveDeploy.SyncModels(NewStar(markDateTable: true), live, apply: true);
            Assert.Equal("Time", live.Tables["Date"].DataCategory);
        }

        // ---- SortByColumn ------------------------------------------------------------------------------

        [Fact]
        public void Sort_by_column_deploys_and_binds_to_the_LIVE_column_not_the_session_one()
        {
            var live = NewStar();
            var src = NewStar(withSortBy: true);
            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.SortBy);
            var liveMonthName = live.Tables["Date"].Columns["Month Name"];
            Assert.NotNull(liveMonthName.SortByColumn);
            Assert.Equal("Month Number", liveMonthName.SortByColumn.Name);
            // The binding must point INTO the live tree — a reference to the session's column would corrupt it.
            Assert.Same(live.Tables["Date"].Columns["Month Number"], liveMonthName.SortByColumn);
        }

        [Fact]
        public void Sort_by_column_dry_run_mutates_nothing()
        {
            var live = NewStar();
            var rep = LiveDeploy.SyncModels(NewStar(withSortBy: true), live, apply: false);
            Assert.Equal(1, rep.SortBy);
            Assert.Null(live.Tables["Date"].Columns["Month Name"].SortByColumn);
        }

        [Fact]
        public void Clearing_a_sort_by_column_deploys_as_none()
        {
            var live = NewStar(withSortBy: true);
            var rep = LiveDeploy.SyncModels(NewStar(), live, apply: true);
            Assert.Equal(1, rep.SortBy);
            Assert.Null(live.Tables["Date"].Columns["Month Name"].SortByColumn);
        }

        // ---- hierarchies -------------------------------------------------------------------------------

        [Fact]
        public void New_hierarchy_is_added_with_its_levels_bound_to_live_columns()
        {
            var live = NewStar();
            var rep = LiveDeploy.SyncModels(NewStar(withHierarchy: true), live, apply: true);

            Assert.Equal(1, rep.Hierarchies);
            var h = live.Tables["Date"].Hierarchies["Calendar"];
            Assert.Single(h.Levels);
            Assert.Same(live.Tables["Date"].Columns["Month Name"], h.Levels[0].Column);
        }

        [Fact]
        public void A_relevelled_hierarchy_is_rebuilt_in_order()
        {
            var live = NewStar(withHierarchy: true);
            var src = NewStar(withHierarchy: true);
            // session gains a second, HIGHER level: Month Number > Month Name
            var sh = src.Tables["Date"].Hierarchies["Calendar"];
            sh.Levels.Clear();
            sh.Levels.Add(new TOM.Level { Name = "Month Number", Ordinal = 0, Column = src.Tables["Date"].Columns["Month Number"] });
            sh.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 1, Column = src.Tables["Date"].Columns["Month Name"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Hierarchies);
            var lh = live.Tables["Date"].Hierarchies["Calendar"];
            Assert.Equal(2, lh.Levels.Count);
            Assert.Equal(new[] { "Month Number", "Month Name" }, lh.Levels.OrderBy(l => l.Ordinal).Select(l => l.Name).ToArray());
            Assert.Same(live.Tables["Date"].Columns["Month Number"], lh.Levels.Single(l => l.Ordinal == 0).Column);
        }

        [Fact]
        public void A_live_only_hierarchy_is_reported_and_never_deleted()
        {
            var live = NewStar(withHierarchy: true);
            var rep = LiveDeploy.SyncModels(NewStar(), live, apply: true);
            Assert.Contains("hierarchy:Date/Calendar", rep.LiveOnly);
            Assert.Equal(1, live.Tables["Date"].Hierarchies.Count);   // still there
        }

        // ---- relationships -----------------------------------------------------------------------------

        [Fact]
        public void New_relationship_is_added_with_endpoints_in_the_live_tree()
        {
            var live = NewStar();
            var rep = LiveDeploy.SyncModels(NewStar(withRelationship: true), live, apply: true);

            Assert.Equal(1, rep.Relationships);
            var r = Assert.Single(live.Relationships.OfType<TOM.SingleColumnRelationship>());
            Assert.Same(live.Tables["Sales"].Columns["DateKey"], r.FromColumn);
            Assert.Same(live.Tables["Date"].Columns["Date"], r.ToColumn);
        }

        [Fact]
        public void Crossfilter_and_active_drift_deploy_because_they_change_the_answers()
        {
            var live = NewStar(withRelationship: true);
            var src = NewStar(withRelationship: true);
            var sr = src.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
            sr.CrossFilteringBehavior = TOM.CrossFilteringBehavior.BothDirections;
            sr.IsActive = false;

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(2, rep.Relationships);
            var lr = live.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
            Assert.Equal(TOM.CrossFilteringBehavior.BothDirections, lr.CrossFilteringBehavior);
            Assert.False(lr.IsActive);
        }

        [Fact]
        public void A_relationship_is_matched_by_endpoints_not_by_its_guid_name()
        {
            var live = NewStar(withRelationship: true);
            var src = NewStar(withRelationship: true);
            src.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Name = "a-completely-different-guid";

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(0, rep.Relationships);          // same endpoints = the same relationship
            Assert.Empty(rep.LiveOnly.Where(x => x.StartsWith("relationship:")));
            Assert.Single(live.Relationships);           // NOT duplicated
        }

        [Fact]
        public void A_live_only_relationship_is_reported_and_never_deleted()
        {
            var live = NewStar(withRelationship: true);
            var rep = LiveDeploy.SyncModels(NewStar(), live, apply: true);
            Assert.Contains("relationship:Sales[DateKey] -> Date[Date]", rep.LiveOnly);
            Assert.Single(live.Relationships);
        }

        // ---- annotations (waivers) ---------------------------------------------------------------------

        [Fact]
        public void Model_waivers_deploy_and_are_counted()
        {
            var src = NewStar(); var live = NewStar();
            src.Annotations.Add(new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[{\"ruleId\":\"PERF_UNUSED_COLUMNS\"}]" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Annotations);
            Assert.Equal("[{\"ruleId\":\"PERF_UNUSED_COLUMNS\"}]", live.Annotations["Semanticus_Waivers"].Value);
        }

        [Fact]
        public void Object_level_bpa_ignore_rules_deploy()
        {
            var src = NewStar(); var live = NewStar();
            src.Tables["Sales"].Columns["Amount"].Annotations.Add(
                new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"X\"]}" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(1, rep.Annotations);
            Assert.Equal("{\"RuleIDs\":[\"X\"]}", live.Tables["Sales"].Columns["Amount"].Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
        }

        [Fact]
        public void An_annotation_we_do_not_own_is_never_touched()
        {
            var src = NewStar(); var live = NewStar();
            live.Annotations.Add(new TOM.Annotation { Name = "SomeOtherTool_State", Value = "keep-me" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Equal(0, rep.Annotations);
            Assert.Equal("keep-me", live.Annotations["SomeOtherTool_State"].Value);   // not deleted, not blanked
        }

        // ---- new DATA tables / columns -----------------------------------------------------------------

        private static TOM.Model WithDirectLakeTable(TOM.Model m, string exprName, bool addExpressionToo)
        {
            if (addExpressionToo)
                m.Expressions.Add(new TOM.NamedExpression { Name = exprName, Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });

            var t = new TOM.Table { Name = "dim_span", LineageTag = "tag-span" };
            t.Columns.Add(new TOM.DataColumn { Name = "ScopeId", SourceColumn = "ScopeId", DataType = TOM.DataType.String, LineageTag = "tag-scope" });
            t.Columns.Add(new TOM.DataColumn { Name = "RevegEndDate", SourceColumn = "RevegEndDate", DataType = TOM.DataType.DateTime, LineageTag = "tag-reveg" });
            t.Partitions.Add(new TOM.Partition
            {
                Name = "dim_span",
                // A real session's Direct Lake partition carries Mode=DirectLake; the service REJECTS an Entity
                // partition at the default Import mode, so the deploy must carry Mode across.
                Mode = TOM.ModeType.DirectLake,
                Source = new TOM.EntityPartitionSource
                {
                    EntityName = "dim_span",
                    SchemaName = "vegetation",
                    ExpressionSource = m.Expressions[exprName]
                }
            });
            m.Tables.Add(t);
            return m;
        }

        [Fact]
        public void New_direct_lake_table_is_created_with_its_columns_and_announced_as_empty()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains("dim_span", rep.DataTablesAdded);
            Assert.Contains(rep.Changes, c => c.StartsWith("add dataTable:dim_span") && c.Contains("EMPTY until refreshed"));
            var lt = live.Tables["dim_span"];
            Assert.Equal(2, lt.Columns.Count);
            Assert.Equal("RevegEndDate", ((TOM.DataColumn)lt.Columns["RevegEndDate"]).SourceColumn);
            Assert.Equal(TOM.DataType.DateTime, lt.Columns["RevegEndDate"].DataType);

            // Rebound to the LIVE shared expression — never the session's object.
            var eps = Assert.IsType<TOM.EntityPartitionSource>(lt.Partitions["dim_span"].Source);
            Assert.Equal("vegetation", eps.SchemaName);
            Assert.Same(live.Expressions["DirectLake - gold"], eps.ExpressionSource);
            // Mode must ride along: the service rejects an Entity partition at Import mode
            // ("Partitions of type Entity must be in DirectQuery mode or Direct Lake mode").
            Assert.Equal(TOM.ModeType.DirectLake, lt.Partitions["dim_span"].Mode);
        }

        [Fact]
        public void A_direct_lake_table_whose_shared_expression_is_missing_live_is_REFUSED_not_half_created()
        {
            var live = NewStar();   // no "DirectLake - gold" expression here
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            // Remove the expression from the SESSION too, so it isn't deployed ahead of the table.
            src.Expressions.Remove("DirectLake - gold");
            // ...but keep the table's binding pointing at a now-orphaned name.

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.DoesNotContain("dim_span", live.Tables.Cast<TOM.Table>().Select(t => t.Name));
            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:dim_span") && u.Contains("does not exist on the live model"));
        }

        [Fact]
        public void New_data_column_on_an_existing_table_is_added_with_its_source_binding()
        {
            var live = NewStar();
            var src = NewStar();
            src.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "Qty", SourceColumn = "Qty", DataType = TOM.DataType.Int64, LineageTag = "tag-qty" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.Contains("add dataColumn:Sales/Qty", rep.Changes);
            Assert.Equal("Qty", ((TOM.DataColumn)live.Tables["Sales"].Columns["Qty"]).SourceColumn);
        }

        [Fact]
        public void Dry_run_of_the_whole_structural_set_mutates_nothing()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(markDateTable: true, withSortBy: true, withHierarchy: true, withRelationship: true), "DirectLake - gold", addExpressionToo: true);
            src.Annotations.Add(new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[]" });

            var rep = LiveDeploy.SyncModels(src, live, apply: false);

            Assert.True(rep.TotalChanges > 0);
            Assert.Equal(2, live.Tables.Count);                       // dim_span NOT created
            Assert.Null(live.Tables["Date"].Columns["Month Name"].SortByColumn);
            Assert.Empty(live.Tables["Date"].Hierarchies);
            Assert.Empty(live.Relationships);
            Assert.True(string.IsNullOrEmpty(live.Tables["Date"].DataCategory));   // TOM reports "" for unset, not null
            Assert.False(live.Annotations.Contains("Semanticus_Waivers"));
        }

        [Fact]
        public void Identical_structural_models_still_report_zero_changes()
        {
            var a = NewStar(markDateTable: true, withSortBy: true, withHierarchy: true, withRelationship: true);
            var b = NewStar(markDateTable: true, withSortBy: true, withHierarchy: true, withRelationship: true);
            var rep = LiveDeploy.SyncModels(a, b, apply: false);
            Assert.Equal(0, rep.TotalChanges);
            Assert.Empty(rep.Unmatched);
            Assert.Empty(rep.LiveOnly);
        }

        // ---- BLOCKER 1: cross-bindings resolve by IDENTITY, never a same-named impostor -----------------

        [Fact]
        public void Sort_by_binds_to_the_lineage_matched_column_not_a_same_named_impostor()
        {
            // Live has the real sort key "Month Number" (tag-mn) AND an unrelated live-only column "SortKey"
            // (tag-impostor). The session RENAMES tag-mn to "SortKey" and points Month Name's sort-by at it. A
            // name-based bind would grab the impostor; identity binds to the real tag-mn column (still "Month Number"
            // live because the rename collides with the impostor and is skipped).
            var live = NewStar();
            live.Tables["Date"].Columns.Add(new TOM.DataColumn { Name = "SortKey", SourceColumn = "Imp", DataType = TOM.DataType.Int64, LineageTag = "tag-impostor" });

            var src = NewStar();
            var srcMonthNo = src.Tables["Date"].Columns["Month Number"];   // tag-mn
            srcMonthNo.Name = "SortKey";                                   // rename OldName->SortKey (same tag)
            src.Tables["Date"].Columns["Month Name"].SortByColumn = srcMonthNo;

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            var liveMonthName = live.Tables["Date"].Columns["Month Name"];
            Assert.NotNull(liveMonthName.SortByColumn);
            // Bound to the real tag-mn column (impostor "SortKey" is tag-impostor), never the impostor.
            Assert.Same(live.Tables["Date"].Columns["Month Number"], liveMonthName.SortByColumn);
            Assert.NotEqual("tag-impostor", liveMonthName.SortByColumn.LineageTag);
            // The rename onto the impostor's name is refused, honestly reported — not silently applied.
            Assert.Contains(rep.Conflicts, c => c.Contains("SortKey"));
        }

        [Fact]
        public void New_relationship_endpoint_binds_by_identity_not_a_same_named_impostor()
        {
            // Live Sales has the real key column "DateKey" (tag-key) AND a live-only impostor also literally named
            // "DateKey"? Not possible (unique names) — so the impostor angle for relationships is a renamed key: live
            // key is "DateKey" (tag-key); an unrelated live column "LegacyKey" (tag-legacy) exists. The session renames
            // tag-key to "LegacyKey" and builds a relationship from it. Identity must bind the endpoint to tag-key.
            var live = NewStar();
            live.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "LegacyKey", SourceColumn = "Legacy", DataType = TOM.DataType.DateTime, LineageTag = "tag-legacy" });

            var src = NewStar();
            var srcKey = src.Tables["Sales"].Columns["DateKey"];   // tag-key
            srcKey.Name = "LegacyKey";                             // collides with the live impostor on name
            src.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-x", FromColumn = srcKey, ToColumn = src.Tables["Date"].Columns["Date"],
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            var r = Assert.Single(live.Relationships.OfType<TOM.SingleColumnRelationship>());
            Assert.Same(live.Tables["Sales"].Columns["DateKey"], r.FromColumn);   // the real tag-key, not the tag-legacy impostor
            Assert.Equal("tag-key", r.FromColumn.LineageTag);
        }

        // ---- BLOCKER 2: re-level preserves live-only levels + matched-level metadata -------------------

        [Fact]
        public void Re_level_preserves_a_live_only_level_and_matched_level_metadata()
        {
            // Live hierarchy: Month Name (ord0, carries a description) then a live-only "Day" (ord1). The session
            // hierarchy carries only Month Number (ord0) + Month Name (ord1) — it never mentions "Day". The old
            // Clear()+rebuild deleted "Day" and dropped Month Name's description; the reconcile preserves both.
            var live = NewStar(withHierarchy: true);
            var lh = live.Tables["Date"].Hierarchies["Calendar"];
            lh.Levels[0].Description = "keep-me";                        // matched-level metadata that must survive
            // Day is the DEEPEST live-only level (ordinal 2) — the session's two levels take 0 and 1, so nothing collides
            // with it and it is preserved in place.
            lh.Levels.Add(new TOM.Level { Name = "Day", Ordinal = 2, Column = live.Tables["Date"].Columns["Date"], LineageTag = "tag-day" });

            var src = NewStar(withHierarchy: true);
            var sh = src.Tables["Date"].Hierarchies["Calendar"];
            sh.Levels.Clear();
            sh.Levels.Add(new TOM.Level { Name = "Month Number", Ordinal = 0, Column = src.Tables["Date"].Columns["Month Number"] });
            sh.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 1, Column = src.Tables["Date"].Columns["Month Name"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            var reLive = live.Tables["Date"].Hierarchies["Calendar"];
            Assert.Contains("Day", reLive.Levels.Cast<TOM.Level>().Select(l => l.Name));         // live-only level PRESERVED
            Assert.Equal("keep-me", reLive.Levels.Cast<TOM.Level>().First(l => l.Name == "Month Name").Description);   // metadata preserved
            Assert.Contains(rep.LiveOnly, x => x.Contains("Day") && x.Contains("preserved"));
        }

        [Fact]
        public void Re_level_refuses_when_a_session_ordinal_collides_with_a_preserved_live_only_level()
        {
            // Live: Month Name (ord0), live-only Day (ord1). Session wants Month Name (ord0) + a NEW Quarter (ord1) —
            // ordinal 1 already belongs to the preserved live-only Day, so we refuse rather than corrupt the drill path.
            var live = NewStar(withHierarchy: true);
            live.Tables["Date"].Hierarchies["Calendar"].Levels.Add(
                new TOM.Level { Name = "Day", Ordinal = 1, Column = live.Tables["Date"].Columns["Date"] });

            var src = NewStar(withHierarchy: true);
            var sh = src.Tables["Date"].Hierarchies["Calendar"];
            sh.Levels.Add(new TOM.Level { Name = "Quarter", Ordinal = 1, Column = src.Tables["Date"].Columns["Month Number"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Unmatched, x => x.StartsWith("hierarchy:Date/Calendar") && x.Contains("collides"));
            Assert.DoesNotContain("Quarter", live.Tables["Date"].Hierarchies["Calendar"].Levels.Cast<TOM.Level>().Select(l => l.Name));
            Assert.Contains("Day", live.Tables["Date"].Hierarchies["Calendar"].Levels.Cast<TOM.Level>().Select(l => l.Name));   // untouched
        }

        // ---- BLOCKER 3: additive supplement never nulls a live-only nested calc-group member -----------

        private static TOM.Model CalcGroupModel(string noSel, string multiSel)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1605, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "TI", LineageTag = "tag-ti" };
            t.CalculationGroup = new TOM.CalculationGroup { Precedence = 0 };
            t.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "YTD", Expression = "SELECTEDMEASURE ()", Ordinal = 0 });
            if (noSel != null) t.CalculationGroup.NoSelectionExpression = new TOM.CalculationGroupExpression { Expression = noSel };
            if (multiSel != null) t.CalculationGroup.MultipleOrEmptySelectionExpression = new TOM.CalculationGroupExpression { Expression = multiSel };
            db.Model.Tables.Add(t);
            return db.Model;
        }

        [Fact]
        public void A_live_only_calc_group_selection_expression_is_preserved_not_nulled()
        {
            // Live carries BOTH selection expressions; the session carries only NoSelection (its MultipleOrEmpty is
            // null/absent). The additive supplement must not null the live-only MultipleOrEmptySelectionExpression.
            var live = CalcGroupModel(noSel: "SELECTEDMEASURE ()", multiSel: "BLANK ()");
            var src = CalcGroupModel(noSel: "SELECTEDMEASURE ()", multiSel: null);

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(0, rep.Metadata);   // nothing the SOURCE introduces — a live-only member is not a change
            Assert.NotNull(live.Tables["TI"].CalculationGroup.MultipleOrEmptySelectionExpression);
            Assert.Equal("BLANK ()", live.Tables["TI"].CalculationGroup.MultipleOrEmptySelectionExpression.Expression);
        }

        [Fact]
        public void A_changed_member_deploys_while_a_live_only_sibling_member_is_preserved()
        {
            var live = CalcGroupModel(noSel: "SELECTEDMEASURE ()", multiSel: "BLANK ()");
            var src = CalcGroupModel(noSel: "SELECTEDMEASURE () * 2", multiSel: null);   // NoSelection changed, MultipleOrEmpty absent

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.Metadata);
            Assert.Equal("SELECTEDMEASURE () * 2", live.Tables["TI"].CalculationGroup.NoSelectionExpression.Expression);   // updated
            Assert.Equal("BLANK ()", live.Tables["TI"].CalculationGroup.MultipleOrEmptySelectionExpression.Expression);    // live-only sibling preserved
        }

        // ---- MAJOR 5/6/7: new-table children reported/carried; owned annotations; expression audit ------

        [Fact]
        public void A_new_direct_lake_table_reports_a_measure_it_cannot_carry_and_carries_its_owned_annotation()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            var dl = src.Tables["dim_span"];
            dl.Measures.Add(new TOM.Measure { Name = "Span Count", Expression = "COUNTROWS ( dim_span )", LineageTag = "tag-spanct" });
            dl.Annotations.Add(new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[]" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            // MAJOR 5: the uncarried measure is NAMED, never silently dropped.
            Assert.Contains(rep.Unmatched, u => u.StartsWith("measure:dim_span/Span Count") && u.Contains("newly created table"));
            Assert.Empty(live.Tables["dim_span"].Measures.Cast<TOM.Measure>());
            // MAJOR 6: the table's owned annotation deployed on the owned channel.
            Assert.True(rep.Annotations >= 1);
            Assert.Equal("[]", live.Tables["dim_span"].Annotations["Semanticus_Waivers"].Value);
        }

        [Fact]
        public void Owned_annotation_on_a_new_measure_counts_identically_under_dry_run_and_apply()
        {
            TOM.Model MakeSrc()
            {
                var s = NewStar();
                var m = new TOM.Measure { Name = "Flagged", Expression = "1", LineageTag = "tag-flag" };
                m.Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"Z\"]}" });
                s.Tables["Sales"].Measures.Add(m);
                return s;
            }
            var dry = LiveDeploy.SyncModels(MakeSrc(), NewStar(), apply: false);
            var live = NewStar();
            var applied = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            Assert.Equal(1, dry.Annotations);
            Assert.Equal(dry.Annotations, applied.Annotations);   // dry-run parity
            Assert.Equal("{\"RuleIDs\":[\"Z\"]}", live.Tables["Sales"].Measures["Flagged"].Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
        }

        [Fact]
        public void A_session_only_shared_expression_co_authored_for_a_new_dl_table_is_audited()
        {
            // The shared expression exists ONLY in the session; it is added early so the Entity partition binds to a real
            // live object. That early add must still be reported/claimed like the normal expression pass would (MAJOR 7).
            var live = NewStar();   // no "DirectLake - gold" expression
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Changes, c => c == "add namedExpression:DirectLake - gold");
            Assert.Contains("expression:DirectLake - gold", rep.SyncedRefs);
            Assert.NotNull(live.Expressions.Find("DirectLake - gold"));
        }

        [Fact]
        public void Whole_structural_set_dry_run_equals_a_serialized_no_op_on_live()
        {
            // A dry-run over the full structural set must not mutate live at all — prove it by serialized equality
            // (a byte-for-byte model shell) before and after.
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var before = TOM.JsonSerializer.SerializeObject(live);

            var src = WithDirectLakeTable(NewStar(markDateTable: true, withSortBy: true, withHierarchy: true, withRelationship: true), "DirectLake - gold", addExpressionToo: true);
            src.Annotations.Add(new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[]" });
            var rep = LiveDeploy.SyncModels(src, live, apply: false);

            Assert.True(rep.TotalChanges > 0);
            Assert.Equal(before, TOM.JsonSerializer.SerializeObject(live));   // dry-run mutated nothing
        }

        // ==== ROUND 2 ==================================================================================
        // Regression coverage for the round-2 findings: identity resolution at the last bypassed sites
        // (DL expression / hierarchy level / relationship), incomplete-new-table ref withholding + DL
        // residual guards, dry-run==commit report parity for structure referencing NEW objects, owned
        // annotations on structural creations, and the escaped level-ref grammar.

        // ---- BLOCKER 1a: a Direct Lake table binding to a same-named IMPOSTOR expression is REFUSED ------

        [Fact]
        public void Direct_lake_table_binding_to_a_same_named_impostor_expression_is_refused()
        {
            // Live has "DirectLake - gold" (tag-live-expr); the session's same-named expression carries a DIFFERENT
            // lineage tag (republished/retagged under us). Match refuses it — binding by name would grab the exact
            // impostor Match rejected, so the whole table is refused, never silently bound to the wrong source.
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S", LineageTag = "tag-live-expr" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            src.Expressions["DirectLake - gold"].LineageTag = "tag-session-expr";   // same name, DIFFERENT tag = impostor

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.DoesNotContain("dim_span", live.Tables.Cast<TOM.Table>().Select(t => t.Name));   // not half-created
            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:dim_span") && u.Contains("DIFFERENT lineage tag"));
            Assert.DoesNotContain("table:dim_span", rep.SyncedRefs);
        }

        // ---- BLOCKER 1b: an impostor-bound hierarchy level is reconciled onto the lineage-matched column -

        [Fact]
        public void Impostor_bound_hierarchy_level_is_reconciled_onto_the_lineage_matched_column()
        {
            // Live "Date": a hierarchy level bound to a same-named IMPOSTOR column ("Key" tag-imp); the REAL column
            // (tag-real, live-named "RealKey") exists too. The session level binds to the real column (tag-real, named
            // "Key"). The old NAME-only compare read "Key"=="Key" as EQUAL and never reconciled; identity sees the live
            // level is bound to tag-imp, not the resolved tag-real, and rebinds it.
            TOM.Model MakeLive()
            {
                var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };
                var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
                date.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
                var imp = new TOM.DataColumn { Name = "Key", SourceColumn = "K", DataType = TOM.DataType.Int64, LineageTag = "tag-imp" };
                var real = new TOM.DataColumn { Name = "RealKey", SourceColumn = "RK", DataType = TOM.DataType.Int64, LineageTag = "tag-real" };
                date.Columns.Add(imp); date.Columns.Add(real);
                var h = new TOM.Hierarchy { Name = "H", LineageTag = "tag-h" };
                h.Levels.Add(new TOM.Level { Name = "L", Ordinal = 0, Column = imp });   // bound to the IMPOSTOR
                date.Hierarchies.Add(h);
                db.Model.Tables.Add(date);
                return db.Model;
            }
            TOM.Model MakeSrc()
            {
                var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };
                var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
                date.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
                var real = new TOM.DataColumn { Name = "Key", SourceColumn = "RK", DataType = TOM.DataType.Int64, LineageTag = "tag-real" };
                date.Columns.Add(real);
                var h = new TOM.Hierarchy { Name = "H", LineageTag = "tag-h" };
                h.Levels.Add(new TOM.Level { Name = "L", Ordinal = 0, Column = real });
                date.Hierarchies.Add(h);
                db.Model.Tables.Add(date);
                return db.Model;
            }
            var live = MakeLive();
            var rep = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            var lvl = live.Tables["Date"].Hierarchies["H"].Levels.Cast<TOM.Level>().Single();
            Assert.Equal("tag-real", lvl.Column.LineageTag);   // rebound onto the real column, not the impostor
            Assert.Equal(1, rep.Hierarchies);
        }

        // ---- BLOCKER 1c: relationship drift lands on the identity-matched rel; a rename adds no duplicate

        [Fact]
        public void Relationship_drift_lands_on_the_identity_matched_rel_and_an_endpoint_rename_adds_no_duplicate()
        {
            // Live: Sales[DateKey](tag-key) -> Date[Date](tag-d). The session RENAMES the FROM endpoint (tag-key) to
            // "TxnDate" and flips crossfilter. The name-based signature would drift, so the old code treated it as a NEW
            // relationship and DUPLICATED it (often with a duplicate guid name). Identity projects the endpoints onto the
            // live tree, matches the same relationship, applies the drift in place, and adds nothing.
            var live = NewStar(withRelationship: true);
            var src = NewStar(withRelationship: true);
            src.Tables["Sales"].Columns["DateKey"].Name = "TxnDate";   // tag-key preserved, name changed
            src.Relationships.OfType<TOM.SingleColumnRelationship>().Single().CrossFilteringBehavior = TOM.CrossFilteringBehavior.BothDirections;

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Single(live.Relationships);   // NOT duplicated despite the endpoint rename
            var lr = live.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
            Assert.Equal(TOM.CrossFilteringBehavior.BothDirections, lr.CrossFilteringBehavior);   // drift landed on the matched rel
            Assert.Equal("tag-key", lr.FromColumn.LineageTag);
            Assert.Same(live.Tables["Sales"].Columns["TxnDate"], lr.FromColumn);   // endpoint followed the rename in place
        }

        // ---- BLOCKER 2: incomplete new table ref withheld + DL residual guards -------------------------

        [Fact]
        public void An_incomplete_new_table_withholds_its_ref_from_synced_refs()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            src.Tables["dim_span"].Measures.Add(new TOM.Measure { Name = "Span Count", Expression = "COUNTROWS ( dim_span )", LineageTag = "tag-spanct" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            // The shell IS created + counted, but the ref is WITHHELD: an authored child (the measure) couldn't be
            // carried, so a selective push cannot reconcile the table Create as fully applied.
            Assert.Contains("dim_span", rep.DataTablesAdded);
            Assert.Contains(rep.Unmatched, u => u.StartsWith("measure:dim_span/Span Count"));
            Assert.DoesNotContain("table:dim_span", rep.SyncedRefs);
        }

        [Fact]
        public void New_direct_lake_table_reports_an_uncarryable_column_key_and_is_not_half_created()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            src.Tables["dim_span"].Columns["ScopeId"].IsKey = true;   // a residual the DL clone can't carry

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.DoesNotContain("dim_span", live.Tables.Cast<TOM.Table>().Select(t => t.Name));   // not half-created
            Assert.Contains(rep.Unmatched, u => u.StartsWith("column:dim_span/ScopeId") && u.Contains("cannot create"));
            Assert.DoesNotContain("table:dim_span", rep.SyncedRefs);
        }

        [Fact]
        public void New_direct_lake_table_reports_uncarryable_table_metadata_and_is_not_half_created()
        {
            var live = NewStar();
            live.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
            var src = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
            src.Tables["dim_span"].IsPrivate = true;   // a table-level residual the DL clone can't carry

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.DoesNotContain("dim_span", live.Tables.Cast<TOM.Table>().Select(t => t.Name));
            Assert.Contains(rep.Unmatched, u => u.StartsWith("table:dim_span") && u.Contains("cannot create"));
            Assert.DoesNotContain("table:dim_span", rep.SyncedRefs);
        }

        // ---- MAJOR 3: dry-run == commit report parity for structure referencing NEW objects ------------

        [Fact]
        public void A_new_column_with_its_own_sort_by_reports_identically_dry_run_and_commit()
        {
            TOM.Model MakeSrc()
            {
                var s = NewStar();
                var c = new TOM.DataColumn { Name = "MonthAbbr", SourceColumn = "MAb", DataType = TOM.DataType.String, LineageTag = "tag-mab" };
                c.SortByColumn = s.Tables["Date"].Columns["Month Number"];   // a NEW column's OWN sort-by
                s.Tables["Date"].Columns.Add(c);
                return s;
            }
            var dry = LiveDeploy.SyncModels(MakeSrc(), NewStar(), apply: false);
            var live = NewStar();
            var commit = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            Assert.True(commit.SortBy >= 1);   // the new column's own sort-by deployed (was never processed before)
            Assert.Equal(dry.SortBy, commit.SortBy);
            Assert.Equal(dry.Added, commit.Added);
            Assert.Equal(dry.Unmatched, commit.Unmatched);
            Assert.Equal(dry.SyncedRefs.OrderBy(x => x, System.StringComparer.Ordinal), commit.SyncedRefs.OrderBy(x => x, System.StringComparer.Ordinal));
            Assert.Same(live.Tables["Date"].Columns["Month Number"], ((TOM.DataColumn)live.Tables["Date"].Columns["MonthAbbr"]).SortByColumn);
        }

        [Fact]
        public void A_relationship_to_a_new_table_reports_identically_dry_run_and_commit()
        {
            TOM.Model MakeSrc()
            {
                var s = WithDirectLakeTable(NewStar(), "DirectLake - gold", addExpressionToo: true);
                s.Relationships.Add(new TOM.SingleColumnRelationship
                {
                    Name = "rel-new",
                    FromColumn = s.Tables["Sales"].Columns["DateKey"],
                    ToColumn = s.Tables["dim_span"].Columns["ScopeId"],   // endpoint on the NEW table
                    FromCardinality = TOM.RelationshipEndCardinality.Many,
                    ToCardinality = TOM.RelationshipEndCardinality.One
                });
                return s;
            }
            TOM.Model MakeLive()
            {
                var m = NewStar();
                m.Expressions.Add(new TOM.NamedExpression { Name = "DirectLake - gold", Kind = TOM.ExpressionKind.M, Expression = "let S = 1 in S" });
                return m;
            }
            var dry = LiveDeploy.SyncModels(MakeSrc(), MakeLive(), apply: false);
            var live = MakeLive();
            var commit = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            Assert.True(commit.Relationships >= 1);   // dry-run no longer REFUSES what commit carries
            Assert.Equal(dry.Relationships, commit.Relationships);
            Assert.Equal(dry.Unmatched, commit.Unmatched);
            Assert.Equal(dry.SyncedRefs.OrderBy(x => x, System.StringComparer.Ordinal), commit.SyncedRefs.OrderBy(x => x, System.StringComparer.Ordinal));
            Assert.Contains(live.Relationships.OfType<TOM.SingleColumnRelationship>(), r => r.ToColumn?.Table?.Name == "dim_span");
        }

        // ---- MAJOR 4: owned annotations on new hierarchy / level / relationship ------------------------

        [Fact]
        public void A_new_hierarchy_and_its_level_carry_owned_annotations_dry_run_and_commit()
        {
            TOM.Model MakeSrc()
            {
                var s = NewStar();
                var h = new TOM.Hierarchy { Name = "Cal2", LineageTag = "tag-cal2" };
                h.Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"H\"]}" });
                var lvl = new TOM.Level { Name = "Mn", Ordinal = 0, Column = s.Tables["Date"].Columns["Month Name"] };
                lvl.Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"L\"]}" });
                h.Levels.Add(lvl);
                s.Tables["Date"].Hierarchies.Add(h);
                return s;
            }
            var dry = LiveDeploy.SyncModels(MakeSrc(), NewStar(), apply: false);
            var live = NewStar();
            var commit = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            Assert.Equal(2, commit.Annotations);          // hierarchy + level owned annotations
            Assert.Equal(dry.Annotations, commit.Annotations);   // dry-run parity
            var h = live.Tables["Date"].Hierarchies["Cal2"];
            Assert.Equal("{\"RuleIDs\":[\"H\"]}", h.Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
            Assert.Equal("{\"RuleIDs\":[\"L\"]}", h.Levels.Find("Mn").Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
        }

        [Fact]
        public void A_new_relationship_carries_its_owned_annotation_dry_run_and_commit()
        {
            TOM.Model MakeSrc()
            {
                var s = NewStar(withRelationship: true);
                s.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Annotations.Add(
                    new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"R\"]}" });
                return s;
            }
            var dry = LiveDeploy.SyncModels(MakeSrc(), NewStar(), apply: false);
            var live = NewStar();
            var commit = LiveDeploy.SyncModels(MakeSrc(), live, apply: true);

            Assert.True(commit.Annotations >= 1);
            Assert.Equal(dry.Annotations, commit.Annotations);   // dry-run parity
            var lr = live.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
            Assert.Equal("{\"RuleIDs\":[\"R\"]}", lr.Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
        }

        // ---- MINOR 5: the preserved live-only level ref uses the escaped level grammar -----------------

        [Fact]
        public void A_preserved_live_only_level_ref_uses_the_escaped_level_grammar()
        {
            var live = NewStar(withHierarchy: true);
            live.Tables["Date"].Hierarchies["Calendar"].Levels.Add(
                new TOM.Level { Name = "Day", Ordinal = 2, Column = live.Tables["Date"].Columns["Date"], LineageTag = "tag-day" });
            var src = NewStar(withHierarchy: true);
            var sh = src.Tables["Date"].Hierarchies["Calendar"];
            sh.Levels.Clear();
            sh.Levels.Add(new TOM.Level { Name = "Month Number", Ordinal = 0, Column = src.Tables["Date"].Columns["Month Number"] });
            sh.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 1, Column = src.Tables["Date"].Columns["Month Name"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains("level:Date/Calendar/Day (live-only, preserved)", rep.LiveOnly);
        }

        // ==== ROUND 3 ==================================================================================
        // BLOCKER 1: an endpoint-rename delete must not remove a relationship the same deploy identity-matched/updated.
        // MAJOR 2: a level-only owned-annotation change claims the PARENT hierarchy ref (what reconciliation understands).
        // MAJOR 3: an owned-annotation-only change on a MATCHED relationship deploys via the selective channel.
        // MINOR 4: the preserved live-only level ref actually escapes '/' '\' (and passes '%' through) + follows a rename.

        // ---- BLOCKER 1: endpoint-rename collision must not DELETE the just-updated relationship ---------

        [Fact]
        public void An_endpoint_rename_delete_is_refused_when_it_lands_on_the_just_updated_relationship()
        {
            // The full third-state sequence, driven through the ModelCompare delete channel (SyncAndApply), NOT a direct
            // SyncModels call. Staged: rename Sales[DateKey]->Sales[TxnDate] + flip crossfilter. ModelCompare (session vs
            // the diff-time snapshot) emits Create(new sig) + Delete(OLD sig). Between snapshot and load a live-only
            // impostor column named "TxnDate" appears: the column rename collides and is SKIPPED, so ProjectRelSig
            // re-projects the renamed relationship onto its OLD signature and UPDATES the existing relationship in place —
            // the very relationship the Delete(old sig) targets. The explicit-delete channel must REFUSE (honest conflict),
            // never commit the deletion.
            var session = NewStar(withRelationship: true);
            session.Tables["Sales"].Columns["DateKey"].Name = "TxnDate";                 // rename (tag-key preserved)
            session.Relationships.OfType<TOM.SingleColumnRelationship>().Single().CrossFilteringBehavior = TOM.CrossFilteringBehavior.BothDirections;

            var snapshotB = NewStar(withRelationship: true);                             // the live state at diff time (old names)

            // The delete target is exactly what the production path builds: ModelCompare's Delete(old sig) -> FromDiffItem.
            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            var delTarget = LiveDeleteTarget.FromDiffItem(delItem);
            Assert.Equal("relationship", delTarget.Kind);
            Assert.Equal("Sales[DateKey]->Date[Date]", delTarget.Name);                  // the OLD name-based endpoint signature

            // The THIRD live state loaded at deploy time: old names + a live-only impostor column literally named "TxnDate".
            var third = NewStar(withRelationship: true);
            third.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "TxnDate", SourceColumn = "Imp", DataType = TOM.DataType.DateTime, LineageTag = "tag-impostor" });

            var committed = false;
            var rep = LiveDeploy.SyncAndApply(session, third, commit: true, "ep", "db", new[] { delTarget },
                saveChanges: () => committed = true, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.False(rep.Committed);                                                 // aborted — nothing written
            Assert.False(committed);                                                     // SaveChanges never ran
            Assert.Contains("relationship:Sales[DateKey]->Date[Date]", rep.DeletesRefusedConflict);
            Assert.Contains("just updated the same object", rep.Error);
            Assert.Single(third.Relationships);                                          // the relationship was NOT deleted
        }

        // ---- ROUND 4 · BLOCKER 1: a FAILED replacement Create must abort its paired Delete -------------

        /// <summary>A two-table star where the single relationship's FROM endpoint is either "DateKey" or "NewKey" (with a
        /// caller-chosen relationship name + active flag). NewKey carries IsKey=true — a residual the live push CANNOT
        /// create (CloneDeployColumn drops it), so a vanished NewKey can never be re-added by SyncModels, which is what
        /// makes the re-pointed relationship unresolvable at deploy time.</summary>
        private static TOM.Model StarRepointable(string relFromColumn, string relName, bool relActive = true)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };
            var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
            date.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
            var dCol = new TOM.DataColumn { Name = "Date", SourceColumn = "Date", DataType = TOM.DataType.DateTime, LineageTag = "tag-d" };
            date.Columns.Add(dCol);
            db.Model.Tables.Add(date);

            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
            var dateKey = new TOM.DataColumn { Name = "DateKey", SourceColumn = "DateKey", DataType = TOM.DataType.DateTime, LineageTag = "tag-key" };
            var newKey = new TOM.DataColumn { Name = "NewKey", SourceColumn = "NewKey", DataType = TOM.DataType.DateTime, LineageTag = "tag-newkey", IsKey = true };
            sales.Columns.Add(dateKey); sales.Columns.Add(newKey);
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", SourceColumn = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-amt" });
            db.Model.Tables.Add(sales);

            db.Model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = relName, IsActive = relActive,
                FromColumn = sales.Columns[relFromColumn], ToColumn = dCol,
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });
            return db.Model;
        }

        [Fact]
        public void A_failed_replacement_relationship_create_aborts_its_paired_delete_instead_of_dropping_the_relationship()
        {
            // The exact failed-new + untouched-old race, driven through ModelCompare.Diff -> staging -> SyncAndApply.
            // The session re-points the relationship from Sales[DateKey] to Sales[NewKey]. ModelCompare emits
            // Create(new sig) + Delete(old sig) and links them by endpoint lineage (the To endpoint Date[Date] is kept, the
            // From column moved). Snapshot B still has NewKey, so staging merges the Create into B (the pushed model then
            // holds BOTH relationships; the new one is inactive so both coexist). Then NewKey VANISHES before the XMLA
            // load: SyncModels cannot re-create it (IsKey residual) so the re-pointed relationship goes Unmatched, while the
            // old relationship matches live unchanged. Without the coupling its explicit Delete would commit a deletion
            // with NO replacement (data loss); the coupling refuses it and aborts the whole push.
            var session = StarRepointable("NewKey", "rel-new", relActive: false);
            var snapshotB = StarRepointable("DateKey", "rel-old");   // live at diff time: rel on DateKey, NewKey column present

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var createItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Create");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            Assert.Contains(createItem.Ref, delItem.ReplacementCreateRefs);   // ModelCompare linked the pair (one-to-one)
            Assert.Single(delItem.ReplacementCreateRefs);                     // unambiguous: exactly one required replacement

            // STAGING: merge ONLY the Create into snapshot B in place (exactly what LocalEngine does before a push).
            var staged = ModelCompare.Apply(session, snapshotB, diff, new System.Collections.Generic.HashSet<string> { createItem.Ref });
            Assert.Contains(createItem.Ref, staged.Applied);   // the new relationship staged (NewKey resolves in B)
            Assert.Equal(2, snapshotB.Relationships.Count);    // pushed model = old + new relationship

            var delTarget = LiveDeleteTarget.FromDiffItem(delItem);
            Assert.Contains(createItem.Ref, delTarget.ReplacementNewRefs);   // the linkage rides onto the delete target

            var third = StarRepointable("DateKey", "rel-old");
            third.Tables["Sales"].Columns.Remove("NewKey");   // NewKey vanished between the diff and the load

            var committed = false;
            var rep = LiveDeploy.SyncAndApply(snapshotB, third, commit: true, "ep", "db", new[] { delTarget },
                saveChanges: () => committed = true, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.False(rep.Committed);                                             // aborted — nothing written
            Assert.False(committed);                                                 // SaveChanges never ran
            Assert.Contains("relationship:Sales[DateKey]->Date[Date]", rep.DeletesRefusedConflict);
            Assert.Contains("replacement", rep.Error);
            Assert.Single(third.Relationships);                                      // the old relationship was NOT deleted
            var survivor = third.Relationships.Cast<TOM.SingleColumnRelationship>().Single();
            Assert.Equal("DateKey", survivor.FromColumn.Name);                       // and it is still the ORIGINAL relationship
            Assert.Equal("Date", survivor.ToColumn.Name);
        }

        [Fact]
        public void A_normal_relationship_delete_still_proceeds_when_the_deploy_did_not_touch_it()
        {
            // The guard is precise: a genuinely-removed relationship (matched but UNTOUCHED — no rename, no drift) still
            // deletes through the explicit channel. Proves the BLOCKER-1 fix doesn't over-refuse normal deletes.
            var pushed = NewStar(withRelationship: true);   // B' still carries the relationship (deletes are never merged in)
            var live = NewStar(withRelationship: true);
            var delTarget = new LiveDeleteTarget { Kind = "relationship", Ref = "relationship:Sales[DateKey]->Date[Date]", Name = "Sales[DateKey]->Date[Date]" };

            var rep = LiveDeploy.SyncAndApply(pushed, live, commit: true, "ep", "db", new[] { delTarget },
                saveChanges: () => { }, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.True(rep.Committed);
            Assert.Empty(rep.DeletesRefusedConflict);
            Assert.Contains("relationship:Sales[DateKey]->Date[Date]", rep.DeletedRefs);
            Assert.Empty(live.Relationships);   // removed
        }

        // ---- ROUND 5 · BLOCKER 1(a): AMBIGUOUS (many-to-one) coupling must be UNIQUE + fail-closed ------

        /// <summary>A Sales+Date star for the role-playing multi-repoint scenario. Sales carries four candidate FROM
        /// columns; each relationship in <paramref name="rels"/> joins Sales[fromCol] -> Date[Date] (all INACTIVE so
        /// several can coexist). DeliveryKey carries IsKey=true — a residual the live push CANNOT re-create (held back),
        /// so a vanished DeliveryKey makes its relationship endpoint unresolvable at deploy time.</summary>
        private static TOM.Model RolePlayStar(params (string fromCol, string relName)[] rels)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };
            var date = new TOM.Table { Name = "Date", LineageTag = "tag-date" };
            date.Partitions.Add(new TOM.Partition { Name = "Date", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
            var dCol = new TOM.DataColumn { Name = "Date", SourceColumn = "Date", DataType = TOM.DataType.DateTime, LineageTag = "tag-d" };
            date.Columns.Add(dCol);
            db.Model.Tables.Add(date);

            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
            void AddCol(string n, string tag, bool isKey = false) =>
                sales.Columns.Add(new TOM.DataColumn { Name = n, SourceColumn = n, DataType = TOM.DataType.DateTime, LineageTag = tag, IsKey = isKey });
            AddCol("OrderKey", "tag-order");
            AddCol("ShipKey", "tag-ship");
            AddCol("InvoiceKey", "tag-invoice");
            AddCol("DeliveryKey", "tag-delivery", isKey: true);   // uncreatable live -> its relationship can go unresolvable
            db.Model.Tables.Add(sales);

            foreach (var (fromCol, relName) in rels)
                db.Model.Relationships.Add(new TOM.SingleColumnRelationship
                {
                    Name = relName, IsActive = false,
                    FromColumn = sales.Columns[fromCol], ToColumn = dCol,
                    FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
                });
            return db.Model;
        }

        [Fact]
        public void An_ambiguous_role_playing_repoint_couples_each_delete_to_the_WHOLE_candidate_group()
        {
            // Target has two role-playing relationships to Date[Date] (via OrderKey + ShipKey); the source replaces BOTH
            // with InvoiceKey + DeliveryKey. Under the 3-of-4 endpoint predicate EVERY old matches EVERY new, so the
            // coupling is AMBIGUOUS. A first-match link (round 4) would tie both Deletes to the SAME first Create; the
            // fix puts all four in ONE connected component so each Delete requires BOTH Creates — the group is safe only
            // if every replacement lands.
            var session = RolePlayStar(("InvoiceKey", "rel-inv"), ("DeliveryKey", "rel-del"));
            var snapshotB = RolePlayStar(("OrderKey", "rel-ord"), ("ShipKey", "rel-shp"));

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var creates = diff.Items.Where(i => i.ObjectType == "Relationship" && i.Action == "Create").ToList();
            var deletes = diff.Items.Where(i => i.ObjectType == "Relationship" && i.Action == "Delete").ToList();
            Assert.Equal(2, creates.Count);
            Assert.Equal(2, deletes.Count);
            var createRefs = creates.Select(c => c.Ref).ToArray();
            foreach (var d in deletes)
            {
                Assert.NotNull(d.ReplacementCreateRefs);
                Assert.Equal(2, d.ReplacementCreateRefs.Length);          // the WHOLE group, not a first-match single
                foreach (var cr in createRefs) Assert.Contains(cr, d.ReplacementCreateRefs);
            }
        }

        [Fact]
        public void An_ambiguous_role_playing_repoint_fails_closed_when_one_replacement_is_missing_live()
        {
            // The end-to-end: both replacements staged, but at deploy time DeliveryKey has vanished (IsKey -> uncreatable)
            // so rel-del never lands live. Because the group is ambiguous, BOTH deletes are refused and NEITHER old
            // relationship is dropped — no relationship is ever deleted without its own replacement live. (Under the round-4
            // first-match link, if InvoiceKey synced both Deletes would have passed and ShipKey's real replacement — the
            // missing DeliveryKey — would have been lost.)
            var session = RolePlayStar(("InvoiceKey", "rel-inv"), ("DeliveryKey", "rel-del"));
            var snapshotB = RolePlayStar(("OrderKey", "rel-ord"), ("ShipKey", "rel-shp"));

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var creates = diff.Items.Where(i => i.ObjectType == "Relationship" && i.Action == "Create").ToList();
            var deletes = diff.Items.Where(i => i.ObjectType == "Relationship" && i.Action == "Delete").ToList();
            var createRefs = creates.Select(c => c.Ref).ToArray();

            var staged = ModelCompare.Apply(session, snapshotB, diff,
                new System.Collections.Generic.HashSet<string>(createRefs, System.StringComparer.Ordinal));
            Assert.Equal(2, staged.Applied.Count);
            Assert.Equal(4, snapshotB.Relationships.Count);   // pushed model = 2 old + 2 new relationships

            var delTargets = deletes.Select(LiveDeleteTarget.FromDiffItem).ToArray();

            var third = RolePlayStar(("OrderKey", "rel-ord"), ("ShipKey", "rel-shp"));
            third.Tables["Sales"].Columns.Remove("DeliveryKey");   // one replacement's endpoint is gone (and can't be re-created)

            var committed = false;
            var rep = LiveDeploy.SyncAndApply(snapshotB, third, commit: true, "ep", "db", delTargets,
                saveChanges: () => committed = true, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.False(rep.Committed);
            Assert.False(committed);
            Assert.Contains("relationship:Sales[OrderKey]->Date[Date]", rep.DeletesRefusedConflict);
            Assert.Contains("relationship:Sales[ShipKey]->Date[Date]", rep.DeletesRefusedConflict);   // the WHOLE group refused
            Assert.Contains(third.Relationships.Cast<TOM.SingleColumnRelationship>(), r => r.FromColumn.Name == "OrderKey");
            Assert.Contains(third.Relationships.Cast<TOM.SingleColumnRelationship>(), r => r.FromColumn.Name == "ShipKey");
        }

        // ---- ROUND 5 · BLOCKER 1(b): couple a re-point whose KEPT endpoint was ALSO renamed, by lineage tag ----

        [Fact]
        public void A_simultaneous_endpoint_rename_couples_the_repoint_by_lineage_tag_not_name()
        {
            // A re-point where the KEPT endpoint (Date[Date]) is renamed (to Date[Cal]) WHILE the FROM column moves
            // (DateKey -> NewKey). Neither endpoint shares a NAME across the two sides, so a name-only predicate would
            // leave the genuine pair UNLINKED (weakening the guard). By lineage tag the kept endpoint reads as intact, so
            // ModelCompare still couples the Delete to its replacement Create.
            var snapshotB = StarRepointable("DateKey", "rel-old");                 // target: Sales[DateKey] -> Date[Date]
            var session = StarRepointable("NewKey", "rel-new", relActive: false);  // source: Sales[NewKey] -> Date[Date]
            session.Tables["Date"].Columns["Date"].Name = "Cal";                   // KEPT endpoint renamed (tag-d preserved)

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var createItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Create");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");

            // The endpoint signatures share NO name part (DateKey/NewKey and Date/Cal both differ) — only the tag compare pairs them.
            Assert.Equal("relationship:Sales[NewKey]->Date[Cal]", createItem.Ref);
            Assert.Equal("relationship:Sales[DateKey]->Date[Date]", delItem.Ref);
            Assert.NotNull(delItem.ReplacementCreateRefs);
            Assert.Contains(createItem.Ref, delItem.ReplacementCreateRefs);
        }

        // ---- ROUND 5 · MINOR 2: an already-present (matched, no write) replacement satisfies the paired delete ----

        [Fact]
        public void A_converged_push_lets_the_paired_delete_proceed_when_the_replacement_is_already_live()
        {
            // The replacement relationship was introduced INDEPENDENTLY in the live model (identical to what we push), so
            // SyncModels MATCHES it and writes nothing — it never enters SyncedRefs. Round 4 then falsely refused the paired
            // Delete (its replacement "did not sync"). MINOR 2: a matched-already-present replacement satisfies the coupling
            // (tracked in MatchedRefs), so the Delete proceeds and only the replacement remains.
            var session = StarRepointable("NewKey", "rel-new", relActive: false);
            var snapshotB = StarRepointable("DateKey", "rel-old");

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var createItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Create");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            Assert.Contains(createItem.Ref, delItem.ReplacementCreateRefs);

            var staged = ModelCompare.Apply(session, snapshotB, diff,
                new System.Collections.Generic.HashSet<string> { createItem.Ref });
            Assert.Contains(createItem.Ref, staged.Applied);   // pushed model = old + new relationship

            var delTarget = LiveDeleteTarget.FromDiffItem(delItem);

            // Third live state: the old relationship + the replacement ALREADY present live (converged), NewKey column present.
            var third = StarRepointable("DateKey", "rel-old");
            third.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-new-live", IsActive = false,
                FromColumn = third.Tables["Sales"].Columns["NewKey"], ToColumn = third.Tables["Date"].Columns["Date"],
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One
            });

            var committed = false;
            var rep = LiveDeploy.SyncAndApply(snapshotB, third, commit: true, "ep", "db", new[] { delTarget },
                saveChanges: () => committed = true, recalcCalcTables: _ => { }, identityStrict: true);

            Assert.True(rep.Committed);
            Assert.True(committed);
            Assert.Empty(rep.DeletesRefusedConflict);
            Assert.Contains("relationship:Sales[DateKey]->Date[Date]", rep.DeletedRefs);
            var survivor = third.Relationships.Cast<TOM.SingleColumnRelationship>().Single();
            Assert.Equal("NewKey", survivor.FromColumn.Name);   // only the replacement remains
        }

        // ---- MAJOR 2: a level-only owned-annotation change claims the parent hierarchy ref -------------

        [Fact]
        public void A_level_only_owned_annotation_change_claims_the_parent_hierarchy_ref()
        {
            // The hierarchy is structurally EQUAL (same single level, same bound column) so the level fast-path exits
            // without claiming the hierarchy ref; only a level BPA-ignore annotation changed. Reconciliation understands
            // ONLY hierarchy:<t>/<h> (ModelCompare never emits level:<t>/<h>/<l>), so the parent ref MUST be claimed.
            var live = NewStar(withHierarchy: true);
            var src = NewStar(withHierarchy: true);
            src.Tables["Date"].Hierarchies["Calendar"].Levels.Cast<TOM.Level>().Single(l => l.Name == "Month Name")
                .Annotations.Add(new TOM.Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = "{\"RuleIDs\":[\"L\"]}" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.True(rep.Annotations >= 1);                                           // the level annotation deployed
            Assert.Contains("hierarchy:Date/Calendar", rep.SyncedRefs);                  // the PARENT ref is claimed (was only level: before)
            Assert.Equal("{\"RuleIDs\":[\"L\"]}",
                live.Tables["Date"].Hierarchies["Calendar"].Levels.Find("Month Name").Annotations["BestPracticeAnalyzer_IgnoreRules"].Value);
        }

        // ---- MAJOR 3: an owned-annotation-only change on a MATCHED relationship deploys ----------------

        [Fact]
        public void An_owned_annotation_only_change_on_a_matched_relationship_is_an_update_not_equal()
        {
            // ModelCompare (the staging filter) must see the owned-annotation-only change as an Update — else the item is
            // Equal, filtered before staging, and a selective apply_model_diff silently no-ops it.
            var left = NewStar(withRelationship: true);
            left.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Annotations.Add(
                new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[{\"ruleId\":\"X\"}]" });
            var right = NewStar(withRelationship: true);   // identical endpoints/behaviour, no owned annotation

            var diff = ModelCompare.Diff(left, right, "left", "right");
            var relItem = Assert.Single(diff.Items.Where(i => i.ObjectType == "Relationship"));
            Assert.Equal("Update", relItem.Action);   // was "Equal" (filtered out) before the fix

            // A FOREIGN-annotation-only change must stay Equal — the relationship channel never carries it, so it must not
            // manufacture a phantom Update the deploy can't apply.
            var lf = NewStar(withRelationship: true);
            lf.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Annotations.Add(
                new TOM.Annotation { Name = "SomeOtherTool_State", Value = "x" });
            var rf = NewStar(withRelationship: true);
            Assert.Empty(ModelCompare.Diff(lf, rf, "l", "r").Items.Where(i => i.ObjectType == "Relationship"));
        }

        [Fact]
        public void An_owned_annotation_only_change_on_a_matched_relationship_deploys_via_the_selective_channel()
        {
            var live = NewStar(withRelationship: true);
            var src = NewStar(withRelationship: true);
            src.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Annotations.Add(
                new TOM.Annotation { Name = "Semanticus_Waivers", Value = "[{\"ruleId\":\"X\"}]" });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(0, rep.Relationships);                                          // no behaviour change
            Assert.Equal(1, rep.Annotations);                                            // the owned annotation deployed
            Assert.Contains("relationship:Sales[DateKey]->Date[Date]", rep.SyncedRefs);  // the matched-relationship ref is claimed
            Assert.Equal("[{\"ruleId\":\"X\"}]",
                live.Relationships.OfType<TOM.SingleColumnRelationship>().Single().Annotations["Semanticus_Waivers"].Value);
        }

        // ---- MINOR 4: the preserved live-only level ref escapes '/' '\' (not '%') and follows a rename -

        [Fact]
        public void A_preserved_live_only_level_ref_escapes_delimiters_in_the_level_name()
        {
            // Live-only levels whose names carry the ref delimiters '/' and '\' (and a benign '%'): the ref must ESCAPE
            // '/' -> '\/' and '\' -> '\\' so a name can't forge ref structure, while '%' (not a delimiter) passes through.
            var live = NewStar(withHierarchy: true);
            var lcal = live.Tables["Date"].Hierarchies["Calendar"];
            lcal.Levels.Add(new TOM.Level { Name = "Fiscal/Year", Ordinal = 2, Column = live.Tables["Date"].Columns["Date"], LineageTag = "tag-fy" });
            lcal.Levels.Add(new TOM.Level { Name = "Wk\\End%", Ordinal = 3, Column = live.Tables["Date"].Columns["Month Number"], LineageTag = "tag-wk" });

            var src = NewStar(withHierarchy: true);
            var sh = src.Tables["Date"].Hierarchies["Calendar"];
            sh.Levels.Clear();
            sh.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 0, Column = src.Tables["Date"].Columns["Month Name"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(@"level:Date/Calendar/Fiscal\/Year (live-only, preserved)", rep.LiveOnly);   // '/' escaped
            Assert.Contains(@"level:Date/Calendar/Wk\\End% (live-only, preserved)", rep.LiveOnly);       // '\' escaped, '%' verbatim
        }

        [Fact]
        public void A_preserved_live_only_level_ref_uses_the_new_table_name_after_a_same_deploy_rename()
        {
            // The Date table is RENAMED in the same deploy (tag-date match) and its hierarchy keeps a live-only "Day"
            // level. The preserved level ref is built AFTER the renames loop, so it must name the table's FINAL name.
            var live = NewStar(withHierarchy: true);
            live.Tables["Date"].Hierarchies["Calendar"].Levels.Add(
                new TOM.Level { Name = "Day", Ordinal = 2, Column = live.Tables["Date"].Columns["Date"], LineageTag = "tag-day" });

            var src = NewStar(withHierarchy: true);
            src.Tables["Date"].Name = "Calendar Dim";                         // rename via tag-date match
            var sh = src.Tables["Calendar Dim"].Hierarchies["Calendar"];
            sh.Levels.Clear();
            sh.Levels.Add(new TOM.Level { Name = "Month Name", Ordinal = 0, Column = src.Tables["Calendar Dim"].Columns["Month Name"] });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal("Calendar Dim", live.Tables.Cast<TOM.Table>().Single(t => t.LineageTag == "tag-date").Name);   // rename applied
            Assert.Contains("level:Calendar Dim/Calendar/Day (live-only, preserved)", rep.LiveOnly);                     // ref follows the NEW table name
            Assert.DoesNotContain(rep.LiveOnly, x => x.StartsWith("level:Date/"));                                       // never the pre-rename name
        }

        // ---- ROUND 6 · BLOCKER 1: couple a CROSS-TABLE re-point (endpoint predicate misses) by shared NAME ----

        /// <summary>A Sales fact with two candidate FROM columns (CustomerKey, OtherKey) and two dims (Customer,
        /// CustomerV2), each with an Id. The single relationship named <paramref name="relName"/> joins
        /// Sales[<paramref name="fromCol"/>] -> <paramref name="toTable"/>[Id]. Re-pointing the TO endpoint to a DIFFERENT
        /// table (or moving BOTH endpoints) keeps the relationship NAME but changes its endpoint signature, so
        /// IsEndpointRepoint (3-of-4 parts) cannot see the pairing — only the shared name does.</summary>
        private static TOM.Model RepointStar(string fromCol, string toTable, string relName)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1604, Model = new TOM.Model() };
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
            return db.Model;
        }

        [Fact]
        public void A_cross_table_repoint_couples_by_name_where_the_endpoint_predicate_misses()
        {
            // Live: r joins Sales[CustomerKey] -> Customer[Id]. Source keeps the NAME "r" but re-points the TO endpoint to a
            // DIFFERENT table (CustomerV2). The FROM endpoint is intact but the TO endpoint's TABLE moved, so the pair shares
            // only TWO of four endpoint parts -> IsEndpointRepoint misses. Round 6: the shared name couples them anyway, so
            // the Delete carries the Create as its required replacement.
            var session = RepointStar("CustomerKey", "CustomerV2", "r");
            var snapshotB = RepointStar("CustomerKey", "Customer", "r");

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var createItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Create");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            Assert.Equal("relationship:Sales[CustomerKey]->CustomerV2[Id]", createItem.Ref);
            Assert.Equal("relationship:Sales[CustomerKey]->Customer[Id]", delItem.Ref);
            Assert.NotNull(delItem.ReplacementCreateRefs);                       // coupled despite the cross-table move
            Assert.Contains(createItem.Ref, delItem.ReplacementCreateRefs);
            Assert.Single(delItem.ReplacementCreateRefs);                        // unambiguous: exactly one required replacement
        }

        [Fact]
        public void A_both_endpoints_move_repoint_couples_by_name()
        {
            // The harder shape: source keeps the NAME "r" but moves BOTH endpoints — FROM column CustomerKey -> OtherKey AND
            // TO table Customer -> CustomerV2. NO endpoint part is shared, so IsEndpointRepoint cannot pair them; only the
            // shared name betrays the replacement. Without the name edge the Delete would drop the old relationship with no
            // replacement while the merged Create collides on the duplicate name.
            var session = RepointStar("OtherKey", "CustomerV2", "r");
            var snapshotB = RepointStar("CustomerKey", "Customer", "r");

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var createItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Create");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            Assert.Equal("relationship:Sales[OtherKey]->CustomerV2[Id]", createItem.Ref);
            Assert.Equal("relationship:Sales[CustomerKey]->Customer[Id]", delItem.Ref);
            Assert.NotNull(delItem.ReplacementCreateRefs);
            Assert.Contains(createItem.Ref, delItem.ReplacementCreateRefs);
        }

        [Fact]
        public void A_delete_and_create_with_DIFFERENT_names_and_disjoint_endpoints_are_not_coupled()
        {
            // Guardrail: name coupling must not OVER-couple. Source drops "r" (Sales[CustomerKey]->Customer[Id]) and creates
            // a genuinely-unrelated relationship "s" (Sales[OtherKey]->CustomerV2[Id]) — different name, disjoint endpoints.
            // Neither edge source fires, so the Delete carries NO requirement and a genuine standalone delete still proceeds.
            var session = RepointStar("OtherKey", "CustomerV2", "s");
            var snapshotB = RepointStar("CustomerKey", "Customer", "r");

            var diff = ModelCompare.Diff(session, snapshotB, "session", "live");
            var delItem = diff.Items.Single(i => i.ObjectType == "Relationship" && i.Action == "Delete");
            Assert.True(delItem.ReplacementCreateRefs == null || delItem.ReplacementCreateRefs.Length == 0);   // uncoupled — no false requirement
        }
    }
}
