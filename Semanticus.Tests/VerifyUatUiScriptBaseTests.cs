using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, M11 UAT rounds 3 and 4: attacks on the script base (<c>@base:</c> on each Script > DAX / Script > TMDL header).
    /// A block the person never edited must be left as the other door changed it; a block they edited over an object nobody
    /// moved must be written. Round 4 (the coordinator's redesign) judges by OBJECT: a block the person edited whose object
    /// ALSO moved is a conflict. Then nothing newer may be lost, no line of two versions may be mixed, the conflict must be
    /// named, and "Apply mine anyway" (applyAnyway) must write exactly the person's block, as typed, in one undo step.
    /// Round 3 rows that asserted a merged result were rewritten to that design in round 4.
    /// </summary>
    public sealed class VerifyUatUiScriptBaseTests
    {
        private static async Task<LocalEngine> FreshAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("ScriptBaseAttack", 1604);
            await e.CreateTableAsync("T", "agent");
            return e;
        }

        private static async Task<MeasureRow> Row(LocalEngine e, string name) => (await e.ListMeasuresAsync()).FirstOrDefault(m => m.Name == name);

        private static string Say(ApplyScriptResult r, string script)
            => $"applied=[{string.Join(",", r.Applied)}] skipped=[{string.Join(" | ", r.Skipped)}] left=[{string.Join(",", r.LeftAsIs)}] conflicts=[{string.Join(",", r.Conflicts)}] notes=[{string.Join(" | ", r.Notes)}]\n--- script ---\n{script}";

        /// <summary>A script's blocks without their header lines, normalised the way the base is hashed.</summary>
        private static string Bodies(string script)
            => ScriptBases.NormalizeTmdl(string.Join("\n", script.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("// @object"))));

        /// <summary>The round 4 contract for one conflicted block: the first apply writes nothing for it (no revision when
        /// nothing else was written), names it in conflicts[] and notes[], and the newer change stands; then applyAnyway naming
        /// it writes the person's block exactly as typed (scripting the object again gives back the typed text) and says so
        /// only after the write.</summary>
        private static async Task<ApplyScriptResult> ConflictThenAnywayAsync(LocalEngine e, string objRef, string edited, Func<Task<bool>> newerStands, string label = null)
        {
            label ??= objRef;
            var r = await e.ApplyTmdlScriptAsync(edited, "human");

            Assert.True(r.Conflicts.SequenceEqual(new[] { label }) && r.Applied.Length == 0, Say(r, edited));
            Assert.Contains(r.Notes, n => n.StartsWith(label + " changed after you scripted it, and you edited it too.") && n.Contains("nothing was written for it"));
            Assert.DoesNotContain(r.Notes, n => n.Contains("your version was written"));
            Assert.True(await newerStands(), "the newer change was lost by the first apply\n" + Say(r, edited));

            var r2 = await e.ApplyTmdlScriptAsync(edited, "human", r.Conflicts);

            Assert.True(r2.Conflicts.Length == 0 && r2.Applied.Length == 1, Say(r2, edited));
            Assert.True(r2.Revision == r.Revision + 1, $"apply anyway took {r2.Revision - r.Revision} revisions, not one");
            Assert.Contains(r2.Notes, n => n.StartsWith(label + ": your version was written over the change"));
            var now = await e.ScriptObjectsAsync(new[] { objRef }, "tmdl");
            // TOM may list a table's measures in its own order; the lines themselves must be exactly the typed ones.
            Assert.True(Bodies(edited).Split('\n').OrderBy(l => l, StringComparer.Ordinal).SequenceEqual(Bodies(now).Split('\n').OrderBy(l => l, StringComparer.Ordinal)), "apply anyway did not write the block as typed\n--- typed ---\n" + edited + "\n--- now ---\n" + now);
            return r2;
        }

        // The person copies a block to give another measure the same formula: they change only the ref on the header line
        // and keep the body. The body still hashes to its base, so the block reads as "untouched" and is never written,
        // with a note that says the person did not edit it. The marker must not let a header edit skip the person's change.
        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("human")]
        public Task A_dax_block_whose_header_was_pointed_at_another_measure_is_written_to_it_sample(string scripter) => A_dax_block_whose_header_was_pointed_at_another_measure_is_written_to_it(scripter);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human")]
        [InlineData("agent")]
        public async Task A_dax_block_whose_header_was_pointed_at_another_measure_is_written_to_it(string scripter)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            var retargeted = script.Replace("// @object " + a + " ", "// @object " + b + " ");
            Assert.Contains("// @object " + b + " @base:", retargeted);

            var r = await e.ApplyDaxScriptAsync(retargeted, scripter);

            Assert.True("1" == (await Row(e, "B")).Expression, Say(r, retargeted));
        }

        // Round 4: the other door added a display folder, the person edited the formula. A conflict: the folder stays, the
        // formula is not written, the person's version is named; applying theirs anyway writes their block as typed.
        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("human", "agent")]
        public Task A_tmdl_formula_edit_over_a_display_folder_the_other_door_added_is_a_named_conflict_sample(string scripter, string other) => A_tmdl_formula_edit_over_a_display_folder_the_other_door_added_is_a_named_conflict(scripter, other);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_formula_edit_over_a_display_folder_the_other_door_added_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "DisplayFolder", "Newer folder", other);
            var edited = script.Replace("= 1", "= 10");
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, a, edited, async () => { var row = await Row(e, "A"); return row.Expression == "1" && row.DisplayFolder == "Newer folder"; })).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("human", "agent")]
        public Task A_tmdl_formula_edit_over_a_format_string_the_other_door_removed_is_a_named_conflict_sample(string scripter, string other) => A_tmdl_formula_edit_over_a_format_string_the_other_door_removed_is_a_named_conflict(scripter, other);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_formula_edit_over_a_format_string_the_other_door_removed_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "FormatString", "0.00", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "FormatString", "", other);
            var edited = script.Replace("= 1", "= 10");
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, a, edited, async () => { var row = await Row(e, "A"); return row.Expression == "1" && string.IsNullOrEmpty(row.FormatString); })).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        // The person adds a property line in the script while the other door removes the description.
        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("human", "agent")]
        public Task A_property_added_in_the_script_over_a_description_the_other_door_removed_is_a_named_conflict_sample(string scripter, string other) => A_property_added_in_the_script_over_a_description_the_other_door_removed_is_a_named_conflict(scripter, other);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_property_added_in_the_script_over_a_description_the_other_door_removed_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "", other);
            var tagLine = script.Split('\n').First(l => l.TrimStart().StartsWith("lineageTag:"));
            var indent = tagLine.Substring(0, tagLine.Length - tagLine.TrimStart().Length);
            var edited = script.Replace(tagLine, indent + "displayFolder: Mine\n" + tagLine);
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, a, edited, async () => { var row = await Row(e, "A"); return string.IsNullOrEmpty(row.DisplayFolder) && string.IsNullOrEmpty(row.Description); })).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        // Adjacent lines: the person edits the description line, the other door rewrote the formula below it as two lines.
        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("human", "agent")]
        public Task A_description_edit_over_a_multi_line_formula_the_other_door_wrote_is_a_named_conflict_sample(string scripter, string other) => A_description_edit_over_a_multi_line_formula_the_other_door_wrote_is_a_named_conflict(scripter, other);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_description_edit_over_a_multi_line_formula_the_other_door_wrote_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetDaxAsync(a, "VAR x = 5\nRETURN x", other);
            var edited = script.Replace("Old words.", "My words.");
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, a, edited, async () => { var row = await Row(e, "A"); return row.Description == "Old words." && row.Expression.Contains("VAR x = 5"); })).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_edit_of_a_measure_the_other_door_renamed_does_not_bring_the_old_name_back(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.RenameObjectAsync(a, "A2", other);
            var edited = script.Replace("= 1", "= 10");

            ApplyScriptResult r = null; Exception thrown = null;
            try { r = await e.ApplyTmdlScriptAsync(edited, scripter); } catch (InvalidOperationException ex) { thrown = ex; }

            var names = (await e.ListMeasuresAsync()).Select(m => m.Name).ToArray();
            Assert.True(!names.Contains("A") && names.Contains("A2"), $"names=[{string.Join(",", names)}] thrown={thrown?.Message}\n" + (r == null ? "" : Say(r, edited)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task An_untouched_tmdl_block_of_a_measure_the_other_door_deleted_does_not_bring_it_back(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.DeleteObjectAsync(a, other);

            ApplyScriptResult r = null; Exception thrown = null;
            try { r = await e.ApplyTmdlScriptAsync(script, scripter); } catch (InvalidOperationException ex) { thrown = ex; }

            Assert.True(await Row(e, "A") == null, $"thrown={thrown?.Message}\n" + (r == null ? "" : Say(r, script)));
        }

        // The person adds a new measure inside a scripted table; the other door changed an existing measure meanwhile. The
        // table is one object, so this is a conflict for the table: neither N nor the old A is written, A keeps 99.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_measure_added_in_a_table_script_whose_neighbour_the_other_door_changed_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { "table:T" }, "tmdl");
            await e.SetDaxAsync(a, "99", other);
            var line = script.Split('\n').First(l => l.TrimStart().StartsWith("measure A ="));
            var indent = line.Substring(0, line.Length - line.TrimStart().Length);
            var edited = script.Replace(line, indent + "measure N = 3\n\n" + line);
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, "table:T", edited, async () => await Row(e, "N") == null && (await Row(e, "A")).Expression == "99")).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_table_script_edit_over_a_measure_the_other_door_added_is_a_named_conflict(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { "table:T" }, "tmdl");
            await e.CreateMeasureAsync("table:T", "M2", "5", other);
            var edited = script.Replace("= 1", "= 10");
            _ = scripter;

            Assert.Single((await ConflictThenAnywayAsync(e, "table:T", edited, async () => (await Row(e, "M2"))?.Expression == "5" && (await Row(e, "A")).Expression == "1")).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_untouched_script_pasted_with_windows_line_ends_and_trimmed_line_ends_is_still_left_as_it_is()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var tmdl = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            var dax = await e.ScriptObjectsAsync(new[] { b }, "dax");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", "agent");
            await e.SetDaxAsync(b, "20", "agent");

            var rt = await e.ApplyTmdlScriptAsync(string.Join("\r\n", tmdl.Split('\n').Select(l => l.TrimEnd())), "human");
            var rd = await e.ApplyDaxScriptAsync(dax.Replace("\n", "\r\n"), "human");

            Assert.True((await Row(e, "A")).Description == "Newer words." && rt.LeftAsIs.Contains(a), Say(rt, tmdl));
            Assert.True((await Row(e, "B")).Expression == "20" && rd.LeftAsIs.Contains(b), Say(rd, dax));
        }

        // A DAX block the person never edited, but whose editor trimmed line-end spaces on save, is still untouched.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_untouched_dax_block_an_editor_trimmed_is_still_left_as_it_is()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "VAR x = 1 \nRETURN x", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            await e.SetDaxAsync(a, "VAR x = 2\nRETURN x", "agent");
            var trimmed = string.Join("\n", script.Split('\n').Select(l => l.TrimEnd()));

            var r = await e.ApplyDaxScriptAsync(trimmed, "human");

            Assert.True((await Row(e, "A")).Expression.Contains("VAR x = 2"), Say(r, trimmed));
        }

        // Both sides changed the first line of a two-line formula; the person's line must not be glued to the other side's
        // second line into a formula nobody wrote.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_same_line_clash_in_a_multi_line_formula_never_writes_a_mix_nobody_wrote()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "VAR x = 1\nRETURN x", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetDaxAsync(a, "VAR y = 1\nRETURN y * 2", "agent");
            var edited = script.Replace("VAR x = 1", "VAR x = 3");

            ApplyScriptResult r = null; Exception thrown = null;
            try { r = await e.ApplyTmdlScriptAsync(edited, "human"); } catch (InvalidOperationException ex) { thrown = ex; }

            var expr = (await Row(e, "A")).Expression.Replace("\r\n", "\n").Trim();
            var lines = expr.Split('\n').Select(l => l.Trim()).ToArray();
            var mine = new[] { "VAR x = 3", "RETURN x" }; var theirs = new[] { "VAR y = 1", "RETURN y * 2" };
            Assert.True(lines.SequenceEqual(mine) || lines.SequenceEqual(theirs), $"expr={expr} thrown={thrown?.Message}\n" + (r == null ? "" : Say(r, edited)));
        }

        // Round 4: the engine no longer holds base texts, so pushing 520 other scripts through changes nothing: an edit over
        // a moved object is still a named conflict (not a silent overwrite), and applying it anyway writes it as typed.
        // Lean suite (8 Oct): 520 rounds took 54 s. The engine keeps no base texts now, so the count is not what the test hangs on; the
        // default run pushes 30 other scripts through, the 520 is the sweep.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task A_tmdl_edit_scripted_30_scripts_ago_over_a_moved_object_is_still_a_named_conflict_sample() => TmdlEditScriptedManyScriptsAgoAsync(30);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task A_tmdl_edit_scripted_520_scripts_ago_over_a_moved_object_is_still_a_named_conflict() => TmdlEditScriptedManyScriptsAgoAsync(520);

        private async Task TmdlEditScriptedManyScriptsAgoAsync(int scripts)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            var x = await e.CreateMeasureAsync("table:T", "X", "0", "agent");
            for (var i = 1; i <= scripts; i++)
            {
                await e.SetDaxAsync(x, i.ToString(), "agent");
                await e.ScriptObjectsAsync(new[] { x }, "tmdl");
            }
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", "agent");
            var edited = script.Replace("= 1", "= 10");

            Assert.Single((await ConflictThenAnywayAsync(e, a, edited, async () => { var row = await Row(e, "A"); return row.Expression == "1" && row.Description == "Newer words."; })).Applied);   // the first apply wrote nothing for the block; the apply-anyway wrote exactly it (join 6: the test body carries an assertion)
        }

        // ---- Round 4 attacks ----

        private static void ForgetScriptedRefs()
        {
            // An engine restart empties the in-memory ref registry; the static fields are the only state it has.
            foreach (var name in new[] { "Refs", "Order" })
            {
                var f = typeof(ScriptBases).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(f);
                f.GetValue(null).GetType().GetMethod("Clear").Invoke(f.GetValue(null), null);
            }
        }

        // Retarget after a restart: the registry is empty, so a block pointed at B cannot be told from a moved object. It must
        // never be read as "untouched" (that would drop the person's edit silently): it is a named conflict, B keeps its own
        // formula, and Apply mine anyway writes it to B.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_retargeted_dax_block_after_a_restart_is_a_named_conflict_and_apply_anyway_writes_it()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            var retargeted = script.Replace("// @object " + a + " ", "// @object " + b + " ");
            ForgetScriptedRefs();

            var r = await e.ApplyDaxScriptAsync(retargeted, "human");

            Assert.True(r.Conflicts.SequenceEqual(new[] { b }) && r.LeftAsIs.Length == 0 && r.Applied.Length == 0, Say(r, retargeted));
            Assert.Equal("2", (await Row(e, "B")).Expression);
            Assert.Equal("1", (await Row(e, "A")).Expression);

            var r2 = await e.ApplyDaxScriptAsync(retargeted, "human", r.Conflicts);

            Assert.True(r2.Applied.SequenceEqual(new[] { b }), Say(r2, retargeted));
            Assert.Equal("1", (await Row(e, "B")).Expression);
        }

        // Apply mine anyway names one of two conflicts: only that block is written, as typed, the other conflict and an
        // untouched block left as they are; one undo puts the named object back to the newer change and touches nothing else.
        [Fact]
        public async Task Apply_anyway_writes_only_the_named_tmdl_block_as_typed_in_one_undo_step()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var c = await e.CreateMeasureAsync("table:T", "C", "3", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a, b, c }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer A.", "agent");
            await e.SetObjectPropertyAsync(b, "Description", "Newer B.", "agent");
            await e.SetObjectPropertyAsync(c, "Description", "Newer C.", "agent");
            var edited = script.Replace("= 1", "= 10").Replace("= 2", "= 20");

            var r = await e.ApplyTmdlScriptAsync(edited, "human");
            Assert.True(r.Conflicts.OrderBy(x => x).SequenceEqual(new[] { a, b }.OrderBy(x => x)) && r.LeftAsIs.SequenceEqual(new[] { c }) && r.Applied.Length == 0, Say(r, edited));

            var r2 = await e.ApplyTmdlScriptAsync(edited, "human", new[] { a });

            Assert.True(r2.Applied.SequenceEqual(new[] { a }) && r2.Conflicts.Length == 0, Say(r2, edited));
            var rowA = await Row(e, "A"); var rowB = await Row(e, "B"); var rowC = await Row(e, "C");
            Assert.True(rowA.Expression == "10" && string.IsNullOrEmpty(rowA.Description), $"A expr={rowA.Expression} desc={rowA.Description}");
            Assert.True(rowB.Expression == "2" && rowB.Description == "Newer B.", $"B expr={rowB.Expression} desc={rowB.Description}");
            Assert.True(rowC.Expression == "3" && rowC.Description == "Newer C.", $"C expr={rowC.Expression} desc={rowC.Description}");

            await e.UndoAsync("human");

            rowA = await Row(e, "A"); rowB = await Row(e, "B"); rowC = await Row(e, "C");
            Assert.True(rowA.Expression == "1" && rowA.Description == "Newer A.", $"undo A expr={rowA.Expression} desc={rowA.Description}");
            Assert.True(rowB.Description == "Newer B." && rowC.Description == "Newer C.", "undo reached past the apply");
        }

        // A conflict plus a written block is ONE undo step; a script whose every block is held back writes no revision and no step.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_dax_apply_with_a_conflict_is_one_undo_step_and_all_held_back_writes_no_revision()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var c = await e.CreateMeasureAsync("table:T", "C", "3", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b, c }, "dax")).Replace("\r\n", "\n"); // the script ends its lines with the platform's line end (CRLF on Windows); the edits below match LF
            await e.SetDaxAsync(a, "5", "agent");
            var moved = await e.SetDaxAsync(c, "7", "agent");

            // A edited over a moved object (conflict), C untouched over a moved object (left): nothing at all to write.
            // (B is cut out: an untouched block over an unmoved object is written as before, by design.)
            var scriptAc = script.Substring(0, script.IndexOf("// @object " + b)) + script.Substring(script.IndexOf("// @object " + c));
            var held = await e.ApplyDaxScriptAsync(scriptAc.Replace("\n1\n", "\n10\n"), "human");
            Assert.True(held.Applied.Length == 0 && held.Conflicts.SequenceEqual(new[] { a }) && held.LeftAsIs.SequenceEqual(new[] { c }), Say(held, script));
            Assert.Equal(moved.Revision, held.Revision);

            // B edited over an unmoved object is written with the conflict held back, in one step.
            var r = await e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n10\n").Replace("\n2\n", "\n20\n"), "human");
            Assert.True(r.Applied.SequenceEqual(new[] { b }) && r.Revision == moved.Revision + 1, Say(r, script));

            await e.UndoAsync("human");

            Assert.Equal("2", (await Row(e, "B")).Expression);
            Assert.Equal("5", (await Row(e, "A")).Expression);
            Assert.Equal("7", (await Row(e, "C")).Expression);
        }

        // Editing the marker (a typo, a hand-made one, the true base) can never make the person's edited block read as
        // untouched: it is a conflict, never "left as it is". (A marker borrowed from another object's block reads as a
        // retarget and is written to this object, by design: see the report.)
        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Theory]
        [InlineData("dax")]
        public Task An_edited_or_borrowed_marker_never_turns_the_persons_edit_into_left_as_it_is_sample(string kind) => An_edited_or_borrowed_marker_never_turns_the_persons_edit_into_left_as_it_is(kind);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("dax")]
        [InlineData("tmdl")]
        public async Task An_edited_or_borrowed_marker_never_turns_the_persons_edit_into_left_as_it_is(string kind)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var scriptA = await e.ScriptObjectsAsync(new[] { a }, kind);
            var scriptB = await e.ScriptObjectsAsync(new[] { b }, kind);
            await e.SetDaxAsync(a, "5", "agent");
            var markerB = System.Text.RegularExpressions.Regex.Match(scriptB, "@base:[0-9a-f]{12}").Value;
            var edited = kind == "dax" ? scriptA.Replace("\n1\n", "\n10\n") : scriptA.Replace("= 1", "= 10");

            _ = markerB;
            foreach (var marker in new[] { "@base:000000000000", "@base:" + ScriptBases.Hash(a, kind == "dax" ? "1" : "x") })
            {
                var forged = System.Text.RegularExpressions.Regex.Replace(edited, "@base:[0-9a-f]{12}", marker);
                var r = kind == "dax" ? await e.ApplyDaxScriptAsync(forged, "human") : await e.ApplyTmdlScriptAsync(forged, "human");

                Assert.True(r.LeftAsIs.Length == 0 && r.Conflicts.SequenceEqual(new[] { a }), marker + "\n" + Say(r, forged));
                Assert.Equal("5", (await Row(e, "A")).Expression);
            }
        }

        // A TMDL apply anyway that validation refuses writes nothing and no note claims a write.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refused_tmdl_apply_anyway_claims_no_write()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", "agent");
            var bad = script.Replace("= 1", "= SUM(((");

            ApplyScriptResult r = null; Exception thrown = null;
            try { r = await e.ApplyTmdlScriptAsync(bad, "human", new[] { a }); } catch (InvalidOperationException ex) { thrown = ex; }

            var row = await Row(e, "A");
            Assert.True(row.Expression == "1" && row.Description == "Newer words.", $"expr={row.Expression} desc={row.Description}");
            if (r != null)
            {
                Assert.True(r.Applied.Length == 0, Say(r, bad));
                Assert.DoesNotContain(r.Notes, n => n.Contains("your version was written"));
            }
            else Assert.DoesNotContain("your version was written", thrown.Message);
        }
    }
}
