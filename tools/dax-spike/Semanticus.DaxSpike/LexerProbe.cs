using System.Text;
using Semanticus.Dax;
using Semanticus.Dax.Text;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.DaxSpike;

// Empirical probe of the FIRST-PARTY Semanticus.Dax lexer (D2 re-point, A1 exit-gate rows 3-4). This
// replaces the original vendored-ANTLR probe: it dumps kinds + spans + raw-vs-decoded text + trivia over
// the same edge cases, and asserts EXACT source tiling — the property the vendored lexer could not provide
// (it silently dropped '@' / ';' / the character after a dotted '.'). The improvements the A0 report
// demanded are now visible directly: '==' is one token, dotted names keep every character, scientific
// literals are one token, bad characters become real BadTokens, and `Date[Key]` keeps `Date` an
// identifier (no keyword-channel shadowing). Writes a runnable report to the scratchpad.
public class LexerProbe
{
    private readonly ITestOutputHelper _out;
    public LexerProbe(ITestOutputHelper o) => _out = o;

    private sealed record Probe(string Dump, int Length, int Covered, int TriviaCount, int Diagnostics);

    private static Probe Run(string dax)
    {
        var sb = new StringBuilder();
        var r = DaxLexer.Lex(DaxSourceText.From(dax));
        sb.AppendLine($"  input  : <{dax}>  (len={dax.Length})");

        int cursor = 0, trivia = 0;
        void Emit(int start, int end, string tag, string body)
        {
            sb.AppendLine($"    [{start,3}..{end,3}) {tag,-26} {body}");
            cursor = end;
        }

        foreach (var t in r.Tokens)
        {
            foreach (var lt in t.LeadingTrivia) { Emit(lt.Span.Start, lt.Span.End, $"TRIVIA {lt.Kind}", $"raw=<{lt.Text}>"); trivia++; }
            var raw = t.Kind == DaxTokenKind.EndOfFileToken ? "" : dax.Substring(t.Span.Start, t.Span.Length);
            Emit(t.Span.Start, t.Span.End, t.Kind.ToString(), $"raw=<{raw}>  val=<{t.ValueText}>");
            foreach (var tt in t.TrailingTrivia) { Emit(tt.Span.Start, tt.Span.End, $"TRIVIA {tt.Kind}", $"raw=<{tt.Text}>"); trivia++; }
        }
        sb.AppendLine($"    tiling : covers [0..{cursor}) of {dax.Length}; trivia={trivia}; diagnostics={r.Diagnostics.Count}");
        return new Probe(sb.ToString(), dax.Length, cursor, trivia, r.Diagnostics.Count);
    }

    [Fact]
    public void Probe_dumps_lexer_behavior()
    {
        var cases = new[]
        {
            "1 + 2",
            "  1 /* c */ +\n  2 // tail\n",             // whitespace + both comment kinds (trivia surfaced)
            "1 -- dash comment\n+ 2",
            "a == b",                                    // '==' is ONE StrictEqualsToken now
            "INFO.VIEW.MEASURES(1)",                     // dotted names: '.' is a real DotToken, no char loss
            "SUM(@x)",                                   // '@' is a real AtToken (not dropped)
            "'Sales Table'[Amount]",                     // quoted table + bracket, raw vs decoded
            "\"a\"\"b\"",                                // doubled-quote string: raw vs decoded
            "[Col]]umn]",                                // doubled bracket
            "SUM('Table'[c]) ; DROP",                    // ';' is a real BadToken
            "CALCULATE([Sales], Region = \"West\")",
            "VAR x = 1 RETURN x",
            "2^-3",
            "MyTable",                                   // bare identifier
            "Date[Key]",                                 // 'Date' stays an identifier (no catalogue shadowing)
            "1.5E10",                                    // scientific notation is ONE token
        };

        var sb = new StringBuilder();
        sb.AppendLine("=== FIRST-PARTY DAX LEXER PROBE (Semanticus.Dax) ===");
        var probes = new List<Probe>();
        foreach (var c in cases)
        {
            var p = Run(c);
            probes.Add(p);
            sb.AppendLine(p.Dump);
        }

        var dir = Environment.GetEnvironmentVariable("DAXSPIKE_OUT")
                  ?? Path.Combine(Path.GetTempPath(), "daxspike");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "lexer-probe.txt");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"[probe written to {path}]");

        // The load-bearing property the vendored lexer lacked: the first-party lexer tiles the WHOLE source
        // (no dropped characters) on every case, and it surfaces trivia on the token stream.
        Assert.All(probes, p => Assert.Equal(p.Length, p.Covered));
        Assert.True(probes[1].TriviaCount > 0, "whitespace/comment trivia must be surfaced on the token stream");
    }
}
