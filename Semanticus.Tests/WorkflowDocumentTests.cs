using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowDocumentTests
    {
        // A C# raw string literal keeps the .cs file's OWN line endings; Roslyn never normalizes them. This
        // repo's .gitattributes marks *.cs as `text`, so a Windows checkout hands every fixture below CRLF
        // while an Ubuntu one hands it LF, and an assertion that hard-codes LF then fails on windows-latest
        // only. Normalize each fixture first, then opt in to the ending the case is actually about.
        private static string OnlyLf(string text) => (text ?? "")
            .Replace(((char)13).ToString() + (char)10, ((char)10).ToString())
            .Replace(((char)13).ToString(), ((char)10).ToString());

        /// <summary>Fixture or expectation text in the newline style this case is exercising.</summary>
        private static string Nl(string text, bool crlf) => crlf
            ? OnlyLf(text).Replace(((char)10).ToString(), ((char)13).ToString() + (char)10)
            : OnlyLf(text);

        private static readonly string Markdown = OnlyLf("""
            ---
            schemaVersion: 2
            name: document-test
            title: Café source
            version: 7
            ---
            <!-- keep this comment and spacing -->
            ## Step 1: First
            Exact text.  
            ```yaml step
            id: first
            ```
            """) + (char)10;

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        private sealed class Fixture : IDisposable
        {
            public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "smx-document-" + Guid.NewGuid().ToString("N"));
            public SessionManager Sessions { get; } = new SessionManager();
            public LocalEngine Engine { get; }
            public string FilePath => Path.Combine(Workspace, ".semanticus", "workflows", "document-test.md");
            public Fixture()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                Engine = new LocalEngine(Sessions, TestEntitlements.Pro, Workspace);
            }
            public void Write(string text) => File.WriteAllBytes(FilePath, new UTF8Encoding(false, true).GetBytes(text));
            public void Dispose() { Sessions.Dispose(); Directory.Delete(Workspace, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Preview_projects_every_choice_and_patches_rich_source_without_touching_other_bytes(bool crlf)
        {
            using var f = new Fixture();
            // The edit model always projects multi-line text with LF, whatever the document's own style is;
            // the patcher converts back to that style when it writes the value in.
            var lf = ((char)10).ToString();
            var oldInstructions = "Review [[loop.table]] and record evidence." + lf + "```dax" + lf + "EVALUATE ROW(\"Kept\", 1)" + lf + "```";
            var newInstructions = oldInstructions.Replace("record evidence", "record the finding");
            var source = ((char)0xfeff).ToString() + Nl("""
                ---
                schemaVersion: 2
                # owner comment stays
                name: document-test
                title: Rich edit
                version: 4
                provenance:
                  owner: finance
                ---
                ## Step 1: Choose tables
                ```yaml step
                id: choose-tables
                ```
                Choose the list.
                ```yaml gate
                inputs:
                  - name: tables
                    question: "Which tables?"
                    type: text
                  - name: shapes
                    question: "Which shapes are open?"
                    type: text
                  - name: witness
                    question: "Expected?"
                    type: text
                ```
                ## Step 2: Review each table
                ```yaml step
                id: each-table
                when: inputs.tables.answered
                forEach:
                  in: inputs.tables
                  as: table
                  maxIterations: 25
                ```
                Review [[loop.table]] and record evidence.
                ```dax
                EVALUATE ROW("Kept", 1)
                ```
                ```yaml gate
                # gate comment stays
                inputs:
                  - name: target
                    question: "Target?"
                    type: objectRef
                verify:
                  - kind: dax_equivalence
                    probe: witness
                    pinnedShapes: [grand_total]
                    openShapes: [cross]
                    openShapesFrom: shapes
                ```
                ## Step 3: Hand off
                ```yaml step
                id: hand-off
                call:
                  workflow: helper-flow
                  with:
                    target: inputs.target
                  returns: [result]
                ```
                Keep this tail.
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.True(doc.Metadata.Parses, doc.Metadata.ParseError);
            Assert.Equal((char)0xfeff, doc.ExactText[0]);
            Assert.NotNull(doc.EditModel);
            Assert.Equal("v2", doc.EditModel.Format);
            Assert.Equal(new[] { "verification", "text", "enum", "number", "objectRef", "planItem" }, doc.EditModel.Choices.InputTypes);
            Assert.Equal(new[] { "answer-or-decline", "required", "optional" }, doc.EditModel.Choices.InputRequired);
            Assert.Equal(new[] { "iteration", "run" }, doc.EditModel.Choices.InputScopes);
            Assert.Equal(new[] { "no-bare-measures" }, doc.EditModel.Choices.DaxPurity);
            Assert.Equal(new[]
            {
                "dax_probe", "dax_equivalence", "expected_values", "anchor_coverage", "readiness_rescan", "bpa_clean",
                "benchmark_delta", "workflow_admissible", "interview_replay", "baseline_captured", "impact_assessment",
                "baseline_exists", "baseline_unchanged", "tests_replay", "plan_item_staged", "plan_item_applied",
            }, doc.EditModel.Choices.VerifyKinds);
            Assert.Equal(new[] { "object", "model" }, doc.EditModel.Choices.VerifyScopes);
            Assert.Equal(new[] { "change", "rename", "remove", "restructure" }, doc.EditModel.Choices.VerifyIntents);
            Assert.Equal(new[] { "grand_total", "cross", "axis:<column>" }, doc.EditModel.Choices.ShapeIds);
            Assert.Equal(new[] { "countersign" }, doc.EditModel.Choices.OpenMismatch);
            var each = Assert.Single(doc.EditModel.Definition.Steps, s => s.Id == "each-table");
            Assert.Equal(oldInstructions, each.Instructions);

            var edits = JsonSerializer.Serialize(new[] { new
            {
                op = "set_field", target = each.Key, field = "instructions",
                expect = oldInstructions, value = newInstructions,
            } });
            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.Equal("ready", preview.Outcome);
            Assert.True(preview.CanApply);
            Assert.True(preview.RequiresReview); // missing helper-flow is the existing admission warning, not a save gate
            Assert.Equal(source.Replace("record evidence", "record the finding"), preview.ProposedText);
            Assert.Equal(new UTF8Encoding(false, true).GetBytes(source), File.ReadAllBytes(f.FilePath));
            Assert.StartsWith("--- a/document-test.md", preview.Diff);
            Assert.Equal(newInstructions, Assert.Single(preview.EditModel.Definition.Steps, s => s.Id == "each-table").Instructions);
            Assert.Equal(doc.Path, preview.SuggestedNextAction.Args.ExpectPath);
            Assert.Equal(doc.ByteHash, preview.SuggestedNextAction.Args.ExpectByteHash);
        }

        [Fact]
        public async Task Preview_patches_rich_lists_and_call_maps_at_their_value_nodes()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Rich fields
                version: 1
                ---
                ## Step 1: Rich
                ```yaml step
                id: rich
                call:
                  workflow: helper-flow
                  with:
                    target: old-target # keep with comment
                  returns: [result]
                ```
                Work.
                ```yaml gate
                inputs:
                  - name: witness
                    question: Witness?
                    type: text
                  - name: target
                    question: Target?
                    type: objectRef
                verify:
                  - kind: dax_equivalence
                    probe: witness
                    pinnedShapes: [grand_total] # keep pinned
                    openShapes: [cross] # keep open comment
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var verify = Assert.Single(step.Gate.Verify);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = verify.Key, field = "openShapes", expect = new[] { "cross" }, value = new[] { "cross", "axis:'Date'[Year]" } },
                new { op = "set_map_entry", target = step.Key, field = "call.with", name = "target", expect = "old-target", value = "new-target" },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Contains("openShapes: [\"cross\", \"axis:'Date'[Year]\"] # keep open comment", preview.ProposedText);
            Assert.Contains("target: new-target # keep with comment", preview.ProposedText);
            Assert.Contains("pinnedShapes: [grand_total] # keep pinned", preview.ProposedText);
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Nested_item_insert_and_remove_round_trip_to_the_original_exact_source(bool crlf)
        {
            using var f = new Fixture();
            var source = Nl("""
                ---
                schemaVersion: 2
                name: document-test
                title: Nested items
                version: 1
                ---
                ## Step 1: Collect
                ```yaml step
                id: collect
                ```
                Ask.
                ```yaml gate
                inputs:
                  - name: first
                    question: First?
                    type: text
                ```
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var first = Assert.Single(step.Gate.Inputs);
            var insertJson = JsonSerializer.Serialize(new
            {
                op = "insert_item", target = step.Key, field = "gate.inputs", after = first.Key,
                tempKey = "new:second", value = new { name = "second", question = "Second?", type = "text", required = "optional", scope = "run" },
            });
            var inserted = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + insertJson + "]");
            Assert.True(inserted.Outcome == "ready", inserted.Reason + ((char)10) + inserted.ProposedText);
            var insertedStep = Assert.Single(inserted.EditModel.Definition.Steps);
            var second = Assert.Single(insertedStep.Gate.Inputs, i => i.Name == "second");
            Assert.Contains(inserted.KeyChanges, change => change.OldKey == "new:second" && change.NewKey == second.Key);
            // The inserted block must arrive in the document's own newline style, not the patcher's.
            Assert.Contains(Nl("  - name: second\n    question: Second?\n    type: text\n    required: optional\n    scope: run\n", crlf), inserted.ProposedText);

            var removeJson = JsonSerializer.Serialize(new
            {
                op = "remove_item", target = second.Key, expectFingerprint = second.Fingerprint,
            });
            var removed = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + removeJson + "]", inserted.ProposedText);
            Assert.Equal("no_change", removed.Outcome);
            Assert.Equal(source, removed.ProposedText);
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Stable_step_structure_moves_copies_adds_and_removes_without_re_emitting_survivors()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Structure
                version: 1
                ---
                ## Step 1: Alpha
                ```yaml step
                id: alpha
                ```
                Alpha body. <!-- exact alpha -->
                ## Step 2: Beta
                ```yaml step
                id: beta
                ```
                Beta body. <!-- exact beta -->
                ## Step 3: Gamma
                ```yaml step
                id: gamma
                ```
                Gamma body. <!-- exact gamma -->
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var steps = doc.EditModel.Definition.Steps;
            var move = JsonSerializer.Serialize(new
            {
                op = "move_step", target = steps[2].Key, after = (string)null, expectOrder = steps.Select(s => s.Key).ToArray(),
            });
            var moved = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + move + "]");
            Assert.True(moved.Outcome == "ready", moved.Reason + ((char)10) + moved.ProposedText);
            Assert.Equal(new[] { "gamma", "alpha", "beta" }, moved.EditModel.Definition.Steps.Select(s => s.Id));
            Assert.Contains("Gamma body. <!-- exact gamma -->", moved.ProposedText);
            Assert.Contains("## Step 1: Gamma", moved.ProposedText);
            Assert.Contains("## Step 3: Beta", moved.ProposedText);

            var movedSteps = moved.EditModel.Definition.Steps;
            var copy = JsonSerializer.Serialize(new
            {
                op = "copy_step", target = movedSteps[1].Key, after = movedSteps[1].Key,
                tempKey = "new:alpha-copy", newId = "alpha-copy",
            });
            var copied = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + copy + "]", moved.ProposedText);
            Assert.True(copied.Outcome == "ready", copied.Reason + ((char)10) + copied.ProposedText);
            Assert.Equal(2, Regex.Matches(copied.ProposedText, "Alpha body[.] <!-- exact alpha -->").Count);
            Assert.Equal(new[] { "gamma", "alpha", "alpha-copy", "beta" }, copied.EditModel.Definition.Steps.Select(s => s.Id));

            var copiedSteps = copied.EditModel.Definition.Steps;
            var remove = JsonSerializer.Serialize(new
            {
                op = "remove_step", target = copiedSteps[0].Key, expectFingerprint = copiedSteps[0].Fingerprint,
            });
            var removed = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + remove + "]", copied.ProposedText);
            Assert.True(removed.Outcome == "ready", removed.Reason + ((char)10) + removed.ProposedText);
            Assert.Equal(new[] { "alpha", "alpha-copy", "beta" }, removed.EditModel.Definition.Steps.Select(s => s.Id));
            Assert.DoesNotContain("exact gamma", removed.ProposedText);
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Moving_an_input_producer_after_its_loop_is_invalid_and_keeps_disk_unchanged()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: References
                version: 1
                ---
                ## Step 1: Choose
                ```yaml step
                id: choose
                ```
                Choose.
                ```yaml gate
                inputs:
                  - name: tables
                    question: Tables?
                    type: text
                ```
                ## Step 2: Repeat
                ```yaml step
                id: repeat
                forEach:
                  in: inputs.tables
                  as: table
                ```
                Review [[loop.table]].
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var steps = doc.EditModel.Definition.Steps;
            var move = JsonSerializer.Serialize(new
            {
                op = "move_step", target = steps[0].Key, after = steps[1].Key, expectOrder = steps.Select(s => s.Key).ToArray(),
            });
            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + move + "]");
            Assert.Equal("invalid", preview.Outcome);
            Assert.Contains("after this step", preview.Reason);
            Assert.False(preview.CanApply);
            Assert.Null(preview.SuggestedNextAction);
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Positional_steps_require_visible_stabilization_and_opaque_v1_is_source_only()
        {
            using var f = new Fixture();
            var clean = """
                ---
                name: document-test
                title: Positional steps
                version: 1
                ---
                ## Step 1: Review result
                Keep this prose and spacing.__TWO_SPACES__
                ## Step 2: Review result
                Keep this too.
                """.Replace("__TWO_SPACES__", "  ");
            f.Write(clean);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var steps = doc.EditModel.Definition.Steps;
            var refusedMove = JsonSerializer.Serialize(new
            {
                op = "move_step", target = steps[1].Key, after = (string)null, expectOrder = steps.Select(s => s.Key).ToArray(),
            });
            var refused = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[" + refusedMove + "]");
            Assert.Equal("invalid", refused.Outcome);
            Assert.Contains("stabilize_step_ids", refused.Reason);
            Assert.Equal(clean, refused.ProposedText);

            var stabilized = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[{\"op\":\"stabilize_step_ids\"}]");
            Assert.True(stabilized.Outcome == "ready", stabilized.Reason + ((char)10) + stabilized.ProposedText);
            Assert.Equal(2, stabilized.EditModel.Definition.SchemaVersion);
            Assert.All(stabilized.EditModel.Definition.Steps, s => Assert.Equal("explicit-stable", s.IdKind));
            Assert.Equal(new[] { "review-result", "review-result-2" }, stabilized.EditModel.Definition.Steps.Select(s => s.Id));
            Assert.Equal(2, stabilized.KeyChanges.Length);
            Assert.Contains("Keep this prose and spacing.  ", stabilized.ProposedText);

            var opaque = clean.Replace("title: Positional steps", "title: Positional steps\nfutureGate: keep-me");
            f.Write(opaque);
            var opaqueDoc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var blocked = await f.Engine.PreviewWorkflowEditAsync(opaqueDoc.Name, opaqueDoc.ByteHash, opaqueDoc.Path, "[{\"op\":\"stabilize_step_ids\"}]");
            Assert.Equal("unpreservable", blocked.Outcome);
            Assert.Equal(opaque, blocked.ProposedText);
            Assert.Null(blocked.SuggestedNextAction);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Duplicate_v1_field_blocks_only_that_field_and_unrelated_patch_preserves_both_spellings(bool crlf)
        {
            using var f = new Fixture();
            var source = Nl("""
                ---
                name: document-test
                title: First title
                title: Winning title
                version: 1
                ---
                ## Step 1: Keep
                Keep body.
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.NotNull(doc.EditModel);
            Assert.Contains(doc.EditModel.Restrictions, r => r.Field == "title" && r.Code == "ambiguous_duplicate");
            var titleEdit = "[{\"op\":\"set_field\",\"target\":\"workflow\",\"field\":\"title\",\"expect\":\"Winning title\",\"value\":\"No\"}]";
            var blocked = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, titleEdit);
            Assert.Equal("unpreservable", blocked.Outcome);
            Assert.Equal(source, blocked.ProposedText);

            var descriptionEdit = "[{\"op\":\"set_field\",\"target\":\"workflow\",\"field\":\"description\",\"expect\":null,\"value\":\"Useful detail\"}]";
            var ready = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, descriptionEdit);
            Assert.True(ready.Outcome == "ready", ready.Reason + ((char)10) + ready.ProposedText);
            Assert.Contains(Nl("title: First title\ntitle: Winning title\n", crlf), ready.ProposedText);
            Assert.Contains(Nl("description: Useful detail\n---", crlf), ready.ProposedText);
            Assert.DoesNotContain("schemaVersion", ready.ProposedText);
        }

        [Fact]
        public async Task Empty_create_preview_projects_the_engine_skeleton_without_making_it_saveable()
        {
            using var f = new Fixture();

            var preview = await f.Engine.PreviewWorkflowEditAsync("new-flow", null, null, "[]", create: true);

            Assert.Equal("no_change", preview.Outcome);
            Assert.False(preview.CanApply);
            Assert.NotNull(preview.EditModel);
            Assert.Equal("new-flow", preview.EditModel.Definition.Name);
            Assert.Equal(2, preview.EditModel.Definition.SchemaVersion);
            Assert.Empty(preview.EditModel.Definition.Steps);
            Assert.Null(preview.SuggestedNextAction);
        }

        [Fact]
        public async Task Empty_create_draft_can_be_reprojected_and_receive_structured_edits()
        {
            using var f = new Fixture();
            var empty = await f.Engine.PreviewWorkflowEditAsync("new-flow", null, null, "[]", create: true);
            var add = JsonSerializer.Serialize(new[] { new
            {
                op = "add_step", after = (string)null, tempKey = "new:start",
                value = new { id = "start", title = "Start", instructions = "Work.", ops = Array.Empty<string>(), gate = new { } },
            } });

            var preview = await f.Engine.PreviewWorkflowEditAsync("new-flow", null, null, add, empty.ProposedText, create: true);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            var step = Assert.Single(preview.EditModel.Definition.Steps);
            Assert.Equal("start", step.Id);
            Assert.Contains(preview.KeyChanges, change => change.OldKey == "new:start"
                && change.NewKey == step.Key && change.NewStepId == "start");
            Assert.DoesNotContain("yaml gate", preview.ProposedText);
            Assert.False(File.Exists(Path.Combine(f.Workspace, ".semanticus", "workflows", "new-flow.md")));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Missing_block_fields_and_maps_are_inserted_at_their_real_yaml_column(bool crlf)
        {
            using var f = new Fixture();
            var source = Nl("""
                ---
                schemaVersion: 2
                name: document-test
                title: Block insertion
                version: 1
                ---
                ## Step 1: Hand off
                ```yaml step
                id: hand-off
                call:
                  workflow: helper-flow
                ```
                Ask.
                ```yaml gate
                inputs:
                  - name: note
                    question: Note?
                    type: text
                ```
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var input = Assert.Single(step.Gate.Inputs);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = input.Key, field = "scope", expect = (string)null, value = "run" },
                new { op = "set_map_entry", target = step.Key, field = "call.with", name = "note", expect = (string)null, value = "inputs.note" },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Contains(Nl("    scope: run\n", crlf), preview.ProposedText);
            Assert.Contains(Nl("  workflow: helper-flow\n  with:\n    note: inputs.note\n", crlf), preview.ProposedText);
            var projected = Assert.Single(preview.EditModel.Definition.Steps);
            Assert.Equal("run", Assert.Single(projected.Gate.Inputs).Scope);
            Assert.Equal("inputs.note", Assert.Single(projected.Call.With).Value);
        }

        [Fact]
        public async Task Null_removes_authored_default_and_list_fields_instead_of_refusing_their_parser_defaults()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Optional fields
                version: 1
                tags: [one]
                ---
                ## Step 1: Repeat
                ```yaml step
                id: repeat
                forEach:
                  in: [Sales]
                  as: table
                  maxIterations: 25
                ```
                Review [[loop.table]].
                ```yaml gate
                ops: [get_table]
                inputs:
                  - name: note
                    question: Note?
                    type: text
                    required: answer-or-decline
                verify:
                  - kind: dax_equivalence
                    pinnedShapes: [grand_total]
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var input = Assert.Single(step.Gate.Inputs);
            var verify = Assert.Single(step.Gate.Verify);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = "workflow", field = "version", expect = 1, value = (int?)null },
                new { op = "set_field", target = "workflow", field = "tags", expect = new[] { "one" }, value = (string[])null },
                new { op = "set_field", target = step.Key, field = "forEach.maxIterations", expect = 25, value = (int?)null },
                new { op = "set_field", target = step.Key, field = "ops", expect = new[] { "get_table" }, value = (string[])null },
                new { op = "set_field", target = input.Key, field = "type", expect = "text", value = (string)null },
                new { op = "set_field", target = input.Key, field = "required", expect = "answer-or-decline", value = (string)null },
                new { op = "set_field", target = verify.Key, field = "pinnedShapes", expect = new[] { "grand_total" }, value = (string[])null },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.DoesNotContain("version:", preview.ProposedText);
            Assert.DoesNotContain("tags:", preview.ProposedText);
            Assert.DoesNotContain("maxIterations:", preview.ProposedText);
            Assert.DoesNotContain("ops:", preview.ProposedText);
            Assert.DoesNotContain("type:", preview.ProposedText);
            Assert.DoesNotContain("required:", preview.ProposedText);
            Assert.DoesNotContain("pinnedShapes:", preview.ProposedText);
            var projected = Assert.Single(preview.EditModel.Definition.Steps);
            Assert.Equal(1, preview.EditModel.Definition.Version);
            Assert.Empty(preview.EditModel.Definition.Tags);
            Assert.Equal(ForEachSpec.DefaultMaxIterations, projected.ForEach.MaxIterations);
            Assert.Empty(projected.Ops);
            Assert.Equal("text", Assert.Single(projected.Gate.Inputs).Type);
            Assert.Equal("answer-or-decline", Assert.Single(projected.Gate.Inputs).Required);
            Assert.Empty(Assert.Single(projected.Gate.Verify).PinnedShapes);
        }

        [Fact]
        public async Task Removing_the_only_gate_item_and_an_optional_scalar_preserves_unowned_comments_and_eof()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Removal
                description: Remove me
                # keep this frontmatter comment
                version: 1
                ---
                ## Step 1: Review
                ```yaml step
                id: review
                ```
                Keep body.
                ```yaml gate
                inputs:
                  - name: note
                    question: Note?
                    type: text
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var input = Assert.Single(Assert.Single(doc.EditModel.Definition.Steps).Gate.Inputs);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = "workflow", field = "description", expect = "Remove me", value = (string)null },
                new { op = "remove_item", target = input.Key, expectFingerprint = input.Fingerprint },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.DoesNotContain("description:", preview.ProposedText);
            Assert.Contains("# keep this frontmatter comment", preview.ProposedText);
            Assert.DoesNotContain("yaml gate", preview.ProposedText);
            Assert.EndsWith("Keep body.", preview.ProposedText);
            Assert.Contains("No newline at end of file", preview.Diff);
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Bare_maps_accept_entries_at_their_authored_indent_without_duplicate_keys(bool crlf)
        {
            using var f = new Fixture();
            var source = Nl("""
                ---
                schemaVersion: 2
                name: document-test
                title: Bare maps
                version: 1
                provenance: # keep provenance comment
                ---
                ## Step 1: Hand off
                ```yaml step
                id: hand-off
                call:
                    workflow: helper-flow
                    with: # keep with comment
                ```
                Keep body.
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_map_entry", target = "workflow", field = "provenance", name = "owner", expect = (string)null, value = "finance" },
                new { op = "set_map_entry", target = step.Key, field = "call.with", name = "target", expect = (string)null, value = "Sales" },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Contains(Nl("provenance: # keep provenance comment\n  owner: finance", crlf), preview.ProposedText);
            Assert.Contains(Nl("    with: # keep with comment\n      target: Sales", crlf), preview.ProposedText);
            Assert.Single(Regex.Matches(preview.ProposedText, "provenance:").Cast<Match>());
            Assert.Single(Regex.Matches(preview.ProposedText, "with:").Cast<Match>());
        }

        // The whole contract in one place: bytes outside the edit survive, inserted text adopts the
        // document's own dominant newline style, and a stale byte hash still refuses the write.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Inserted_text_adopts_the_documents_own_newline_style_and_stale_hashes_still_conflict(bool crlf)
        {
            using var f = new Fixture();
            var cr = (char)13;
            var lf = (char)10;
            var source = Nl("""
                ---
                schemaVersion: 2
                name: document-test
                title: Newline style
                version: 1
                ---
                ## Step 1: Collect
                ```yaml step
                id: collect
                call:
                  workflow: helper-flow
                ```
                Ask.
                ```yaml gate
                inputs:
                  - name: first
                    question: First?
                    type: text
                ```
                """, crlf);
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.Equal(source, doc.ExactText);
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var first = Assert.Single(step.Gate.Inputs);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new
                {
                    op = "insert_item", target = step.Key, field = "gate.inputs", after = first.Key,
                    tempKey = "new:second",
                    value = new { name = "second", question = "Second?", type = "text", required = "optional", scope = "run" },
                },
                new { op = "set_map_entry", target = step.Key, field = "call.with", name = "note", expect = (string)null, value = "inputs.first" },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + lf + preview.ProposedText);
            Assert.Contains(Nl("  - name: second\n", crlf), preview.ProposedText);
            Assert.Contains(Nl("  with:\n    note: inputs.first\n", crlf), preview.ProposedText);
            // Every line ending in the proposal is the document's own, including the ones just inserted.
            var lineEndings = Enumerable.Range(0, preview.ProposedText.Length)
                .Where(i => preview.ProposedText[i] == lf)
                .Select(i => i > 0 && preview.ProposedText[i - 1] == cr)
                .Distinct()
                .ToArray();
            Assert.Equal(new[] { crlf }, lineEndings);
            Assert.Equal(crlf, preview.ProposedText.Contains(cr));
            // Bytes outside the edit are untouched, to the byte.
            Assert.Equal(Nl("""
                ---
                schemaVersion: 2
                name: document-test
                title: Newline style
                version: 1
                ---
                ## Step 1: Collect
                ```yaml step
                id: collect
                call:
                  workflow: helper-flow
                  with:
                    note: inputs.first
                ```
                Ask.
                ```yaml gate
                inputs:
                  - name: first
                    question: First?
                    type: text
                  - name: second
                    question: Second?
                    type: text
                    required: optional
                    scope: run
                ```
                """, crlf), preview.ProposedText);
            Assert.Equal(source, File.ReadAllText(f.FilePath));

            // A stale byte hash still conflicts on a document in either newline style.
            var external = source + Nl("Appended.\n", crlf);
            f.Write(external);
            var stale = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, preview.ProposedText, doc.Path, "human");
            Assert.False(stale.Changed);
            Assert.Contains("changed on disk", stale.Reason);
            Assert.Equal(external, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Stabilize_and_remove_execute_together_against_the_original_positional_fingerprint()
        {
            using var f = new Fixture();
            var source = """
                ---
                name: document-test
                title: Positional removal
                version: 1
                ---
                ## Step 1: Keep this
                First body.
                ## Step 2: Remove this
                Second body.
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var remove = doc.EditModel.Definition.Steps[1];
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "stabilize_step_ids" },
                new { op = "remove_step", target = remove.Key, expectFingerprint = remove.Fingerprint },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Equal("keep-this", Assert.Single(preview.EditModel.Definition.Steps).Id);
            Assert.DoesNotContain("Second body.", preview.ProposedText);
            Assert.Equal(2, preview.KeyChanges.Length);
        }

        [Fact]
        public async Task Stabilizing_an_implicit_v2_step_at_eof_keeps_the_file_unterminated()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: EOF
                version: 1
                ---
                ## Step 1: Review
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[{\"op\":\"stabilize_step_ids\"}]");

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Equal("review", Assert.Single(preview.EditModel.Definition.Steps).Id);
            Assert.EndsWith("```", preview.ProposedText);
            Assert.False(preview.ProposedText.EndsWith(((char)10).ToString(), StringComparison.Ordinal));
        }

        [Fact]
        public async Task Version_one_quoted_scalars_keep_the_exact_requested_value()
        {
            using var f = new Fixture();
            var source = """
                ---
                name: document-test
                title: 'Before' # keep title comment
                description: "Earlier"
                version: 1
                ---
                ## Step 1: Ask
                Body.
                ```yaml gate
                inputs:
                  - name: note
                    question: 'Before?'
                    type: text
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var input = Assert.Single(Assert.Single(doc.EditModel.Definition.Steps).Gate.Inputs);
            var edits = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = "workflow", field = "title", expect = "Before", value = "It's ready" },
                new { op = "set_field", target = "workflow", field = "description", expect = "Earlier", value = "Say \"go\"" },
                new { op = "set_field", target = input.Key, field = "question", expect = "Before?", value = "Who's \"ready\"?" },
            });

            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);

            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Equal("It's ready", preview.EditModel.Definition.Title);
            Assert.Equal("Say \"go\"", preview.EditModel.Definition.Description);
            Assert.Equal("Who's \"ready\"?", Assert.Single(Assert.Single(preview.EditModel.Definition.Steps).Gate.Inputs).Question);
            Assert.Contains("# keep title comment", preview.ProposedText);
        }

        [Fact]
        public async Task Flow_map_members_refuse_individual_removal_without_losing_neighbours()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Flow maps
                version: 1
                ---
                ## Step 1: Call
                ```yaml step
                id: call
                call:
                  workflow: helper-flow
                  with: {first: one, second: two}
                ```
                Body.
                ```yaml gate
                inputs: [{name: choice, question: Choose?, type: text, scope: run}]
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var input = Assert.Single(step.Gate.Inputs);
            var removeFirst = JsonSerializer.Serialize(new[]
            {
                new { op = "remove_map_entry", target = step.Key, field = "call.with", name = "first", expect = "one" },
            });
            var removeSecond = JsonSerializer.Serialize(new[]
            {
                new { op = "remove_map_entry", target = step.Key, field = "call.with", name = "second", expect = "two" },
            });
            var removeInputField = JsonSerializer.Serialize(new[]
            {
                new { op = "set_field", target = input.Key, field = "scope", expect = "run", value = (string)null },
            });

            var refusals = new[]
            {
                await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, removeFirst),
                await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, removeSecond),
                await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, removeInputField),
            };

            Assert.All(refusals, refusal =>
            {
                Assert.Equal("unpreservable", refusal.Outcome);
                Assert.Equal(source, refusal.ProposedText);
                Assert.Contains("flow-style", refusal.Reason);
            });
            Assert.Equal(source, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Rename_back_keeps_aliases_acyclic_and_refuses_stale_key_reuse()
        {
            using var f = new Fixture();
            var source = """
                ---
                schemaVersion: 2
                name: document-test
                title: Rename aliases
                version: 1
                ---
                ## Step 1: Ask
                ```yaml step
                id: ask
                ```
                Body.
                ```yaml gate
                inputs:
                  - name: first
                    question: First?
                    type: text
                  - name: third
                    question: Third?
                    type: text
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var first = Assert.Single(step.Gate.Inputs, item => item.Name == "first");
            var third = Assert.Single(step.Gate.Inputs, item => item.Name == "third");
            var secondKey = step.Key + "/input:second";
            var renameBack = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = first.Key, field = "name", expect = "first", value = "second" },
                new { op = "set_field", target = secondKey, field = "name", expect = "second", value = "first" },
                new { op = "set_field", target = secondKey, field = "question", expect = "First?", value = "Ready?" },
            });

            var ready = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, renameBack);

            Assert.True(ready.Outcome == "ready", ready.Reason + ((char)10) + ready.ProposedText);
            var renamed = Assert.Single(Assert.Single(ready.EditModel.Definition.Steps).Gate.Inputs, item => item.Name == "first");
            Assert.Equal("Ready?", renamed.Question);
            Assert.Equal(2, ready.KeyChanges.Length);

            var staleReuse = JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = first.Key, field = "name", expect = "first", value = "second" },
                new { op = "set_field", target = third.Key, field = "name", expect = "third", value = "first" },
            });
            var refused = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, staleReuse);

            Assert.Equal("invalid", refused.Outcome);
            Assert.Equal(source, refused.ProposedText);
            Assert.Contains("already identifies another item", refused.Reason);
        }

        [Fact]
        public async Task Version_one_provenance_patches_its_real_line_and_inactive_scope_routes_to_source()
        {
            using var f = new Fixture();
            var source = """
                ---
                name: document-test
                title: Version one
                owner: old # keep owner comment
                version: 1
                ---
                ## Step 1: Ask
                Body.
                ```yaml gate
                inputs:
                  - name: note
                    question: Note?
                    type: text
                ```
                """;
            f.Write(source);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var input = Assert.Single(Assert.Single(doc.EditModel.Definition.Steps).Gate.Inputs);
            var provenance = "[{\"op\":\"set_map_entry\",\"target\":\"workflow\",\"field\":\"provenance\",\"name\":\"owner\",\"expect\":\"old\",\"value\":\"new\"}]";

            var changed = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, provenance);
            var scope = JsonSerializer.Serialize(new[] { new
            {
                op = "set_field", target = input.Key, field = "scope", expect = (string)null, value = "run",
            } });
            var refused = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, scope);
            var commaList = "[{\"op\":\"set_field\",\"target\":\"workflow\",\"field\":\"tags\",\"expect\":[],\"value\":[\"one,two\"]}]";
            var unrepresentable = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, commaList);

            Assert.True(changed.Outcome == "ready", changed.Reason + ((char)10) + changed.ProposedText);
            Assert.Contains("owner: new # keep owner comment", changed.ProposedText);
            Assert.DoesNotContain("provenance:", changed.ProposedText);
            Assert.Equal("unpreservable", refused.Outcome);
            Assert.Equal(source, refused.ProposedText);
            Assert.Equal("unpreservable", unrepresentable.Outcome);
            Assert.Equal(source, unrepresentable.ProposedText);
            Assert.NotNull(refused.EditModel);
            Assert.Contains(refused.EditModel.Restrictions,
                restriction => restriction.Target == input.Key && restriction.Field == "scope");
        }

        [Fact]
        public async Task Invalid_dirty_source_and_unknown_new_fields_are_refused_without_partial_drafts()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var broken = "not workflow source";
            var repairAttempt = "[{\"op\":\"set_field\",\"target\":\"workflow\",\"field\":\"title\",\"expect\":null,\"value\":\"Hidden repair\"}]";
            var extra = JsonSerializer.Serialize(new[] { new
            {
                op = "add_step", after = (string)null, tempKey = "new:extra",
                value = new { id = "extra", title = "Extra", instructions = "Work.", ops = Array.Empty<string>(), misspelled = "lost" },
            } });

            var invalidSource = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, repairAttempt, broken);
            var invalidOperation = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, extra);
            var unknownOperation = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[{\"op\":\"set_filed\"}]");

            Assert.Equal("invalid", invalidSource.Outcome);
            Assert.Equal(broken, invalidSource.ProposedText);
            Assert.Equal("invalid", invalidOperation.Outcome);
            Assert.Equal(doc.ExactText, invalidOperation.ProposedText);
            Assert.Contains("unknown field 'misspelled'", invalidOperation.Reason);
            Assert.Equal("invalid", unknownOperation.Outcome);
            Assert.Equal(doc.ExactText, unknownOperation.ProposedText);
            Assert.Contains("Unknown workflow edit operation 'set_filed'", unknownOperation.Reason);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Create_preview_uses_the_v2_skeleton_and_create_only_writer_contract()
        {
            using var f = new Fixture();
            var add = JsonSerializer.Serialize(new
            {
                op = "add_step", after = (string)null, tempKey = "new:start",
                value = new { id = "start", title = "Start here", instructions = "Do the work.", ops = Array.Empty<string>() },
            });
            var preview = await f.Engine.PreviewWorkflowEditAsync("new-flow", null, null, "[" + add + "]", create: true);
            Assert.True(preview.Outcome == "ready", preview.Reason + ((char)10) + preview.ProposedText);
            Assert.Null(preview.Document);
            Assert.Equal(2, preview.EditModel.Definition.SchemaVersion);
            Assert.Equal("start", Assert.Single(preview.EditModel.Definition.Steps).Id);
            Assert.Equal("save_workflow", preview.SuggestedNextAction.Op);
            Assert.True(preview.SuggestedNextAction.Args.CreateOnly);
            Assert.False(File.Exists(Path.Combine(f.Workspace, ".semanticus", "workflows", "new-flow.md")));

            await f.Engine.SaveWorkflowAsync("new-flow", preview.ProposedText, "human", createOnly: true);
            var collision = await f.Engine.PreviewWorkflowEditAsync("new-flow", null, null, "[]", preview.ProposedText, create: true);
            Assert.Equal("conflict", collision.Outcome);
            Assert.Equal(preview.ProposedText, collision.ProposedText);
        }

        [Fact]
        public async Task Preview_conflict_and_invalid_source_keep_the_users_draft_and_write_nothing()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var draft = Markdown + "draft";
            f.Write(Markdown + "external");

            var stale = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[]", draft);
            Assert.Equal("conflict", stale.Outcome);
            Assert.Equal(draft, stale.ProposedText);
            Assert.Equal(Markdown + "external", stale.Document.ExactText);
            Assert.Null(stale.SuggestedNextAction);

            var current = await f.Engine.GetWorkflowDocumentAsync(doc.Name);
            var broken = "not workflow source but keep this draft";
            var invalid = await f.Engine.PreviewWorkflowEditAsync(doc.Name, current.ByteHash, current.Path, "[]", broken);
            Assert.Equal("invalid", invalid.Outcome);
            Assert.Equal(broken, invalid.ProposedText);
            Assert.Null(invalid.EditModel);
            Assert.Equal(Markdown + "external", File.ReadAllText(f.FilePath));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Raw_read_and_noop_preserve_bom_newlines_comments_and_hash(bool bom, bool crlf)
        {
            using var f = new Fixture();
            var lf = ((char)10).ToString();
            var text = (bom ? ((char)0xfeff).ToString() : "") + Markdown.TrimEnd((char)10).Replace(lf, crlf ? ((char)13).ToString() + lf : lf) + "  ";
            f.Write(text);
            var bytes = File.ReadAllBytes(f.FilePath);
            var fixedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(f.FilePath, fixedTime);
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.Equal(text, doc.ExactText);
            Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), doc.ByteHash);
            Assert.Equal(Path.GetFullPath(f.FilePath), doc.Path);
            Assert.Equal("user", doc.Library);
            Assert.True(doc.Metadata.Parses, doc.Metadata.ParseError);
            Assert.Equal(2, doc.Metadata.SchemaVersion);
            Assert.Equal(7, doc.Metadata.Version);
            Assert.True(doc.Metadata.ExplicitIds);
            Assert.Equal(new[] { "first" }, doc.Metadata.StepIds);
            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, "[]");
            Assert.Equal("no_change", preview.Outcome);
            Assert.Equal(doc.ExactText, preview.ProposedText);
            Assert.Equal(doc.ByteHash, preview.ProposedByteHash);
            Assert.Empty(preview.Diff);
            Assert.Null(preview.SuggestedNextAction);
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, doc.ExactText, doc.Path, "human");
            Assert.False(result.Changed);
            Assert.Equal(text, result.Document.ExactText);
            Assert.Equal(bytes, File.ReadAllBytes(f.FilePath));
            Assert.Equal(fixedTime, File.GetLastWriteTimeUtc(f.FilePath));
            Assert.Equal(0, broadcasts);
        }

        [Fact]
        public async Task Changed_edit_persists_exact_bytes_broadcasts_once_and_keeps_run_snapshot()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var run = await f.Engine.StartWorkflowAsync("document-test", "human");
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var step = Assert.Single(doc.EditModel.Definition.Steps);
            var edits = JsonSerializer.Serialize(new[] { new
            {
                op = "set_field", target = step.Key, field = "instructions",
                expect = step.Instructions, value = "Changed café text.",
            } });
            var preview = await f.Engine.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
            Assert.Equal("ready", preview.Outcome);
            var changed = preview.ProposedText;
            var edit = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, changed, doc.Path, "human");
            Assert.True(edit.Changed, edit.Reason);
            Assert.Equal(changed, edit.Document.ExactText);
            Assert.Equal(edit.ByteHash, edit.Document.ByteHash);
            Assert.NotEqual(doc.ByteHash, edit.ByteHash);
            Assert.Equal(new UTF8Encoding(false, true).GetBytes(changed), File.ReadAllBytes(f.FilePath));
            Assert.Equal(1, broadcasts);
            Assert.Equal(run.CurrentStep.Instructions, (await f.Engine.GetWorkflowRunAsync(run.RunId)).CurrentStep.Instructions);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.FilePath)!, "*.tmp"));
        }

        [Fact]
        public async Task Stale_hash_returns_current_source_and_never_writes_the_draft()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var external = Markdown + " external edit";
            f.Write(external);
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " draft", doc.Path, "human");
            Assert.False(result.Changed);
            Assert.Contains("changed on disk", result.Reason);
            Assert.Equal(external, result.Document.ExactText);
            Assert.Equal(result.ByteHash, result.Document.ByteHash);
            Assert.NotEqual(doc.ByteHash, result.ByteHash);
            Assert.Equal(external, File.ReadAllText(f.FilePath));
            Assert.Equal(0, broadcasts);
        }

        [Theory]
        [InlineData("missing-hash")]
        [InlineData("missing-path")]
        [InlineData("wrong-path")]
        [InlineData("broken")]
        [InlineData("renamed")]
        [InlineData("invalid-unicode")]
        public async Task Invalid_edits_return_current_document_without_writing(string scenario)
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var proposed = scenario == "broken" ? "not markdown frontmatter" : scenario == "renamed"
                ? Markdown.Replace("name: document-test", "name: another-name") : scenario == "invalid-unicode"
                ? Markdown + (char)0xd800 : Markdown + " edited";
            var expectPath = scenario == "missing-path" ? "" : scenario == "wrong-path" ? doc.Path + ".other" : doc.Path;
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, scenario == "missing-hash" ? "" : doc.ByteHash, proposed, expectPath, "agent");
            Assert.False(result.Changed);
            Assert.Equal(doc.ByteHash, result.ByteHash);
            Assert.Equal(Markdown, result.Document.ExactText);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Invalid_saved_syntax_remains_readable_and_can_be_repaired()
        {
            using var f = new Fixture();
            f.Write("broken saved source");
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.False(doc.Metadata.Parses);
            Assert.NotNull(doc.Metadata.ParseError);
            Assert.Equal("broken saved source", doc.ExactText);
            var edit = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown, doc.Path, "human");
            Assert.True(edit.Changed, edit.Reason);
            Assert.True(edit.Document.Metadata.Parses);
        }

        [Fact]
        public async Task Invalid_utf8_refuses_read_and_edit_without_replacement_bytes()
        {
            using var f = new Fixture();
            var bytes = new byte[] { 0xff, 0xfe, 0x61 };
            File.WriteAllBytes(f.FilePath, bytes);
            var read = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.GetWorkflowDocumentAsync("document-test"));
            Assert.Contains("UTF-8", read.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.EditWorkflowDocumentAsync("document-test", "hash", Markdown, f.FilePath, "human"));
            Assert.Equal(bytes, File.ReadAllBytes(f.FilePath));
        }

        [Fact]
        public async Task Stock_is_read_only_and_copy_creation_never_replaces_a_newer_project_copy()
        {
            using var f = new Fixture();
            var stock = await f.Engine.GetWorkflowDocumentAsync("new-measure");
            Assert.Equal("stock", stock.Library);
            var stockEdit = JsonSerializer.Serialize(new[] { new
            {
                op = "set_field", target = "workflow", field = "title",
                expect = stock.EditModel.Definition.Title, value = stock.EditModel.Definition.Title + " custom",
            } });
            var stockPreview = await f.Engine.PreviewWorkflowEditAsync(stock.Name, stock.ByteHash, stock.Path, stockEdit);
            Assert.Equal("stock_read_only", stockPreview.Outcome);
            Assert.False(stockPreview.CanApply);
            Assert.Null(stockPreview.SuggestedNextAction);
            Assert.EndsWith(" custom", stockPreview.EditModel.Definition.Title);
            var refused = await f.Engine.EditWorkflowDocumentAsync(stock.Name, stock.ByteHash, stock.ExactText + " edit", stock.Path, "human");
            Assert.False(refused.Changed);
            Assert.Contains("read-only", refused.Reason);
            Assert.Equal(stock.ExactText, refused.Document.ExactText);
            await f.Engine.SaveWorkflowAsync(stock.Name, stock.ExactText + " newer copy", "agent", createOnly: true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.SaveWorkflowAsync(stock.Name, stock.ExactText, "human", createOnly: true));
            Assert.Contains("already exists", error.Message);
            Assert.EndsWith(" newer copy", (await f.Engine.GetWorkflowDocumentAsync(stock.Name)).ExactText);
            await f.Engine.DeleteWorkflowAsync(stock.Name, "human");
            Assert.Equal("stock", (await f.Engine.GetWorkflowDocumentAsync(stock.Name)).Library);
        }

        [Theory]
        [InlineData("../document-test")]
        [InlineData("/tmp/document-test")]
        [InlineData("two/paths")]
        public async Task Every_document_writer_and_reader_rejects_path_traversal(string name)
        {
            using var f = new Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.GetWorkflowDocumentAsync(name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.EditWorkflowDocumentAsync(name, "hash", Markdown, f.FilePath, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.SaveWorkflowAsync(name, Markdown, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.DeleteWorkflowAsync(name, "human"));
        }

        [Fact]
        public async Task Same_hash_competing_edits_have_one_winner_across_engine_instances()
        {
            using var f = new Fixture();
            using var otherSessions = new SessionManager();
            var other = new LocalEngine(otherSessions, TestEntitlements.Pro, f.Workspace);
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var results = await Task.WhenAll(
                Task.Run(() => f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " first", doc.Path, "human")),
                Task.Run(() => other.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " second", doc.Path, "agent")));
            Assert.Single(results, r => r.Changed);
            Assert.Single(results, r => !r.Changed);
            Assert.Equal(results.Single(r => r.Changed).Document.ExactText, File.ReadAllText(f.FilePath));
        }

        [Theory]
        [InlineData("read")]
        [InlineData("preview")]
        [InlineData("edit")]
        [InlineData("save")]
        [InlineData("delete")]
        public async Task Queued_document_operation_refuses_after_context_replacement(string operation)
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var before = f.Sessions.CurrentContext;
            await before.WorkflowGate.WaitAsync();
            Task<WorkflowEditPreviewResult> previewPending = null;
            Task pending;
            if (operation == "preview") pending = previewPending = f.Engine.PreviewWorkflowEditAsync(
                doc.Name, doc.ByteHash, doc.Path, "[]", Markdown + " draft");
            else pending = operation switch
            {
                "read" => f.Engine.GetWorkflowDocumentAsync(doc.Name),
                "edit" => f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " edit", doc.Path, "human"),
                "save" => f.Engine.SaveWorkflowAsync(doc.Name, Markdown + " save", "human"),
                _ => f.Engine.DeleteWorkflowAsync(doc.Name, "human"),
            };
            Assert.False(pending.IsCompleted);
            f.Sessions.ExchangeRetiredContext(new SessionContext(null));
            before.WorkflowGate.Release();
            if (previewPending != null)
            {
                var refusal = await previewPending;
                Assert.Equal("conflict", refusal.Outcome);
                Assert.Equal(Markdown + " draft", refusal.ProposedText);
                Assert.Contains(refusal.Issues, issue => issue.Code == "stale_session");
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                Assert.Contains("model changed", error.Message);
            }
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
            before.Dispose();
        }

        [Fact]
        public async Task Queued_edit_keeps_its_original_project_when_the_same_session_moves()
        {
            using var f = new Fixture();
            var session = await f.Sessions.CreateAsync("Document model", 1600);
            session.SourcePath = Path.Combine(f.Workspace, "model.bim");
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var context = f.Sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            var pending = f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old project edit", doc.Path, "human");
            Assert.False(pending.IsCompleted);
            var secondRoot = Path.Combine(f.Workspace, "second-project");
            var secondFile = Path.Combine(secondRoot, ".semanticus", "workflows", "document-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
            File.WriteAllText(secondFile, Markdown);
            session.SourcePath = Path.Combine(secondRoot, "model.bim");
            context.WorkflowGate.Release();
            var result = await pending;
            Assert.True(result.Changed, result.Reason);
            Assert.Equal(f.FilePath, result.Document.Path);
            Assert.EndsWith(" old project edit", File.ReadAllText(f.FilePath));
            Assert.Equal(Markdown, File.ReadAllText(secondFile));
        }

        [Fact]
        public async Task Mcp_and_named_pipe_edit_the_same_document_and_honor_session_targeting()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var pipe = "semanticus-document-" + Guid.NewGuid().ToString("N");
            using var server = new RpcServer(f.Sessions, f.Engine, pipe);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            try
            {
                using var remote = await RemoteEngine.ConnectAsync(pipe);
                var doc = await remote.GetWorkflowDocumentAsync("document-test");
                var step = Assert.Single(doc.EditModel.Definition.Steps);
                var edits = JsonSerializer.Serialize(new[] { new
                {
                    op = "set_field", target = step.Key, field = "instructions",
                    expect = step.Instructions, value = "Door-parity edit.",
                } });
                var rpcProjection = await remote.PreviewWorkflowEditAsync(doc.Name, doc.ByteHash, doc.Path, edits);
                var mcpProjection = await McpTools.PreviewWorkflowEdit(remote, doc.Name, doc.ByteHash, doc.Path, edits);
                Assert.Equal("ready", rpcProjection.Outcome);
                Assert.Equal(rpcProjection.ProposedText, mcpProjection.ProposedText);
                Assert.Equal(rpcProjection.ProposedByteHash, mcpProjection.ProposedByteHash);
                Assert.Equal(JsonSerializer.Serialize(rpcProjection.EditModel), JsonSerializer.Serialize(mcpProjection.EditModel));
                Assert.Equal(JsonSerializer.Serialize(rpcProjection.Issues), JsonSerializer.Serialize(mcpProjection.Issues));
                Assert.Equal(JsonSerializer.Serialize(rpcProjection.KeyChanges), JsonSerializer.Serialize(mcpProjection.KeyChanges));
                Assert.Equal(rpcProjection.Diff, mcpProjection.Diff);
                var wrongFile = await McpTools.EditWorkflowDocument(remote, doc.Name, doc.ByteHash, doc.ExactText, doc.Path + ".other");
                Assert.False(wrongFile.Changed);
                Assert.Contains("different file", wrongFile.Reason);
                var agentEdit = await McpTools.EditWorkflowDocument(remote, doc.Name, doc.ByteHash, mcpProjection.ProposedText, doc.Path);
                Assert.True(agentEdit.Changed, agentEdit.Reason);
                var humanRead = await f.Engine.GetWorkflowDocumentAsync(doc.Name);
                Assert.Equal(agentEdit.ByteHash, humanRead.ByteHash);
                var stale = await remote.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old human draft", doc.Path, "human");
                Assert.False(stale.Changed);
                Assert.Equal(humanRead.ExactText, stale.Document.ExactText);
                await Assert.ThrowsAnyAsync<Exception>(() => remote.GetWorkflowDocumentAsync(doc.Name, "stale-session"));
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.EditWorkflowDocument(remote, doc.Name, humanRead.ByteHash, Markdown, humanRead.Path, "stale-session"));
                Assert.Equal(humanRead.ExactText, File.ReadAllText(f.FilePath));
            }
            finally { cts.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }

        [Fact]
        public async Task Create_preview_session_fence_survives_named_pipe_and_mcp_save()
        {
            using var f = new Fixture();
            var firstRoot = Path.Combine(f.Workspace, "first-project");
            var first = await f.Sessions.CreateAsync("First model", 1600);
            first.SourcePath = Path.Combine(firstRoot, "model.bim");
            var pipe = "semanticus-document-save-" + Guid.NewGuid().ToString("N");
            using var server = new RpcServer(f.Sessions, f.Engine, pipe);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            try
            {
                using var remote = await RemoteEngine.ConnectAsync(pipe);
                const string name = "session-bound";
                var markdown = Markdown.Replace("document-test", name, StringComparison.Ordinal);
                var preview = await remote.PreviewWorkflowEditAsync(
                    name, null, null, "[]", markdown, create: true, sessionId: first.Id);
                Assert.Equal("ready", preview.Outcome);
                Assert.Equal(first.Id, preview.SuggestedNextAction.Args.SessionId);

                var secondRoot = Path.Combine(f.Workspace, "second-project");
                var second = await f.Sessions.CreateAsync("Second model", 1600);
                second.SourcePath = Path.Combine(secondRoot, "model.bim");
                var firstFile = Path.Combine(firstRoot, ".semanticus", "workflows", name + ".md");
                var secondFile = Path.Combine(secondRoot, ".semanticus", "workflows", name + ".md");

                await Assert.ThrowsAnyAsync<Exception>(() => remote.SaveWorkflowAsync(
                    name, preview.ProposedText, "human", createOnly: true, sessionId: first.Id));
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveWorkflow(
                    remote, name, preview.ProposedText, createOnly: true, sessionId: first.Id));
                Assert.False(File.Exists(firstFile));
                Assert.False(File.Exists(secondFile));

                var saved = await McpTools.SaveWorkflow(
                    remote, name, preview.ProposedText, createOnly: true, sessionId: second.Id);
                Assert.Contains(saved, item => item.Name == name);
                Assert.False(File.Exists(firstFile));
                Assert.Equal(preview.ProposedText, File.ReadAllText(secondFile));
            }
            finally { cts.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_then_project_change_refuses_to_edit_identical_file_in_new_project(bool replaceSession)
        {
            using var f = new Fixture();
            var first = await f.Sessions.CreateAsync("First model", 1600);
            first.SourcePath = Path.Combine(f.Workspace, "model.bim");
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var secondRoot = Path.Combine(f.Workspace, "second-project");
            var secondFile = Path.Combine(secondRoot, ".semanticus", "workflows", "document-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
            File.WriteAllText(secondFile, Markdown);
            var current = replaceSession ? await f.Sessions.CreateAsync("Second model", 1600) : first;
            current.SourcePath = Path.Combine(secondRoot, "model.bim");

            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old draft", doc.Path, "human");

            Assert.False(result.Changed);
            Assert.Contains("different file", result.Reason);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
            Assert.Equal(Markdown, File.ReadAllText(secondFile));
        }
    }
}
