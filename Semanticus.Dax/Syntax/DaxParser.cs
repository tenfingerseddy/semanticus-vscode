using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// The recursive-descent parser (A2 §4). PR-1 tree infrastructure ships without it; the next slice fills
/// this in. The seam is deliberately narrow: one entrypoint, one green root out.
///
/// <para>
/// Contract for the implementation:
/// </para>
/// <list type="number">
///   <item>Lex once with <c>DaxLexer.Lex</c>. Never re-lex, re-classify, or merge tokens (A2 §2 rule 1).</item>
///   <item>Attach EVERY <c>DaxLexResult.Diagnostics</c> entry to the green token or trivia whose raw span
///         contains its <c>Span.Start</c>, using <see cref="LexicalDiagnosticIndex"/>, and assert
///         <c>UnclaimedCount == 0</c> at the end (A2 §8.1 rules 1-3, 6).</item>
///   <item>Attach every <c>DAXP</c> diagnostic to a green element too. <see cref="DaxSyntaxTree"/> reports
///         diagnostics by walking the green tree, so a diagnostic that is not attached does not exist.</item>
///   <item>Return the green root for the family <see cref="DaxParseOptions.SourceKind"/> selects
///         (A2 §3.1). Root type never depends on the input's content.</item>
///   <item>Read <see cref="DaxParseOptions.TargetCapabilities"/> exactly zero times (A2 §9).</item>
/// </list>
/// <para>
/// One thing the tree layer cannot decide for you: a zero-width missing token takes its POSITION from the
/// widths of the slots before it, so it lands before the following token's leading trivia. A2 §8.4 rule 2
/// anchors the diagnostic at "the start of the current token". Those two points differ whenever the
/// following token owns leading trivia, so the diagnostic's span must be built explicitly rather than read
/// back off the inserted token.
/// </para>
/// </summary>
internal static class DaxParser
{
    internal static GreenSyntaxNode ParseRoot(DaxSourceText text, DaxParseOptions options, CancellationToken cancellationToken)
        => ParseRootCore(text, options, cancellationToken, null);

    /// <summary>
    /// Internal, test-only entry, mirroring <c>DaxLexer.LexCore</c>. <paramref name="checkpointObserver"/>
    /// is invoked with the current token index immediately before every parse-phase cancellation check, so
    /// the polling bound is provable and a test can cancel from a known checkpoint (A2 §12.3 item 4).
    /// <paramref name="materializationCheckpointObserver"/> is a separate seam: constructor materialization
    /// of tokens and attached trivia reports the count of items materialized so far, and cannot satisfy a
    /// parse-phase observer (or the other way around). Neither is part of the public surface and both have
    /// zero effect on output when null.
    /// </summary>
    internal static GreenSyntaxNode ParseRootCore(
        DaxSourceText text,
        DaxParseOptions options,
        CancellationToken cancellationToken,
        Action<int>? checkpointObserver,
        Action<int>? materializationCheckpointObserver = null)
        => new Parsing.Parser(text, options, cancellationToken, checkpointObserver, materializationCheckpointObserver).ParseRoot();

    /// <summary>
    /// Internal, test-only: the nesting-depth counter AFTER the parse. A2 §2.1 counts concurrently open
    /// productions, so it must return to zero; A2PR1-S1 was an unmatched <c>CloseProduction</c> that left it
    /// at -1 and silently handed a UDF body one level more than <see cref="DaxParseOptions.MaximumNestingDepth"/>
    /// allows. The counter is what A2PR1-M1's caret guard reads, so it is asserted directly rather than
    /// inferred from a diagnostic.
    /// </summary>
    internal static int ParseRootAndReportFinalDepth(DaxSourceText text, DaxParseOptions options)
    {
        var parser = new Parsing.Parser(text, options, CancellationToken.None, null);
        parser.ParseRoot();
        return parser.FinalNestingDepth;
    }
}
