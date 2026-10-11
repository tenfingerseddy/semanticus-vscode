// The box: one thing typed, one place to go.
//
// Three rules shape this file, all from docs/redesign/jev-plan.md M2.
//
//   Routing only navigates. Nothing here writes, and nothing here runs an operation. The most it does is
//   name a page. That is why it can be asked on a person's free-typed words at all.
//
//   A sentence answer only for questions the state answers deterministically. The engine answers "how big is
//   this model" from its own numbers or it does not answer at all. No sentence on this surface ever comes
//   from a decision service: the service answers three typed shapes and never free text (DecisionContract.cs),
//   so a sentence it wrote could not exist.
//
//   Below the bar, the menu. A wrong door costs a person more than no door, so an unsure route is reported as
//   a fallback to the menu with no tool named.
//
// Criteria wording follows Decisions/MeasureDefaultsPoint.cs: a criterion describes the world. Here the world
// is what each place in the product is FOR, in its own help words, and nothing about what the person opened
// last or what the product would prefer them to click.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine
{
    /// <summary>What the box answers with.</summary>
    public sealed class AskRouteResult
    {
        /// <summary>The tool id to open, or null when nobody is being sent anywhere.</summary>
        public string Tool { get; set; }

        /// <summary>The area that tool lives in, or null with <see cref="Tool"/>.</summary>
        public string Area { get; set; }

        /// <summary>How sure the TOP reading is, from 0 to 1. One when the words or the engine's own state decided it
        /// without asking anything, the service's own number when the service placed the line on a page, and zero
        /// when nothing placed it or when the top reading is a task the words name no page for. Where the line has
        /// two readings, the second one's number is <see cref="AskReading.Confidence"/> on <see cref="Other"/>, so a
        /// line the service placed on Ask your data that reads task first carries the service's number there.</summary>
        public double Confidence { get; set; }

        /// <summary>How sure a route had to be before it was taken.</summary>
        public double ConfidenceBar { get; set; }

        /// <summary>A plain answer to the question, when the engine's own state answers it. Null otherwise, and
        /// null is the ordinary case. Never written by a decision service.</summary>
        public string Sentence { get; set; }

        /// <summary>"menu" when the person should be shown the list of places instead of being sent to one.
        /// Null when a place is named, and null in ONE case where none is: a task reading that names no page but
        /// carries <see cref="Other"/>, where the second choice is what to show and the menu would bury it. Set on
        /// every other path that names no tool, so a caller never has to work out what to do with an empty answer.</summary>
        public string Fallback { get; set; }

        /// <summary>Why no place was named, in plain words. Null when one was.</summary>
        public string Reason { get; set; }

        /// <summary>True when a service was actually asked. False means the route was decided by the engine or
        /// was skipped, and <see cref="Reason"/> says which.</summary>
        public bool Asked { get; set; }

        /// <summary>The thing the person asked to make, when they asked to make one: "measure", "calendar",
        /// "role". Null on every other answer, which is nearly all of them.
        ///
        /// It names WHAT, never where the form is. The page turns it into a door (askbox.tsx), because which
        /// panel a create form lives in is a webview fact and this engine ships without the webview.</summary>
        public string Create { get; set; }

        /// <summary>The model version that answered, when one did.</summary>
        public string ServedModelVersion { get; set; }

        /// <summary>The OTHER way these words can be read, when they can be read two ways. Null on every answer that
        /// has only one reading, which is nearly all of them.
        ///
        /// It exists because the same line can be a task and a question, and five rounds of trying to decide which
        /// with a word list failed in both directions (coordinator, 28 September). Nothing is taken away now: the
        /// reading that fits best is on this result, the other one is here, and the person picks. The fields above
        /// are still the TOP reading, so every caller that knew nothing about this keeps working.</summary>
        public AskReading Other { get; set; }

        /// <summary>Which kind the TOP reading is, "task" or "question". Null on an answer with only one reading,
        /// which is the same thing as <see cref="Other"/> being null and is said twice on purpose: a caller holding
        /// either field can always tell which reading it has.</summary>
        public string Reading { get; set; }
    }

    /// <summary>One way a line can be read, and where that reading is answered.
    ///
    /// Two kinds and no more: "task", the words describe work on the model, which the Plan keeps as a row; and
    /// "question", the words ask about the numbers, which Ask your data answers. <see cref="Tool"/> is null on a task
    /// reading whose words name no page, which is the ordinary case for a task and is not a failure: the Plan still
    /// keeps the row.</summary>
    public sealed class AskReading
    {
        /// <summary>"task" or "question".</summary>
        public string Kind { get; set; }

        /// <summary>The place this reading is answered on, or null when the words name none.</summary>
        public string Tool { get; set; }

        /// <summary>The area that tool lives in, or null with <see cref="Tool"/>.</summary>
        public string Area { get; set; }

        /// <summary>How sure this reading is, on the same scale as the result's own: 1 where the words decided it,
        /// the service's own number where the service did, 0 where nothing placed it.</summary>
        public double Confidence { get; set; }

        /// <summary>The one line a person reads for this choice.</summary>
        public string Line { get; set; }
    }

    /// <summary>The handful of counts the engine can answer a question from without asking anything. Every one
    /// is nullable, and a null means "not known here", which is reported as not knowing rather than as zero.</summary>
    public sealed class AskDeterministicFacts
    {
        public string ModelName { get; set; }
        public int? TableCount { get; set; }
        public int? MeasureCount { get; set; }
        public int? ColumnCount { get; set; }

        /// <summary>The model's size in bytes, when a size reading exists. Null on a model that has never been
        /// measured, which is most of them, and the answer then says so.</summary>
        public long? SizeBytes { get; set; }

        /// <summary>Edits made in this session that are not saved yet.</summary>
        public int? UnsavedChangeCount { get; set; }

        /// <summary>Saved checks in this model's suite. NULL means the suite was not read, which is not the
        /// same as reading it and finding none: the read failed, or saved checks are Pro and this is a free
        /// session. The answer must carry that difference through rather than rounding it to zero.</summary>
        public int? SavedCheckCount { get; set; }
    }

    /// <summary>What the first pass worked out: the winning tool, what it scored, and what the runner-up
    /// scored. The two scores travel together because "clear winner" is a statement about BOTH of them, and a
    /// test that can only see the winner cannot tell a confident match from a coin toss.</summary>
    public sealed class AskWordMatch
    {
        public string Tool { get; set; }
        public string Create { get; set; }
        public int Score { get; set; }
        public int RunnerUp { get; set; }
    }

    public static class AskRoutePoint
    {
        public const string ToolQuestion = "tool";

        /// <summary>The line a person reads when neither the words nor the service found them a place.
        ///
        /// It is the ONLY thing shown in that case. The gate's own reason ("you have not given permission to
        /// send this model's details for advice") goes in Reason, for the assistant door and the logs, and
        /// never becomes the answer: a person who typed "create a measure" and was handed a sentence about
        /// permission was told about our plumbing instead of being answered (Kane, 21 September).</summary>
        public const string NoMatchSentence =
            "Nothing here matches those words yet. Try a page name, or the name of a thing in the model.";

        /// <summary>The longest ask this point may send. A refusal, not a trim, for the same reason the measure
        /// point refuses a long formula: a cut ask is a different question wearing the same digest inputs.</summary>
        public const int MaxAskChars = 400;

        /// <summary>The declared snapshot: the words typed, and nothing else. No model, no names, no numbers.</summary>
        public static IReadOnlyDictionary<string, object> Snapshot(string text)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.AskRoute);
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("ask_text", text ?? string.Empty),
            });
        }

        /// <summary>The one question: which place, or none. The options are in catalogue order,
        /// which is fixed, because the order is part of the request digest.</summary>
        public static IReadOnlyList<DecisionQuestion> Questions()
        {
            var options = AskRouteCatalog.Tools
                .Select(t => new DecisionOption(t.Id, t.Criterion))
                .ToList();

            options.Add(new DecisionOption(AskRouteCatalog.NoTool,
                "the question is not about any of the places above, or it is not about working on a model at all"));

            return new[]
            {
                new DecisionQuestion(ToolQuestion, DecisionQuestionKind.Choice,
                    "Which of these places answers what the person typed?", options),
            };
        }

        /// <summary>The question about a new model, asked only of an ask the words could not settle (AskMakeIntent.Unsure) and always
        /// in the same request as <see cref="Questions"/>, so the service is called once. It is typed: three options, no free text.</summary>
        public const string IntentQuestion = "model_intent";
        public const string IntentNewModel = "new_model";
        public const string IntentThisModel = "this_model";
        public const string IntentNeither = "neither";

        public static IReadOnlyList<DecisionQuestion> IntentQuestions() => new[]
        {
            new DecisionQuestion(IntentQuestion, DecisionQuestionKind.Choice,
                "Is the person asking for a new semantic model to be made, or for something to be done with a model that already exists?",
                new[]
                {
                    new DecisionOption(IntentNewModel, "the person is asking for a new semantic model to be made from a data source they name, such as a SQL database, a warehouse, an endpoint or a lakehouse"),
                    new DecisionOption(IntentThisModel, "the person is asking about, or asking for a change to, a model that already exists"),
                    new DecisionOption(IntentNeither, "the person is not asking for a model to be made and is not asking about one, or it cannot be told which"),
                }),
        };

        /// <summary>True when the service said the words ask for a new model, above the bar. Anything else, including an unknown id, is no.</summary>
        public static bool ProposedNewModel(DecisionAnswers answers, double bar, out double confidence)
        {
            confidence = 0;
            var answer = answers?.For(IntentQuestion);
            if (answer == null) return false;
            confidence = answer.Confidence;
            return answer.Confidence >= bar && string.Equals(answer.Choice, IntentNewModel, StringComparison.Ordinal);
        }

        /// <summary>Read the route out of an answer set, but only above the bar and only when the id is one this
        /// build knows. An unknown id is dropped rather than passed on: naming a place that does not exist would
        /// send a person nowhere and look like a bug in the page.</summary>
        public static string ProposedTool(DecisionAnswers answers, double bar, out double confidence)
        {
            confidence = 0;
            var answer = answers?.For(ToolQuestion);
            if (answer == null) return null;
            confidence = answer.Confidence;
            if (answer.Confidence < bar) return null;
            if (string.Equals(answer.Choice, AskRouteCatalog.NoTool, StringComparison.Ordinal)) return null;
            return AskRouteCatalog.Find(answer.Choice) == null ? null : answer.Choice;
        }

        /// <summary>The three questions the engine's own state answers, and the place each one belongs to.
        ///
        /// Deliberately narrow. Each family is a fixed set of phrasings, so what is matched can be read off this
        /// file and replayed, and a question that is nearly one of these is routed like anything else rather
        /// than answered with a number that might be about something else. Returns null when the text is not one
        /// of them, which is the ordinary case.</summary>
        public static AskRouteResult AnswerFromState(string text, AskDeterministicFacts facts)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = Normalise(text);

            if (Matches(t, "how big is this model", "how big is the model", "model size", "how big", "size of this model", "how much memory"))
                return Answered("stats", SizeSentence(facts));

            if (Matches(t, "what changed", "what has changed", "what did i change", "what have i changed", "what changed today", "any unsaved changes", "do i have unsaved changes"))
                return Answered("history", ChangeSentence(facts));

            if (Matches(t, "how many checks", "how many tests", "how many checks are there", "how many tests are there", "how many saved checks"))
                return Answered("tests", CheckSentence(facts));

            // How many measures, how many tables. It names NO place on purpose, unlike the three above: the
            // count is the whole answer, the box that asked it is on the Overview, and an "Open Overview"
            // button on the Overview is a door back into the room you are standing in.
            if (Matches(t, "how many measures", "how many measures are there", "how many measures does this model have",
                           "how many tables", "how many tables are there", "how many tables does this model have",
                           "how many measures and tables", "what is in this model", "what is in the model"))
                return Told(ShapeSentence(facts));

            return null;
        }

        /// <summary>The first pass: plain words, no service, no permission, nothing leaving the machine.
        ///
        /// WHY IT EXISTS. Everything that was not one of the fixed phrasings above used to go straight to the
        /// decision service, so on a session with no permission the answer to "create a measure" was the
        /// consent refusal and nothing else. Routing a person to a page they named in their own words needs no
        /// model, no service and no permission, and doing it here means the common asks are answered instantly
        /// and for free.
        ///
        /// HOW IT SCORES. A noun is worth 2, a verb 1 and a RELATION 4, a phrase is worth one more than a
        /// single word, and a tool matching both a noun and a verb takes a further 2. That last bonus is the
        /// whole point of splitting the lists: it puts one noun plus one verb (5) above two nouns (4), so
        /// "what breaks if I rename Total Sales" lands on Lineage while "rename Total Sales" lands on Find
        /// and replace. A relation counts as a verb for that bonus, because it is one.
        ///
        /// WHEN IT ANSWERS. Only on a clear winner: at least 2, and strictly ahead of the runner-up. A tie is
        /// not a near miss to be broken by a rule of thumb, it is two plausible doors, and the second pass (or
        /// the menu) is the honest answer to that.
        ///
        /// THEN THE SHAPE OF A DATA QUESTION. Words no page claims but that split a number by something ("units
        /// sold per brand last year", "customer count by city", "revenue for each country in 2024") are a question
        /// about the numbers, and Ask your data is where those are answered. Without this, a person who had not
        /// given permission read "Nothing here matches" for half the data questions (verify-askdata-r3: 136 of the
        /// 302 labelled ones). Returns null when neither places the words.
        ///
        /// A QUESTION'S SHAPE BEATS THE DATA PAGE. Data browses sample rows and answers no question, and its noun
        /// "value" won "average order value per month in 2024?". When the catalogue's winner is Data but the words
        /// split a number or ask for an amount, the question goes to Ask your data; "show me the rows in Sales" still
        /// goes to Data.
        ///
        /// NOTHING IS HELD BACK FROM ASK YOUR DATA ANY MORE (coordinator, 28 September, fix-askdata-r6). Five rounds
        /// tried to tell a task from a question with a word list here, and it failed in both directions: business-style
        /// tasks ("Totals per region are double counting") still reached Ask your data, and plainly-worded data
        /// questions ("please show me net sales by region", "net sales broken down by region") never did, because
        /// "please", "need" and "broken" were read as a task. So this pass no longer hides a reading. It says only
        /// what the words place, and <see cref="Readings"/> turns that into the two choices a person picks between.</summary>
        public static AskWordMatch MatchWords(string text)
        {
            var catalog = MatchCatalog(text);
            if (catalog != null && string.Equals(catalog.Tool, AskRouteCatalog.DataTool, StringComparison.Ordinal))
                return catalog;
            if (catalog != null && string.Equals(catalog.Tool, AskRouteCatalog.RowsTool, StringComparison.Ordinal)
                && (DataQuestionMatch(text) != null || AsksForAnAmount(text)))
                return new AskWordMatch { Tool = AskRouteCatalog.DataTool, Score = catalog.Score, RunnerUp = catalog.RunnerUp };
            return catalog ?? DataQuestionMatch(text);
        }

        /// <summary>The catalogue alone: the pages' own words, and nothing inferred from the shape of the words. What
        /// a plan target uses to say where it is done by hand (PlanTargets.JumpFor), because a target is a task,
        /// and a task that happens to say "by" ("Month Name should sort by Month Number") is not a data question.
        /// Null when there is no clear winner.</summary>
        public static AskWordMatch MatchCatalog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var words = Blank(Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Stem).ToArray());
            if (words.Length == 0) return null;

            var best = (Tool: (string)null, Score: 0);
            var runnerUp = 0;

            // A request to make a model from a source is the Model Spec's, whatever else the words mention: "connect to SQL and
            // create a model" also says "connect", and Diagram is what that word alone finds (AskMakeIntent).
            if (AskMakeIntent.Classify(text) == MakeIntent.Yes)
                return new AskWordMatch { Tool = ModelSpecTool, Score = MakeIntentWorth, RunnerUp = 0, Create = CreateThing(words, ModelSpecTool) };

            foreach (var tool in AskRouteCatalog.Tools)
            {
                var nouns = tool.Words.Nouns.Sum(w => Hit(words, w, NounWorth));
                var verbs = tool.Words.Verbs.Sum(w => Hit(words, w, VerbWorth))
                          + tool.Words.Relations.Sum(w => Hit(words, w, RelationWorth));
                if (nouns == 0 && verbs == 0) continue;
                var score = nouns + verbs + (nouns > 0 && verbs > 0 ? 2 : 0);
                if (score > best.Score) { runnerUp = best.Score; best = (tool.Id, score); }
                else if (score > runnerUp) runnerUp = score;
            }

            if (best.Tool == null || best.Score < 2 || best.Score <= runnerUp) return null;

            return new AskWordMatch
            {
                Tool = best.Tool,
                Score = best.Score,
                RunnerUp = runnerUp,
                Create = CreateThing(words, best.Tool),
            };
        }

        /// <summary>The thing being made, when a create verb and exactly one known thing are both present and
        /// that thing belongs to the page that won. Anything less is null: "add a note about the sales role"
        /// names two things and a guess between them would be worse than no second sentence at all.
        ///
        /// A LONGER NAME BEATS THE SHORTER ONE INSIDE IT. "create a calculated table" names two things by the
        /// letter of the list, "calculated table" and "table", and they are not two candidates: one of them is
        /// a word inside the other. The most words matched wins, and only a single winner at that length
        /// counts, so a real pair of things still answers with nothing.</summary>
        private static string CreateThing(string[] words, string tool)
        {
            if (!AskRouteCatalog.CreateVerbs.Any(v => Hit(words, v, VerbWorth) > 0)) return null;

            var named = AskRouteCatalog.CreateThings
                .Select(thing => new
                {
                    Thing = thing,
                    Length = thing.Nouns.Where(n => Hit(words, n, NounWorth) > 0)
                        .Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                        .DefaultIfEmpty(0).Max(),
                })
                .Where(x => x.Length > 0)
                .ToList();
            if (named.Count == 0) return null;

            var longest = named.Max(x => x.Length);
            var best = named.Where(x => x.Length == longest).ToList();
            if (best.Count != 1) return null;
            return string.Equals(best[0].Thing.Tool, tool, StringComparison.Ordinal) ? best[0].Thing.Name : null;
        }

        /// <summary>The words split a number by something: "by", "per", "for each", or a comparison ("vs", "versus"),
        /// with words on both sides, and "by default" is never a split. Deliberately a SHAPE, not a verdict on what
        /// the person meant: it only runs once no page's own words won, and it only ever adds Ask your data as one of
        /// two readings (Readings), because "Sort MonthName by MonthNumber" has the same shape as "units by brand".</summary>
        public static AskWordMatch DataQuestionMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var words = Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < words.Length - 1; i++)
            {
                var w = words[i];
                var split = w == "per" || w == "vs" || w == "versus"
                    || (w == "by" && words[i + 1] != "default")
                    || (w == "each" && words[i - 1] == "for" && i > 1);
                if (split) return new AskWordMatch { Tool = AskRouteCatalog.DataTool, Score = NounWorth };
            }
            return null;
        }

        /// <summary>WHICH READING IS SHOWN FIRST, and nothing more than that (coordinator, 28 September).
        ///
        /// Both readings are always offered, so being wrong here costs a person one glance at the second choice; it
        /// can no longer lose a task or hide Ask your data. That is the whole reason a plain word rule is honest here
        /// and was not honest as a gate: five rounds proved a word list cannot know every way a person writes a task,
        /// and a list that HIDES a reading turns each miss into a dead end.
        ///
        /// THE RULE IS THE RULING'S OWN, WORD FOR WORD: Ask your data is the first choice only for a line that plainly
        /// asks (<see cref="AsksAQuestion"/>). Every other line reads the task first, and Ask your data is the named
        /// second choice. Round 6 also put the question first where the words or the service placed a line as a
        /// question and a list of telling words found nothing, and where one of Ask your data's phrases sat anywhere
        /// in the line. Both bent the ruling: "Units per order would be handy on the dashboard" tells with no word on
        /// that list, and "Net sales broken down by region is wrong for March" uses the page's phrase to complain
        /// (verify-askdata-r6 F3). A bare data question ("total sales by region") now reads the task first too, which
        /// is the ruling's price: it costs that line one glance, and it never costs a task its place.</summary>
        public static bool QuestionReadFirst(string text) => AsksAQuestion(text);

        /// <summary>The line PLAINLY ASKS, in the ruling's own terms and nothing wider:
        ///   IT ENDS IN A QUESTION MARK.
        ///   IT OPENS WITH what, which, how, why, when, who or where (and "whats", "whose", "whom", the same words).
        ///   IT OPENS WITH "show me" OR "list".
        ///   IT OPENS BY ASKING FOR ASK YOUR DATA BY NAME ("ask your data please", "I need ask your data", "go to ask
        ///   your data"). The person asked for the page itself, which is the plainest ask there is; it is the one
        ///   addition to the ruling's list, and route_ask's description names it. Only as the opening: a complaint that
        ///   mentions the page ("Totals in Ask your data are wrong") does not ask (Sol on fix-askdata-final).
        /// The opening is read after the words that only soften a start ("please", "can you", "could we"), so
        /// "please show me net sales by region" opens with "show me". A phrase anywhere else in the line is never an
        /// opening: "Units split by channel don't add up" does not ask.</summary>
        public static bool AsksAQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.TrimEnd().EndsWith("?", StringComparison.Ordinal)) return true;
            var words = Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            var i = 0;
            while (i < words.Length)
            {
                var soft = SoftStarts.FirstOrDefault(s => StartsWith(words, i, s));
                if (soft == null) break;
                i += soft.Length;
            }
            if (i < words.Length && QuestionWords.Contains(words[i])) return true;
            if (AskingStarts.Any(a => StartsWith(words, i, a))) return true;
            var go = PageOpeners.FirstOrDefault(o => StartsWith(words, i, o));
            return StartsWith(words, i + (go?.Length ?? 0), PageName);
        }

        /// <summary>The words a question opens with, as the ruling lists them. Single words only.</summary>
        internal static readonly HashSet<string> QuestionWords = new HashSet<string>(new[]
        {
            "what", "whats", "which", "how", "why", "when", "who", "whose", "whom", "where",
        }, StringComparer.Ordinal);

        /// <summary>The two openings the ruling lists that are not question words: "show me" and "list". Not "show"
        /// alone ("Show Margin % by brand as a percentage" is a format task), not "see", "tell me", "compare", "break
        /// down" or "give me": those open tasks as often as questions (verify-askdata-r6 F3).</summary>
        internal static readonly string[][] AskingStarts =
        {
            new[] { "show", "me" }, new[] { "list" },
        };

        /// <summary>Ask your data's own name.</summary>
        private static readonly string[] PageName = { "ask", "your", "data" };

        /// <summary>What may come before the page's name when a line opens by asking for the page.</summary>
        private static readonly string[][] PageOpeners =
        {
            new[] { "open" }, new[] { "go", "to" }, new[] { "take", "me", "to" }, new[] { "use" }, new[] { "i", "need" },
            new[] { "i", "want" },
        };

        /// <summary>Words that only soften the start of a clause, passed over before the opening is read. Courtesy and
        /// joining words only: nothing here can make a line ask, it only lets "please list" and "can you show me" be
        /// read by their real opening.</summary>
        private static readonly string[][] SoftStarts =
        {
            new[] { "please" }, new[] { "now" }, new[] { "next" }, new[] { "then" }, new[] { "also" }, new[] { "just" },
            new[] { "and" },
            new[] { "can", "you" }, new[] { "could", "you" }, new[] { "would", "you" }, new[] { "will", "you" },
            new[] { "can", "we" }, new[] { "could", "we" }, new[] { "would", "we" },
        };

        private static bool StartsWith(string[] words, int at, string[] phrase)
        {
            if (at + phrase.Length > words.Length) return false;
            for (var j = 0; j < phrase.Length; j++)
                if (!string.Equals(words[at + j], phrase[j], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>The words that make a line a question about the numbers even with no split in it: an amount
        /// asked for ("how many", "how much", "total", "sum of", "average", "count of", "number of").</summary>
        private static readonly string[][] AmountWords =
        {
            new[] { "how", "many" }, new[] { "how", "much" }, new[] { "total" }, new[] { "sum", "of" },
            new[] { "average" }, new[] { "count", "of" }, new[] { "number", "of" },
        };

        /// <summary>The words ask for an amount: "how many", "how much", "total", "sum of", "average", "count of",
        /// "number of".</summary>
        public static bool AsksForAnAmount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var words = Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray();
            return AmountWords.Any(phrase => Contains(words, phrase.Select(Stem).ToArray()));
        }

        /// <summary>Where the words say a TASK is done by hand: the pages' own words, and never Ask your data, which
        /// answers questions and changes nothing. Null when no page clearly wins, which is the ordinary case for a
        /// task and is not a failure.
        ///
        /// THE ROW AND THE FIELD READ THIS SAME FUNCTION (PlanTargets.JumpFor calls it, and so does the task reading
        /// below), so they cannot disagree about where a task is done. They did: the Plan's field read "Ask your data
        /// is the place for that." while the row Enter added from the same words read "Semanticus does not know a page
        /// for this one" (verify-askdata-r5 F1).</summary>
        public static AskRouteTool TaskPlace(string text)
        {
            var match = MatchCatalog(text);
            if (match?.Tool == null || string.Equals(match.Tool, AskRouteCatalog.DataTool, StringComparison.Ordinal))
                return null;
            return AskRouteCatalog.Find(match.Tool);
        }

        /// <summary>BOTH READINGS OF A LINE ASK YOUR DATA PLACES, in the order they are shown (coordinator, 28
        /// September, fix-askdata-r6).
        ///
        /// The rule it replaces was "a task is never sent to Ask your data", decided from a word list. Five rounds
        /// showed a word list cannot know every way a person writes a task or a question, and it failed both ways at
        /// once: business-style tasks still reached Ask your data, and polite data questions never did. So no reading
        /// is taken away any more. Both are named, the words only decide which is shown FIRST, and the person picks.
        ///
        /// WHICH ONE IS FIRST is <see cref="QuestionReadFirst"/>, and it is only an order: the question first only
        /// where the line plainly asks. What placed the line (the words or the service) never decides it. The one box
        /// that asks this is the Plan's field, "Describe a task or an aim", and Enter there keeps the words as a row
        /// whatever this says, so Ask your data is never the only answer wherever it lands.
        ///
        /// EACH READING CARRIES ITS OWN CONFIDENCE. The question reading gets the number that placed it: 1 where the
        /// words did, the service's own where the service did, never a fixed 1 over the top of a real number
        /// (verify-askdata-r4 V1). The task reading gets 1 where the catalogue named its page and 0 where no page was
        /// named, which is not a failure and is exactly what the row shows.</summary>
        public static AskRouteResult Readings(AskRouteResult result, string text, bool askDataGranted,
            double questionConfidence)
        {
            var place = TaskPlace(text);
            var task = new AskReading
            {
                Kind = TaskReading,
                Tool = place?.Id,
                Area = place?.Area,
                Confidence = place == null ? 0 : 1,
                Line = place == null ? KeepAsATask + " " + NoPageForATask : KeepAsATask + " It is done on " + place.Label + ".",
            };
            var asked = askDataGranted ? AskRouteCatalog.DataTool : AskRouteCatalog.QueriesTool;
            var question = new AskReading
            {
                Kind = QuestionReading,
                Tool = asked,
                Area = AskRouteCatalog.AreaOf(asked),
                Confidence = questionConfidence,
                Line = askDataGranted ? AskItAsAQuestion : AskItAsAQuestion + " " + DataQuestionProLine,
            };

            var questionFirst = QuestionReadFirst(text);
            var top = questionFirst ? question : task;
            result.Other = questionFirst ? task : question;
            result.Reading = top.Kind;
            result.Tool = top.Tool;
            result.Area = top.Area;
            result.Confidence = top.Confidence;
            result.Fallback = null;
            result.Reason = null;
            // The top reading's own line, where naming the place is not the whole of it: the Pro line where Ask your
            // data is not in this tier, and the task's own line wherever the task is first, so it reads the same first
            // or second and always says what Enter does ("Keep it as a task."). A plain data question read task first
            // used to open on "Semanticus does not know a page for this one." alone (verify-askdata-r6 L3).
            result.Sentence = questionFirst
                ? (askDataGranted ? null : DataQuestionProLine)
                : task.Line;
            return result;
        }

        /// <summary>One reading of an answer by kind, wherever it sits: the top one or the other one. Null when the
        /// answer has only one reading, or when its two readings are not this kind. The top reading's Line is the
        /// result's own Sentence, which is null where the place's name is the whole line.</summary>
        public static AskReading ReadingOf(AskRouteResult result, string kind)
        {
            if (result?.Reading == null) return null;
            if (string.Equals(result.Reading, kind, StringComparison.Ordinal))
                return new AskReading
                {
                    Kind = kind, Tool = result.Tool, Area = result.Area,
                    Confidence = result.Confidence, Line = result.Sentence,
                };
            return string.Equals(result.Other?.Kind, kind, StringComparison.Ordinal) ? result.Other : null;
        }

        /// <summary>THE WHOLE WORDS PASS as one answer: the catalogue, the data-question shape and the Data page
        /// redirect, then both readings where Ask your data is one of them. Null when the words place nothing, which
        /// is the caller's signal to go on to the service.
        ///
        /// It lives here rather than inside LocalEngine so the labelled sets can be measured against the code the
        /// engine really runs, instead of against a copy of it written in a test that can drift.</summary>
        public static AskRouteResult PlaceByWords(AskRouteResult result, string text, bool askDataGranted)
        {
            var matched = MatchWords(text);
            if (matched == null) return null;
            result.Asked = false;
            if (string.Equals(matched.Tool, AskRouteCatalog.DataTool, StringComparison.Ordinal))
                return Readings(result, text, askDataGranted, questionConfidence: 1);
            result.Tool = matched.Tool;
            result.Area = AskRouteCatalog.AreaOf(matched.Tool);
            result.Create = matched.Create;
            result.Confidence = 1;
            result.Fallback = null;
            return result;
        }

        /// <summary>The two kinds of reading, named once so both doors and the page spell them the same.</summary>
        public const string TaskReading = "task";
        public const string QuestionReading = "question";

        /// <summary>The second choice when the words read as a task first. Never "Ask your data is the place for
        /// that.", which would be the page deciding for the person (coordinator, 28 September).</summary>
        public const string AskItAsAQuestion = "Or ask it as a question.";

        /// <summary>The task choice. The Plan's field adds it on Enter whatever else is named.</summary>
        public const string KeepAsATask = "Keep it as a task.";

        /// <summary>The words the Plan's ROW uses for a task it can find no page for (plan-row.tsx). The field says
        /// the same thing in the same words, which is the whole point of it being one string.</summary>
        public const string NoPageForATask = "Semanticus does not know a page for this one.";

        /// <summary>The line a data question reads where Ask your data is not part of this session's tier.</summary>
        public const string DataQuestionProLine =
            "Ask your data is part of Semanticus Pro. Queries runs any query you write, on every tier.";

        private static bool Contains(string[] words, string[] phrase)
        {
            for (var i = 0; i + phrase.Length <= words.Length; i++)
            {
                var all = true;
                for (var j = 0; j < phrase.Length && all; j++)
                    all = string.Equals(words[i + j], phrase[j], StringComparison.Ordinal);
                if (all) return true;
            }
            return false;
        }

        /// <summary>What one matched word is worth. A NOUN is the thing a page is about and is worth twice a
        /// VERB, which on its own is nearly always ambiguous: "rename" is Find and replace when it is the
        /// thing being asked for and Lineage when it is only the verb in a question about impact, and these
        /// two numbers are the whole of what tells those two asks apart.
        ///
        /// A RELATION beats them both, and 4 rather than 3 is deliberate: a two-word noun PHRASE is worth 3,
        /// so at 3 a relation only ties with one and "what uses the date table" would answer nobody. A
        /// relation word is what the question is ABOUT, so it has to be above every noun and not level with
        /// the long ones.</summary>
        private const int NounWorth = 2;
        private const int VerbWorth = 1;
        private const int RelationWorth = 4;

        /// <summary>The place a request to make a model from a source opens: the Model Spec, whose empty-model view is the wizard.</summary>
        public const string ModelSpecTool = "spec";

        /// <summary>What a request to make a model is worth: more than any place's words can score, because the ask was read as a whole.</summary>
        private const int MakeIntentWorth = 100;

        /// <summary>Whether a catalogue word is in the text, and what it is worth: its base worth, plus one
        /// more when it is a PHRASE, because two or three words in a row is not something anybody types by
        /// accident. Both sides are stemmed, so the singular and the plural are the same word.</summary>
        private static int Hit(string[] words, string entry, int worth)
        {
            var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray();
            if (parts.Length == 0) return 0;
            for (var i = 0; i + parts.Length <= words.Length; i++)
            {
                var all = true;
                for (var j = 0; j < parts.Length && all; j++)
                    all = string.Equals(words[i + j], parts[j], StringComparison.Ordinal);
                if (all) return parts.Length == 1 ? worth : worth + 1;
            }
            return 0;
        }

        /// <summary>Take out the words that belong to something outside Semanticus (AskRouteCatalog.WordsThatAreNotOurs)
        /// before anything is scored, by replacing each of them with a token no list holds. Blanking rather than
        /// dropping, so the words around them keep their positions and a phrase cannot be joined across the gap.</summary>
        private static string[] Blank(string[] words)
        {
            string[] blanked = null;
            foreach (var phrase in AskRouteCatalog.WordsThatAreNotOurs)
            {
                var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray();
                for (var i = 0; i + parts.Length <= words.Length; i++)
                {
                    var all = true;
                    for (var j = 0; j < parts.Length && all; j++)
                        all = string.Equals((blanked ?? words)[i + j], parts[j], StringComparison.Ordinal);
                    if (!all) continue;
                    blanked = blanked ?? (string[])words.Clone();
                    for (var j = 0; j < parts.Length; j++) blanked[i + j] = NotOurs;
                }
            }
            return blanked ?? words;
        }

        /// <summary>The token a blanked word becomes. A space is not a word any list can hold, so nothing matches it.</summary>
        private const string NotOurs = " ";

        /// <summary>Singular and plural as one word. Deliberately crude: it is a lookup key for a hand-written
        /// list, not a stemmer, and a real one would start matching words nobody put in the list.</summary>
        private static string Stem(string word)
        {
            if (word.Length > 3 && word.EndsWith("ies", StringComparison.Ordinal)) return word.Substring(0, word.Length - 3) + "y";
            // "classes" and "passes" lose two letters; everything else loses one. This rule used to fire on
            // every word ending in "ses", which turned "uses" into "us" and "cases" into "cas", so the one
            // verb that makes a dependency question a dependency question was never matched at all: Lineage
            // scored ZERO on "what uses this measure", not one (measured 22 September).
            if (word.Length > 4 && word.EndsWith("sses", StringComparison.Ordinal)) return word.Substring(0, word.Length - 2);
            if (word.Length > 2 && word.EndsWith("s", StringComparison.Ordinal) && !word.EndsWith("ss", StringComparison.Ordinal))
                return word.Substring(0, word.Length - 1);
            return word;
        }

        /// <summary>An answer that is only a sentence. No tool, and no menu either: the question was answered,
        /// so offering the list of every page as a fallback would read as a failure.</summary>
        private static AskRouteResult Told(string sentence) => new AskRouteResult
        {
            Confidence = 1,
            Sentence = sentence,
            Asked = false,
        };

        private static AskRouteResult Answered(string tool, string sentence) => new AskRouteResult
        {
            Tool = tool,
            Area = AskRouteCatalog.AreaOf(tool),
            Confidence = 1,
            Sentence = sentence,
            Asked = false,
        };

        private static string SizeSentence(AskDeterministicFacts f)
        {
            var shape = Shape(f);
            if (f?.SizeBytes == null || f.SizeBytes <= 0)
                return shape == null
                    ? "This model has not been measured, so there is no size to report yet."
                    : shape + " Its size in memory has not been measured yet, so Size will read it.";
            return shape == null
                ? "This model is " + Megabytes(f.SizeBytes.Value) + " in memory."
                : shape + " It is " + Megabytes(f.SizeBytes.Value) + " in memory.";
        }

        private static string ChangeSentence(AskDeterministicFacts f)
        {
            if (f?.UnsavedChangeCount == null) return "This session has not recorded any changes yet.";
            var n = f.UnsavedChangeCount.Value;
            if (n == 0) return "Nothing has changed since the model was last saved.";
            return n == 1
                ? "One change has been made and not saved yet."
                : Count(n) + " changes have been made and not saved yet.";
        }

        private static string CheckSentence(AskDeterministicFacts f)
        {
            // Null is NOT zero. It means nobody looked: the suite could not be read, or nothing on this
            // session has read it yet. Answering "no saved checks" there told a person their model was empty
            // of checks on no evidence at all, about a model that may well have some (Astra 16). Saved checks
            // stopped being Pro on 22 September, so the free tier is no longer one of the reasons.
            if (f?.SavedCheckCount == null)
                return "How many saved checks this model has is not known here. Open the Validation page to see them.";
            var n = f.SavedCheckCount.Value;
            if (n == 0) return "This model has no saved checks yet.";
            return n == 1 ? "This model has one saved check." : "This model has " + Count(n) + " saved checks.";
        }

        private static string ShapeSentence(AskDeterministicFacts f)
        {
            var shape = Shape(f);
            return shape ?? "How many tables and measures this model has is not known here yet.";
        }

        private static string Shape(AskDeterministicFacts f)
        {
            if (f?.TableCount == null || f.MeasureCount == null) return null;
            return (string.IsNullOrWhiteSpace(f.ModelName) ? "This model" : f.ModelName)
                 + " has " + Count(f.TableCount.Value) + " " + (f.TableCount.Value == 1 ? "table" : "tables")
                 + " and " + Count(f.MeasureCount.Value) + " " + (f.MeasureCount.Value == 1 ? "measure" : "measures") + ".";
        }

        private static string Megabytes(long bytes)
        {
            var mb = bytes / 1024d / 1024d;
            return mb < 1
                ? Math.Max(1, bytes / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KB"
                : mb.ToString(mb < 10 ? "N1" : "N0", CultureInfo.InvariantCulture) + " MB";
        }

        private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>Lower case, no punctuation, single spaces. Matching on anything richer would be a parser,
        /// and a parser here would quietly grow into one nobody can replay.</summary>
        private static string Normalise(string text)
        {
            var chars = text.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray();
            return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool Matches(string normalised, params string[] phrasings) =>
            phrasings.Any(p => string.Equals(normalised, p, StringComparison.Ordinal));
    }
}
