// The column category family (T268, M4 columns; the fair replay of 26 September).
//
// WHY THIS SETTING. The fair replay (docs/redesign/jev-columns-replay.md, "Third run") graded Jev against a key
// checked from each column's own data, on 218 fresh public models. Data category was the one new column setting
// that cleared Kane's bar on both reads: Jev when sure, else the rule, beat the rule on the key (98.3% against 97.4%,
// 65 wins to 22, p 0.000004) and on the blind hand labels (98.7% against 96.9%), with no harmful mistake. Its wins
// are kinds a name rule cannot see: postal codes, continents, coordinates. Summarize by, hidden and sort by stay on
// their rules.
//
// THE PRO PATH. Exactly what the replay scored: Jev's pick when it is sure (the point's bar, 0.85), otherwise the
// rule the replay compared against, which reads the column's measured value shares (<see cref="ValueRule"/>, ported
// from tools/jev-replay/columns/rules.mjs baselineDatacatTyped). For every column it asks about and hears back on,
// this family DECIDES: a sure pick, or below the bar the value-share rule on a measured column. A column it decides
// is taken away from the names-only geographic rule (CAT-GEO) in both directions, so a sure "none" really leaves the
// column alone (14 of Jev's 65 replay wins were exactly that) and the fallback is the stronger rule that was tested,
// not the weaker one. A column it did not decide (not asked, no answer, or unsure and not measured) stays CAT-GEO's,
// as it is for a free session.
//
// WHAT JEV SEES. The facts the fair replay's call carried, under this point's four fields (ColumnAskFacts.cs, where
// the mapping and its two stated differences are written down): names, types, structure and derived facts only.
// Never a value, a minimum, a maximum or a sample, and never the column's own data category or any other setting.
//
// WHAT THE ENGINE CHECKS. Every pick is checked against the options the engine lists for the column's type (a
// number can only be a coordinate, a postal code or a barcode; a date is never asked), and a link category needs
// values that are links when the column was measured. A pick that fails is never a line; it goes on the refused list
// with a plain reason.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Map is pure. The
// asking is LocalEngine.JevFamilies.cs, through DecisionService, where every gate lives (Pro, consent, budget, the
// fence).

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class ColumnCategoryFamily
    {
        internal const string Name = "column-category";

        /// <summary>WHETHER JEV DECIDES: NO. The 26 September re-test on the question that ships (one question per call;
        /// tools/jev-replay/columns/retest-bar.json, README "Re-test") failed the committed bar on the blind hand read:
        /// Pro path 98.4% against the rule's 97.4% on 497 columns, 7 wins to 2, exact McNemar p 0.18 (the win could be
        /// luck). The key read passed (98.4% against 97.5% on 4,465, 53 to 12, p 0.0000003), but the bar fixed before the
        /// run decides on the hand read, so the point is off and CAT-GEO decides, for Pro and free alike.</summary>
        internal const bool JevDecides = false;

        internal const string Question = "data_category";

        /// <summary>How many columns one run asks about for the first time. A cap, not a sample: every ask is a call
        /// against the session's budget. Answers already heard are never asked again, and the columns past the cap are
        /// asked in the runs that follow, which the next read starts (LocalEngine.JevFamilies.cs, the batches).</summary>
        internal const int MaxColumnsPerRun = 60;

        internal sealed class Ask
        {
            public string Ref;
            public string ColumnName;
            public string TableRef;
            public string TableName;
            public string DataType;

            /// <summary>The measured share of values that are links, or null when the column was not measured.</summary>
            public double? UrlShare;

            /// <summary>The column's measured facts, or null when it was not measured. Read by the value-share rule.</summary>
            public Facts.MeasuredFacts Measured;

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;
        }

        // ---- the options the engine lists ---------------------------------------------------------------

        private static readonly string[] NumberOptions = { "Latitude", "Longitude", "PostalCode", "Barcode" };
        private static readonly string[] LinkOptions = { "WebUrl", "ImageUrl" };

        private static bool IsNumber(string dataType) =>
            dataType == nameof(DataType.Int64) || dataType == nameof(DataType.Double) || dataType == nameof(DataType.Decimal);

        /// <summary>The categories (not counting none) that fit a column of this data type, in the registry's order.
        /// Text can be any of them; a number only a coordinate, a postal code or a barcode; anything else none.</summary>
        internal static IReadOnlyList<string> OptionsFor(string dataType)
        {
            if (dataType == nameof(DataType.String)) return DecisionRegistry.DataCategories.Where(o => o != "none").ToArray();
            if (IsNumber(dataType)) return DecisionRegistry.DataCategories.Where(NumberOptions.Contains).ToArray();
            return Array.Empty<string>();
        }

        // ---- read the model once --------------------------------------------------------------------------

        internal static Prepared Prepare(Model m, Facts.ModelFacts facts)
        {
            var prepared = new Prepared();
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnCategory);
            var questions = Questions();
            var shape = ColumnAskFacts.Read(m);

            // Every wanted column, in table then column order. How many are ASKED in one run is the batch cap
            // (LocalEngine.JevFamilies.cs, MaxColumnsPerRun); the rest are asked in the runs after it.
            foreach (var table in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (table is CalculationGroupTable) continue;
                foreach (var c in table.Columns.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Wants(c)) continue;
                    var (tableFacts, measured) = ColumnAskFacts.FactsOf(facts, table, c);
                    prepared.Asks.Add(new Ask
                    {
                        Ref = ObjectRefs.For(c),
                        ColumnName = c.Name,
                        TableRef = ObjectRefs.For(table),
                        TableName = table.Name,
                        DataType = c.DataType.ToString(),
                        UrlShare = measured?.UrlShare,
                        Measured = measured,
                        Snapshot = ColumnAskFacts.Snapshot(shape, table, c, tableFacts, measured, point),
                        Questions = questions,
                    });
                }
            }
            return prepared;
        }

        /// <summary>A column this family asks about: visible, text or a number, and no data category yet.</summary>
        internal static bool Wants(Column c) =>
            c != null && !c.IsHidden && c.Type != ColumnType.RowNumber && OptionsFor(c.DataType.ToString()).Count > 0
            && (string.IsNullOrWhiteSpace(c.DataCategory) || string.Equals(c.DataCategory, "Uncategorized", StringComparison.OrdinalIgnoreCase));

        /// <summary>The replay's criteria (tools/jev-replay/columns/rules.mjs, DATACAT_CRITERIA), word for word, so the
        /// question asked here is the one the replay scored.</summary>
        private static readonly Dictionary<string, string> Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "none", "the values are none of the kinds listed below" },
            { "Country", "the column holds country or region names" },
            { "StateOrProvince", "the column holds state, province or region-within-country names" },
            { "County", "the column holds county names" },
            { "City", "the column holds city or town names" },
            { "PostalCode", "the column holds postal codes or zip codes" },
            { "Address", "the column holds street addresses" },
            { "Place", "the column holds place names that are not one of the kinds above" },
            { "Continent", "the column holds continent names" },
            { "Latitude", "the column holds latitude coordinates" },
            { "Longitude", "the column holds longitude coordinates" },
            { "ImageUrl", "the column holds web addresses that point at image files" },
            { "WebUrl", "the column holds web addresses that point at web pages" },
            { "Barcode", "the column holds barcode values" },
        };

        internal static IReadOnlyList<DecisionQuestion> Questions() => new[]
        {
            new DecisionQuestion(Question, DecisionQuestionKind.Choice,
                "What kind of real-world thing do the values in this column hold? Answer none unless the values clearly are that kind of thing.",
                DecisionRegistry.DataCategories.Select(o => new DecisionOption(o, Criteria[o])).ToList()),
        };

        // ---- Jev's answer into a line -------------------------------------------------------------------------

        internal static void Map(Ask ask, DecisionAnswers answers, double bar, List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var answer = answers.For(Question);
            if (answer == null) return;
            Map(ask, answer.Choice, answer.Confidence, bar, lines, refused);
        }

        /// <summary>The rule id on a line the value-share rule decided below the bar. Its provenance is rule, not Jev.</summary>
        internal const string ValueRuleId = "CAT-VALUES";

        /// <summary>Whether this family decides the column (and CAT-GEO stands aside): Jev was sure, or the column was
        /// measured so the value-share rule can decide it.</summary>
        internal static bool Decides(double confidence, double bar, bool measured) => confidence >= bar || measured;

        /// <summary>One pick into one line, a refusal, or nothing. Pure: no model, no ask. Below the bar the
        /// value-share rule decides a measured column; an unmeasured one is left to CAT-GEO.</summary>
        internal static void Map(Ask ask, string category, double confidence, double bar,
                                 List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null) return;
            if (confidence < bar)
            {
                if (ask.Measured == null) return;                                  // CAT-GEO decides
                var ruled = ValueRule(ask.ColumnName, ask.DataType, ask.Measured);
                if (ruled == null) return;
                var because = ask.ColumnName + " holds " + Holds(ruled) + ", going by what its values look like on this computer.";
                lines.Add(new ChangeItem
                {
                    ObjectRef = ask.Ref, ObjectName = ask.ColumnName, Kind = "set_data_category", Source = JevFamily.Source,
                    Category = "Formatting", Severity = "Info", Risk = "safe", Target = "dataCategory",
                    Title = "Mark as " + Plain(ruled), Rationale = because, Before = "(none)", After = ruled, Status = "approved",
                    JevItem = JevFamily.ItemId(Name, ask.TableRef), DecisionPoint = DecisionRegistry.ColumnCategory,
                    RuleId = ValueRuleId, Reason = because,
                    CheckText = ruled + " is a data category Power BI offers for a " + (IsNumber(ask.DataType) ? "number" : "text")
                              + " column, and this column had none.",
                });
                return;
            }
            if (string.IsNullOrEmpty(category)) return;
            if (!DecisionRegistry.DataCategories.Contains(category)) return;       // never offered
            if (string.Equals(category, "none", StringComparison.Ordinal)) return;

            var word = Plain(category);
            if (!OptionsFor(ask.DataType).Contains(category))
            {
                refused.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = ask.ColumnName + " holds " + (IsNumber(ask.DataType) ? "numbers" : "values of another kind")
                           + ", and a " + word + " category does not fit that, so it was left out.",
                });
                return;
            }
            if (LinkOptions.Contains(category) && ask.UrlShare.HasValue && ask.UrlShare.Value <= 0)
            {
                refused.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = "None of the values in " + ask.ColumnName + " is a web address, so it was not marked as a link.",
                });
                return;
            }

            var reason = ask.ColumnName + " holds " + Holds(category) + ", so maps, links and Copilot can treat it as one.";
            lines.Add(new ChangeItem
            {
                ObjectRef = ask.Ref,
                ObjectName = ask.ColumnName,
                Kind = "set_data_category",
                Source = JevFamily.Source,
                Category = "Formatting",
                Severity = "Info",
                Risk = "safe",
                Target = "dataCategory",
                Title = "Mark as " + word,
                Rationale = reason,
                Before = "(none)",
                After = category,
                Status = "approved",
                JevItem = JevFamily.ItemId(Name, ask.TableRef),
                DecisionPoint = DecisionRegistry.ColumnCategory,
                DecisionConfidence = Math.Round(confidence, 4),
                DecisionWord = JevFamily.ConfidenceWord(confidence, bar),
                Reason = reason,
                CheckText = category + " is a data category Power BI offers for a " + (IsNumber(ask.DataType) ? "number" : "text")
                          + " column, and this column had none.",
            });
        }

        // ---- the value-share rule (rules.mjs baselineGeo + baselineDatacatTyped, ported) --------------------------

        private static readonly (string Token, string Category)[] NameHints =
        {
            ("latitude", "Latitude"), ("longitude", "Longitude"), ("postal", "PostalCode"), ("zip", "PostalCode"),
            ("address", "Address"), ("city", "City"), ("countryregion", "Country"), ("country", "Country"),
            ("state", "StateOrProvince"), ("province", "StateOrProvince"), ("barcode", "Barcode"), ("imageurl", "ImageUrl"),
            ("image", "ImageUrl"), ("weburl", "WebUrl"), ("url", "WebUrl"),
        };

        private static string ByName(string name)
        {
            var n = (name ?? "").ToLowerInvariant().Replace(" ", "");
            foreach (var (token, category) in NameHints) if (n.Contains(token)) return category;
            return null;
        }

        /// <summary>The rule the fair replay judged Jev against: a value share of 0.8 or more names the kind (image
        /// link, web link, country, region, city; a postal code also needs the name), otherwise the name, and a
        /// postal-code name on values that are not code shaped is none. Numbers: latitude and longitude by name.</summary>
        internal static string ValueRule(string name, string dataType, Facts.MeasuredFacts m)
        {
            if (m == null) return null;
            var byName = ByName(name);
            if (dataType == nameof(DataType.String))
            {
                var p = m.PlaceShare ?? new Facts.PlaceShare();
                if (m.ImageUrlShare >= 0.8) return "ImageUrl";
                if (m.UrlShare >= 0.8) return "WebUrl";
                if (p.Country >= 0.8) return "Country";
                if (p.Region >= 0.8) return "StateOrProvince";
                if ((p.City ?? 0) >= 0.8) return "City";
                if (byName == "PostalCode") return p.Postcode >= 0.8 ? "PostalCode" : null;
                return byName;
            }
            return byName == "Latitude" || byName == "Longitude" ? byName : null;
        }

        private static string Plain(string category)
        {
            switch (category)
            {
                case "StateOrProvince": return "state or province";
                case "PostalCode": return "postal code";
                case "ImageUrl": return "image link";
                case "WebUrl": return "web link";
                default: return category.ToLowerInvariant();
            }
        }

        private static string Holds(string category)
        {
            switch (category)
            {
                case "Country": return "country names";
                case "StateOrProvince": return "state or province names";
                case "County": return "county names";
                case "City": return "city or town names";
                case "PostalCode": return "postal codes";
                case "Address": return "street addresses";
                case "Place": return "place names";
                case "Continent": return "continent names";
                case "Latitude": return "latitudes";
                case "Longitude": return "longitudes";
                case "ImageUrl": return "links to images";
                case "WebUrl": return "links to web pages";
                case "Barcode": return "barcodes";
                default: return "values of one kind";
            }
        }

        /// <summary>The first "why" line of a category item.</summary>
        internal static string Missing(int n, string table) =>
            JevFamily.Count(n, "column", "columns") + " in " + table + (n == 1 ? " holds" : " hold")
            + " places, links or codes, but Power BI is not told so yet. Maps and links cannot use "
            + (n == 1 ? "it" : "them") + " until " + (n == 1 ? "it is" : "they are") + " marked.";
    }
}
