using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 join part 6, the join's own fixes (each test was written before the fix and failed on the merged tree).
    /// J2: script apply (v-uat-ui-4 notes a, b, d): the retarget check compares the objects a header names, not their spellings;
    /// a block the object already holds is nothing to do; an untouched block over an unmoved object writes nothing.
    /// </summary>
    public sealed class Join6Tests
    {
        private static async Task<LocalEngine> FreshAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("Join6", 1606);
            await e.CreateTableAsync("T", "agent");
            return e;
        }

        private static async Task<MeasureRow> Row(LocalEngine e, string name) => (await e.ListMeasuresAsync()).First(m => m.Name == name);

        /// <summary>The other door changes the thing the script's blocks are judged on: a DAX script's expression, a TMDL script's description.</summary>
        private static Task<SetResult> MoveAsync(LocalEngine e, string objRef, string kind) =>
            kind == "dax" ? e.SetDaxAsync(objRef, "5", "agent") : e.SetObjectPropertyAsync(objRef, "Description", "Newer words.", "agent");

        private static async Task<bool> StillMovedAsync(LocalEngine e, string kind) =>
            kind == "dax" ? (await Row(e, "A")).Expression == "5" : (await Row(e, "A")).Description == "Newer words.";

        // J2a. A person re-spells a header (another case): TOM looks the object up case-blind, so it is the SAME object, not a
        // retarget. An edited block over a moved object is a conflict, an untouched one is left as it is now.
        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task J2a_a_header_respelled_in_another_case_is_the_same_object_and_still_a_conflict(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, kind);
            await MoveAsync(e, a, kind);
            var respelled = "measure:t/a";
            var edited = (kind == "dax" ? script.Replace("\n1\n", "\n10\n") : script.Replace("Old words.", "My words."))
                .Replace("// @object " + a + " ", "// @object " + respelled + " ");
            Assert.Contains("// @object " + respelled + " @base:", edited);
            var before = await Row(e, "A");

            var r = kind == "dax" ? await e.ApplyDaxScriptAsync(edited, "human") : await e.ApplyTmdlScriptAsync(edited, "human");

            Assert.Equal(new[] { respelled }, r.Conflicts);
            Assert.Empty(r.Applied);
            Assert.True(await StillMovedAsync(e, kind), "the newer change was written over");
            Assert.Equal(before.Expression, (await Row(e, "A")).Expression);
        }

        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task J2a_an_untouched_block_under_a_respelled_header_is_left_as_it_is_now(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, kind);
            var moved = await MoveAsync(e, a, kind);
            var respelled = script.Replace("// @object " + a + " ", "// @object measure:T/a ");

            var r = kind == "dax" ? await e.ApplyDaxScriptAsync(respelled, "human") : await e.ApplyTmdlScriptAsync(respelled, "human");

            Assert.Equal(new[] { "measure:T/a" }, r.LeftAsIs);
            Assert.Empty(r.Applied);
            Assert.True(await StillMovedAsync(e, kind), "the newer change was written over");
            Assert.Equal(moved.Revision, r.Revision);
        }

        // J2b. Applying the same script twice: the second time every edited block already equals the live object, so it is
        // nothing to do (it was a false conflict: the person's own write looked like a change made after they scripted).
        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task J2b_applying_one_script_twice_is_not_a_conflict_the_second_time(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, kind);
            var edited = kind == "dax" ? script.Replace("\n1\n", "\n10\n") : script.Replace("Old words.", "My words.");

            var first = kind == "dax" ? await e.ApplyDaxScriptAsync(edited, "human") : await e.ApplyTmdlScriptAsync(edited, "human");
            Assert.Equal(1, first.Applied.Length);
            var second = kind == "dax" ? await e.ApplyDaxScriptAsync(edited, "human") : await e.ApplyTmdlScriptAsync(edited, "human");

            Assert.Empty(second.Conflicts);
            Assert.Empty(second.Applied);
            Assert.Empty(second.Skipped);
            Assert.Equal(first.Revision, second.Revision);                  // no commit, no undo step, no revision
            Assert.DoesNotContain(second.Notes, n => n.Contains("changed after you scripted"));
            Assert.Contains(second.Notes, n => n.StartsWith(a + ": already as you wrote it"));
            Assert.Equal(kind == "dax" ? "10" : "My words.", kind == "dax" ? (await Row(e, "A")).Expression : (await Row(e, "A")).Description);
        }

        // J2d. A block nobody edited over an object nobody moved writes nothing: no rewrite, no empty revision.
        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task J2d_a_script_nobody_edited_writes_nothing_and_an_edit_writes_only_its_block(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var c = await e.CreateMeasureAsync("table:T", "C", "3", "agent");
            var rev = (await e.SetObjectPropertyAsync(c, "Description", "C words.", "agent")).Revision;
            var script = (await e.ScriptObjectsAsync(new[] { a, b, c }, kind)).Replace("\r\n", "\n"); // the script ends its lines with the platform's line end (CRLF on Windows); the edits below match LF

            var none = kind == "dax" ? await e.ApplyDaxScriptAsync(script, "human") : await e.ApplyTmdlScriptAsync(script, "human");
            Assert.Empty(none.Applied);
            Assert.Empty(none.Conflicts);
            Assert.Empty(none.Skipped);
            Assert.Equal(rev, none.Revision);
            Assert.Empty(none.Notes);                                        // nothing to say about blocks nobody touched

            var edited = kind == "dax" ? script.Replace("\n2\n", "\n20\n") : script.Replace("= 2", "= 20");
            var one = kind == "dax" ? await e.ApplyDaxScriptAsync(edited, "human") : await e.ApplyTmdlScriptAsync(edited, "human");
            Assert.Equal(new[] { b }, one.Applied);
            Assert.Equal(rev + 1, one.Revision);
            Assert.Equal("20", (await Row(e, "B")).Expression);
        }

        // Merge find (psc-bugs x sf-int): the start's own first save goes to <folder>/<name>.SemanticModel/definition, and psc-bugs read "definition" as a name
        // an opener reads inside the folder around it and redirected the save, so the plan's model never sat where the plan said (100 start tests failed).
        // A definition/ inside a .SemanticModel folder that is new is the layout itself; one inside any other folder still goes in a folder of its own.
        [Fact]
        public async Task A_first_save_into_the_definition_folder_of_a_new_semantic_model_folder_goes_exactly_there()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "j6" + Guid.NewGuid().ToString("N").Substring(0, 5));
            try
            {
                using var e = await FreshAsync();
                var target = System.IO.Path.Combine(root, "Uplift.SemanticModel", "definition");
                var r = await e.SaveAsync(target, "tmdl", false, "human");
                Assert.Equal(target, r.Path);
                Assert.True(string.IsNullOrEmpty(r.Note), "the save was moved: " + r.Note);
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(target, "database.tmdl")) || System.IO.File.Exists(System.IO.Path.Combine(target, "model.tmdl")));
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        // Sol join 6, C5. A new semantic-model folder dropped directly beside an existing one in a project made a second *.SemanticModel/definition under that
        // project, so the project no longer had ONE model to open and Old.pbip stopped opening (the opener fell through to a model.bim a TMDL project never has).
        // A new envelope never lands directly under a folder that already holds a model: it goes in a folder of its own beside it, with the plain note.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task C5_a_first_save_of_a_new_semantic_model_beside_an_existing_one_goes_in_its_own_folder_and_the_old_project_still_opens(bool withPbip)
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "j6" + Guid.NewGuid().ToString("N").Substring(0, 5));
            try
            {
                var oldDef = System.IO.Path.Combine(root, "Old.SemanticModel", "definition");
                System.IO.Directory.CreateDirectory(oldDef);
                if (withPbip) System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Old.pbip"), "{}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(oldDef, "database.tmdl"), "database Old\n\tcompatibilityLevel: 1604\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(oldDef, "model.tmdl"), "model Model\n\tculture: en-US\n");
                var opener = withPbip ? System.IO.Path.Combine(root, "Old.pbip") : root;
                Assert.Equal(oldDef, ModelPathResolver.Resolve(opener));
                using var e = await FreshAsync();

                var asked = System.IO.Path.Combine(root, "New.SemanticModel", "definition");
                var r = await e.SaveAsync(asked, "tmdl", false, "human");

                Assert.NotEqual(asked, r.Path);
                Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, "New.SemanticModel")), "a second model sits directly beside the old one");
                Assert.False(string.IsNullOrEmpty(r.Note));
                Assert.Contains("left as it was", r.Note);
                Assert.Contains("'" + System.IO.Path.GetFileName(root) + "'", r.Note);
                Assert.Equal(oldDef, ModelPathResolver.Resolve(opener));   // the old project still opens as the old model
                Assert.True(ModelPathResolver.IsTmdlRoot(r.Path), "the new model was not saved where it says");
                Assert.True(r.Path.StartsWith(root + System.IO.Path.DirectorySeparatorChar));
                Assert.Equal(r.Path, ModelPathResolver.Resolve(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(r.Path))));   // its own folder opens as the new model
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_first_save_into_a_definition_folder_inside_any_other_folder_still_goes_in_a_folder_of_its_own()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "j6" + Guid.NewGuid().ToString("N").Substring(0, 5));
            try
            {
                using var e = await FreshAsync();
                var r = await e.SaveAsync(System.IO.Path.Combine(root, "Plain", "definition"), "tmdl", false, "human");
                Assert.NotEqual(System.IO.Path.Combine(root, "Plain", "definition"), r.Path);
                Assert.False(string.IsNullOrEmpty(r.Note));
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        // Sol join 6, C1. "Apply mine anyway" is a continuation of an apply the person made in ONE model. When the model switched while the
        // conflict note was open, the second apply named the same ref in the other model, treated its own conflict as overridden and wrote
        // there. The session id the first apply was made in travels with the continuation and is checked inside the mutation.
        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task C1_apply_mine_anyway_after_a_model_switch_refuses_and_writes_nothing_in_the_other_model(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var sessionA = (await e.SessionInfoAsync()).SessionId;
            var script = await e.ScriptObjectsAsync(new[] { a }, kind);
            await MoveAsync(e, a, kind);
            var edited = kind == "dax" ? script.Replace("\n1\n", "\n10\n") : script.Replace("Old words.", "My words.");
            var first = kind == "dax" ? await e.ApplyDaxScriptAsync(edited, "human", null, sessionA) : await e.ApplyTmdlScriptAsync(edited, "human", null, sessionA);
            Assert.Equal(new[] { a }, first.Conflicts);

            // Another model with the same ref and its own newer change opens while the note is still on screen.
            await e.CreateModelAsync("OtherModel", 1606);
            await e.CreateTableAsync("T", "agent");
            await e.CreateMeasureAsync("table:T", "A", "99", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Other words.", "agent");
            Assert.NotEqual(sessionA, (await e.SessionInfoAsync()).SessionId);

            var anyway = new[] { a };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => kind == "dax"
                ? e.ApplyDaxScriptAsync(edited, "human", anyway, sessionA)
                : e.ApplyTmdlScriptAsync(edited, "human", anyway, sessionA));

            Assert.Contains("Nothing was written", ex.Message);
            var other = await Row(e, "A");
            Assert.Equal("99", other.Expression);
            Assert.Equal("Other words.", other.Description);
        }

        [Fact]
        public async Task C1_the_session_fence_is_optional_so_the_agent_door_and_older_callers_apply_as_before()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            var sessionId = (await e.SessionInfoAsync()).SessionId;

            var r = await e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n10\n"), "human", null, sessionId);

            Assert.Equal(new[] { a }, r.Applied);
            Assert.Equal("10", (await Row(e, "A")).Expression);
        }

        // Sol join 6, C2 / C4. A UI save compares what the person SAW with what is live INSIDE the engine's mutation, never against a copy the page or
        // the editor holds. Each of these writes was unfenced by value: a script apply, the assistant or another editor wrote a newer value after the
        // person's editor started, and the person's save landed over it with no conflict shown.
        [Fact]
        public async Task C2_a_dax_save_that_started_from_an_older_expression_is_refused_and_the_newer_formula_stands()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetDaxAsync(a, "99", "human");   // another human write (a script apply) after the editor opened on "1"

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDaxAsync(a, "1 + 1 + 0", "human", null, null, "1"));

            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Contains("Nothing was written", ex.Message);
            Assert.Equal("99", (await Row(e, "A")).Expression);
        }

        [Fact]
        public async Task C2_a_dax_save_that_started_from_the_live_expression_writes_and_so_does_one_with_no_starting_point()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");

            var r = await e.SetDaxAsync(a, "1 + 1", "human", null, null, "1");
            Assert.True(r.Changed);
            Assert.Equal("1 + 1", (await Row(e, "A")).Expression);

            await e.SetDaxAsync(a, "7", "human");   // no starting point given: the MCP door and every older caller are unfenced
            Assert.Equal("7", (await Row(e, "A")).Expression);
        }

        [Fact]
        public async Task C2_a_dax_save_of_what_another_writer_already_wrote_is_not_a_conflict()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetDaxAsync(a, "5", "agent");

            var r = await e.SetDaxAsync(a, "5", "human", null, null, "1");

            Assert.False(r.Changed);
            Assert.Equal("5", (await Row(e, "A")).Expression);
        }

        [Fact]
        public async Task C4_a_documentation_save_over_a_section_the_assistant_rewrote_is_refused_inside_the_write()
        {
            using var e = await FreshAsync();
            await e.SetDocSectionAsync("table:T", "notes", "Original words", "human");
            await e.SetDocSectionAsync("table:T", "notes", "New assistant words", "agent");   // lands before the page's 300 ms reload

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDocSectionAsync("table:T", "notes", "Human draft", "human", "Original words"));

            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Equal("New assistant words", await e.GetDocSectionAsync("table:T", "notes"));
        }

        [Fact]
        public async Task C4_a_documentation_save_from_what_is_live_writes_and_an_absent_section_reads_as_empty()
        {
            using var e = await FreshAsync();

            var first = await e.SetDocSectionAsync("table:T", "notes", "First", "human", "");   // the editor opened on a section that did not exist
            Assert.True(first.Changed);
            var second = await e.SetDocSectionAsync("table:T", "notes", "Second", "human", "First");
            Assert.True(second.Changed);
            await e.SetDocSectionAsync("table:T", "notes", "Third", "human");   // no starting point: unfenced

            Assert.Equal("Third", await e.GetDocSectionAsync("table:T", "notes"));
        }

        [Fact]
        public async Task C4_a_calculation_item_save_over_a_newer_expression_is_refused_inside_the_write()
        {
            using var e = await FreshAsync();
            var group = await e.CreateCalculationGroupAsync("Time", "agent");
            var item = await e.CreateCalculationItemAsync(group, "YTD", "SELECTEDMEASURE()", "human");
            await e.SetDaxAsync(item, "SELECTEDMEASURE() + 2", "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetDaxAsync(item, "SELECTEDMEASURE() + 3", "human", null, null, "SELECTEDMEASURE()"));

            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Equal("SELECTEDMEASURE() + 2", await e.GetDaxAsync(item));
        }

        [Fact]
        public async Task C4_a_row_filter_save_over_a_newer_filter_is_refused_inside_the_write()
        {
            using var e = await FreshAsync();
            await e.CreateRoleAsync("Readers", "Read", "human");
            await e.SetTablePermissionAsync("Readers", "table:T", "TRUE()", "human");
            await e.SetTablePermissionAsync("Readers", "table:T", "FALSE()", "agent");   // the other door tightened it after the editor opened on TRUE()

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetTablePermissionAsync("Readers", "table:T", "1 = 1", "human", "TRUE()"));

            Assert.Contains("changed in the model after you opened it", ex.Message);
            var role = (await e.ListRolesAsync()).Single(x => x.Name == "Readers");
            Assert.Equal("FALSE()", role.TableFilters.Single(f => f.Table == "T").FilterExpression);

            var ok = await e.SetTablePermissionAsync("Readers", "table:T", "1 = 1", "human", "FALSE()");   // from what is live: writes
            Assert.True(ok.Changed);
        }

        // Merge find (psc-plan x sf-int): the Plan's words queue now lists a key or date column's format (tool set_property). The start plan's words asked for
        // it too and then refused it ("Only a measure has a format I can write here"), so no assistant could ever finish the words and every start test stalled.
        [Fact]
        public void A_column_format_on_the_words_queue_is_not_a_start_word_and_a_measure_format_is()
        {
            Assert.Null(Semanticus.Engine.StartPlan.StartPlanWords.WordKindOfQueue(new WordsQueueItem { Kind = "format", Tool = "set_property", ObjectRef = "column:T/K" }));
            Assert.NotNull(Semanticus.Engine.StartPlan.StartPlanWords.WordKindOfQueue(new WordsQueueItem { Kind = "format", Tool = "set_measure_format", ObjectRef = "measure:T/M" }));
        }
    }
}
