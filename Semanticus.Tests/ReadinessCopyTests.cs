using Semanticus.Analysis;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Copy review P1 (rounds 1-3): a handful of readiness messages append the MCP op the user's AI assistant would
    /// call to remediate (e.g. "Enable it (enable_qna)."). The op is load-bearing for the AGENT door, so the RULE
    /// supplies it OUT OF BAND as a typed <see cref="ReadinessCopy.FixOp"/> part (via <see cref="ReadinessCopy.Op"/>),
    /// never a substring of any string. <see cref="ReadinessCopy.Compose"/> builds the finding's <c>Message</c> (op
    /// rendered inline as " (op_id)", byte-identical to the old literal) and <c>DisplayMessage</c> (op dropped; null
    /// when there is no op, never empty). Because the op is never inside a string, nothing is ever scanned or mutated,
    /// so arbitrary literal text — object names, custom-rule messages, literal parentheses, even a raw control char —
    /// passes through BOTH fields untouched. These tests pin every direction and the previously-broken edge cases.
    /// </summary>
    public class ReadinessCopyTests
    {
        // MUST NOT TOUCH: any message with no Op() part is pure literal text. Message == the text byte-for-byte,
        // DisplayMessage == null, no matter what the text contains — an object named like an op, an op-substring
        // identifier, an op mixed with prose in one parenthetical, a non-trailing parenthetical, nested parens,
        // multiple parentheticals, or a literal control char (U+0001, the round-2 sentinel that this design abolishes).
        // Round-1's client regex and round-2's in-band sentinel both mangled cases in this list; the out-of-band design
        // cannot, because it never inspects the string.
        [Theory]
        [InlineData("Measure (enable_qna) is invalid")]                        // object literally NAMED like an op
        [InlineData("Column (enable_qna_v2) auto-aggregates")]                 // op-substring identifier
        [InlineData("Enable it (enable_qna, then review).")]                   // op id mixed with prose in one paren
        [InlineData("Before (enable_qna) after")]                             // non-trailing parenthetical
        [InlineData("Nested (outer (enable_qna)) remains")]                   // nested parens
        [InlineData("A (enable_qna); B (review)")]                            // multiple parentheticals
        [InlineData("Finding (SummarizeBy = None)")]                          // property note
        [InlineData("Visible dates (order_date, ship_date)")]                 // object names
        [InlineData("Measure A\u0001secret\u0001B is invalid")]     // literal U+0001 pair (round-2 sentinel) -> untouched
        [InlineData("Custom \u0001 literal")]                          // a single/unbalanced U+0001 -> untouched
        public void LiteralMessage_IsNeverTouched(string message)
        {
            var (msg, display) = ReadinessCopy.Compose(message);
            Assert.Equal(message, msg);      // agent Message byte-identical to the input (incl. any control char)
            Assert.Null(display);            // no op => DisplayMessage null; the UI renders Message verbatim
        }

        // MUST RENDER: an out-of-band Op() part appears in the agent Message as " (op_id)" and is dropped from the
        // analyst DisplayMessage. The op is a typed part, so this is unaffected by anything the literal text contains.
        [Fact]
        public void MarkedOp_MessageKeepsOp_DisplayDropsIt()
        {
            var (msg, display) = ReadinessCopy.Compose("Enable it", ReadinessCopy.Op("enable_qna"), ".");
            Assert.Equal("Enable it (enable_qna).", msg);
            Assert.Equal("Enable it.", display);
        }

        [Fact]
        public void MultipleOps_AllRenderedInMessage_AllDroppedInDisplay()
        {
            var (msg, display) = ReadinessCopy.Compose(
                "Trim unused objects", ReadinessCopy.Op("unused_objects"),
                " or scope the AI data schema", ReadinessCopy.Op("set_ai_data_schema"),
                " so the fields fall inside the index.");
            Assert.Equal("Trim unused objects (unused_objects) or scope the AI data schema (set_ai_data_schema) so the fields fall inside the index.", msg);
            Assert.Equal("Trim unused objects or scope the AI data schema so the fields fall inside the index.", display);
        }

        // THE CRUX: a literal object-name parenthetical AND an op in one message. The object name is a text part, so it
        // survives in BOTH renderings; only the typed Op() part is dropped from the display. An allowlist or a sentinel
        // could confuse the two; a typed part cannot.
        [Fact]
        public void ObjectNameParenAndOp_ObjectSurvives_OpRemoved()
        {
            var (msg, display) = ReadinessCopy.Compose(
                "Table exposes visible date columns (order_date, ship_date); keep one canonical date",
                ReadinessCopy.Op("mark_date_table"), ".");
            Assert.Equal("Table exposes visible date columns (order_date, ship_date); keep one canonical date (mark_date_table).", msg);
            Assert.Equal("Table exposes visible date columns (order_date, ship_date); keep one canonical date.", display);
        }

        // A control char in a TEXT part survives byte-identically in BOTH Message and DisplayMessage when an op is
        // present (the op is dropped; the text — control char included — is not). Proves the out-of-band split never
        // touches caller text even while it renders an op.
        [Fact]
        public void ControlCharInTextPart_SurvivesInBothFields()
        {
            var (msg, display) = ReadinessCopy.Compose("Measure \u0001x\u0001 named", ReadinessCopy.Op("rename_object"), " is odd");
            Assert.Equal("Measure \u0001x\u0001 named (rename_object) is odd", msg);
            Assert.Equal("Measure \u0001x\u0001 named is odd", display);   // op gone, the U+0001 text intact
            Assert.Contains('\u0001', display);
        }

        // P1-2: DisplayMessage is null (never "") when dropping the op would leave nothing, so the UI's blank-fallback
        // never renders an empty finding. The agent still gets the op in Message.
        [Fact]
        public void OpOnly_MessageHasOp_DisplayIsNull_NotEmpty()
        {
            var (msg, display) = ReadinessCopy.Compose(ReadinessCopy.Op("enable_qna"));
            Assert.Equal(" (enable_qna)", msg);
            Assert.Null(display);
        }

        // Byte-identity: the real op-bearing rule messages render exactly the pre-change literal in Message and a clean
        // op-free DisplayMessage. Pins the agent door against regression for the three representative rules the review
        // diffed against git history (NAME-HIERARCHY single op, LIMIT-QNA-INDEX two ops, DATE-AMBIGUOUS three ops).
        [Fact]
        public void RealRuleMessages_RenderByteIdentical()
        {
            var (renameMsg, renameDisp) = ReadinessCopy.Compose(
                "Hierarchy 'Sales'[H1] has a cryptic name; Q&A/Copilot offer it as a drill path, so rename it to business language",
                ReadinessCopy.Op("rename_object"), ".");
            Assert.Equal("Hierarchy 'Sales'[H1] has a cryptic name; Q&A/Copilot offer it as a drill path, so rename it to business language (rename_object).", renameMsg);
            Assert.Equal("Hierarchy 'Sales'[H1] has a cryptic name; Q&A/Copilot offer it as a drill path, so rename it to business language.", renameDisp);

            var (dateMsg, dateDisp) = ReadinessCopy.Compose(
                "Keep one canonical date and relate through it", ReadinessCopy.Op("mark_date_table"),
                ", hide the extra date columns", ReadinessCopy.Op("set_column_hidden"),
                ", or state the default date in AI instructions", ReadinessCopy.Op("set_ai_instructions"), ".");
            Assert.Equal("Keep one canonical date and relate through it (mark_date_table), hide the extra date columns (set_column_hidden), or state the default date in AI instructions (set_ai_instructions).", dateMsg);
            Assert.Equal("Keep one canonical date and relate through it, hide the extra date columns, or state the default date in AI instructions.", dateDisp);
        }

        // A plain no-op message (the common case: ~40 built-ins and every custom rule) returns Message unchanged and a
        // null DisplayMessage, so the webview renders Message verbatim (App.tsx: displayMessage?.trim() ? ... : message).
        [Fact]
        public void PlainMessage_Unchanged_NullDisplay()
        {
            const string m = "Description of 'Sales' is a placeholder; replace it with the real meaning.";
            var (msg, display) = ReadinessCopy.Compose(m);
            Assert.Equal(m, msg);
            Assert.Null(display);
        }
    }
}
