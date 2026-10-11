// Verifier tests for m11/bpa-grade (the Best practice letter, 9c4a0502). Each test here failed on that commit and names
// one defect the verifier found. They are left failing on purpose: the builder fixes the product, not the test.
//
//   V1  the number the sentence shows can contradict the letter it explains (the help says B is up to 0.10)
//   V2  a density just over a band's top is rounded into that band before it is compared
//   V3  one object is written "1 objects"
//   V4  splitting a table into more partitions (an incremental refresh policy does exactly this) changes the letter
//   V7  the sentence hides the info notes that make up most of a letter below A
//   V6  bpa_scan names the letter "bestPractice" in its summary and "grade" in its full detail

using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class BpaGradeVerifyTests
    {
        // V1. The why sentence shows the weight to two places. Near a band's top the shown number reads as the band
        // below while the letter is the band above: "Best practice C: ... 0.10 weighted per object", beside a help
        // text that says B is up to 0.10. The number a person is shown must sit inside the band its letter names.
        [Theory]
        [InlineData(0, 25, 1, 500)]    // 50.5 / 500 = 0.101, letter C, shown as 0.10
        [InlineData(0, 63, 0, 500)]    // 126 / 500 = 0.252, letter D, shown as 0.25
        [InlineData(0, 126, 0, 500)]   // 252 / 500 = 0.504, letter F, shown as 0.50
        public void The_weight_the_sentence_shows_sits_in_the_band_its_letter_names(int errors, int warnings, int info, int objects)
        {
            var (letter, _, why) = BpaGrading.Compute(errors, warnings, info, objects, 0);
            var shown = double.Parse(Regex.Match(why, @"(\d+\.\d+) weighted per object").Groups[1].Value, CultureInfo.InvariantCulture);
            var (low, high) = letter switch
            {
                "B" => (0.0, BpaGrading.BMax),
                "C" => (BpaGrading.BMax, BpaGrading.CMax),
                "D" => (BpaGrading.CMax, BpaGrading.DMax),
                _ => (BpaGrading.DMax, double.MaxValue),
            };
            Assert.True(shown > low && shown <= high, $"letter {letter}, but the sentence shows {shown}: {why}");
        }

        // V2. 401 warnings over 8000 objects is 802 / 8000 = 0.10025 per object, which is over B's 0.10, so the rule as
        // written (code comment, help and tool description) says C. The weight was rounded to three places before the
        // comparison, which turned 0.10025 into 0.100 and the letter into B. (Builder, review-fix round 2: the inputs
        // were 100 warnings and 1 info note over 2000 objects; under the coordinator's ruling that info notes carry no
        // weight that is exactly 0.10, a B, so the same density is now reached with warnings alone.)
        [Fact]
        public void A_density_just_over_the_top_of_B_is_not_B() =>
            Assert.Equal("C", BpaGrading.Compute(0, 401, 0, 8000, 0).Letter);

        // V3. A new, empty model has one object to check (the model itself), and its sentence says "1 objects".
        [Fact]
        public void One_object_is_one_object_in_the_sentence() =>
            Assert.DoesNotContain(" 1 objects", BpaGrading.Compute(0, 1, 0, 1, 0).Why);

        private const string EmptyRows = "let Source = #table(type table [Amount = number, CustomerKey = number], {}) in Source";

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> SalesModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Fairness", 1604);
            await engine.CreateImportTableAsync("Sales", EmptyRows, "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", "CustomerKey", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( Sales[Amount] )", "human");
            await engine.CreateMeasureAsync("table:Sales", "Order Lines", "COUNTROWS ( Sales )", "human");
            return (engine, sessions);
        }

        // V4. The same design, split into monthly partitions. A model opened from the service carries the partitions an
        // incremental refresh policy made, so this is the same model opened from its project folder and from its
        // workspace. The partitions add no finding (asserted), yet every one of them is an "object scanned", so the
        // letter moves. Ten years of monthly partitions is the ceiling tried; the failure names where it moved.
        private static async Task PartitionsKeepTheLetterAsync(int partitions)
        {
            var (engine, sessions) = await SalesModelAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                Assert.NotEqual("A", before.BestPractice);
                for (var month = 1; month <= partitions; month++)
                {
                    var name = "Sales " + (2016 + (month - 1) / 12).ToString(CultureInfo.InvariantCulture) + " "
                               + ((month - 1) % 12 + 1).ToString("00", CultureInfo.InvariantCulture);
                    await sessions.Current.MutateAsync("human", "add partition " + name, m => m.Tables["Sales"].AddMPartition(name, EmptyRows));
                    var after = await engine.BpaScanAsync();
                    Assert.Equal(before.ViolationCount, after.ViolationCount);
                    Assert.True(before.BestPractice == after.BestPractice,
                        $"{before.BestPractice} with 1 partition ({before.BestPracticeWhy}) but {after.BestPractice} with {month + 1} ({after.BestPracticeWhy})");
                }
            }
        }

        // Default: two partitions, the smallest split, scanned after each. The grade counts every partition as an object scanned.
        [Fact]
        public Task Splitting_a_table_into_partitions_does_not_change_the_letter_sample() => PartitionsKeepTheLetterAsync(2);

        // Sweep: the ten-year ceiling, 120 monthly partitions and a scan after each (120 scans), the form the verifier ran.
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public Task Splitting_a_table_into_partitions_does_not_change_the_letter() => PartitionsKeepTheLetterAsync(120);

        // V6. One name everywhere (the brief): the summary calls the letter bestPractice, the full detail of the same
        // tool calls it grade, which is also what ai_readiness_scan calls the OTHER letter.
        [Fact]
        public async Task Both_details_of_bpa_scan_name_the_letter_bestPractice()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var summary = await McpTools.BpaScanSummary(engine);
                var full = JsonSerializer.SerializeToElement(await McpTools.BpaScan(engine),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Assert.NotNull(summary.BestPractice);
                Assert.True(full.TryGetProperty("bestPractice", out var letter),
                    "bpa_scan detail='full' carries no bestPractice field; its fields are " + string.Join(", ", full.EnumerateObject().Select(p => p.Name)));
                Assert.Equal(summary.BestPractice, letter.GetString());
            }
        }

        // V7. Info notes never stop an A, but below A they weigh 0.5 each, so on a model with many notes they make up most of
        // the weight. The sentence then names only the errors and warnings: "Best practice D: 0 open errors and 1 warning,
        // 0.46 weighted per object", where 0.44 of the 0.46 is 95 info notes it never mentions. Whatever the fix (notes weigh
        // nothing below A, or the sentence names them), the sentence must name every kind of finding that makes up its weight.
        [Theory]
        [InlineData(0, 1, 95, 108, 0)]   // the SQL-style start's 95 notes over 108 objects, plus one warning: D
        [InlineData(0, 0, 95, 108, 1)]   // the same notes and one error or warning rule that could not run: D
        public void The_sentence_names_the_info_notes_when_they_make_up_the_weight(int errors, int warnings, int info, int objects, int unknowns)
        {
            var (letter, _, why) = BpaGrading.Compute(errors, warnings, info, objects, unknowns);
            if (letter == "A" || info == 0 || BpaGrading.InfoWeight == 0) return;
            Assert.True(why.Contains("info", System.StringComparison.OrdinalIgnoreCase) || why.Contains("note", System.StringComparison.OrdinalIgnoreCase),
                $"letter {letter}, most of whose weight is {info} info notes, but the sentence does not name them: {why}");
        }
    }
}
