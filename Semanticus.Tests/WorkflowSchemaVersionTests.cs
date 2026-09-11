using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Format v2: the version field and the strict-refusal rules (docs/workflow-canvas-spec.md §1.2, §1.3,
    /// §1.8; acceptance checks 1-5, 6b, 6c). The defect these close is [T169]: today the parser files every
    /// key it does not recognise into <c>Provenance</c>, so an old engine handed a new file runs it anyway,
    /// missing the parts it cannot read, and looks like it worked.
    /// </summary>
    public sealed class WorkflowSchemaVersionTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        /// <summary>A minimal well-formed file at the given version, with extra frontmatter/step lines spliced in.</summary>
        private static string File(int? schemaVersion, string[] extraFrontmatter = null, string[] stepBody = null)
        {
            var head = schemaVersion == null
                ? new[] { "---" }
                : new[] { "---", "schemaVersion: " + schemaVersion };
            return Md(head
                .Concat(new[] { "name: t", "title: T" })
                .Concat(extraFrontmatter ?? Array.Empty<string>())
                .Concat(new[] { "---", "", "## Step 1: Do", "" })
                .Concat(stepBody ?? new[] { "Body." })
                .ToArray());
        }

        // ---- check 1: an unreadable future version is refused, verbatim ------------------------------

        [Fact]
        public void A_future_schema_version_is_refused_with_the_verbatim_reason()
        {
            var def = WorkflowParser.Parse(File(3));
            Assert.Equal(
                "this workflow file says schemaVersion: 3. This Semanticus reads up to 2. Update Semanticus, "
                + "or open the file with the version that wrote it. Nothing was run.",
                def.Error);
            // and it must NOT have been quietly absorbed as provenance, which is what happens today
            Assert.False(def.Provenance.ContainsKey("schemaVersion"));
        }

        [Fact]
        public void A_non_integer_schema_version_is_refused_naming_the_value()
        {
            var def = WorkflowParser.Parse(Md("---", "schemaVersion: two", "name: t", "---", "", "## Step 1: Do", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("two", def.Error);
            Assert.Contains("schemaVersion", def.Error);
        }

        // ---- check 5: position is part of the contract -----------------------------------------------

        [Fact]
        public void Schema_version_placed_second_is_refused_naming_the_preceding_key()
        {
            var def = WorkflowParser.Parse(Md("---", "name: t", "schemaVersion: 2", "---", "", "## Step 1: Do", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("schemaVersion", def.Error);
            Assert.Contains("'name'", def.Error);   // names the key that preceded it
        }

        // ---- v1 stays v1, forever --------------------------------------------------------------------

        [Fact]
        public void Absent_schema_version_means_v1_and_unknown_keys_still_land_in_provenance()
        {
            var def = WorkflowParser.Parse(File(null, extraFrontmatter: new[] { "derived_from: wfr-3" }));
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("wfr-3", def.Provenance["derived_from"]);
        }

        [Fact]
        public void Explicit_schema_version_1_is_identical_to_absent()
        {
            var def = WorkflowParser.Parse(File(1, extraFrontmatter: new[] { "derived_from: wfr-3" }));
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("wfr-3", def.Provenance["derived_from"]);
        }

        // ---- check 3: v2 refuses an unknown frontmatter key ------------------------------------------

        [Fact]
        public void V2_refuses_an_unknown_frontmatter_key_naming_key_scope_and_line()
        {
            var def = WorkflowParser.Parse(File(2, extraFrontmatter: new[] { "derived_from: wfr-3" }));
            Assert.NotNull(def.Error);
            Assert.Contains("'derived_from'", def.Error);
            Assert.Contains("frontmatter", def.Error);
            Assert.Contains("line 5", def.Error);       // ---(1) schemaVersion(2) name(3) title(4) derived_from(5)
            Assert.DoesNotContain("Provenance", def.Error);
        }

        [Fact]
        public void V2_keeps_provenance_under_an_explicit_block()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: wfr-3",
                "  distilled: 2026-07-12",
                "---", "", "## Step 1: Do", "", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("wfr-3", def.Provenance["derived_from"]);
            Assert.Equal("2026-07-12", def.Provenance["distilled"]);
        }

        // ---- check 2: v2 refuses an unknown key inside a gate ----------------------------------------

        [Fact]
        public void V2_refuses_an_unknown_gate_key_naming_key_scope_and_line()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "Body.", "", "```yaml gate", "strictness: hard", "verrify:", "  - kind: bpa_clean", "```",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'verrify'", def.Error);
            Assert.Contains("gate", def.Error);
            Assert.Contains("line 13", def.Error);      // ---(1) sV(2) name(3) title(4) ---(5) _(6) heading(7) _(8) Body(9) _(10) fence(11) strictness(12) verrify(13)
            // the refusal teaches the allowed set, per the tool-result contract
            Assert.Contains("strictness, ops, inputs, verify", def.Error);
        }

        [Fact]
        public void V2_refuses_an_unknown_key_inside_a_gate_input_map()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "Body.", "", "```yaml gate", "inputs:", "  - name: a", "    quesion: Ask?", "```",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'quesion'", def.Error);
            Assert.Contains("input", def.Error);
        }

        [Fact]
        public void V2_refuses_an_unknown_key_inside_a_gate_verify_map()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "Body.", "", "```yaml gate", "verify:", "  - kind: bpa_clean", "    scop: model", "```",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'scop'", def.Error);
            Assert.Contains("verify", def.Error);
        }

        [Fact]
        public void V1_still_absorbs_every_one_of_those_silently()
        {
            // the v1-forever guarantee, stated as its own assertion so a strictness bug that leaked into v1
            // fails here rather than in a seed round-trip nobody reads
            var def = WorkflowParser.Parse(File(null, stepBody: new[]
            {
                "Body.", "", "```yaml gate", "strictness: hard", "verrify:", "  - kind: bpa_clean", "```",
            }));
            Assert.Null(def.Error);
        }

        // ---- check 6b: the three new v2 blocks are strict too ----------------------------------------

        [Fact]
        public void V2_refuses_an_unknown_key_inside_a_yaml_step_fence()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "whn: inputs.a.answered", "```", "", "Body.",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'whn'", def.Error);
            Assert.Contains("step", def.Error);
            Assert.Contains("id, when, forEach, call", def.Error);
        }

        [Fact]
        public void V2_refuses_an_unknown_key_inside_a_for_each_map()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "forEach:", "  in: [a, b]", "  az: table", "```", "", "Body.",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'az'", def.Error);
            Assert.Contains("forEach", def.Error);
            Assert.Contains("in, as, maxIterations", def.Error);
        }

        [Fact]
        public void V2_refuses_an_unknown_key_inside_a_call_map()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  retruns: [x]", "```", "", "Body.",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("'retruns'", def.Error);
            Assert.Contains("call", def.Error);
            Assert.Contains("workflow, with, returns", def.Error);
        }

        [Fact]
        public void The_with_map_is_the_one_open_map_and_is_not_refused_by_name()
        {
            // §1.3: `with:` keys are the callee's declared input names, so they are validated against the
            // callee (check_workflow), never against a fixed list. An unstated exception is how a strictness
            // rule quietly becomes a suggestion, so it is asserted.
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  with:", "    anythingAtAll: 5", "```", "", "Body.",
            }));
            Assert.Null(def.Error);
            Assert.Equal("5", def.Steps[0].Call.With["anythingAtAll"]);
        }

        // ---- check 4 + 6c: reserved keys are refused BY NAME, and the set is enumerated ---------------

        [Fact]
        public void After_is_refused_with_its_own_stage_two_reservation_not_as_an_unknown_key()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "after: [step-1]", "```", "", "Body.",
            }));
            Assert.Equal(
                "Step 1: 'after:' is reserved for a later version of this format. This version runs steps in "
                + "file order. Remove it, or use 'when:' to control whether a step runs. Nothing was saved.",
                def.Error);
            Assert.DoesNotContain("is not a key this format has", def.Error);
        }

        /// <summary>
        /// The reserved set as this test knows it, written out longhand. Cross-checked against the engine's
        /// shared constant below so the pair can only agree by both being right: a spelling added to the
        /// engine without a test fails, and a spelling dropped from the engine fails too. A count-shaped
        /// assertion passes while a member is missing (spec §1.8), so there is no count here.
        /// </summary>
        private static readonly (string Scope, string Key)[] ExpectedReservations =
        {
            ("step", "instructions"),
            ("step", "maxRunTime"),
            ("step", "approvals"),
            ("step", "agent"),
            ("step", "script"),
            ("step", "lookup"),
            ("gate", "successCriteria"),
            ("frontmatter", "variables"),
            ("frontmatter", "agents"),
        };

        [Fact]
        public void The_reserved_key_set_is_exactly_the_set_the_spec_names()
        {
            var actual = WorkflowParser.ReservedDestinationKeys
                .Select(r => (r.Scope, r.Key)).OrderBy(x => x).ToArray();
            Assert.Equal(ExpectedReservations.OrderBy(x => x).ToArray(), actual);
        }

        [Theory]
        [MemberData(nameof(EveryReservation))]
        public void Every_reserved_key_is_refused_with_its_own_named_reservation(string scope, string key)
        {
            var def = WorkflowParser.Parse(FileWithKeyIn(scope, key));
            Assert.NotNull(def.Error);
            Assert.Contains($"'{key}:' is reserved", def.Error);
            // the whole point of a NAMED reservation: the author learns "not yet", never "you typed it wrong"
            Assert.DoesNotContain("is not a key this format has", def.Error);
            // and the message is this key's own, not a shared stub
            var reservation = WorkflowParser.ReservedDestinationKeys.Single(r => r.Scope == scope && r.Key == key);
            Assert.Contains(reservation.Reason, def.Error);
        }

        public static TheoryData<string, string> EveryReservation()
        {
            var data = new TheoryData<string, string>();
            foreach (var (scope, key) in ExpectedReservations) data.Add(scope, key);
            return data;
        }

        [Fact]
        public void A_reservation_holds_only_in_its_own_scope()
        {
            // §1.8: a key is reserved WHERE IT WILL LIVE. successCriteria on a box is a plain unknown key;
            // in a gate it is the named reservation. A reservation in the wrong scope is one that has to move.
            var onTheBox = WorkflowParser.Parse(FileWithKeyIn("step", "successCriteria"));
            Assert.Contains("'successCriteria' is not a key this format has", onTheBox.Error);
            Assert.DoesNotContain("is reserved", onTheBox.Error);

            var inTheGate = WorkflowParser.Parse(FileWithKeyIn("gate", "successCriteria"));
            Assert.Contains("'successCriteria:' is reserved", inTheGate.Error);

            // and the mirror case: `script` belongs on the box, so in frontmatter it is merely unknown
            var scriptInFrontmatter = WorkflowParser.Parse(FileWithKeyIn("frontmatter", "script"));
            Assert.Contains("'script' is not a key this format has", scriptInFrontmatter.Error);
        }

        [Fact]
        public void A_reserved_key_is_not_refused_in_v1()
        {
            // v1 parses forever. A reservation is a v2 rule, so a v1 file using `script:` keeps working.
            foreach (var (scope, key) in ExpectedReservations)
            {
                var def = WorkflowParser.Parse(FileWithKeyIn(scope, key, schemaVersion: null));
                Assert.True(def.Error == null, $"v1 refused reserved key '{key}' in {scope}: {def.Error}");
            }
        }

        private static string FileWithKeyIn(string scope, string key, int? schemaVersion = 2)
        {
            switch (scope)
            {
                case "frontmatter":
                    return File(schemaVersion, extraFrontmatter: new[] { key + ": x" });
                case "gate":
                    return File(schemaVersion, stepBody: new[] { "Body.", "", "```yaml gate", "strictness: hard", key + ": x", "```" });
                case "step":
                    // a v1 file has no `yaml step` fence at all, so the v1 case is proven on the gate scope
                    // instead; here the fence is the home the key is reserved in.
                    return File(schemaVersion, stepBody: new[] { "```yaml step", "id: do-it", key + ": x", "```", "", "Body." });
                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, "unknown reservation scope");
            }
        }

        // ---- §1.6: the two delimiters have two lifetimes ---------------------------------------------

        [Fact]
        public void V2_refuses_a_loop_reference_written_with_slot_delimiters()
        {
            var def = WorkflowParser.Parse(File(2, stepBody: new[]
            {
                "```yaml step", "id: do-it", "forEach:", "  in: [a, b]", "  as: table", "```", "",
                "Check {{loop.table}} now.",
            }));
            Assert.NotNull(def.Error);
            Assert.Contains("{{loop.", def.Error);
            Assert.Contains("[[loop.table]]", def.Error);
        }
    }
}
