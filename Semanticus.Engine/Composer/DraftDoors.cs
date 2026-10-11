// What the composer's doors need from a draft (slice 10, lanes U5 and L5a).
//
// THE MENU (composer contract 8, "below the bar, the menu"). Jev drafts steps from words only once its compose.draft
// replay clears the bar (lane L5b, still open: the key is refused). Until then a draft from words opens with no step
// chosen and the engine's own step templates listed, the ones the words name first. The match is plain word matching
// against a fixed list, written here; nothing is inferred and no service is asked, so the engine still runs no
// inference. A person (or an assistant, on the person's say) picks a step with edit_draft add; its arguments become
// "You choose" questions where the engine can list the options and parts to write where only a writer can.
//
// AFTER THE CHANGE. Try again reruns only what did not run after the change (the Sol F-269 follow-up); Keep it anyway
// closes a failed after-check without touching the model (composer contract 10.3).
//
// THE DEFERRED CODEX FINDINGS. A Semanticus question's default that names another answer is filled from that answer
// (F-278), and a blank that declares a generator is written by the engine's fixed template when it applies (F-277).
// Prepare-phase reads (F-276) run in DraftRunner, before the change.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.Composer
{
    /// <summary>The step templates a draft from words may pick (lane L5a). Engine-written titles over allowlisted ops.</summary>
    internal static class DraftMenu
    {
        internal sealed class Template
        {
            public string Id;
            public string Title;
            public string Op;
            public string[] Words;
            public (string Arg, string Value)[] Literals = Array.Empty<(string, string)>();
            /// <summary>The checks the step carries (lane author-gaps, C7): what the parked starter it replaces checked,
            /// in the draft's own kinds. "{step}" in a target or an 'of' is the step's id, so the check names the step's
            /// own questions.</summary>
            public ComposedCheck[] Checks = Array.Empty<ComposedCheck>();
            /// <summary>True when the step asks for an independently known number, optional, that its dax_probe check
            /// compares with (new-measure's "verificationValue", which could be declined).</summary>
            public bool Expected;
            /// <summary>The words of that question when they are not the measure's own (lane l9-finish).</summary>
            public string ExpectedQuestion;
            /// <summary>True when the step also asks, optionally, for an existing measure to probe through what it made
            /// (add-relationship's "target": a measure in a context that uses the new relationship).</summary>
            public bool ProbeMeasure;
        }

        /// <summary>The list, in the order it shows when the words name none of it. Every op is on the executable
        /// allowlist's change group (DraftDoorsTests holds it to that).</summary>
        internal static readonly Template[] All =
        {
            // The checks the parked starters carried (lane author-gaps, C7), so "the draft carries its own checks" is true:
            // new-measure validated and linted the formula, compared it with a number the person knew when they had one,
            // and ran the best practice rules on it; import-table checked the loaded rows; add-relationship relied on the
            // one side being a key.
            T("add-measure", "Add a measure", "create_measure", "measure", "total", "count", "average", "rate", "ratio", "kpi", "calculate")
                .Checking(true, C("valid_dax", "written"), C("dax_lint", "written"), C("dax_probe", "after", of: "{step}-expected"), C("bpa_clean", "after")),
            T("describe", "Describe an object", "set_description", "describe", "description", "document", "explain"),
            T("hide", "Hide an object", "set_column_hidden", "hide", "hidden", "invisible").With(("hidden", "true")),
            T("format", "Format a measure", "set_measure_format", "format", "percent", "currency", "decimal"),
            T("folder", "Put objects in a folder", "set_display_folder", "folder", "organise", "organize", "tidy"),
            T("rename", "Rename an object", "rename_object", "rename", "call it"),
            T("synonyms", "Add synonyms", "set_synonyms", "synonym", "copilot", "also called", "also known"),
            // Lane l9-finish: the parked add-relationship and import-table checked a number the person knew too
            // (dax_probe, "when: inputs.controlTotal.answered"), so these steps ask for one, optional, and check it only
            // when it is given. The parked sentence sends the person here, so it has to be true here.
            T("relate", "Relate two tables", "create_relationship", "relate", "relationship", "join", "link", "connect")
                .Checking(true, C("key_unique", "before", target: "{{questions.{step}-toColumnRef}}"),
                          C("dax_probe", "after", target: "{{questions.{step}-measure}}", of: "{step}-expected",
                            when: "inputs.{step}-expected.answered && inputs.{step}-measure.answered"))
                .Probing("A number an existing measure should give once the tables are related, if you know one"),
            T("date-table", "Mark the date table", "mark_date_table", "date table", "calendar"),
            T("data-type", "Set a column's data type", "set_column_data_type", "data type", "datatype", "type of"),
            T("summarize", "Set how a column sums up", "set_summarize_by", "summarize", "summarise", "aggregate", "sum up"),
            T("sort-by", "Sort a column by another", "set_sort_by_column", "sort", "order by"),
            T("category", "Set a column's data category", "set_data_category", "category", "geography", "web address", "image"),
            T("change-formula", "Change a measure's formula", "update_measure", "formula", "rewrite", "fix the", "update"),
            T("import", "Import a table", "create_import_table", "import", "load", "bring in", "source")
                .Checking(true, C("row_count", "after", of: "{step}-expected", when: "inputs.{step}-expected.answered"))
                .Asking("The row count the source reports for this table, if you know it"),
            T("add-column", "Add a column", "create_column", "add a column", "new column"),
            T("calc-group", "Add a calculation group", "create_calculation_group", "calculation group"),
            T("role", "Add a role", "create_role", "role", "security"),
        };

        internal static Template T(string id, string title, string op, params string[] words) =>
            new Template { Id = id, Title = title, Op = op, Words = words };

        private static Template With(this Template t, params (string, string)[] literals) { t.Literals = literals; return t; }

        private static Template Checking(this Template t, bool expected, params ComposedCheck[] checks) { t.Expected = expected; t.Checks = checks; return t; }

        private static Template Asking(this Template t, string question) { t.ExpectedQuestion = question; return t; }

        private static Template Probing(this Template t, string question) { t.ProbeMeasure = true; t.ExpectedQuestion = question; return t; }

        private static ComposedCheck C(string kind, string phase, string target = null, string of = null, string when = null) =>
            new ComposedCheck { Kind = kind, Phase = phase, Target = target, Of = of == null ? null : new[] { of }, When = when };

        /// <summary>A template's checks for one step: "{step}" becomes the step's id.</summary>
        internal static List<ComposedCheck> ChecksFor(Template t, string stepId) =>
            t.Checks.Select(c => new ComposedCheck
            {
                Kind = c.Kind, Phase = c.Phase, Min = c.Min, Max = c.Max,
                Target = c.Target?.Replace("{step}", stepId, StringComparison.Ordinal),
                Of = c.Of?.Select(o => o.Replace("{step}", stepId, StringComparison.Ordinal)).ToArray(),
                When = c.When?.Replace("{step}", stepId, StringComparison.Ordinal),
            }).ToList();

        internal static Template Find(string id) =>
            All.FirstOrDefault(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>How many of a template's keywords the words name, read the way <see cref="Best"/> reads them: whole
        /// words from the start of a word, then a slip of the fingers. The menu and Describe it share this one reading,
        /// so "separate" never names "rate" on one and "meaure" names measure on both.</summary>
        private static int Hits(Template t, string words) =>
            string.IsNullOrWhiteSpace(words) ? 0 : t.Words.Count(k => KeywordAt(words, k) >= 0);

        /// <summary>Where a keyword first appears in the words, or -1: whole-word from the start of a word, else a
        /// one-word keyword with a slip of the fingers (FuzzyWords), after the exact match has had its chance.</summary>
        private static int KeywordAt(string words, string keyword)
        {
            var m = PatternOf(keyword).Match(words);
            if (m.Success) return m.Index;
            // A word another step names exactly is that step's word, not a slip of this one: "calculation" in "calculation
            // group" is Add a calculation group, not a slip of Add a measure's "calculate" (m11/completeness verifier round 4,
            // L1; lost at the M11 join, where the menu matched Add a measure for "build a time intelligence calculation group").
            var at = FuzzyWords.FindNear(words, keyword);
            return at >= 0 && NamedExactly(words).Contains(at) ? -1 : at;
        }

        private static Regex PatternOf(string keyword) =>
            KeywordPatterns.GetOrAdd(keyword, k => new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(k), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        [ThreadStatic] private static string _namedWords;
        [ThreadStatic] private static HashSet<int> _named;

        /// <summary>Every place in the words where some step's keyword starts exactly. Worked out only when a slip was found, and
        /// kept for the last words read on this thread, because the menu and the word match read one text many times over.</summary>
        private static HashSet<int> NamedExactly(string words)
        {
            if (_named != null && ReferenceEquals(_namedWords, words)) return _named;
            var named = new HashSet<int>();
            foreach (var k in All.SelectMany(t => t.Words).Distinct(StringComparer.Ordinal))
                for (var m = PatternOf(k).Match(words); m.Success; m = m.NextMatch()) named.Add(m.Index);
            _namedWords = words;
            return _named = named;
        }

        // One pattern per keyword, made once. The static Regex.Match keeps only a few patterns, and the menu's keywords are
        // far more, so each match used to build its pattern again: a draft at the cap, matched word by word, took seconds.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> KeywordPatterns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>Whether <see cref="Best"/> picks <paramref name="t"/> for the clause. A clause that names none of its
        /// keywords cannot be its best, so only a clause that does is read against the whole list.</summary>
        internal static bool Names(string clause, Template t)
        {
            if (string.IsNullOrWhiteSpace(clause) || !t.Words.Any(k => KeywordAt(clause, k) >= 0)) return false;
            if (FullReadsForTest.Value != null) FullReadsForTest.Value.Value++;
            return Best(clause) == t;
        }

        /// <summary>Test seam: counts the clauses <see cref="Names"/> read against the whole list, in the calling test's own
        /// flow, so a test holds a menu add to reading only the kept words that name its step. Null in production.</summary>
        internal static readonly System.Threading.AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>> FullReadsForTest =
            new System.Threading.AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>>();

        /// <summary>The one template a short clause names, or null (the Workflows page's Describe it, which opens Author).
        /// Whole-word matching from the start of a word, so "rate" does not match "separate"; the most keywords wins,
        /// and a tie goes to the keyword that comes first, because a step's verb leads its clause ("describe the Sales
        /// measures" is Describe, not Add a measure). A one-word keyword also matches with a slip of the fingers
        /// (FuzzyWords: "meaure" is measure, Kane's UAT on 25 Sep), after an exact match has had its chance.</summary>
        internal static Template Best(string clause, IEnumerable<Template> list = null)
        {
            if (string.IsNullOrWhiteSpace(clause)) return null;
            Template best = null;
            int bestHits = 0, bestAt = int.MaxValue;
            foreach (var t in list ?? All)
            {
                int hits = 0, at = int.MaxValue;
                foreach (var k in t.Words)
                {
                    var index = KeywordAt(clause, k);
                    if (index < 0) continue;
                    hits++;
                    at = Math.Min(at, index);
                }
                if (hits > bestHits || (hits == bestHits && hits > 0 && at < bestAt)) { best = t; bestHits = hits; bestAt = at; }
            }
            return best;
        }

        /// <summary>The menu for these words: the templates the words name, most named first, and the newest words
        /// (Add more words) weighing more than the first; then the rest in list order, so a person can still pick one.
        /// A step that adds is named only by the clauses that ask to add (the adding rule, read over the whole words), so
        /// words that ask to take something away never put an add at the top (verifier on m11/sol-fixes-1 round 4, L1).</summary>
        internal static DraftMenuItem[] For(string words, string lastWords)
        {
            string asking = null, askingLast = null;
            int Score(Template t)
            {
                if (!AddingRule.Adds(t.Op)) return Hits(t, lastWords) * 10 + Hits(t, words);
                asking ??= DescribedSteps.AskingToAdd(words);
                askingLast ??= string.IsNullOrWhiteSpace(lastWords) ? "" : DescribedSteps.AskingToAdd(words, lastWords);
                return Hits(t, askingLast) * 10 + Hits(t, asking);
            }
            var scored = All.Select((t, i) => (t, i, score: Score(t))).ToList();
            // Under words that ask to take something away, the steps no word names list the ones that add last, so the
            // list's own first step (Add a measure) is not the one offered first either.
            var addsLast = DescribedSteps.TakesAway(words);
            return scored.Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.i)
                .Concat(scored.Where(x => x.score == 0).OrderBy(x => addsLast && AddingRule.Adds(x.t.Op) ? 1 : 0).ThenBy(x => x.i))
                .Select(x => new DraftMenuItem { Id = x.t.Id, Title = x.t.Title, Changes = DraftCopy.Changes(new[] { x.t.Op }), Matched = x.score > 0 })
                .ToArray();
        }

        /// <summary>The question a "pick:" argument asks, in the engine's words.</summary>
        internal static string QuestionFor(string arg, string enumerator) => arg switch
        {
            "fromColumnRef" => "Which column does it start from?",
            "toColumnRef" => "Which column does it point to?",
            "sortByColumnName" => "Sort by which column?",
            "dateColumn" => "Which date column?",
            _ => enumerator switch
            {
                "tables" => "Which table?",
                "columns" or "number_columns" => "Which column?",
                "measures" => "Which measure?",
                "objects" => "Which object?",
                "roles" => "Which role?",
                "relationships" => "Which relationship?",
                "partitions" => "Which partition?",
                "calculation_groups" => "Which calculation group?",
                "format_templates" => "Which format?",
                "data_categories" => "Which data category?",
                "source_columns" => "Which source column?",
                _ => Words(arg) + "?",
            },
        };

        /// <summary>"summarizeBy" as "Summarize by".</summary>
        internal static string Words(string arg)
        {
            var spaced = Regex.Replace(arg ?? "", "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant();
            return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
        }
    }
}

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        // ---- F-278: Semanticus answers filled from the answers they name ------------------------------------------

        /// <summary>A question Semanticus answers takes its default, with any input or answer it names filled in. A
        /// default that still names an answer nobody has given stays unanswered: the token itself is never an answer
        /// (Codex on pull request 395, F-278). An answer the person or the assistant gave is theirs and is kept.</summary>
        internal static void ResolveSemanticusAnswers(Draft d)
        {
            for (var pass = 0; pass <= d.Questions.Count; pass++)
            {
                var moved = false;
                foreach (var q in d.Questions.Where(q => q.By == "semanticus" && q.Default != null && q.AnsweredBy != "you" && q.AnsweredBy != "assistant"))
                {
                    var others = d.Questions.Where(x => x != q && x.Answer != null).GroupBy(x => x.Name)
                        .ToDictionary(g => g.Key, g => g.First().Answer, StringComparer.Ordinal);
                    var filled = FillAnswers(q.Default, others);
                    var answer = filled != null && filled.Contains("{{", StringComparison.Ordinal) ? null : filled;
                    if (string.Equals(answer, q.Answer, StringComparison.Ordinal)) continue;
                    q.Answer = answer;
                    q.AnsweredBy = answer == null ? null : "semanticus";
                    moved = true;
                }
                if (!moved) return;
            }
        }

        // ---- F-277: blanks a generator writes -----------------------------------------------------------------------

        /// <summary>A generator's blank waits for the answers it is written from; it is not sent to the assistant while
        /// one of them is still open, because the engine may yet write it.</summary>
        private static bool WaitsForItsAnswers(Draft d, DraftBlank b) =>
            b.Generator != null && (b.DependsOn ?? Array.Empty<string>())
                .Any(n => d.Questions.Any(q => q.Name == n && q.Required && q.Answer == null));

        /// <summary>Every open blank whose generator applies is written by Semanticus from its fixed template, checked like
        /// any other words, and never reaches the assistant (composer contract 1.5 and 2.3; Codex, F-277). One that does
        /// not apply is left as it was, for the assistant or the person.</summary>
        private async Task GenerateBlanksAsync(Draft d)
        {
            foreach (var st in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change))
                foreach (var b in st.Blanks.Where(b => b.Generator != null && b.State == DraftBlankStates.Empty && string.IsNullOrEmpty(b.RequestId)))
                {
                    var text = await GeneratedTextAsync(d, b).ConfigureAwait(false);
                    if (text == null) continue;
                    bool passed;
                    try { passed = ( await CheckDraftBlankAsync(ChangeOf(d), st.Id, b.Name, text).ConfigureAwait(false)).Passed; }
                    catch { continue; }
                    if (!passed) continue;
                    b.State = DraftBlankStates.Generated;
                    b.Text = text;
                    b.FilledBy = "semanticus";
                    b.Reason = null;
                    b.NeedsWhy = null;
                }
        }

        private static readonly Regex BracketColumn = new Regex(@"^'?(?<t>[^'\[]+)'?\[(?<c>[^\]]+)\]$", RegexOptions.CultureInvariant);

        /// <summary>The text a blank's generator writes, or null when it does not apply. starter_measure reads the
        /// answers the blank depends on for one column (a ref or Table[Column] that exists) and one kind of total from
        /// StarterMeasureTemplates. The other generators in the registry name ops a draft does not write a blank for
        /// yet, so they do not apply here and the blank stays with its writer.</summary>
        private async Task<string> GeneratedTextAsync(Draft d, DraftBlank b)
        {
            if (b.Generator != "starter_measure") return null;
            var answers = AnswersOf(d);
            var given = (b.DependsOn ?? Array.Empty<string>()).Select(n => answers.TryGetValue(n, out var v) ? v?.Trim() : null).Where(v => !string.IsNullOrEmpty(v)).ToList();
            var option = given.Select(v => v.ToLowerInvariant()).FirstOrDefault(v => StarterMeasureTemplates.MeasureOptions.Contains(v, StringComparer.Ordinal));
            string table = null, column = null;
            foreach (var v in given)
            {
                if (v.StartsWith("column:", StringComparison.Ordinal) && v.IndexOf('/') > 7)
                {
                    table = v.Substring(7, v.IndexOf('/') - 7); column = v.Substring(v.IndexOf('/') + 1); break;
                }
                var m = BracketColumn.Match(v);
                if (m.Success) { table = m.Groups["t"].Value.Trim(); column = m.Groups["c"].Value.Trim(); break; }
            }
            if (option == null || table == null) return null;
            bool exists;
            try { exists = await _sessions.Require().ReadAsync(md => md.Tables.Contains(table) && md.Tables[table].Columns.Contains(column)).ConfigureAwait(false); }
            catch { return null; }
            if (!exists) return null;
            return b.Fills == "name" ? StarterMeasureTemplates.Name(option, column)
                 : b.Fills == "expression" ? StarterMeasureTemplates.Dax(option, table, column) : null;
        }

        // ---- after the change: Try again and Keep it anyway --------------------------------------------------------

        private static bool LeftToTry(DraftAfterResult a) =>
            a.What != "read" && (a.Status == DraftAfterStatus.CouldNotRun || a.Status == DraftAfterStatus.CouldNotCheck || a.Status == DraftAfterStatus.NotRun);

        /// <summary>Try again (the Sol F-269 follow-up): the after steps that did not run and the after checks that could
        /// not check run again. What ran stays as it ran, and the change itself is never made a second time.</summary>
        private async Task<string> TryAgainAsync(Draft d)
        {
            if (d.Run?.Outcome != "applied" || !d.Run.After.Any(LeftToTry))
                throw new InvalidOperationException("Nothing is left to try again. Every step after the change ran and every check after it came out.");
            var reads = d.Run.After.Where(a => a.What == "read").ToList();
            var earlier = d.Run.After.Where(a => a.What != "read").ToList();
            d.Run.After = reads.Concat(await RunAfterAsync(d, earlier).ConfigureAwait(false)).ToList();
            d.Run.FinishedUtc = DateTimeOffset.UtcNow;
            var left = d.Run.After.Count(LeftToTry);
            return left == 0 ? "Tried again. Everything after the change has run."
                 : "Tried again. " + (left == 1 ? "1 step or check" : left.ToString(CultureInfo.InvariantCulture) + " steps or checks") + " still did not run.";
        }

        /// <summary>Keep it anyway on a failed after-check (composer contract 10.3): the person keeps the change as it is.
        /// Nothing in the model changes; the row reads Done and says the person kept it.</summary>
        private static string KeepAnyway(Draft d)
        {
            if (d.Run?.Outcome != "applied" || d.Run.KeptAnyway
                || !d.Run.After.Any(a => a.What == "check" && a.Status == DraftAfterStatus.Failed))
                throw new InvalidOperationException("No check failed after this change, so there is nothing to keep anyway.");
            d.Run.KeptAnyway = true;
            return "Kept. The change stays as it is.";
        }

        // ---- the menu (lane L5a) ------------------------------------------------------------------------------------

        private static void RequireWords(Draft d, string what)
        {
            if (d.Source != DraftSources.Words)
                throw new InvalidOperationException(what + " works on a draft from words. This one came from "
                    + (d.Source == DraftSources.Workflow ? "the workflow " + d.Workflow : "a list of steps") + ".");
        }

        /// <summary>Add more words: the new words are kept, and the menu lists again with them first. Steps already
        /// there stay as they are, kept or dropped. Once Jev drafts on the Plan, the NEW words are drafted by the per-clause
        /// rule as clauses of their own (D6b: new words typed without "and" or "then" never join the old words' last
        /// clause), and every step they name is added, even one of a kind already there (D7); a step Jev only leaned to
        /// arrives dropped, and an extra one (in a clause Jev was sure of something else) not when its kind is already
        /// there. Below the bar it is the menu, as before.</summary>
        private async Task<string> RefineAsync(DraftPlace p, Draft d, string text)
        {
            RequireWords(d, DraftCopy.AddMoreWords);
            var more = text?.Trim();
            if (string.IsNullOrEmpty(more)) throw new ArgumentException("Say what else it should do.", nameof(text));
            var before = d.Words;
            var words = ((before ?? "") + " " + more).Trim();
            PlanTasks.RequireWithinFullText(words, "a draft", nameof(text));
            d.Words = words;
            d.LastWords = more;
            var typed = d.WordsTyped ?? (string.IsNullOrWhiteSpace(before) ? new List<string>() : new List<string> { before.Trim() });
            typed.Add(more);
            d.WordsTyped = typed;
            if (!JevDraftsOnPlan) return "Listed the steps again with your new words. " + DraftCopy.PickTheSteps;
            // THE RULE (R3-D6b, the coordinator's of 27 Sep): every batch of typed words is kept until an answered ask
            // has read it. So the words asked are exactly the ones no answered ask has read yet (Draft.NotRead: clauses
            // past the limit, and words typed while Jev could not answer), then the new words; never words already read,
            // so never the first twelve clauses again. Each is cut from the next by a line break, which ends a clause.
            // An earlier clause the new words say again is asked once, as the new words'.
            // THE NEW WORDS ARE ALWAYS READ (verifier on m11/sol-fixes-1, D8): one ask reads MaxSteps clauses, so kept words
            // fill only the room the new words leave, oldest first, and the rest wait for the next Add more words, said so.
            // Kept first used to push the new words past the limit whenever twelve or more were kept, unread and unsaid.
            var freshClauses = DescribedSteps.ClausesOf(more, out _);
            var fresh = new HashSet<string>(freshClauses, StringComparer.OrdinalIgnoreCase);
            var kept = (d.NotRead ?? new List<string>()).Where(w => !fresh.Contains(w)).ToList();
            var room = Math.Max(0, DescribedSteps.MaxSteps - freshClauses.Count);
            var earlier = new List<string>();
            var earlierClauses = 0;
            foreach (var w in kept)
            {
                var clauses = DescribedSteps.ClausesOf(w, out _).Count;
                if (earlierClauses + clauses > room) break;
                earlier.Add(w);
                earlierClauses += clauses;
            }
            var waiting = kept.Skip(earlier.Count).ToList();
            // The adding rule (version 5) reads each clause with what comes after it: kept words re-read here have the later
            // typings after them, and words still waiting past the room are unread after them (K4).
            // Version 6 (A, B): the belt and Jev's last question read every typing so far, one text.
            var ask = await AskJevDraftByClauseAsync(string.Join("\n", earlier.Append(more)), d.Steps.SelectMany(s => s.Actions).Select(a => a.Op),
                earlierClauses, waiting.Count > 0, string.Join("\n", typed),
                d.Steps.Where(s => s.Kept && s.TickedFromWords != null).Select(s => s.TickedFromWords).ToList()).ConfigureAwait(false);
            // Still not read, in the order typed: the earlier words asked and not answered, the ones that waited, the new.
            var asked = new HashSet<string>(earlier, StringComparer.OrdinalIgnoreCase);
            var notRead = ask.Unread.Where(asked.Contains).Concat(waiting).Concat(ask.Unread.Where(w => !asked.Contains(w))).ToList();
            d.NotRead = notRead.Count > 0 ? notRead : null;
            d.NotReadWhy = d.NotRead == null ? null : ask.NotAsked;
            // A clause the adding rule left with no step is kept with its reason, beside those kept before (the adding rule).
            if (ask.NoStep.Count > 0) d.NoStep = (d.NoStep ?? new List<DraftWordsWithoutStep>()).Concat(ask.NoStep).ToList();
            if (ask.Unticked.Count > 0) d.Unticked = (d.Unticked ?? new List<DraftWordsWithoutStep>()).Concat(ask.Unticked).ToList();
            // A (version 6): every add already ticked from the words is read again with the new words, as one text.
            var retiered = await RetierEarlierAddsAsync(p, d, ask).ConfigureAwait(false);
            var drafted = ask.Drafted;
            // What was read and what waits is said only when an ask could leave. With none able to (no permission, the
            // point off), nothing was read and the next Add more words asks no one either: the reason and "Your words are
            // kept on the draft." are the whole truth (verifier on m11/sol-fixes-1 round 2, VR3).
            var leftOut = ask.NotAsked != null ? ""
                        : (ask.LeftOut > 0 ? " " + DraftCopy.PlanStepsLeftOut(ask.LeftOut, DescribedSteps.MaxSteps) : "")
                          + (waiting.Count > 0 ? " " + DraftCopy.EarlierWordsStillWaiting(waiting.Count) : "");
            // The draft says Jev was not sure only while the latest answered ask was not sure of a clause: a sure no, or a sure add the
            // adding rule set aside, is not unsure, and an answer that is sure clears what an earlier one said (Sol on m11/completeness round 4).
            if (drafted != null) d.JevNotSure = drafted.BelowBar && !drafted.SureOfEach;
            if (drafted == null || drafted.BelowBar)
                return "Listed the steps again with your new words. " + (d.JevNotSure ? DraftCopy.JevNotSure : DraftCopy.PickTheSteps)
                       + (drafted == null ? " " + (ask.NotAsked != null ? DraftCopy.WordsKeptNotAsked(ask.NotAsked) : DraftCopy.WordsKeptUnread) : "") + leftOut
                       + (retiered > 0 ? " " + DraftCopy.NotTicked(retiered) : "");
            int byJev = 0, byWords = 0, dropped = 0, fromEarlier = 0, notTicked = retiered;
            foreach (var x in drafted.Steps)
            {
                var step = AddTemplateStep(d, x.Template, x.Picked);
                if (x.Untick) { step.Kept = false; notTicked++; if (x.Note != null) x.Note.StepId = step.Id; continue; }
                if (!x.Sure) step.Kept = false;
                else
                {
                    if (AddingRule.Adds(x.Template.Op) && x.Clause >= 0 && x.Clause < ask.ClauseWords.Count) step.TickedFromWords = ask.ClauseWords[x.Clause];
                    if (x.FromWords) d.WordSteps++;
                }
                // Credit only the steps the new words gave (R3-D6b-copy); the earlier words' are said apart.
                if (x.Clause >= 0 && x.Clause < earlierClauses) { if (x.Sure) fromEarlier++; continue; }
                if (!x.Sure) dropped++;
                else if (x.FromWords) byWords++;
                else byJev++;
            }
            // A change step added after the steps that leave the model moves up to the other changes, and the steps are
            // numbered again; a step dropped together with another keeps pointing at it.
            d.Steps = d.Steps.OrderBy(s => s.Phase == DraftPhases.After ? 1 : 0).ToList();
            var renumber = d.Steps.Select((s, i) => (s.Number, New: i + 1)).ToDictionary(x => x.Number, x => x.New);
            foreach (var s in d.Steps)
            {
                s.Number = renumber[s.Number];
                if (s.DroppedWith is int with && renumber.TryGetValue(with, out var moved)) s.DroppedWith = moved;
            }
            CheckStepOrder(d);
            d.JevDrafted |= drafted.Steps.Any(x => x.Sure && !x.FromWords);
            // What was really added, counted whoever named it; a dropped step is said apart (D4, D4b); steps from words
            // typed earlier are said as theirs (R3-D6b-copy).
            var sentence = DraftCopy.MoreWordsAdded(byJev, byWords, dropped);
            if (fromEarlier > 0) sentence += " " + DraftCopy.EarlierWordsAdded(earlier.Where(w => !ask.Unread.Contains(w)).ToList(), fromEarlier);
            if (notTicked > 0) sentence += " " + DraftCopy.NotTicked(notTicked);
            return sentence + leftOut;
        }

        /// <summary>The adding rule, version 6, A: every add the engine ticked from the person's words in an earlier typing was read
        /// again with ALL the typings as one text by this typing's ask (K4 over what came after it, the belt over everything, then
        /// Jev's last question). One that no longer holds is unticked, with the person's words and one true line on why, like any add
        /// offered unticked; nothing already ticked is exempt except a step the person ticked or dropped by hand. Returns how many
        /// it unticked.</summary>
        private async Task<int> RetierEarlierAddsAsync(DraftPlace p, Draft d, ClauseAsk ask)
        {
            var pool = ask.Retier.ToList();
            var count = 0;
            foreach (var st in d.Steps.Where(s => s.Kept && s.TickedFromWords != null).ToList())
            {
                var at = pool.FindIndex(x => string.Equals(x.Clause, st.TickedFromWords, StringComparison.Ordinal));
                if (at < 0) continue;
                var r = pool[at].Reading;
                pool.RemoveAt(at);
                if (r.Tier == AddingRule.Tier.Kept) continue;
                var words = st.TickedFromWords;
                await KeepOrDropAsync(p, d, st, false).ConfigureAwait(false);
                d.Unticked = (d.Unticked ?? new List<DraftWordsWithoutStep>())
                    .Append(new DraftWordsWithoutStep { Words = words, Reason = r.Tier == AddingRule.Tier.Unticked ? r.Reason : r.TakesAway ? AddingRule.TakeAwayReason : AddingRule.NotAskedReason, StepId = st.Id }).ToList();
                count++;
            }
            return count;
        }

        /// <summary>Add a step from the menu. Each argument the engine can list becomes a "You choose" question with its
        /// options; each one only a writer can supply becomes a part to write; the op's own flags are set by the
        /// template. Nothing changes in the model.</summary>
        private async Task<string> AddMenuStepAsync(DraftPlace p, Draft d, string id)
        {
            RequireWords(d, "Adding a step");
            var t = DraftMenu.Find(id)
                ?? throw new ArgumentException("Pick one of the steps on the menu: " + string.Join(", ", DraftMenu.All.Select(x => x.Id)) + ".", nameof(id));
            var step = AddTemplateStep(d, t, null);
            CheckStepOrder(d);
            // Words the person resolved by picking their step by hand count as read (the coordinator's rule, R3-D6b), however
            // far down the kept words they are: a bound on the match left the ones past it to be asked again, and Jev then
            // added a second copy of the step (verifier on m11/sol-fixes-1 round 2, VR2). A draft at the cap keeps thousands,
            // so the match first asks whether a word names this step's keywords at all (DraftMenu.Names).
            if (d.NotRead != null)
            {
                d.NotRead = d.NotRead.Where(w => !DraftMenu.Names(w, t)).ToList();
                if (d.NotRead.Count == 0) { d.NotRead = null; d.NotReadWhy = null; }
            }
            await Task.CompletedTask.ConfigureAwait(false);
            var parts = step.Blanks.Count;
            var asks = d.Questions.Count(q => q.Step == step.Id);
            return "Added step " + step.Number.ToString(CultureInfo.InvariantCulture) + ", " + t.Title + "."
                   + (asks == 0 && parts == 0 ? "" : " It asks you " + (asks == 1 ? "1 question" : asks + " questions")
                      + " and has " + (parts == 1 ? "1 part" : parts + " parts") + " to write.");
        }

        /// <summary>One step from a template, the menu's and Jev's alike. Its phase is its op's group (a change, or a step
        /// after the change). Each argument the engine can list becomes a "You choose" question, answered already when Jev
        /// picked it surely from the options the engine listed (<paramref name="picked"/>); each one only a writer can
        /// supply becomes a part to write; the op's own flags are set by the template. Nothing changes in the model, and
        /// the caller checks the order.</summary>
        internal static DraftStep AddTemplateStep(Draft d, DraftMenu.Template t, IReadOnlyDictionary<string, DraftOption> picked)
        {
            var number = d.Steps.Count == 0 ? 1 : d.Steps.Max(s => s.Number) + 1;
            var stepId = t.Id;
            for (var n = 2; d.Steps.Any(s => s.Id == stepId); n++) stepId = t.Id + "-" + n.ToString(CultureInfo.InvariantCulture);
            var action = new ComposedAction { Op = t.Op };
            foreach (var (arg, value) in t.Literals) action.Args[arg] = value;
            var phase = OpParameterKinds.GroupOf(t.Op) == OpParameterKinds.After ? DraftPhases.After : DraftPhases.Change;
            var step = new DraftStep { Id = stepId, Number = number, Title = t.Title, Phase = phase, Actions = new List<ComposedAction> { action } };
            foreach (var prm in OpParameterKinds.Parameters(t.Op) ?? Array.Empty<OpParameterKinds.OpParameter>())
            {
                if (!prm.Required || action.Args.ContainsKey(prm.Name)) continue;
                var kind = OpParameterKinds.KindOf(t.Op, prm.Name) ?? "";
                DraftQuestion q = null;
                if (kind.StartsWith("pick:", StringComparison.Ordinal))
                {
                    var from = kind.Substring(5);
                    q = new DraftQuestion { Question = DraftMenu.QuestionFor(prm.Name, from), From = from,
                        Type = from == "format_templates" || from == "data_categories" ? "text" : "objectRef" };
                }
                else if (kind.StartsWith("enum:", StringComparison.Ordinal))
                    q = new DraftQuestion { Question = DraftMenu.Words(prm.Name) + "?", Type = "enum", Values = kind.Substring(5).Split('|') };
                else if (kind == "flag")
                    q = new DraftQuestion { Question = DraftMenu.Words(prm.Name) + "?", Type = "enum", Values = new[] { "true", "false" } };
                else if (kind == "number")
                    q = new DraftQuestion { Question = DraftMenu.Words(prm.Name) + "?", Type = "number" };
                if (q == null) continue;   // authored text becomes a part to write below; internal is Semanticus's own
                q.Name = stepId + "-" + prm.Name;
                q.By = "you";
                q.Required = true;
                q.Step = stepId;
                // Jev's sure pick, and only an option the engine listed: the answer is the engine's own ref.
                if (picked != null && picked.TryGetValue(prm.Name, out var option) && option?.Ref != null)
                {
                    q.Answer = option.Ref;
                    q.AnsweredBy = "jev";
                }
                d.Questions.Add(q);
                action.Args[prm.Name] = "{{questions." + q.Name + "}}";
            }
            if (t.ProbeMeasure)
                d.Questions.Add(new DraftQuestion
                {
                    Name = stepId + "-measure", Question = "An existing measure to check through it, if you want one checked", Type = "objectRef",
                    From = "measures", By = "you", Required = false, Step = stepId,
                });
            if (t.Expected)
                d.Questions.Add(new DraftQuestion
                {
                    Name = stepId + "-expected", Question = t.ExpectedQuestion ?? "A number you know it should give, if you have one", Type = "number",
                    By = "you", Required = false, Step = stepId,
                });
            step.Checks = DraftMenu.ChecksFor(t, stepId);
            FinishStep(step);
            d.Steps.Add(step);
            return step;
        }

        // ---- what a view carries for the doors ---------------------------------------------------------------------

        /// <summary>The options of every question that lists from the model (composer contract 1.4 and 2.2), read when
        /// the view is built. Lists that live outside the model (the source, the service) carry none here.</summary>
        private async Task FillOptionsAsync(Draft d, DraftView view)
        {
            if (view?.Questions == null || view.Questions.Length == 0) return;
            var wanted = d.Questions.Where(q => q.From != null).ToDictionary(q => q.Name, q => q.From, StringComparer.Ordinal);
            if (wanted.Count == 0) return;
            try
            {
                await _sessions.Require().ReadAsync(m =>
                {
                    foreach (var qv in view.Questions)
                        if (wanted.TryGetValue(qv.Name, out var from)) qv.Options = OptionsOf(m, from);
                    return true;
                }).ConfigureAwait(false);
            }
            catch { /* no model open: the question asks without a list */ }
        }

        private const int MaxOptions = 300;

        private static DraftOption[] OptionsOf(Model m, string from, int max = MaxOptions)
        {
            var colon = from.IndexOf(':');
            var name = colon < 0 ? from : from.Substring(0, colon);
            var arg = colon < 0 ? null : from.Substring(colon + 1);
            var tableName = arg != null && arg.StartsWith("table:", StringComparison.Ordinal) ? arg.Substring(6) : arg;
            IEnumerable<Table> tables = tableName == null ? m.Tables : m.Tables.Where(t => t.Name == tableName);
            static DraftOption O(ITabularObject o, string label) => new DraftOption { Ref = ObjectRefs.For(o), Label = label };
            IEnumerable<DraftOption> list = name switch
            {
                "tables" => m.Tables.Select(t => O(t, t.Name)),
                "columns" => tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber)).Select(c => O(c, c.Table.Name + "[" + c.Name + "]")),
                "number_columns" => tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber
                        && (c.DataType == DataType.Int64 || c.DataType == DataType.Decimal || c.DataType == DataType.Double)))
                    .Select(c => O(c, c.Table.Name + "[" + c.Name + "]")),
                "measures" => m.AllMeasures.Select(x => O(x, x.Name)),
                "objects" => m.Tables.Select(t => O(t, t.Name))
                    .Concat(m.Tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber)).Select(c => O(c, c.Table.Name + "[" + c.Name + "]")))
                    .Concat(m.AllMeasures.Select(x => O(x, x.Name))),
                "partitions" => tables.SelectMany(t => t.Partitions).Select(x => O(x, x.Table.Name + ", " + x.Name)),
                // A role and a relationship are named by their names on the ops that take them.
                "roles" => m.Roles.Select(r => new DraftOption { Ref = r.Name, Label = r.Name }),
                "relationships" => m.Relationships.Select(r => new DraftOption { Ref = r.Name, Label = r.Name }),
                _ => null,
            };
            return list?.Take(max).ToArray();
        }
    }
}
