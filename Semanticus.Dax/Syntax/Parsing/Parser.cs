using System.Diagnostics;
using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax.Parsing;

/// <summary>
/// The recursive-descent parser (A2 §4). One instance per parse.
///
/// <para>
/// It consumes A1's flat token stream and never re-lexes, re-classifies, or merges a token (A2 §2 rule 1):
/// every green token in the tree is materialized once, up front, from exactly one <see cref="DaxToken"/>.
/// A token the grammar cannot use is re-hosted as skipped-token trivia with its raw text intact, so
/// <c>ToFullString()</c> is ordinally equal to the source for every input (A2 §2 rule 4).
/// </para>
/// <para>
/// <see cref="DaxParseOptions.TargetCapabilities"/> is read ZERO times (A2 §9), and
/// <see cref="DaxParseOptions.AllowLeadingEquals"/> is read once, to decide a diagnostic and never a shape
/// (A2 §5.2).
/// </para>
/// </summary>
internal sealed partial class Parser
{
    /// <summary>
    /// Items between cancellation checks. A1 polls on a source-position bound (DaxLexer.PollBound). The
    /// parser polls twice, on two different units, through two observers: constructor materialization
    /// counts each token, each attached trivia item, each list-copy slot, and each green aggregation
    /// visit, then the parse loop counts tokens consumed.
    /// Cancellation is the only permitted escape from totality (A2 §2 rule 3).
    /// </summary>
    private const int CancellationPollBound = 256;

    private readonly DaxParseOptions _options;
    private readonly IReadOnlyList<DaxToken> _raw;
    private readonly GreenToken[] _tokens;
    private readonly CancellationToken _ct;
    private readonly Action<int>? _checkpointObserver;
    private readonly Action<int>? _materializationCheckpointObserver;

    private int _index;
    private int _lastCheckpoint;
    private int _materializedCount;
    private int _lastMaterializationCheckpoint;
    private DaxToken? _lastAccepted;

    /// <summary>
    /// Concurrently open grammar productions (A2 §2.1). A left-associative binary LOOP does not increment
    /// it, because a loop opens nothing; a right-associative operand DESCENT does, because it does.
    /// </summary>
    private int _depth;

    /// <summary>
    /// Internal, test-only: the counter after the parse. Every <see cref="TryOpenProduction"/> that returned
    /// true is matched by exactly one <see cref="CloseProduction"/>, so this is zero for every input.
    /// </summary>
    internal int FinalNestingDepth => _depth;

    // Open delimiter counts. They exist so a closing delimiter is a resynchronization stopper only when
    // something is actually open: at the root, a stray ')' is junk to be skipped, not a place to stop
    // (A0 §11.5 says "closing delimiter OF THE CONTAINING EXPRESSION"). Golden G-P-REC-010 turns on this.
    private int _parenDepth;
    private int _braceDepth;
    private int _listDepth;

    /// <summary>The enclosing construct's A0 §11.5 stop set, saved and restored by each production.</summary>
    private Resync _resync = Resync.Root;

    /// <summary>
    /// Set when a diagnostic has already been emitted at the current required-primary position, so the
    /// <see cref="GreenMissingExpression"/> that fills it carries no additional <c>DAXP1001</c>
    /// (A2 §8.3: never <c>DAXP1043</c> AND <c>DAXP1001</c> for the same token at the same position).
    /// </summary>
    private bool _suppressExpressionExpected;

    private readonly List<GreenToken> _pendingSkipped = new();
    private readonly List<DaxDiagnostic> _pendingSkippedDiagnostics = new();

    internal Parser(DaxSourceText text, DaxParseOptions options, CancellationToken cancellationToken, Action<int>? checkpointObserver, Action<int>? materializationCheckpointObserver = null)
    {
        _options = options;
        _ct = cancellationToken;
        _checkpointObserver = checkpointObserver;
        _materializationCheckpointObserver = materializationCheckpointObserver;

        var lexResult = DaxLexer.Lex(text, null, cancellationToken);
        _raw = lexResult.Tokens;

        // A2 §8.1 rules 1-3: every lexical diagnostic attaches to the unique lexical element whose RAW SPAN
        // contains its Span.Start, exactly once, on the green element. Materializing every token here, in
        // source order, is what makes that a single mechanical pass rather than a per-production decision.
        // This pass is also the unit of work for cancellation: a single token can own nearly a million
        // trivia items, and Eat() never sees them. Polling here is a separate seam from the parse-token
        // observer so one phase cannot satisfy the other test.
        var lexicalDiagnostics = new LexicalDiagnosticIndex(lexResult);
        _tokens = new GreenToken[_raw.Count];
        for (var i = 0; i < _raw.Count; i++) _tokens[i] = Materialize(_raw[i], lexicalDiagnostics);

        // A1 §7.4 properties 1 and 3 (partition + multiplicity) guarantee every diagnostic found an owner.
        Debug.Assert(lexicalDiagnostics.UnclaimedCount == 0, "A lexical diagnostic found no owning element; A1 §7.4 partition is broken.");
    }

    private GreenToken Materialize(DaxToken token, LexicalDiagnosticIndex index)
    {
        // Visit order is source order: leading trivia, the token's own raw span, then trailing trivia.
        var leading = MaterializeTrivia(token.LeadingTrivia, index);
        var own = index.Take(token.Span);
        var trailing = MaterializeTrivia(token.TrailingTrivia, index);
        NoteMaterialized();
        // GreenToken sums trivia for width and flags. Those visits belong to the same 256-item
        // materialization contract as NoteMaterialized above; leaving them uncounted was a late gap.
        return GreenFactory.Token(token, leading, trailing, own, NoteMaterialized);
    }

    private IReadOnlyList<GreenTrivia>? MaterializeTrivia(IReadOnlyList<DaxTrivia> trivia, LexicalDiagnosticIndex index)
    {
        if (trivia.Count == 0) return null;
        var green = new GreenTrivia[trivia.Count];
        for (var i = 0; i < trivia.Count; i++)
        {
            green[i] = GreenFactory.Trivia(trivia[i], index.Take(trivia[i].Span));
            NoteMaterialized();
        }
        return green;
    }

    // ---- token access ---------------------------------------------------------------------------

    private DaxToken CurrentRaw => _raw[_index];

    /// <summary>True at A1's terminal EOF token. Recovery loops key on this, never on <see cref="CurrentKind"/>.</summary>
    private bool AtRealEof => CurrentRaw.Kind == DaxTokenKind.EndOfFileToken;

    /// <summary>
    /// The grammar's view of the current token. A1's single resource-limit remainder token
    /// (<c>DaxToken.IsTerminalRecovery</c>, A1 §9.2) reads as EOF here, so the parser stops rather than
    /// parsing an unscanned remainder as ordinary DAX. The token itself is still preserved, as skipped
    /// source, by the ordinary trailing-run rule.
    /// </summary>
    private SyntaxKind CurrentKind => CurrentRaw.IsTerminalRecovery
        ? SyntaxKind.EndOfFileToken
        : SyntaxKindFacts.FromTokenKind(CurrentRaw.Kind);

    private SyntaxKind PeekKind(int offset)
    {
        var i = _index + offset;
        if (i >= _raw.Count) i = _raw.Count - 1;
        return _raw[i].IsTerminalRecovery ? SyntaxKind.EndOfFileToken : SyntaxKindFacts.FromTokenKind(_raw[i].Kind);
    }

    private GreenToken Eat()
    {
        Poll();
        var token = _tokens[_index];
        if (_pendingSkipped.Count > 0) token = FlushPendingSkippedInto(token);
        _lastAccepted = _raw[_index];
        Advance();
        _suppressExpressionExpected = false;
        return token;
    }

    /// <summary>Never advances past EOF; every loop exits on EOF explicitly (A0 §11.6).</summary>
    private void Advance()
    {
        if (_index < _raw.Count - 1) _index++;
    }

    private GreenToken Expect(SyntaxKind kind, string code)
        => CurrentKind == kind ? Eat() : InsertMissingToken(kind, code);

    /// <summary>
    /// A0 §11.2 missing-token insertion. The diagnostic's span is CONSTRUCTED from the current token's raw
    /// start and is deliberately not read back off the inserted token: a zero-width green token takes its
    /// position from the widths of the preceding slots, so it sits before the current token's leading
    /// trivia, while A2 §8.4 rules 1-2 require the diagnostic to sit after it.
    /// </summary>
    private GreenToken InsertMissingToken(SyntaxKind kind, string code)
        => GreenFactory.MissingToken(kind, new[] { Diagnostic(code, InsertionSpan) });

    private TextSpan InsertionSpan
        => new(AtRealEof ? _lastAccepted?.Span.End ?? CurrentRaw.Span.Start : CurrentRaw.Span.Start, 0);

    private static DaxDiagnostic Diagnostic(string code, TextSpan span)
        => new(code, DaxParserDiagnosticCodes.GetDefaultSeverity(code), DaxParserDiagnosticCodes.GetDefaultMessage(code), span);

    private static GreenSyntaxNode WithDiagnostic(GreenSyntaxNode node, string code, TextSpan span)
        => (GreenSyntaxNode)node.WithAdditionalDiagnostics(Diagnostic(code, span));

    // ---- cancellation ---------------------------------------------------------------------------

    private void Poll()
    {
        if (_index - _lastCheckpoint >= CancellationPollBound) Checkpoint();
    }

    private void Checkpoint()
    {
        _lastCheckpoint = _index;
        _checkpointObserver?.Invoke(_index);
        _ct.ThrowIfCancellationRequested();
    }

    private void NoteMaterialized()
    {
        _materializedCount++;
        if (_materializedCount - _lastMaterializationCheckpoint >= CancellationPollBound)
            MaterializationCheckpoint();
    }

    private void MaterializationCheckpoint()
    {
        _lastMaterializationCheckpoint = _materializedCount;
        _materializationCheckpointObserver?.Invoke(_materializedCount);
        _ct.ThrowIfCancellationRequested();
    }

    // ---- skipped-token trivia (A0 §11.3) --------------------------------------------------------

    /// <summary>
    /// Unexpected tokens are buffered and flushed onto the LEADING trivia of the next accepted token.
    ///
    /// <para>
    /// A0 §11.3 also asks for same-line skipped source to become TRAILING trivia of the last accepted
    /// token. That case is not implementable here without rewriting a completed green subtree through call
    /// frames the skipping production cannot reach (green is immutable and the preceding token is already
    /// embedded in a finished node), and at the root A2 §4.1 forbids it outright: "The root's Expression
    /// child is never widened to swallow them", which golden G-P-ROOT-005 restates by putting the trailing
    /// run on EOF. Leading attachment is the one rule that holds at every site. Round-trip, structure, and
    /// every diagnostic span are identical either way; only the owning token differs.
    /// </para>
    /// </summary>
    private GreenToken FlushPendingSkippedInto(GreenToken token)
    {
        var trivia = GreenFactory.SkippedTokensTrivia(
            _pendingSkipped,
            _pendingSkippedDiagnostics.Count > 0 ? _pendingSkippedDiagnostics.ToArray() : null,
            NoteMaterialized);

        var leading = new List<GreenTrivia>(token.LeadingTrivia.Count + 1) { trivia };
        foreach (var existing in token.LeadingTrivia)
        {
            leading.Add(existing);
            NoteMaterialized();
        }

        _pendingSkipped.Clear();
        _pendingSkippedDiagnostics.Clear();
        return token.WithTrivia(leading, token.TrailingTrivia, NoteMaterialized);
    }

    /// <summary>Re-hosts the current token, carrying its lexical diagnostics unchanged (A2 §8.1 rule 4).</summary>
    private void SkipCurrent()
    {
        Poll();
        _pendingSkipped.Add(_tokens[_index]);
        Advance();
    }

    /// <summary>
    /// Re-hosts the current token with a diagnostic of its own. The token is then excluded from any
    /// <c>DAXP1005</c> run: one source defect produces one diagnostic (A2 §8.1 rule 5).
    /// </summary>
    private void SkipCurrentWithDiagnostic(string code, TextSpan span)
    {
        Poll();
        _pendingSkipped.Add((GreenToken)_tokens[_index].WithAdditionalDiagnostics(Diagnostic(code, span)));
        Advance();
    }

    /// <summary>
    /// The A0 §11.5 stop sets PR-1 owns. The query rows belong to PR-2. A closing delimiter stops recovery
    /// only when one is open, so a stray ')' at the root is skipped rather than treated as a resume point.
    /// </summary>
    private enum Resync
    {
        Root,
        ArgumentOrConstructorList,
        Parenthesized,
        VarBlock,
        UdfParameters
    }

    private bool AtResyncPoint()
    {
        if (AtRealEof) return true;

        var kind = CurrentKind;
        return _resync switch
        {
            Resync.ArgumentOrConstructorList =>
                (kind == SyntaxKind.CommaToken && _listDepth > 0) ||
                (kind == SyntaxKind.CloseParenToken && _parenDepth > 0) ||
                (kind == SyntaxKind.CloseBraceToken && _braceDepth > 0),

            Resync.Parenthesized =>
                (kind == SyntaxKind.CloseParenToken && _parenDepth > 0) ||
                (kind == SyntaxKind.CommaToken && _listDepth > 0) ||
                kind == SyntaxKind.ReturnKeyword,

            Resync.VarBlock =>
                kind == SyntaxKind.VarKeyword ||
                kind == SyntaxKind.ReturnKeyword ||
                (kind == SyntaxKind.CloseParenToken && _parenDepth > 0) ||
                (kind == SyntaxKind.CloseBraceToken && _braceDepth > 0) ||
                (kind == SyntaxKind.CommaToken && _listDepth > 0),

            Resync.UdfParameters =>
                kind == SyntaxKind.CommaToken ||
                kind == SyntaxKind.CloseParenToken ||
                kind == SyntaxKind.LambdaArrowToken,

            _ => false
        };
    }

    /// <summary>
    /// Skips to the enclosing resynchronization point, emitting ONE diagnostic per contiguous run
    /// (A2 §8.4 rule 4). A stray <c>=&gt;</c> takes <c>DAXP1067</c> of its own and splits the run, because a
    /// lambda is a root-only construct and that is the honest thing to say about it (A2 §4.9).
    /// </summary>
    /// <param name="code">The run code: <c>DAXP1005</c> normally, <c>DAXP1006</c> for the trailing run.</param>
    /// <param name="stopAtPrimaryStart">
    /// Set by the required-primary recovery loop so that skipping stops as soon as something usable appears,
    /// rather than consuming the rest of the expression.
    /// </param>
    private void SkipUnexpectedTokens(string code, bool stopAtPrimaryStart = false)
    {
        var runStart = -1;

        while (!AtRealEof && !AtResyncPoint())
        {
            if (stopAtPrimaryStart && (CanStartPrimary(CurrentKind) || DaxSyntaxFacts.IsKeyword(CurrentKind))) break;

            if (CurrentKind == SyntaxKind.LambdaArrowToken)
            {
                EndRun(runStart, code);
                runStart = -1;
                SkipCurrentWithDiagnostic(DaxParserDiagnosticCodes.LambdaOnlyAtUdfRoot, CurrentRaw.Span);
                continue;
            }

            if (runStart < 0) runStart = _index;
            SkipCurrent();
        }

        EndRun(runStart, code);
    }

    private void EndRun(int runStart, string code)
    {
        if (runStart < 0 || runStart == _index) return;

        // A2 §8.1 rule 5: no parser echo. A run whose every token already owns a lexical diagnostic is one
        // source defect that has already been reported once (golden G-P-REC-003: SUM(#$%) is DAXL1001 x3
        // and DAXP1005 x0).
        if (code == DaxParserDiagnosticCodes.UnexpectedTokensSkipped && AllOwnLexicalDiagnostics(runStart, _index)) return;

        AddRunDiagnostic(code, runStart, _index);
    }

    private void AddRunDiagnostic(string code, int firstIndex, int endIndex)
        => _pendingSkippedDiagnostics.Add(Diagnostic(code, TextSpan.FromBounds(_raw[firstIndex].Span.Start, _raw[endIndex - 1].Span.End)));

    private bool AllOwnLexicalDiagnostics(int firstIndex, int endIndex)
    {
        for (var i = firstIndex; i < endIndex; i++)
        {
            var owned = _tokens[i].Diagnostics;
            if (owned is null) return false;

            var lexical = false;
            foreach (var diagnostic in owned)
                if (diagnostic.Code.StartsWith("DAXL", StringComparison.Ordinal)) { lexical = true; break; }
            if (!lexical) return false;
        }
        return true;
    }

    // ---- nesting depth (A2 §2.1) ----------------------------------------------------------------

    /// <summary>
    /// Opens one grammar production. On exceeding the limit it follows A0 §11.6 exactly: one
    /// <c>DAXP1007</c> on the token that would have opened the production, then consume to the enclosing
    /// resynchronization point as skipped-token trivia, then continue normally. The caller returns a
    /// <see cref="GreenMissingExpression"/> without a second diagnostic.
    /// </summary>
    private bool TryOpenProduction() => TryOpenProduction(callerOwnsDepthDiagnostic: false, out _);

    /// <summary>
    /// As <see cref="TryOpenProduction()"/>, but the <c>DAXP1007</c> is handed BACK instead of being left on
    /// the skipped token.
    ///
    /// <para>
    /// A caller that builds its recovery shell out of inserted MISSING TOKENS has to take it. Left on
    /// skipped-token trivia, the diagnostic flushes onto the leading trivia of the next accepted token,
    /// which is a token OUTSIDE the subtree it describes — so the shell held two inserted delimiters while
    /// <c>ContainsDiagnostics</c> was false and any later phase gating on diagnostics read the malformed
    /// call as valid (A2PR1-M4). The diagnostic is MOVED, never copied: same code, same severity, same span,
    /// exactly one owner, because one source defect produces one diagnostic (A2 §8.1 rule 5).
    /// </para>
    /// </summary>
    private bool TryOpenProduction(out DaxDiagnostic? depthDiagnostic)
        => TryOpenProduction(callerOwnsDepthDiagnostic: true, out depthDiagnostic);

    private bool TryOpenProduction(bool callerOwnsDepthDiagnostic, out DaxDiagnostic? depthDiagnostic)
    {
        depthDiagnostic = null;

        if (_depth < _options.MaximumNestingDepth)
        {
            _depth++;
            return true;
        }

        var span = CurrentRaw.Span;
        if (callerOwnsDepthDiagnostic)
        {
            depthDiagnostic = Diagnostic(DaxParserDiagnosticCodes.MaximumNestingDepthExceeded, span);
            SkipCurrent();
        }
        else
        {
            SkipCurrentWithDiagnostic(DaxParserDiagnosticCodes.MaximumNestingDepthExceeded, span);
        }

        while (!AtRealEof && !AtResyncPoint()) SkipCurrent();   // same defect, so no DAXP1005 on top
        _suppressExpressionExpected = true;
        return false;
    }

    private void CloseProduction() => _depth--;

    private GreenSyntaxList PollingList(IReadOnlyList<GreenNode?> children)
        => GreenFactory.List(children, NoteMaterialized);

    private GreenSyntaxList PollingSeparatedList(IReadOnlyList<GreenNode> items, IReadOnlyList<GreenToken> separators)
        => GreenFactory.SeparatedList(items, separators, NoteMaterialized);

    // ---- shared recovery pieces -----------------------------------------------------------------

    /// <summary>
    /// A2 §6.3 positions P1-P4: a reserved keyword is never a clean bare name. Exactly one
    /// <c>DAXP1043</c>, the keyword to skipped-token trivia, and the caller retries the primary, so
    /// <c>TOTAL[Amount]</c> leaves a bare <c>[Amount]</c> behind (golden G-P-KW-001).
    ///
    /// <para>
    /// <c>VAR</c>, <c>RETURN</c>, and <c>NOT</c> are excluded: they have grammatical roles here and a role
    /// always wins over a name reading (A2 §6.2 rule 2, §8.3 rows 1-2).
    /// </para>
    /// </summary>
    private bool TryRejectKeywordName()
    {
        var kind = CurrentKind;
        if (!DaxSyntaxFacts.IsKeyword(kind)) return false;
        if (kind is SyntaxKind.VarKeyword or SyntaxKind.ReturnKeyword or SyntaxKind.NotKeyword) return false;

        SkipCurrentWithDiagnostic(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, CurrentRaw.Span);
        _suppressExpressionExpected = true;
        return true;
    }

    private GreenSyntaxNode MissingExpression()
    {
        if (_suppressExpressionExpected)
        {
            _suppressExpressionExpected = false;
            return new GreenMissingExpression();
        }
        return new GreenMissingExpression(new[] { Diagnostic(DaxParserDiagnosticCodes.ExpressionExpected, InsertionSpan) });
    }
}
