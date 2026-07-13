using System;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class TimeIntelligenceVariantsTests
    {
        [Theory]
        [InlineData("TOTALYTD([Sales], 'Date'[Date])", TiVariantKind.Ytd)]
        [InlineData("CALCULATE([Sales], DATESYTD('Date'[Date]))", TiVariantKind.Ytd)]
        [InlineData("TOTALQTD([Sales], 'Date'[Date])", TiVariantKind.Qtd)]
        [InlineData("calculate([Sales], datesqtd('Date'[Date]))", TiVariantKind.Qtd)]
        [InlineData("totalmtd([Sales], 'Date'[Date])", TiVariantKind.Mtd)]
        [InlineData("CALCULATE([Sales], SAMEPERIODLASTYEAR('Date'[Date]))", TiVariantKind.PriorYear)]
        [InlineData("CALCULATE([Sales], dateadd('Date'[Date], -1, year))", TiVariantKind.PriorYear)]
        public void Classifier_recognizes_supported_kinds_case_insensitively(string dax, TiVariantKind kind)
        {
            var c = TimeIntelligenceVariants.Classify(dax, "Sales");
            Assert.Equal(kind, c.Kind);
            Assert.Equal("'Date'[Date]", c.DateColumnRef);
            Assert.Null(c.Reason);
        }

        [Fact]
        public void Classifier_recognizes_year_over_year_subtraction()
        {
            var c = TimeIntelligenceVariants.Classify(
                "[Sales] - CALCULATE([Sales], SAMEPERIODLASTYEAR('Date'[Date]))", "Sales");
            Assert.Equal(TiVariantKind.YearOverYearDelta, c.Kind);
        }

        [Fact]
        public void Classifier_recognizes_year_over_year_division_shape()
        {
            var c = TimeIntelligenceVariants.Classify(
                "[Sales] / CALCULATE([Sales], DATEADD('Date'[Date], -1, YEAR))", "Sales");
            Assert.Equal(TiVariantKind.YearOverYearDelta, c.Kind);
        }

        [Fact]
        public void Classifier_recognizes_divide_function_year_over_year_shape()
        {
            var c = TimeIntelligenceVariants.Classify(
                "DIVIDE([Sales], CALCULATE([Sales], SAMEPERIODLASTYEAR('Date'[Date])))", "Sales");
            Assert.Equal(TiVariantKind.YearOverYearDelta, c.Kind);
        }

        [Fact]
        public void Comments_are_stripped_before_classification()
        {
            var dax = "// TOTALYTD([Sales], 'Wrong'[Date])\n/* DATESQTD('Wrong'[Date]) */\nCALCULATE([Sales], DATESMTD('Date'[Date])) -- SAMEPERIODLASTYEAR('Wrong'[Date])";
            var c = TimeIntelligenceVariants.Classify(dax, "Sales");
            Assert.Equal(TiVariantKind.Mtd, c.Kind);
            Assert.Equal("'Date'[Date]", c.DateColumnRef);
            Assert.DoesNotContain("TOTALYTD", TimeIntelligenceVariants.StripComments(dax));
            Assert.DoesNotContain("SAMEPERIODLASTYEAR", TimeIntelligenceVariants.StripComments(dax));
        }

        [Fact]
        public void Comment_markers_inside_strings_are_not_treated_as_comments()
        {
            var dax = "VAR Note = \"// keep /* this */ -- text\" RETURN TOTALYTD([Sales], 'Date'[Date])";
            var stripped = TimeIntelligenceVariants.StripComments(dax);
            Assert.Contains("// keep /* this */ -- text", stripped);
            Assert.Equal(TiVariantKind.Ytd, TimeIntelligenceVariants.Classify(dax, "Sales").Kind);
        }

        [Fact]
        public void Missing_expected_base_reference_is_unrecognized()
        {
            var c = TimeIntelligenceVariants.Classify("TOTALYTD([Revenue], 'Date'[Date])", "Sales");
            Assert.Equal(TiVariantKind.Unrecognized, c.Kind);
            Assert.Equal("expected base measure reference is missing", c.Reason);
        }

        [Fact]
        public void Two_different_ti_functions_are_unrecognized()
        {
            var c = TimeIntelligenceVariants.Classify(
                "TOTALYTD([Sales], 'Date'[Date]) + TOTALMTD([Sales], 'Date'[Date])", "Sales");
            Assert.Equal(TiVariantKind.Unrecognized, c.Kind);
            Assert.Equal("multiple different TI functions are present", c.Reason);
        }

        [Fact]
        public void Missing_date_column_is_unrecognized()
        {
            var c = TimeIntelligenceVariants.Classify("DATESYTD([Sales])", "Sales");
            Assert.Equal(TiVariantKind.Unrecognized, c.Kind);
            Assert.Equal("date column reference could not be extracted", c.Reason);
        }

        [Fact]
        public void Quoted_table_name_date_column_is_extracted()
        {
            var c = TimeIntelligenceVariants.Classify("TOTALYTD([Sales], 'Calendar Table'[Day])", "Sales");
            Assert.Equal(TiVariantKind.Ytd, c.Kind);
            Assert.Equal("'Calendar Table'[Day]", c.DateColumnRef);
        }

        [Fact]
        public void Unknown_pattern_has_the_fixed_honest_reason()
        {
            var c = TimeIntelligenceVariants.Classify("CALCULATE([Sales], PREVIOUSYEAR('Date'[Date]))", "Sales");
            Assert.Equal(TiVariantKind.Unrecognized, c.Kind);
            Assert.Equal("pattern not recognized", c.Reason);
        }

        [Fact]
        public void Ytd_query_uses_period_end_for_variant_and_full_period_for_expected()
        {
            var q = TimeIntelligenceVariants.BuildIdentityQuery(
                C(TiVariantKind.Ytd), "Sales YTD", D(2025, 1, 1), D(2025, 12, 31), D(2024, 1, 1), D(2024, 12, 31));
            Assert.Contains("\"__ti_variant\"", q.Dax);
            Assert.Contains("\"__ti_expected\"", q.Dax);
            Assert.Contains("DATESBETWEEN('Date'[Date], DATE(2025,12,31), DATE(2025,12,31))", q.Dax);
            Assert.Contains("DATESBETWEEN('Date'[Date], DATE(2025,1,1), DATE(2025,12,31))", q.Dax);
        }

        [Fact]
        public void Prior_year_query_uses_current_period_for_variant_and_prior_period_for_base()
        {
            var q = TimeIntelligenceVariants.BuildIdentityQuery(
                C(TiVariantKind.PriorYear), "Sales PY", D(2025, 1, 1), D(2025, 12, 31), D(2024, 1, 1), D(2024, 12, 31));
            Assert.Contains("CALCULATE([Sales PY], DATESBETWEEN('Date'[Date], DATE(2025,1,1), DATE(2025,12,31)))", q.Dax);
            Assert.Contains("CALCULATE([Sales], DATESBETWEEN('Date'[Date], DATE(2024,1,1), DATE(2024,12,31)))", q.Dax);
            Assert.Contains("\"__ti_variant\"", q.Dax);
            Assert.Contains("\"__ti_expected\"", q.Dax);
        }

        [Fact]
        public void Year_over_year_query_subtracts_prior_base_and_escapes_measure_brackets()
        {
            var c = C(TiVariantKind.YearOverYearDelta, "Sales ]x[");
            var q = TimeIntelligenceVariants.BuildIdentityQuery(
                c, "Sales ]x[ YoY", D(2025, 1, 1), D(2025, 12, 31), D(2024, 1, 1), D(2024, 12, 31));
            Assert.Contains("[Sales ]]x[ YoY]", q.Dax);
            Assert.Contains("[Sales ]]x[]", q.Dax);
            Assert.Contains("DATE(2025,1,1), DATE(2025,12,31)", q.Dax);
            Assert.Contains("DATE(2024,1,1), DATE(2024,12,31)", q.Dax);
            Assert.Contains(" - CALCULATE([Sales ]]x[]", q.Dax);
            Assert.Contains("\"__ti_variant\"", q.Dax);
            Assert.Contains("\"__ti_expected\"", q.Dax);
        }

        [Fact]
        public void Unrecognized_query_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => TimeIntelligenceVariants.BuildIdentityQuery(
                C(TiVariantKind.Unrecognized), "Unknown", D(2025, 1, 1), D(2025, 12, 31), D(2024, 1, 1), D(2024, 12, 31)));
        }

        [Fact]
        public void Judge_passes_inside_mixed_tolerance_and_names_values()
        {
            var v = TimeIntelligenceVariants.Judge(C(TiVariantKind.Ytd), "Sales YTD", 100.000009m, 100m, true);
            Assert.Equal(Verdict.Pass, v.Verdict);
            Assert.Contains("100.000009", v.Explanation);
            Assert.Contains("100", v.Explanation);
        }

        [Fact]
        public void Judge_fails_outside_tolerance_and_names_both_values_and_delta()
        {
            var v = TimeIntelligenceVariants.Judge(C(TiVariantKind.Ytd), "Sales YTD", 101.5m, 100m, true);
            Assert.Equal(Verdict.Fail, v.Verdict);
            Assert.Contains("101.5", v.Explanation);
            Assert.Contains("100", v.Explanation);
            Assert.Contains("delta: 1.5", v.Explanation);
        }

        [Fact]
        public void Judge_null_variant_side_is_not_verifiable()
        {
            var v = TimeIntelligenceVariants.Judge(C(TiVariantKind.Qtd), "Sales QTD", null, 10m, true);
            Assert.Equal(Verdict.NotVerifiable, v.Verdict);
            Assert.Contains("variant side is blank", v.Explanation);
        }

        [Fact]
        public void Judge_not_executed_is_not_verifiable()
        {
            var v = TimeIntelligenceVariants.Judge(C(TiVariantKind.Mtd), "Sales MTD", 10m, 10m, false);
            Assert.Equal(Verdict.NotVerifiable, v.Verdict);
            Assert.Contains("identity query was not run", v.Explanation);
        }

        [Fact]
        public void Judge_both_blank_is_not_verifiable_never_pass()
        {
            var v = TimeIntelligenceVariants.Judge(C(TiVariantKind.PriorYear), "Sales PY", null, null, true);
            Assert.Equal(Verdict.NotVerifiable, v.Verdict);
            Assert.NotEqual(Verdict.Pass, v.Verdict);
            Assert.Contains("both sides blank over the sampled period", v.Explanation);
        }

        [Fact]
        public void Mtd_uses_the_latest_complete_calendar_month_and_truncates_time()
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(
                new DateTime(2025, 1, 15, 10, 30, 0), new DateTime(2025, 4, 12, 22, 0, 0), TiVariantKind.Mtd);
            Assert.True(p.Verifiable);
            Assert.Equal(D(2025, 3, 1), p.PeriodStart);
            Assert.Equal(D(2025, 3, 31), p.PeriodEnd);
            Assert.Equal(p.PeriodStart, p.PriorStart);
            Assert.Equal(p.PeriodEnd, p.PriorEnd);
        }

        [Fact]
        public void Mtd_accepts_the_current_month_when_its_last_day_is_present()
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2025, 1, 1), D(2025, 4, 30), TiVariantKind.Mtd);
            Assert.Equal(D(2025, 4, 1), p.PeriodStart);
            Assert.Equal(D(2025, 4, 30), p.PeriodEnd);
        }

        [Fact]
        public void Qtd_uses_the_latest_complete_calendar_quarter()
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2024, 11, 1), D(2025, 8, 14), TiVariantKind.Qtd);
            Assert.True(p.Verifiable);
            Assert.Equal(D(2025, 4, 1), p.PeriodStart);
            Assert.Equal(D(2025, 6, 30), p.PeriodEnd);
        }

        [Fact]
        public void Ytd_uses_the_latest_complete_calendar_year()
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2022, 7, 1), D(2025, 2, 1), TiVariantKind.Ytd);
            Assert.True(p.Verifiable);
            Assert.Equal(D(2024, 1, 1), p.PeriodStart);
            Assert.Equal(D(2024, 12, 31), p.PeriodEnd);
        }

        [Theory]
        [InlineData(TiVariantKind.PriorYear)]
        [InlineData(TiVariantKind.YearOverYearDelta)]
        public void Year_comparisons_use_the_latest_pair_of_complete_years(TiVariantKind kind)
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2021, 1, 1), D(2024, 12, 31), kind);
            Assert.True(p.Verifiable);
            Assert.Equal(D(2024, 1, 1), p.PeriodStart);
            Assert.Equal(D(2024, 12, 31), p.PeriodEnd);
            Assert.Equal(D(2023, 1, 1), p.PriorStart);
            Assert.Equal(D(2023, 12, 31), p.PriorEnd);
        }

        [Theory]
        [InlineData(TiVariantKind.Mtd)]
        [InlineData(TiVariantKind.Qtd)]
        [InlineData(TiVariantKind.Ytd)]
        public void No_complete_period_is_stated_plainly(TiVariantKind kind)
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2025, 2, 2), D(2025, 2, 20), kind);
            Assert.False(p.Verifiable);
            Assert.Equal("no complete period in the data", p.Reason);
        }

        [Theory]
        [InlineData(TiVariantKind.PriorYear)]
        [InlineData(TiVariantKind.YearOverYearDelta)]
        public void One_complete_year_is_not_enough_for_year_comparisons(TiVariantKind kind)
        {
            var p = TimeIntelligenceVariants.SelectSamplePeriod(D(2024, 1, 1), D(2024, 12, 31), kind);
            Assert.False(p.Verifiable);
            Assert.Equal("the data does not span two complete years", p.Reason);
        }

        private static TiClassification C(TiVariantKind kind, string baseMeasure = "Sales") =>
            new TiClassification { Kind = kind, BaseMeasure = baseMeasure, DateColumnRef = "'Date'[Date]" };

        private static DateTime D(int year, int month, int day) => new DateTime(year, month, day);
    }
}
