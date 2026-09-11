using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.DaxSpike;

// Corpus-driven evidence: harvest every model DAX expression in the repo, prove 100% byte-exact
// round-trip (the hard invariant), and REPORT clean-parse rate + a ranked list of grammar gaps that
// real DAX hits in this prototype. Writes a machine-readable summary to the scratchpad.
public class CorpusTests
{
    private readonly ITestOutputHelper _out;
    public CorpusTests(ITestOutputHelper o) => _out = o;

    private static string OutDir()
    {
        var d = Environment.GetEnvironmentVariable("DAXSPIKE_OUT")
                ?? Path.Combine(Path.GetTempPath(), "daxspike");
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- recovery classification ----
    private static void Collect(GreenNode n, List<string> badChars, List<TokenKind> skippedKinds, List<string> skippedRaw, ref bool missing)
    {
        if (n is GreenToken t)
        {
            if (t.IsMissing) missing = true;
            foreach (var tr in t.Leading) if (tr.Kind == TriviaKind.Bad) badChars.Add(tr.Text);
            foreach (var tr in t.Trailing) if (tr.Kind == TriviaKind.Bad) badChars.Add(tr.Text);
            return;
        }
        var g = (GreenSyntax)n;
        if (g.Kind == NodeKind.SkippedTokens)
            foreach (var c in g.Slots) if (c is GreenToken st) { skippedKinds.Add(st.TokenKind); skippedRaw.Add(st.RawText); }
        foreach (var c in g.Children) Collect(c, badChars, skippedKinds, skippedRaw, ref missing);
    }

    // a skipped identifier of the shape E5 / e10 / E+3 — the tail of an exponent the lexer split off.
    private static readonly Regex ExpTail = new(@"^[eE][+-]?[0-9]+$", RegexOptions.Compiled);

    private static string Bucket(string src, GreenNode root)
    {
        var bad = new List<string>(); var skipped = new List<TokenKind>(); var skippedRaw = new List<string>(); bool missing = false;
        Collect(root, bad, skipped, skippedRaw, ref missing);
        var badText = string.Concat(bad);

        if (badText.Contains('@')) return "xmla @parameter (lexer drops '@')";
        if (skippedRaw.Any(r => ExpTail.IsMatch(r))) return "scientific notation (1E5 / 1.5e10 split by lexer)";
        if (src.TrimStart().StartsWith("=")) return "leading '=' (formula-bar)";
        if (skipped.Contains(TokenKind.KwDefine) || skipped.Contains(TokenKind.KwEvaluate)) return "query DEFINE/EVALUATE statement";
        if (skipped.Contains(TokenKind.KwOrder) || skipped.Contains(TokenKind.KwStart)) return "query ORDER BY / START AT";
        if (badText.Length > 0)
        {
            // report the distinct non-space bad characters
            var chars = new SortedSet<char>();
            foreach (var c in badText) if (!char.IsWhiteSpace(c)) chars.Add(c);
            return "lexer-dropped char(s): " + string.Join("", chars);
        }
        if (skipped.Count > 0) return "skipped tokens: " + string.Join(",", skipped.Distinct().Take(4));
        if (missing) return "missing token inserted (incomplete construct)";
        return "other";
    }

    [Fact]
    public void Corpus_round_trips_100pct_and_reports_stats()
    {
        var items = Corpus.HarvestAll();
        Assert.True(items.Count > 0, "corpus is empty - harvest failed");

        int total = items.Count, clean = 0, rt = 0;
        int bimTotal = 0, bimClean = 0, bimRt = 0;
        int tmdlTotal = 0, tmdlClean = 0, tmdlRt = 0;
        var gapCounts = new Dictionary<string, int>();
        var gapExample = new Dictionary<string, string>();
        var rtFailures = new List<CorpusItem>();

        foreach (var it in items)
        {
            var isBim = it.File.EndsWith(".bim");
            if (isBim) bimTotal++; else tmdlTotal++;

            var r = DaxParser.Parse(it.Dax);
            bool roundTrips = r.Root.ToFullString() == it.Dax;
            if (roundTrips) { rt++; if (isBim) bimRt++; else tmdlRt++; }
            else rtFailures.Add(it);

            if (r.CleanParse) { clean++; if (isBim) bimClean++; else tmdlClean++; }
            else
            {
                var b = Bucket(it.Dax, r.Root);
                gapCounts[b] = gapCounts.GetValueOrDefault(b) + 1;
                if (!gapExample.ContainsKey(b)) gapExample[b] = it.Dax.Length > 90 ? it.Dax.Substring(0, 90) + "..." : it.Dax;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== A0 GREEN-TREE CORPUS RESULTS ===");
        sb.AppendLine($"TOTAL expressions      : {total}");
        sb.AppendLine($"  round-trip exact     : {rt} ({Pct(rt, total)})");
        sb.AppendLine($"  clean parse          : {clean} ({Pct(clean, total)})  (no skipped/missing nodes)");
        sb.AppendLine($"  BIM   : {bimTotal} total, {bimRt} round-trip ({Pct(bimRt, bimTotal)}), {bimClean} clean ({Pct(bimClean, bimTotal)})");
        sb.AppendLine($"  TMDL  : {tmdlTotal} total, {tmdlRt} round-trip ({Pct(tmdlRt, tmdlTotal)}), {tmdlClean} clean ({Pct(tmdlClean, tmdlTotal)})");
        sb.AppendLine();
        sb.AppendLine("GRAMMAR GAPS (ranked, prototype did not cleanly parse):");
        foreach (var kv in gapCounts.OrderByDescending(k => k.Value))
            sb.AppendLine($"  {kv.Value,5}  {kv.Key}\n           e.g. {Escape(gapExample[kv.Key])}");
        sb.AppendLine();
        if (rtFailures.Count > 0)
        {
            sb.AppendLine($"ROUND-TRIP FAILURES ({rtFailures.Count}) — INVARIANT VIOLATIONS:");
            foreach (var f in rtFailures.Take(20)) sb.AppendLine($"  [{f.File}] {Escape(f.Dax)}");
        }
        else sb.AppendLine("ROUND-TRIP FAILURES: NONE (invariant holds over the whole corpus).");

        var path = Path.Combine(OutDir(), "corpus-results.txt");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"[written {path}]");

        // HARD invariant: every finite input round-trips byte-exactly (design §5.4).
        Assert.Equal(total, rt);
    }

    private static string Pct(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:F1}%";
    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
