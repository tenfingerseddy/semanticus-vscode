using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;
using YamlDotNet.Core;   // C21 decodes the stored provenance form with the YAML engine itself

namespace Semanticus.Tests
{
    /// <summary>
    /// THE EXIT-TEST PACK for [T217], written before the library touched anything and ratified as the
    /// deal that decides the slice: v2 is read by a real YAML library, our format rules are enforced on
    /// the model it returns, and the class-(a) family (our hand reader disagreeing with standard YAML)
    /// ends as a family instead of one review round at a time.
    ///
    /// Two kinds of test, and the kind is the point:
    ///   E* — RED-FIRST on the hand-rolled reader. Each is a standard-YAML construct the hand reader
    ///        refuses or mangles today. They can only go green when a real YAML engine reads the fence
    ///        and frontmatter interiors, so they prove the library actually arrived.
    ///   C* — CONTROLS. Behaviour that must hold on BOTH readers: our own format rules (closed key
    ///        sets, reservations, shape rules), the refusals standard YAML shares with us, and the
    ///        v1-forever freeze. A control that fails after the swap is the slice failing its premise.
    ///
    /// Behavioural equivalence is what these assert, never message text: refused versus accepted, value
    /// preserved versus dropped, and a refusal naming a line or a key. Message-pinned tests elsewhere
    /// may be updated to behaviour when the library rewords a refusal; every such change is disclosed
    /// in the slice report.
    /// </summary>
    public sealed class WorkflowV2YamlReaderExitTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        private static WorkflowDef V2(params string[] frontAndBody) => WorkflowParser.Parse(Md(frontAndBody));

        // ============================================================================================
        // E — red-first: standard YAML the hand-rolled reader cannot read
        // ============================================================================================

        [Fact]
        public void E1_a_flow_map_under_with_is_read_with_its_values_preserved()
        {
            // `with: { k: v }` is ordinary YAML. The hand reader refused the flow form outright (F-044's
            // fix), because it could not read it; a real YAML engine reads it, and the value must arrive.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "call:",
                "  workflow: verified-measure",
                "  with: { targetMeasure: inputs.target }",
                "```",
                "Body.");
            Assert.Null(def.Error);
            Assert.Equal("inputs.target", def.Steps[0].Call.With["targetMeasure"]);
        }

        [Fact]
        public void E2_a_flow_map_under_forEach_is_read()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "forEach: { in: [Sales, Product], as: table }",
                "```",
                "Body.");
            Assert.Null(def.Error);
            Assert.Equal(new[] { "Sales", "Product" }, def.Steps[0].ForEach.InLiteral);
            Assert.Equal("table", def.Steps[0].ForEach.As);
        }

        [Fact]
        public void E3_a_block_sequence_is_the_same_list_as_a_flow_sequence()
        {
            // `tags:` followed by `- finance` / `- monthly` is the same YAML document as
            // `tags: [finance, monthly]`. The hand reader refused the block spelling.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags:",
                "  - finance",
                "  - monthly",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
        }

        [Fact]
        public void E4_a_block_sequence_under_a_gate_list_key_is_read()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "ops:",
                "  - create_measure",
                "  - update_measure",
                "```",
                "Body.");
            Assert.Null(def.Error);
            Assert.Equal(new[] { "create_measure", "update_measure" }, def.Steps[0].Ops);
        }

        [Fact]
        public void E5_a_block_scalar_description_keeps_its_lines()
        {
            // `description: |` with indented lines is the YAML way to write a paragraph. The hand
            // reader refused every indented line under a scalar key.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "description: |",
                "  First line.",
                "  Second line.",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Contains("First line.", def.Description);
            Assert.Contains("Second line.", def.Description);
        }

        [Fact]
        public void E6_a_double_quoted_scalar_with_escaped_quotes_is_one_value()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t",
                "title: \"A \\\"quoted\\\" word\"",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal("A \"quoted\" word", def.Title);
        }

        [Fact]
        public void E7_a_single_quoted_scalar_with_a_doubled_quote_is_one_value()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t",
                "title: 'it''s fine'",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal("it's fine", def.Title);
        }

        // ============================================================================================
        // C — controls: what must hold on both readers
        // ============================================================================================

        [Fact]
        public void C1_a_duplicate_frontmatter_key_is_refused_naming_a_line()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: First", "title: Second", "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("title", def.Error);
            Assert.Contains("line", def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C2_a_duplicate_key_inside_one_verify_item_is_refused()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "verify:",
                "  - kind: bpa_clean",
                "    kind: readiness_rescan",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("kind", def.Error);
        }

        [Fact]
        public void C3_an_unknown_key_is_refused_teaching_the_closed_set()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "verrify:",
                "  - kind: bpa_clean",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("verrify", def.Error);
            Assert.Contains("strictness, ops, inputs, verify", def.Error);
        }

        [Fact]
        public void C4_an_unclosed_flow_list_is_refused_naming_a_line()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "ops: [create_measure",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("line", def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C5_an_unclosed_quote_is_refused()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t",
                "title: \"never closed",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
        }

        [Fact]
        public void C6_text_after_a_closing_quote_is_refused()
        {
            // The smuggled-second-key shape: `id: "do-it" instructions: x`. Standard YAML refuses it
            // too, which is the point of the swap; it must stay refused, and a reader must be told where.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: \"do-it\" instructions: x",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
        }

        [Fact]
        public void C7_a_comment_is_not_content_and_does_not_end_a_block()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "strictness: hard   # a comment",
                "inputs: # questions",
                "  - name: approval",
                "    # a full-line comment between properties",
                "    type: verification",
                "    required: answer-or-decline",
                "    question: Approved?",
                "```",
                "Body.");
            Assert.Null(def.Error);
            Assert.Equal("hard", def.Steps[0].Gate.Strictness);
            Assert.Equal("approval", def.Steps[0].Gate.Inputs.Single().Name);
            Assert.Equal("verification", def.Steps[0].Gate.Inputs.Single().Type);
        }

        [Fact]
        public void C8_a_quoted_comma_does_not_split_a_list_item()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [\"finance, monthly\"]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance, monthly" }, def.Tags);
        }

        [Fact]
        public void C9_an_empty_list_item_is_refused_not_deleted()
        {
            // `["", Sales]` is legal YAML and an authoring mistake in this format everywhere a list is
            // read. This rule is OURS, enforced on the model the library returns.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [\"\", finance]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("empty", def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C10_a_scalar_on_a_list_key_is_refused_not_dropped()
        {
            // `verify: bpa_clean` used to parse to zero verifies. Legal YAML, wrong SHAPE for this
            // format: the rule is ours and stays.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "verify: bpa_clean",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("verify", def.Error);
        }

        [Fact]
        public void C11_reserved_keys_keep_their_named_reservations()
        {
            foreach (var (fence, key) in new[] { ("step", "script"), ("step", "after"), ("gate", "successCriteria") })
            {
                var def = V2(
                    "---", "schemaVersion: 2", "name: t", "title: T", "---",
                    "## Step 1: Go",
                    "```yaml " + fence,
                    fence == "step" ? "id: go" : "strictness: hard",
                    key + ": x",
                    "```",
                    "Body.");
                Assert.NotNull(def.Error);
                Assert.Contains("reserved for a later version", def.Error);
            }
        }

        [Fact]
        public void C12_version_first_and_the_too_new_refusal_survive()
        {
            var notFirst = V2("---", "name: t", "schemaVersion: 2", "title: T", "---", "## Step 1: Go", "Body.");
            Assert.NotNull(notFirst.Error);
            Assert.Contains("first key", notFirst.Error);

            var tooNew = V2("---", "schemaVersion: 3", "name: t", "title: T", "---", "## Step 1: Go", "Body.");
            Assert.NotNull(tooNew.Error);
            Assert.Contains("reads up to 2", tooNew.Error);
        }

        public static readonly TheoryData<string[], string> ThingsYamlAbsorbs = new TheoryData<string[], string>
        {
            // An unknown key: the line the old ledger existed to catch, refused by the closed sets.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "owner: finance", "---",
                      "## Step 1: Go", "Body." }, "owner" },
            // A SECOND DOCUMENT: YamlDotNet ends document one and hands back its node; without a
            // StreamEnd demand, everything after the '---' vanishes — the silent-drop family arriving
            // through YAML's own front door. Here the dropped key is a RESERVED one, so the loss is a
            // reservation that never fires.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go",
                      "```yaml gate", "strictness: hard", "---", "successCriteria: ignored", "```",
                      "Body." }, "document" },
            // The same second document through the step fence, because a family fix at one call site is
            // how this project's findings recur.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go",
                      "```yaml step", "id: go", "---", "after: step-9", "```",
                      "Body." }, "document" },
            // An ANCHOR declaration: `&label` parses to plain 'T' with the syntax vanishing. Aliases
            // were refused from day one; a declaration nobody can ever alias back is still author text
            // this format does not read, absorbed.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: &label T", "---",
                      "## Step 1: Go", "Body." }, "anchor" },
            // A TAG: `!audit` vanishes the same way.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "description: !audit D", "---",
                      "## Step 1: Go", "Body." }, "tag" },
            // A tag on a COLLECTION, so the refusal is proven on every node kind, not just scalars.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "tags: !!set [finance]", "---",
                      "## Step 1: Go", "Body." }, "tag" },
            // An anchor on a collection, same reason.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go",
                      "```yaml step", "id: go", "forEach: &loop", "  in: [Sales]", "  as: table", "```",
                      "Body." }, "anchor" },
            // THE SEVENTH ABSORPTION, found by walking the event stream while rebuilding this guard: a
            // '%YAML'/'%TAG' directive (and the explicit '---' it requires) arrives as a non-implicit
            // DocumentStart and was absorbed whole — a version pin the author wrote, silently ignored.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go",
                      "```yaml gate", "%YAML 1.2", "---", "strictness: hard", "```",
                      "Body." }, "%" },
            // THE NINTH ABSORPTION (read two, finding 2): an explicit '...' end-of-document marker
            // arrives as DocumentEnd with IsImplicit false and was consumed without a look — the same
            // one-property blindness as the DocumentStart case, one event over.
            { new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go",
                      "```yaml gate", "strictness: hard", "...", "```",
                      "Body." }, "document" },
        };

        [Theory]
        [MemberData(nameof(ThingsYamlAbsorbs))]
        public void C13_nothing_yaml_absorbs_escapes_the_model(string[] file, string expectInError)
        {
            // The consumed-or-refused guarantee in its post-library form, held against the LIBRARY's own
            // absorptions rather than only against the closed-key switch. The first revision of this test
            // exercised only the unknown-key case and stayed green while a second document, an anchor and
            // a tag all vanished — the guard-that-names-a-hazard-it-does-not-contain fault, this
            // project's sixth meeting with it. Every case here is author text YamlDotNet reads and this
            // format does not; each must be refused by name, never silently dropped.
            var def = WorkflowParser.Parse(Md(file));
            Assert.True(def.Error != null, "absorbed without a word: " + string.Join(" / ", file));
            Assert.Contains(expectInError, def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C14_an_empty_quoted_list_item_is_refused_not_kept()
        {
            // `tags: ""` yielded ONE EMPTY TAG: the quoted-scalar branch returned before the empty-item
            // check, so the stated refusal did not survive onto the model.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: \"\"",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("empty", def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C15_a_map_under_a_list_key_is_an_honest_refusal_not_a_cast_error()
        {
            // A map where a list belongs hit a blind cast, and the InvalidCastException surfaced internal
            // type names and named no line. A refusal must teach; a stack trace teaches nothing.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags:",
                "  a: b",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("line", def.Error);
            Assert.DoesNotContain("Cast", def.Error);
            Assert.DoesNotContain("YScalar", def.Error);
        }

        [Fact]
        public void C17_runaway_nesting_is_refused_not_a_stack_overflow()
        {
            // Read two, finding 1. ReadNode recurses per nesting level, and the measured crash on this
            // machine (Release, default stack, one process per probe) is between 1450 and 1500 levels:
            // 1450 parses to a refusal, 1500 kills the process. A crash is not a parse error, so the
            // depth is bounded explicitly at 32 — roughly 45x under the measured cliff and more than
            // 10x anything a legal file uses (the format nests three levels).
            var deep = new string('[', 40) + "x" + new string(']', 40);
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: " + deep,
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("levels", def.Error);
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void C17b_a_shallow_nested_list_is_refused_as_what_it_is()
        {
            // The message honesty half: `tags: [[a]]` was refused as "has an empty item", which reports
            // a mistake the author did not make. A list inside a list is refused as a list inside a list.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [[finance]]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("list", def.Error);
            Assert.DoesNotContain("empty", def.Error);
        }

        [Fact]
        public void C18_provenance_lists_keep_their_written_form_faithfully()
        {
            // Read two, finding 4. `["a, b"]` and `[a, b]` collapsed to the same stored string, and
            // quoted whitespace trimmed away — lossy today, a bug the day anything reparses it. The
            // stored form now distinguishes them: an item carrying a comma, bracket, quote, edge
            // whitespace or nothing is written back quoted.
            var one = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [\"a, b\"]",
                "---",
                "## Step 1: Go", "Body.");
            var two = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [a, b]",
                "---",
                "## Step 1: Go", "Body.");
            var three = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [\" x \"]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(one.Error); Assert.Null(two.Error); Assert.Null(three.Error);
            Assert.Equal("[\"a, b\"]", one.Provenance["derived_from"]);
            Assert.Equal("[a, b]", two.Provenance["derived_from"]);
            Assert.Equal("[\" x \"]", three.Provenance["derived_from"]);
            Assert.NotEqual(one.Provenance["derived_from"], two.Provenance["derived_from"]);
        }

        [Fact]
        public void C19_forEach_in_stays_a_reference_not_a_bare_list_and_the_refusal_teaches_both_spellings()
        {
            // Read two, finding 3, decided rather than patched: forEach `in:` deliberately does NOT
            // take the plain comma-list spelling other list keys take, and the spec now says why — a
            // bare scalar is ambiguous between a one-item literal and a mistyped input reference, and
            // `in: input.tableList` (missing s) read as a literal would loop once over garbage at run
            // time, the exact authoring-time-to-run-time downgrade this format refuses everywhere.
            // No red possible for a kept behaviour; this pins the refusal and its teaching.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "forEach:",
                "  in: Sales, Product",
                "  as: table",
                "```",
                "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("[Sales, Product]", def.Error);
            Assert.Contains("inputs.", def.Error);
        }

        [Fact]
        public void C18b_provenance_items_that_would_reparse_as_something_else_are_quoted(
        )
        {
            // Read three, finding 2. `["a: b"]` stored unquoted as `[a: b]`, which REPARSES as a map;
            // an item with a backslash before a quote made the escaping ambiguous. Storage now quotes
            // anything outside a conservative safe set and escapes backslash before quote, so the stored
            // form decodes uniquely.
            var colon = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [\"a: b\"]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(colon.Error);
            Assert.Equal("[\"a: b\"]", colon.Provenance["derived_from"]);

            // Item is literally a\"b (backslash then quote): both characters escape, uniquely.
            var slash = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [\"a\\\\\\\"b\"]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(slash.Error);
            Assert.Equal("[\"a\\\\\\\"b\"]", slash.Provenance["derived_from"]);
        }

        [Fact]
        public void C18c_the_one_remaining_provenance_collapse_is_null_versus_empty_and_it_is_deliberate()
        {
            // Stated, not hidden: a YAML null item (a bare `-` in a block list) and an empty string
            // item (`[""]`) both store as `[""]`. Deliberate, because provenance is the format's one
            // unvalidated bag, its store is a string map, and no consumer distinguishes null from empty;
            // refusing or encoding the difference would validate the unvalidated. Every OTHER collapse
            // is closed by the quoting rules above. (`[~]` is NOT this case: a plain `~` is kept as the
            // literal text `~`, quoted on storage, because this format never interprets scalar content.)
            var nullItem = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:", "  x:", "    -", "---", "## Step 1: Go", "Body.");
            var emptyItem = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:", "  x: [\"\"]", "---", "## Step 1: Go", "Body.");
            Assert.Null(nullItem.Error); Assert.Null(emptyItem.Error);
            Assert.Equal("[\"\"]", emptyItem.Provenance["x"]);
            Assert.Equal(emptyItem.Provenance["x"], nullItem.Provenance["x"]);
        }

        [Fact]
        public void C20_a_bare_section_key_is_an_empty_section_as_it_always_was()
        {
            // Read three, finding 3. A bare `verify:` (or `inputs:`) reaches SeqOfMaps as a YScalar with
            // a null Value, so the old `e.Value == null` empty-section branch was unreachable and the
            // once-accepted empty section was refused as "puts a value on a list key" — a value the
            // author never wrote. Decision: a bare section key means an empty section, exactly what the
            // pre-swap v2 reader and v1 both did.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml gate",
                "strictness: hard",
                "inputs:",
                "verify:",
                "```",
                "Body.");
            Assert.True(def.Error == null, "a bare section key was refused: " + def.Error);
            Assert.Equal("hard", def.Steps[0].Gate.Strictness);
            Assert.Empty(def.Steps[0].Gate.Inputs);
            Assert.Empty(def.Steps[0].Gate.Verify);
        }

        [Fact]
        public void C21_provenance_encoding_round_trips_a_generated_hostile_set()
        {
            // Read four, finding 1, written as the PROPERTY rather than a hand-list, because the two
            // prior versions of this rule each missed a case a hand-list did not contain (`a: b`, then
            // `- a` and a literal newline, then U+2028/U+2029 written raw). The property: reading the
            // stored form back as standard YAML yields exactly the authored items, each one a scalar,
            // from a stored form that is one line under EVERY Unicode line terminator YAML knows. The
            // set is GENERATED from a pool of YAML indicator, quote, escape, control, line-terminator,
            // Zs and Cf characters in four positions plus pairings, so a future escape has to survive
            // ~230 shapes, not the ones somebody thought of. Lone surrogates are deliberately absent:
            // the encoder's input comes only from YamlDotNet, which cannot deliver one from YAML text.
            var pool = new[]
            {
                '-', '?', ':', '#', '&', '*', '!', '|', '>', '%', '@', '`', '~',
                '"', '\'', '\\', '[', ']', '{', '}', ',', ' ', '\t', '\n', '\r',
                '\0', '\u0007', '\u001b',
                '\u0085', '\u2028', '\u2029', '\u00a0', '\ufeff',
                'a', '0', '.', '_',
            };
            var items = new List<string>();
            foreach (var c in pool)
            {
                items.Add(c.ToString());
                items.Add(c + " a");
                items.Add("a " + c);
                items.Add("a" + c + "b");
                items.Add(string.Concat(c, '\\'));
                items.Add(string.Concat(c, '"'));
            }

            var encoded = WorkflowParser.EncodeProvenanceList(items);
            // One stored line stays ONE LINE under every Unicode line terminator, not just ASCII's.
            foreach (var terminator in new[] { '\n', '\r', '\u0085', '\u2028', '\u2029' })
                Assert.DoesNotContain(terminator, encoded);

            // Decode with the YAML engine itself, which is the only referee that matters here.
            var parser = new YamlDotNet.Core.Parser(new System.IO.StringReader(encoded));
            parser.Consume<YamlDotNet.Core.Events.StreamStart>();
            parser.Consume<YamlDotNet.Core.Events.DocumentStart>();
            parser.Consume<YamlDotNet.Core.Events.SequenceStart>();
            var decoded = new List<string>();
            while (!parser.TryConsume<YamlDotNet.Core.Events.SequenceEnd>(out _))
            {
                Assert.True(parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var s),
                    $"the stored form re-read as structure, not a scalar: {encoded}");
                decoded.Add(s.Value);
            }
            Assert.Equal(items, decoded);
        }

        [Fact]
        public void C22_a_bare_provenance_key_is_an_empty_bag()
        {
            // Read four, finding 2: the bare-section ruling applied to maps. Pre-swap v2 read an empty
            // `provenance:` as an empty bag; a bare optional map key means an empty map.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "---",
                "## Step 1: Go", "Body.");
            Assert.True(def.Error == null, "a bare 'provenance:' was refused: " + def.Error);
            Assert.Empty(def.Provenance);

            // Control the other way: a real VALUE on the key is still refused.
            var withValue = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance: legacy",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(withValue.Error);
            Assert.Contains("provenance", withValue.Error);
        }

        [Fact]
        public void C23_a_bare_with_key_is_an_empty_hand_over()
        {
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "call:",
                "  workflow: other",
                "  with:",
                "```",
                "Body.");
            Assert.True(def.Error == null, "a bare 'with:' was refused: " + def.Error);
            Assert.Empty(def.Steps[0].Call.With);

            // Control the other way: a real VALUE on the key is still refused.
            var withValue = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "call:",
                "  workflow: other",
                "  with: x",
                "```",
                "Body.");
            Assert.NotNull(withValue.Error);
            Assert.Contains("with", withValue.Error);
        }

        [Fact]
        public void C24_a_quoted_list_item_keeps_its_content_exactly()
        {
            // Read five, finding 2: every scalar inside a real YAML sequence was trimmed, quoted ones
            // included, so `[" finance "]` was ACCEPTED AND ALTERED to `finance` — the silent-change
            // fault in its last hiding place, reaching tags, triggers, ops, loop literals, returns,
            // slot values and shape lists alike. The faithful rule: a QUOTED scalar keeps its content
            // exactly (the author asked for those spaces); a PLAIN scalar sheds edge whitespace because
            // YAML itself says so. Two scopes asserted so the fix is the reader's, not one call site's.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [\" finance \", plain]",
                "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "forEach: { in: [\" Sales \"], as: table }",
                "```",
                "Body.");
            Assert.True(def.Error == null, "a quoted padded item was refused: " + def.Error);
            Assert.Equal(new[] { " finance ", "plain" }, def.Tags);
            Assert.Equal(new[] { " Sales " }, def.Steps[0].ForEach.InLiteral);
        }

        [Theory]
        [InlineData("\"schemaVersion\": 2")]   // quoted key
        [InlineData("'schemaVersion': 2")]     // single-quoted key
        [InlineData("\"schemaVersion\\\": 2")] // escaped quote in the key
        [InlineData("\"schemaVersion: 2")]     // unclosed leading quote
        [InlineData("schemaVersion\": 2")]     // dangling trailing quote
        [InlineData("'schemaVersion\": 2")]    // mismatched pair
        public void C25_every_nonplain_version_spelling_loads_frozen_as_v1_and_warns(string versionLine)
        {
            // THE READ-EIGHT RULING, replacing two rounds of raw-line quoting taxonomy that each bled a
            // finding: the scan recognises exactly ONE spelling (plain `schemaVersion:` at column zero),
            // so every other spelling is not a version to it — no refusals, no downgrade-versus-broken
            // judgement, no YAML semantics reimplemented on raw lines. The file loads EXACTLY as the
            // pinned pre-v2 parser loads it (byte-frozen, proved by the fixtures and goldens), and the
            // additive warn says the line is not being read as the version. Any v2 construct in the file
            // additionally draws the inert-fence warn, so nothing v2-shaped goes quietly.
            var def = V2(
                "---", versionLine, "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: guarded",
                "when: date.dayOfMonth < 0",
                "```",
                "Body.");
            Assert.True(def.Error == null, "a non-plain version spelling was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            Assert.Equal("step-1", def.Steps[0].Id);            // the fence is inert prose under v1, frozen
            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("yaml step"));
        }

        [Theory]
        [InlineData("\"schemaVersion\": 1")]   // quoted key, decodes to schemaVersion
        [InlineData("'schemaVersion': 1")]
        public void C25b_in_v2_a_second_decoded_version_key_is_refused_by_the_model_walk(string secondLine)
        {
            // The other half of the ruling: v2's model walk enforces the rest, and it works on DECODED
            // keys, so the quoted spelling — and every spelling nobody has thought of yet — meets the
            // one-declaration rule by construction, because YamlDotNet does the decoding.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", secondLine, "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(def.Error);
            Assert.Contains("schemaVersion", def.Error);
        }

        [Theory]
        [InlineData("\" schemaVersion\": 2")]  // inner leading space: a DIFFERENT key under YAML
        [InlineData("SchemaVersion: 2")]       // case differs: a different key (YAML keys are case-sensitive)
        [InlineData("SCHEMAVERSION: 2")]
        public void C25c_a_genuinely_different_key_is_not_the_version_and_reads_as_v1(string line)
        {
            // Deliberately NOT versions and NOT refusals: YAML keys are case-sensitive and keep inner
            // whitespace, so these are ordinary unknown keys — provenance in v1, and the closed-set
            // refusal in a real v2 file. Pinned so nobody "helpfully" widens the match later.
            var def = V2(
                "---", line, "name: t", "title: T", "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Fact]
        public void C25d_a_nonplain_version_key_below_a_closed_scan_window_loads_v1_and_warns()
        {
            // Read seven refused this shape; the read-eight ruling replaces the refusal with the
            // section 1.2.1 pattern, because the refusal's classifier was itself the defect farm: the
            // file loads exactly as the frozen reader loads it, the warn says the line is not being read
            // as the version, and the inert step fence draws its own warn beside it.
            var def = V2(
                "---",
                "name: t",
                "slots:",
                "  - name: region",
                "    question: Which region?",
                "schemaVersion\": 2",
                "---",
                "## Step 1: Go",
                "```yaml step",
                "id: guarded",
                "when: date.dayOfMonth < 0",
                "```",
                "Body.");
            Assert.True(def.Error == null, "a below-window non-plain spelling was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("schemaVersion"));
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("yaml step"));
        }

        [Fact]
        public void C25e_control_a_quoted_LINE_below_the_window_is_not_a_version_and_does_not_refuse()
        {
            // The class-A boundary, held from the other side: the same position, but a quoted LINE
            // (colon inside a closed pair) is a scalar and must go on loading as v1.
            var def = V2(
                "---",
                "name: t",
                "slots:",
                "  - name: region",
                "    question: Which region?",
                "\"schemaVersion: 2\"",
                "---",
                "## Step 1: Go", "Body.");
            Assert.Null(def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Fact]
        public void C26_a_whitespace_only_quoted_item_is_refused_not_admitted()
        {
            // Read six, finding 2: C24 pins padded CONTENT and C9/C14 pin the EMPTY string, so a check
            // narrowed to zero-length would stay green everywhere while `["   "]` slipped through.
            // Watched red against exactly that narrowed check before being trusted.
            var tags = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [\"   \"]",
                "---",
                "## Step 1: Go", "Body.");
            Assert.NotNull(tags.Error);
            Assert.Contains("empty", tags.Error, StringComparison.OrdinalIgnoreCase);

            var loop = V2(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go",
                "```yaml step",
                "id: go",
                "forEach: { in: [\"  \"], as: table }",
                "```",
                "Body.");
            Assert.NotNull(loop.Error);
            Assert.Contains("empty", loop.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void C16_the_specs_documented_provenance_list_is_accepted()
        {
            // The spec's own example (workflow-canvas-spec.md, the provenance block) is
            // `derived_from: [wfr-3, wfr-7]`. The spec is the contract; forcing provenance values to
            // scalars refused the documented shape.
            var def = V2(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:",
                "  derived_from: [wfr-3, wfr-7]",
                "  distilled: 2026-07-12",
                "---",
                "## Step 1: Go", "Body.");
            Assert.True(def.Error == null, "the spec's documented provenance shape was refused: " + def.Error);
            Assert.Equal("[wfr-3, wfr-7]", def.Provenance["derived_from"]);
            Assert.Equal("2026-07-12", def.Provenance["distilled"]);
        }

        // ---- v1 is untouched: the same constructs keep their frozen v1 meaning ---------------------

        [Fact]
        public void V1_control_a_block_scalar_description_stays_inert_in_v1()
        {
            // v1 has no block scalars: `description: |` reads as the literal value '|' and the indented
            // lines are absorbed, exactly as before v2 existed. Routing v1 through a YAML library would
            // CHANGE this, which is why the exit test pins it.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: T",
                "description: |",
                "  First line.",
                "---",
                "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("|", def.Description);
        }

        [Fact]
        public void V1_control_a_quoted_comma_still_splits_a_v1_list()
        {
            // v1 splits on every comma and strips quotes afterwards. A defect, frozen forever (F-065).
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: T",
                "tags: [\"finance, monthly\"]",
                "---",
                "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
        }

        [Fact]
        public void V1_control_a_duplicate_frontmatter_key_still_wins_last_in_v1()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: First", "title: Second", "---",
                "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("Second", def.Title);
        }
    }
}
