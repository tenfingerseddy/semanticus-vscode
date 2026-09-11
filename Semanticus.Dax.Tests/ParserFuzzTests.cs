using System.Text;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Deterministic seeded pseudo-fuzz for the parser's two unconditional promises: TOTALITY (A2 §2 rule 3 —
/// every finite input yields a tree, and only cancellation or resource exhaustion may prevent it) and
/// PROGRESS (A0 §11.6 — every recovery loop terminates). Round-trip (§2 rule 4), span tiling, and the
/// §8.1 rule 6 conservation invariant ride along on every input, because they must hold for arbitrary
/// input and not only for the goldens.
///
/// <para>
/// The generator mirrors <see cref="FuzzTests"/>: a self-contained SplitMix64 PRNG with a fixed seed, so
/// the exact input sequence is reproducible on every platform, and inputs mix random UTF-16 with mutated
/// real DAX and spliced token fragments. The count is lower than A1's 100,000 because a parse is roughly
/// an order of magnitude more work than a lex; raise it with <c>SEMANTICUS_DAX_PARSE_FUZZ_COUNT</c>, which
/// may only ever raise the floor.
/// </para>
/// </summary>
public class ParserFuzzTests
{
    private const int MinimumCount = 20_000;
    private const ulong Seed = 0xA2DA_C0DE_2026_0728UL;
    private static readonly int Count = Resolve();

    private static int Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("SEMANTICUS_DAX_PARSE_FUZZ_COUNT");
        return int.TryParse(raw, out var n) && n > MinimumCount ? n : MinimumCount;
    }

    private sealed class Rng
    {
        private ulong _s;
        public Rng(ulong seed) => _s = seed;
        public ulong Next()
        {
            _s += 0x9E3779B97F4A7C15UL;
            var z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(Next() % (ulong)maxExclusive);
        public bool NextBool() => (Next() & 1) == 0;
    }

    private static readonly string[] RealSnippets =
    {
        "VAR x = SUM(Sales[Amount]) RETURN x * 1.1",
        "CALCULATE([Revenue], 'Date'[Year] = 2026)",
        "IF([Margin] >= 0.5, \"High\", \"Low\")",
        "INFO.VIEW.MEASURES()",
        "-- a comment\r\n[Amount] + 2 && [Flag] || NOT [X]",
        "'Table Name'[Col] IN {1, 2, 3}",
        "(x: INT64 = 1, y: TABLE EXPR) => x + y",
        "TOTALYTD([Sales], 'Fiscal')",
        "{(1,2,3), (4,5,6)}",
        "NOT(ISBLANK([x])) && -2 ^ 2 <> 2 ^ -3"
    };

    private static readonly string[] Fragments =
    {
        "SUM", "CALCULATE", "VAR", "RETURN", "IN", "NOT", "TABLE", "COLUMN", "DEFINE", "TOTAL",
        "(", ")", "{", "}", "[Col]", "[Col]]x]", "'Tbl'", "'O''B'", "\"str\"", "\"a\"\"b\"",
        "1", "1.5", ".5", "1E5", "0xFF", "dt\"2026-07-23\"",
        "+", "-", "*", "/", "^", "&", "&&", "||", "=", "==", "=>", "<", "<=", ">", ">=", "<>",
        ".", ",", "@p", ":", " ", "\t", "\r\n", "// line\n", "/* c */", "/* open", "\"open",
        "'open", "[open", "$", "#", ";", "%", "\\", "!", "?", "Date", "INFO.VIEW"
    };

    private static readonly DaxParseOptions[] Roots =
    {
        DaxParseOptions.Default,
        new() { AllowLeadingEquals = true },
        new() { SourceKind = DaxSourceKind.UdfBody }
    };

    [Fact]
    public void Fuzz_totality_progress_round_trip_and_conservation()
    {
        var rng = new Rng(Seed);

        for (var i = 0; i < Count; i++)
        {
            var source = (i % 3) switch
            {
                0 => RandomUtf16(rng),
                1 => MutateSnippet(rng),
                _ => SpliceFragments(rng)
            };
            var options = Roots[rng.NextInt(Roots.Length)];

            CheckOne(source, options, i);
        }
    }

    private static void CheckOne(string source, DaxParseOptions options, int i)
    {
        // TOTALITY (A2 §2 rule 3): a tree comes back, for any finite input, with no exception. Termination
        // is asserted by the fact that this call returns at all: an unguarded recovery loop hangs here.
        DaxSyntaxTree tree;
        try
        {
            tree = Parse(source, options);
        }
        catch (Exception ex)
        {
            Assert.Fail($"totality broke at i={i}: {ex.GetType().Name}: {ex.Message}\nsrc={HexDump(source)}");
            return;
        }

        // A2 §2 rule 4: tree-level tiling, ordinally, for EVERY input.
        if (tree.Root.ToFullString() != source)
            Assert.Fail($"round-trip broke at i={i}\nsrc={HexDump(source)}");

        try
        {
            AssertSpansTile(tree.Root, source);
            AssertLexicalConservation(tree, source);
        }
        catch (Exception ex)
        {
            Assert.Fail($"invariant broke at i={i}: {ex.Message}\nsrc={HexDump(source)}");
        }

        // A2 §2 rule 5: determinism, over the same option tuple.
        var again = Parse(source, options);
        if (Render(tree.Root) != Render(again.Root) || Serialize(tree) != Serialize(again))
            Assert.Fail($"determinism broke at i={i}\nsrc={HexDump(source)}");
    }

    /// <summary>
    /// A0 §11.6 stated directly: no recovery loop may consume nothing and try again. Every input in the
    /// battery, plus a set of shapes chosen to sit exactly on a recovery loop's re-entry point, must
    /// terminate and preserve every code unit.
    /// </summary>
    [Theory]
    [InlineData(",,,,,,,,,,,,,,,,")]
    [InlineData("))))))))))))))))")]
    [InlineData("}}}}}}}}}}}}}}}}")]
    [InlineData("((((((((((((((((")]
    [InlineData("{{{{{{{{{{{{{{{{")]
    [InlineData("VAR VAR VAR VAR VAR")]
    [InlineData("RETURN RETURN RETURN")]
    [InlineData("F(,,,,,,,,,,,,,,,,)")]
    [InlineData("{,,,,,,,,}")]
    [InlineData("=> => => => =>")]
    [InlineData("......")]
    [InlineData("#$%#$%#$%#$%")]
    [InlineData("IN IN IN IN")]
    [InlineData("NOT NOT NOT NOT NOT")]
    public void Recovery_loops_make_progress_on_pathological_input(string source)
    {
        foreach (var options in Roots)
        {
            var tree = Parse(source, options);
            Assert.Equal(source, tree.Root.ToFullString());
            AssertSpansTile(tree.Root, source);

            // Every code unit is owned by exactly one SURFACE token, with any skipped source living inside
            // that token's trivia (A0 §11.3). Concatenating the surface tokens must rebuild the input.
            Assert.Equal(source, string.Concat(tree.Root.DescendantTokens().Select(t => t.ToFullString())));
        }
    }

    private static string RandomUtf16(Rng rng)
    {
        var length = rng.NextInt(40);
        var builder = new StringBuilder(length);
        for (var j = 0; j < length; j++)
        {
            int unit = rng.NextInt(10) switch
            {
                0 => 0xFEFF,
                1 => rng.NextInt(0x20),
                2 => 0xD800 + rng.NextInt(0x400),
                3 => 0xDC00 + rng.NextInt(0x400),
                4 => "+-*/^&=<>|(){}[],.@:".ToCharArray()[rng.NextInt(19)],
                5 => "'\"".ToCharArray()[rng.NextInt(2)],
                _ => rng.NextInt(0x10000)
            };
            builder.Append((char)unit);
        }
        return builder.ToString();
    }

    private static string MutateSnippet(Rng rng)
    {
        var builder = new StringBuilder(RealSnippets[rng.NextInt(RealSnippets.Length)]);
        var mutations = 1 + rng.NextInt(4);
        for (var m = 0; m < mutations && builder.Length > 0; m++)
        {
            var index = rng.NextInt(builder.Length);
            switch (rng.NextInt(5))
            {
                case 0: builder.Remove(index, 1); break;
                case 1: builder.Insert(index, (char)rng.NextInt(0x10000)); break;
                case 2: builder[index] = "\"'[]/*".ToCharArray()[rng.NextInt(6)]; break;
                case 3: builder.Insert(index, Fragments[rng.NextInt(Fragments.Length)]); break;
                default: builder[index] = (char)rng.NextInt(0x80); break;
            }
        }
        return builder.ToString();
    }

    private static string SpliceFragments(Rng rng)
    {
        var parts = 1 + rng.NextInt(12);
        var builder = new StringBuilder();
        for (var p = 0; p < parts; p++) builder.Append(Fragments[rng.NextInt(Fragments.Length)]);
        return builder.ToString();
    }

    private static string HexDump(string source)
    {
        var builder = new StringBuilder(source.Length * 6);
        foreach (var c in source) builder.Append("U+").Append(((int)c).ToString("X4")).Append(' ');
        return builder.ToString();
    }
}

/// <summary>
/// A2 §12.3 item 4's parser half, as PR-1's down payment. The parser exposes two internal, test-only
/// checkpoint observers on <c>DaxParser.ParseRootCore</c>. The parse-phase observer is invoked with the
/// current TOKEN INDEX immediately before every parse cancellation check. The materialization observer is
/// a separate seam, invoked with the count of tokens, trivia, list-copy slots, and green-aggregation
/// visits so far. Either phase cannot satisfy the other test: a trivia-heavy one-token input never reaches
/// a parse checkpoint, and a long token stream finishes constructor materialization before the parse
/// observer can cancel. List copies and green aggregation after the last Eat or Skip poll belong to the
/// materialization contract, not the parse-token one. Timing-based or already-cancelled tests cannot prove
/// any of this.
/// </summary>
public class ParserCancellationTests
{
    private const int PollBound = 256;   // Parser.CancellationPollBound

    private static string LongSource(int terms) => string.Join(" + ", Enumerable.Repeat("1", terms));

    /// <summary>
    /// One real token with thousands of leading delimited comments. A1 attaches every initial trivia item
    /// to that token, so the parser constructor materializes thousands of trivia before any Eat() poll.
    /// </summary>
    private static string TriviaHeavySource(int comments) => string.Concat(Enumerable.Repeat("/*x*/", comments)) + "1";

    /// <summary>
    /// One real token, then thousands of stray close-parens. No trivia, so constructor materialization
    /// equals token count. ParseEndOfFile then skips the parens and FlushPendingSkippedInto copies them
    /// into skipped-token trivia after the last SkipCurrent poll.
    /// </summary>
    private static string TrailingSkippedSource(int skipped) => "1" + new string(')', skipped);

    /// <summary>
    /// A table constructor of N ones, no trivia: <c>{1,1,...,1}</c>. Token count is 2N+2
    /// (open, N ones, N-1 commas, close, EOF). After the last Eat poll, SeparatedList copies 2N-1
    /// slots and GreenSyntaxList walks them to aggregate width and flags.
    /// </summary>
    private static string FlatListSource(int ones) => "{" + string.Join(",", Enumerable.Repeat("1", ones)) + "}";

    [Fact]
    public void Checkpoint_gaps_stay_within_the_polling_bound()
    {
        var source = LongSource(4_000);           // ~7,999 tokens: many checkpoints, none vacuous
        var positions = new List<int>();

        DaxParser.ParseRootCore(DaxSourceText.From(source), DaxParseOptions.Default, default, positions.Add);

        Assert.True(positions.Count >= 3, $"too few checkpoints ({positions.Count})");
        for (var i = 1; i < positions.Count; i++)
        {
            var gap = positions[i] - positions[i - 1];
            Assert.True(gap >= 0 && gap <= PollBound,
                $"checkpoint gap {positions[i - 1]}->{positions[i]} ({gap}) exceeds the {PollBound}-token bound");
        }
    }

    [Fact]
    public void Cancelling_from_the_observer_throws_at_that_checkpoint()
    {
        var source = LongSource(4_000);
        using var cts = new CancellationTokenSource();
        var positions = new List<int>();
        var materialized = new List<int>();

        Action<int> observer = index =>
        {
            positions.Add(index);
            if (index >= PollBound) cts.Cancel();   // request cancellation from INSIDE the observer
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                observer,
                materialized.Add));

        Assert.Equal(PollBound, positions[^1]);                 // threw AT the cancel checkpoint...
        Assert.DoesNotContain(positions, p => p > PollBound);   // ...and nothing was parsed past it
        // Materialization of this many tokens finished first. If the parse observer were the materialization
        // seam in disguise, cancel would have fired there and this list would not have passed PollBound.
        Assert.True(materialized.Count >= 3 && materialized[^1] > PollBound,
            $"parse cancel must not be satisfiable by materialization (last={materialized.LastOrDefault()}, n={materialized.Count})");
    }

    [Fact]
    public void Cancelling_from_the_materialization_observer_throws_at_that_checkpoint()
    {
        var source = TriviaHeavySource(4_000);
        using var cts = new CancellationTokenSource();
        var materialized = new List<int>();
        var parsed = new List<int>();

        Action<int> materializationObserver = count =>
        {
            materialized.Add(count);
            if (count >= PollBound) cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                parsed.Add,
                materializationObserver));

        Assert.Equal(PollBound, materialized[^1]);
        Assert.DoesNotContain(materialized, c => c > PollBound);
        Assert.Empty(parsed);   // one real token: the parse observer cannot fire, so it cannot satisfy this
    }

    [Fact]
    public void Cancelling_during_GreenToken_construction_after_materialization_checkpoints_complete()
    {
        // TriviaHeavySource(4000) materializes 4000 leading comments plus the identifier. NoteMaterialized
        // checkpoints at 256..3840, then GreenToken construction rewalks every trivia item. The next
        // checkpoint under one 256-item contract is 4096, inside that late walk. Cancelling only then
        // proves earlier checkpoints completed and that the rewalk is not an unpolled gap.
        const int comments = 4_000;
        var source = TriviaHeavySource(comments);
        using var cts = new CancellationTokenSource();
        var materialized = new List<int>();
        var parsed = new List<int>();
        const int lastFirstPassCheckpoint = (comments / PollBound) * PollBound;
        const int firstLateCheckpoint = lastFirstPassCheckpoint + PollBound;

        Action<int> materializationObserver = count =>
        {
            materialized.Add(count);
            if (count >= firstLateCheckpoint) cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                parsed.Add,
                materializationObserver));

        Assert.Contains(lastFirstPassCheckpoint, materialized);
        Assert.Equal(firstLateCheckpoint, materialized[^1]);
        Assert.DoesNotContain(materialized, c => c > firstLateCheckpoint);
        Assert.Empty(parsed);
    }

    [Fact]
    public void Cancelling_during_skipped_recovery_copy_after_materialization_checkpoints_complete()
    {
        // TrailingSkippedSource(4000) materializes 4002 tokens (1, 4000 parens, EOF) with no trivia.
        // NoteMaterialized checkpoints at 256..3840, then SkipCurrent polls the parse observer, then
        // FlushPendingSkippedInto copies the 4000 skipped tokens into a green list. The next
        // materialization checkpoint under one 256-item contract is 4096, inside that copy. Cancelling
        // only then proves earlier checkpoints completed and that the copy is not an unpolled gap.
        const int skipped = 4_000;
        var source = TrailingSkippedSource(skipped);
        using var cts = new CancellationTokenSource();
        var materialized = new List<int>();
        var parsed = new List<int>();
        const int tokenCount = skipped + 2;
        const int lastFirstPassCheckpoint = (tokenCount / PollBound) * PollBound;
        const int firstLateCheckpoint = lastFirstPassCheckpoint + PollBound;

        Action<int> materializationObserver = count =>
        {
            materialized.Add(count);
            if (count >= firstLateCheckpoint) cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                parsed.Add,
                materializationObserver));

        Assert.Contains(lastFirstPassCheckpoint, materialized);
        Assert.Equal(firstLateCheckpoint, materialized[^1]);
        Assert.DoesNotContain(materialized, c => c > firstLateCheckpoint);
    }

    [Fact]
    public void Cancelling_while_skipped_recovery_copies_existing_leading_trivia()
    {
        // TOTAL is rejected into skipped-token trivia. The comments already belong to the accepted [x]
        // token, so FlushPendingSkippedInto must copy all 400 existing leading trivia items after the
        // constructor's first materialization pass. Cancellation at 1024 lands inside that copy, not in
        // the GreenToken constructor that rewalks the completed list afterwards.
        var source = "TOTAL\n" + string.Concat(Enumerable.Repeat("/*x*/", 400)) + "[x]";
        using var cts = new CancellationTokenSource();
        var materialized = new List<int>();

        Action<int> materializationObserver = count =>
        {
            materialized.Add(count);
            if (count >= 1024) cts.Cancel();
        };

        var error = Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                null,
                materializationObserver));

        Assert.Equal(1024, materialized[^1]);
        Assert.Contains("FlushPendingSkippedInto", error.StackTrace);
        Assert.DoesNotContain("GreenToken..ctor", error.StackTrace);
    }

    [Fact]
    public void Cancelling_during_flat_list_aggregation_after_copy_checkpoints_complete()
    {
        // FlatListSource(2000) materializes 4002 tokens. NoteMaterialized checkpoints at 256..3840.
        // After the last Eat poll, SeparatedList copies 3999 slots and GreenSyntaxList rewalks them.
        // One extra walk reaches 8001 (last checkpoint 7936) and cannot hit 8192. Two extra walks
        // can. Cancelling at 8192 proves both the copy and the aggregation sit on the same 256-item
        // contract, not only the first of those visits.
        const int ones = 2_000;
        var source = FlatListSource(ones);
        using var cts = new CancellationTokenSource();
        var materialized = new List<int>();
        var parsed = new List<int>();
        const int tokenCount = 2 * ones + 2;
        const int slotCount = 2 * ones - 1;
        const int lastFirstPassCheckpoint = (tokenCount / PollBound) * PollBound;
        const int lastCopyCheckpoint = ((tokenCount + slotCount) / PollBound) * PollBound;
        const int firstAggregationCheckpoint = lastCopyCheckpoint + PollBound;

        Action<int> materializationObserver = count =>
        {
            materialized.Add(count);
            if (count >= firstAggregationCheckpoint) cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxParser.ParseRootCore(
                DaxSourceText.From(source),
                DaxParseOptions.Default,
                cts.Token,
                parsed.Add,
                materializationObserver));

        Assert.Contains(lastFirstPassCheckpoint, materialized);
        Assert.Contains(lastCopyCheckpoint, materialized);
        Assert.Equal(firstAggregationCheckpoint, materialized[^1]);
        Assert.DoesNotContain(materialized, c => c > firstAggregationCheckpoint);
    }

    [Fact]
    public void An_already_cancelled_token_stops_before_any_work()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Parse("1 + 1", DaxParseOptions.Default, cts.Token));
    }

    [Fact]
    public void The_observer_has_zero_effect_on_output_when_unset()
    {
        // The seam is internal and must not be observable: same tree, same diagnostics, observer or not.
        const string source = "VAR a = SUM('T'[c], ) RETURN a + TOTAL[x]";

        var observed = DaxParser.ParseRootCore(DaxSourceText.From(source), DaxParseOptions.Default, default, _ => { });
        var plain = DaxParser.ParseRootCore(DaxSourceText.From(source), DaxParseOptions.Default, default, null);

        // Non-vacuity first: an empty rendering, or no diagnostics, would make the two comparisons below
        // agree about nothing. The source above is deliberately malformed so that both are non-empty.
        Assert.NotEmpty(RenderGreen(plain));
        Assert.NotEmpty(SerializeGreen(plain));

        Assert.Equal(plain.ToFullString(), observed.ToFullString());
        Assert.Equal(RenderGreen(plain), RenderGreen(observed));
        Assert.Equal(SerializeGreen(plain), SerializeGreen(observed));
    }

    private static DaxSyntaxTree Parse(string source, DaxParseOptions options, CancellationToken cancellationToken)
        => DaxSyntaxTree.Parse(DaxSourceText.From(source), options, cancellationToken);

    /// <summary>
    /// A total rendering of a GREEN root: kind, full width, flags, and for terminals the raw text, with
    /// trivia and structured trivia included.
    ///
    /// <para>
    /// The observer seam hands back a green root, and a green node has no red wrapper without a
    /// <see cref="DaxSyntaxTree"/> to host it, so <c>ParseHarness.Render</c> cannot be reused. Recursive,
    /// unlike the walkers in the library: the only input is the one fixed literal above, not arbitrary depth.
    /// </para>
    /// </summary>
    private static string RenderGreen(GreenNode root)
    {
        var builder = new StringBuilder();
        Write(root, 0, builder);
        return builder.ToString();

        static void Write(GreenNode node, int depth, StringBuilder builder)
        {
            builder.Append(' ', depth * 2).Append(node.Kind).Append(' ').Append(node.FullWidth)
                   .Append(' ').Append(node.Flags);

            switch (node)
            {
                case GreenToken token:
                    builder.Append(token.IsMissing ? " MISSING" : $" \"{token.Text}\"").Append('\n');
                    foreach (var trivia in token.LeadingTrivia) Write(trivia, depth + 1, builder);
                    foreach (var trivia in token.TrailingTrivia) Write(trivia, depth + 1, builder);
                    return;
                case GreenTrivia trivia:
                    builder.Append(" \"").Append(trivia.Text).Append("\"\n");
                    if (trivia.Structure is { } structure) Write(structure, depth + 1, builder);
                    return;
                default:
                    builder.Append('\n');
                    for (var i = 0; i < node.SlotCount; i++)
                        if (node.GetSlot(i) is { } slot) Write(slot, depth + 1, builder);
                    return;
            }
        }
    }

    /// <summary>
    /// The green root's diagnostics in the same <c>code/severity[start..end)</c> form
    /// <c>ParseHarness.Serialize</c> uses, read the way <see cref="DaxSyntaxTree.GetDiagnostics"/> reads them.
    /// </summary>
    private static string SerializeGreen(GreenNode root)
    {
        var collected = new List<DaxDiagnostic>();
        DiagnosticCollector.Collect(root, collected);
        return string.Join("\n", DiagnosticCollector.Sorted(collected)
            .Select(d => $"{d.Code}/{d.Severity}[{d.Span.Start}..{d.Span.End})"));
    }
}
