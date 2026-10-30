using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// Verifier v-rc23b, step 2 (S1). The start plan's yes also gives the model's own version 7 permission: the card's
// agreeStartPlan calls setConsent with every field in force, and grant_start_disclosure calls SetConsentAsync with
// DecisionRegistry.DeclaredFields. That list includes the Totals Fit probe rows (a member name and a measure value per
// row). Both doors still say that rows or values never go. These tests fail until the two texts are true.
public sealed class VerifierRc23bDisclosureTests
{
    private static string RepoFile(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Semanticus.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, path)).Replace("\r\n", "\n");
    }

    [Fact]
    public void The_start_plan_card_does_not_promise_that_values_never_go_while_its_yes_grants_the_totals_probe_rows()
    {
        Assert.Contains(DecisionRegistry.DeclaredFields, f => f == DecisionRegistry.TotalsFit + ":probe");
        var card = RepoFile("Semanticus.VSCode/webview/src/consent.tsx");
        var modelCard = Regex.Match(card, @"const CARD = \{([\s\S]*?)\n\};").Groups[1].Value;
        var startCard = Regex.Match(card, @"export const START_CARD = \{([\s\S]*?)\n\};").Groups[1].Value;
        Assert.Contains("Each probe row contains a member name and a measure value.", modelCard);
        // The start card lists CARD.goes under "this also lets Pro decide for it", then says what never goes.
        Assert.Contains("CARD.goes.map", card[card.IndexOf("consent-start-model", StringComparison.Ordinal)..]);
        var never = Regex.Match(startCard, @"never: \[([^\]]*)\]").Groups[1].Value;
        Assert.DoesNotContain("rows or values from any table", never);
    }

    // What "Not scored yet" says when the model's permission is missing or out of date: Pro advice is off, which Turn on fixes.
    // It used to read the calm "turns on when Jev scoring is released" line, which told a licensed person the feature was not out (fx29 gap 1).
    [Theory]
    [InlineData("you have not given permission to send this model's details for advice")]
    [InlineData("the list of details that would be sent has changed since you agreed, so permission is needed again")]
    public void Evidence_permission_reasons_read_as_not_available(string reason)
        => Assert.Equal(ScoringCopy.AdviceOff, ScoringCopy.Reason(Semanticus.Engine.Decisions.DecisionOutcome.Skipped(reason)));

    [Fact]
    public void Grant_start_disclosure_names_the_formula_and_probe_rows_its_yes_also_grants()
    {
        var method = typeof(McpTools).GetMethod(nameof(McpTools.GrantStartDisclosureOp), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Assert.Contains("turns Jev on for this model", description);
        Assert.Contains(DecisionRegistry.DeclaredFields, f => f == DecisionRegistry.TotalsFit + ":probe");
        Assert.Contains("formula", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("probe rows", description, StringComparison.OrdinalIgnoreCase);
    }
}
