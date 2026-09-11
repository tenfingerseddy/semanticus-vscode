using Semanticus.Dax;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Text;
using Semanticus.DaxSpike;

// A2 §12.4, the corpus bar. Run locally, paste the output into the PR.
//
//   dotnet run -c Release --project tools/dax-corpus-bar/Semanticus.DaxCorpusBar
//
// It is a RATE plus a WHITELIST, never an absolute count, and it prints its partition counts on every run,
// because the corpus is a live junction and drifts (§12.4 items 4 and 5). A run that reports fewer than
// 3,555 / 1,016 / 2,539 is a PARTIAL SNAPSHOT: it is labelled as such, it is a bar failure, and its rates
// must not be quoted as corpus-wide. Reproducing the full corpus needs both halves present:
//
//   git submodule update --init --recursive external/TabularEditor
//   a local link or clone at the gitignored tools/readiness-corpus/work/

const double CleanFloor = 0.999;   // §12.4 item 2

// §12.4 item 3: the whitelist is exactly the FOUR known harvester artifacts, identified rather than
// counted. Three are measures whose multi-line HTML/CSS string bodies the line-based TMDL extractor
// truncates mid-string; one is the stray `1)'` left behind by the same truncation
// (A0-greentree-results §3, §8). Pinning them by identity, not by count, is what makes a DIFFERENT
// non-clean expression fail the bar even while the total stays at four.
var whitelist = new (string FileSuffix, string Kind, string TextPrefix)[]
{
    ("PBIR x-ray.SemanticModel/TMDLScripts/Script 1.tmdl", "measure", "VAR _navVisual ="),
    ("PBIR x-ray.SemanticModel/definition/tables/Visuals_pbir.tmdl", "measure", "VAR _navVisual ="),
    ("PBIR x-ray.SemanticModel/definition/tables/Visuals_pbir.tmdl", "measure", "VAR _css = \""),
    ("PNP_Publicada_dev.SemanticModel/definition/tables/NaturezaDespesa.tmdl", "calcColumn", "1)'")
};

var root = Corpus.RepoRoot();
var bim = Corpus.HarvestBim(root);
var tmdl = Corpus.HarvestTmdl(root);
var all = bim.Concat(tmdl).ToList();

var vendored = all.Count(i => i.File.Contains("/external/TabularEditor/", StringComparison.OrdinalIgnoreCase));
var readiness = all.Count(i => i.File.Contains("/tools/readiness-corpus/", StringComparison.OrdinalIgnoreCase));
var bimFiles = all.Where(i => i.File.EndsWith(".bim", StringComparison.OrdinalIgnoreCase)).Select(i => i.File).Distinct().Count();
var tmdlFiles = all.Where(i => i.File.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase)).Select(i => i.File).Distinct().Count();

Console.WriteLine("A2 §12.4 corpus bar");
Console.WriteLine($"  repo root            {root}");
Console.WriteLine();
Console.WriteLine("PARTITIONS (§12.4 item 4 - printed every run; the corpus is a live junction)");
Console.WriteLine($"  expressions total    {all.Count}");
// NB: these file counts are files that YIELDED at least one expression, not files walked. A2 §6.1's
// "21 .bim and 713 .tmdl files" counts files scanned, so the two figures differ legitimately; the
// EXPRESSION partitions are the ones the baseline pins.
Console.WriteLine($"  from .bim            {bim.Count}   ({bimFiles} files yielding expressions)");
Console.WriteLine($"  from .tmdl           {tmdl.Count}   ({tmdlFiles} files yielding expressions)");
Console.WriteLine($"  vendored TabularEditor {vendored}");
Console.WriteLine($"  readiness corpus     {readiness}");
Console.WriteLine($"  A2 baseline          3555 / 1016 / 2539 / 288 / 3267");
var partial = all.Count < 3555 || bim.Count < 1016 || tmdl.Count < 2539;
if (partial) Console.WriteLine("  *** PARTIAL SNAPSHOT: fewer expressions than the A2 baseline. Rates below are NOT corpus-wide. ***");
Console.WriteLine();

var tokens = 0L;
var roundTripFailures = new List<CorpusItem>();
var nonClean = new List<(CorpusItem Item, string Reasons)>();

foreach (var item in all)
{
    var text = DaxSourceText.From(item.Dax);
    tokens += DaxLexer.Lex(text).Tokens.Count - 1;   // exclude EOF

    var tree = DaxSyntaxTree.Parse(text, DaxParseOptions.Default);

    // §12.4 item 1: 100% exact round-trip, ordinally. No whitelist, no exceptions.
    if (!string.Equals(tree.Root.ToFullString(), item.Dax, StringComparison.Ordinal))
        roundTripFailures.Add(item);

    // §12.4 item 2: "clean" = no missing token, no skipped-token trivia, no DAXP diagnostic.
    var parserDiagnostics = tree.GetDiagnostics().Where(d => d.Code.StartsWith("DAXP", StringComparison.Ordinal)).ToList();
    var reasons = new List<string>();
    if (tree.Root.ContainsRecovery) reasons.Add("recovery (missing token or skipped-token trivia)");
    if (parserDiagnostics.Count > 0)
        reasons.Add(string.Join(", ", parserDiagnostics.Select(d => $"{d.Code}[{d.Span.Start}..{d.Span.End})")));

    if (reasons.Count > 0) nonClean.Add((item, string.Join(" | ", reasons)));
}

var whitelisted = nonClean.Where(n => IsWhitelisted(n.Item)).ToList();
var unexplained = nonClean.Where(n => !IsWhitelisted(n.Item)).ToList();

var cleanCount = all.Count - nonClean.Count;
var rawRate = all.Count == 0 ? 0d : (double)cleanCount / all.Count;

// §12.4 item 3: the whitelisted artifacts are counted, named, and EXCLUDED from the clean-parse rate.
var scored = all.Count - whitelisted.Count;
var scoredRate = scored == 0 ? 0d : (double)cleanCount / scored;

Console.WriteLine("RESULTS");
Console.WriteLine($"  tokens               {tokens}");
Console.WriteLine($"  round-trip failures  {roundTripFailures.Count}   (§12.4 item 1 requires 0)");
Console.WriteLine($"  clean parse (raw)    {cleanCount}/{all.Count} = {rawRate:P3}   (the figure A2 §12.4 quotes)");
Console.WriteLine($"  clean parse (scored) {cleanCount}/{scored} = {scoredRate:P3}   (§12.4 item 2 requires >= 99.9%, whitelist excluded)");
Console.WriteLine($"  non-clean            {nonClean.Count}   ({whitelisted.Count} whitelisted, {unexplained.Count} unexplained)");
Console.WriteLine();

if (nonClean.Count > 0)
{
    Console.WriteLine("NON-CLEAN EXPRESSIONS (named, counted, and identified rather than merely counted)");
    foreach (var (item, reasons) in nonClean)
    {
        Console.WriteLine($"  [{(IsWhitelisted(item) ? "whitelisted" : "UNEXPLAINED")}] {Relative(item.File, root)}  [{item.Kind}]");
        Console.WriteLine($"    reason: {reasons}");
        Console.WriteLine($"    text:   {Excerpt(item.Dax)}");
    }
    Console.WriteLine();
}

foreach (var missing in whitelist.Where(w => !nonClean.Any(n => Matches(n.Item, w))))
    Console.WriteLine($"  NOTE: whitelisted artifact no longer present or now parses clean: {missing.FileSuffix} [{missing.Kind}] {missing.TextPrefix}");

foreach (var item in roundTripFailures.Take(20))
    Console.WriteLine($"  ROUND-TRIP FAILURE  {Relative(item.File, root)} [{item.Kind}]: {Excerpt(item.Dax)}");

var failures = new List<string>();
if (roundTripFailures.Count > 0) failures.Add($"§12.4 item 1: {roundTripFailures.Count} round-trip failures (must be 0)");
if (unexplained.Count > 0)
    failures.Add($"§12.4 item 3: {unexplained.Count} non-clean expression(s) outside the 4-item whitelist; " +
                 "each must be diagnosed as a grammar gap or a new harvester artifact before A2 exits");
if (scoredRate < CleanFloor && scored > 0) failures.Add($"§12.4 item 2: clean rate {scoredRate:P3} is below 99.9%");
if (all.Count == 0) failures.Add("no corpus found: initialize the submodule and link tools/readiness-corpus/work");
if (partial) failures.Add("partial snapshot: fewer expressions than the A2 baseline 3555 / 1016 / 2539; a degraded gate is never a pass");

if (failures.Count == 0)
{
    Console.WriteLine("BAR PASSED.");
    return 0;
}

Console.WriteLine("BAR FAILED:");
foreach (var failure in failures) Console.WriteLine($"  - {failure}");
return 1;

bool IsWhitelisted(CorpusItem item) => whitelist.Any(w => Matches(item, w));

static bool Matches(CorpusItem item, (string FileSuffix, string Kind, string TextPrefix) entry)
    => item.File.Replace('\\', '/').EndsWith(entry.FileSuffix, StringComparison.OrdinalIgnoreCase)
       && string.Equals(item.Kind, entry.Kind, StringComparison.Ordinal)
       && item.Dax.TrimStart().StartsWith(entry.TextPrefix, StringComparison.Ordinal);

static string Relative(string file, string root)
    => file.Replace('\\', '/').Replace(root.Replace('\\', '/') + "/", string.Empty, StringComparison.OrdinalIgnoreCase);

static string Excerpt(string dax)
{
    var single = dax.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", " ");
    return single.Length <= 160 ? single : single[..160] + " ...";
}
