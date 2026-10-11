using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/psc-bugs: the defects the Plan statement check (second run) found, beyond the two tests it left failing
    /// (VerifyPlanStatementCheck2Tests). A first save never writes into a folder that already holds something; an acceptance of a
    /// relationship that cannot follow a rename is counted as orphaned; a Plan row whose every line is dropped is not ready;
    /// FMT-SORTBY leaves a Y/N flag alone.
    /// </summary>
    public sealed class PscBugsTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public PscBugsTests(ModelTemplates templates) => _templates = templates;

        private static string Scratch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pscb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---- item 1: a first save never lands in a folder that already holds something ---------------------------------

        /// <summary>A model named like a folder the openers read ("definition") is not saved under that name: the note says the name
        /// is RESERVED, not taken, because nothing was ever there.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_reserved_model_name_is_called_reserved_in_the_note_not_taken()
        {
            var root = Scratch();
            try
            {
                File.WriteAllText(Path.Combine(root, "notes.txt"), "mine");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("definition", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(root, "TMDL", false, "human");
                Assert.Equal(Path.Combine(root, "definition 2"), saved.Path);
                Assert.Contains("'definition' was reserved", saved.Note);
                Assert.DoesNotContain("was taken", saved.Note);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_first_save_into_a_folder_with_other_files_goes_into_a_new_folder_named_after_the_model()
        {
            var root = Scratch();
            try
            {
                var note = Path.Combine(root, "notes.txt");
                File.WriteAllText(note, "mine");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");

                var saved = await f.Engine.SaveAsync(root, "TMDL", false, "human");

                Assert.Equal(Path.Combine(root, "Brand New"), saved.Path);
                Assert.True(File.Exists(Path.Combine(saved.Path, "model.tmdl")));
                Assert.False(File.Exists(Path.Combine(root, "model.tmdl")), "model files were left loose in the chosen folder");
                Assert.Equal("mine", File.ReadAllText(note));

                // The model's home is now that folder: a save with no path goes back to it, never to a second new folder.
                await f.Engine.CreateTableAsync("Second", "human");
                var again = await f.Engine.SaveAsync(null, "TMDL", false, "human");
                Assert.Equal(saved.Path, again.Path);
                Assert.True(File.Exists(Path.Combine(saved.Path, "tables", "Second.tmdl")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_first_save_into_an_empty_folder_uses_that_folder()
        {
            var root = Scratch();
            try
            {
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(root, "TMDL", false, "human");
                Assert.Equal(Path.GetFullPath(root), saved.Path);
                Assert.True(File.Exists(Path.Combine(root, "model.tmdl")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_save_takes_the_next_number_when_the_models_own_folder_name_is_already_taken_by_files()
        {
            var root = Scratch();
            try
            {
                var taken = Path.Combine(root, "Brand New");
                Directory.CreateDirectory(taken);
                File.WriteAllText(Path.Combine(taken, "other.tmdl"), "table Other\n");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(root, "TMDL", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New 2"), saved.Path);
                Assert.Contains("Brand New 2", saved.Note);
                Assert.Equal("table Other\n", File.ReadAllText(Path.Combine(taken, "other.tmdl")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_first_save_pointed_at_another_projects_pbip_file_goes_beside_it_and_that_project_is_untouched()
        {
            var root = Scratch();
            try
            {
                var def = Path.Combine(root, "Existing.SemanticModel", "definition");
                Directory.CreateDirectory(def);
                File.WriteAllText(Path.Combine(root, "Existing.pbip"), "{}");
                File.WriteAllText(Path.Combine(def, "database.tmdl"), "database Existing\n\tcompatibilityLevel: 1604\n");
                File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
                var before = File.ReadAllText(Path.Combine(def, "model.tmdl"));
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(Path.Combine(root, "Existing.pbip"), "TMDL", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New"), saved.Path);
                Assert.Contains("Existing.pbip", saved.Note);
                Assert.Equal(before, File.ReadAllText(Path.Combine(def, "model.tmdl")));
                Assert.False(File.Exists(Path.Combine(def, "tables", "Only.tmdl")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ---- item 1, round 2: every format, every path ----------------------------------------------------------------------

        private static async Task<DecisionFixture> NewModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Brand New", 1604);
            await f.Engine.CreateTableAsync("Only", "human");
            return f;
        }

        [Fact]
        public async Task A_first_save_over_an_existing_bim_goes_beside_it_unless_the_person_chose_that_file_in_a_dialog()
        {
            var root = Scratch();
            try
            {
                var bim = Path.Combine(root, "model.bim");
                File.WriteAllText(bim, "{\"name\":\"Mine\"}");
                using var f = await NewModelAsync();
                var beside = await f.Engine.SaveAsync(bim, "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New.bim"), beside.Path);
                Assert.Contains("already exists", beside.Note);
                // The agent door passes no dialog flag and may pass overwrite: still redirected, file kept.
                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                var viaAgent = await McpTools.SaveModel(f.Engine, bim, "bim", true);
                Assert.Equal(Path.Combine(root, "Brand New 2.bim"), viaAgent.Path);
                Assert.Equal("{\"name\":\"Mine\"}", File.ReadAllText(bim));
                // The person's dialog names the file and confirms the replace; then it is written.
                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(bim, "BIM", false, "human", true);
                Assert.Equal(Path.GetFullPath(bim), saved.Path);
                Assert.Contains("Brand New", File.ReadAllText(bim));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_bim_save_into_a_folder_with_files_goes_into_a_new_bim_named_after_the_model()
        {
            var root = Scratch();
            try
            {
                File.WriteAllText(Path.Combine(root, "notes.txt"), "mine");
                using var f = await NewModelAsync();
                var saved = await f.Engine.SaveAsync(root, "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New.bim"), saved.Path);
                Assert.True(File.Exists(saved.Path));
                Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "notes.txt")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Theory]
        [InlineData("tmdlroot", "TMDL")]
        public Task A_first_save_into_a_model_folder_or_inside_one_is_redirected_next_to_it_and_nothing_of_it_changes_sample(string aim, string format) => A_first_save_into_a_model_folder_or_inside_one_is_redirected_next_to_it_and_nothing_of_it_changes(aim, format);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("tmdlroot", "TMDL")]
        [InlineData("tmdlroot", "BIM")]
        [InlineData("insideenvelope", "TMDL")]
        [InlineData("newbiminenvelope", "BIM")]
        [InlineData("newbiminenvelope", "TMDL")]
        public async Task A_first_save_into_a_model_folder_or_inside_one_is_redirected_next_to_it_and_nothing_of_it_changes(string aim, string format)
        {
            var root = Scratch();
            try
            {
                var flat = Path.Combine(root, "flat");
                Directory.CreateDirectory(flat);
                File.WriteAllText(Path.Combine(flat, "model.tmdl"), "model Model\n");
                var env = Path.Combine(root, "Existing.SemanticModel");
                Directory.CreateDirectory(env);
                var path = aim switch
                {
                    "tmdlroot" => flat,
                    "insideenvelope" => Path.Combine(env, "fresh"),
                    _ => Path.Combine(env, "new.bim"),
                };
                string Own(string d) => string.Join("|", Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal));
                var home = aim == "tmdlroot" ? flat : env;
                var before = Own(home);
                using var f = await NewModelAsync();
                var saved = await f.Engine.SaveAsync(path, format, false, "human");
                Assert.Equal(before, Own(home));
                Assert.Equal(root, Path.GetDirectoryName(saved.Path));
                Assert.Contains("next to '" + Path.GetFileName(home) + "', so that model is left as it was", saved.Note);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ---- item 2: an acceptance that cannot follow is counted ------------------------------------------------------------

        [Fact]
        public async Task An_older_relationship_acceptance_whose_tables_were_renamed_is_counted_as_orphaned()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Psc2b", 1604);
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "DUE_DT", "DateTime", null, "human");
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/INV_DT", "column:dim_date/CAL_DT", null, true, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/DUE_DT", "column:dim_date/CAL_DT", null, false, "human");

            // A record written before relationships were followed by identity: a name, no tag, for a relationship that is no longer called that.
            var stale = JsonSerializer.Serialize(new[]
            {
                new WaiverRecord { System = "air", RuleId = "REL-INACTIVE", ObjectRef = "relationship:'old_fact'[DUE_DT] --> 'old_date'[CAL_DT]", Reason = "On purpose.", By = "human", When = DateTime.UtcNow.ToString("o") },
            });
            await f.Sessions.Require().MutateAsync("human", "test setup", m => ((IAnnotationObject)m).SetAnnotation(WaiverStore.Annotation, stale));

            var scan = await e.AiReadinessScanAsync();
            Assert.Equal(1, scan.OrphanedWaiverCount);
            Assert.False(scan.Findings.Single(x => x.RuleId == "REL-INACTIVE").Waived);
        }

        // ---- item 7: a Y/N flag is not a label that needs a sort order ---------------------------------------------------

        private static RuleEvaluation SortByEval(SessionManager sessions)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return ReadinessRuleSet.Default().Single(r => r.Id == "FMT-SORTBY").Evaluate(sessions.Current.Model);
        }

        [Theory]
        [InlineData("IsWeekday")]
        public Task A_yes_no_flag_is_not_counted_by_the_sort_order_rule_sample(string name) => A_yes_no_flag_is_not_counted_by_the_sort_order_rule(name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Is Weekday")]
        [InlineData("IsWeekday")]
        [InlineData("is_weekday")]
        [InlineData("Has Month End")]
        [InlineData("Weekday Flag")]
        public async Task A_yes_no_flag_is_not_counted_by_the_sort_order_rule(string name)
        {
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.CreateModelAsync("SortFlag", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", name, "String", name, "human");
            var eval = SortByEval(sessions);
            Assert.Equal(0, eval.Applicable);
            Assert.Empty(eval.Violations);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_real_weekday_label_still_needs_a_sort_order()
        {
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.CreateModelAsync("SortLabel", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Weekday Name", "String", "Weekday Name", "human");
            await e.CreateColumnAsync("table:Date", "Is Weekday", "String", "Is Weekday", "human");
            var eval = SortByEval(sessions);
            Assert.Equal(1, eval.Applicable);
            Assert.Single(eval.Violations);
            Assert.Equal("Weekday Name", eval.Violations[0].ObjectName);
        }

        // ---- item 4: a row whose every line is dropped is not ready --------------------------------------------------------

        /// <summary>Jev answering the folder question with a folder it is not sure of: the line arrives dropped, because an unsure answer is the person's to keep.</summary>
        private sealed class UnsureAboutTheFolder : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) =>
                Task.FromResult(BatchedByName.Reply(request, (plain, _) =>
                    plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":\"folder:Counts\",\"confidence\":0.2}" : null));
        }

        [Theory]
        [InlineData("studio")]
        public Task A_row_whose_every_line_arrives_dropped_is_not_ready_and_run_the_plan_does_not_offer_it_again_sample(string door) => A_row_whose_every_line_arrives_dropped_is_not_ready_and_run_the_plan_does_not_offer_it_again(door);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_row_whose_every_line_arrives_dropped_is_not_ready_and_run_the_plan_does_not_offer_it_again(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            // Lean suite: the model is built once for the class and each row opens a copy of the saved file.
            await _templates.OpenCopyAsync(f, "staff", async b =>
            {
                var be = b.Engine;
                await be.CreateModelAsync("Psc4", 1702);
                await be.CreateTableAsync("Staff", "human");
                await be.CreateColumnAsync("table:Staff", "Rate", "Decimal", "Rate", "human");
                var a0 = await be.CreateMeasureAsync("table:Staff", "Anchor", "COUNTROWS ( Staff )", "human");
                await be.SetMeasureFormatAsync(a0, "#,0", "human");
                await be.SetObjectPropertyAsync(a0, "DisplayFolder", "Counts", "human");
                var v0 = await be.CreateMeasureAsync("table:Staff", "Average Standard Rate", "AVERAGE ( Staff[Rate] )", "human");
                await be.SetMeasureFormatAsync(v0, "0.00", "human");
                // The words are written, so the row under test does not wait for the words row.
                await be.SetDescriptionAsync("table:Staff", "The people who do the work.", "human");
                await be.SetDescriptionAsync("column:Staff/Rate", "What one hour of the person costs.", "human");
                await be.SetDescriptionAsync(a0, "How many people there are.", "human");
                await be.SetDescriptionAsync(v0, "The mean of the standard hourly rate.", "human");
            }, "Psc4.bim");
            await f.ConsentAsync();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new UnsureAboutTheFolder(), new DecisionBudget()));

            async Task<ModelPlanView> Settled()
            {
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                return door == "mcp" ? await McpTools.GetModelPlan(e) : await new EngineRpcTarget(e).getModelPlan();
            }

            var plan = await Settled();
            var row = plan.Items.Single(i => i.Id == "jev.measures.table:Staff");
            var all = string.Join(", ", plan.Items.Select(i => i.Id + "=" + i.StateWord));
            Assert.NotEmpty(row.ChangeSet.Lines);
            Assert.All(row.ChangeSet.Lines, l => Assert.False(l.Kept));
            Assert.True(row.StateWord != "ready", "the row is offered to Run the plan with every line dropped: " + all);
            Assert.False(string.IsNullOrWhiteSpace(row.StateNote), "the row says why it is not ready");

            // Run the plan never takes it, and never names it as a row it left out.
            var run = door == "mcp"
                ? DoorResult.AsItems(await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest))
                : await new EngineRpcTarget(e).applyPlanItems(null, "human", plan.ReadyDigest);
            Assert.DoesNotContain("Staff", run.Sentence ?? "");
            Assert.DoesNotContain(run.Skipped ?? Array.Empty<ApplyPlanSkip>(), x => x.ItemId == "jev.measures.table:Staff");

            // Ticking the line is the person's own choice, and the row is the run's again.
            plan = await Settled();
            row = plan.Items.Single(i => i.Id == "jev.measures.table:Staff");
            await new EngineRpcTarget(e).setPlanItem(row.ChangeSet.Lines[0].PlanItemId, null, true);
            plan = await Settled();
            Assert.Equal("ready", plan.Items.Single(i => i.Id == "jev.measures.table:Staff").StateWord);
        }
    }
    /// <summary>
    /// m11/psc-bugs round 3: the guard against a first save landing in another model is ONE rule (walk up from the target to the
    /// filesystem root, refuse if any folder on the way is a model the openers can open), and its list of shapes comes from
    /// <see cref="ModelPathResolver"/>, which mirrors the code that opens models. These tests fail the moment an opener learns a
    /// shape the list does not know, and prove the walk for every shape in the list at any depth.
    /// </summary>
    public sealed class PscBugsShapeTests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public PscBugsShapeTests(DirTemplate tpl) => _tpl = tpl;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        private static string Scratch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pscs-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Every name an opener could look for: any quoted file-like name (letters, digits, dash, underscore, then a dot and an
        /// extension, with no space) and the folder name "definition". Not a list of extensions: an opener that learns an old
        /// <c>.Dataset</c> envelope or a bare <c>model.bim</c> must show up here whatever it is called.</summary>
        private static System.Collections.Generic.SortedSet<string> NamesIn(string source)
        {
            var found = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                "\"(\\*?[A-Za-z0-9_-]*\\.[A-Za-z][A-Za-z0-9]*|definition)\""))
                found.Add(m.Groups[1].Value.TrimStart('*'));    // "*.pbip" and ".pbip" are the same name; "model.tmdl" is a whole name
            return found;
        }

        /// <summary>Every name the opener code mentions: TE2's handler, the split-folder loader and <c>ModelPathResolver.Resolve</c>.</summary>
        private static System.Collections.Generic.SortedSet<string> OpenerSourceNames()
        {
            var root = RepoRoot();
            var te = File.ReadAllText(Path.Combine(root, "external", "TabularEditor", "TOMWrapper", "TOMWrapper", "TabularModelHandler.FileHandling.cs"));
            var split = File.ReadAllText(Path.Combine(root, "Semanticus.Core", "Compat", "SplitModelSerializer.LinuxPaths.cs"));
            var resolverSource = File.ReadAllText(Path.Combine(root, "Semanticus.Engine", "ModelPathResolver.cs"));
            var from = resolverSource.IndexOf("public static string Resolve(", StringComparison.Ordinal);
            var to = resolverSource.IndexOf("private static string DefinitionUnderProjectFolder(", StringComparison.Ordinal);
            Assert.True(from > 0 && to > from, "ModelPathResolver.Resolve moved: update this test to read the opener's body");
            var seen = NamesIn(te);
            seen.UnionWith(NamesIn(split));
            seen.UnionWith(NamesIn(resolverSource.Substring(from, to - from)));
            // Quoted JSON property paths that look like file names to the pattern; they name no file or folder.
            seen.ExceptWith(new[] { "PerspectiveCollection.SerializedPerspective", "calculationGroup.calculationItems" });
            return seen;
        }

        [Fact]
        public void Every_name_an_opener_looks_for_is_a_shape_the_save_guard_knows_or_is_explained_as_not_one()
        {
            var split = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Core", "Compat", "SplitModelSerializer.LinuxPaths.cs"));
            var seen = OpenerSourceNames();
            // The scan must actually be reading the openers: these have been in them since the guard was written.
            Assert.Contains("database.json", seen);
            Assert.Contains("database.tmdl", seen);
            Assert.Contains(".pbip", seen);

            var known = ModelPathResolver.OpenerNames.Concat(ModelPathResolver.OpenerNamesThatAreNotFolderShapes.Select(x => x.Name)).ToHashSet(StringComparer.Ordinal);
            var unknown = seen.Where(n => !known.Contains(n)).ToArray();
            Assert.True(unknown.Length == 0,
                "an opener now looks for [" + string.Join(", ", unknown) + "], which the first-save guard does not know. Teach ModelPathResolver.ShapeOf the shape and add the name to " +
                "OpenerNames (or explain it in OpenerNamesThatAreNotFolderShapes), so a first save is redirected away from it.");
            foreach (var (name, why) in ModelPathResolver.OpenerNamesThatAreNotFolderShapes) Assert.False(string.IsNullOrWhiteSpace(why), name + " has no reason");

            // The folders the Tabular Editor loader reads by name from a split folder's root are all listed.
            var parts = System.Text.RegularExpressions.Regex.Matches(split, "InArray\\(path,\\s*\"(\\w+)\"").Select(m => m.Groups[1].Value).Append("tables").Distinct().ToArray();
            Assert.True(parts.Length >= 6, "the loader's folder list was not read: " + string.Join(", ", parts));
            var missing = parts.Where(x => !ModelPathResolver.TabularEditorFolderParts.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
            Assert.True(missing.Length == 0, "the Tabular Editor loader reads [" + string.Join(", ", missing) + "] from a folder's root and ModelPathResolver.TabularEditorFolderParts does not list it");
        }

        /// <summary>The same list, asked of the opener itself: for each name, build the smallest folder that carries it and open it with
        /// the real handler. Whatever the opener takes as a model, the guard must know. A bare folder is the control that proves the
        /// probe can tell the two apart.</summary>
        [Fact]
        public void A_folder_the_real_opener_takes_as_a_model_is_one_the_save_guard_knows()
        {
            var root = Scratch();
            try
            {
                static bool OpenerRecognises(string dir)
                {
                    if (!string.Equals(ModelPathResolver.Resolve(dir), dir, StringComparison.Ordinal)) return true;
                    try { using var h = new TabularModelHandler(dir); return true; }
                    catch (Exception ex) { return !ex.Message.Contains("does not contain a database.json", StringComparison.Ordinal); }
                }
                var bare = Path.Combine(root, "bare"); Directory.CreateDirectory(bare);
                Assert.False(OpenerRecognises(bare), "the probe cannot tell a bare folder from a model; it proves nothing");
                Assert.Equal(ModelPathResolver.ModelShape.None, ModelPathResolver.ShapeOf(bare));

                var builds = new System.Collections.Generic.Dictionary<string, Action<string>>
                {
                    ["database.tmdl"] = d => File.WriteAllText(Path.Combine(d, "database.tmdl"), "database X\n"),
                    ["database.tmd"] = d => File.WriteAllText(Path.Combine(d, "database.tmd"), "database X\n"),
                    ["model.tmdl"] = d => File.WriteAllText(Path.Combine(d, "model.tmdl"), "model Model\n"),
                    ["model.tmd"] = d => File.WriteAllText(Path.Combine(d, "model.tmd"), "model Model\n"),
                    ["definition"] = d => { Directory.CreateDirectory(Path.Combine(d, "definition")); File.WriteAllText(Path.Combine(d, "definition", "model.tmdl"), "model Model\n"); },
                    [".pbip"] = d => File.WriteAllText(Path.Combine(d, "x.pbip"), "{}"),
                    ["definition.pbism"] = d => File.WriteAllText(Path.Combine(d, "definition.pbism"), "{}"),
                    ["database.json"] = d => File.WriteAllText(Path.Combine(d, "database.json"), "{}"),
                };
                // A name in the list with no build here is a name this probe cannot test: add one.
                foreach (var n in ModelPathResolver.OpenerNames.Where(n => n != ".SemanticModel" && n != ".bim"))
                    Assert.True(builds.ContainsKey(n), "no probe is built for the opener name '" + n + "'");
                foreach (var (name, build) in builds)
                {
                    var dir = Path.Combine(root, "p-" + name.Replace('.', '_'));
                    Directory.CreateDirectory(dir);
                    build(dir);
                    if (OpenerRecognises(dir))
                        Assert.True(ModelPathResolver.ShapeOf(dir) != ModelPathResolver.ModelShape.None, "the opener takes a folder carrying '" + name + "' as a model; the guard does not");
                }
                // Every other name the opener code mentions, and every name on either list, is probed too, not only the ones built by
                // hand above: a name an opener learns to take (an old .Dataset envelope, a bare model.bim) must fail here, not pass
                // because nobody wrote its probe. Each name is tried as a file, as a folder holding a TMDL root and as a folder holding
                // a definition/ TMDL root, all inside a folder of their own.
                var every = OpenerSourceNames().Concat(ModelPathResolver.OpenerNames).Concat(ModelPathResolver.OpenerNamesThatAreNotFolderShapes.Select(x => x.Name)).Distinct();
                var probed = 0;
                foreach (var n in every)
                {
                    var leaf = n.StartsWith('.') ? "x" + n : n;
                    var variants = new (string Label, Action<string> Build)[]
                    {
                        ("a file", d => File.WriteAllText(Path.Combine(d, leaf), "{}")),
                        ("a folder with a TMDL root", d => { Directory.CreateDirectory(Path.Combine(d, leaf)); File.WriteAllText(Path.Combine(d, leaf, "model.tmdl"), "model Model\n"); }),
                        ("a folder with a definition/ TMDL root", d => { Directory.CreateDirectory(Path.Combine(d, leaf, "definition")); File.WriteAllText(Path.Combine(d, leaf, "definition", "model.tmdl"), "model Model\n"); }),
                    };
                    foreach (var (label, build) in variants)
                    {
                        var dir = Path.Combine(root, "g" + (++probed));
                        Directory.CreateDirectory(dir);
                        try { build(dir); } catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { continue; }
                        if (OpenerRecognises(dir))
                            Assert.True(ModelPathResolver.ShapeOf(dir) != ModelPathResolver.ModelShape.None, "the opener takes a folder carrying '" + n + "' (" + label + ") as a model; the guard does not");
                    }
                }
                Assert.True(probed >= 30, "the name probe ran for too few names: " + probed);
                // The two shapes that are a folder NAME, not a file in it.
                var sm = Path.Combine(root, "X.SemanticModel"); Directory.CreateDirectory(sm);
                Assert.True(ModelPathResolver.ShapeOf(sm).HasFlag(ModelPathResolver.ModelShape.SemanticModelFolder));
                var bimDir = Path.Combine(root, "X.bim"); Directory.CreateDirectory(bimDir);
                Assert.True(ModelPathResolver.ShapeOf(bimDir).HasFlag(ModelPathResolver.ModelShape.BimNamedFolder));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        public static readonly object[][] Shapes =
        {
            new object[] { "TMDL root (model.tmdl)", "m" },
            new object[] { "TMDL root (database.tmdl)", "d" },
            new object[] { "definition folder", "def" },
            new object[] { "SemanticModel folder", "sm" },
            new object[] { "project (.pbip)", "pbip" },
            new object[] { "semantic model marker (definition.pbism)", "pbism" },
            new object[] { "Tabular Editor folder that is also a TMDL root", "te+tmdl" },
            new object[] { "folder named like a .bim", "bimdir" },
        };

        private static string MakeShape(string where, string key)
        {
            Directory.CreateDirectory(where);
            switch (key)
            {
                case "m": File.WriteAllText(Path.Combine(where, "model.tmdl"), "model Model\n"); break;
                case "d": File.WriteAllText(Path.Combine(where, "database.tmdl"), "database X\n"); break;
                case "def": Directory.CreateDirectory(Path.Combine(where, "definition")); File.WriteAllText(Path.Combine(where, "definition", "model.tmdl"), "model Model\n"); break;
                case "sm": break; // the caller names the folder
                case "pbip": File.WriteAllText(Path.Combine(where, "x.pbip"), "{}"); break;
                case "pbism": File.WriteAllText(Path.Combine(where, "definition.pbism"), "{}"); break;
                case "te+tmdl": File.WriteAllText(Path.Combine(where, "database.json"), "{}"); File.WriteAllText(Path.Combine(where, "model.tmdl"), "model Model\n"); break;
                case "bimdir": break;
            }
            return where;
        }

        /// <summary>Lean suite: every shape the openers know, saved as BIM only (the default tier); TMDL and folder formats too are the sweep tier.</summary>
        [Theory]
        [MemberData(nameof(Shapes))]
        public Task A_first_save_aimed_anywhere_below_a_model_of_any_shape_the_openers_know_is_redirected_and_the_model_is_left_whole_as_BIM(string label, string key) =>
            RedirectedBelowAsync(label, key, new[] { "BIM" });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public Task A_first_save_aimed_anywhere_below_a_model_of_any_shape_the_openers_know_is_redirected_in_every_format_and_the_model_is_left_whole(string label, string key) =>
            RedirectedBelowAsync(label, key, new[] { "TMDL", "BIM", "folder" });

        private async Task RedirectedBelowAsync(string label, string key, string[] formats)
        {
            var root = Scratch();
            try
            {
                var home = Path.Combine(root, key == "sm" ? "Theirs.SemanticModel" : key == "bimdir" ? "theirs.bim" : "theirs");
                MakeShape(home, key);
                string Snapshot() => string.Join("\n", Directory.EnumerateFileSystemEntries(home, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => Path.GetRelativePath(home, x) + (File.Exists(x) ? "=" + File.ReadAllText(x) : "/")));
                var before = Snapshot().Split('\n').Where(l => l.Length > 0).ToArray();
                using var f = new DecisionFixture(TestEntitlements.Pro);
                foreach (var format in formats)
                foreach (var tail in new[] { "", "fresh", Path.Combine("a", "b", "fresh"), Path.Combine("a", "b", "fresh.bim") })
                {
                    await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                    await f.Engine.CreateTableAsync("Only", "human");
                    var target = tail == "" ? home : Path.Combine(home, tail);
                    var saved = await f.Engine.SaveAsync(target, format, false, "human");
                    var after = Snapshot().Split('\n');
                    Assert.True(!before.Except(after).Any(), $"{label} / {format} / '{tail}': the model's own files changed: [{string.Join(" ;; ", before.Except(after).Take(3))}]");
                    var holds = File.Exists(saved.Path) ? File.ReadAllText(saved.Path).Contains("Brand New") : Directory.Exists(saved.Path) && Directory.EnumerateFiles(saved.Path, "*", SearchOption.AllDirectories).Any();
                    Assert.True(holds, $"{label} / {format} / '{tail}': the result names '{saved.Path}', which holds no model");
                    // A project folder only holds its model, so a new child of it is fine; every other shape owns what is inside it.
                    if (key != "pbip")
                    {
                        Assert.False(saved.Path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) || saved.Path == home, $"{label} / {format} / '{tail}': saved inside the model, at {saved.Path}");
                        Assert.Equal(root, Path.GetDirectoryName(saved.Path));
                        Assert.Contains("so that model is left as it was", saved.Note);
                        Assert.Contains(saved.Path, saved.Note);
                    }
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>A name already taken gets a number, an existing .bim is never replaced by the agent door, and the only failure
        /// left is a folder where every name is taken.</summary>
        [Fact]
        public async Task A_first_save_that_meets_a_taken_name_takes_the_next_number_and_never_replaces_a_bim_the_person_did_not_pick()
        {
            var root = Scratch();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Brand New"));
                File.WriteAllText(Path.Combine(root, "Brand New", "keep.txt"), "mine");
                File.WriteAllText(Path.Combine(root, "theirs.bim"), "{\"name\":\"Theirs\"}");
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var first = await f.Engine.SaveAsync(root, "TMDL", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New 2"), first.Path);
                Assert.Contains("Brand New 2", first.Note);
                Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "Brand New", "keep.txt")));

                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                var viaBim = await f.Engine.SaveAsync(Path.Combine(root, "theirs.bim"), "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New.bim"), viaBim.Path);
                Assert.Equal("{\"name\":\"Theirs\"}", File.ReadAllText(Path.Combine(root, "theirs.bim")));
                Assert.Contains("left as it was", viaBim.Note);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>Every number taken: the one case with nowhere safe to go. Ninety-nine folders, each with a file in it, are walked for a free
        /// name (lean suite: this part of the taken-name test is the sweep tier, the number-taking itself stays in the default one above).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_first_save_with_every_number_taken_has_nowhere_safe_to_go_and_says_so()
        {
            var root = Scratch();
            try
            {
                var full = Path.Combine(root, "full");
                for (var n = 1; n <= 99; n++)
                {
                    var d = Path.Combine(full, n == 1 ? "Brand New" : "Brand New " + n);
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "x.txt"), "x");
                }
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.SaveAsync(full, "TMDL", false, "human"));
                Assert.Contains("No free name", ex.Message);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Theory]
        [InlineData("TMDL", "tables")]
        [InlineData("BIM", "dataSources/fresh.bim")]
        public Task A_first_save_inside_a_folder_a_tabular_editor_loader_reads_lands_at_that_folders_root_sample(string format, string inside) => A_first_save_inside_a_folder_a_tabular_editor_loader_reads_lands_at_that_folders_root(format, inside);

        /// <summary>A Tabular Editor folder on its own takes a new child at its root; a path inside a folder its loader reads by
        /// name (tables/ and the rest) is moved to that root. Either way it opens as itself afterwards.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("TMDL", "tables")]
        [InlineData("TMDL", "tables/Keep/deeper")]
        [InlineData("folder", "roles/new")]
        [InlineData("BIM", "tables")]
        [InlineData("BIM", "dataSources/fresh.bim")]
        public async Task A_first_save_inside_a_folder_a_tabular_editor_loader_reads_lands_at_that_folders_root(string format, string inside)
        {
            var root = Scratch();
            try
            {
                var te = Path.Combine(root, "te");
                // Lean suite: the Tabular Editor split folder is built once for the class and each row works on a copy of it.
                var built = await _tpl.GetAsync(async tplRoot =>
                {
                    var tpl = Path.Combine(tplRoot, "te");
                    using var seed = new DecisionFixture(TestEntitlements.Pro);
                    await seed.Engine.CreateModelAsync("Keep", 1604);
                    await seed.Engine.CreateTableAsync("Keep", "human");
                    await seed.Engine.CreateColumnAsync("table:Keep", "Id", "Int64", "Id", "human");
                    // A real split folder (tables/<name>/<name>.json), as Tabular Editor writes it; the engine's own "folder" save keeps all in database.json.
                    var sess = seed.Sessions.Current;
                    await sess.RunAsync(() => { sess.Save(tpl, SaveFormat.TabularEditorFolder, TabularEditor.TOMWrapper.Serialization.SerializeOptions.DefaultFolder, false); return true; });
                    Assert.True(Directory.Exists(Path.Combine(tpl, "tables", "Keep")));
                    return tpl;
                });
                CopyTree(built, te);
                Directory.CreateDirectory(Path.Combine(te, inside.Split('/')[0]));
                var before = string.Join("\n", Directory.EnumerateFiles(te, "*", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus")).OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(te, p) + "=" + File.ReadAllText(p)));
                using (var f = new DecisionFixture(TestEntitlements.Pro))
                {
                    await f.Engine.CreateModelAsync("Brand New", 1604);
                    await f.Engine.CreateTableAsync("Only", "human");
                    var saved = await f.Engine.SaveAsync(Path.Combine(te, inside), format, false, "human");
                    Assert.Equal(te, Path.GetDirectoryName(saved.Path));
                    Assert.Contains("so that model is left as it was", saved.Note);
                }
                var after = string.Join("\n", Directory.EnumerateFiles(te, "*", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + ".semanticus") && !p.Contains("Brand New")).OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetRelativePath(te, p) + "=" + File.ReadAllText(p)));
                Assert.Equal(before, after);
                using var check = new DecisionFixture(TestEntitlements.Pro);
                Assert.Equal(1, (await check.Engine.OpenAsync(te)).Tables);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_bim_first_save_to_a_path_with_no_extension_adds_the_extension_and_goes_beside_a_file_already_named_so()
        {
            var root = Scratch();
            try
            {
                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");
                var saved = await f.Engine.SaveAsync(Path.Combine(root, "mine"), "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "mine.bim"), saved.Path);
                Assert.True(File.Exists(saved.Path));
                Assert.False(File.Exists(Path.Combine(root, "mine")) || Directory.Exists(Path.Combine(root, "mine")));

                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                File.WriteAllText(Path.Combine(root, "theirs.bim"), "{\"name\":\"Theirs\"}");
                var beside = await f.Engine.SaveAsync(Path.Combine(root, "theirs"), "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "theirs 2.bim"), beside.Path);
                Assert.Equal("{\"name\":\"Theirs\"}", File.ReadAllText(Path.Combine(root, "theirs.bim")));

                // A TMDL save to a name with no extension is still a folder. A BIM save to a name that has another extension is not
                // written under it (round 5: the name could be one an opener reads inside a folder): it is the model's own .bim beside it.
                var tmdl = await f.Engine.SaveAsync(Path.Combine(root, "folder"), "TMDL", false, "human");
                Assert.True(Directory.Exists(tmdl.Path));
                await f.Engine.CreateModelAsync("Brand New", 1604, discardUnsaved: true);
                await f.Engine.CreateTableAsync("Only", "human");
                var dotted = await f.Engine.SaveAsync(Path.Combine(root, "v1.2"), "BIM", false, "human");
                Assert.Equal(Path.Combine(root, "Brand New.bim"), dotted.Path);
                Assert.Contains("'v1.2' is not a .bim file name", dotted.Note);
                Assert.False(File.Exists(Path.Combine(root, "v1.2")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
