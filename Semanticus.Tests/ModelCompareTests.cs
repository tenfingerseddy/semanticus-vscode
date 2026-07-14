using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Tests
{
    /// <summary>
    /// The ALM compare/apply ROOT-CAUSE fixes (PR #118 Group B): refs are escaped so two objects can't collide on one
    /// ref (B1/B2), Apply resolves the target by IDENTITY not the source name (B3), duplicate target tags are surfaced
    /// as Ambiguous instead of silent first-wins (B4), and an empty selection applies NOTHING (B5). All on raw TOM,
    /// offline — no endpoint.
    /// </summary>
    public sealed class ModelCompareTests
    {
        private static TOM.Table Table(TOM.Model m, string name, string tag)
        {
            var t = new TOM.Table { Name = name };
            if (tag != null) t.LineageTag = tag;
            m.Tables.Add(t);
            return t;
        }
        private static void Meas(TOM.Table t, string name, string expr, string tag)
        {
            var mm = new TOM.Measure { Name = name, Expression = expr };
            if (tag != null) mm.LineageTag = tag;
            t.Measures.Add(mm);
        }
        private static void Col(TOM.Table t, string name, string tag)
        {
            var c = new TOM.DataColumn { Name = name, DataType = TOM.DataType.Int64 };
            if (tag != null) c.LineageTag = tag;
            t.Columns.Add(c);
        }
        private static string ExprOf(TOM.Model m, string table, string measure) => m.Tables.Find(table)?.Measures.Find(measure)?.Expression;

        // ---- B1: a name containing '/' must not make two different measures share one ref (which used to tick/delete both). ----
        [Fact]
        public void B1_slash_in_name_does_not_collide_two_measures_on_one_ref()
        {
            // Source: table A has measure "B/C"=2; table "A/B" has no measure "C".
            var left = new TOM.Model();
            Meas(Table(left, "A", "t-a"), "B/C", "2", "t-bc");
            Table(left, "A/B", "t-ab");
            // Target: table A has measure "B/C"=1 (→ Update); table "A/B" has measure "C" (→ Delete). Under the OLD
            // unescaped grammar BOTH refs were "measure:A/B/C" — one collided string for two distinct objects.
            var right = new TOM.Model();
            Meas(Table(right, "A", "t-a"), "B/C", "1", "t-bc");
            Meas(Table(right, "A/B", "t-ab"), "C", "9", "t-c");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Update");
            var delete = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Delete");
            Assert.NotEqual(update.Ref, delete.Ref);   // ROOT CAUSE fixed: the two refs are now DISTINCT

            // Tick ONLY the update. The unselected delete must NOT happen, and the update must land on the right measure.
            ModelCompare.Apply(left, right, diff, new HashSet<string> { update.Ref });
            Assert.Equal("2", ExprOf(right, "A", "B/C"));       // update landed on the correct measure
            Assert.NotNull(right.Tables.Find("A/B").Measures.Find("C"));   // the unselected delete did NOT fire
        }

        // ---- Load path: a TMDL folder must come back in PowerBI compatibility MODE + a restored level. ----
        // Regression for the Compare "-2" bug: DeserializeDatabaseFromFolder leaves CompatibilityMode = Unknown, so
        // serializing a Power-BI-only property (a calendar RelatedColumnDetails column, CL 1701+) validated it in
        // AnalysisServices mode and threw "compatibility level of -2 is below the minimal compatibility level of -2".
        [Fact]
        public void LoadRawModelDb_restores_PowerBI_context_for_a_tmdl_folder()
        {
            var src = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(TestModels.FindBim()), null, AS.CompatibilityMode.PowerBI);
            var dir = Path.Combine(Path.GetTempPath(), "sem-tmdl-" + Guid.NewGuid().ToString("N"));
            try
            {
                TOM.TmdlSerializer.SerializeDatabaseToFolder(src, dir);
                var loaded = ModelCompare.LoadRawModelDb(dir);
                Assert.Equal(AS.CompatibilityMode.PowerBI, loaded.CompatibilityMode);   // NOT Unknown (the "-2" trigger)
                Assert.Equal(src.CompatibilityLevel, loaded.CompatibilityLevel);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // A path pasted with surrounding quotes must load, not resolve relative to the process CWD (the VS Code
        // install dir). Regression for "No TMDL/.bim model found at ...\Microsoft VS Code"C:\...\model.tmdl"".
        [Fact]
        public void LoadRawModelDb_strips_surrounding_quotes_from_the_path()
        {
            var src0 = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(TestModels.FindBim()), null, AS.CompatibilityMode.PowerBI);
            var qdir = Path.Combine(Path.GetTempPath(), "sem-tmdl-q-" + Guid.NewGuid().ToString("N"));
            try
            {
                TOM.TmdlSerializer.SerializeDatabaseToFolder(src0, qdir);
                var quoted = "\"" + qdir + "\"";
                Assert.NotNull(ModelCompare.LoadRawModelDb(quoted).Model);   // quoted absolute path resolves, not CWD-relative
            }
            finally { if (Directory.Exists(qdir)) Directory.Delete(qdir, true); }
        }

        [Fact]
        public void Diff_serializes_a_model_only_tmdl_with_RelatedColumnDetails_without_the_minus_two_error()
        {
            var src = new TOM.Database
            {
                Name = "M", CompatibilityLevel = 1701, CompatibilityMode = AS.CompatibilityMode.PowerBI,
                Model = new TOM.Model(),
            };
            var table = new TOM.Table { Name = "T" };
            src.Model.Tables.Add(table);
            var key = new TOM.DataColumn { Name = "Key", DataType = TOM.DataType.Int64 };
            var rows = new TOM.DataColumn { Name = "Rows", DataType = TOM.DataType.String };
            table.Columns.Add(key);
            table.Columns.Add(rows);
            rows.RelatedColumnDetails = new TOM.RelatedColumnDetails();
            rows.RelatedColumnDetails.GroupByColumns.Add(new TOM.GroupByColumn { GroupingColumn = key });

            var dir = Path.Combine(Path.GetTempPath(), "sem-tmdl-model-only-" + Guid.NewGuid().ToString("N"));
            try
            {
                TOM.TmdlSerializer.SerializeModelToFolder(src.Model, dir);
                var loaded = ModelCompare.LoadRawModelDb(dir, src.CompatibilityLevel);
                Assert.Equal(AS.CompatibilityMode.PowerBI, loaded.CompatibilityMode);
                Assert.Null(Record.Exception(() => ModelCompare.Diff(loaded.Model, loaded.Model, "src", "tgt", includeEqual: true)));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Diff_serializes_an_attached_PowerBI_grouping_column_in_its_database_context()
        {
            var db = new TOM.Database
            {
                Name = "M", CompatibilityLevel = 1701, CompatibilityMode = AS.CompatibilityMode.PowerBI,
                Model = new TOM.Model(),
            };
            var table = new TOM.Table { Name = "T" };
            db.Model.Tables.Add(table);
            var key = new TOM.DataColumn { Name = "Key", DataType = TOM.DataType.Int64 };
            var rows = new TOM.DataColumn { Name = "Rows", DataType = TOM.DataType.String };
            table.Columns.Add(key);
            table.Columns.Add(rows);
            rows.RelatedColumnDetails = new TOM.RelatedColumnDetails();
            rows.RelatedColumnDetails.GroupByColumns.Add(new TOM.GroupByColumn { GroupingColumn = key });

            Assert.Null(Record.Exception(() => ModelCompare.Diff(db.Model, db.Model, "src", "tgt", includeEqual: true)));
        }

        // Push Changes does not compare the in-memory model object to itself. The public route snapshots the open
        // session through TMDL, then loads the target independently. TMDL materializes some semantic defaults that a
        // .bim legitimately omits, so this public-path pin is stronger than ModelCompare.Diff(model, model).
        [Fact]
        public async Task Public_compare_of_the_open_model_to_its_own_file_is_exactly_empty()
        {
            var db = new TOM.Database("SelfCompare")
            {
                CompatibilityLevel = 1604,
                CompatibilityMode = AS.CompatibilityMode.PowerBI,
                Model = new TOM.Model(),
            };
            var cg = new TOM.Table { Name = "Time Intelligence", LineageTag = "table-ti", CalculationGroup = new TOM.CalculationGroup() };
            cg.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "MTD" });
            db.Model.Tables.Add(cg);
            db.Model.Expressions.Add(new TOM.NamedExpression { Name = "Source Parameter" });

            var path = Path.Combine(Path.GetTempPath(), "semanticus-self-compare-" + Guid.NewGuid().ToString("N") + ".bim");
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(TOM.JsonSerializer.SerializeDatabase(db));
                foreach (var item in root.SelectTokens("$..calculationItems[*]").OfType<Newtonsoft.Json.Linq.JObject>()) item.Remove("ordinal");
                foreach (var expression in root.SelectTokens("$.model.expressions[*]").OfType<Newtonsoft.Json.Linq.JObject>()) expression.Remove("kind");
                var json = root.ToString();
                Assert.DoesNotContain("\"ordinal\"", json);   // omitted .bim defaults are the reproduction precondition
                Assert.DoesNotContain("\"kind\"", json);
                File.WriteAllText(path, json);

                using var engine = new LocalEngine(new SessionManager());
                await engine.OpenAsync(path);
                var diff = await engine.CompareModelsAsync(
                    new ModelRef { Kind = "session", Label = "working copy" },
                    new ModelRef { Kind = "file", Path = path, Label = "same model" },
                    includeEqual: false, origin: "human");

                Assert.True(diff.Created + diff.Updated + diff.Deleted == 0,
                    string.Join(Environment.NewLine, diff.Items.Select(i => $"{i.Action} {i.ObjectType} {i.Ref}{Environment.NewLine}LEFT={i.LeftText}{Environment.NewLine}RIGHT={i.RightText}")));
                Assert.Empty(diff.Items);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // ---- Compare-to-self must not show a calculated column as a phantom Update on its INFERRED dataType (T94). ----
        // A calculated column's dataType is server-inferred from its DAX (isDataTypeInferred:true): the XMLA-loaded side
        // carries the computed type + marker, the offline/TMDL side does not, so a compare-to-self flagged every
        // calculated column Updated. The inferred type is server-derived; Normalize scrubs it (like a measure's).
        [Fact]
        public void Diff_ignores_a_calculated_columns_inferred_datatype()
        {
            var left = new TOM.Model();
            var lt = Table(left, "T", "t-t");
            lt.Columns.Add(new TOM.CalculatedColumn { Name = "CC", Expression = "1", LineageTag = "c-cc" });
            var right = new TOM.Model();
            var rt = Table(right, "T", "t-t");
            // Set DataType before IsDataTypeInferred so the marker survives true (the DataType setter flips it false).
            rt.Columns.Add(new TOM.CalculatedColumn { Name = "CC", Expression = "1", LineageTag = "c-cc", DataType = TOM.DataType.DateTime, IsDataTypeInferred = true });

            var diff = ModelCompare.Diff(left, right, "src", "tgt", includeEqual: true);
            var cc = diff.Items.Single(i => i.ObjectType == "Column" && i.Name == "CC");
            Assert.Equal("Equal", cc.Action);   // inferred-type divergence is scrubbed -> no phantom Update
        }

        // ...but an AUTHORED dataType change (a data column carries no inferred marker) MUST still surface as an Update.
        [Fact]
        public void Diff_still_reports_an_authored_datatype_change()
        {
            var left = new TOM.Model();
            var lt = Table(left, "T", "t-t");
            lt.Columns.Add(new TOM.DataColumn { Name = "DC", LineageTag = "c-dc", DataType = TOM.DataType.Int64 });
            var right = new TOM.Model();
            var rt = Table(right, "T", "t-t");
            rt.Columns.Add(new TOM.DataColumn { Name = "DC", LineageTag = "c-dc", DataType = TOM.DataType.String });

            var diff = ModelCompare.Diff(left, right, "src", "tgt", includeEqual: true);
            var dc = diff.Items.Single(i => i.ObjectType == "Column" && i.Name == "DC");
            Assert.Equal("Update", dc.Action);   // authored int64 -> string is a real, breaking change
        }

        // ---- B2: relationships whose endpoint names contain '[' ']' '->' must not collide on one sig (wrong-rel delete). ----
        [Fact]
        public void B2_relationship_sig_is_injective_for_bracket_arrow_names()
        {
            // Two relationships that produce the SAME signature under the OLD template "FT[FC]->TT[TC]":
            //   R1: A["x]->B[y"] -> C["z"]   ⇒ "A[x]->B[y]->C[z]"
            //   R2: A["x"]       -> B["y]->C[z"] ⇒ "A[x]->B[y]->C[z]"
            var m = new TOM.Model();
            var a = Table(m, "A", "t-a"); Col(a, "x]->B[y", "c1"); Col(a, "x", "c2");
            var b = Table(m, "B", "t-b"); Col(b, "y]->C[z", "c3");
            var c = Table(m, "C", "t-c"); Col(c, "z", "c4");
            m.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r1", FromColumn = a.Columns["x]->B[y"], ToColumn = c.Columns["z"] });
            m.Relationships.Add(new TOM.SingleColumnRelationship { Name = "r2", FromColumn = a.Columns["x"], ToColumn = b.Columns["y]->C[z"] });

            // Diff against a model with NO relationships → both read as Delete, each with its own ref.
            var bare = new TOM.Model();
            Table(bare, "A", "t-a"); Table(bare, "B", "t-b"); Table(bare, "C", "t-c");
            var diff = ModelCompare.Diff(bare, m, "src", "tgt");
            var rels = diff.Items.Where(i => i.ObjectType == "Relationship" && i.Action == "Delete").ToList();
            Assert.Equal(2, rels.Count);
            Assert.NotEqual(rels[0].Ref, rels[1].Ref);   // ROOT CAUSE fixed: injective sig, distinct refs

            // Deleting ONE via the live-delete channel removes exactly that relationship, leaving the other intact.
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { LiveDeleteTarget.FromDiffItem(rels[0]) }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Single(m.Relationships);   // the OTHER relationship survives (pre-fix: first-match deleted the wrong one)
        }

        // ---- B3a: a source table renamed on the target (tag-matched) must not land a child on an unrelated same-named table. ----
        [Fact]
        public void B3a_update_lands_on_the_renamed_target_not_an_unrelated_same_named_table()
        {
            // Source calls the table "Sales" (tag t-rev), measure M=2.
            var left = new TOM.Model();
            Meas(Table(left, "Sales", "t-rev"), "M", "2", "t-m");
            // Target: the SAME lineage-tagged table was renamed to "Revenue" (M=1), AND an UNRELATED "Sales" table
            // (different tag) also exists with its own M=99.
            var right = new TOM.Model();
            Meas(Table(right, "Revenue", "t-rev"), "M", "1", "t-m");
            Meas(Table(right, "Sales", "t-other"), "M", "99", "t-other-m");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Update");
            ModelCompare.Apply(left, right, diff, new HashSet<string> { update.Ref });

            Assert.Equal("2", ExprOf(right, "Revenue", "M"));   // landed on the tag-matched (renamed) table
            Assert.Equal("99", ExprOf(right, "Sales", "M"));    // the unrelated same-named table is UNTOUCHED
        }

        // ---- B3b: a lineage-matched rename applies as a RENAME, not duplicate+orphan. ----
        [Fact]
        public void B3b_lineage_matched_rename_is_a_rename_not_duplicate_plus_orphan()
        {
            var left = new TOM.Model();
            Meas(Table(left, "T", "t-t"), "NewName", "1", "t-m");   // source renamed the measure
            var right = new TOM.Model();
            Meas(Table(right, "T", "t-t"), "OldName", "1", "t-m");  // target still carries the OLD name (same tag)

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var it = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Update");
            ModelCompare.Apply(left, right, diff, new HashSet<string> { it.Ref });

            var measures = right.Tables.Find("T").Measures;
            Assert.Single(measures);                       // exactly one — NOT duplicate+orphan
            Assert.NotNull(measures.Find("NewName"));      // renamed
            Assert.Null(measures.Find("OldName"));         // the stale old name is gone
        }

        // ---- B4: NOT reachable in this lane. The ambiguity guard (Action=="Ambiguous", refuse-and-report on a
        // duplicate target LineageTag) is kept as DEFENSE-IN-DEPTH, but raw TOM — which ModelCompare compares — rejects
        // duplicate lineage tags in a collection on BOTH Add and deserialize ("An object with lineage-tag 'x' already
        // exists in the collection"), so the scenario can't be constructed as a test. See the PR comment (probe evidence).
        // The dupTags branch therefore never fires for a valid model and changes no behavior; it fails SAFE if a future
        // non-validating path ever produced a dupe on this irreversible-delete lane.

        // ---- B5: the loaded gun. An EMPTY (non-null) selection applies NOTHING; null applies ALL. ----
        [Fact]
        public void B5_empty_selection_applies_nothing_null_applies_all()
        {
            var left = new TOM.Model();
            Meas(Table(left, "S", "t-s"), "M", "2", "t-m");

            // Empty set ⇒ apply nothing (pre-fix: empty == apply-all).
            var rightEmpty = new TOM.Model();
            Meas(Table(rightEmpty, "S", "t-s"), "M", "1", "t-m");
            var diffE = ModelCompare.Diff(left, rightEmpty, "src", "tgt");
            var oEmpty = ModelCompare.Apply(left, rightEmpty, diffE, new HashSet<string>());
            Assert.Empty(oEmpty.Applied);
            Assert.Equal("1", ExprOf(rightEmpty, "S", "M"));   // unchanged

            // null ⇒ apply all.
            var rightAll = new TOM.Model();
            Meas(Table(rightAll, "S", "t-s"), "M", "1", "t-m");
            var diffA = ModelCompare.Diff(left, rightAll, "src", "tgt");
            var oAll = ModelCompare.Apply(left, rightAll, diffA, null);
            Assert.NotEmpty(oAll.Applied);
            Assert.Equal("2", ExprOf(rightAll, "S", "M"));     // applied
        }

        // ---- Identity is TERMINAL on a miss: a carried tag that no longer resolves must NOT degrade to name. ----
        // (Follow-up to the codex review of 97c74ec: the fallback reintroduced the wrong-object bug for the stale-tag case.)

        // Table identity stale: item carries TargetTableTag=T1; the apply target has no T1 but an unrelated table with
        // the SAME (source) name. A selected child DELETE must be reported Failed, and that unrelated table's child
        // must be UNTOUCHED. Pre-fix (tag→name fallback) it deleted the unrelated child.
        [Fact]
        public void Stale_owning_table_tag_refuses_and_leaves_the_unrelated_same_named_table_untouched()
        {
            var left = new TOM.Model();
            Table(left, "Sales", "T1");                       // source table (tag T1), no "Drop"
            var a = new TOM.Model();
            Meas(Table(a, "Sales", "T1"), "Drop", "1", "d1"); // diff target A: a right-only "Drop" => Delete carrying TargetTableTag=T1
            var diff = ModelCompare.Diff(left, a, "src", "A");
            var del = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Delete");

            var b = new TOM.Model();                           // apply target B: NO T1, but an UNRELATED "Sales" with its own "Drop"
            Meas(Table(b, "Sales", "other"), "Drop", "1", "other-d");

            var outcome = ModelCompare.Apply(left, b, diff, new HashSet<string> { del.Ref });
            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == del.Ref && f.Reason.Contains("no longer exists"));
            Assert.NotNull(b.Tables.Find("Sales").Measures.Find("Drop"));   // the unrelated table's child is UNTOUCHED
        }

        // Child identity stale: item carries TargetTag=M1; the apply target has a same-named "Margin" but RECREATED with
        // a different tag. A selected UPDATE must be reported Failed, and live "Margin" must be UNCHANGED. Pre-fix it
        // name-matched and overwrote the wrong measure.
        [Fact]
        public void Stale_child_tag_refuses_and_leaves_the_same_named_live_child_unchanged()
        {
            var left = new TOM.Model();
            Meas(Table(left, "T", "tt"), "Margin", "2", "M1");   // source wants Margin=2
            var a = new TOM.Model();
            Meas(Table(a, "T", "tt"), "Margin", "1", "M1");       // diff => Update carrying TargetTag=M1
            var diff = ModelCompare.Diff(left, a, "src", "A");
            var upd = diff.Items.Single(i => i.ObjectType == "Measure" && i.Action == "Update");

            var b = new TOM.Model();
            Meas(Table(b, "T", "tt"), "Margin", "1", "DIFFERENT");   // recreated with a different tag

            var outcome = ModelCompare.Apply(left, b, diff, new HashSet<string> { upd.Ref });
            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == upd.Ref && f.Reason.Contains("no longer exists"));
            Assert.Equal("1", ExprOf(b, "T", "Margin"));   // the same-named live child is UNCHANGED
        }

        // Tag-less child kind (a partition — TOM assigns no LineageTag): name resolution is legitimate and unaffected.
        [Fact]
        public void Tagless_child_still_resolves_by_name()
        {
            var left = new TOM.Model();
            var lt = Table(left, "S", "ts");
            lt.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            var right = new TOM.Model();
            var rt = Table(right, "S", "ts");
            rt.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            rt.Partitions.Add(new TOM.Partition { Name = "P2", Source = new TOM.MPartitionSource { Expression = "let y=2 in y" } });   // right-only => Delete

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var del = diff.Items.Single(i => i.ObjectType == "Partition" && i.Action == "Delete");
            Assert.Null(del.TargetTag);   // partitions carry no identity tag -> name resolution is legitimate

            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { del.Ref });
            Assert.Contains(del.Ref, outcome.Applied);
            Assert.Null(rt.Partitions.Find("P2"));       // deleted by name
            Assert.NotNull(rt.Partitions.Find("P1"));
        }

        // ---- RETAG / REPUBLISH: same name, DIFFERENT non-empty tags ⇒ Match does not pair; both halves flagged. ----

        [Fact]
        public void Retag_surfaces_a_flagged_delete_and_create_pair()
        {
            var left = new TOM.Model(); Table(left, "Sales", "T1");    // source Sales tag T1
            var right = new TOM.Model(); Table(right, "Sales", "T2");  // target Sales tag T2 (republished)
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var del = diff.Items.Single(i => i.ObjectType == "Table" && i.Action == "Delete");
            var cre = diff.Items.Single(i => i.ObjectType == "Table" && i.Action == "Create");
            Assert.True(del.LikelyRepublished);
            Assert.True(cre.LikelyRepublished);
            Assert.Equal("T2", del.TargetTag);                          // the delete carries the LIVE tag
            Assert.Equal(cre.Ref, del.RetaggedCounterpart);            // cross-linked (same name-based ref)
            Assert.Equal(del.Ref, cre.RetaggedCounterpart);
        }

        [Fact]
        public void Both_tags_empty_pair_as_normal_no_republish_flag()   // untagged models: NO regression
        {
            var left = new TOM.Model(); var lt = Table(left, "Sales", null); Meas(lt, "M", "2", null);
            var right = new TOM.Model(); var rt = Table(right, "Sales", null); Meas(rt, "M", "1", null);
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            Assert.Contains(diff.Items, i => i.ObjectType == "Measure" && i.Action == "Update");   // paired as an Update
            Assert.DoesNotContain(diff.Items, i => i.LikelyRepublished);
        }

        [Fact]
        public void One_tag_empty_pairs_as_normal_no_republish_flag()    // source tagged, target not (or vice versa): NO regression
        {
            var left = new TOM.Model(); Table(left, "Sales", "T1");
            var right = new TOM.Model(); Table(right, "Sales", null);
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            Assert.DoesNotContain(diff.Items, i => i.LikelyRepublished);   // the IsNullOrEmpty allowance still pairs them
        }

        [Fact]
        public void Genuine_right_only_delete_is_not_flagged_and_stays_deletable()
        {
            var left = new TOM.Model(); Table(left, "Keep", "K1");
            var right = new TOM.Model(); Table(right, "Keep", "K1"); Table(right, "Gone", "G1");   // Gone has NO left counterpart
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var del = diff.Items.Single(i => i.ObjectType == "Table" && i.Action == "Delete");
            Assert.Equal("table:Gone", del.Ref);
            Assert.False(del.LikelyRepublished);                        // ordinary delete — must stay deletable
        }

        // Per-collection uniqueness: two columns in DIFFERENT tables may legally share a LineageTag. Resolving within
        // one table's children must not mis-resolve the other's.
        [Fact]
        public void Cross_table_duplicate_column_tag_does_not_misresolve()
        {
            var m = new TOM.Model();
            var t1 = new TOM.Table { Name = "T1", LineageTag = "t1" }; t1.Columns.Add(new TOM.DataColumn { Name = "C", DataType = TOM.DataType.Int64, LineageTag = "dup" }); m.Tables.Add(t1);
            var t2 = new TOM.Table { Name = "T2", LineageTag = "t2" }; t2.Columns.Add(new TOM.DataColumn { Name = "C", DataType = TOM.DataType.Int64, LineageTag = "dup" }); m.Tables.Add(t2);   // same tag, different table — legal
            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(m, new[] { new LiveDeleteTarget { Kind = "column", Ref = "column:T1/C", Tag = "dup", TableTag = "t1", Name = "C", Table = "T1" } }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Null(t1.Columns.Find("C"));       // T1's C removed
            Assert.NotNull(t2.Columns.Find("C"));    // T2's same-tag C is UNTOUCHED (resolved within T1 only)
        }

        // (Defect 2) ModelCompare.ApplyTopLevel resolves an EXPRESSION by tag (terminal on miss), not by name — a
        // stale diff item for FxRate tag A applied against a target holding FxRate tag B must NOT mutate B.
        [Fact]
        public void Expression_apply_resolves_by_tag_not_name()
        {
            var left = new TOM.Model(); left.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "2", LineageTag = "A" });
            var a = new TOM.Model(); a.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "A" });
            var diff = ModelCompare.Diff(left, a, "src", "A");               // Update, TargetTag=A
            var upd = diff.Items.Single(i => i.ObjectType == "Expression" && i.Action == "Update");
            Assert.Equal("A", upd.TargetTag);

            var b = new TOM.Model(); b.Expressions.Add(new TOM.NamedExpression { Name = "FxRate", Kind = TOM.ExpressionKind.M, Expression = "1", LineageTag = "B" });   // retagged target
            var outcome = ModelCompare.Apply(left, b, diff, new HashSet<string> { upd.Ref });
            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == upd.Ref && f.Reason.Contains("no longer exists"));
            Assert.Equal("1", b.Expressions.Find("FxRate").Expression);     // NOT mutated
        }

        // ---- AlmRef round-trips exactly one (kind, table, name) for pathological names. ----
        [Theory]
        [InlineData("measure", "A", "B/C")]
        [InlineData("measure", "A/B", "C")]
        [InlineData("measure", "Fin:2024", "Rev/Net")]
        [InlineData("table", null, "Finance/Sales")]
        [InlineData("column", "A\\B", "C:D")]
        public void AlmRef_child_and_top_round_trip(string kind, string table, string name)
        {
            var refStr = table == null ? AlmRef.Top(kind, name) : AlmRef.Child(kind, table, name);
            var (k, t, n) = AlmRef.Parse(refStr);
            Assert.Equal(kind, k);
            Assert.Equal(table, t);
            Assert.Equal(name, n);
        }

        // ---- #121: a live-read model carries server-populated runtime state an offline model never has. ----

        /// <summary>Round-trip a model through TOM's own deserializer with the properties an XMLA endpoint stamps onto
        /// every object. This is how the bug actually arrives: the setters are read-only in TOM, so injecting them into
        /// the JSON is the only faithful reproduction of a live-read model.</summary>
        private static TOM.Model AsReadFromServer(TOM.Model m)
        {
            const string T = "2026-07-09T01:02:03.000000Z";
            var db = new TOM.Database("d") { CompatibilityLevel = 1600, Model = m.Clone() };
            var root = Newtonsoft.Json.Linq.JObject.Parse(TOM.JsonSerializer.SerializeDatabase(db));

            // Stamp ONLY the properties each TOM type actually accepts — its deserializer rejects the rest, which keeps
            // this fixture honest about what a server really sends (e.g. a Partition has no structureModifiedTime).
            void Stamp(Newtonsoft.Json.Linq.JObject o, params string[] props) { foreach (var p in props) o[p] = T; }
            Newtonsoft.Json.Linq.JArray Arr(Newtonsoft.Json.Linq.JObject o, string p) => o[p] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();

            foreach (var tbl in Arr((Newtonsoft.Json.Linq.JObject)root["model"], "tables").OfType<Newtonsoft.Json.Linq.JObject>())
            {
                Stamp(tbl, "modifiedTime", "structureModifiedTime");
                foreach (var col in Arr(tbl, "columns").OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    Stamp(col, "modifiedTime", "structureModifiedTime", "refreshedTime");
                    col["state"] = "Ready";
                    col["attributeHierarchy"] = new Newtonsoft.Json.Linq.JObject { ["state"] = "Ready", ["modifiedTime"] = T, ["refreshedTime"] = T };
                }
                foreach (var ms in Arr(tbl, "measures").OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    Stamp(ms, "modifiedTime", "structureModifiedTime");
                    ms["dataType"] = "int64";   // server-INFERRED from the expression; absent offline
                }
                foreach (var p in Arr(tbl, "partitions").OfType<Newtonsoft.Json.Linq.JObject>())
                { Stamp(p, "modifiedTime", "refreshedTime"); p["state"] = "Ready"; }
            }
            return TOM.JsonSerializer.DeserializeDatabase(root.ToString(), null, AS.CompatibilityMode.PowerBI).Model;
        }

        [Fact]
        public void Server_populated_properties_do_not_make_an_identical_model_read_as_changed()
        {
            var offline = new TOM.Model();
            var t = Table(offline, "Sales", "t-1");
            Col(t, "Amount", "c-1");
            Col(t, "Qty", "c-2");
            Meas(t, "Total", "SUM(Sales[Amount])", "m-1");
            t.Partitions.Add(new TOM.Partition { Name = "P", Source = new TOM.MPartitionSource { Expression = "let x = 1 in x" } });

            var diff = ModelCompare.Diff(offline, AsReadFromServer(offline), "offline", "live", includeEqual: true);

            Assert.Empty(diff.Items.Where(i => i.Action != "Equal"));
            Assert.Equal(0, diff.Updated);
        }

        [Fact]
        public void Column_dataType_is_authored_so_a_real_change_is_still_reported()
        {
            var offline = new TOM.Model();
            Col(Table(offline, "Sales", "t-1"), "Amount", "c-1");   // Int64
            var live = AsReadFromServer(offline);
            live.Tables.Find("Sales").Columns.Find("Amount").DataType = TOM.DataType.Decimal;

            var diff = ModelCompare.Diff(offline, live, "offline", "live");

            Assert.Equal("Update", Assert.Single(diff.Items.Where(i => i.ObjectType == "Column")).Action);
        }

        [Fact]
        public void Measure_expression_change_is_still_reported_despite_the_dataType_scrub()
        {
            var offline = new TOM.Model();
            Meas(Table(offline, "Sales", "t-1"), "Total", "SUM(Sales[Amount])", "m-1");
            var live = AsReadFromServer(offline);
            live.Tables.Find("Sales").Measures.Find("Total").Expression = "SUMX(Sales, 1)";

            var diff = ModelCompare.Diff(offline, live, "offline", "live");

            Assert.Equal("Update", Assert.Single(diff.Items.Where(i => i.ObjectType == "Measure")).Action);
        }

        // ============================================================================================
        // #124 — a MATCHED table was compared only on TableProps, so its calculation-group ITEMS, its calc-group
        // Precedence and its RefreshPolicy were NOT walked. A changed calc-item DAX expression (a REAL change)
        // produced ZERO diff — a false-EQUAL that silently drops the change on deploy (data loss). The fix walks
        // calc items (detect), routes their file-apply (ApplyOne) AND the live delete channel (LiveDeploy), and
        // reports a Precedence / RefreshPolicy change as a Table Update.
        // ============================================================================================
        private static TOM.Table CalcGroup(TOM.Model m, string name, string tag, int precedence = 0)
        {
            var t = new TOM.Table { Name = name };
            if (tag != null) t.LineageTag = tag;
            t.CalculationGroup = new TOM.CalculationGroup { Precedence = precedence };
            m.Tables.Add(t);
            return t;
        }
        private static void CalcItem(TOM.Table t, string name, string expr)
            => t.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = name, Expression = expr });
        private static string CalcItemExpr(TOM.Model m, string table, string name)
            => m.Tables.Find(table)?.CalculationGroup?.CalculationItems.Find(name)?.Expression;

        // The headline false-EQUAL: two models whose calc-item DAX differs must yield exactly one Update that Apply lands.
        [Fact]
        public void CalcItem_expression_change_yields_one_update_and_applies()   // #124
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti"), "YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )");
            var right = new TOM.Model(); CalcItem(CalcGroup(right, "TI", "t-ti"), "YTD", "SELECTEDMEASURE ()");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var upd = Assert.Single(diff.Items);                       // pre-fix: ZERO items (blind) — a silent false-EQUAL
            Assert.Equal("CalculationItem", upd.ObjectType);
            Assert.Equal("Update", upd.Action);
            Assert.Equal("YTD", upd.Name);

            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { upd.Ref });
            Assert.Contains(upd.Ref, outcome.Applied);
            Assert.Empty(outcome.Failed);
            Assert.Equal("CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )", CalcItemExpr(right, "TI", "YTD"));
        }

        [Fact]
        public void CalcItem_non_default_ordinal_remains_an_authored_update()
        {
            var left = new TOM.Model(); var li = CalcGroup(left, "TI", "t-ti"); CalcItem(li, "YTD", "a");
            li.CalculationGroup.CalculationItems["YTD"].Ordinal = 2;
            var right = new TOM.Model(); var ri = CalcGroup(right, "TI", "t-ti"); CalcItem(ri, "YTD", "a");
            ri.CalculationGroup.CalculationItems["YTD"].Ordinal = 0;

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = Assert.Single(diff.Items);
            Assert.Equal("CalculationItem", update.ObjectType);
            Assert.Equal("Update", update.Action);
            Assert.Contains("\"ordinal\": 2", update.LeftText);
            Assert.DoesNotContain("\"ordinal\"", update.RightText);   // default zero is normalized to omitted
        }

        // Detection + delete channel must land TOGETHER: a right-only calc item is a Delete that the LiveDeploy delete
        // channel actually honours (pre-fix it hit the "unknown kind" default → a SILENT dropped delete, worse than blind).
        [Fact]
        public void CalcItem_right_only_is_a_delete_the_live_channel_honors()   // #124
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti"), "YTD", "a");
            var right = new TOM.Model(); var rg = CalcGroup(right, "TI", "t-ti"); CalcItem(rg, "YTD", "a"); CalcItem(rg, "QTD", "b");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var del = Assert.Single(diff.Items);
            Assert.Equal("CalculationItem", del.ObjectType);
            Assert.Equal("Delete", del.Action);
            Assert.Equal("QTD", del.Name);

            var target = LiveDeleteTarget.FromDiffItem(del);
            Assert.Equal("calculationitem", target.Kind);   // routes to the calc-item channel, NOT the silent default
            Assert.Equal("t-ti", target.TableTag);          // owning-table identity carried (resolves the right table)

            var rep = new DeployReport();
            LiveDeploy.RemoveExplicit(right, new[] { target }, apply: true, rep);
            Assert.Equal(1, rep.Deleted);
            Assert.Empty(rep.DeletesRefused);
            Assert.Empty(rep.DeletesAlreadyAbsent);
            Assert.Null(right.Tables.Find("TI").CalculationGroup.CalculationItems.Find("QTD"));
            Assert.NotNull(right.Tables.Find("TI").CalculationGroup.CalculationItems.Find("YTD"));   // survivor untouched
        }

        // Guard the null calc group on the target: adding a calc item to a plain table can't work — it must FAIL with a
        // clear reason via the outcome, never a raw NullReference across the door.
        [Fact]
        public void CalcItem_apply_onto_a_table_with_no_calc_group_fails_with_a_clear_reason()   // #124
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti"), "YTD", "a");
            var target = new TOM.Model(); Table(target, "TI", "t-ti");   // same tag, but a PLAIN table (no calc group)

            var create = new ModelDiffItem { Ref = "calculationitem:TI/YTD", ObjectType = "CalculationItem", Name = "YTD", Table = "TI", Action = "Create", TargetTable = "TI", TargetTableTag = "t-ti" };
            var diff = new ModelDiff { Items = new[] { create } };
            var outcome = ModelCompare.Apply(left, target, diff, new HashSet<string> { create.Ref });
            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == create.Ref && f.Reason.Contains("no calculation group"));
        }

        [Fact]
        public void CalcGroup_precedence_change_is_a_table_update()   // #124
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti", precedence: 10), "YTD", "a");
            var right = new TOM.Model(); CalcItem(CalcGroup(right, "TI", "t-ti", precedence: 0), "YTD", "a");
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var upd = Assert.Single(diff.Items);
            Assert.Equal("Table", upd.ObjectType);
            Assert.Equal("Update", upd.Action);
            Assert.Contains("precedence=10", upd.LeftText);
            Assert.Contains("precedence=0", upd.RightText);
        }

        [Fact]
        public void RefreshPolicy_change_is_a_table_update()   // #124
        {
            var left = new TOM.Model();  var lt = Table(left, "Sales", "t-s");
            lt.RefreshPolicy = new TOM.BasicRefreshPolicy { IncrementalPeriods = 3, IncrementalGranularity = TOM.RefreshGranularityType.Month, RollingWindowPeriods = 5, RollingWindowGranularity = TOM.RefreshGranularityType.Year, SourceExpression = "let x = 1 in x" };
            var right = new TOM.Model(); Table(right, "Sales", "t-s");   // no refresh policy

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var upd = Assert.Single(diff.Items);
            Assert.Equal("Table", upd.ObjectType);
            Assert.Equal("Update", upd.Action);
            Assert.Contains("refreshPolicy=", upd.LeftText);
            Assert.Contains("refreshPolicy=none", upd.RightText);
        }

        // ---- finding 1: a matched-table Update was reported Applied but NEVER copied Precedence — the round-trip shape
        // (Diff → Apply → re-Diff must be EQUAL) is what catches that false success. (It also caught a latent bug: the
        // source table was looked up by it.Table (null for a table) → Find(null) THREW; fixed to it.Name.) ----
        [Fact]
        public void CalcGroup_precedence_change_applies_and_reDiff_is_equal()   // #124 (finding 1)
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti", precedence: 10), "YTD", "a");
            var right = new TOM.Model(); CalcItem(CalcGroup(right, "TI", "t-ti", precedence: 0), "YTD", "a");
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var upd = Assert.Single(diff.Items);   // only the table Update (YTD is identical)
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { upd.Ref });
            Assert.Contains(upd.Ref, outcome.Applied);
            Assert.Empty(outcome.Failed);
            Assert.Equal(10, right.Tables.Find("TI").CalculationGroup.Precedence);          // actually copied
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);               // re-diff EQUAL — no false success
        }

        [Fact]
        public void RefreshPolicy_change_applies_and_reDiff_is_equal()   // #124 (finding 1)
        {
            var left = new TOM.Model();  var lt = Table(left, "Sales", "t-s");
            lt.RefreshPolicy = new TOM.BasicRefreshPolicy { IncrementalPeriods = 3, IncrementalGranularity = TOM.RefreshGranularityType.Month, RollingWindowPeriods = 5, RollingWindowGranularity = TOM.RefreshGranularityType.Year, SourceExpression = "let x = 1 in x" };
            var right = new TOM.Model(); Table(right, "Sales", "t-s");   // no policy
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var upd = Assert.Single(diff.Items);
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { upd.Ref });
            Assert.Contains(upd.Ref, outcome.Applied);
            Assert.Empty(outcome.Failed);
            Assert.NotNull(right.Tables.Find("Sales").RefreshPolicy);                       // actually copied
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);               // re-diff EQUAL
        }

        // A GENUINE new table from a real diff must apply (it.Table is null for a table — the source is keyed by it.Name).
        [Fact]
        public void Genuine_table_create_from_a_real_diff_applies_and_reDiff_is_equal()   // finding 1 (resolver bug)
        {
            var left = new TOM.Model();  Meas(Table(left, "Brand", "t-brand"), "Cnt", "1", "m-cnt");
            var right = new TOM.Model();   // empty → Brand is a genuine Create
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var create = Assert.Single(diff.Items.Where(i => i.ObjectType == "Table" && i.Action == "Create"));
            var outcome = ModelCompare.Apply(left, right, diff, null);   // apply all
            Assert.Contains(create.Ref, outcome.Applied);   // pre-fix: Find(it.Table=null) THREW → Failed "Value cannot be null (key)"
            Assert.Empty(outcome.Failed);
            Assert.NotNull(right.Tables.Find("Brand"));
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);   // re-diff EQUAL
        }

        // A plain-table ↔ calc-group KIND transition is a structural rebuild — Apply must REJECT it honestly (never a
        // half-apply that claims success), so the item lands in Failed with a clear reason and the table is untouched.
        [Fact]
        public void Table_kind_transition_update_is_rejected_honestly()   // #124 (finding 1)
        {
            var left = new TOM.Model();  CalcItem(CalcGroup(left, "TI", "t-ti"), "YTD", "a");   // calc-group table
            var right = new TOM.Model(); Table(right, "TI", "t-ti");                             // SAME tag, PLAIN table
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var tableUpd = Assert.Single(diff.Items.Where(i => i.ObjectType == "Table" && i.Action == "Update"));
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { tableUpd.Ref });
            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == tableUpd.Ref && f.Reason.Contains("changed kind"));
            Assert.Null(right.Tables.Find("TI").CalculationGroup);   // never half-mutated into a calc group
        }

        // ---- finding 3: RefreshPolicyText concatenates SourceExpression + PollingExpression UNESCAPED, so two DISTINCT
        // policies serialize identically → a false-EQUAL. Equality must use the TYPED fields (RefreshPolicyEqual). ----
        [Fact]
        public void RefreshPolicy_unescaped_concat_collision_is_still_a_diff()   // #124 (finding 3)
        {
            // Both render as "…;source=x;polling=y;polling=z" under the old concat, but are DIFFERENT policies:
            var left = new TOM.Model();  var lt = Table(left, "Sales", "t-s");
            lt.RefreshPolicy = new TOM.BasicRefreshPolicy { SourceExpression = "x;polling=y", PollingExpression = "z" };
            var right = new TOM.Model(); var rt = Table(right, "Sales", "t-s");
            rt.RefreshPolicy = new TOM.BasicRefreshPolicy { SourceExpression = "x", PollingExpression = "y;polling=z" };
            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            Assert.Contains(diff.Items, i => i.ObjectType == "Table" && i.Action == "Update");   // pre-fix: ZERO items (false-EQUAL)
        }

        // ============================================================================================
        // #135: table/model/calc-group CONTAINER properties were outside the collection walker. These
        // pins prove detection AND the only safe success contract: Diff → Apply → re-Diff is EQUAL.
        // ============================================================================================
        [Fact]
        public void Detail_rows_definition_is_a_table_update_that_round_trips()   // #135
        {
            var left = new TOM.Model(); var lt = Table(left, "Sales", "t-s");
            lt.DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "SELECTCOLUMNS ( Sales, \"Amount\", Sales[Amount] )" };
            var right = new TOM.Model(); Table(right, "Sales", "t-s");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = Assert.Single(diff.Items, i => i.ObjectType == "Table" && i.Action == "Update");
            Assert.Contains("defaultDetailRowsDefinition", update.LeftText);
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { update.Ref });

            Assert.Contains(update.Ref, outcome.Applied);
            Assert.Empty(outcome.Failed);
            Assert.Equal(lt.DefaultDetailRowsDefinition.Expression, right.Tables["Sales"].DefaultDetailRowsDefinition.Expression);
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);
        }

        [Fact]
        public void Table_and_model_annotations_are_visible_and_round_trip_but_audit_annotations_are_ignored()   // #135
        {
            var left = new TOM.Model(); var lt = Table(left, "Sales", "t-s");
            left.Annotations.Add(new TOM.Annotation { Name = "Owner", Value = "Finance" });
            left.Annotations.Add(new TOM.Annotation { Name = "Semanticus_VerifiedEdits", Value = "left-chain" });
            left.Annotations.Add(new TOM.Annotation { Name = "TabularEditor_SerializeOptions", Value = "left-tool" });
            lt.Annotations.Add(new TOM.Annotation { Name = "ToolHint", Value = "keep" });
            var right = new TOM.Model(); Table(right, "Sales", "t-s");
            right.Annotations.Add(new TOM.Annotation { Name = "Semanticus_VerifiedEdits", Value = "right-chain" });
            right.Annotations.Add(new TOM.Annotation { Name = "TabularEditor_SerializeOptions", Value = "right-tool" });
            right.Annotations.Add(new TOM.Annotation { Name = "Stale", Value = "remove" });

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var model = Assert.Single(diff.Items, i => i.ObjectType == "Model");
            var table = Assert.Single(diff.Items, i => i.ObjectType == "Table");
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { model.Ref, table.Ref });

            Assert.Empty(outcome.Failed);
            Assert.Equal("Finance", right.Annotations["Owner"].Value);
            Assert.Equal("right-chain", right.Annotations["Semanticus_VerifiedEdits"].Value);   // audit is ride-along, not diff/apply state
            Assert.Equal("right-tool", right.Annotations["TabularEditor_SerializeOptions"].Value);
            Assert.False(right.Annotations.Contains("Stale"));
            Assert.Equal("keep", right.Tables["Sales"].Annotations["ToolHint"].Value);
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);
        }

        [Fact]
        public void Calc_group_selection_expressions_are_visible_and_round_trip()   // #135
        {
            var left = new TOM.Model(); var lt = CalcGroup(left, "TI", "t-ti"); CalcItem(lt, "YTD", "SELECTEDMEASURE ()");
            lt.CalculationGroup.NoSelectionExpression = new TOM.CalculationGroupExpression
            {
                Expression = "SELECTEDMEASURE ()",
                FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "\"0.0%\"" }
            };
            lt.CalculationGroup.MultipleOrEmptySelectionExpression = new TOM.CalculationGroupExpression { Expression = "BLANK ()" };
            var right = new TOM.Model(); var rt = CalcGroup(right, "TI", "t-ti"); CalcItem(rt, "YTD", "SELECTEDMEASURE ()");

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = Assert.Single(diff.Items, i => i.ObjectType == "Table");
            Assert.Contains("noSelectionExpression", update.LeftText);
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { update.Ref });

            Assert.Empty(outcome.Failed);
            Assert.Equal("SELECTEDMEASURE ()", rt.CalculationGroup.NoSelectionExpression.Expression);
            Assert.Equal("\"0.0%\"", rt.CalculationGroup.NoSelectionExpression.FormatStringDefinition.Expression);
            Assert.Equal("BLANK ()", rt.CalculationGroup.MultipleOrEmptySelectionExpression.Expression);
            Assert.Empty(ModelCompare.Diff(left, right, "src", "tgt").Items);
        }

        [Fact]
        public void Unsupported_model_shell_metadata_is_refused_before_mutation()   // #135
        {
            var left = new TOM.Model(); var right = new TOM.Model();
            var property = typeof(TOM.Model).GetProperty("DisableAutoExists");
            Assert.NotNull(property); Assert.True(property.CanWrite);
            var originalTargetValue = property.GetValue(right);
            property.SetValue(left, 1);

            var diff = ModelCompare.Diff(left, right, "src", "tgt");
            var update = Assert.Single(diff.Items, i => i.ObjectType == "Model");
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { update.Ref });

            Assert.Empty(outcome.Applied);
            Assert.Contains(outcome.Failed, f => f.Ref == update.Ref && f.Reason.Contains("cannot copy yet"));
            Assert.Equal(originalTargetValue, property.GetValue(right));
            Assert.Single(ModelCompare.Diff(left, right, "src", "tgt").Items, i => i.ObjectType == "Model");
        }

        [Fact]
        public void Container_property_catalog_is_pinned_so_a_TOM_bump_cannot_silently_expand_the_shell()   // #135
        {
            static string[] Props(Type t) => t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] {
                "AlternateSourcePrecedence", "Annotations", "CalculationGroup", "Calendars", "ChangedProperties", "Columns",
                "DataCategory", "DefaultDetailRowsDefinition", "Description", "DirectLakeIndexingBehavior", "ExcludedArtifacts",
                "ExcludeFromAutomaticAggregations", "ExcludeFromModelRefresh", "ExtendedProperties", "Hierarchies", "IsHidden",
                "IsPrivate", "IsRemoved", "LineageTag", "Measures", "Model", "ModifiedTime", "Name", "ObjectType", "Parent",
                "Partitions", "RefreshPolicy", "Sets", "ShowAsVariationsOnly", "SourceLineageTag", "StructureModifiedTime", "SystemManaged"
            }.OrderBy(n => n, StringComparer.Ordinal), Props(typeof(TOM.Table)));
            Assert.Equal(new[] {
                "AnalyticsAIMetadata", "Annotations", "AutomaticAggregationOptions", "BindingInfoCollection", "Collation", "Culture",
                "Cultures", "DataAccessOptions", "Database", "DataSourceDefaultMaxConnections", "DataSources",
                "DataSourceVariablesOverrideBehavior", "DefaultDataView", "DefaultDirectLakeIndexingBehavior", "DefaultMeasure",
                "DefaultMode", "DefaultPowerBIDataSourceVersion", "Description", "DirectLakeBehavior", "DisableAutoExists",
                "DiscourageCompositeModels", "DiscourageImplicitMeasures", "DiscourageReportMeasures", "ExcludedArtifacts",
                "Expressions", "ExtendedProperties", "ForceUniqueNames", "Functions", "HasLocalChanges", "IsRemoved", "MAttributes",
                "MaxParallelismPerQuery", "MaxParallelismPerRefresh", "MetadataAccessPolicy", "Model", "ModifiedTime", "Name",
                "ObjectType", "Parent", "Perspectives", "QueryGroups", "Relationships", "Roles", "SelectionExpressionBehavior",
                "Server", "SourceQueryCulture", "StorageLocation", "StructureModifiedTime", "Tables", "ValueFilterBehavior"
            }.OrderBy(n => n, StringComparer.Ordinal), Props(typeof(TOM.Model)));
            Assert.Equal(new[] {
                "Annotations", "CalculationItems", "Description", "IsRemoved", "Model", "ModifiedTime",
                "MultipleOrEmptySelectionExpression", "NoSelectionExpression", "ObjectType", "Parent", "Precedence", "Table"
            }.OrderBy(n => n, StringComparer.Ordinal), Props(typeof(TOM.CalculationGroup)));
        }

        // ============================================================================================
        // #120 — CloneNamed used raw TOM .Clone(), which copies the LineageTag verbatim; adding the clone into a
        // collection that ALREADY holds that tag on a DIFFERENT object throws ArgumentException (tags are unique
        // per-collection). The fix clears the clone's tag ON AN ACTUAL COLLISION only (fresh identity) so the add
        // succeeds; a non-colliding tag is preserved (rename-safety).
        // ============================================================================================
        [Fact]
        public void Apply_create_clears_a_colliding_lineage_tag_and_keeps_the_incumbent()   // #120
        {
            var left = new TOM.Model();  Meas(Table(left, "Sales", "t-s"), "Newbie", "2", "dup");
            var right = new TOM.Model(); Meas(Table(right, "Sales", "t-s"), "Existing", "1", "dup");   // a DIFFERENT object already holds "dup"

            // Hand-built Create of "Newbie" (tag dup) onto Sales — mirrors a cross-model copy / stale-diff apply where the
            // target INDEPENDENTLY carries the same tag (a clean Diff would tag-pair them; the collision is the copy path).
            var create = new ModelDiffItem { Ref = "measure:Sales/Newbie", ObjectType = "Measure", Name = "Newbie", Table = "Sales", Action = "Create", TargetTable = "Sales", TargetTableTag = "t-s" };
            var diff = new ModelDiff { Items = new[] { create } };

            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { create.Ref });
            Assert.Contains(create.Ref, outcome.Applied);       // pre-fix: Add throws (dup tag) → Failed
            Assert.Empty(outcome.Failed);
            var added = right.Tables.Find("Sales").Measures.Find("Newbie");
            Assert.NotNull(added);
            Assert.True(string.IsNullOrEmpty(added.LineageTag));                                  // fresh identity
            Assert.Equal("dup", right.Tables.Find("Sales").Measures.Find("Existing").LineageTag); // the incumbent kept its tag
        }

        [Fact]
        public void Apply_create_preserves_a_non_colliding_lineage_tag()   // #120 (rename-safety: only clear on an ACTUAL collision)
        {
            var left = new TOM.Model();  Meas(Table(left, "Sales", "t-s"), "Newbie", "2", "fresh-tag");
            var right = new TOM.Model(); Table(right, "Sales", "t-s");   // no measures — no collision

            var create = new ModelDiffItem { Ref = "measure:Sales/Newbie", ObjectType = "Measure", Name = "Newbie", Table = "Sales", Action = "Create", TargetTable = "Sales", TargetTableTag = "t-s" };
            var diff = new ModelDiff { Items = new[] { create } };
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { create.Ref });
            Assert.Contains(create.Ref, outcome.Applied);
            Assert.Equal("fresh-tag", right.Tables.Find("Sales").Measures.Find("Newbie").LineageTag);   // preserved
        }

        // The #120 fix on the TABLE-create path: cherry-picking a table whose LineageTag already sits on a DIFFERENT
        // table in the destination must clear the clone's tag (fresh identity), not throw the raw duplicate-tag
        // ArgumentException. (A clean Diff tag-pairs same-tag tables as a rename; the collision is the cross-model copy
        // path — a hand-built Create, exactly like cherry_pick emits.)
        [Fact]
        public void Apply_table_create_clears_a_colliding_lineage_tag_and_keeps_the_incumbent()   // #120 (table-create path)
        {
            var left = new TOM.Model();  Table(left, "NewTbl", "dup");
            var right = new TOM.Model(); Table(right, "Existing", "dup");   // a DIFFERENT table already holds "dup"
            var create = new ModelDiffItem { Ref = "table:NewTbl", ObjectType = "Table", Name = "NewTbl", Table = "NewTbl", Action = "Create" };
            var diff = new ModelDiff { Items = new[] { create } };
            var outcome = ModelCompare.Apply(left, right, diff, new HashSet<string> { create.Ref });
            Assert.Contains(create.Ref, outcome.Applied);       // pre-fix: raw .Clone()+Add throws dup-tag → Failed
            Assert.Empty(outcome.Failed);
            var added = right.Tables.Find("NewTbl");
            Assert.NotNull(added);
            Assert.True(string.IsNullOrEmpty(added.LineageTag));                          // fresh identity
            Assert.Equal("dup", right.Tables.Find("Existing").LineageTag);               // incumbent kept its tag
        }
    }
}
