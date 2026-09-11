using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol round-2 findings against format v2 slice 1 (branch head 1a22d561, the FIX commit for round 1).
    /// One test per finding, written from Sol's own concrete input and watched fail before anything changed.
    ///
    /// Three of the four are one family: a refusal added in the round-1 fixes fires on input that was legal
    /// before it. Over-refusal is the worse direction of error here, because the whole point of the v1
    /// ruleset is that a file which loaded yesterday loads forever, and because a static check that rejects
    /// a legal v2 file makes check_workflow lie about a file that would run.
    ///
    /// The R5/R6 sections are Sol ROUND 3 against the round-2 fix commit (3f99679d), one dimension: did those
    /// corrections overshoot into the opposite error. Both are UNDER-refusal, the direction that lets the
    /// round-1 defect back in through a hole, and they live here rather than in a class of their own because
    /// each one extends a round-2 guard that was too narrow.
    /// </summary>
    public sealed class WorkflowV2SolRoundTwoTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine e, string ws) Make(IEntitlement ent = null)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-sol2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), ent ?? new Free(), ws), ws);
        }

        private static string Md(params string[] lines) => string.Join("\n", lines);

        /// <summary>Reads a fixture from <c>Semanticus.Tests/fixtures/workflow-v1-shapes</c> BY FILE, so a
        /// test that claims to pin a fixture's shape breaks when that shape is edited away. Tests here used
        /// to retype the shape inline and merely resemble the fixture, which let two fixtures be gutted with
        /// every named test still green (Sol round 7, findings 4 and 5). README.md in that directory maps
        /// fixture to test, and <c>WorkflowV1CompatibilityTests</c> proves each mapping mechanically by
        /// checking the named test's body actually mentions the file.</summary>
        private static string Fixture(string stem)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            var path = Path.Combine(dir!.FullName, "Semanticus.Tests", "fixtures", "workflow-v1-shapes", stem + ".md");
            Assert.True(File.Exists(path), "missing fixture: " + path);
            return File.ReadAllText(path);
        }

        // ---- finding 1: the whole-frontmatter version scan refuses a v1 file it used to accept ----------

        [Fact]
        public void R1_a_v1_file_repeating_the_same_schema_version_still_loads()
        {
            // Sol's input verbatim. Before v2 existed, `schemaVersion` was not a key at all: both copies were
            // ordinary unknown keys, preserved-and-ignored, and the file loaded. The round-1 duplicate refusal
            // was written for the case it is actually needed for (two DIFFERENT versions, where the file does
            // not say which ruleset it wants) and caught this one on the way past.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 1", "name: t", "schemaVersion: 1", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file repeating 'schemaVersion: 1' was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Fact]
        public void R1_sibling_a_v1_file_may_declare_its_version_after_another_key()
        {
            // The sibling Sol did not name, same cause and same line: the must-be-first ordering rule is a v2
            // strictness rule, and it fires on a v1 file that loaded before v2 existed.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "schemaVersion: 1", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file declaring schemaVersion after 'name' was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Theory]
        // A v2 file with a second declaration: still refused, in either order and whatever the second value
        // is. v2 is strict by design and has no back-compat debt. The refusal MOVED to ParseFrontmatter in
        // round 5 (see R5) but the verdict on every one of these is unchanged.
        [InlineData("schemaVersion: 2|name: t|schemaVersion: 1")]
        [InlineData("schemaVersion: 2|name: t|schemaVersion: 2")]
        [InlineData("schemaVersion: 2|name: t|schemaVersion: 9")]
        // the must-be-first rule still holds for a v2 file.
        [InlineData("name: t|schemaVersion: 2")]
        public void R1_controls_the_refusals_that_must_survive(string pipeSeparatedFrontmatter)
        {
            var frontmatter = pipeSeparatedFrontmatter.Split('|');
            var def = WorkflowParser.Parse(Md(
                new[] { "---" }.Concat(frontmatter).Concat(new[] { "---", "## Step 1: Do", "Body." }).ToArray()));
            Assert.True(def.Error != null, "expected a refusal for: " + string.Join(" / ", frontmatter));
            Assert.Contains("schemaVersion", def.Error);
        }

        // ---- finding 2: a comment after a v2 list key is read as a value and refused --------------------

        [Fact]
        public void R2_a_comment_after_a_v2_gate_list_key_is_not_a_value()
        {
            // Trimming turns `inputs: # questions` into the value `# questions`, and the inline-comment cut
            // only ever looked for " #" INSIDE a value, so a value that is nothing but a comment was never
            // recognised as one. The round-1 refusal of a value on a list key then fired on it.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Ask", "Body.",
                "```yaml gate", "strictness: hard", "inputs: # questions",
                "  - name: approval", "    question: Approved?", "```"));
            Assert.True(def.Error == null, "a comment after 'inputs:' was read as a value: " + def.Error);
            Assert.Equal(new[] { "approval" }, def.Steps[0].Gate!.Inputs.Select(i => i.Name).ToArray());
        }

        [Fact]
        public void R2_family_a_whole_line_comment_value_is_a_comment_in_every_v2_scope()
        {
            // The family, not the instance: the same trimming happens on every key in every scope, so the
            // fix belongs in the splitter and the guard enumerates the places it shows up.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Ask", "Body.",
                "```yaml gate", "strictness: hard", "ops: # none yet", "verify: # checks",
                "  - kind: bpa_clean", "```",
                "## Step 2: Loop",
                "```yaml step", "id: each", "forEach: # over the tables", "  in: [Sales, Product]", "  as: table",
                "```", "", "Body."));
            Assert.True(def.Error == null, "a comment-only value was read as a value: " + def.Error);
            Assert.Equal(new[] { "bpa_clean" }, def.Steps[0].Gate!.Verify.Select(v => v.Kind).ToArray());
            Assert.Equal("table", def.Steps[1].ForEach!.As);
        }

        [Fact]
        public void R2_control_a_real_value_on_a_v2_gate_list_key_is_still_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Ask", "Body.",
                "```yaml gate", "strictness: hard", "verify: bpa_clean", "```"));
            Assert.True(def.Error != null, "'verify: bpa_clean' was accepted and the value dropped");
            Assert.Contains("verify", def.Error);
        }

        [Fact]
        public void R2_control_v1_reads_a_comment_shaped_value_exactly_as_it_always_did()
        {
            // v1-forever cuts BOTH ways. A v1 file that stored '# questions' as an unknown key's value has to
            // keep storing it, so the comment fix is v2-only. Widening it to v1 would turn a file whose
            // `name:` is comment-shaped into a file with no name, which is the same over-refusal in reverse.
            //
            // Read from the FIXTURE, and asserting BOTH halves it carries. The inline version of this test
            // checked only a top-level value, so deleting the fixture's `inputs: # ...` gate line left every
            // test green while the README claimed that shape was pinned (Sol round 7, finding 5).
            var def = WorkflowParser.Parse(Fixture("comment-shaped-values"));
            Assert.Null(def.Error);
            // half one: a comment-shaped value on a top-level key is a VALUE in v1
            Assert.Equal("# this is a value in v1, not a comment", def.Description);
            Assert.Equal("# so is this one", def.Provenance["note"]);
            // half two: on a gate LIST key, v1 drops the value and still reads the list beneath it. This is
            // the exact line v2 now treats as a comment (R2), so the two rulesets are pinned against the
            // same bytes.
            Assert.Contains("inputs: # ", Fixture("comment-shaped-values"));
            Assert.Equal(new[] { "approval" }, def.Steps[0].Gate!.Inputs.Select(i => i.Name).ToArray());
        }

        // ---- finding 3: the round-1 family guard does not observe what it claims -----------------------

        [Fact]
        public void R3_a_v1_gate_key_reserved_in_v2_is_ignored_not_preserved()
        {
            // Sol's input. `successCriteria` is reserved in the GATE scope, and a v1 gate's unknown keys are
            // ignored outright (they never reach Provenance, which only frontmatter writes to). So the round-1
            // guard's "preserved into Provenance" is the wrong observation for every key whose real scope is
            // the gate or the step; it only ever tested those keys in the frontmatter, where they are plain
            // unknown keys and always were. This test states the RIGHT observation for the gate scope: the
            // file loads, the gate parses, and the reserved key has no effect.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Do", "Body.",
                "```yaml gate", "strictness: hard", "successCriteria: legacy",
                "inputs:", "  - name: approval", "    question: Approved?", "```"));
            Assert.True(def.Error == null, "v1 refused a gate key that v2 reserves: " + def.Error);
            Assert.Equal("hard", def.Steps[0].Gate!.Strictness);
            Assert.Equal(new[] { "approval" }, def.Steps[0].Gate!.Inputs.Select(i => i.Name).ToArray());
            Assert.False(def.Provenance.ContainsKey("successCriteria"),
                "a gate key reached Provenance, which only frontmatter writes to");
        }

        [Fact]
        public void R3_every_reserved_key_is_inert_in_v1_in_its_own_scope()
        {
            // Enumerated from the shared constant, so a reservation added later cannot skip this guard, and
            // each key is exercised in the scope it is actually reserved in.
            foreach (var r in WorkflowParser.ReservedDestinationKeys.Concat(new[] { WorkflowParser.ReservedAfterKey }))
            {
                var def = r.Scope switch
                {
                    "frontmatter" => WorkflowParser.Parse(Md(
                        "---", "name: t", r.Key + ": legacy", "---", "## Step 1: Do", "Body.")),
                    "gate" => WorkflowParser.Parse(Md(
                        "---", "name: t", "---", "## Step 1: Do", "Body.",
                        "```yaml gate", "strictness: hard", r.Key + ": legacy", "```")),
                    // A `yaml step` fence is inert TEXT in v1 (there is no v2 step block to read), so the v1
                    // observation for a step-scoped key is that the file loads and the block stays in the body.
                    _ => WorkflowParser.Parse(Md(
                        "---", "name: t", "---", "## Step 1: Do",
                        "```yaml step", "id: do-it", r.Key + ": legacy", "```", "", "Body.")),
                };
                Assert.True(def.Error == null, $"v1 refused reserved key '{r.Key}' in scope '{r.Scope}': {def.Error}");
                if (r.Scope == "frontmatter")
                    Assert.Equal("legacy", def.Provenance[r.Key]);
                else
                    Assert.False(def.Provenance.ContainsKey(r.Key),
                        $"'{r.Key}' is scoped '{r.Scope}' but reached Provenance");
                if (r.Scope == "step")
                    Assert.Contains(r.Key, def.Steps[0].Instructions ?? "");
            }
        }

        [Fact]
        public void R3_every_reserved_key_is_refused_in_v2_in_its_own_scope()
        {
            // The other half of the same guard: reserved means refused WITH ITS OWN REASON in v2, in the scope
            // the constant names. Round 1 asserted the v1 half only, so nothing checked that the scope column
            // on each reservation is the scope the parser actually enforces it in.
            foreach (var r in WorkflowParser.ReservedDestinationKeys.Concat(new[] { WorkflowParser.ReservedAfterKey }))
            {
                var def = r.Scope switch
                {
                    "frontmatter" => WorkflowParser.Parse(Md(
                        "---", "schemaVersion: 2", "name: t", "title: T", r.Key + ": legacy", "---",
                        "## Step 1: Do", "Body.")),
                    "gate" => WorkflowParser.Parse(Md(
                        "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do", "Body.",
                        "```yaml gate", "strictness: hard", r.Key + ": legacy", "```")),
                    _ => WorkflowParser.Parse(Md(
                        "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                        "```yaml step", "id: do-it", r.Key + ": legacy", "```", "", "Body.")),
                };
                Assert.True(def.Error != null, $"v2 accepted reserved key '{r.Key}' in scope '{r.Scope}'");
                Assert.Contains(r.Key, def.Error);
            }
        }

        // ---- finding 4: a `returns:` name is not collected into the caller's namespace ------------------

        private static async Task<LocalEngine> LibraryWithACallAsync()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("sub-approve", Md(
                "---", "schemaVersion: 2", "name: sub-approve", "title: Sub approve", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```"), "human");
            await e.SaveWorkflowAsync("sub-note", Md(
                "---", "schemaVersion: 2", "name: sub-note", "title: Sub note", "---",
                "## Step 1: Note", "Body.",
                "```yaml gate", "inputs:", "  - name: target", "    question: Which one?", "```"), "human");
            // Sol's two cases in one caller: a `when:` reading the returned answer, and a `with:` feeding it
            // onward. Step 1 is the only place `approval` is declared, and it is declared by the CALLEE.
            await e.SaveWorkflowAsync("caller-x", Md(
                "---", "schemaVersion: 2", "name: caller-x", "title: Caller", "---",
                "## Step 1: Get approval",
                "```yaml step", "id: get-approval", "call:", "  workflow: sub-approve",
                "  returns: [approval]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body.",
                "## Step 3: Hand on",
                "```yaml step", "id: hand-on", "call:", "  workflow: sub-note", "  with:",
                "    target: inputs.approval", "```", "", "Body."), "human");
            return e;
        }

        [Fact]
        public async Task R4_a_returned_answer_resolves_in_the_caller()
        {
            // docs/workflow-canvas-spec.md:499-500: "`returns:` lists callee input names whose answers come
            // back into the caller's namespace under the same name." So both references are legal and neither
            // check may report them unresolvable. A static check that refuses a file which would run is worse
            // than no check at all, because it teaches the author to stop reading check_workflow.
            var e = await LibraryWithACallAsync();
            var r = await e.CheckWorkflowAsync("caller-x");
            var bogus = r.Findings.Where(f => f.Message.Contains("approval") && f.Message.Contains("no gate")).ToArray();
            Assert.True(bogus.Length == 0, "a returned answer was reported unresolvable: "
                + string.Join(" | ", bogus.Select(f => f.Severity + ": " + f.Message)));
        }

        [Fact]
        public async Task R4_sibling_a_returned_answer_resolves_in_a_verify_too()
        {
            // The sibling Sol did not name, same cause one file over: check_workflow's OWN run-wide input set
            // (LocalEngine.Workflows.cs) is built from gate declarations alone, so a verify `when:` or
            // `probe:` naming a returned answer is reported unresolvable exactly the same way.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("sub-approve", Md(
                "---", "schemaVersion: 2", "name: sub-approve", "title: Sub approve", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```"), "human");
            await e.SaveWorkflowAsync("caller-z", Md(
                "---", "schemaVersion: 2", "name: caller-z", "title: Caller", "---",
                "## Step 1: Get approval",
                "```yaml step", "id: get-approval", "call:", "  workflow: sub-approve",
                "  returns: [approval]", "```", "", "Body.",
                "## Step 2: Check", "Body.",
                "```yaml gate", "verify:", "  - kind: bpa_clean", "    when: inputs.approval.answered", "```"), "human");
            var r = await e.CheckWorkflowAsync("caller-z");
            var bogus = r.Findings.Where(f => f.Message.Contains("approval") && f.Message.Contains("no gate collects")).ToArray();
            Assert.True(bogus.Length == 0, "a returned answer was reported unresolvable in a verify: "
                + string.Join(" | ", bogus.Select(f => f.Severity + ": " + f.Message)));
        }

        // ---- ROUND 3, finding 1: the version scan and the frontmatter parser disagree about what a key is --

        [Theory]
        // A SECOND, DIFFERENT version hidden by one space. Still refused, but the refusal MOVED in round 5:
        // it now comes from ParseFrontmatter as a duplicate, not from the version scan. That is not a
        // loosening. The scan is version-independent by design and looks only at column-zero lines before
        // the first nested line, so it never sees this line at all; the reader does see it, knows the
        // version and the block structure, and refuses it there. Same input, same verdict, different site
        // and a different message, which is the price of a scan that cannot contradict itself.
        [InlineData("schemaVersion: 2|name: t|  schemaVersion: 1")]
        public void R5_an_indented_line_is_a_key_to_the_version_scan_too(string pipeSeparatedFrontmatter)
        {
            var frontmatter = pipeSeparatedFrontmatter.Split('|');
            var def = WorkflowParser.Parse(Md(
                new[] { "---" }.Concat(frontmatter).Concat(new[] { "---", "## Step 1: Do", "Body." }).ToArray()));
            Assert.True(def.Error != null, "an indented frontmatter line escaped the version scan: "
                + string.Join(" / ", frontmatter));
            // [T217]: an indented key under a scalar is invalid YAML, so the refusal is the library's and
            // names the line. Either way the second declaration cannot slip through, which is the finding.
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void R5_control_v1_stays_tolerant_of_an_indented_repeat()
        {
            // The round-2 direction must survive round 3's tightening: widening WHAT the scan sees must not
            // widen WHICH versions it refuses. A v1 file repeating the same version, indented or not, loads.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 1", "name: t", "  schemaVersion: 1", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file with an indented repeat was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Fact]
        public void R5_control_a_template_slot_list_is_not_read_as_a_version()
        {
            // The one shape that made the indent skip look reasonable: a `slots:` block's indented lines. They
            // are keys to the flat scan too, and that is fine, because none of them is `schemaVersion`.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "slots:", "  - name: customer", "    question: Which customer?",
                "---", "## Step 1: Do", "Body about {{customer}}."));
            Assert.True(def.Error == null, "a template with a slot block was refused: " + def.Error);
            Assert.Equal(2, def.SchemaVersion);
        }

        // ---- ROUND 4, finding 3: the widened scan broke a v1 template ----------------------------------

        [Fact]
        public void R7_a_v1_template_slot_may_carry_a_schema_version_property()
        {
            // Sol's input, and a v1-FOREVER break introduced by the round-3 widening. `slots:` opens the one
            // nested structure a v1 file has, so `    schemaVersion: 2` inside a slot item is a slot property
            // that v1 ignores. The widened scan read it as the file's version and refused the file for
            // declaring it after `name`. This is round 1's finding 1 all over again, arriving through the
            // fix for round 3.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "kind: template", "title: T",
                "slots:",
                "  - name: customer",
                "    question: Which customer?",
                "    schemaVersion: 2",
                "---", "## Step 1: Do", "Body about {{customer}}."));
            Assert.True(def.Error == null, "a v1 template with a slot property named schemaVersion was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal(new[] { "customer" }, def.Slots.Select(s => s.Name).ToArray());
        }

        [Fact]
        public void R7_the_two_readers_agree_about_a_list_item_line()
        {
            // The other shape Sol named. Here the two already AGREE and this pins that they do: the reader
            // that builds the def splits `  - schemaVersion: 2` into the key '- schemaVersion', which is not
            // the version key, so v1 keeps it as an ordinary unknown key and the scan is right to pass over
            // it. Recorded as a test rather than argued in a report, because "they agree" is a claim too.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "  - schemaVersion: 2", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 list-item line was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("2", def.Provenance["- schemaVersion"]);
        }

        [Fact]
        public void R7_a_real_top_level_declaration_inside_a_template_still_counts()
        {
            // The control the round-3 one should have been: the slot block must hide a slot property, and
            // nothing else. A genuine top-level second version in the same file is still refused.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "slots:",
                "  - name: customer",
                "    question: Which customer?",
                "    schemaVersion: 2",
                "schemaVersion: 1",
                "---", "## Step 1: Do", "Body about {{customer}}."));
            Assert.True(def.Error != null, "a real second declaration after a slots block was missed");
            Assert.Contains("schemaVersion", def.Error);
        }

        // ---- ROUND 4, finding 2: a legal three-level return chain -------------------------------------

        [Fact]
        public async Task R8_a_return_relayed_through_two_hand_offs_resolves()
        {
            // C declares it, B brings it back, A brings back what B has. B's namespace legally contains the
            // name (that is what round 3 established), but both checks read B's own gates only, so A was
            // told twice that a name it can really see does not exist.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("chain-c", Md(
                "---", "schemaVersion: 2", "name: chain-c", "title: C", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: result", "    question: What is it?", "```"), "human");
            await e.SaveWorkflowAsync("chain-b", Md(
                "---", "schemaVersion: 2", "name: chain-b", "title: B", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: to-c", "call:", "  workflow: chain-c",
                "  returns: [result]", "```", "", "Body."), "human");
            await e.SaveWorkflowAsync("chain-a", Md(
                "---", "schemaVersion: 2", "name: chain-a", "title: A", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: to-b", "call:", "  workflow: chain-b",
                "  returns: [result]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.result.answered", "```", "", "Body."), "human");

            var r = await e.CheckWorkflowAsync("chain-a");
            var all = string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message));
            Assert.DoesNotContain(r.Findings, f => f.Message.Contains("result") && f.Severity == "warn");
            Assert.True(r.Ok, "a legal three-level return chain was reported broken; findings: " + all);
        }

        [Fact]
        public async Task R8_control_a_name_no_one_in_the_chain_declares_is_still_reported()
        {
            // The control: relaying through B must not turn the check off. `outcome` is declared nowhere in
            // the chain, so A is still told.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("chain-c", Md(
                "---", "schemaVersion: 2", "name: chain-c", "title: C", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: result", "    question: What is it?", "```"), "human");
            await e.SaveWorkflowAsync("chain-b", Md(
                "---", "schemaVersion: 2", "name: chain-b", "title: B", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: to-c", "call:", "  workflow: chain-c",
                "  returns: [result]", "```", "", "Body."), "human");
            await e.SaveWorkflowAsync("chain-a2", Md(
                "---", "schemaVersion: 2", "name: chain-a2", "title: A", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: to-b", "call:", "  workflow: chain-b",
                "  returns: [outcome]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.outcome.answered", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("chain-a2");
            Assert.Contains(r.Findings, f => f.Message.Contains("act") && f.Message.Contains("outcome"));
            Assert.False(r.Ok);
        }

        // ---- ROUND 5, finding 2: pass two can hide the declaration pass one found ----------------------
        //
        // Written DESIGN-NEUTRAL while the remedy was open, and kept that way now it is ruled: a file whose
        // frontmatter carries a `schemaVersion: 2` line must not end up read as v1 in SILENCE. Under the
        // version-independent scan that shipped, the line is not a declaration at all (it is not the first
        // key) and check_workflow says so out loud; the assertion never had to change to fit the answer.
        //
        // Characterisation of the DEFECT, kept so the fix is legible: both files parsed clean, came back as
        // SchemaVersion 1, and accepted `bogus:`, which v2 refuses as an unknown frontmatter key, with
        // nothing said anywhere.

        private static async Task<(string parseError, int version, string findings)> VersionSilenceProbeAsync(string name, params string[] frontmatter)
        {
            var md = Md(new[] { "---" }.Concat(frontmatter)
                .Concat(new[] { "---", "## Step 1: Do", "Body." }).ToArray());
            var def = WorkflowParser.Parse(md);
            var (e, _) = Make();
            if (def.Error == null) await e.SaveWorkflowAsync(name, md, "human");
            var findings = def.Error != null ? "" : string.Join(" | ",
                (await e.CheckWorkflowAsync(name)).Findings.Select(f => f.Severity + ": " + f.Message));
            return (def.Error, def.SchemaVersion, findings);
        }

        [Fact]
        public async Task R9_a_version_hidden_in_a_provenance_block_is_never_silent()
        {
            // Sol's input. Pass one reads flat, sees `  schemaVersion: 2` as a top-level key and concludes v2;
            // pass two runs strict, lets `provenance:` open a block, swallows that line, finds no version and
            // returns 1. The file is then read as v1 and `bogus:` is accepted.
            var (err, version, findings) = await VersionSilenceProbeAsync("nested-version",
                "provenance:", "  schemaVersion: 2", "name: nested-version", "bogus: accepted-in-v1");
            Assert.True(err != null || version >= 2 || findings.Contains("version", StringComparison.OrdinalIgnoreCase),
                $"a frontmatter carrying 'schemaVersion: 2' was read as v{version} with nothing said. error={err ?? "none"}; findings={(findings.Length == 0 ? "none" : findings)}");
        }

        [Fact]
        public async Task R9_a_version_hidden_under_a_commented_slots_key_is_never_silent()
        {
            // The same downgrade through a different door, which is why `provenance:` was not the complete
            // divergence set: `slots: # list` carries a value in pass one (v1 does not strip a comment-only
            // value) and carries none in pass two, so it opens a block only in pass two.
            var (err, version, findings) = await VersionSilenceProbeAsync("slots-version",
                "slots: # list", "  schemaVersion: 2", "name: slots-version", "bogus: accepted-in-v1");
            Assert.True(err != null || version >= 2 || findings.Contains("version", StringComparison.OrdinalIgnoreCase),
                $"a frontmatter carrying 'schemaVersion: 2' was read as v{version} with nothing said. error={err ?? "none"}; findings={(findings.Length == 0 ? "none" : findings)}");
        }

        // ---- ROUND 5: what the ruled design deliberately CHANGED, stated as tests ---------------------

        [Theory]
        // Three inputs that used to be refused and now load as version 1 with a warn. Every one of them is a
        // V1 file under the ruled design, because the version is the first key at column zero before any
        // nested line and none of these declares one there:
        //   - the version really is 1 (first key), and a later `schemaVersion: 2` is not a second version, it
        //     is a stray key; v1 keeps stray keys, so it cannot be refused;
        [InlineData("schemaVersion: 1|name: t|schemaVersion: 2")]
        //   - the same with an unreadable future version, which is likewise not a declaration at all here;
        [InlineData("schemaVersion: 1|name: t|schemaVersion: 9")]
        //   - and a file whose first line is nested, which closes the window before any version is seen.
        [InlineData("  name: t|schemaVersion: 2")]
        // This is a LOOSENING and it is deliberate: before v2 existed these were ordinary unknown keys and
        // every one of these files loaded, so refusing them broke the v1-forever promise (rounds 1 and 4 were
        // both this defect). What replaces the refusal is a warn, not silence, because a version line nobody
        // reads is exactly the [T169] failure this slice exists to end.
        public void R11_a_v1_file_with_a_stray_version_line_loads_and_says_so(string pipeSeparatedFrontmatter)
        {
            var frontmatter = pipeSeparatedFrontmatter.Split('|');
            var def = WorkflowParser.Parse(Md(
                new[] { "---" }.Concat(frontmatter).Concat(new[] { "---", "## Step 1: Do", "Body." }).ToArray()));
            Assert.True(def.Error == null, "a v1 file with a stray schemaVersion line was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R13_a_quoted_LINE_that_merely_looks_like_a_version_key_stays_v1_forever()
        {
            // Read seven's class A, from read six's own fix: `"schemaVersion: 2"` is a quoted LINE — the
            // colon sits INSIDE one closed pair of quotes, so it is a scalar, not a key-value — and the
            // pinned pre-v2 parser keeps it as provenance and loads the file. Read six's broken-quoting
            // refusal stripped quote characters from the SPLITTER's key, which cannot tell a quoted key
            // from a quoted line, and refused this file at line 2. The fixture carries the double-quoted,
            // single-quoted and trailing-text spellings; the golden (captured from the pinned commit)
            // pins the exact provenance the pre-v2 parser kept, and this test pins load + version.
            var def = WorkflowParser.Parse(Fixture("quoted-version-line"));
            Assert.True(def.Error == null, "a v1 file with a quoted version-shaped LINE was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            // The read-eight warn detector is deliberately over-approximate, and this shape is inside
            // its stated over-approximation: a quoted LINE that looks key-shaped draws the hedged warn.
            // The load-bearing halves are above — no refusal, version 1, bytes frozen by the golden.
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R14_every_nonplain_version_key_spelling_loads_frozen_and_warns()
        {
            // The read-eight ruling's fixture: a quoted key, an escaped-quote key, and a block scalar
            // whose content line starts with a quote (read eight's class A — the raw-line classifier
            // refused it; with the classifier deleted it cannot be misclassified, because nothing
            // classifies raw lines any more). The golden, captured from the pinned commit, pins the
            // exact provenance the frozen reader keeps; this test pins load + version + warn.
            var def = WorkflowParser.Parse(Fixture("nonplain-version-keys"));
            Assert.True(def.Error == null, "a non-plain version spelling was refused in v1: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("|", def.Description);   // v1 has no block scalars, frozen
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R11_control_a_plain_v1_declaration_is_not_warned_about()
        {
            // The control: the warn must fire for a version line nobody READ, not for every version line. A
            // file whose first key is `schemaVersion: 1` declared its version properly and gets no warn.
            // Read from the fixture, so the README row naming this test is a fact about that file.
            var def = WorkflowParser.Parse(Fixture("explicit-v1"));
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.DoesNotContain(WorkflowParser.V2Findings(def, null, null),
                f => f.Message.Contains("schemaVersion"));
            // and it is still preserved exactly as the pre-v2 parser preserved it, because in v1
            // `schemaVersion` was never a key of the format, just an unknown one
            Assert.Equal("1", def.Provenance["schemaVersion"]);
        }

        [Fact]
        public void R11_a_v1_duplicate_version_keeps_the_LAST_value_in_provenance()
        {
            // The caller-visible half the golden alone would not have defended, and the reason the
            // duplicate-version fixture exists: before v2 neither line was a key of the format, so both were
            // unknown keys and the LAST one won. Losing that would have passed load, version and warning.
            var def = WorkflowParser.Parse(Fixture("duplicate-version"));
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("2", def.Provenance["schemaVersion"]);
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R11_an_unknown_top_level_key_with_a_value_is_preserved()
        {
            // The v1 forward-compatibility promise itself, pinned against the fixture that carries it. No
            // shipped file uses an unknown key, so without this the promise rested on the golden alone.
            var def = WorkflowParser.Parse(Fixture("unknown-top-level-key"));
            Assert.Null(def.Error);
            Assert.Equal("some-other-workflow", def.Provenance["derived_from"]);
            Assert.Equal("2026-01-01", def.Provenance["distilled_on"]);
        }

        // ---- UNSCOPED 5, findings 1 and 2: two v2 fixes that leaked into the frozen v1 ruleset -------------
        //
        // Both were introduced by earlier rounds on this branch, both changed how an EXISTING file parses,
        // and the v1 golden could not see either because no shipped file and no fixture carried the shape.
        // Residual risk 6 on PR #301 predicted exactly this. The fixtures below are that residual closed.

        [Fact]
        public void U5_v1_splits_an_inline_list_on_every_comma_including_a_quoted_one()
        {
            // F-065. v1 has no quote-aware split: it splits on every comma and strips quote characters
            // afterwards, so `["finance, monthly"]` is TWO tags. That is a defect and it is v1's, frozen.
            // The quote-aware scan added for v2 was applied to both rulesets and silently made it one.
            var def = WorkflowParser.Parse(Fixture("quoted-comma-in-list"));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
            Assert.Equal(new[] { "a", "b", "plain" }, def.Triggers);
        }

        [Fact]
        public void U5_v1_reads_a_comment_shaped_frontmatter_LINE_as_an_ordinary_key()
        {
            // F-066. v1 has no comment syntax anywhere, so `# owner: finance` is a key whose value v1
            // preserves into Provenance under the forward-compatibility promise. Comment support was added
            // for v2 and dropped that provenance entry from every v1 file that had one.
            //
            // Distinct from comment-shaped-values.md, which pins `key: # text` (a VALUE that looks like a
            // comment). This pins a whole LINE that looks like one.
            var def = WorkflowParser.Parse(Fixture("comment-line-in-frontmatter"));
            Assert.Null(def.Error);
            Assert.Equal("finance", def.Provenance["# owner"]);
            Assert.Equal("also a key in v1", def.Provenance["# indented"]);
        }

        [Fact]
        public void U5_a_column_zero_comment_shaped_line_ENDS_a_v1_gate_section()
        {
            // F-066's second observable instance, which the finding did not name: the comment skip was
            // added to FOUR shared readers, not one. In ParseGate a column-zero line closes the section
            // above it, so under v1 this file's `inputs:` ends at the '#' line and the SECOND input is
            // dropped. Skipping the line as a comment silently resurrected it.
            //
            // The block-interior comment in this same fixture's `slots:` is deliberately NOT asserted here:
            // it lands on an inert unknown property, so it is not observable in parse output at all. Said
            // plainly rather than asserted weakly.
            var def = WorkflowParser.Parse(Fixture("comment-line-in-blocks"));
            Assert.Null(def.Error);
            var gate = Assert.Single(def.Steps).Gate;
            Assert.Equal(new[] { "approval" }, gate.Inputs.Select(i => i.Name).ToArray());
        }

        [Fact]
        public void U6_v1_truncates_at_a_quoted_hash_and_keeps_an_apostrophe()
        {
            // The FROZEN halves of F-073 and F-074, pinned against the pre-v2 parser rather than against
            // this branch's output. Both fixes went into readers v1 shares, which is exactly how the fifth
            // read's two class-A breaks were made, so neither is trusted to a unit test written from here.
            //
            // v1 cuts at the first " #" with no idea about quotes, so the whole list collapses to one
            // literal item carrying the opening bracket and quote. And v1 strips quote characters only from
            // an item's ENDS, so an apostrophe inside a word survives while a wrapping one is removed.
            // Both are defects. Both are v1's, frozen.
            var def = WorkflowParser.Parse(Fixture("hash-and-apostrophe-in-list"));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "[\"finance" }, def.Tags);
            Assert.Equal(new[] { "O'Reilly", "finance" }, def.Triggers);
        }

        // ---- ROUND 6, finding 1: the warn did not fire for a version hidden inside a block ------------

        [Fact]
        public void R12_a_version_hidden_inside_a_slots_block_is_still_reported()
        {
            // Sol's input is the FIXTURE ITSELF, loaded from disk rather than retyped. The `schemaVersion: 2`
            // inside its slots block never reached the rule the spec says covers it, because the block branch
            // consumed the line before the version check ran. Reading the real file is the point: when that
            // line was mutated to `futureKey: 2`, the retyped version of this test stayed green while the
            // fixture no longer carried the hazard its README row claimed.
            var def = WorkflowParser.Parse(Fixture("slot-with-unknown-property"));
            Assert.True(def.Error == null, "a v1 template fixture was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R12_a_version_hidden_under_a_provenance_key_is_still_reported()
        {
            // The sibling hiding place: a v1 `provenance:` key does not open a block, so this one was already
            // caught. Pinned so the two stay caught together.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "provenance:", "  schemaVersion: 2", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
        }

        [Fact]
        public void R12_v2_refuses_a_version_hidden_inside_a_block()
        {
            // The other half of the same spec sentence: what v1 warns about, v2 refuses. Without this the
            // rule would be half-implemented in the direction that matters more.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:", "  schemaVersion: 2", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "v2 accepted a second schemaVersion hidden in a provenance block");
            Assert.Contains("schemaVersion", def.Error);
        }

        [Fact]
        public void R12_control_a_list_item_key_is_still_not_a_version()
        {
            // The control that keeps the widened rule from over-reaching: `- schemaVersion` is a different
            // key (R7), so it must not trip the warn. Without this the rule could quietly become "any line
            // containing the word".
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "  - schemaVersion: 2", "---", "## Step 1: Do", "Body."));
            Assert.Null(def.Error);
            Assert.DoesNotContain(WorkflowParser.V2Findings(def, null, null),
                f => f.Message.Contains("schemaVersion"));
        }

        // ---- ROUND 5, finding 3: the namespace helper had no depth bound -------------------------------

        [Fact]
        public async Task R10_a_name_four_workflows_deep_is_not_in_the_callers_namespace()
        {
            // The run engine will never reach D: the depth limit is 3, counting the top-level run as 1. A
            // checker that admits a name from depth 4 tells the author a reference resolves that the engine
            // refuses at run start, which is the authoring-time-to-run-time downgrade pointing backwards.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("deep-d", Md(
                "---", "schemaVersion: 2", "name: deep-d", "title: D", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: result", "    question: What is it?", "```"), "human");
            foreach (var (self, next) in new[] { ("deep-c", "deep-d"), ("deep-b", "deep-c") })
                await e.SaveWorkflowAsync(self, Md(
                    "---", "schemaVersion: 2", "name: " + self, "title: T", "---",
                    "## Step 1: Hand off",
                    "```yaml step", "id: onward", "call:", "  workflow: " + next,
                    "  returns: [result]", "```", "", "Body."), "human");
            await e.SaveWorkflowAsync("deep-a", Md(
                "---", "schemaVersion: 2", "name: deep-a", "title: A", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: onward", "call:", "  workflow: deep-b",
                "  returns: [result]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.result.answered", "```", "", "Body."), "human");

            var r = await e.CheckWorkflowAsync("deep-a");
            var all = string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message));
            Assert.Contains(r.Findings, f => f.Message.Contains("act") && f.Message.Contains("result"));
            Assert.False(r.Ok, "findings: " + all);
        }

        // ---- ROUND 3, finding 2: a callee that cannot parse still validates the caller ------------------

        [Fact]
        public async Task R6_a_call_to_an_unreadable_workflow_is_reported()
        {
            // Sol's input. `broken` retains its parsed input rows even though ValidateInputBindings refused the
            // file, so the round-2 union accepted `returns: [result]` from a workflow that cannot run, and the
            // caller came back Ok. That is the round-1 defect's shape exactly: silence about something the
            // author clearly meant to have an effect.
            var (e, ws) = Make();
            await e.SaveWorkflowAsync("broken", Md(
                "---", "schemaVersion: 2", "name: broken", "title: Broken", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: result", "    question: What came back?", "```"), "human");
            // Written past save_workflow on purpose: it parse-validates, so a file this broken can only reach
            // the library the way a hand-edited one does.
            var file = Directory.GetFiles(ws, "broken.md", SearchOption.AllDirectories).Single();
            File.WriteAllText(file, Md(
                "---", "schemaVersion: 2", "name: broken", "title: Broken", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: result", "    question: What came back?",
                "    type: text", "verify:", "  - kind: expected_values", "    probe: result",
                "    anchors: missing", "```"));
            await e.SaveWorkflowAsync("caller-broken", Md(
                "---", "schemaVersion: 2", "name: caller-broken", "title: Caller", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: broken",
                "  returns: [result]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.result.answered", "```", "", "Body."), "human");

            var r = await e.CheckWorkflowAsync("caller-broken");
            var all = string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message));
            Assert.Contains(r.Findings, f => f.Message.Contains("broken") && f.Severity == "warn");
            Assert.False(r.Ok, "a caller handing off to an unreadable workflow reported Ok; findings: " + all);
        }

        [Fact]
        public async Task R6_a_returned_name_the_callee_does_not_declare_does_not_resolve()
        {
            // The second half of the same under-refusal: the union took the caller's raw `returns:` list on
            // trust, so a name the callee never declares still made every reference to it resolve. The
            // returns line itself was warned about; the references it licensed were not.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("sub-approve", Md(
                "---", "schemaVersion: 2", "name: sub-approve", "title: Sub approve", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```"), "human");
            await e.SaveWorkflowAsync("caller-w", Md(
                "---", "schemaVersion: 2", "name: caller-w", "title: Caller", "---",
                "## Step 1: Get approval",
                "```yaml step", "id: get-approval", "call:", "  workflow: sub-approve",
                "  returns: [verdict]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.verdict.answered", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller-w");
            var all = string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message));
            Assert.Contains(r.Findings, f => f.Message.Contains("act") && f.Message.Contains("verdict"));
            Assert.False(r.Ok, "findings: " + all);
        }

        [Fact]
        public async Task R6_control_a_sound_hand_off_still_resolves()
        {
            // The control for both halves: a real callee, a declared name, and the reference still resolves.
            var e = await LibraryWithACallAsync();
            var r = await e.CheckWorkflowAsync("caller-x");
            var bogus = r.Findings.Where(f => f.Message.Contains("approval") && f.Message.Contains("no gate")).ToArray();
            Assert.True(bogus.Length == 0, "a sound hand-off stopped resolving: "
                + string.Join(" | ", bogus.Select(f => f.Severity + ": " + f.Message)));
        }

        [Fact]
        public async Task R4_an_answer_no_call_returns_is_still_reported()
        {
            // The control: widening the namespace must not blunt the check. `aproval` is returned by nothing
            // and declared by nobody, so it stays a warn.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("sub-approve", Md(
                "---", "schemaVersion: 2", "name: sub-approve", "title: Sub approve", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```"), "human");
            await e.SaveWorkflowAsync("caller-y", Md(
                "---", "schemaVersion: 2", "name: caller-y", "title: Caller", "---",
                "## Step 1: Get approval",
                "```yaml step", "id: get-approval", "call:", "  workflow: sub-approve",
                "  returns: [approval]", "```", "", "Body.",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.aproval.answered", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller-y");
            Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("aproval"));
        }
    }
}
