using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, M11 UAT round 2 (stale-copy class): Script > DAX and Script > TMDL hand the person (or the assistant) a
    /// copy of objects as they stood when scripted. Applying that copy later must not silently write the older values over
    /// a newer change the other door made in between. Either the newer change survives, or the apply writes nothing and
    /// says so. Left failing: the apply has no base to compare with, so it writes every block of the old copy back.
    /// </summary>
    public sealed class VerifyUatUiStaleScriptTests
    {
        private static async Task<LocalEngine> FreshAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("StaleScript", 1604);
            await e.CreateTableAsync("T", "agent");
            return e;
        }

        private static async Task<MeasureRow> Row(LocalEngine e, string name) => (await e.ListMeasuresAsync()).First(m => m.Name == name);

        // The person scripts two measures as DAX and starts editing A. The assistant then changes B. The person applies
        // the script: B's block is the old expression, and writing it back undoes the assistant's change with no notice.
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_stale_dax_script_does_not_write_an_older_expression_over_a_newer_change(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            var script = (await e.ScriptObjectsAsync(new[] { a, b }, "dax")).Replace("\r\n", "\n"); // the script ends its lines with the platform's line end (CRLF on Windows); the edits below match LF
            Assert.Contains("// @object " + b, script);

            await e.SetDaxAsync(b, "20", other);                 // the newer change, made while the script is open

            var edited = script.Replace("\n1\n", "\n10\n");      // the scripter's own edit, to A only
            Assert.NotEqual(script, edited);
            ApplyScriptResult result = null;
            Exception refused = null;
            try { result = await e.ApplyDaxScriptAsync(edited, scripter); }
            catch (InvalidOperationException ex) { refused = ex; }

            Assert.True("20" == (await Row(e, "B")).Expression,
                $"The {other}'s newer expression for B was written over by the {scripter}'s older script copy with no notice. "
                + $"Applied: [{string.Join(", ", result?.Applied ?? Array.Empty<string>())}], skipped: [{string.Join(", ", result?.Skipped ?? Array.Empty<string>())}], refused: {refused?.Message ?? "no"}.");
        }

        // The person scripts a measure as TMDL to change its formula. The assistant then rewrites the same measure's
        // description. The TMDL block carries the old description, and applying it puts the old words back.
        [Theory]
        [InlineData("human", "agent")]
        [InlineData("agent", "human")]
        public async Task A_stale_tmdl_script_does_not_write_an_older_description_over_a_newer_change(string scripter, string other)
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "agent");
            var script = await e.ScriptObjectsAsync(new[] { a }, "tmdl");
            Assert.Contains("Old words.", script);

            await e.SetObjectPropertyAsync(a, "Description", "Newer words.", other);   // made while the script is open

            var edited = script.Replace("= 1", "= 10");          // the scripter's own edit, to the formula only
            Assert.NotEqual(script, edited);
            var result = await e.ApplyTmdlScriptAsync(edited, scripter);

            var row = await Row(e, "A");
            Assert.True(row.Description == "Newer words.",
                $"The {other}'s newer description was written over by the {scripter}'s older TMDL copy with no notice: it now reads '{row.Description}'. "
                + $"Applied: [{string.Join(", ", result.Applied ?? Array.Empty<string>())}], skipped: [{string.Join(", ", result.Skipped ?? Array.Empty<string>())}].");
        }
    }
}
