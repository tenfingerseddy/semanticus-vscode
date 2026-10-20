// Describe a workflow in words, and get its steps for Author (Kane's UAT on 25 Sep: "describe a workflow tries to add it
// to the plan to author and it becomes a mess").
//
// DESCRIBING IS AUTHORING. Words typed on the Workflows page become the steps of a new workflow, open in Author, and
// nothing goes on the Plan: only running a workflow makes Plan rows. Words typed on the Plan itself (the field, Ctrl J)
// still make a Plan draft; that path is compose_task and is unchanged.
//
// The words are cut into clauses at the places a person separates steps (a full stop, a comma, "then", "and"), and each
// clause is matched against the composer's own step list (DraftMenu.All), so the Plan's menu and Author name the same
// steps with the same words. A clause that names none of them is still a step: a named placeholder, in the person's
// words, to fill in. A clause that only says when ("every month") is left out of the steps, because when a workflow
// comes back is a repeat on the Plan, not a step.
//
// TWO WAYS TO CHOOSE (Kane's UAT on 25 Sep: "Jev doesnt appear to choose MCP operations just give free text for agent
// steps"; Kane, same day: "Jev really drafts. this was the plan all along"). Once the compose.draft replay clears its
// bar on this door (JevDraftBuilder.JevDraftsOnWorkflows, on since 26 Sep), a Pro session with Jev available and permission given asks JevDraftBuilder once per
// step the words name, the same chooser the Plan's composer asks: Jev says which of the engine's step templates the
// step's words ask for and which listed table or partition it works on, and the engine proves every pick. Everywhere
// else (Free is refused before anything, and no bar, no service, no permission, the point switched off, an answer
// refused, or nothing sure) the word match decides: plain matching over a fixed list, a slip of one letter allowed, no
// inference and no service. A step neither can decide stays a named placeholder, with the reason in plain words.
//
// WHO DOES IT is the engine's rule, not Jev's (composer contract section 8 asks Jev only to include and pick): a step whose
// op is one a draft runs is written into the format 3 file as Semanticus's step (C7), and every other step as your
// assistant's. Author's summary reads it from the file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine.Composer
{
    /// <summary>One step read off the words.</summary>
    public sealed class DescribedStep
    {
        /// <summary>The step's title: the person's own words for it, first letter raised.</summary>
        public string Title { get; set; }
        /// <summary>The clause as typed, which is what goes to Jev for this step.</summary>
        public string Words { get; set; }
        /// <summary>The op Semanticus found for it, or null for a placeholder.</summary>
        public string Op { get; set; }
        /// <summary>The composer's name for that step ("Hide an object"), or null.</summary>
        public string StepName { get; set; }
        public bool Matched { get; set; }
        /// <summary>Every op the step may use, in the engine's order. Empty for a placeholder. Each is the op of a step
        /// template on the executable allowlist.</summary>
        public string[] Ops { get; set; } = Array.Empty<string>();
        /// <summary>Who could do it, by the engine's rule: "semanticus" when every op it uses needs nothing written,
        /// else "assistant". The format 3 file (Document) writes each step with the performer Author shows.</summary>
        public string Performer { get; set; } = "assistant";
        /// <summary>What the step works on, as the engine names it ("Sales"), when Jev picked it from the options the
        /// engine listed; null otherwise.</summary>
        public string Target { get; set; }
        /// <summary>"jev" when Jev chose the step's templates, "words" when the word match did, null for a placeholder.</summary>
        public string ChosenBy { get; set; }
        /// <summary>The checks the step carries, as check kinds: its template's own (lane author-gaps, C7). Empty for a
        /// placeholder and for a step your assistant does.</summary>
        public string[] Checks { get; set; } = Array.Empty<string>();
        /// <summary>For a placeholder, why no op was chosen, in plain words. Null otherwise.</summary>
        public string Reason { get; set; }
        /// <summary>True for a step past the most one description reads (<see cref="DescribedSteps.MaxSteps"/>): it is
        /// kept as a step to fill in, in the person's words, and neither Jev nor the word match read it (Sol on
        /// m11/integration 568435d3, finding 4: such a clause used to be dropped).</summary>
        public bool NotRead { get; set; }
        /// <summary>The adding rule keeps this step's add (Tier.Kept), so a template that adds, of the kind the words name, may be its
        /// step. The Workflows door only (describe_workflow_steps, item 3 of the M11 join). Not on the wire: the engine's own reading.</summary>
        internal bool AsksToAdd { get; set; }
        /// <summary>The op of the kind the words name ("create_role"), when the rule keeps or offers an add for them. Not on the wire.</summary>
        internal string RuleKind { get; set; }
        /// <summary>A step named Add that makes nothing new (Add synonyms) may be this step's: the words hold an adding verb or a want of
        /// a new thing (the rule's tier is not NotDrafted) and do not take something away (<see cref="DescribedSteps.AddsOnWorkflows"/>).
        /// Not on the wire.</summary>
        internal bool MayNameAnAdd { get; set; }
        /// <summary>The word match's first choice was a template that adds, the rule does not keep an add for these words, and it was set
        /// aside. Not on the wire.</summary>
        internal bool AddWithheld { get; set; }
        /// <summary>Why the adding rule lets no step that adds be this step's, in plain words, or null when it may add. Not on the wire.</summary>
        internal string NotAnAdd { get; set; }
        /// <summary>Which sentence of the words the clause sits in (AddingRule.Sentences): a take-away sign anywhere in
        /// its sentence vetoes an add for it (the adding rule, version 3). Not part of the answer.</summary>
        internal int Sentence { get; set; }
    }

    /// <summary>What describe_workflow_steps answers. Nothing is stored.</summary>
    public sealed class DescribedWorkflow
    {
        public string Words { get; set; }
        public DescribedStep[] Steps { get; set; } = Array.Empty<DescribedStep>();
        /// <summary>A clause that said when rather than what ("every month"), or null.</summary>
        public string When { get; set; }
        /// <summary>How many steps the words name past the most one description reads (12). Each is kept as a step to
        /// fill in, marked NotRead, and the sentence names it: a step the words name is never dropped.</summary>
        public int LeftOut { get; set; }
        /// <summary>The words of each step past that limit, as typed, in order. Empty when there are none.</summary>
        public string[] NotRead { get; set; } = Array.Empty<string>();
        public string Sentence { get; set; }
        /// <summary>True when Jev was asked (and answered or not). False on a session where it was not asked.</summary>
        public bool JevAsked { get; set; }
        /// <summary>Why Jev did not choose, in plain words, when it was not asked or its answer was refused. Null otherwise.</summary>
        public string JevNote { get; set; }
        /// <summary>The description as a format 3 workflow, the way Author opens it, unsaved: each step's action, who does
        /// it, its questions, parts to write and checks. Null only when nothing could be written.</summary>
        public DraftDocument Document { get; set; }
    }

    internal static class DescribedSteps
    {
        public const int MaxSteps = 12;
        /// <summary>How many clauses past <see cref="MaxSteps"/> a workflow keeps as steps of their own. Every clause after
        /// those is kept too, together, as ONE step to fill in holding their words: a clause is never dropped (Sol, finding
        /// 4), and a description at the 100,000-character cap no longer makes a 9,192-step workflow and a 5.8 MB answer
        /// (verifier on m11/sol-fixes-1, D3).</summary>
        public const int MaxNotReadSteps = 12;
        public const int MaxTitleChars = 80;

        private static readonly Regex Cut = new Regex(
            @"[.;:\n]+|[!?]+|,|\b(?:and then|after that|then|and|finally|next)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Filler = new Regex(@"^(?:first|firstly|please|also|lastly|finally|then|and)\b[\s,]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private const string Days = "day|week|month|quarter|year|morning|evening|night|weekday|monday|tuesday|wednesday|thursday|friday|saturday|sunday";

        /// <summary>A leading "every month" or "each Monday": when, not what.</summary>
        private static readonly Regex LeadingWhen = new Regex(
            @"^(?:(?:every|each)\s+(?:(?:other|single)\s+)?(?:" + Days + @")s?|(?:daily|weekly|monthly|nightly|quarterly|yearly|once\s+a\s+(?:" + Days + @"))|on\s+(?:" + Days + @")s?)\b[\s,]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The steps after the change a workflow can hold (compose_task's list of steps that leave the model),
        /// after the composer's own list so a clause that names both is the change. Author only: the Plan's menu drafts
        /// changes.</summary>
        internal static readonly DraftMenu.Template[] AfterTheChange =
        {
            DraftMenu.T("refresh", "Refresh a table", "refresh_partition", "refresh", "reload", "process"),
            DraftMenu.T("run-tests", "Run the saved checks", "run_tests", "checks", "check", "tests", "test", "validate"),
            DraftMenu.T("publish", "Publish to a stage", "deploy_stage", "publish", "deploy", "release"),
            DraftMenu.T("save", "Save the model", "save_model", "save the model", "save it", "save changes"),
        };

        /// <summary>Every template the word match reads for a described step: the composer's, the steps after the change,
        /// and the two reads. "run the tests" is the saved checks (two hits) and "test its performance" a benchmark (two
        /// hits), whatever the list order.</summary>
        internal static IEnumerable<DraftMenu.Template> WordTemplates =>
            DraftMenu.All.Concat(AfterTheChange).Concat(JevDraftBuilder.Reads);

        /// <summary>The words cut into steps, each chosen by the word match. The Jev path starts from this and replaces
        /// what Jev decided.</summary>
        /// <summary><paramref name="bounded"/> is the Workflows door: the clauses past the limit become at most
        /// <see cref="MaxNotReadSteps"/> steps of their own and one step for the rest (<see cref="KeptNotRead"/>). The Plan
        /// keeps every clause apart instead (Draft.NotRead), because Add more words reads them a few at a time.</summary>
        /// <summary><paramref name="applyAddingRule"/> (the default) reads every step by the adding rule, as the Workflows door does
        /// (<see cref="ApplyAddingRule"/>, item 3 of the M11 join); the Plan's own reading passes false, because it applies the rule itself,
        /// tier by tier, over the whole text (<see cref="RuleOf"/>).</summary>
        internal static DescribedWorkflow From(string words, bool bounded = false, bool applyAddingRule = true)
        {
            var text = (words ?? "").Trim();
            if (text.Length == 0) throw new ArgumentException("Describe the workflow in words: what it does, step by step.", nameof(words));
            var clauses = ClausesOf(text, out var when, out var sentences);
            var steps = new List<DescribedStep>();
            foreach (var (clause, sentence) in clauses.Zip(sentences).Take(MaxSteps))
            {
                var step = new DescribedStep { Title = TitleOf(clause), Words = clause, Sentence = sentence };
                ByWords(step, DraftMenu.Best(clause, WordTemplates));
                steps.Add(step);
            }
            // Past the limit a clause is not read, but it is kept, in the person's words, as a step to fill in.
            var past = clauses.Skip(MaxSteps).ToList();
            steps.AddRange(bounded ? KeptNotRead(past, DraftCopy.DescribeNotRead(MaxSteps), DraftCopy.DescribeRestNotRead)
                                   : past.Select(c => NotReadStep(c, DraftCopy.DescribeNotRead(MaxSteps))));
            var d = new DescribedWorkflow
            {
                Words = text, Steps = steps.ToArray(), When = when, LeftOut = past.Count,
                NotRead = steps.Where(s => s.NotRead).Select(s => s.Words).ToArray(),
            };
            if (applyAddingRule) ApplyAddingRule(d, null, null);
            else Summarise(d);
            return d;
        }

        /// <summary>The words cut into clauses, as From cuts them, with no word match: cheap enough to count a long list of
        /// kept words by. <paramref name="when"/> is the first clause that said when rather than what ("every month").</summary>
        internal static List<string> ClausesOf(string words, out string when) => ClausesOf(words, out when, out _);

        /// <summary>The words cut into clauses, with each clause's sentence (<paramref name="sentences"/>, as
        /// AddingRule.Sentences reads them from where each clause sits), for the adding rule's sentence-wide veto.</summary>
        internal static List<string> ClausesOf(string words, out string when, out int[] sentences)
        {
            when = null;
            var clauses = new List<string>();
            var spans = new List<(int, int)>();
            var text = (words ?? "").Trim();
            var from = 0;
            // "e.g." and "i.e." are not clause ends: cut over the text with their stops blanked, keep the words as typed.
            foreach (var cut in Cut.Matches(AddingRule.MaskAbbreviations(text)).Cast<Match>().Append(null))
            {
                var to = cut?.Index ?? text.Length;
                var raw = text.Substring(from, to - from);
                var start = from;
                from = cut == null ? text.Length : cut.Index + cut.Length;
                var clause = Filler.Replace(raw.Trim(), "").Trim();
                var lead = LeadingWhen.Match(clause);
                if (lead.Success)
                {
                    when ??= lead.Value.Trim().TrimEnd(',').Trim();
                    clause = clause.Substring(lead.Length).Trim();
                }
                clause = clause.Trim(' ', ',', '-');
                if (clause.Length > 0) { clauses.Add(clause); spans.Add((start, to)); }
            }
            sentences = AddingRule.Sentences(text, spans);
            return clauses;
        }

        private static DescribedStep NotReadStep(string words, string reason)
        {
            var step = new DescribedStep { Title = TitleOf(words), Words = words };
            ByWords(step, null);
            step.NotRead = true;
            step.Reason = reason;
            return step;
        }

        /// <summary>Clauses no one read, as steps to fill in, bounded (verifier on m11/sol-fixes-1, D3): the first
        /// <see cref="MaxNotReadSteps"/> a step each with <paramref name="one"/> as its reason, and every clause after them
        /// in ONE step whose words are theirs, in order, and whose reason (<paramref name="rest"/>, given the count and the
        /// words) says so. Nothing typed is dropped, and the step count stays small whatever the length.</summary>
        internal static IEnumerable<DescribedStep> KeptNotRead(IReadOnlyList<string> clauses, string one, Func<int, string, string> rest)
        {
            foreach (var c in clauses.Take(MaxNotReadSteps)) yield return NotReadStep(c, one);
            if (clauses.Count <= MaxNotReadSteps) yield break;
            var words = string.Join(", ", clauses.Skip(MaxNotReadSteps));
            var n = clauses.Count - MaxNotReadSteps;
            var step = NotReadStep(words, rest(n, words));
            step.Title = DraftCopy.RestNotReadTitle(n);
            yield return step;
        }

        /// <summary>What the word match found for one step, or a placeholder with its reason.</summary>
        internal static void ByWords(DescribedStep step, DraftMenu.Template t)
        {
            step.Op = t?.Op;
            step.StepName = t?.Title;
            step.Matched = t != null;
            step.Ops = t == null ? Array.Empty<string>() : new[] { t.Op };
            step.ChosenBy = t == null ? null : "words";
            step.Performer = PerformerOf(step.Ops);
            step.Target = null;
            step.Checks = ChecksOf(step.Ops);
            step.Reason = t == null ? DraftCopy.DescribeNoMatch : null;
        }

        /// <summary>The templates a described step can hand to Semanticus as a step it runs: the composer's changes and the
        /// steps after the change. The two reads stay your assistant's, because a draft runs every read before the change.</summary>
        internal static DraftMenu.Template RunnableTemplate(string op) =>
            op == null ? null : DraftMenu.All.Concat(AfterTheChange).FirstOrDefault(t => t.Op == op);

        // THE ADDING RULE, version 5 (the coordinator, 29 Sep 19:05; AddingRule.cs holds the whole rule). An add is KEPT only
        // when the words ask for a NEW thing of a named kind, not into a set, with nothing unread after it (K1 to K4);
        // otherwise, when the clause holds an adding verb, it is offered UNTICKED with the person's words and one true line
        // on why; with no adding verb it is not drafted. Jev's picks and the word match alike go through it, and Jev never
        // lifts an add above unticked. A clause with no ticked step is kept with its reason (Draft.NoStep or Draft.Unticked).

        /// <summary>The adding rule over the clauses read. <paramref name="jev"/> is each clause's answer from Jev (null for
        /// none), which with the clause's word match says which clauses got a kept step that does not add (K4).
        /// <paramref name="tables"/> are the model's table names (K3). <paramref name="unreadAfter"/> says, per clause, that
        /// words after it are still unread (a clause past the limit, or kept words that wait).</summary>
        internal static AddingRule.Reading[] RuleOf(IReadOnlyList<DescribedStep> clauses, IReadOnlyList<JevDraftBuilder.Result> jev = null,
            IReadOnlyCollection<string> tables = null, IReadOnlyList<bool> unreadAfter = null, IReadOnlyCollection<string> objects = null,
            bool wholeSigned = false)
        {
            var words = clauses.Select(c => c.Words).ToList();
            var sentences = clauses.Select(c => c.Sentence).ToList();
            return AddingRule.Read(words, sentences, GotKept(clauses, jev), tables, unreadAfter, objects, wholeSigned);
        }

        /// <summary>Whether each clause got a kept step that does not add, by its word match alone (K4). Jev's pick does NOT count
        /// (the M11 join, verifier VR20): Jev never lifts an add above unticked, so a later clause that takes the add back ("undo that",
        /// "take it back") cannot become a step, and so an answer to K4, because Jev was sure of some step for it. <paramref name="jev"/>
        /// stays in the signature for the callers that pass what Jev said; it is not read here.</summary>
        internal static List<bool> GotKept(IReadOnlyList<DescribedStep> clauses, IReadOnlyList<JevDraftBuilder.Result> jev) =>
            clauses.Select(c =>
            {
                var t = RunnableTemplate(c.Op);
                return t != null && !Adds(t);
            }).ToList();

        /// <summary>The rule over the WHOLE text (every typing), where the clauses this ask read keep what Jev's answers said they
        /// got (<paramref name="askedGot"/>) and every other clause the word match's (version 6, A).</summary>
        internal static AddingRule.Reading[] WholeRule(IReadOnlyList<string> wholeClauses, IReadOnlyList<int> sentences, IReadOnlyList<string> asked,
            IReadOnlyList<bool> askedGot, IReadOnlyCollection<string> tables, IReadOnlyCollection<string> objects, bool wholeSigned)
        {
            var pool = new List<(string Text, bool Got)>();
            for (var i = 0; i < asked.Count; i++) pool.Add((asked[i], i < askedGot.Count && askedGot[i]));
            var facts = new WordMatchFacts(wholeClauses);
            for (var k = 0; k < wholeClauses.Count; k++)
            {
                var at = pool.FindIndex(x => string.Equals(x.Text, wholeClauses[k], StringComparison.Ordinal));
                if (at < 0) continue;
                facts.Set(k, pool[at].Got);
                pool.RemoveAt(at);
            }
            return AddingRule.Read(wholeClauses, sentences, facts, tables, null, objects, wholeSigned);
        }

        /// <summary>The rule over every clause of the words with only the word match to say what each clause got (the menu, and
        /// a test's read of the rule alone): no Jev. The word match is asked only for the clauses K4 has to look at, so a draft
        /// holding thousands of clauses stays cheap.</summary>
        internal static AddingRule.Reading[] RuleOfWords(string words, IReadOnlyCollection<string> tables = null, IReadOnlyCollection<string> objects = null)
        {
            var clauses = ClausesOf(words, out _, out var sentences);
            // The words are the whole text: a sign of the frozen list anywhere in them demotes a kept add (version 6, B).
            return AddingRule.Read(clauses, sentences, new WordMatchFacts(clauses), tables, null, objects, AddingRule.WholeSigned(new[] { words }));
        }

        /// <summary>Whether each clause got a kept step that does not add from the word match, worked out when asked and kept.</summary>
        private sealed class WordMatchFacts : IReadOnlyList<bool>
        {
            private readonly IReadOnlyList<string> _clauses;
            private readonly bool?[] _got;
            private readonly bool _countReads;
            /// <param name="countReads">The Workflows door: a read the word match found (a benchmark, a profile) is a step of the file, the
            /// assistant's, so a clause that got one counts as read for K4. The Plan's draft runs no read, so there it stays unread.</param>
            public WordMatchFacts(IReadOnlyList<string> clauses, bool countReads = false) { _clauses = clauses; _got = new bool?[clauses.Count]; _countReads = countReads; }
            public int Count => _clauses.Count;
            public bool this[int i] => _got[i] ??= Got(DraftMenu.Best(_clauses[i], WordTemplates)?.Op);
            private bool Got(string op) =>
                RunnableTemplate(op) is DraftMenu.Template t && !Adds(t) || _countReads && op != null && JevDraftBuilder.Reads.Any(r => r.Op == op);
            /// <summary>A fact Jev's answer gave for the clause, in place of the word match's.</summary>
            public void Set(int i, bool got) => _got[i] = got;
            public IEnumerator<bool> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>The words' clauses that ask to add, ticked, by the adding rule over the whole words, as one text: what a
        /// step that adds may be named by. With <paramref name="tail"/>, only the clauses of the words' end that it cut into.</summary>
        internal static string AskingToAdd(string words, string tail = null)
        {
            var clauses = ClausesOf(words, out _, out var sentences);
            var rule = AddingRule.Read(clauses, sentences, new WordMatchFacts(clauses), wholeSigned: AddingRule.WholeSigned(new[] { words }));
            var skip = tail == null ? 0 : Math.Max(0, clauses.Count - ClausesOf(tail, out _).Count);
            return string.Join(". ", clauses.Where((_, i) => i >= skip && rule[i].AsksToAdd));
        }

        /// <summary>Whether any clause of the words asks to take something away, by the adding rule.</summary>
        internal static bool TakesAway(string words)
        {
            var clauses = ClausesOf(words, out _, out var sentences);
            return AddingRule.Read(clauses, sentences).Any(r => r.TakesAway);
        }

        /// <summary>The last sentence of <paramref name="words"/> when it is still open (the words do not end with ., !, ?
        /// or a line break), by AddingRule's own sentence ends (one copy, so "e.g." does not end it); null when the words end
        /// a sentence. Since version 5 nothing reads it: no check is made over the draft's earlier sentence.</summary>
        internal static string OpenSentence(string words)
        {
            var text = (words ?? "").TrimEnd(' ', (char)9);
            if (text.Length == 0 || text[text.Length - 1] is '.' or '!' or '?' or (char)10 or (char)13) return null;
            var masked = AddingRule.MaskAbbreviations(text);
            var at = -1;
            for (var p = masked.Length - 1; p >= 0 && at < 0; p--)
                if (masked[p] is '!' or '?' or ';' or (char)10 or (char)13 || masked[p] == '.' && p + 1 < masked.Length && char.IsWhiteSpace(masked[p + 1])) at = p;
            return text.Substring(at + 1).Trim();
        }

        /// <summary>Whether a step template adds something to the model (its op creates).</summary>
        internal static bool Adds(DraftMenu.Template t) => AddingRule.Adds(t?.Op);

        /// <summary>THE WORKFLOWS DOOR'S WIDER READING of a step that adds (m11/completeness, kept at the join by the coordinator's ruling
        /// of 30 Sep): an op that creates, or a step whose name says Add though it makes nothing new (Add synonyms). A step of the second
        /// kind is this step's only when <paramref name="mayNameAnAdd"/>: its words hold an adding verb and take nothing away
        /// (<see cref="MayNameAnAdd"/>). So "add synonyms for Total Sales" keeps Add synonyms, and words that take something away never get
        /// it, from the word match or from a Jev sure of it (the completeness verifier, round 4). A step that creates is read by the adding
        /// rule alone, as on the Plan, and AddingRule.Adds is not changed: the Plan reads Add synonyms as a step that adds nothing.</summary>
        internal static bool AddsOnWorkflows(DraftMenu.Template t, bool mayNameAnAdd) =>
            t != null && (AddingRule.Adds(t.Op) || !mayNameAnAdd && t.Title.StartsWith("Add ", StringComparison.Ordinal));

        /// <summary>Whether a step named Add that makes nothing new may be the step of a clause the rule read as <paramref name="r"/>.</summary>
        internal static bool MayNameAnAdd(AddingRule.Reading r) => r.HoldsAdd && !r.TakesAway;

        /// <summary>The step a clause Jev is not sure of takes from its words (the Plan's per-clause rule): its word match's
        /// template when that does not add. A step that adds comes from the adding rule only (<see cref="Allowed"/>).</summary>
        internal static DraftMenu.Template FallbackTemplate(IReadOnlyList<DescribedStep> clauses, int i, AddingRule.Reading[] rule = null)
        {
            var t = RunnableTemplate(clauses[i].Op);
            return t != null && Adds(t) ? null : t;
        }

        /// <summary>A clause's answer from Jev, held to the adding rule: every step that does not add stays; a step that adds
        /// stays only as the kind the clause names, ticked when the rule keeps the add and Jev was sure, else offered
        /// unticked; with no adding verb none stays. A kept add Jev did not pick is the words' own (the kind they name). An
        /// unticked add carries <see cref="JevDraftBuilder.Drafted.Untick"/>. A clause that names no kind offers no step, only its note.</summary>
        internal static List<JevDraftBuilder.Drafted> Allowed(JevDraftBuilder.Result r, AddingRule.Reading rule)
        {
            // Words that take something away never get a step named Add that makes nothing new (Add synonyms), whatever Jev says: the Workflows
            // door's reading (AddsOnWorkflows), given to the Plan too (M11 join part 3, join-2 section 6 item A).
            var steps = r?.Steps.Where(x => !Adds(x.Template) && !AddsOnWorkflows(x.Template, !rule.TakesAway)).ToList() ?? (rule.HoldsAdd ? new List<JevDraftBuilder.Drafted>() : null);
            if (!rule.HoldsAdd) return steps;
            var kind = rule.Kind != null ? DraftMenu.All.FirstOrDefault(t => t.Op == rule.Kind) : null;
            var jevAdd = kind == null ? null : r?.Steps.FirstOrDefault(x => x.Template == kind);
            if (rule.Tier == AddingRule.Tier.Kept)
            {
                steps.Add(jevAdd != null && jevAdd.Sure ? jevAdd : new JevDraftBuilder.Drafted { Template = kind, Sure = true, FromWords = true });
                // A step of another kind that Jev only leaned to arrives dropped, as it always did (never ticked).
                steps.AddRange(r?.Steps.Where(x => Adds(x.Template) && x.Template != kind && !x.Sure) ?? Enumerable.Empty<JevDraftBuilder.Drafted>());
            }
            else if (kind != null)
                steps.Add(new JevDraftBuilder.Drafted
                {
                    Template = kind, Sure = false, Untick = true, FromWords = jevAdd == null,
                    Picked = jevAdd?.Picked ?? new Dictionary<string, DraftOption>(StringComparer.Ordinal), Unsure = jevAdd?.Unsure ?? new List<string>(),
                });
            return steps;
        }

        /// <summary>The check kinds a step's template carries (lane author-gaps, C7).</summary>
        internal static string[] ChecksOf(string op) =>
            RunnableTemplate(op)?.Checks.Select(c => c.Kind).ToArray() ?? Array.Empty<string>();

        /// <summary>The check kinds of every op a step uses, in its ops' order, each kind once: a step Jev chose two ops
        /// for carries both ops' checks (Sol, finding 3), as the file does, one step per op.</summary>
        internal static string[] ChecksOf(IEnumerable<string> ops) =>
            ops.SelectMany(ChecksOf).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>What a drafted step works on, in the engine's words: a picked table by its name, a picked partition by
        /// its table's name (its label is "Sales, Sales 2024"), any other pick by its label.</summary>
        private static string TargetOf(JevDraftBuilder.Drafted x)
        {
            foreach (var (arg, option) in x.Picked)
            {
                var kind = OpParameterKinds.KindOf(x.Template.Op, arg);
                if (kind == "pick:partitions") { var comma = option.Label.IndexOf(", ", StringComparison.Ordinal); return comma > 0 ? option.Label.Substring(0, comma) : option.Label; }
                return option.Label;
            }
            return null;
        }

        internal static string PerformerOf(string[] ops) =>
            ops.Length > 0 && ops.All(JevDraftBuilder.NeedsNothingWritten) ? "semanticus" : "assistant";

        /// <summary>Does this clause, on its own with nothing before or after it, ask to ADD something? The adding rule keeps its add.</summary>
        internal static bool AsksToAdd(string clause) => RuleOfWords(clause).Any(r => r.Tier == AddingRule.Tier.Kept);

        /// <summary>THE ADDING RULE ON THE WORKFLOWS DOOR (the M11 join, item 3). The steps are read by the same rule the Plan's words are, so
        /// "delete the replaced measures" is never the step Add a measure here either. A step that adds is this step's only when the rule
        /// KEEPS its add (a new thing of a named kind, not into a set, nothing unread after it), and then only as the kind the words name;
        /// otherwise the word match's best template that does not add, and when there is none a placeholder with the reason. A workflow has
        /// no tick, so the Plan's "offered unticked" is a placeholder here: the person adds the step in Author, or words it as a plain add.
        /// <paramref name="tables"/> and <paramref name="objects"/> are a model's names (K2, K3), or null for none.</summary>
        internal static void ApplyAddingRule(DescribedWorkflow d, IReadOnlyCollection<string> tables, IReadOnlyCollection<string> objects)
        {
            var read = d.Steps.Where(x => !x.NotRead).ToList();
            if (read.Count == 0) return;
            var clauses = read.Select(x => x.Words).ToList();
            var sentences = read.Select(x => x.Sentence).ToList();
            var unreadAfter = read.Select(_ => d.LeftOut > 0).ToList();
            var rule = AddingRule.Read(clauses, sentences, new WordMatchFacts(clauses, countReads: true), tables, unreadAfter, objects, AddingRule.WholeSigned(new[] { d.Words }));
            for (var i = 0; i < read.Count; i++)
            {
                var step = read[i];
                var r = rule[i];
                step.AsksToAdd = r.Tier == AddingRule.Tier.Kept;
                step.RuleKind = r.Kind;
                step.MayNameAnAdd = MayNameAnAdd(r);
                var best = DraftMenu.Best(step.Words, WordTemplates);
                if (r.Tier == AddingRule.Tier.Kept)
                {
                    // The kind the words name is the add; a word-matched add of another kind is not.
                    if (r.Kind != null && (best == null || (Adds(best) && best.Op != r.Kind)) && RunnableTemplate(r.Kind) is DraftMenu.Template kind)
                        ByWords(step, kind);
                    continue;
                }
                step.NotAnAdd = WorkflowReasonFor(r);
                if (!AddsOnWorkflows(best, step.MayNameAnAdd)) continue;
                step.AddWithheld = true;
                var other = DraftMenu.Best(step.Words, WordTemplates.Where(x => !AddsOnWorkflows(x, step.MayNameAnAdd)));
                ByWords(step, other);
                if (other == null) step.Reason = step.NotAnAdd;
            }
            Summarise(d);
        }

        /// <summary>The line a Workflows step gives when the rule keeps no add for its words: the rule's own reason for words that take
        /// something away or ask for no add, and for words that mention an add it does not keep, the rule's first sentence (its "Tick it"
        /// sentence is the Plan's) and how to make it a step here.</summary>
        internal static string WorkflowReasonFor(AddingRule.Reading r)
        {
            if (r.Tier != AddingRule.Tier.Unticked || string.IsNullOrEmpty(r.Reason)) return AddingRule.ReasonFor(r);
            var first = r.Reason.Split(new[] { ". " }, 2, StringSplitOptions.None)[0].TrimEnd('.') + ".";
            // The advice has to be something the words did not already do (join-2 item D): "add a Sales measure, Delete the old role" starts
            // with add and names the thing, so it is told to start with add or create only when the words did not ask.
            var how = r.Reason == AddingRule.NotAskedReason ? " To add it, start with add or create and name the new thing, or add the step yourself in Author."
                    : r.Reason == AddingRule.NoKindReason ? " To add it, say what kind of thing to make, such as a measure or a role, or add the step yourself in Author."
                    : r.Reason == AddingRule.NotNewReason ? " To add it, say it is a new one, such as a new measure, or add the step yourself in Author."
                    : " To add it, put the add in a sentence of its own, or add the step yourself in Author.";
            return first + how;
        }

        /// <summary>Lay what Jev drafted for each step over the word match. A step with a sure template takes Jev's
        /// templates (sure ones only, in the engine's order) and its sure pick; a step with none keeps the word match
        /// when it found something, and otherwise stays a placeholder with the reason.</summary>
        internal static void ApplyJev(DescribedWorkflow d, JevDraftBuilder.Result[] drafted)
        {
            for (var i = 0; i < drafted.Length && i < d.Steps.Length; i++)
            {
                var step = d.Steps[i];
                var r = drafted[i];
                if (r == null) continue;
                // A template that adds is Jev's step only where the rule keeps the add, and only as the kind the words name: Jev never lifts an
                // add the rule does not keep (a sure add for words that only mention adding is set aside, as the word match's is).
                // A step named Add that makes nothing new (Add synonyms) is Jev's step only when the words hold an adding verb and take nothing
                // away (AddsOnWorkflows).
                bool Stands(JevDraftBuilder.Drafted x) => !AddsOnWorkflows(x.Template, step.MayNameAnAdd) || step.AsksToAdd && x.Template.Op == step.RuleKind;
                var sure = r.Steps.Where(x => x.Sure && Stands(x)).ToList();
                var jevWithheld = r.Steps.Any(x => x.Sure && !Stands(x));
                if (sure.Count > 0)
                {
                    step.Ops = sure.Select(x => x.Template.Op).Distinct(StringComparer.Ordinal).ToArray();
                    step.Op = step.Ops[0];
                    step.StepName = sure[0].Template.Title;
                    step.Matched = true;
                    step.ChosenBy = "jev";
                    step.Performer = PerformerOf(step.Ops);
                    step.Target = sure.Select(TargetOf).FirstOrDefault(t => t != null);
                    step.Checks = ChecksOf(step.Ops);
                    step.Reason = null;
                }
                else if (r.SaidNone)
                {
                    // A sure no from Jev is a placeholder with that reason whatever the word match found: describe_workflow_steps says
                    // the word match is used when Jev is off or not sure, and a sure no is neither (m11/completeness D5).
                    ByWords(step, null);
                    step.Reason = DraftCopy.DescribeJevNone;
                }
                else if (!step.Matched)
                    // Words that only ask for no add, where Jev alone read a step that adds, say that Jev read it so (Sol on
                    // m11/completeness round 4); the rule's own reason stands where it says more (a take-away, an add it offers).
                    step.Reason = step.AddWithheld ? step.NotAnAdd
                        : jevWithheld ? (step.NotAnAdd == null || step.NotAnAdd == AddingRule.NoAddReason ? DraftCopy.DescribeJevNotAnAdd : step.NotAnAdd)
                        : DraftCopy.DescribeJevUnsure;
            }
            Summarise(d);
            var byJev = d.Steps.Where(x => x.ChosenBy == "jev").Sum(FileSteps);
            // Who does each step is in the file now (C7: a step Semanticus runs is written as Semanticus's), and Author's own
            // summary says it, so the sentence only says what Jev chose, counted in the file's steps as the sentence is (D9).
            d.Sentence += DraftCopy.DescribeChosen(byJev, d.Steps.Sum(FileSteps));
        }

        /// <summary>The Plan's per-clause rule, as one pure step (26 Sep; D7 on 26 Sep). Each clause gives its own steps:
        /// Jev's when Jev was sure of one (the sure ones kept, the ones it leaned to dropped), else the clause's word match
        /// as a kept step, else what Jev leaned to, dropped. ONE STEP PER CLAUSE, even when two clauses ask for the same
        /// kind of step: "format Total Sales as currency, then format Order Count as a whole number" is two format steps,
        /// each with its own pick, never one step that silently loses the second object. That holds for a clause only
        /// Jev's leaning stands for too (R2-D7): in "hide Amount, then also Qty please" the second clause's leaning hide is
        /// that clause's own step, so it arrives dropped beside the first clause's hide, with its own pick, and no sure step
        /// takes its place. Only an EXTRA leaning step, one a clause Jev was sure of something else in leaned to as well,
        /// is not added twice: when its kind is already there (from another clause, or in <paramref name="have"/>, the ops
        /// a draft already holds) it adds nothing, and a sure step takes the place of such a step of its kind rather than
        /// sitting beside it. The kept steps are in the engine's order, clauses in the order typed within one kind.</summary>
        internal static JevDraftBuilder.Result MergeClauses(IReadOnlyList<DescribedStep> clauses, IReadOnlyList<JevDraftBuilder.Result> drafted,
            IEnumerable<string> have = null, AddingRule.Reading[] rule = null)
        {
            var held = new HashSet<string>(have ?? Array.Empty<string>(), StringComparer.Ordinal);
            var merged = new JevDraftBuilder.Result
            {
                SaidNone = drafted.All(r => r == null || r.SaidNone),
                SureOfEach = drafted.Where(r => r != null).All(r => r.SaidNone || r.Steps.Any(x => x.Sure)),
            };
            rule ??= RuleOf(clauses, drafted);
            // The leaning steps that are a clause's own (the clause has nothing else): never folded, never replaced.
            var own = new HashSet<JevDraftBuilder.Drafted>();
            for (var i = 0; i < clauses.Count; i++)
            {
                // The adding rule holds for Jev's picks as for the word match: a step that adds is ticked only when the rule
                // keeps it, offered unticked when the clause holds an adding verb, and dropped otherwise, sure or leaning.
                var allowed = Allowed(i < drafted.Count ? drafted[i] : null, rule[i]);
                var steps = allowed?.Where(x => !x.Untick).ToList();
                var unticked = allowed?.Where(x => x.Untick) ?? Enumerable.Empty<JevDraftBuilder.Drafted>();
                IEnumerable<JevDraftBuilder.Drafted> take;
                var leaningOnly = false;
                if (steps != null && steps.Any(x => x.Sure)) take = steps;
                else
                {
                    var t = FallbackTemplate(clauses, i, rule);
                    if (t != null) take = new[] { new JevDraftBuilder.Drafted { Template = t, Sure = true, FromWords = true } };
                    else { take = steps ?? new List<JevDraftBuilder.Drafted>(); leaningOnly = true; }
                }
                foreach (var x in take.Concat(unticked))
                {
                    x.Clause = i;
                    if (x.Untick) { merged.Steps.Add(x); own.Add(x); }
                    else if (x.Sure)
                    {
                        var leaning = merged.Steps.FindIndex(m => !m.Sure && !own.Contains(m) && m.Template.Id == x.Template.Id);
                        if (leaning >= 0) merged.Steps[leaning] = x;
                        else merged.Steps.Add(x);
                    }
                    else if (leaningOnly) { merged.Steps.Add(x); own.Add(x); }
                    else if (!held.Contains(x.Template.Op) && !merged.Steps.Any(m => m.Template.Id == x.Template.Id))
                        merged.Steps.Add(x);
                }
            }
            // A stable sort: two steps of one kind stay in the order their clauses were typed.
            merged.Steps = merged.Steps.OrderBy(x => JevDraftBuilder.RankOf(x.Template.Id)).ToList();
            return merged;
        }

        /// <summary>How many steps a described step is in the workflow file: one per op it uses (Sol, finding 3; AsDraft),
        /// and one for a placeholder.</summary>
        internal static int FileSteps(DescribedStep s) => Math.Max(1, s.Ops.Length);

        /// <summary>The sentence counts the steps the workflow HAS, not the clauses: "summarise Unit Price by average" is
        /// one clause and two steps (verifier on m11/sol-fixes-1, D9).</summary>
        internal static void Summarise(DescribedWorkflow d)
        {
            var steps = d.Steps;
            var total = steps.Sum(FileSteps);
            var toFill = steps.Count(s => s.Ops.Length == 0);
            d.Sentence = steps.Length == 0 ? "No steps in those words yet. Add them in Author."
                : (total == 1 ? "1 step" : total.ToString(CultureInfo.InvariantCulture) + " steps") + " from your words"
                  + (toFill == 0 ? "." : toFill == total ? ". None is a step Semanticus knows yet, so each is a placeholder to fill in."
                     : "; " + toFill.ToString(CultureInfo.InvariantCulture) + " to fill in.")
                  + (d.NotRead.Length > 0 ? " " + DraftCopy.StepsNotRead(d.NotRead, d.LeftOut, MaxSteps) : "");
        }

        /// <summary>The words of a draft that have no step to stand for them: those the adding rule left with none, and those
        /// whose add is offered unticked but names no step to offer (the note is their only place). Author opens each as a
        /// step to fill in.</summary>
        internal static List<DraftWordsWithoutStep> WordsWithNoStep(Draft d) =>
            (d.NoStep ?? new List<DraftWordsWithoutStep>()).Concat((d.Unticked ?? new List<DraftWordsWithoutStep>()).Where(x => x.StepId == null)).ToList();

        /// <summary>A Plan draft as Author opens it (edit_draft open_in_author): a copy, with each clause no ask read
        /// (Draft.NotRead) a step to fill in after the change steps, bounded as the Workflows door keeps them
        /// (<see cref="KeptNotRead"/>). The stored draft is not touched (verifier on m11/sol-fixes-1, D6).</summary>
        internal static Draft WithNotReadSteps(Draft d)
        {
            var noStepWords = WordsWithNoStep(d);
            if ((d.NotRead == null || d.NotRead.Count == 0) && noStepWords.Count == 0) return d;
            var copy = DraftStore.Copy(d);
            var n = 0;
            // The words the adding rule left with no step come first, each a step to fill in with the rule's reason
            // (one ask reads at most MaxSteps clauses, so each compose or Add more words keeps at most that many).
            var noStep = noStepWords.Select(x => NotReadStep(x.Words, x.Reason));
            foreach (var st in noStep.Concat(KeptNotRead(d.NotRead ?? new List<string>(), DraftCopy.PlanWordsNotRead, DraftCopy.PlanRestNotRead)))
            {
                n++;
                copy.Steps.Add(new DraftStep
                {
                    Id = "not-read-" + n.ToString(CultureInfo.InvariantCulture), Number = copy.Steps.Count + 1, Title = st.Title, Performer = DraftPerformers.Assistant,
                    Phase = DraftPhases.Change, Body = st.Reason, Kept = true,
                });
            }
            var ordered = copy.Steps.Select((x, i) => (x, i)).OrderBy(x => x.x.Phase == DraftPhases.After ? 1 : 0).ThenBy(x => x.x.Number).ThenBy(x => x.i).Select(x => x.x).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].Number = i + 1;
            copy.Steps = ordered;
            return copy;
        }

        /// <summary>A workflow's title from its description: the first sentence, cut near 60 characters at a comma or a
        /// word (the same rule as the page's describedTitle).</summary>
        internal static string WorkflowTitle(string words)
        {
            var text = (words ?? "").Trim();
            var first = text.Split('.', (char)10)[0].Trim();
            if (first.Length == 0) first = text;
            if (first.Length <= 60) return first;
            var cut = first.Substring(0, 60);
            var comma = cut.LastIndexOf(',');
            var space = cut.LastIndexOf(' ');
            var at = comma > 30 ? comma : space > 30 ? space : cut.Length;
            return Regex.Replace(cut.Substring(0, at), @"[,;:\s]+$|\s+(?:and|then|or)$", "", RegexOptions.IgnoreCase);
        }

        /// <summary>The description as a draft Author can open as a format 3 workflow. EVERY op a step uses is a step of
        /// its own (Sol on m11/integration 568435d3, finding 3: "refresh Sales with validation", which Jev was sure asks for
        /// a refresh and the tests, used to save the refresh alone). An op Semanticus runs becomes that template's step
        /// (its action, questions, parts to write and checks, as compose_task builds it); any other op (a read) is your
        /// assistant's step, with the op in its brief; a placeholder is your assistant's with no brief. The first step a
        /// clause gives carries its title in the person's words, the others their template's name. A step after the
        /// change keeps the ones that follow it after the change, and a change step is moved up to the other changes,
        /// because that is the order a draft runs in.</summary>
        internal static Draft AsDraft(DescribedWorkflow d)
        {
            var draft = new Draft { Id = "described", Title = WorkflowTitle(d.Words), Words = d.Words, Source = DraftSources.Words };
            var after = false;
            var n = 0;
            DraftStep Assistant(string title, string brief)
            {
                var step = new DraftStep
                {
                    Id = "step-" + n.ToString(CultureInfo.InvariantCulture), Number = draft.Steps.Count + 1, Title = title,
                    Performer = DraftPerformers.Assistant, Phase = after ? DraftPhases.After : DraftPhases.Change, Brief = brief,
                };
                draft.Steps.Add(step);
                return step;
            }
            foreach (var st in d.Steps)
            {
                var first = true;
                foreach (var op in st.Ops.Length > 0 ? st.Ops : new string[] { null })
                {
                    n++;
                    var t = RunnableTemplate(op);
                    DraftStep step;
                    if (t != null)
                    {
                        step = LocalEngine.AddTemplateStep(draft, t, null);
                        if (first) step.Title = st.Title;
                    }
                    else
                    {
                        step = Assistant(first ? st.Title : JevDraftBuilder.Reads.FirstOrDefault(r => r.Op == op)?.Title ?? st.Title,
                            op == null ? null : "Use " + op + ".");
                        // A placeholder says why it has no action in What it does, as the seeded-steps fallback does.
                        if (op == null) step.Body = st.Reason;
                    }
                    if (step.Phase == DraftPhases.After) after = true;
                    first = false;
                }
            }
            var ordered = draft.Steps.Select((s, i) => (s, i)).OrderBy(x => x.s.Phase == DraftPhases.After ? 1 : 0).ThenBy(x => x.i).Select(x => x.s).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].Number = i + 1;
            draft.Steps = ordered;
            return draft;
        }

        private static string TitleOf(string clause)
        {
            var t = Regex.Replace(clause, @"\s+", " ");
            if (t.Length > MaxTitleChars) t = t.Substring(0, MaxTitleChars).TrimEnd();
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }
    }
}

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>A test's own answer to "has the compose.draft replay cleared its bar". Null on every real path, which
        /// reads the committed per-door switches (JevDraftBuilder.JevDraftsOnWorkflows and JevDraftsOnPlan).</summary>
        internal bool? JevDraftsForTest;

        /// <summary>Jev drafts on the Workflows door (Describe it in words): the committed per-door switch.</summary>
        private bool JevDraftsOnWorkflows => JevDraftsForTest ?? JevDraftBuilder.JevDraftsOnWorkflows;

        /// <summary>Jev drafts on the Plan door (compose_task from words, Add more words).</summary>
        private bool JevDraftsOnPlan => JevDraftsForTest ?? JevDraftBuilder.JevDraftsOnPlan;

        /// <summary>describe_workflow_steps: the steps a description names, for Author. Stores nothing and puts nothing on
        /// the Plan. Pro (Workflows), like the rest of authoring, and refused before anything is read or asked.</summary>
        public async Task<DescribedWorkflow> DescribeWorkflowStepsAsync(string words)
        {
            RequireProFeature();
            // compose_task's one limit on a person's own text, refused the same plain way (the coordinator, 27 Sep).
            PlanTasks.RequireWithinFullText(words?.Trim(), "a description", nameof(words));
            var d = DescribedSteps.From(words, bounded: true);
            if (d.Steps.Length == 0) return d;
            // From has read the steps by the adding rule already (item 3 of the M11 join). A workflow is a template that outlives this
            // model, and this door says it reads nothing in the model, so the rule was told no model names: a bare object is new, and
            // "to the Sales table" is a set (offered as a placeholder, the safe side).
            if (!JevDraftsOnWorkflows) return WithDocument(d);

            // A step past the limit is not asked: its place in the answers stays empty, and it stays a step to fill in.
            var asks = d.Steps.Select(x => x.NotRead ? Task.FromResult(new JevDraftOutcome()) : AskJevDraftAsync(x.Words, JevDraftBuilder.Scope.Describe)).ToArray();
            var outcomes = await Task.WhenAll(asks).ConfigureAwait(false);
            d.JevAsked = outcomes.Any(o => o.Asked);
            var note = outcomes.Select(o => o.Note).FirstOrDefault(n => n != null);
            if (note != null) d.JevNote = note;
            DescribedSteps.ApplyJev(d, outcomes.Select(o => o.Result).ToArray());
            return WithDocument(d);
        }

        /// <summary>The description as the format 3 file Author opens. A description that cannot be written as one keeps
        /// its steps, and Author seeds them one by one as before.</summary>
        private DescribedWorkflow WithDocument(DescribedWorkflow d)
        {
            try { d.Document = RenderDraftDocument(DescribedSteps.AsDraft(d)); }
            catch (Exception) { d.Document = null; }
            return d;
        }

        /// <summary>The Plan's words, asked the way Workflows asks them: once per step the words name, in parallel, and
        /// laid together by <see cref="DescribedSteps.MergeClauses"/> (the per-clause rule, 26 Sep, committed before the
        /// second fresh set: Jev's steps for a clause Jev was sure of, else that clause's word match as a kept step, so a
        /// clause the words name is never dropped). <paramref name="have"/> is the ops a draft already holds, for Add more
        /// words. Its Drafted is null when no clause was answered (the menu, as before). Unread is every clause no answered
        /// ask read and no step stands for, in the order typed: one past the limit (Sol, finding 4), or one whose ask came
        /// back unanswered (the service away, no permission, the budget spent) and that the word match could not place.
        /// The Plan keeps those words until an answered ask reads them (R3-D6b, the coordinator's rule of 27 Sep).</summary>
        /// <remarks><paramref name="earlierClauses"/> is how many of the first clauses are kept words from before (Add more words):
        /// with <paramref name="earlierWait"/>, more kept words still wait past the room one ask has, so words after them are
        /// unread (K4). Words past the limit one ask reads are unread after every clause read.</remarks>
        internal async Task<ClauseAsk> AskJevDraftByClauseAsync(string words, IEnumerable<string> have = null, int earlierClauses = 0, bool earlierWait = false,
            string wholeText = null, IReadOnlyList<string> tickedBefore = null)
        {
            var described = DescribedSteps.From(words, applyAddingRule: false);
            var ask = new ClauseAsk { LeftOut = described.LeftOut };
            var read = described.Steps.Where(c => !c.NotRead).ToArray();
            var outcomes = read.Length == 0 ? Array.Empty<JevDraftOutcome>()
                : await Task.WhenAll(read.Select(c => AskJevDraftAsync(c.Words, JevDraftBuilder.Scope.Compose))).ConfigureAwait(false);
            var none = outcomes.All(o => o.Result == null);
            var (tables, objects) = await ModelNamesAsync().ConfigureAwait(false);
            var unread = read.Select((_, i) => described.LeftOut > 0 || earlierWait && i < earlierClauses).ToArray();
            // Version 6: the belt reads every typing so far (the whole text), and Jev's last question sees it all.
            var whole = wholeText ?? words;
            var rule = DescribedSteps.RuleOf(read, outcomes.Select(o => o.Result).ToArray(), tables, unread, objects, AddingRule.WholeSigned(new[] { whole }));
            if (!none && outcomes.Any(o => o.Asked)) await DemoteWhenJevDoubtsAsync(read.Select(c => c.Words).ToList(), rule, whole, tables).ConfigureAwait(false);
            ask.ClauseWords = read.Select(c => c.Words).ToList();
            // A (version 6): the adds ticked from earlier typings are read again with the whole text, the clauses of this ask keeping
            // what Jev's answers said they got; Jev's last question sees them too. A step no longer read as kept is unticked by the caller.
            if (wholeText != null && tickedBefore != null && tickedBefore.Count > 0)
            {
                var wc = DescribedSteps.ClausesOf(wholeText, out _, out var ws);
                var wr = DescribedSteps.WholeRule(wc, ws, ask.ClauseWords, DescribedSteps.GotKept(read, outcomes.Select(o => o.Result).ToArray()), tables, objects, AddingRule.WholeSigned(new[] { wholeText }));
                var used = new HashSet<int>();
                var picks = new List<(string Marker, int At)>();
                foreach (var marker in tickedBefore)
                {
                    var at = Enumerable.Range(0, wc.Count).FirstOrDefault(k => !used.Contains(k) && string.Equals(wc[k], marker, StringComparison.Ordinal), -1);
                    if (at < 0) continue;
                    used.Add(at);
                    picks.Add((marker, at));
                }
                var still = picks.Where(x => wr[x.At].Tier == AddingRule.Tier.Kept && wr[x.At].Kind != null).ToList();
                var arr = still.Select(x => wr[x.At]).ToArray();
                if (outcomes.Any(o => o.Asked)) await DemoteWhenJevDoubtsAsync(still.Select(x => wc[x.At]).ToList(), arr, wholeText, tables).ConfigureAwait(false);
                for (var k = 0; k < still.Count; k++) wr[still[k].At] = arr[k];
                ask.Retier = picks.Select(x => (x.Marker, wr[x.At])).ToList();
            }
            var n = 0;
            foreach (var c in described.Steps)
            {
                if (c.NotRead) { ask.Unread.Add(c.Words); continue; }
                var at = n++;
                // With nothing answered the draft is the menu and no step stands for any clause: every clause is kept as not
                // read. Otherwise a clause gives what MergeClauses takes for it, and one that is left with no step is kept:
                // with the adding rule's reason when that rule took its step away or its words ask to take something away
                // (a true reason, never "not read"), else as not read when its ask was not answered.
                if (none) { ask.Unread.Add(c.Words); continue; }
                var r = outcomes[at].Result;
                // A clause that holds an adding verb and is not kept is offered unticked, with its words and the true line on why.
                if (rule[at].Tier == AddingRule.Tier.Unticked)
                {
                    ask.Unticked.Add(new DraftWordsWithoutStep { Words = c.Words, Reason = rule[at].Reason });
                    continue;
                }
                var allowed = DescribedSteps.Allowed(r, rule[at]);
                var fallback = DescribedSteps.FallbackTemplate(read, at, rule);
                // A step kept for the clause stands for it. Steps Jev only leaned to (dropped) stand for it too, unless its
                // words ask to take something away: then the note says no step here does that, beside them (verifier round 4, L2).
                if (fallback != null || (allowed != null && allowed.Any(x => x.Sure))) continue;
                if (allowed != null && allowed.Count > 0 && !rule[at].TakesAway) continue;
                var withheld = DescribedSteps.Adds(DescribedSteps.RunnableTemplate(c.Op)) || (r != null && r.Steps.Any(x => DescribedSteps.Adds(x.Template)));
                if (withheld || rule[at].TakesAway) ask.NoStep.Add(new DraftWordsWithoutStep { Words = c.Words, Reason = AddingRule.ReasonFor(rule[at]) });
                else if (r == null) ask.Unread.Add(c.Words);
            }
            // No ask left the machine (the point switched off, no permission, no service, the budget spent): Add more words
            // asks no one either until that changes, so the words kept are said with this reason, never with a promise
            // (verifier on m11/sol-fixes-1, D5).
            if (outcomes.Length > 0 && outcomes.All(o => !o.Asked)) ask.NotAsked = outcomes.Select(o => o.Note).FirstOrDefault(x => x != null);
            if (none) return ask;
            ask.Drafted = DescribedSteps.MergeClauses(read, outcomes.Select(o => o.Result).ToArray(), have, rule);
            ask.Drafted.LeftOut = described.LeftOut;
            ask.Drafted.NotRead = described.NotRead;
            // Each unticked note knows its step, so it is said only while that step stays unticked.
            foreach (var x in ask.Drafted.Steps.Where(x => x.Untick))
                x.Note = ask.Unticked.FirstOrDefault(u => u.Words == read[x.Clause].Words && u.StepId == null && !ask.Drafted.Steps.Any(y => y != x && y.Note == u));
            return ask;
        }

        /// <summary>The last check of the adding rule (version 6, C): for each add the rule keeps, one typed question with the whole
        /// text in view, asked in parallel. Anything but a sure yes makes the add unticked with a note; Jev can only demote.
        /// THE QUESTION FAILS CLOSED (the M11 join, verifier VR19): on Pro, Jev slow, off, refused, not answering, or words too long
        /// to ask about as a whole leave the add UNTICKED, never ticked, because a ticked add can run (an assistant's run_draft is the
        /// person's review) and "no sure yes" is not a reason to write something.</summary>
        internal async Task DemoteWhenJevDoubtsAsync(IReadOnlyList<string> clauses, AddingRule.Reading[] rule, string whole, IReadOnlyList<string> tables)
        {
            var kept = Enumerable.Range(0, rule.Length).Where(i => rule[i].Tier == AddingRule.Tier.Kept && rule[i].Kind != null).ToList();
            if (kept.Count == 0) return;
            if ((whole ?? "").Length > JevDraftBuilder.MaxWantedChars)
            {
                foreach (var i in kept) rule[i] = new AddingRule.Reading(AddingRule.Tier.Unticked, rule[i].Kind, AddingRule.NotCheckedReason, rule[i].TakesAway);
                return;
            }
            // ONE REQUEST FOR ALL THE ADDS (m11/jev-batch): they share the whole text and the table names, so it is one question for each
            // add over one snapshot. More adds than one request holds go in several, never dropped.
            var wanted = await AskJevWantedManyAsync(kept.Select(i => (clauses[i], rule[i].Kind)).ToList(), whole, tables).ConfigureAwait(false);
            for (var k = 0; k < kept.Count; k++)
                if (wanted[k] != true)
                    rule[kept[k]] = new AddingRule.Reading(AddingRule.Tier.Unticked, rule[kept[k]].Kind,
                        wanted[k] == false ? AddingRule.NotSureWantedReason : AddingRule.NotCheckedReason, rule[kept[k]].TakesAway);
        }

        /// <summary>Ask the last question for a list of adds, in one request per <see cref="JevDraftBuilder.MaxQuestions"/> of them: for
        /// each, true for a sure yes (at the point's bar), false for anything else that came back, null when nothing did (not asked,
        /// refused, no service, an error, or that add's own answer was refused). The answer is read add by add
        /// (DecisionRequest.PerQuestion), so one malformed answer leaves that add "not checked" and the rest stand.</summary>
        internal async Task<bool?[]> AskJevWantedManyAsync(IReadOnlyList<(string Clause, string Op)> adds, string whole, IReadOnlyList<string> tables)
        {
            var result = new bool?[adds.Count];
            var wanted = new List<(int At, string Clause, DraftMenu.Template Template)>();
            for (var i = 0; i < adds.Count; i++)
            {
                var t = DraftMenu.All.FirstOrDefault(x => x.Op == adds[i].Op);
                if (t != null) wanted.Add((i, adds[i].Clause, t));
            }
            for (var start = 0; start < wanted.Count; start += JevDraftBuilder.MaxQuestions)
            {
                var chunk = wanted.Skip(start).Take(JevDraftBuilder.MaxQuestions).ToList();
                try
                {
                    var s = _sessions.Current;
                    var point = DecisionRegistry.Require(DecisionRegistry.ComposeDraft);
                    var (snapshot, questions) = JevDraftBuilder.BuildWantedMany(chunk.Select(x => (x.Clause, x.Template)).ToList(), whole, tables);
                    var fence = s == null ? null : new DecisionSessionFence(s.Id, s.Revision, () =>
                    {
                        var current = _sessions.Current;
                        return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
                    });
                    var asked = await Splitter.AskAsync(Decisions, point.Name, snapshot, questions,
                        isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                        enabled: DecisionPointEnabled(point.Name),
                        fence: fence,
                        ct: CancellationToken.None,
                        perQuestion: true).ConfigureAwait(false);
                    if (!asked.Asked || !asked.Answers.Decided) continue;
                    for (var k = 0; k < chunk.Count; k++)
                    {
                        var a = asked.Answers.For(questions[k].Name);
                        if (a != null) result[chunk[k].At] = a.Choice == "yes" && a.Confidence >= point.ConfidenceBar;
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        /// <summary>The model's table names (K3 of the adding rule: "to the Sales table"); none when no model is open.</summary>
        internal async Task<string[]> TableNamesAsync() => (await ModelNamesAsync().ConfigureAwait(false)).Tables;

        /// <summary>The model's table names (K3) and the names of its tables, columns, measures and roles (K2: a bare object is
        /// new when the model has no thing of that name); none when no model is open.</summary>
        internal async Task<(string[] Tables, string[] Objects)> ModelNamesAsync()
        {
            var s = _sessions.Current;
            if (s == null) return (Array.Empty<string>(), Array.Empty<string>());
            try
            {
                return await s.ReadAsync(m =>
                {
                    var tables = m.Tables.Select(t => t.Name).ToArray();
                    var objects = tables.Concat(m.Tables.SelectMany(t => t.Columns.Select(c => c.Name)))
                        .Concat(m.AllMeasures.Select(x => x.Name)).Concat(m.Roles.Select(r => r.Name)).Distinct(StringComparer.Ordinal).ToArray();
                    return (tables, objects);
                }).ConfigureAwait(false);
            }
            catch (Exception) { return (Array.Empty<string>(), Array.Empty<string>()); }
        }

        /// <summary>What asking the Plan's words clause by clause came to (see AskJevDraftByClauseAsync).</summary>
        internal sealed class ClauseAsk
        {
            public JevDraftBuilder.Result Drafted;
            public List<string> Unread = new List<string>();
            /// <summary>The words of each clause this ask read, in order: the clause a step's <see cref="JevDraftBuilder.Drafted.Clause"/> names.</summary>
            public List<string> ClauseWords = new List<string>();
            /// <summary>For each add ticked from an earlier typing, its clause and how the whole text reads it now.</summary>
            public List<(string Clause, AddingRule.Reading Reading)> Retier = new List<(string, AddingRule.Reading)>();
            /// <summary>Clauses read and left with no step by the adding rule, each with the rule's true reason.</summary>
            public List<DraftWordsWithoutStep> NoStep = new List<DraftWordsWithoutStep>();
            /// <summary>Clauses whose add is offered unticked (the rule's tier), each with the person's words and the one true
            /// line on why. A step's own note carries its step's id once the step is on the draft.</summary>
            public List<DraftWordsWithoutStep> Unticked = new List<DraftWordsWithoutStep>();
            public int LeftOut;
            /// <summary>Why no clause was asked at all ("Jev was not asked: ..."), or null when one was.</summary>
            public string NotAsked;
        }

        /// <summary>What one ask to compose.draft came to: the engine's reading of Jev's answer, or null with the plain
        /// reason it was not asked or not used.</summary>
        internal sealed class JevDraftOutcome
        {
            public bool Asked;
            public JevDraftBuilder.Request Request;
            public JevDraftBuilder.Result Result;
            public string Note;
        }

        /// <summary>Ask compose.draft about these words: the engine lists the templates and the model's options, Jev
        /// answers, the engine reads the answer back. Never throws for an ordinary failure; the outcome says why.</summary>
        internal async Task<JevDraftOutcome> AskJevDraftAsync(string words, JevDraftBuilder.Scope scope)
        {
            var outcome = new JevDraftOutcome();
            var s = _sessions.Current;
            var sources = JevDraftBuilder.Sources(scope);
            string[] tables = Array.Empty<string>();
            var options = new Dictionary<string, DraftOption[]>(StringComparer.Ordinal);
            if (s != null)
                tables = await s.ReadAsync(m =>
                {
                    foreach (var from in sources) options[from] = OptionsOf(m, from);
                    return m.Tables.Select(t => t.Name).ToArray();
                }).ConfigureAwait(false);
            var point = DecisionRegistry.Require(DecisionRegistry.ComposeDraft);
            outcome.Request = JevDraftBuilder.Build(words, scope, tables, from => options.TryGetValue(from, out var o) ? o : null);
            var fence = s == null ? null : new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });

            DecisionOutcome asked;
            try
            {
                asked = await Splitter.AskAsync(Decisions, point.Name, outcome.Request.Snapshot, outcome.Request.Questions,
                    isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                    enabled: DecisionPointEnabled(point.Name),
                    fence: fence,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome.Note = DraftCopy.JevNotAsked(ex.Message);
                return outcome;
            }
            if (!asked.Asked) { outcome.Note = DraftCopy.JevNotAsked(asked.SkippedReason); return outcome; }
            outcome.Asked = true;
            if (!asked.Answers.Decided) { outcome.Note = DraftCopy.JevNotUsed(asked.Answers.NotDecidedReason); return outcome; }
            outcome.Result = JevDraftBuilder.Read(asked.Answers, point.ConfidenceBar, outcome.Request);
            return outcome;
        }
    }
}
