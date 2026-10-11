using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The "Best practice" letter (Kane, 28 Sep: "AI readiness and BPA is best practice at A grade"). A means no open
    /// Error and no open Warning, Info allowed, waivers count as passed. Below A the letter is cut on severity-weighted
    /// open findings per object scanned, so a big model is not punished for its size. These pin every boundary.
    /// </summary>
    public sealed class BpaGradeTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        [Theory]
        [InlineData(0, 0, 0, 10, 0, "A")]
        [InlineData(0, 0, 500, 10, 0, "A")]    // info never stops an A, however many
        [InlineData(0, 1, 0, 1000, 0, "B")]    // one warning, however big the model, is not A
        [InlineData(1, 0, 0, 1000, 0, "B")]
        [InlineData(0, 0, 0, 10, 1, "B")]      // a blocking rule that could not run stops an A
        [InlineData(0, 0, 500, 10, 1, "B")]    // below A, info notes weigh nothing, however many
        [InlineData(0, 5, 0, 100, 0, "B")]     // 10 / 100 = 0.10, the top of B
        [InlineData(0, 5, 900, 100, 0, "B")]   // the same with 900 notes: still the top of B
        [InlineData(0, 5, 0, 99, 0, "C")]      // 10 / 99 = 0.101, just past B
        [InlineData(5, 0, 0, 100, 0, "C")]     // 25 / 100 = 0.25, the top of C
        [InlineData(5, 0, 0, 99, 0, "D")]      // 25 / 99, just past C
        [InlineData(10, 0, 0, 100, 0, "D")]    // 50 / 100 = 0.50, the top of D
        [InlineData(10, 0, 0, 99, 0, "F")]     // 50 / 99, just past D
        [InlineData(43, 78, 95, 150, 0, "F")]
        public void Letter_boundaries(int e, int w, int i, int objects, int unknowns, string letter) =>
            Assert.Equal(letter, BpaGrading.Compute(e, w, i, objects, unknowns).Letter);

        [Fact]
        public void Size_does_not_punish_a_big_model()
        {
            // The same density on a model ten times bigger has the same letter.
            Assert.Equal(BpaGrading.Compute(2, 3, 4, 100, 0).Letter, BpaGrading.Compute(20, 30, 40, 1000, 0).Letter);
        }

        // The 0 to 100 score behind the bars (Kane, K20): the same bands as AI readiness (80 and up good, 60 to 79 warn, under 60 bad),
        // folded from the letter (A and B good, C and D warn, F bad), so a bar and its letter can never disagree. 100 only when clean.
        [Fact]
        public void The_score_sits_in_the_band_of_the_letter()
        {
            static string Band(int score) => score >= 80 ? "good" : score >= 60 ? "warn" : "bad";
            static string LetterBand(string l) => l is "A" or "B" ? "good" : l is "C" or "D" ? "warn" : "bad";
            for (var objects = 1; objects <= 300; objects++)
                for (var errors = 0; errors <= 6; errors++)
                    for (var warnings = 0; warnings <= 40; warnings++)
                    {
                        var score = BpaGrading.Score(errors, warnings, objects);
                        Assert.InRange(score, 0, 100);
                        Assert.Equal(errors + warnings == 0, score == 100);
                        Assert.Equal(LetterBand(BpaGrading.Compute(errors, warnings, 0, objects, 0).Letter), Band(score));
                    }
        }

        [Fact]
        public async Task A_full_scan_carries_a_score_for_each_category()
        {
            var engine = await ModelAsync();
            var card = await engine.BpaScanAsync();
            Assert.NotEmpty(card.Categories);
            foreach (var c in card.Categories)
            {
                var open = card.Violations.Where(v => !v.IsAccepted() && v.Category == c.Category).ToArray();
                Assert.Equal(open.Count(v => v.Severity >= 3), c.Errors);
                Assert.Equal(open.Count(v => v.Severity == 2), c.Warnings);
                Assert.True(c.ObjectsChecked > 0, c.Category);
                Assert.Equal(BpaGrading.Score(c.Errors, c.Warnings, c.ObjectsChecked), c.Score);
            }
            Assert.Equal(BpaGrading.Score(card.Violations.Count(v => !v.IsAccepted() && v.Severity >= 3), card.Violations.Count(v => !v.IsAccepted() && v.Severity == 2), card.ObjectsScanned), card.BestPracticeScore);
        }

        [Fact]
        public void The_rule_text_has_no_em_dash() => Assert.DoesNotContain("\u2014", BpaGrading.Rule);

        [Fact]
        public void Info_notes_never_move_a_letter_below_A()
        {
            // One warning over 108 objects is a B; the 95 notes of the SQL-style start beside it must leave it a B.
            var bare = BpaGrading.Compute(0, 1, 0, 108, 0);
            var noted = BpaGrading.Compute(0, 1, 95, 108, 0);
            Assert.Equal("B", bare.Letter);
            Assert.Equal(bare, noted);
        }

        private static void TheNumberShownSitsInItsLettersBand(IEnumerable<int> objectCounts)
        {
            // Every mix of errors and warnings over the given model sizes: the number the sentence prints is
            // inside the band the letter names, so "0.10" never stands beside a C.
            foreach (var objects in objectCounts)
                for (var errors = 0; errors <= 4; errors++)
                    for (var warnings = 0; warnings <= 60; warnings++)
                    {
                        if (errors + warnings == 0) continue;
                        var (letter, weight, why) = BpaGrading.Compute(errors, warnings, 0, objects, 0);
                        var shown = double.Parse(Regex.Match(why, @"(\d+\.\d+) weighted per object").Groups[1].Value, CultureInfo.InvariantCulture);
                        Assert.Equal(weight, shown);
                        var (low, high) = letter switch
                        {
                            "B" => (0.0, BpaGrading.BMax),
                            "C" => (BpaGrading.BMax, BpaGrading.CMax),
                            "D" => (BpaGrading.CMax, BpaGrading.DMax),
                            _ => (BpaGrading.DMax, double.MaxValue),
                        };
                        Assert.True(shown > low && shown <= high, $"{errors}e {warnings}w over {objects}: {why}");
                        Assert.DoesNotContain(" 1 objects", why);
                    }
        }

        // Default: every size up to 24 (where the bands' edges for 0 to 4 errors move fastest) and a spread of larger ones.
        [Fact]
        public void The_number_shown_always_sits_in_its_letters_band_sample() =>
            TheNumberShownSitsInItsLettersBand(Enumerable.Range(1, 24).Concat(new[] { 49, 50, 99, 100, 101, 250, 399, 400 }));

        // Sweep: every size up to 400 objects.
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public void The_number_shown_always_sits_in_its_letters_band() =>
            TheNumberShownSitsInItsLettersBand(Enumerable.Range(1, 400));

        [Fact]
        public void A_rule_that_could_not_run_is_named_beside_open_findings() =>
            Assert.Contains("1 error or warning rule could not be checked", BpaGrading.Compute(0, 1, 0, 10, 1).Why);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        [Fact]
        public void The_help_and_the_bpa_scan_description_state_the_rule_word_for_word()
        {
            var help = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.VSCode", "webview", "src", "help.tsx"));
            foreach (var sentence in Regex.Split(BpaGrading.Rule, @"(?<=\.) "))
                Assert.True(help.Contains(sentence, StringComparison.Ordinal), "the Model quality help lacks: " + sentence);
            var description = typeof(McpTools).GetMethod(nameof(McpTools.BpaScanOp))!.GetCustomAttribute<DescriptionAttribute>()!.Description;
            Assert.Contains(BpaGrading.Rule, description);
            Assert.Contains("bestPractice", description);
        }

        private static async Task<LocalEngine> ModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("Grade", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            return engine;
        }

        [Fact]
        public async Task A_full_scan_carries_the_letter_and_the_objects_it_counted()
        {
            var engine = await ModelAsync();
            var card = await engine.BpaScanAsync();
            Assert.True(card.ObjectsScanned > 0);
            Assert.NotNull(card.BestPractice);
            Assert.False(string.IsNullOrWhiteSpace(card.BestPracticeWhy));
            var open = card.Violations.Where(v => !v.Waived).ToArray();
            var expected = BpaGrading.Compute(open.Count(v => v.Severity >= 3), open.Count(v => v.Severity == 2),
                open.Count(v => v.Severity <= 1), card.ObjectsScanned, card.Unknowns.Count(u => u.BlocksGate)).Letter;
            Assert.Equal(expected, card.BestPractice);
        }

        [Fact]
        public async Task Waiving_every_error_and_warning_gives_an_A()
        {
            var engine = await ModelAsync();
            var card = await engine.BpaScanAsync();
            Assert.NotEqual("A", card.BestPractice);
            foreach (var v in card.Violations.Where(v => !v.Waived && v.Severity >= 2))
                await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "Accepted for the test with a written reason.", "human");
            var after = await engine.BpaScanAsync();
            Assert.Equal("A", after.BestPractice);
            Assert.True(after.WaivedCount > 0);
        }

        [Fact]
        public async Task The_mcp_summary_names_it_Best_practice()
        {
            var engine = await ModelAsync();
            var s = await McpTools.BpaScanSummary(engine);
            Assert.Equal((await engine.BpaScanAsync()).BestPractice, s.BestPractice);
            Assert.Contains("Best practice", s.Note);
        }
    }
}
