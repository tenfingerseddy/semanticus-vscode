using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 UAT round 4: a script keeps each block's base (its ref and body), and apply judges by OBJECT. A block the person
    /// never edited is left as the other door changed it. A block they edited whose object did not move is written. A block
    /// they edited whose object ALSO moved is a conflict: nothing is written for it, it is named, and only an explicit
    /// "apply mine anyway" writes it, as typed. Lines of two versions are never mixed. A script with no base keeps today's
    /// behaviour. Both doors, both kinds.
    /// </summary>
    public sealed class ScriptBaseTests
    {
        private static async Task<LocalEngine> FreshAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("ScriptBase", 1604);
            await e.CreateTableAsync("T", "agent");
            return e;
        }

        private static async Task<MeasureRow> Row(LocalEngine e, string name) => (await e.ListMeasuresAsync()).First(m => m.Name == name);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_dax_block_left_as_it_is_is_named_and_a_script_that_leaves_everything_writes_no_revision(string scripter, string other)
        {
            using var e = await FreshAsync();
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = await e.ScriptObjectsAsync(new[] { b }, "dax");
            var moved = await e.SetDaxAsync(b, "20", other);

            var r = await e.ApplyDaxScriptAsync(script, scripter);

            Assert.Equal("20", (await Row(e, "B")).Expression);
            Assert.Equal(new[] { b }, r.LeftAsIs);
            Assert.Empty(r.Applied);
            Assert.Contains(r.Notes, n => n.StartsWith(b + ": left as it is now") && n.Contains("changed after you scripted"));
            Assert.Equal(moved.Revision, r.Revision);   // nothing was written: no commit, no undo step
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_dax_block_the_person_edited_is_a_conflict_when_its_object_also_moved_and_nothing_is_written(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            var moved = await e.SetDaxAsync(a, "5", other);

            var r = await e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n10\n"), scripter);

            Assert.Equal("5", (await Row(e, "A")).Expression);              // the newer change stands
            Assert.Equal(new[] { a }, r.Conflicts);
            Assert.Empty(r.Applied);
            Assert.Empty(r.LeftAsIs);
            Assert.Equal(moved.Revision, r.Revision);                       // nothing was written: no commit, no undo step
            Assert.Contains(r.Notes, n => n == $"{a} changed after you scripted it, and you edited it too. Your version is still in the script; nothing was written for it.");
        }

        // The rest of a script still applies in one step when one block is a conflict, and "apply mine anyway" writes only the named block.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_conflict_holds_back_only_its_own_block_and_apply_anyway_writes_only_the_named_block(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var c = await e.CreateMeasureAsync("table:T", "C", "3", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b, c }, "dax")).Replace("\r\n", "\n"); // the script ends its lines with the platform's line end (CRLF on Windows); the edits below match LF
            await e.SetDaxAsync(a, "5", other);
            var edited = script.Replace("\n1\n", "\n10\n").Replace("\n2\n", "\n20\n");   // A conflicts; B is edited and unmoved; C untouched

            var r = await e.ApplyDaxScriptAsync(edited, scripter);

            Assert.Equal(new[] { b }, r.Applied);                            // C was untouched and nothing moved under it: not written (join 6, J2d)
            Assert.Equal(new[] { a }, r.Conflicts);
            Assert.Equal("5", (await Row(e, "A")).Expression);
            Assert.Equal("20", (await Row(e, "B")).Expression);

            await e.SetDaxAsync(b, "200", other);                            // a later change must survive the second call: it names only A
            var r2 = await e.ApplyDaxScriptAsync(edited, scripter, new[] { a });

            Assert.Equal(new[] { a }, r2.Applied);
            Assert.Empty(r2.Conflicts);
            Assert.Equal("10", (await Row(e, "A")).Expression);
            Assert.Equal("200", (await Row(e, "B")).Expression);
            Assert.Contains(r2.Notes, n => n.StartsWith(a + ": your version was written over the change"));
        }

        [Fact]
        public async Task Apply_anyway_naming_a_ref_that_is_not_in_the_script_refuses_and_writes_nothing()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            await e.SetDaxAsync(a, "5", "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n10\n"), "human", new[] { "measure:T/Nope" }));

            Assert.Contains("not a block of this script", ex.Message);
            Assert.Equal("5", (await Row(e, "A")).Expression);
        }

        // A block that was never edited is not written even when named: there is nothing of the person's in it.
        [Fact]
        public async Task Apply_anyway_does_not_write_a_block_the_person_never_edited()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            await e.SetDaxAsync(a, "5", "agent");

            var r = await e.ApplyDaxScriptAsync(script, "human", new[] { a });

            Assert.Equal("5", (await Row(e, "A")).Expression);
            Assert.Empty(r.Applied);
            Assert.Equal(new[] { a }, r.LeftAsIs);
        }

        // Validation refuses an apply-anyway: nothing is written and no note claims otherwise.
        [Fact]
        public async Task A_refused_apply_anyway_writes_nothing_and_says_nothing_was_applied()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            await e.SetDaxAsync(a, "5", "agent");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\nSUM(((\n"), "human", new[] { a }));

            Assert.StartsWith("Nothing was written", ex.Message);
            Assert.DoesNotContain("applied", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("5", (await Row(e, "A")).Expression);
        }

        [Fact]
        public async Task A_dax_script_that_nothing_moved_under_applies_as_before_with_no_notes()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b }, "dax")).Replace("\r\n", "\n"); // the script ends its lines with the platform's line end (CRLF on Windows); the edits below match LF

            var r = await e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n10\n"), "human");

            Assert.Equal(new[] { a }, r.Applied);                            // B was never edited and nothing moved under it: not rewritten (join 6, J2d)
            Assert.Empty(r.LeftAsIs);
            Assert.Empty(r.Notes);
            Assert.Equal("10", (await Row(e, "A")).Expression);
        }

        // Windows line ends (D1 of the verifier on m11/winfix). The bodies are TWO lines, so a CRLF sits INSIDE a block: with one-line bodies a line end
        // is only ever at a block edge, where Trim drops it whatever the product does, and a product that compared raw bytes would pass. An editor on
        // Windows saves a script with every line end CRLF, or a mix; a model saved on Windows holds CRLF inside its expressions. None of it may turn a
        // stale block into a write, and the verdict is the one an LF script gets.
        // The script as an editor on Windows saves it (every line end CRLF), or as a mix (some lines CRLF, some LF).
        public static TheoryData<string, string> Shapes() => new TheoryData<string, string> { { "crlf", "agent" }, { "crlf", "human" }, { "mixed", "agent" }, { "mixed", "human" } };

        private static string Shape(string lf, string shape)
        {
            if (shape == "crlf") return lf.Replace("\n", "\r\n");
            var lines = lf.Split('\n');
            return string.Concat(lines.Select((l, i) => i == lines.Length - 1 ? l : l + (i % 2 == 0 ? "\r\n" : "\n")));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task A_multi_line_dax_script_with_windows_line_ends_never_overwrites_a_newer_change(string shape, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1\n+ 1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2\n+ 2", "agent");
            var c = await e.CreateMeasureAsync("table:T", "C", "3\n+ 3", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b, c }, "dax")).Replace("\r\n", "\n");
            await e.SetDaxAsync(a, "5\n+ 5", other);
            await e.SetDaxAsync(c, "7\n+ 7", other);
            var edited = Shape(script.Replace("\n1\n+ 1\n", "\n10\n+ 10\n").Replace("\n2\n+ 2\n", "\n20\n+ 20\n"), shape);
            Assert.Contains("\r\n", edited);

            var r = await e.ApplyDaxScriptAsync(edited, other == "agent" ? "human" : "agent");

            Assert.Equal(new[] { b }, r.Applied);
            Assert.Equal(new[] { a }, r.Conflicts);
            Assert.Equal(new[] { c }, r.LeftAsIs);
            Assert.Equal("5\n+ 5", (await Row(e, "A")).Expression);   // the newer change stands
            Assert.Equal("7\n+ 7", (await Row(e, "C")).Expression);   // the newer change stands
            Assert.Equal("20\n+ 20", (await Row(e, "B")).Expression.Replace("\r\n", "\n"));
        }

        // A model whose expressions themselves hold CRLF (a .bim saved on Windows), scripted and applied untouched after the other
        // door moved one: nothing is written for the moved one, and the untouched ones are not rewritten.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task An_expression_that_holds_CRLF_is_judged_untouched_when_the_script_comes_back_with_LF(string other)
        {
            var sessions = new SessionManager();
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.CreateModelAsync("ScriptBase", 1604);
            await e.CreateTableAsync("T", "agent");
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            // As a model saved on Windows holds them: CRLF inside the expression, set straight on TOM (SetDax would normalise it).
            await sessions.Require().MutateAsync("agent", "crlf expressions", m =>
            {
                m.Tables["T"].Measures["A"].Expression = "1\r\n+ 1";
                m.Tables["T"].Measures["B"].Expression = "2\r\n+ 2";
            });
            var script = (await e.ScriptObjectsAsync(new[] { a, b }, "dax")).Replace("\r\n", "\n");
            Assert.Contains("1\n+ 1", script);
            var moved = await sessions.Require().MutateAsync(other, "newer change", m => m.Tables["T"].Measures["A"].Expression = "5\r\n+ 5");

            var r = await e.ApplyDaxScriptAsync(script, other == "agent" ? "human" : "agent");

            Assert.Empty(r.Applied);
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { a }, r.LeftAsIs);
            Assert.Equal("5\n+ 5", (await Row(e, "A")).Expression.Replace("\r\n", "\n"));   // the newer change stands (the read side gives LF)
            Assert.Equal("2\n+ 2", (await Row(e, "B")).Expression.Replace("\r\n", "\n"));   // untouched (Applied is empty above)
            Assert.Equal(moved, r.Revision);
        }

        // The TMDL door: a whole untouched CRLF script over a moved object leaves the newer change as it is.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task An_untouched_tmdl_script_saved_with_CRLF_leaves_a_newer_change_as_it_is(string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1\n+ 1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b }, "tmdl")).Replace("\r\n", "\n");
            var moved = await e.SetDaxAsync(a, "5\n+ 5", other);

            var r = await e.ApplyTmdlScriptAsync(script.Replace("\n", "\r\n"), other == "agent" ? "human" : "agent");

            Assert.Empty(r.Applied);
            Assert.Empty(r.Conflicts);
            Assert.Equal("5\n+ 5", (await Row(e, "A")).Expression.Replace("\r\n", "\n"));
            Assert.Equal(moved.Revision, r.Revision);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_dax_script_with_no_base_keeps_todays_behaviour(string scripter, string other)
        {
            using var e = await FreshAsync();
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            await e.SetDaxAsync(b, "20", other);

            // Typed by hand, or scripted by an older build: no @base marker on the header.
            var r = await e.ApplyDaxScriptAsync($"// @object {b}\n2\n", scripter);

            Assert.Equal("2", (await Row(e, "B")).Expression);
            Assert.Equal(new[] { b }, r.Applied);
            Assert.Empty(r.LeftAsIs);
            Assert.Empty(r.Notes);
        }

        [Fact]
        public async Task A_measure_name_with_spaces_still_round_trips_with_a_base_marker()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "Total Sales Amount", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "dax");
            Assert.Contains($"// @object {a} @base:", script);

            var r = await e.ApplyDaxScriptAsync(script.Replace("\n1\n", "\n7\n"), "human");

            Assert.Equal(new[] { a }, r.Applied);
            Assert.Equal("7", (await Row(e, "Total Sales Amount")).Expression);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_block_left_as_it_is_is_named(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            var moved = await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);

            var r = await e.ApplyTmdlScriptAsync(script, scripter);

            Assert.Equal("Newer words.", (await Row(e, "A")).Description);
            Assert.Equal(new[] { a }, r.LeftAsIs);
            Assert.Empty(r.Applied);
            Assert.Empty(r.Skipped);
            Assert.Contains(r.Notes, n => n.StartsWith(a + ": left as it is now"));
            Assert.Equal(moved.Revision, r.Revision);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_block_edited_on_the_same_lines_as_a_newer_change_is_a_conflict_and_writes_nothing(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            var moved = await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);

            var r = await e.ApplyTmdlScriptAsync(script.Replace("Old words.", "My words."), scripter);

            Assert.Equal("Newer words.", (await Row(e, "A")).Description);
            Assert.Equal(new[] { a }, r.Conflicts);
            Assert.Empty(r.Applied);
            Assert.Equal(moved.Revision, r.Revision);
            Assert.Contains(r.Notes, n => n.StartsWith(a + " changed after you scripted it, and you edited it too."));
        }

        // Different lines of the same measure are still a conflict: judged by object, never by line, so no line is mixed.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_edit_of_other_lines_than_a_newer_change_is_also_a_conflict_and_mixes_nothing(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);

            var r = await e.ApplyTmdlScriptAsync(script.Replace("= 1", "= 10"), scripter);

            var row = await Row(e, "A");
            Assert.Equal("1", row.Expression);
            Assert.Equal("Newer words.", row.Description);
            Assert.Equal(new[] { a }, r.Conflicts);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_conflict_is_written_as_typed_only_when_named_in_apply_anyway(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);
            var edited = script.Replace("= 1", "= 10");

            var r = await e.ApplyTmdlScriptAsync(edited, scripter, new[] { a });

            var row = await Row(e, "A");
            Assert.Equal("10", row.Expression);
            Assert.Equal("Old words.", row.Description);                    // as written: the script's own copy, not a mix
            Assert.Equal(new[] { a }, r.Applied);
            Assert.Empty(r.Conflicts);
            Assert.Contains(r.Notes, n => n.StartsWith(a + ": your version was written over the change"));
        }

        [Fact]
        public async Task A_table_script_edited_while_the_other_door_added_a_measure_is_a_conflict_for_the_table()
        {
            using var e = await FreshAsync();
            await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { "table:T" }, "tmdl");
            await e.CreateMeasureAsync("table:T", "M2", "5", "agent");

            var r = await e.ApplyTmdlScriptAsync(script.Replace("= 1", "= 10"), "human");

            Assert.Equal(new[] { "table:T" }, r.Conflicts);
            Assert.Equal("1", (await Row(e, "A")).Expression);
            Assert.Equal("5", (await Row(e, "M2")).Expression);
        }

        [Fact]
        public async Task A_tmdl_script_that_nothing_moved_under_applies_as_before_with_no_notes()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");

            var r = await e.ApplyTmdlScriptAsync(script.Replace("= 1", "= 10"), "human");

            Assert.Equal(new[] { a }, r.Applied);
            Assert.Empty(r.LeftAsIs);
            Assert.Empty(r.Notes);
            Assert.Equal("10", (await Row(e, "A")).Expression);
        }

        [Fact]
        public async Task A_tmdl_table_document_leaves_an_untouched_table_the_other_door_changed()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var script = await e.ScriptObjectsAsync(new[] { "table:T" }, "tmdl");
            await e.SetDaxAsync(a, "99", "agent");

            var r = await e.ApplyTmdlScriptAsync(script, "human");

            Assert.Equal("99", (await Row(e, "A")).Expression);
            Assert.Equal(new[] { "table:T" }, r.LeftAsIs);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_tmdl_script_with_no_base_keeps_todays_behaviour(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);
            var stripped = System.Text.RegularExpressions.Regex.Replace(script, " @base:[0-9a-f]{12}", "");
            Assert.DoesNotContain("@base:", stripped);

            var r = await e.ApplyTmdlScriptAsync(stripped.Replace("= 1", "= 10"), scripter);

            Assert.Equal("Old words.", (await Row(e, "A")).Description);   // today's behaviour for a script with no base
            Assert.Empty(r.LeftAsIs);
            Assert.Empty(r.Notes);
        }

        // The marker is a hash of the ref and the body, so it needs no memory: an engine that never saw this script judges it the same way.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_marker_from_an_engine_that_has_forgotten_it_still_names_a_conflict_and_writes_nothing()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", "agent");
            // A marker that is not this block's hash (a hand-made or damaged one): it reads as edited over a moved object.
            var forged = System.Text.RegularExpressions.Regex.Replace(script, "@base:[0-9a-f]{12}", "@base:" + ScriptBases.Hash(a, "not held"));

            var r = await e.ApplyTmdlScriptAsync(forged, "human");

            Assert.Equal(new[] { a }, r.Conflicts);
            Assert.Equal("Newer words.", (await Row(e, "A")).Description);
        }

        [Fact]
        public void The_base_hash_covers_the_ref_and_ignores_line_ends_and_line_endings()
        {
            Assert.NotEqual(ScriptBases.Hash("measure:T/A", "1"), ScriptBases.Hash("measure:T/B", "1"));
            Assert.Equal(ScriptBases.NormalizeDax("VAR x = 1 \r\nRETURN x\r\n"), ScriptBases.NormalizeDax("VAR x = 1\nRETURN x"));
            Assert.Equal(ScriptBases.NormalizeTmdl("a  \r\nb\t\r\n"), ScriptBases.NormalizeTmdl("a\nb"));
            Assert.NotEqual(ScriptBases.NormalizeDax("VAR x = 1\nRETURN x"), ScriptBases.NormalizeDax("VAR x = 2\nRETURN x"));
        }
    }
}
