using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Workflow format v3, the composer keys (composer contract section 1, lane L1). The reader parses and
    /// validates them and check_workflow judges them; nothing here runs a composed step, because the run path
    /// is a later lane. Until it lands, admission refuses to start a file whose composer fields it cannot run
    /// (the same guard stage 1 used for when, forEach and call).
    /// </summary>
    public sealed class WorkflowV3FormatTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        private static (LocalEngine e, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfv3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), TestEntitlements.Pro, ws), ws);
        }

        private static readonly string[] FrameHead =
        {
            "---", "schemaVersion: 3", "name: return-rate", "title: Return rate",
            "assistantNotes: Name measures in Title Case.",
            "inputs:",
            "  - name: table",
            "    question: Which table holds the new measure?",
            "    type: objectRef",
            "    from: tables",
            "---", "",
        };

        // The frames' step 5 (contract 1.8), verbatim apart from its number: a file's steps number from 1.
        private static readonly string[] FrameStep =
        {
            "## Step 1: Add the measure Return rate", "",
            "```yaml step",
            "id: return-rate",
            "performer: semanticus",
            "action:",
            "  op: create_measure",
            "  tableRef: \"{{inputs.table}}\"",
            "  name: Return rate",
            "  expression: \"{{blanks.formula}}\"",
            "  displayFolder: Returns",
            "questions:",
            "  - name: compares",
            "    question: Return rate compares",
            "    type: enum",
            "    by: you",
            "    values: [Returned units to units sold, Refund amount to sales amount]",
            "    jev: measure.ratio_basis",
            "blanks:",
            "  - name: formula",
            "    kind: dax",
            "    fills: expression",
            "    dependsOn: [compares]",
            "instructions: >-",
            "  Compare returned units to units sold for the same period. Count each return once,",
            "  even when it has several refund lines. Name it Return rate.",
            "checks:",
            "  - kind: valid_dax",
            "    phase: written",
            "  - kind: refs_within",
            "    phase: written",
            "    of: [Returns, Sales]",
            "  - kind: result_range",
            "    phase: after",
            "    min: 0",
            "    max: 1",
            "```", "",
            "Semanticus adds the measure once the formula passes its checks.",
        };

        private static string Frame() => Md(FrameHead.Concat(FrameStep).ToArray());

        private static string FrameWithout(params string[] drop) =>
            Md(FrameHead.Concat(FrameStep.Where(l => !drop.Contains(l))).ToArray());

        // ---- the finish line ------------------------------------------------------------------------

        [Fact]
        public void The_frames_step_5_parses_and_every_key_is_stored()
        {
            var def = WorkflowParser.Parse(Frame());
            Assert.Null(def.Error);
            Assert.Equal(3, def.SchemaVersion);
            Assert.Equal("Name measures in Title Case.", def.AssistantNotes);
            var input = Assert.Single(def.Inputs);
            Assert.Equal(("table", "objectRef", "tables"), (input.Name, input.Type, input.From));

            var st = Assert.Single(def.Steps);
            Assert.Equal("return-rate", st.Id);
            Assert.Equal(WorkflowPerformers.Semanticus, st.Performer);
            Assert.Equal("change", st.Phase);                  // derived from create_measure, not written
            Assert.False(st.PhaseWritten);
            Assert.Equal("create_measure", st.Action.Op);
            Assert.Equal(new[] { "tableRef", "name", "expression", "displayFolder" }, st.Action.Arguments.Select(a => a.Name));
            Assert.Equal("{{blanks.formula}}", st.Action.Argument("expression").Value);

            var q = Assert.Single(st.Questions);
            Assert.Equal(("compares", "enum", "you", "measure.ratio_basis"), (q.Name, q.Type, q.By, q.Jev));
            Assert.Equal(new[] { "Returned units to units sold", "Refund amount to sales amount" }, q.Values);

            var b = Assert.Single(st.Blanks);
            Assert.Equal(("formula", "dax", "expression", "assistant"), (b.Name, b.Kind, b.Fills, b.By));
            Assert.Equal(new[] { "compares" }, b.DependsOn);

            Assert.StartsWith("Compare returned units to units sold", st.Brief);
            Assert.Equal("Semanticus adds the measure once the formula passes its checks.", st.Instructions);

            Assert.Equal(new[] { ("valid_dax", "written"), ("refs_within", "written"), ("result_range", "after") },
                st.Checks.Select(c => (c.Kind, c.Phase)));
            Assert.Equal(new[] { "Returns", "Sales" }, st.Checks[1].Of);
            Assert.Equal(("0", "1"), (st.Checks[2].Min, st.Checks[2].Max));
        }

        // The frame's 'jev:' line is the one key check_workflow refuses (below); the rest of step 5 is accepted.
        [Fact]
        public async Task Check_workflow_accepts_the_frames_step_5()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("return-rate", FrameWithout("    jev: measure.ratio_basis"), "human");
            var report = await e.CheckWorkflowAsync("return-rate");
            Assert.Null(report.ParseError);
            Assert.True(report.Ok, string.Join(" | ", report.Findings.Select(f => f.Severity + ": " + f.Message)));
        }

        // Honesty lane: a question's 'jev:' is parsed and nothing reads it, so a file that says Jev answers a question
        // would run as if the line were not there. check_workflow refuses it, in a plain sentence, until a real
        // decision point backs it.
        [Fact]
        public async Task Check_workflow_refuses_a_question_that_says_Jev_answers_it()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("return-rate", Frame(), "human");
            var report = await e.CheckWorkflowAsync("return-rate");
            Assert.Null(report.ParseError);
            Assert.False(report.Ok);
            var warn = Assert.Single(report.Findings, f => f.Severity == "warn" && f.Message.Contains("'jev:"));
            Assert.Contains("return-rate", warn.Message);
            Assert.Contains("'compares'", warn.Message);
            Assert.Contains("Remove", warn.Message);
            Assert.DoesNotContain("\u2014", warn.Message);
        }

        // A8: an INPUT carrying 'jev:' is the same promise nothing keeps. compose_task already refused it; check_workflow
        // passed it as Ok.
        [Fact]
        public async Task Check_workflow_refuses_an_input_that_says_Jev_answers_it()
        {
            var (e, _) = Make();
            var head = FrameHead.SelectMany(l => l == "    from: tables" ? new[] { l, "    jev: measure.ratio_basis" } : new[] { l });
            await e.SaveWorkflowAsync("return-rate", Md(head.Concat(FrameStep.Where(l => l != "    jev: measure.ratio_basis")).ToArray()), "human");
            var report = await e.CheckWorkflowAsync("return-rate");
            Assert.Null(report.ParseError);
            Assert.False(report.Ok);
            var warn = Assert.Single(report.Findings, f => f.Severity == "warn" && f.Message.Contains("'jev:"));
            Assert.Contains("input 'table'", warn.Message);
            Assert.Contains("Remove", warn.Message);
            Assert.DoesNotContain("\u2014", warn.Message);
        }

        // Composer lane L5: compose.draft drafts steps from words and answers no workflow question, so compose_task on a
        // saved workflow whose question says 'jev:' refuses it plainly, the way check_workflow does, instead of drafting
        // as if the line were not there.
        [Fact]
        public async Task Compose_task_refuses_a_question_that_says_Jev_answers_it()
        {
            var (e, _) = Make();
            await e.CreateModelAsync("Contoso", 1604);
            await e.SaveWorkflowAsync("return-rate", Frame(), "human");
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => e.ComposeTaskAsync(null, "return-rate", null, null, null, "human"));
            Assert.Contains("'jev:", ex.Message);
            Assert.Contains("'compares'", ex.Message);
            Assert.Contains("Remove", ex.Message);
            Assert.DoesNotContain("\u2014", ex.Message);
        }

        [Fact]
        public void Removing_the_blank_is_refused_naming_the_argument_that_needs_one()
        {
            var def = WorkflowParser.Parse(FrameWithout("blanks:", "  - name: formula", "    kind: dax", "    fills: expression", "    dependsOn: [compares]"));
            Assert.NotNull(def.Error);
            Assert.Contains("'expression'", def.Error);
            Assert.Contains("formula", def.Error);
            Assert.Contains("Step 1", def.Error);
        }

        [Fact]
        public async Task Removing_the_blank_and_its_argument_is_refused_by_check_workflow_naming_the_argument()
        {
            var (e, _) = Make();
            var md = FrameWithout("  expression: \"{{blanks.formula}}\"",
                "blanks:", "  - name: formula", "    kind: dax", "    fills: expression", "    dependsOn: [compares]");
            await e.SaveWorkflowAsync("return-rate", md, "human");
            var report = await e.CheckWorkflowAsync("return-rate");
            Assert.False(report.Ok);
            var warn = Assert.Single(report.Findings, f => f.Severity == "warn" && f.Message.Contains("'expression'"));
            Assert.Contains("blank", warn.Message);
            Assert.Contains("return-rate", warn.Message);
        }

        // ---- the version -------------------------------------------------------------------------

        [Fact]
        public void The_engine_reads_up_to_version_3_and_refuses_4_by_version()
        {
            Assert.Equal(3, WorkflowParser.MaxSchemaVersion);
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 4", "name: t", "---", "## Step 1: Go", "Body."));
            Assert.Equal("this workflow file says schemaVersion: 4. This Semanticus reads up to 3. Update Semanticus, "
                + "or open the file with the version that wrote it. Nothing was run.", def.Error);
        }

        [Theory]
        [InlineData("assistantNotes: House style.", "assistantNotes", "frontmatter")]
        [InlineData("inputs: []", "inputs", "frontmatter")]
        public void A_v3_frontmatter_key_in_a_v2_file_is_the_closed_set_error(string line, string key, string scope)
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 2", "name: t", line, "---", "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains($"'{key}' is not a key this format has (line 4)", def.Error);
            Assert.StartsWith(scope, def.Error);
            Assert.DoesNotContain("assistantNotes", def.Error.Substring(def.Error.IndexOf("keys are:", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData("performer: assistant", "performer")]
        [InlineData("phase: after", "phase")]
        [InlineData("checks: []", "checks")]
        [InlineData("questions: []", "questions")]
        [InlineData("blanks: []", "blanks")]
        [InlineData("action: { op: save_model }", "action")]
        public void A_v3_step_key_in_a_v2_file_is_the_closed_set_error(string line, string key)
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 2", "name: t", "---", "## Step 1: Go", "```yaml step", line, "```", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains($"Step 1 step block: '{key}' is not a key this format has (line 7)", def.Error);
        }

        [Fact]
        public void Instructions_is_claimed_in_v3_and_still_refused_in_v2()
        {
            var v2 = WorkflowParser.Parse(Md("---", "schemaVersion: 2", "name: t", "---", "## Step 1: Go", "```yaml step", "instructions: Do it well.", "```", "Body."));
            Assert.NotNull(v2.Error);
            Assert.Contains("'instructions:' is reserved for version 3 of this format", v2.Error);

            var v3 = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "instructions: Do it well.", "```", "Body."));
            Assert.Null(v3.Error);
            Assert.Equal("Do it well.", v3.Steps[0].Brief);
            Assert.Equal("Body.", v3.Steps[0].Instructions);
        }

        [Fact]
        public void The_other_step_reservations_still_hold_in_v3()
        {
            foreach (var key in new[] { "maxRunTime", "approvals", "agent", "script", "lookup", "after" })
            {
                var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                    "```yaml step", "performer: assistant", key + ": x", "```", "Body."));
                Assert.NotNull(def.Error);
                Assert.Contains($"'{key}:' is reserved for a later version of this format", def.Error);
            }
        }

        // ---- refusals name the key, the scope and the line -----------------------------------------------

        [Theory]
        [InlineData("    colour: red", "Step 1 question", "Question")]
        public void An_unknown_question_key_is_named_with_its_scope_and_line(string line, string ctx, string scopeWord)
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "questions:", "  - name: q", "    question: Q?", line, "```", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains($"{ctx}: 'colour' is not a key this format has (line 11). {scopeWord} keys are:", def.Error);
        }

        [Fact]
        public void An_unknown_blank_key_and_an_unknown_check_key_are_named_with_scope_and_line()
        {
            var blank = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "action:", "  op: set_description", "  objRef: measure:Sales/Total", "  text: \"{{blanks.words}}\"",
                "blanks:", "  - name: words", "    kind: description", "    fills: text", "    tone: warm", "```"));
            Assert.NotNull(blank.Error);
            Assert.Contains("Step 1 blank: 'tone' is not a key this format has (line 15). Blank keys are:", blank.Error);

            var check = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "checks:", "  - kind: max_chars", "    max: 20", "    strict: yes", "```"));
            Assert.NotNull(check.Error);
            Assert.Contains("Step 1 check: 'strict' is not a key this format has (line 11). Check keys are:", check.Error);
        }

        [Fact]
        public void An_unknown_frontmatter_input_key_is_named_with_scope_and_line()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "inputs:", "  - name: table", "    picker: tables", "---",
                "## Step 1: Go", "```yaml step", "performer: assistant", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("inputs: 'picker' is not a key this format has (line 6). Question keys are:", def.Error);
        }

        [Fact]
        public void A_bad_value_names_the_key_the_scope_and_the_line()
        {
            var performer = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: robot", "```"));
            Assert.Contains("Step 1 step block: performer 'robot' is not one of: semanticus | assistant | person (line 7).", performer.Error);

            var kind = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "checks:", "  - kind: looks_fine", "```"));
            Assert.Contains("Step 1 check: kind 'looks_fine' is not one of:", kind.Error);
            Assert.Contains("(line 9)", kind.Error);
        }

        // ---- performer rules -----------------------------------------------------------------------

        [Fact]
        public void A_gate_fence_on_a_step_Semanticus_runs_is_refused()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "action:", "  op: save_model", "```",
                "```yaml gate", "verify:", "  - kind: bpa_clean", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("A step Semanticus runs uses questions, blanks and checks, not a gate.", def.Error);
        }

        [Fact]
        public void A_gate_fence_on_an_assistant_step_is_still_read_in_v3()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "```",
                "```yaml gate", "inputs:", "  - name: note", "    question: Note?", "```", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("note", Assert.Single(def.Steps[0].Gate.Inputs).Name);
        }

        [Fact]
        public void A_v3_step_defaults_to_Semanticus_so_a_step_with_no_action_is_refused_with_the_way_out()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("Step 1", def.Error);
            Assert.Contains("'action:'", def.Error);
            Assert.Contains("performer: assistant", def.Error);
        }

        [Theory]
        [InlineData("assistant")]
        [InlineData("person")]
        public void An_action_is_refused_on_a_step_Semanticus_does_not_run(string performer)
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: " + performer, "action:", "  op: save_model", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'action:'", def.Error);
            Assert.Contains("(line 8)", def.Error);
        }

        [Fact]
        public void A_blank_must_fill_an_argument_of_the_action_that_holds_it()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "action:", "  op: set_description", "  objRef: measure:Sales/Total", "  text: \"{{blanks.words}}\"",
                "blanks:", "  - name: words", "    kind: description", "    fills: objRef", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'text'", def.Error);
            Assert.Contains("words", def.Error);
        }

        [Fact]
        public void A_reference_to_an_undeclared_input_question_or_step_is_refused()
        {
            var input = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "action:", "  op: save_model", "  path: \"{{inputs.where}}\"", "```"));
            Assert.Contains("'{{inputs.where}}'", input.Error);
            Assert.Contains("'path'", input.Error);

            var later = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---",
                "## Step 1: Go", "```yaml step", "action:", "  op: save_model", "  path: \"{{steps.two.path}}\"", "```",
                "## Step 2: Then", "```yaml step", "id: two", "performer: assistant", "```"));
            Assert.Contains("'{{steps.two.path}}'", later.Error);
            Assert.Contains("earlier step", later.Error);
        }

        [Fact]
        public void DependsOn_must_name_a_question_this_step_or_an_earlier_one_asks()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "action:", "  op: set_description", "  objRef: measure:Sales/Total", "  text: \"{{blanks.words}}\"",
                "blanks:", "  - name: words", "    kind: description", "    fills: text", "    dependsOn: [audience]", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'audience'", def.Error);
        }

        [Theory]
        [InlineData("valid_dax", "after")]
        [InlineData("result_range", "before")]
        [InlineData("key_unique", "written")]
        public void A_check_in_the_wrong_phase_is_refused(string kind, string phase)
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "checks:", "  - kind: " + kind, "    phase: " + phase,
                kind == "result_range" ? "    min: 0" : "", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains($"'{kind}'", def.Error);
            Assert.Contains(phase, def.Error);
        }

        [Fact]
        public void The_brief_and_the_notes_are_capped_at_2000_characters()
        {
            var longText = new string('a', 2001);
            var notes = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "assistantNotes: " + longText, "---",
                "## Step 1: Go", "```yaml step", "performer: assistant", "```"));
            Assert.Contains("2000", notes.Error);
            var brief = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---",
                "## Step 1: Go", "```yaml step", "performer: assistant", "instructions: " + longText, "```"));
            Assert.Contains("2000", brief.Error);
        }

        // ---- check_workflow: placement and outward ops ---------------------------------------------------

        [Fact]
        public async Task A_change_step_after_an_after_step_is_refused_by_check_workflow()
        {
            var (e, _) = Make();
            var md = Md("---", "schemaVersion: 3", "name: order", "title: T", "---",
                "## Step 1: Save", "```yaml step", "id: save", "action:", "  op: save_model", "```",
                "## Step 2: Describe", "```yaml step", "id: describe", "action:", "  op: set_description",
                "  objRef: measure:Sales/Total", "  text: Total sales.", "```");
            await e.SaveWorkflowAsync("order", md, "human");
            var report = await e.CheckWorkflowAsync("order");
            Assert.False(report.Ok);
            Assert.Contains(report.Findings, f => f.Severity == "warn" && f.Message.Contains("describe") && f.Message.Contains("save"));
        }

        [Fact]
        public async Task An_op_that_leaves_the_model_placed_in_the_change_group_is_refused_by_check_workflow()
        {
            var (e, _) = Make();
            var md = Md("---", "schemaVersion: 3", "name: outward", "title: T", "---",
                "## Step 1: Save", "```yaml step", "id: save", "phase: change", "action:", "  op: save_model", "```");
            await e.SaveWorkflowAsync("outward", md, "human");
            var report = await e.CheckWorkflowAsync("outward");
            Assert.False(report.Ok);
            Assert.Contains(report.Findings, f => f.Severity == "warn" && f.Message.Contains("save_model") && f.Message.Contains("after"));
        }

        [Fact]
        public async Task An_op_off_the_allowlist_on_a_step_Semanticus_runs_is_refused_by_check_workflow()
        {
            var (e, _) = Make();
            var md = Md("---", "schemaVersion: 3", "name: offlist", "title: T", "---",
                "## Step 1: Script", "```yaml step", "action:", "  op: apply_tmdl", "  script: x", "```");
            await e.SaveWorkflowAsync("offlist", md, "human");
            var report = await e.CheckWorkflowAsync("offlist");
            Assert.False(report.Ok);
            Assert.Contains(report.Findings, f => f.Severity == "warn" && f.Message.Contains("apply_tmdl"));
        }

        [Fact]
        public async Task A_question_whose_options_depend_on_an_earlier_write_needs_a_stop()
        {
            var (e, _) = Make();
            string Doc(string performer) => Md("---", "schemaVersion: 3", "name: late", "title: T", "---",
                "## Step 1: Table", "```yaml step", "id: make", "action:", "  op: create_import_table", "  name: Returns",
                "  mExpression: \"{{blanks.query}}\"", "blanks:", "  - name: query", "    kind: m", "    fills: mExpression", "```",
                "## Step 2: Pick", "```yaml step", "performer: " + performer, "questions:", "  - name: key",
                "    question: Which column is the key?", "    type: objectRef", "    from: \"columns:{{steps.make.table}}\"", "```");
            await e.SaveWorkflowAsync("late", Doc("assistant"), "human");
            var report = await e.CheckWorkflowAsync("late");
            Assert.Contains(report.Findings, f => f.Severity == "warn" && f.Message.Contains("key") && f.Message.Contains("make"));

            await e.SaveWorkflowAsync("late", Doc("person"), "human");
            var stopped = await e.CheckWorkflowAsync("late");
            Assert.DoesNotContain(stopped.Findings, f => f.Message.Contains("'key'"));
        }

        // ---- admission, now the composed run path has landed (lane L4) ------------------------------------

        // A step Semanticus runs is run by a draft (compose_task), never by the guided runner, which would run the file
        // as if its action were not there. The frame's fields are all ones a draft runs, so nothing is left unrunnable;
        // start_workflow still refuses it, and names the door that runs it.
        [Fact]
        public async Task A_v3_file_with_a_step_Semanticus_runs_is_drafted_not_started()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("return-rate", Frame(), "human");
            Assert.Empty(WorkflowParser.UnexecutableControlFields(WorkflowParser.Parse(Frame())));
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => e.StartWorkflowAsync("return-rate", "human"));
            Assert.Contains("compose_task", ex.Message);
            Assert.Contains("return-rate", ex.Message);
        }

        // What neither path runs yet stays refused: a person's stop, and a step Semanticus runs beside one the
        // assistant does in the same file.
        [Fact]
        // Lane author-gaps, B9: a draft now runs a person's stop and an assistant's step inside a composed file, so the
        // mixed file has nothing left unrun. A person's stop in a GUIDED file still has no path (start_workflow has
        // nothing to pause at), and a gate on an assistant's step inside a composed file is the guided runner's alone.
        public void A_person_stop_runs_as_a_draft_and_a_gate_on_a_person_stop_is_unrunnable_and_a_mixed_file_runs()
        {
            var person = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Stop",
                "```yaml step", "performer: person", "```", "Look."));
            Assert.Null(person.Error);
            // N3 (lane l9-finish): a person's stop is a field only a draft runs, so the file is a draft's, and nothing in it
            // reads as unrunnable. (It used to be a guided file with a stop start_workflow had nothing to pause at.)
            Assert.True(WorkflowParser.IsComposed(person));
            Assert.Empty(WorkflowParser.UnexecutableControlFields(person));

            var mixed = WorkflowParser.Parse(Md(FrameHead.Concat(FrameStep).Concat(new[]
                { "## Step 2: Tidy", "```yaml step", "id: tidy", "performer: assistant", "```", "Tidy up." }).ToArray()));
            Assert.Null(mixed.Error);
            Assert.Empty(WorkflowParser.UnexecutableControlFields(mixed));

            var gated = WorkflowParser.Parse(Md(FrameHead.Concat(FrameStep).Concat(new[]
                { "## Step 2: Tidy", "```yaml step", "id: tidy", "performer: assistant", "```", "Tidy up.",
                  "```yaml gate", "verify:", "  - kind: bpa_clean", "    scope: object", "```" }).ToArray()));
            Assert.Null(gated.Error);
            // Lane l9-finish: a gate on an assistant's step of a composed file runs in the draft's guided sub-run
            // (composer contract 1.3). A gate on a person's stop still has nothing a draft could run.
            Assert.Empty(WorkflowParser.UnexecutableControlFields(gated));
            var stopGated = WorkflowParser.Parse(Md(FrameHead.Concat(FrameStep).Concat(new[]
                { "## Step 2: Tidy", "```yaml step", "id: tidy", "performer: person", "```", "Tidy up.",
                  "```yaml gate", "verify:", "  - kind: bpa_clean", "    scope: object", "```" }).ToArray()));
            Assert.Null(stopGated.Error);
            Assert.Contains(WorkflowParser.UnexecutableControlFields(stopGated), u => u.StepId == "tidy" && u.Field == "gate");
        }

        [Fact]
        public void An_all_assistant_v3_file_has_nothing_admission_cannot_run()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: 3", "name: t", "---", "## Step 1: Go",
                "```yaml step", "performer: assistant", "```", "Body."));
            Assert.Null(def.Error);
            Assert.Empty(WorkflowParser.UnexecutableControlFields(def));
        }

        // ---- the v2 to v3 upgrade (contract 11.3) -------------------------------------------------------

        private const string V2Source =
            "---\nschemaVersion: 2\nname: upgrade-test\ntitle: Upgrade test\n---\n" +
            "## Step 1: Ask\n```yaml step\nid: ask\n```\nWrite a note.\n```yaml gate\ninputs:\n  - name: note\n    question: Note?\n    required: optional\n```\n" +
            "## Step 2: Finish\nWrap up.";

        private const string V3Expected =
            "---\nschemaVersion: 3\nname: upgrade-test\ntitle: Upgrade test\n---\n" +
            "## Step 1: Ask\n```yaml step\nperformer: assistant\nid: ask\n```\nWrite a note.\n```yaml gate\ninputs:\n  - name: note\n    question: Note?\n    required: optional\n```\n" +
            "## Step 2: Finish\n```yaml step\nperformer: assistant\n```\nWrap up.";

        private sealed class UpgradeFixture : IDisposable
        {
            public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "smx-upgrade3-" + Guid.NewGuid().ToString("N"));
            public SessionManager Sessions { get; } = new SessionManager();
            public LocalEngine Engine { get; }
            public string FilePath => Path.Combine(Workspace, ".semanticus", "workflows", "upgrade-test.md");
            public UpgradeFixture(string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllBytes(FilePath, new UTF8Encoding(false).GetBytes(text));
                Engine = new LocalEngine(Sessions, TestEntitlements.Pro, Workspace);
            }
            public void Dispose() { Sessions.Dispose(); try { Directory.Delete(Workspace, true); } catch { } }
        }

        [Fact]
        public async Task Upgrade_v2_to_v3_sets_the_version_and_assistant_on_every_step_and_nothing_else()
        {
            using var f = new UpgradeFixture(V2Source);
            var activities = new System.Collections.Generic.List<ActivityEvent>();
            f.Sessions.Bus.Activity += activities.Add;
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.True(preview.CanApply, preview.Reason);
            Assert.Null(preview.ParseError);
            Assert.Equal(V3Expected, preview.ProposedText);

            var applied = await f.Engine.UpgradeWorkflowAsync("upgrade-test", false, preview.Document.ByteHash, preview.Document.Path);
            Assert.True(applied.Changed, applied.Reason);
            Assert.Equal(V3Expected, File.ReadAllText(f.FilePath));
            // Honesty lane: the receipt names the version it upgraded TO, not version 2 on every upgrade.
            Assert.Equal("Upgraded the workflow to version 3. Existing instructions and gates are preserved.", applied.Reason);
            Assert.Equal("Upgraded workflow 'upgrade-test' to version 3", Assert.Single(activities, a => a.Kind == "upgrade_workflow").Label);

            var again = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.False(again.Changed);
            Assert.False(again.CanApply);
            Assert.Contains("already uses version 3", again.Reason);
        }

        [Fact]
        public async Task A_v2_file_and_its_v3_upgrade_produce_the_same_guided_run()
        {
            string Trace(params WorkflowRunView[] views) => string.Join("\n", views.Select(v =>
                Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    v.Status, v.StepIndex, v.TotalSteps, v.CurrentStep,
                    Steps = v.Steps.Select(s => new { s.StepId, s.Status, s.Note, s.EffectiveStrictness }),
                })));

            async Task<string> Walk(LocalEngine engine)
            {
                var start = await engine.StartWorkflowAsync("upgrade-test", "human");
                var one = await engine.SubmitWorkflowStepAsync(start.RunId, "ask", "{\"note\": \"done\"}", "human");
                var two = await engine.SubmitWorkflowStepAsync(start.RunId, "step-2", "{}", "human");
                return Trace(start, one, two);
            }

            using var v2 = new UpgradeFixture(V2Source);
            var before = await Walk(v2.Engine);

            using var v3 = new UpgradeFixture(V2Source);
            var preview = await v3.Engine.UpgradeWorkflowAsync("upgrade-test");
            var applied = await v3.Engine.UpgradeWorkflowAsync("upgrade-test", false, preview.Document.ByteHash, preview.Document.Path);
            Assert.True(applied.Changed, applied.Reason);
            Assert.Equal(3, WorkflowParser.Parse(File.ReadAllText(v3.FilePath)).SchemaVersion);
            var after = await Walk(v3.Engine);

            Assert.Contains("completed", before);
            Assert.Equal(before, after);
        }
    }
}
