using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A guard on the guards. This suite reads its OWN source and fails when a test cannot fail.
///
/// <para>
/// It exists because that has now happened three times in this project: twelve assertion-free tests once,
/// and eighteen more here (A2PR1-M5), where a <c>[Theory]</c> bound to 21 rows opened with
/// <c>if (rejected) return;</c> and so executed no assertion for 18 of them. xUnit reported 21 green cases.
/// CLAUDE.md rule 5 makes a test that cannot fail a non-test by definition, and the headline
/// "454 passed / 217 new" was the pull request's primary evidence, so the count itself was overstated.
/// Fixing eighteen cases fixes eighteen cases; this attacks the SHAPE those eighteen had.
/// </para>
/// <para>
/// It does NOT fix the class, and an earlier version of this remark said it did. The class is "a test that
/// executes no assertion", and only a runtime check could decide that. What these rules catch is a list of
/// syntactic shapes. The same defect in a modest hat still passes: <c>var skip = n == 1; if (skip) return;</c>
/// is caught only because the local's initialiser mentions a parameter, and <c>if (all.Count &lt; 2) return;</c>
/// is not caught at all, because the condition is an arbitrary expression. Twenty-nine <c>[Fact]</c> tests in
/// <c>Semanticus.Tests</c> open with <c>if (!OperatingSystem.IsWindows()) return;</c> and assert nothing on the
/// Linux leg; that is the same class and it is out of this rule's reach, tracked as its own row rather than
/// quietly implied to be covered.
/// </para>
/// <para>
/// HONEST ABOUT ITS LIMITS. This is a source scan, not a compiler. It reads the region from each test
/// attribute to the next one, which is a superset of the method body, so rule 2 (an assertion exists) can
/// only ever be too permissive, never too strict. It cannot tell a genuinely non-nullable expression from a
/// nullable one, so that half of rule 3 stays a named deny-list of the exact unfailable assertions review
/// has already found rather than a general nullability check.
/// </para>
/// <para>
/// Rule 3's OTHER half is general within its class: any two-argument <c>Assert.Equal</c> whose two argument
/// expressions are textually identical after whitespace normalisation is rejected, so
/// <c>Assert.Equal(Serialize(Parse(s)), Serialize(Parse(s)))</c> is caught as well as
/// <c>Assert.Equal(x, x)</c>. It is still textual: it cannot see two DIFFERENT expressions that happen to
/// be the same value, and an assertion helper that ends up comparing a thing to itself through a variable
/// is invisible to it. It catches the shapes that have actually shipped here; it is not a proof that every
/// remaining test can fail.
/// </para>
/// <para>
/// A DELIBERATE OVER-REACH, stated plainly because the rule cannot be read as narrower than it is:
/// <c>Assert.Equal(f(x), f(x))</c> is not unfailable in general. It fails when <c>f</c> is nondeterministic,
/// so written on purpose it is a determinism assertion, and this rule rejects those too. That is the
/// intended trade, and the remedy is never an exemption: compute the two values into two NAMED variables and
/// compare those, which is what <c>ParseHarness.AssertDeterministic</c> already does
/// (<c>Assert.Equal(Render(first.Root), Render(second.Root))</c> — two parses, two names, and it reads as the
/// determinism claim it is). A carve-out keyed on "both sides call the same function" would have let the one
/// real defect this rule was widened to catch straight through, because that defect has exactly that shape;
/// only the method NAME distinguished them, and a name is not something this scan can adjudicate.
/// </para>
/// <para>
/// SELF-EXCLUSION IS STRUCTURAL, not an allowlist. There is no file, method or line exclusion anywhere in
/// this scan, deliberately, and the scan reads its own source like any other file. What keeps it from
/// reporting itself is that its own patterns are not code the rules can see: doc comments are removed
/// (<see cref="StripDocComments"/>), string and character literals and ordinary comments are blanked
/// (<see cref="MaskLiteralsAndComments"/>), and the deny-list patterns are regex literals whose escaping
/// means they do not match the plain text they look for.
/// </para>
/// <para>
/// An earlier version of this remark claimed every pattern here was a regex literal. It was not: rendering
/// an offender back into a message needed the UNESCAPED name, so a plain occurrence sat inside a scanned
/// region and survived only because its two interpolation placeholders happened to be spelled differently,
/// at the exact point where their equality is the loop's own test. Renaming one would have made the rule
/// report itself. Masking now removes that whole class, and <see cref="AssertEqual"/> is spelled in two
/// pieces as well, so the property does not rest on a coincidence of naming.
/// </para>
/// <para>
/// It FAILS rather than skips when it cannot find the sources, because a gate that cannot run has not
/// passed.
/// </para>
/// </summary>
public class TestHygieneTests
{
    /// <summary>
    /// Rule 1, A2PR1-M5 itself. A theory row that returns because of one of its own parameters asserts
    /// nothing and still reports green. If a subset of rows needs different assertions, the DATA SOURCE is
    /// what should be filtered, so the case count tells the truth.
    ///
    /// <para>
    /// A parameterless <c>[Fact]</c> passes this trivially, which is why the rule does not need to tell a
    /// fact from a theory. That is a statement about this RULE'S REACH, not about the defect: an earlier
    /// version of this remark claimed only a parameterized test can have this shape, and that is false. A
    /// <c>[Fact]</c> that returns early on a LOCAL asserts just as little, and there are two live ones in
    /// <c>Semanticus.Tests</c> — <c>BaselineTests.Overflow_beyond_the_cap_is_reported_not_silent</c>
    /// (<c>if (all.Count &lt; 2) return;</c>) and
    /// <c>CertifiedBaselineTests.Identity_a_same_tag_clone_is_ambiguous_never_HELD</c>
    /// (<c>if (shared &lt; 2) return;</c>). Both files ARE scanned now, so the reason this rule still misses
    /// them is the CONDITION SHAPE and nothing else: it matches a bare identifier, and those are comparisons.
    /// An earlier version of this remark blamed "a local's value is not something a source scan can decide",
    /// which was a wrong explanation for a real observation, and it was wrong in the flattering direction,
    /// because at the time the whole project was simply out of scope.
    /// </para>
    /// <para>
    /// A guard on a local IS matched when the local's initialiser mentions a parameter, which is the
    /// <c>var skip = n == 1; if (skip) return;</c> form. A guard on an arbitrary expression is not, and that
    /// is the residual limit.
    /// </para>
    /// </summary>
    [Fact]
    public void No_test_returns_early_on_one_of_its_own_parameters()
    {
        var offenders = new List<string>();

        foreach (var (file, method) in TestMethods())
            foreach (var parameter in ParameterGuards(method))
                offenders.Add($"{file}: {method.Name} returns early on its parameter '{parameter}'");

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Rule 2: a test method with no assertion in it at all.
    ///
    /// <para>
    /// MASKED, like rule 3. Review planted two tests whose only assertion TEXT sat in a <c>//</c> comment and
    /// in a string literal, and all five rules stayed green: masking had reached two of this file's three call
    /// sites, so one idea had two implementations. An assertion that is only mentioned is not an assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_test_method_contains_at_least_one_assertion()
    {
        var offenders = TestMethods()
            .Where(x => !HasAssertion(x.Method.Body))
            .Select(x => $"{x.File}: {x.Method.Name} contains no assertion")
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Rule 3, A2PR1-N5. An assertion over an expression whose type is not nullable cannot fail; it reads as
    /// coverage and buys none. The list is exact on purpose, so adding one back is a deliberate act.
    ///
    /// <para>
    /// The deny-list alone was too narrow: its identity alternative back-references <c>\w+</c>, so it saw
    /// <c>Assert.Equal(x, x)</c> and never <c>Assert.Equal(f(x), f(x))</c> — which is the form that actually
    /// shipped, at <c>ParserFuzzTests.The_observer_has_zero_effect_on_output_when_unset</c>. So the identity
    /// case is now decided structurally by <see cref="SelfComparisons"/> for arguments of any shape.
    /// </para>
    /// </summary>
    [Fact]
    public void No_assertion_is_unfailable_by_construction()
    {
        var offenders = new List<string>();

        foreach (var (file, method) in TestMethods())
        {
            // Masked, for the same reason rule 3's other half is: a banned pattern written inside a string
            // literal is data. Without this, the positive controls below could not name the shapes they test.
            // Length-preserving, so the match index still slices the AUTHOR'S text back out for the message.
            var masked = MaskLiteralsAndComments(method.Body);
            foreach (var match in Unfailable.Matches(masked).Cast<Match>())
                offenders.Add(
                    $"{file}({method.Line(match.Index)}): {method.Name} has an unfailable assertion " +
                    $"`{method.Body.Substring(match.Index, match.Length)}`");

            foreach (var (call, index) in SelfComparisons(method.Body))
                offenders.Add(
                    $"{file}({method.Line(index)}): {method.Name} compares an expression to itself, " +
                    $"so the assertion cannot fail: `{call}`");

            // Silence is an offence. An unreadable call used to be skipped, which made a broken splitter
            // indistinguishable from a clean suite.
            foreach (var index in UnreadableAssertEquals(method.Body))
                offenders.Add(
                    $"{file}({method.Line(index)}): {method.Name} has an {AssertEqual} whose arguments this " +
                    "scan could not read, so rule 3 did not examine it. Fix the splitter or simplify the call; " +
                    "an unexamined assertion is not a passing one.");
        }

        // A bare Assert.Equal(x, x) is seen by BOTH halves and so reports twice, once from the deny-list and
        // once structurally. Two lines about one defect is noise, not a wrong verdict; the alternative is
        // suppressing a rule, which this scan does not do. Writing that example unescaped here is safe now
        // BECAUSE comments are masked; the earlier version of this comment escaped it and then explained the
        // escaping by saying comments are not stripped, which contradicted the masking three lines above.
        // Not Assert.Empty: xUnit truncates a collection display, and a gate whose whole value is naming the
        // file and line must print every offender in full.
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    /// <summary>
    /// Non-vacuity: this suite is worthless if it is not actually reading the tests.
    ///
    /// <para>
    /// THE FLOORS ARE LOOSE, DELIBERATELY, and the margin is stated so nobody later reads them as tight. They
    /// are set to catch a COLLAPSE, not a drift: against a measured 2419 methods in 178 files with 3122
    /// <c>Assert.Equal</c> calls, a fifty percent collapse fails with "Only 1293 test methods found", while the
    /// real headroom is only fifteen to twenty percent. So deleting a test project would be caught and deleting
    /// a few files would not. The per-file attribute-count check below is what covers the small end, because it
    /// compares every file against itself rather than against a constant.
    /// </para>
    /// </summary>
    [Fact]
    public void The_hygiene_scan_reads_the_whole_suite()
    {
        var methods = TestMethods().ToList();

        Assert.True(methods.Count > 2_000,
            $"Only {methods.Count} test methods found across all test projects; the scan is not reading the suite. " +
            "This floor is keyed to the MEASURED population (2419 methods in 178 files), not to one project.");
        Assert.True(methods.Count(m => m.Method.Parameters.Length > 0) > 20,
            $"Only {methods.Count(m => m.Method.Parameters.Length > 0)} parameterized tests found, so rule 1 " +
            "is scanning almost nothing.");
        var files = methods.Select(m => m.File).Distinct().Count();
        Assert.True(files >= 150,
            $"The scan is reading {files} test files. It read 19 of 178 for the whole of this rule's life, in a " +
            "test named for reading the whole suite, so this floor is deliberately close to the real total.");
        Assert.True(methods.Select(m => m.File.Split('/')[0]).Distinct().Count() >= 2,
            "Only one test project is being scanned, which is exactly the gap this floor exists to close.");

        // The scan must find a method for EVERY test attribute, or it is silently skipping tests.
        foreach (var path in SourceFiles())
        {
            var text = StripDocComments(File.ReadAllText(path));
            var attributes = TestAttributeToken.Matches(text).Count;
            var found = TestMethods().Count(m => m.File == ShortName(path));
            Assert.True(attributes == found,
                $"{ShortName(path)} has {attributes} test attributes but the scan found {found} methods. " +
                "The scan is silently skipping tests in that file, which is the one thing this rule exists " +
                "to make impossible.");
        }

        // Rule 3 specifically. Its regex can keep matching while the splitter behind it reads nothing, and
        // an empty result is otherwise indistinguishable from a clean suite, so both halves are counted.
        var (foundCalls, readCalls) = AssertEqualCoverage();
        Assert.True(foundCalls > 2_500,
            $"Only {foundCalls} {AssertEqual} calls seen across {methods.Count} methods in " +
            $"{methods.Select(m => m.File).Distinct().Count()} files; rule 3 is scanning almost nothing.");
        Assert.Equal(foundCalls, readCalls);
    }

    /// <summary>
    /// POSITIVE CONTROLS. Every rule above reports by finding nothing, and a rule that has stopped working
    /// also finds nothing, so each one is run here against input it MUST flag and input it must not.
    ///
    /// <para>
    /// Review's demonstration is the reason this exists: break <see cref="Arguments"/> so it always returns
    /// null and every rule stays green forever, which is the defect class this whole file exists to close,
    /// sitting inside the thing closing it.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_rule_flags_input_it_is_supposed_to_flag()
    {
        // ---- rule 1: an early return on the test's own parameter ----
        Assert.NotEmpty(ParameterGuards(Synthetic("if (rejected) return;", "rejected")));
        Assert.NotEmpty(ParameterGuards(Synthetic("if (!rejected) return;", "rejected")));
        Assert.Empty(ParameterGuards(Synthetic("if (somethingElse) return;", "rejected")));
        Assert.Empty(ParameterGuards(Synthetic("Assert.True(rejected);", "rejected")));
        // The same defect in a modest hat: the guard is on a local, but the local IS the parameter.
        Assert.NotEmpty(ParameterGuards(Synthetic("var skip = n == 1;\n if (skip) return;", "n")));
        Assert.Empty(ParameterGuards(Synthetic("var skip = other == 1;\n if (skip) return;", "n")));

        // ---- rule 2: an assertion exists ----
        // Rule 2 runs over MASKED text, so the control must too, or it tests a different rule than the one
        // that ships. Review planted a test whose only assertion text sat in a comment and another whose sat
        // in a string literal, and both passed, because masking had reached two of three call sites.
        Assert.False(HasAssertion("var x = 1; x++;"), "rule 2 saw an assertion in code that has none");
        Assert.True(HasAssertion("Assert.True(x);"), "rule 2 missed a plain assertion");
        Assert.True(HasAssertion("GoldenUdf(source, tree);"), "rule 2 missed a Golden* helper, which the convention allows");
        Assert.False(HasAssertion("var x = 1;   // Assert.True(x) would go here"),
            "rule 2 counted an assertion mentioned in a // comment");
        Assert.False(HasAssertion("var note = \"Assert.Equal(a, b)\";"),
            "rule 2 counted an assertion sitting inside a string literal");
        Assert.False(HasAssertion("/* Assert.True(x); */ var x = 1;"),
            "rule 2 counted an assertion inside a block comment");

        // WIDTH. Nothing above constrains how much the recogniser may match, because every negative is
        // parenthesis-free: appending `|\w+\(` to Asserts makes rule 2 vacuous across all 2419 methods and
        // every one of these still passes. These are ordinary calls that are NOT assertions, so a recogniser
        // wide enough to accept them is a recogniser that has stopped deciding anything.
        Assert.False(HasAssertion("var parsed = Parse(source);"), "rule 2's recogniser matches any call, so it decides nothing");
        Assert.False(HasAssertion("var tree = DaxSyntaxTree.Parse(text, options);"), "rule 2's recogniser matches any member call");
        Assert.False(HasAssertion("Setup(); Teardown();"), "rule 2's recogniser matches bare calls");
        Assert.False(HasAssertion("using var cts = new CancellationTokenSource();"), "rule 2's recogniser matches a constructor");

        // ---- rule 3's DENY-LIST half, which is a separate decision from the structural half ----
        // Review neutered both of its alternatives in place and all five rules stayed green, because it was a
        // method local no control could reach.
        Assert.Matches(Unfailable, "Assert.True(true)");   // the deny-list stopped seeing Assert.True(true)
        Assert.Matches(Unfailable, "Assert.NotNull(tree.Root)");   // the deny-list stopped seeing Assert.NotNull on a non-nullable root
        Assert.Matches(Unfailable, "Assert.NotNull(tree.GetExpressionRoot())");   // the deny-list stopped seeing Assert.NotNull on GetExpressionRoot()
        Assert.Matches(Unfailable, "Assert.Equal(x, x)");   // the deny-list stopped seeing the bare identity form
        Assert.DoesNotMatch(Unfailable, "Assert.True(condition)");   // the deny-list now rejects a legitimate Assert.True
        Assert.DoesNotMatch(Unfailable, "Assert.NotNull(candidate)");   // the deny-list now rejects a legitimate Assert.NotNull
        Assert.DoesNotMatch(Unfailable, "Assert.Equal(x, y)");   // the deny-list now rejects a legitimate comparison

        // ---- rule 3: the shapes that must be caught ----
        foreach (var offender in new[]
        {
            "Assert.Equal(x, x)",
            "Assert.Equal(Serialize(Parse(s)), Serialize(Parse(s)))",
            "Assert.Equal(f(a, b), f(a, b))",                                  // commas inside arguments
            "Assert.Equal(Make<string, int>(), Make<string, int>())",           // commas inside type arguments
            "Assert.Equal(f(x), f(x), comparer)",                              // three-argument overload
            "Assert.Equal(f(x), f(x) /* same on purpose */)",                   // trailing block comment
            "Assert.Equal(f(x),\n            f(x))",                           // split across lines
            "Assert.Equal(new[] { 1, 2 }, new[] { 1, 2 })",                     // collection expressions
            "Assert.Equal<string>(f(x), f(x))",                                 // explicit type argument
            "Assert.Equal<int, string>(g(y), g(y))",                            // several type arguments
            "Assert.Equal <string> (f(x), f(x))",                               // and spaced out
        })
            Assert.True(SelfComparisons(offender).Any(), $"rule 3 no longer flags a self-comparison written as: {offender}");

        // ---- rule 3: the shapes that must NOT be caught ----
        foreach (var clean in new[]
        {
            "Assert.Equal(a, b)",
            "Assert.Equal(f(x), f(y))",
            "Assert.Equal(Render(first.Root), Render(second.Root))",            // the sanctioned determinism form
            "Assert.Equal(Make<string, int>(), Make<int, string>())",
            "Assert.Equal(expected, actual, comparer)",
            "Assert.Equal(\"f(x), f(x)\", actual)",                             // it is a string, not two arguments
            "Assert.Equal<string>(f(x), f(y))",                                 // explicit type argument, real comparison
        })
            Assert.False(SelfComparisons(clean).Any(), $"rule 3 falsely flags a legitimate assertion written as: {clean}");

        // ---- rule 3: every one of those was actually READ, not skipped as unparseable ----
        foreach (var call in new[] { "Assert.Equal(x, x)", "Assert.Equal(Make<string, int>(), Make<string, int>())" })
            Assert.False(UnreadableAssertEquals(call).Any(), $"rule 3 reported a readable call as unreadable: {call}");

        // ---- and an unreadable call IS reported, rather than passing quietly ----
        Assert.True(UnreadableAssertEquals("Assert.Equal(x, x").Any(),
            "an unbalanced call is no longer reported as unreadable, so rule 3 is silently skipping calls again");

        // ---- rule 3 at the REAL SIZES, not just as a one-line fixture ----------------------------
        //
        // Every fixture above is a short synthetic string while the population is 58 to 16,372 characters,
        // median 802. Review exploited exactly that: make Arguments return a plausible-but-WRONG pair for
        // bodies over 400 characters and rule 3 detected nothing across the whole suite while all five tests
        // stayed green, because no control ever handed it a long body. A control whose fixtures do not
        // resemble the population it guards is not a control.
        var realBodies = TestMethods().Select(m => m.Method.Body.Length).OrderBy(n => n).ToList();
        var p90 = realBodies[(int)(realBodies.Count * 0.9)];
        var longest = 0;

        foreach (var size in new[] { 120, 800, 2_000, 4_000, 16_500 })
            foreach (var atEnd in new[] { false, true })
            {
                var body = Padded("Assert.Equal(Serialize(Parse(s)), Serialize(Parse(s)))", size, atEnd);
                longest = Math.Max(longest, body.Length);
                var where = atEnd ? "at the end of" : "at the start of";
                Assert.True(SelfComparisons(body).Any(),
                    $"rule 3 missed a self-comparison {where} a {body.Length}-character body");
                Assert.False(UnreadableAssertEquals(body).Any(),
                    $"rule 3 could not read a call {where} a {body.Length}-character body");
                Assert.False(SelfComparisons(Padded("Assert.Equal(f(x), f(y))", size, atEnd)).Any(),
                    $"rule 3 falsely flagged a legitimate comparison {where} a {body.Length}-character body");
            }

        Assert.True(longest >= p90,
            $"The longest rule 3 fixture is {longest} characters but the 90th percentile real test body is " +
            $"{p90}. Extend the fixture sizes: a control that never sees the population's real sizes cannot " +
            "catch a defect that only appears at them.");
    }

    /// <summary>
    /// <paramref name="offender"/> embedded in filler that reads like a test body, padded to about
    /// <paramref name="length"/> characters, with the offender at the end or the beginning. Both placements,
    /// because a length-sensitive defect in the splitter can bite on one and not the other.
    /// </summary>
    private static string Padded(string offender, int length, bool atEnd)
    {
        var filler = new System.Text.StringBuilder();
        for (var i = 0; filler.Length < length; i++)
            filler.Append($"        var value{i} = Parse(\"SUM('T'[c{i}]) + {i}\");\n")
                  .Append($"        Assert.Equal({i}, value{i}.Root.Span.Start + {i});\n");

        return atEnd ? $"{{\n{filler}        {offender};\n}}" : $"{{\n        {offender};\n{filler}}}";
    }

    /// <summary>A method-shaped fixture, so a rule can be run against input that is not the real suite.</summary>
    private static TestMethod Synthetic(string body, params string[] parameters)
        => new("Synthetic", parameters, body, 0, body);

    // ---- the scan -----------------------------------------------------------------------------------

    /// <summary>
    /// Any call to something named <c>Assert*</c>, <c>Expect*</c> or <c>Golden*</c>, so that delegating to a
    /// local assertion helper counts. That is a convention this scan now enforces as well as relies on.
    /// </summary>
    private static readonly Regex Asserts = new(@"\bAssert\.|\bAssert\w*\(|\bExpect\w*\(|\bGolden\w*\(");

    private static readonly Regex TestAttributeToken = new(@"\[(?:Fact|Theory)\b");

    /// <summary>
    /// The head of an <c>Assert.Equal</c> call. Written as a regex literal so that this file's own copy of
    /// the pattern is not itself a match: the scan reads its own source like every other file.
    ///
    /// <para>
    /// An EXPLICIT TYPE ARGUMENT is part of the head. <c>Assert.Equal&lt;T&gt;(x, x)</c> matched neither this
    /// pattern nor the deny-list, so it was neither examined NOR counted as unreadable: a hole in the very
    /// population count rule 4 exists to make honest. No live instances today, which is why review filed it as
    /// a should rather than a must, and why it is cheaper to close now than to discover later.
    /// </para>
    /// </summary>
    private static readonly Regex AssertEqualHead = new(@"\bAssert\.Equal\s*(?:<[^>(\n]*>)?\s*\(");

    private static readonly Regex Whitespace = new(@"\s+");

    /// <summary>
    /// Rule 3's named deny-list. A STATIC FIELD, not a method local, because review neutered both of its
    /// alternatives in place and all five rules stayed green: rule 5 could not reach it, so "rule 5 drives
    /// every rule" was false as written. Everything a rule decides with now lives where a control can drive it.
    /// </summary>
    private static readonly Regex Unfailable = new(
        @"Assert\.NotNull\(\s*(?:\w+\.)?(?:tree\.Root|Root|GetExpressionRoot\(\)|GetUdfBodyRoot\(\)|\w+\.GetExpressionRoot\(\)|\w+\.GetUdfBodyRoot\(\))\s*\)" +
        @"|Assert\.True\(\s*true\s*\)" +
        @"|Assert\.Equal\(\s*(\w+)\s*,\s*\1\s*\)");

    /// <summary>
    /// Spelled in two pieces so that this file does not contain the plain text its own scan looks for. The
    /// class remark calls self-exclusion "by escaping"; rendering an offender back into a message needs the
    /// unescaped name, and review found that the rendered form was a live plain occurrence sitting inside a
    /// scanned region, surviving only because its two placeholders happened to have different names.
    /// </summary>
    private const string AssertEqual = "Assert" + ".Equal";

    /// <summary>
    /// Rule 2's core, as a named function for the same reason rule 1's and the deny-list are: a control that
    /// re-implements what the rule does is not driving the rule. Review dropped the mask from rule 2's call
    /// site and nothing failed, because the control had masked its own fixture before checking it.
    /// </summary>
    private static bool HasAssertion(string body) => Asserts.IsMatch(MaskLiteralsAndComments(body));

    /// <summary>
    /// Rule 1's core, callable on a synthetic method so the rule can have a positive control.
    ///
    /// <para>
    /// Two shapes, because the direct one alone understated the limit: review pointed out that
    /// <c>var skip = n == 1; if (skip) return;</c> is the same defect verbatim and passed. So a bare-identifier
    /// guard also counts when that identifier is a LOCAL whose initialiser mentions a parameter. What is still
    /// out of reach is a guard on an arbitrary expression (<c>if (all.Count &lt; 2) return;</c>) — see the
    /// remark on the rule itself.
    /// </para>
    /// </summary>
    private static IEnumerable<string> ParameterGuards(TestMethod method)
    {
        var body = MaskLiteralsAndComments(method.Body);

        foreach (var parameter in method.Parameters)
        {
            if (Guards(parameter)) { yield return parameter; continue; }

            // A local standing in for the parameter is the same defect one name removed.
            foreach (var local in LocalsDerivedFrom(parameter))
                if (Guards(local))
                {
                    yield return $"{parameter}' (through the local '{local}')";
                    break;
                }
        }

        bool Guards(string name)
            => Regex.IsMatch(body, $@"if\s*\(\s*!?\s*{Regex.Escape(name)}\s*\)\s*return\s*;");

        IEnumerable<string> LocalsDerivedFrom(string parameter)
        {
            foreach (var declaration in Regex.Matches(body, @"\b(?:var|bool)\s+(\w+)\s*=\s*([^;]{1,200});").Cast<Match>())
                if (Regex.IsMatch(declaration.Groups[2].Value, $@"\b{Regex.Escape(parameter)}\b"))
                    yield return declaration.Groups[1].Value;
        }
    }

    /// <summary>
    /// Every <c>Assert.Equal</c> in <paramref name="body"/> whose FIRST TWO arguments are the same expression.
    /// An assertion that compares an expression to itself cannot fail, whatever the expression is.
    ///
    /// <para>
    /// First two of N, not exactly two, because xUnit's three-argument overloads take a comparer or a
    /// precision after the pair being compared, and <c>Assert.Equal(a, a, comparer)</c> is no more failable
    /// than <c>Assert.Equal(a, a)</c>.
    /// </para>
    /// <para>
    /// TWO INTERPRETATIONS are tried, and a hit under either one counts. The plain reading does not treat
    /// <c>&lt;</c> and <c>&gt;</c> as nesting, so <c>Assert.Equal(Make&lt;string, int&gt;(), Make&lt;string,
    /// int&gt;())</c> reads as four arguments and is missed; the generic-aware reading recovers it. Because a
    /// hit under either counts, a wrong guess about a <c>&lt;</c> can only ever ADD a detection, never hide
    /// one, which is the safe direction for a heuristic to be wrong in.
    /// </para>
    /// </summary>
    private static IEnumerable<(string Call, int Index)> SelfComparisons(string body)
        => Analyse(body).Where(c => c.IsSelfComparison).Select(c => ($"{AssertEqual}({c.Left}, {c.Left})", c.Index));

    /// <summary>
    /// ONE PASS over every <c>Assert.Equal</c> in <paramref name="body"/>, producing what the rule actually
    /// decided about each: the argument pair it compared, or that it could not read one.
    ///
    /// <para>
    /// Single pass on purpose. The counter used to be <c>found - unreadable</c>, computed in a second walk, so
    /// it agreed with itself no matter what: review made <see cref="Arguments"/> return a plausible-but-WRONG
    /// pair for bodies over 400 characters and rule 3 detected nothing on the real suite while the counter
    /// still reported full coverage, because a wrong pair is a readable one. What a rule examined can only be
    /// measured by the pass that examined it, never derived beside it.
    /// </para>
    /// </summary>
    private static List<AssertEqualCall> Analyse(string body)
    {
        var calls = new List<AssertEqualCall>();

        foreach (var head in AssertEqualHeads(body))
        {
            var openParen = head.Index + head.Length - 1;
            string? left = null;
            string? right = null;

            foreach (var genericAware in new[] { false, true })
            {
                var arguments = Arguments(body, openParen, genericAware);
                if (arguments is null || arguments.Count < 2) continue;

                left ??= Normalize(arguments[0]);
                right ??= Normalize(arguments[1]);

                // A hit under EITHER reading counts, so settle on the pair that agrees.
                var candidateLeft = Normalize(arguments[0]);
                if (candidateLeft.Length > 0 && candidateLeft == Normalize(arguments[1]))
                {
                    left = candidateLeft;
                    right = candidateLeft;
                    break;
                }
            }

            calls.Add(new AssertEqualCall(head.Index, left, right));
        }

        return calls;

        static string Normalize(string argument) => Whitespace.Replace(argument, " ").Trim();
    }

    /// <summary>
    /// One <c>Assert.Equal</c> and rule 3's verdict on it. <see cref="Left"/> null means the arguments could
    /// not be read, which is an offence rather than a skip.
    /// </summary>
    private sealed record AssertEqualCall(int Index, string? Left, string? Right)
    {
        public bool WasRead => Left is not null;

        public bool IsSelfComparison => Left is { Length: > 0 } && Left == Right;
    }

    /// <summary>
    /// Every <c>Assert.Equal</c> whose arguments could not be read under EITHER interpretation.
    ///
    /// <para>
    /// This exists because review found the real hole: an unreadable call was <c>continue</c>d over, so a
    /// splitter that returned null for everything was indistinguishable from a clean suite and all four rules
    /// stayed green forever. A rule that cannot see is not a rule that passes, so silence is now an offence.
    /// </para>
    /// </summary>
    private static IEnumerable<int> UnreadableAssertEquals(string body)
        => Analyse(body).Where(c => !c.WasRead).Select(c => c.Index);

    /// <summary>
    /// How many <c>Assert.Equal</c> calls rule 3 found and how many it actually COMPARED, both taken from the
    /// one pass that did the comparing, so the pair cannot agree with itself while the rule sees nothing.
    /// </summary>
    private static (int Found, int Read) AssertEqualCoverage()
    {
        var found = 0;
        var read = 0;
        foreach (var (_, method) in TestMethods())
        {
            var calls = Analyse(method.Body);
            found += calls.Count;
            read += calls.Count(c => c.WasRead);
        }
        return (found, read);
    }

    /// <summary>
    /// The top-level, comma-separated arguments of the call whose '(' is at <paramref name="openParen"/>.
    ///
    /// <para>
    /// Depth-aware on purpose. Splitting on the first comma mis-reads <c>Assert.Equal(f(a, b), f(a, b))</c>
    /// as four arguments and so misses exactly the shape rule 3 has to see. Commas and brackets inside nested
    /// calls, collection expressions, string literals (plain, verbatim and raw) and character literals do not
    /// split. Returns null when the call does not close, since an unbalanced read is not evidence of anything.
    /// </para>
    /// <para>
    /// COMMENTS ARE DROPPED rather than kept, because they are not part of the expression: review found that
    /// <c>Assert.Equal(f(x), f(x) /* why */)</c> escaped the rule purely because the trailing comment made the
    /// two argument texts differ.
    /// </para>
    /// <para>
    /// <paramref name="genericAware"/> additionally treats <c>&lt;...&gt;</c> as nesting, so a type argument
    /// list stops splitting the call. It is a heuristic: <c>&lt;</c> opens only when the previous character
    /// could end a type name, which distinguishes <c>Make&lt;string, int&gt;</c> from <c>a &lt; b</c> in every
    /// real case here but not in principle. Callers try BOTH readings and accept a hit from either, so a wrong
    /// guess costs a possible false positive and can never hide a real one.
    /// </para>
    /// </summary>
    private static List<string>? Arguments(string text, int openParen, bool genericAware)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var generic = 0;

        for (var i = openParen + 1; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                var end = SkipComment(text, i);
                if (end < 0) return null;
                current.Append(' ');   // a comment separates tokens, it does not join them
                i = end;
                continue;
            }

            if (c == '"' || c == '\'' || (c == '@' && i + 1 < text.Length && text[i + 1] == '"'))
            {
                var end = SkipLiteral(text, i);
                if (end < 0) return null;
                current.Append(text, i, end - i + 1);
                i = end;
                continue;
            }

            if (genericAware && c == '<' && i > 0 && CanEndTypeName(text[i - 1])) generic++;
            else if (genericAware && c == '>' && generic > 0) generic--;

            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
                case ')' when depth == 0:
                    arguments.Add(current.ToString());
                    return arguments;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0 && generic == 0:
                    arguments.Add(current.ToString());
                    current.Clear();
                    continue;
            }

            current.Append(c);
        }

        return null;
    }

    /// <summary>
    /// Every <c>Assert.Equal</c> head that is real CODE, found over a copy with string and character literals
    /// and comments blanked out.
    ///
    /// <para>
    /// A call written inside a string literal is data, not an assertion. Without this, the positive controls
    /// below could not name the shapes they test, because naming a bad assertion in a string would BE one as
    /// far as the scan could tell. Blanking is length-preserving, so reported line numbers stay true.
    /// </para>
    /// <para>
    /// Only head DISCOVERY uses the masked copy. Arguments are then read from the real text, which already
    /// handles literals, so an offender is rendered in the words the author actually wrote.
    /// </para>
    /// </summary>
    private static IEnumerable<Match> AssertEqualHeads(string body)
        => AssertEqualHead.Matches(MaskLiteralsAndComments(body)).Cast<Match>();

    /// <summary>Replaces the interior of every literal and comment with spaces, preserving length exactly.</summary>
    private static string MaskLiteralsAndComments(string text)
    {
        var masked = text.ToCharArray();

        for (var i = 0; i < text.Length; i++)
        {
            int end;
            if (text[i] == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
                end = SkipComment(text, i);
            else if (text[i] == '"' || text[i] == '\'' || (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '"'))
                end = SkipLiteral(text, i);
            else
                continue;

            if (end < 0) end = text.Length - 1;   // unterminated: mask to the end rather than trust the rest
            for (var j = i; j <= end && j < masked.Length; j++)
                if (masked[j] != '\n' && masked[j] != '\r') masked[j] = ' ';
            i = end;
        }

        return new string(masked);
    }

    /// <summary>Whether <paramref name="c"/> can be the last character of a type name, so a following '&lt;' opens type arguments.</summary>
    private static bool CanEndTypeName(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '>' || c == '?';

    /// <summary>The index of the last character of the comment starting at <paramref name="i"/>, or -1 if a block comment never closes.</summary>
    private static int SkipComment(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            var newline = text.IndexOf('\n', i);
            return newline < 0 ? text.Length - 1 : newline - 1;
        }

        var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? -1 : close + 1;
    }

    /// <summary>
    /// The index of the last character of the C# literal starting at <paramref name="i"/>, or -1 if it never
    /// closes. Covers <c>'c'</c>, <c>"s"</c>, <c>@"s"</c> and raw <c>"""s"""</c> — the goldens are raw string
    /// literals full of brackets and commas, so getting this wrong would corrupt every split downstream.
    /// </summary>
    private static int SkipLiteral(string text, int i)
    {
        if (text[i] == '\'')
        {
            for (var j = i + 1; j < text.Length; j++)
            {
                if (text[j] == '\\') { j++; continue; }
                if (text[j] == '\'') return j;
            }
            return -1;
        }

        if (text[i] == '@')
        {
            for (var j = i + 2; j < text.Length; j++)
            {
                if (text[j] != '"') continue;
                if (j + 1 < text.Length && text[j + 1] == '"') { j++; continue; }   // "" is an escaped quote
                return j;
            }
            return -1;
        }

        var quotes = 0;
        while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;

        if (quotes >= 3)
        {
            // A raw literal ends at a run of AT LEAST as many quotes as it opened with.
            for (var j = i + quotes; j < text.Length; j++)
            {
                if (text[j] != '"') continue;
                var run = 0;
                while (j + run < text.Length && text[j + run] == '"') run++;
                if (run >= quotes) return j + run - 1;
                j += run - 1;
            }
            return -1;
        }

        if (quotes == 2) return i + 1;   // the empty string

        for (var j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\') { j++; continue; }
            if (text[j] == '"') return j;
        }
        return -1;
    }

    /// <summary>
    /// A test method signature. Anchored to the START OF A LINE, which is what lets the scan skip over
    /// <c>[InlineData(...)]</c> rows without parsing them: an attribute line begins with '[', a signature
    /// line begins with an access modifier. Parsing the attribute rows with a regex is what broke the first
    /// version of this, because <c>[InlineData("[x] IN {1}")]</c> contains both a bracket and braces.
    ///
    /// <para>
    /// An attribute list may precede the modifier ON THE SAME LINE, because
    /// <c>[Fact] public void Flags_iferror() =&gt; ...</c> is a real and common style here. Widening the scan
    /// from 19 files to 178 found <c>Semanticus.Tests/DaxLintTests.cs</c> writing all 64 of its tests that way,
    /// so the scan saw 64 attributes and zero methods and skipped the file entirely. An <c>[InlineData]</c> row
    /// still cannot match, because after its brackets there is no access modifier.
    /// </para>
    /// </summary>
    private static readonly Regex Signature = new(
        @"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:public|private|internal|protected)[^\n]*?\b(?<name>\w+)\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Multiline);

    private static IEnumerable<(string File, TestMethod Method)> TestMethods()
    {
        foreach (var path in SourceFiles())
        {
            // Doc comments go first, so prose can neither satisfy a rule about code nor be mistaken for an
            // attribute.
            var text = StripDocComments(File.ReadAllText(path));
            var name = ShortName(path);
            var attributes = TestAttributeToken.Matches(text);

            for (var i = 0; i < attributes.Count; i++)
            {
                // From the START OF THE ATTRIBUTE'S LINE, not from the attribute. Signature is line-anchored,
                // and `^` cannot match before the start position, so searching from the attribute itself skips
                // past a same-line signature and silently binds this attribute to the NEXT method's signature.
                var lineStart = text.LastIndexOf('\n', attributes[i].Index) + 1;
                var signature = Signature.Match(text, lineStart);
                if (!signature.Success) continue;

                var start = signature.Index + signature.Length;
                var end = i + 1 < attributes.Count ? attributes[i + 1].Index : text.Length;
                if (end < start) end = start;

                yield return (name, new TestMethod(
                    signature.Groups["name"].Value,
                    ParameterNames(signature.Groups["parameters"].Value),
                    text[start..end],
                    start,
                    text));
            }
        }
    }

    /// <summary>
    /// Doc-comment lines are BLANKED, not deleted, so that an offender's line number in the scanned text is
    /// still its line number in the file on disk. Deleting them shifted every line below a doc comment.
    /// </summary>
    private static string StripDocComments(string text)
        => string.Join('\n', text.Split('\n')
            .Select(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal) ? string.Empty : line));

    private static string[] ParameterNames(string parameters)
        => parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty)
            .Where(p => p.Length > 0)
            .ToArray();

    /// <summary>
    /// EVERY test project in the repository, recursively.
    ///
    /// <para>
    /// It used to be this project's top directory only: 19 files of 178, while the test asserting it was named
    /// <c>..._reads_the_whole_suite</c>. <c>Semanticus.Tests</c>, the larger suite by an order of magnitude, was
    /// never opened, so the guard covered a ninth of what its own name claimed.
    /// </para>
    /// <para>
    /// Discovered from this file's own path upwards, so it does not depend on the working directory, and it
    /// FAILS rather than skips when it cannot find the projects. A gate that cannot run has not passed, and a
    /// gate that quietly reads less than it says is worse.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SourceFiles()
    {
        var directory = Path.GetDirectoryName(ThisFile())
            ?? throw new InvalidOperationException($"Cannot read a directory from '{ThisFile()}'.");

        var root = Path.GetDirectoryName(directory)
            ?? throw new InvalidOperationException($"Cannot read a repository root from '{directory}'.");

        if (!Directory.Exists(root))
            throw new InvalidOperationException(
                $"The repository is not at '{root}', so the hygiene scan cannot run. " +
                "A gate that cannot run has not passed. Run the suite from the repository checkout.");

        var projects = Directory.GetDirectories(root, "*.Tests", SearchOption.TopDirectoryOnly);
        if (projects.Length == 0)
            throw new InvalidOperationException($"No test projects found under '{root}'.");

        var files = projects
            .SelectMany(p => Directory.GetFiles(p, "*Tests.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
            throw new InvalidOperationException($"No test sources found under '{root}'.");

        return files;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// Project plus file name, e.g. <c>Semanticus.Tests/BaselineTests.cs</c>. A bare file name stopped being
    /// unique the moment the scan covered more than one project, and an offender the reader cannot locate is
    /// most of the way to no offender at all.
    /// </summary>
    private static string ShortName(string path)
    {
        var file = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        while (directory is not null && !Path.GetFileName(directory).EndsWith(".Tests", StringComparison.Ordinal))
            directory = Path.GetDirectoryName(directory);
        return directory is null ? file : $"{Path.GetFileName(directory)}/{file}";
    }

    private sealed record TestMethod(string Name, string[] Parameters, string Body, int BodyStart, string FileText)
    {
        /// <summary>The 1-based line in the FILE of an offset inside <see cref="Body"/>.</summary>
        public int Line(int offsetInBody)
            => FileText.AsSpan(0, BodyStart + offsetInBody).Count('\n') + 1;
    }
}
