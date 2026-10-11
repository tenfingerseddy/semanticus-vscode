using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane author-gaps, B7 (composer contract 10.5, Plan-heart contract 21.5): Author edits a format-3 workflow's own
    /// fields in its form, not only in Source. The edit model carries who does each step, its action, questions, blanks,
    /// Brief and checks, and the workflow's notes for the assistant and its inputs; set_field writes each one back into
    /// the file so it still parses; Add question and Add check on a v3 step write v3 fields, not a guided gate. A v2 file
    /// is told to upgrade first rather than given a field it cannot hold.
    /// </summary>
    public sealed class WorkflowV3AuthorEditTests
    {
        private const string Doc = "---\nschemaVersion: 3\nname: author-v3\ntitle: Author v3\n"
            + "assistantNotes: Keep formulas short.\n"
            + "inputs:\n  - name: table\n    question: Which table?\n    type: objectRef\n    from: tables\n---\n"
            + "## Step 1: Add the measure Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: \"{{inputs.table}}\"\n  name: Units\n  expression: \"{{blanks.formula}}\"\n"
            + "blanks:\n  - name: formula\n    kind: dax\n    fills: expression\n"
            + "instructions: Sum the quantity.\n"
            + "checks:\n  - kind: valid_dax\n    phase: written\n```\n"
            + "## Step 2: Tidy up\n```yaml step\nid: tidy\nperformer: assistant\n```\n";

        private static async Task<(LocalEngine engine, SessionManager sm, WorkflowDocumentResult doc)> OpenAsync(string text = Doc, string name = "author-v3")
        {
            var workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-v3edit-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(workspace);
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
            await engine.SaveWorkflowAsync(name, text, "human");
            return (engine, sm, await engine.GetWorkflowDocumentAsync(name));
        }

        private static string Edits(params object[] ops) => JsonSerializer.Serialize(ops, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        [Fact]
        public async Task The_edit_model_carries_the_format_3_fields()
        {
            var (_, sm, doc) = await OpenAsync();
            using (sm)
            {
                var def = doc.EditModel.Definition;
                Assert.Equal("Keep formulas short.", def.AssistantNotes);
                Assert.Equal("table", Assert.Single(def.Inputs).Name);
                var units = def.Steps.Single(s => s.Id == "units");
                Assert.Equal("semanticus", units.Performer);
                Assert.Equal("create_measure", units.Action.Op);
                Assert.Contains(units.Action.Args, a => a.Name == "tableRef" && a.Value == "{{inputs.table}}");
                Assert.Equal("formula", Assert.Single(units.Blanks).Name);
                Assert.Equal("Sum the quantity.", units.Brief);
                Assert.Equal("valid_dax", Assert.Single(units.Checks).Kind);
                Assert.Equal("assistant", def.Steps.Single(s => s.Id == "tidy").Performer);
                Assert.Contains("result_range", doc.EditModel.Choices.CheckKinds["after"]);
            }
        }

        [Fact]
        public async Task The_Brief_questions_checks_notes_and_inputs_are_written_back_and_still_parse()
        {
            var (engine, sm, doc) = await OpenAsync();
            using (sm)
            {
                var units = doc.EditModel.Definition.Steps.Single(s => s.Id == "units");
                var tidy = doc.EditModel.Definition.Steps.Single(s => s.Id == "tidy");
                var edits = Edits(
                    new { op = "set_field", target = units.Key, field = "brief", expect = "Sum the quantity.", value = "Sum the quantity, once per line." },
                    new { op = "set_field", target = units.Key, field = "checks", expect = units.Checks,
                          value = new object[] { new { kind = "valid_dax", phase = "written" }, new { kind = "result_range", phase = "after", min = 0, max = 1000000 } } },
                    new { op = "set_field", target = tidy.Key, field = "questions", expect = tidy.Questions,
                          value = new object[] { new { name = "folder", question = "Which folder?", type = "enum", values = new[] { "Sales", "Returns" } } } },
                    new { op = "set_field", target = tidy.Key, field = "brief", expect = (string)null, value = "Put the helpers in one folder." },
                    new { op = "set_field", target = "workflow", field = "assistantNotes", expect = "Keep formulas short.", value = "Keep formulas short and plain." },
                    new { op = "set_field", target = "workflow", field = "inputs", expect = doc.EditModel.Definition.Inputs,
                          value = new object[] { new { name = "table", question = "Which table?", type = "objectRef", from = "tables" },
                                                  new { name = "period", question = "Which period?", type = "text", required = "optional" } } });
                var preview = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
                Assert.True(preview.CanApply, preview.Reason + " " + string.Join(" ", preview.Issues.Select(i => i.Message)));
                var def = WorkflowParser.Parse(preview.ProposedText);
                Assert.Null(def.Error);
                Assert.Equal("Keep formulas short and plain.", def.AssistantNotes);
                Assert.Equal(new[] { "table", "period" }, def.Inputs.Select(i => i.Name).ToArray());
                Assert.Equal("optional", def.Inputs[1].Required);
                var u = def.Steps.Single(s => s.Id == "units");
                Assert.Equal("Sum the quantity, once per line.", u.Brief);
                Assert.Equal(new[] { "valid_dax", "result_range" }, u.Checks.Select(c => c.Kind).ToArray());
                Assert.Equal("1000000", u.Checks[1].Max);
                var t = def.Steps.Single(s => s.Id == "tidy");
                Assert.Equal("Put the helpers in one folder.", t.Brief);
                Assert.Equal(new[] { "Sales", "Returns" }, Assert.Single(t.Questions).Values);
                // The step bodies and everything else stay byte for byte.
                Assert.Contains("## Step 2: Tidy up", preview.ProposedText);
                Assert.Contains("  expression: \"{{blanks.formula}}\"", preview.ProposedText);
            }
        }

        [Fact]
        public async Task A_step_can_be_given_to_someone_else_and_back_to_Semanticus_with_an_action()
        {
            var (engine, sm, doc) = await OpenAsync();
            using (sm)
            {
                var tidy = doc.EditModel.Definition.Steps.Single(s => s.Id == "tidy");
                // Semanticus with no action is refused with the way out; setting the action gives the step to Semanticus.
                var early = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path,
                    Edits(new { op = "set_field", target = tidy.Key, field = "performer", expect = "assistant", value = "semanticus" }));
                Assert.False(early.CanApply);
                Assert.Contains("Choose the action Semanticus runs first", early.Reason + string.Join(" ", early.Issues.Select(i => i.Message)));
                var edits = Edits(
                    new { op = "set_field", target = tidy.Key, field = "action", expect = (object)null,
                          value = new { op = "set_display_folder", args = new object[] { new { name = "refs", values = new[] { "measure:Sales/Units" } }, new { name = "folder", value = "Helpers" } } } });
                var preview = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
                Assert.True(preview.CanApply, preview.Reason);
                var def = WorkflowParser.Parse(preview.ProposedText);
                Assert.Null(def.Error);
                var t = def.Steps.Single(s => s.Id == "tidy");
                Assert.Equal("semanticus", t.Performer);
                Assert.Equal("set_display_folder", t.Action.Op);
                Assert.Equal(new[] { "measure:Sales/Units" }, t.Action.Argument("refs").Values);

                // And back: giving a step Semanticus runs to the assistant takes its action and blanks off.
                var units = doc.EditModel.Definition.Steps.Single(s => s.Id == "units");
                var back = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path,
                    Edits(new { op = "set_field", target = units.Key, field = "performer", expect = "semanticus", value = "assistant" }));
                Assert.True(back.CanApply, back.Reason);
                var u = WorkflowParser.Parse(back.ProposedText).Steps.Single(s => s.Id == "units");
                Assert.Equal("assistant", u.Performer);
                Assert.Null(u.Action);
                Assert.Empty(u.Blanks);
            }
        }

        [Fact]
        public async Task A_new_step_in_a_format_3_workflow_says_who_does_it()
        {
            var (engine, sm, doc) = await OpenAsync();
            using (sm)
            {
                var last = doc.EditModel.Definition.Steps.Last();
                var edits = Edits(new { op = "add_step", after = last.Key, tempKey = "new:test:1", value = new { id = "look", title = "Look at it", instructions = "", ops = Array.Empty<string>(), performer = "person" } });
                var preview = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
                Assert.True(preview.CanApply, preview.Reason);
                Assert.Equal("person", WorkflowParser.Parse(preview.ProposedText).Steps.Single(s => s.Id == "look").Performer);
            }
        }

        [Fact]
        public async Task A_format_2_workflow_is_told_to_upgrade_before_it_takes_a_format_3_field()
        {
            const string V2 = "---\nschemaVersion: 2\nname: author-v2\ntitle: Author v2\n---\n## Step 1: Do it\n```yaml step\nid: do-it\n```\nDo the thing.\n";
            var (engine, sm, doc) = await OpenAsync(V2, "author-v2");
            using (sm)
            {
                var step = doc.EditModel.Definition.Steps.Single();
                Assert.Null(step.Performer);
                var edits = Edits(new { op = "set_field", target = step.Key, field = "brief", expect = (string)null, value = "Advice." });
                var preview = await engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
                Assert.False(preview.CanApply);
                Assert.Contains("format 3", preview.Reason + string.Join(" ", preview.Issues.Select(i => i.Message)));
            }
        }
    }
}
