// The first Jev family: measures (T268, jev-groundwork-contract.md section 2).
//
// WHICH POINT, AND WHY NOT A NEW ONE. The existing measure.folder_and_format point, which the pilot proved,
// asked about EVERY existing measure that has no display folder or no number format, not only about a measure
// the moment it is first saved. The family asks exactly the request propose_measure_defaults asks (the same
// snapshot, the same questions, in the same order), so the two share one cache entry per measure and one
// recorded fixture answers both. A second request shape for the same question would have been a second
// thing for the replay bar to prove.
//
// WHAT JEV CHOOSES AND WHAT THE ENGINE DOES WITH IT.
//   folder  one of the folders this model already uses, or none. "none" proposes nothing.
//   format  one of six families. The ENGINE turns a family into a real format string: the first static
//           template of the matching catalog category (format-templates.json, the same library the picker
//           reads), or, for money, the money format this model ALREADY uses. It never guesses a currency:
//           a model with no money format yet gets no format line for a money measure, and the refused list
//           says so. "text_or_none" proposes nothing.
// Every line carries a reason the engine built from what it counted, and a check it ran. A choice the check
// refuses is never a line; it goes on the refused list with the reason.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Map is pure.
// The asking is LocalEngine.JevFamilies.cs, off the dispatcher, through DecisionService, which is where every
// gate lives (Pro, consent, budget, the fence).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class MeasuresFamily
    {
        internal const string Name = "measures";

        /// <summary>How many measures one run ASKS about. A cap on the asks, never on what is seen: Prepare lists every eligible
        /// measure, and LocalEngine.JevFamilies.cs asks this many of them per run, not-yet-asked first, then starts the next run
        /// for the rest (m11/sf-progress). Every ask is a call and the session has a budget, so a first plan on a very large model
        /// must not spend it all in one read.</summary>
        internal const int MaxMeasuresPerRun = 60;

        /// <summary>WHETHER THE ASKS GO ONE PER TABLE (m11/jev-batch, JevTableBatch.cs): a table's measures in one request, the
        /// table's columns and measures once, one question per folder or format pointing at the measure's entry in `measures`.
        /// True, as shipped: ON (Kane, 1 Oct: "the batching is the best mitigation not caps"). The comparison run of 30 September missed its fixed bar (docs/redesign/jev-batch-comparison-result.md: the unsure middle moves, sure answers stay), and Kane accepted that an unsure answer can move and arrive ticked or unticked in the reviewed Plan. JevPerObjectModeTests still proves both modes give the same plan lines. False: one request per object (the test seam JevBatchedForTest sets it).</summary>
        internal const bool Batched = true;

        /// <summary>How many measures one batched request asks about (two questions each at most).</summary>
        internal const int ChunkSize = 12;

        /// <summary>One measure the family will ask about, with everything Map needs to turn an answer into
        /// lines without reading the model again.</summary>
        internal sealed class Ask
        {
            public string Ref;
            public string Name;
            public string TableRef;
            public string TableName;
            public string CurrentFolder;
            public string CurrentFormat;
            public bool WantsFolder;
            public bool WantsFormat;

            /// <summary>How many of the model's folders the folder question did not offer (a question offers thirty at most).</summary>
            public int FoldersNotOffered;
            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>The folders this model uses, with how many measures and columns sit in each.</summary>
            public Dictionary<string, int> FolderCounts;

            /// <summary>The money format this model already uses most, or null when it uses none.</summary>
            public string MoneyFormat;
            public int MoneyFormatUses;

            /// <summary>Derived formula facts, never the formula: whether it counts rows, whether it divides.</summary>
            public bool CountsRows;
            public bool Divides;

            /// <summary>Whether the format fill rule proves this measure holds a whole number and may be filled with a
            /// whole-number format (FormatFill.MayFill, asked once here on the dispatcher). Map only reads it: a line the
            /// engine cannot prove is a suggestion, never an approved fill.</summary>
            public bool MayFillWholeNumber;

            /// <summary>What the measure computed when this ask was prepared (FormatFill.ComputeDigest), on the same read of the model that
            /// Jev is asked about. Every format line worked out from the answer carries it, so a tick, an approve or a write on a line
            /// whose measure has changed since is refused (addendum 4 item 23).</summary>
            public string Digest;

            /// <summary>This measure's entry in a batched request's `measures` (MeasureDefaultsPoint.Entry).</summary>
            public Dictionary<string, object> Entry;
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;

            /// <summary>What each table's asked measures share, by table ref (the batched request's one copy).</summary>
            public Dictionary<string, MeasureDefaultsPoint.TableShare> Shares =
                new Dictionary<string, MeasureDefaultsPoint.TableShare>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The batched requests for the asks: each table's measures in chunks, and for each measure the questions it
        /// always asked (a folder, a format) under a name that carries its ref, each starting "For the measure `measures[i]`:" and
        /// then saying what it always said, with the same options.</summary>
        internal static List<TableBatch<Ask>> Batches(Prepared prepared, int size = ChunkSize)
        {
            TableBatch<Ask> Build(IReadOnlyList<Ask> chunk)
            {
                var batch = new TableBatch<Ask> { TableRef = chunk[0].TableRef, Asks = chunk.ToList() };
                var questions = new List<DecisionQuestion>();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var names = new List<KeyValuePair<string, string>>();
                    foreach (var q in chunk[i].Questions)
                    {
                        var name = JevTableBatch.QuestionName(q.Name, chunk[i].Ref);
                        questions.Add(new DecisionQuestion(name, q.Kind, "For the measure " + JevTableBatch.Path("measures", i) + ": " + q.Instructions, q.Options));
                        names.Add(new KeyValuePair<string, string>(name, q.Name));
                    }
                    batch.Names[chunk[i]] = names;
                }
                batch.Questions = questions;
                batch.Snapshot = MeasureDefaultsPoint.BatchSnapshot(prepared.Shares[batch.TableRef], chunk.Select(a => a.Entry));
                return batch;
            }
            return JevTableBatch.Chunks(prepared.Asks, a => a.TableRef, size).SelectMany(c => JevTableBatch.Fit(c, Build)).ToList();
        }

        private static readonly Regex CountsRowsPattern =
            new Regex(@"\b(COUNTROWS|DISTINCTCOUNT|DISTINCTCOUNTNOBLANK|COUNT|COUNTA|COUNTX|COUNTAX|COUNTBLANK)\s*\(",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex DividesPattern =
            new Regex(@"\bDIVIDE\s*\(|[^/]/[^/*]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly char[] MoneySymbols = { '$', '€', '£', '¥', '₹' };

        /// <summary>Read the model once, on the dispatcher, and build one ask per measure that wants a folder
        /// or a format: EVERY eligible measure, in the order they are asked (the run cuts that list, Prepare does not). Uses the point's own snapshot and question builders, so the request is byte for byte
        /// the one propose_measure_defaults would send.</summary>
        internal static Prepared Prepare(Model m, Func<Model, Table, string[]> folderSuggestions)
        {
            var prepared = new Prepared();

            var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Tables)
            {
                foreach (var f in t.Measures.Select(x => x.DisplayFolder).Concat(t.Columns.Select(x => x.DisplayFolder)))
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    var key = f.Trim();
                    folderCounts[key] = folderCounts.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }

            var money = m.Tables.SelectMany(t => t.Measures)
                .Select(x => x.FormatString)
                .Where(fmt => !string.IsNullOrWhiteSpace(fmt) && fmt.IndexOfAny(MoneySymbols) >= 0)
                .GroupBy(fmt => fmt, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            var tableFolders = new Dictionary<Table, HashSet<string>>();
            HashSet<string> TableFolders(Table t) =>
                tableFolders.TryGetValue(t, out var set) ? set : tableFolders[t] = MeasureDefaultsPoint.FoldersOf(t);
            foreach (var table in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var measure in table.Measures.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!MeasureDefaultsPoint.WantsProposal(measure, out var wantsFolder, out var wantsFormat)) continue;

                    var measureRef = ObjectRefs.For(measure);

                    var suggested = folderSuggestions(m, table);
                    var questions = MeasureDefaultsPoint.Questions(suggested, wantsFolder, wantsFormat, folderCounts, TableFolders(table));
                    var notOffered = wantsFolder ? MeasureDefaultsPoint.RankFolders(suggested, folderCounts, TableFolders(table)) : default;
                    if (questions.Count == 0) continue;

                    var expression = measure.Expression ?? string.Empty;
                    var tableRefOf = ObjectRefs.For(table);
                    if (!prepared.Shares.ContainsKey(tableRefOf)) prepared.Shares[tableRefOf] = MeasureDefaultsPoint.Share(table);
                    prepared.Asks.Add(new Ask
                    {
                        Ref = measureRef,
                        Name = measure.Name,
                        TableRef = ObjectRefs.For(table),
                        TableName = table.Name,
                        CurrentFolder = measure.DisplayFolder ?? string.Empty,
                        CurrentFormat = measure.FormatString ?? string.Empty,
                        WantsFolder = wantsFolder,
                        WantsFormat = wantsFormat,
                        FoldersNotOffered = wantsFolder ? notOffered.Total - notOffered.Offered.Count : 0,
                        Snapshot = MeasureDefaultsPoint.Snapshot(measure),
                        Entry = MeasureDefaultsPoint.Entry(measure),
                        Questions = questions,
                        FolderCounts = folderCounts,
                        MoneyFormat = money?.Key,
                        MoneyFormatUses = money?.Count() ?? 0,
                        CountsRows = CountsRowsPattern.IsMatch(expression),
                        Divides = DividesPattern.IsMatch(expression),
                        MayFillWholeNumber = wantsFormat && FormatFill.MayFill(measure, "#,0"),
                        Digest = wantsFormat ? FormatFill.ComputeDigest(measure) : null,
                    });
                }
            }
            return prepared;
        }

        /// <summary>The catalog category behind each format family the engine can fill in on its own. Money
        /// is absent on purpose: it is filled from the model's own money format, never a catalog currency.</summary>
        private static readonly Dictionary<string, string> FamilyCategory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whole_number"] = "Whole numbers",
            ["decimal"] = "Decimals",
            ["percent"] = "Percentages",
            ["date_or_time"] = "Dates",
        };

        /// <summary>Turn one measure's answers into change lines, or into refusals. Pure: no model, no ask.</summary>
        internal static void Map(Ask ask, DecisionAnswers answers, double bar, IReadOnlyList<FormatTemplateInfo> templates,
                                 List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var itemId = JevFamily.ItemId(Name, ask.TableRef);

            if (ask.WantsFolder)
            {
                var answer = answers.For(MeasureDefaultsPoint.FolderQuestion);
                var folder = answer == null ? null : MeasureDefaultsPoint.FolderFromOptionId(answer.Choice);
                if (folder != null && !string.Equals(folder, ask.CurrentFolder, StringComparison.Ordinal))
                {
                    if (!ask.FolderCounts.TryGetValue(folder, out var uses))
                    {
                        refused.Add(new ModelPlanChangeRefusal
                        {
                            Target = ask.Ref,
                            Reason = "The folder chosen for it is not one this model uses, so it was left out.",
                        });
                    }
                    else
                    {
                        lines.Add(Line(ask, itemId, "set_display_folder", "displayFolder", "Put in the " + folder + " folder",
                            ask.CurrentFolder, folder, answer.Confidence, bar,
                            reason: folder + " already holds " + JevFamily.Count(uses, "item", "items")
                                  + " in this model, and this measure belongs with them.",
                            check: folder + " is a folder this model already uses."
                                 + (ask.FoldersNotOffered > 0
                                     ? " Jev was offered the " + MeasureDefaultsPoint.FolderCap + " folders most used with this table, and "
                                       + JevFamily.Count(ask.FoldersNotOffered, "other was", "others were") + " not offered."
                                     : ""),
                            folderLine: true));
                    }
                }
            }

            if (ask.WantsFormat)
            {
                var answer = answers.For(MeasureDefaultsPoint.FormatQuestion);
                var family = answer?.Choice;
                if (family == null || string.Equals(family, "text_or_none", StringComparison.Ordinal)) return;

                string format = null, reason = null, check = null;
                if (string.Equals(family, "currency", StringComparison.Ordinal))
                {
                    if (ask.MoneyFormat == null)
                    {
                        refused.Add(new ModelPlanChangeRefusal
                        {
                            Target = ask.Ref,
                            Reason = "It reads as money, but this model has no money format yet, so no currency was "
                                   + "picked for it. Choose one by hand.",
                        });
                        return;
                    }
                    format = ask.MoneyFormat;
                    reason = "It reads as an amount of money, and " + format + " is the money format this model already uses.";
                    check = format + " is already used by " + JevFamily.Count(ask.MoneyFormatUses, "measure", "measures")
                          + " in this model.";
                }
                else if (FamilyCategory.TryGetValue(family, out var category))
                {
                    format = JevFamily.LibraryFormat(templates, family, category);
                    if (format == null) return;
                    reason = FormatReason(family, ask);
                    check = format + " is the standard " + Lower(category) + " format in the format library, and the model accepts it.";
                }
                else return;

                var problem = FormatStringRules.Problem(format);
                if (problem != null)
                {
                    refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = problem });
                    return;
                }
                if (string.Equals(format, ask.CurrentFormat, StringComparison.Ordinal)) return;

                // THE FORMAT FILL RULE: only a line FormatFill proves may arrive approved. Anything else is a suggestion.
                var suggestion = !(ask.MayFillWholeNumber && FormatFill.IsWholeNumberFormat(format));
                var formatLine = Line(ask, itemId, "set_measure_format", "formatString", "Format as " + format,
                    ask.CurrentFormat, format, answer.Confidence, bar, reason, suggestion ? JevFormatSuggestion.Note : check,
                    suggestion);
                formatLine.WorkedOutDigest = ask.Digest;
                lines.Add(formatLine);
            }
        }

        private static string FormatReason(string family, Ask ask)
        {
            switch (family)
            {
                case "percent":
                    return "It reads as a share or a rate" + (ask.Divides ? ", and its formula divides one number by another" : "")
                         + ", so it shows out of 100.";
                case "whole_number":
                    return "It counts whole things" + (ask.CountsRows ? ", and its formula counts rows" : "")
                         + ", so it shows no decimal places.";
                case "decimal":
                    return "It is a measured amount that can have a fraction, so it shows two decimal places.";
                case "date_or_time":
                    return "It returns a date or a time, so it shows as a date.";
                default:
                    return "It reads as a number of this kind, so it uses the matching format.";
            }
        }

        private static ChangeItem Line(Ask ask, string itemId, string kind, string target, string title,
                                       string before, string after, double confidence, double bar,
                                       string reason, string check, bool suggestion = false, bool folderLine = false)
        {
            var word = JevFamily.ConfidenceWord(confidence, bar);
            // A FOLDER BELOW THE BAR ARRIVES UNTICKED (m11/jev-batch R2). The registry promises that under the bar the proposal is
            // "left out" (DecisionRegistry, measure.folder_and_format), and "likely" is under the bar: only a sure folder is
            // approved. A person still sees the line, with its word, and ticks it if it fits.
            var status = folderLine
                ? (string.Equals(word, JevFamily.Sure, StringComparison.Ordinal) ? "approved" : "rejected")
                : JevFormatSuggestion.Status(suggestion, string.Equals(word, JevFamily.Unsure, StringComparison.Ordinal), JevFamily.StatusFor(word));
            return new ChangeItem
            {
                ObjectRef = ask.Ref,
                ObjectName = ask.Name,
                Kind = kind,
                Source = JevFamily.Source,
                Category = "Formatting",
                Severity = "Info",
                Risk = suggestion ? "suggestion" : "safe",
                Target = target,
                Title = title,
                Rationale = JevFormatSuggestion.ReasonFor(suggestion, reason),
                Before = string.IsNullOrEmpty(before) ? "(none)" : before,
                After = after,
                Status = status,
                JevItem = itemId,
                DecisionPoint = DecisionRegistry.MeasureFolderAndFormat,
                DecisionConfidence = Math.Round(confidence, 4),
                DecisionWord = word,
                Reason = JevFormatSuggestion.ReasonFor(suggestion, reason),
                CheckText = check,
            };
        }

        private static string Capital(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Lower(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}
