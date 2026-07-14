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
        public void Model_annotations_and_table_detail_rows_metadata_reach_live_and_are_claimed()   // #135
        {
            var src = NewModel();
            src.Annotations.Add(new TOM.Annotation { Name = "Owner", Value = "Finance" });
            src.Tables["Sales"].Annotations.Add(new TOM.Annotation { Name = "ToolHint", Value = "keep" });
            src.Tables["Sales"].DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "SELECTCOLUMNS ( Sales, \"Amount\", Sales[Amount] )" };
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(2, rep.Metadata);   // one model shell + one table shell
            Assert.Equal("Finance", live.Annotations["Owner"].Value);
            Assert.Equal("keep", live.Tables["Sales"].Annotations["ToolHint"].Value);
            Assert.Equal(src.Tables["Sales"].DefaultDetailRowsDefinition.Expression, live.Tables["Sales"].DefaultDetailRowsDefinition.Expression);
            Assert.Contains("model", rep.SyncedRefs);
            Assert.Contains("table:Sales", rep.SyncedRefs);
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
        public void Child_annotations_and_table_data_category_reach_live_and_are_claimed()   // #135
        {
            var src = NewModel();
            src.Tables["Sales"].DataCategory = "Fact";
            src.Tables["Sales"].Columns["Amount"].Annotations.Add(new TOM.Annotation { Name = "ColumnHint", Value = "currency" });
            src.Tables["Sales"].Measures["Total"].Annotations.Add(new TOM.Annotation { Name = "MeasureHint", Value = "trusted" });
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal(1, rep.DataCategories);
            Assert.Equal(2, rep.Metadata);
            Assert.Equal("Fact", live.Tables["Sales"].DataCategory);
            Assert.Equal("currency", live.Tables["Sales"].Columns["Amount"].Annotations["ColumnHint"].Value);
            Assert.Equal("trusted", live.Tables["Sales"].Measures["Total"].Annotations["MeasureHint"].Value);
            Assert.Contains("table:Sales", rep.SyncedRefs);
            Assert.Contains("column:Sales/Amount", rep.SyncedRefs);
            Assert.Contains("measure:Sales/Total", rep.SyncedRefs);
            Assert.Empty(rep.Unmatched);
        }

        [Fact]
        public void Unsupported_column_metadata_is_named_and_the_ref_is_withheld()   // #135
        {
            var src = NewModel(); src.Tables["Sales"].Columns["Amount"].IsKey = true;
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Contains(rep.Unmatched, x => x.StartsWith("column:Sales/Amount") && x.Contains("column metadata"));
            Assert.DoesNotContain("column:Sales/Amount", rep.SyncedRefs);
            Assert.False(live.Tables["Sales"].Columns["Amount"].IsKey);
        }

        [Fact]
        public void New_child_annotations_are_carried_before_create_is_claimed()   // #135
        {
            var src = NewModel();
            var column = new TOM.CalculatedColumn { Name = "Tax", Expression = "Sales[Amount] * 0.1", LineageTag = "tag-tax" };
            column.Annotations.Add(new TOM.Annotation { Name = "ColumnHint", Value = "derived" });
            src.Tables["Sales"].Columns.Add(column);
            var measure = new TOM.Measure { Name = "Average", Expression = "AVERAGE ( Sales[Amount] )", LineageTag = "tag-average" };
            measure.Annotations.Add(new TOM.Annotation { Name = "MeasureHint", Value = "reviewed" });
            src.Tables["Sales"].Measures.Add(measure);
            var live = NewModel();

            var rep = LiveDeploy.SyncModels(src, live, apply: true);

            Assert.Equal("derived", live.Tables["Sales"].Columns["Tax"].Annotations["ColumnHint"].Value);
            Assert.Equal("reviewed", live.Tables["Sales"].Measures["Average"].Annotations["MeasureHint"].Value);
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
