// The compose.draft chooser: steps from a person's words, chosen by Jev once its replay clears the bar (composer lane
// L5, composer contract section 8; Kane 25 Sep: "Jev really drafts. this was the plan all along").
//
// WHAT EXISTED BEFORE THIS FILE. Nothing asked Jev for steps. A draft from words opened as the menu (lane L5a) with a
// line that blamed Jev's confidence for an ask that never happened (the honesty lane replaced it), and describe_workflow_steps
// cut the words and matched them against the same fixed list. This file is the one chooser both doors use:
// compose_task (the Plan field, Ctrl J) asks it on the whole words, and describe_workflow_steps asks it once per step
// the words name.
//
// THE PATTERN: the engine lists and proves, Jev chooses.
//   The engine lists the candidates: every step template on the executable allowlist this door can run (DraftMenu.All,
//   the steps after the change, and on Workflows the two performance reads), and for each argument the engine can list
//   from the open model, the options (tables, partitions, measures...).
//   Jev answers two typed shapes and nothing else: for each template, whether the words ask for it (a noul), and for
//   each listed argument, one option or none (a choice). The answer reader refuses any id that was not offered.
//   The engine orders the kept steps by what depends on what (Rank), drops the unsure ones (they arrive dropped, for a
//   person to keep), and turns an unsure pick into a "You choose" question. On the Plan a clause Jev is sure of nothing
//   in takes its word match, one step per clause (DescribedSteps.MergeClauses); only when neither Jev nor the words
//   name a step does the draft open as the menu. Titles, questions and every word shown are the engine's; Jev supplies
//   ids only.
//
// THE BAR, per door (Kane, 26 Sep). Both doors cleared their bars on fresh sets and Jev drafts on both
// (JevDraftsOnWorkflows, JevDraftsOnPlan). Free never asks.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine.Composer
{
    internal static class JevDraftBuilder
    {
        /// <summary>WHETHER JEV DRAFTS, per door (composer contract section 8, replay-gate.md; Kane, 26 Sep: each door is
        /// judged on its own bar, the Pro path at 80% on a fresh set). The fresh-set run is in
        /// docs/redesign/jev-compose-replay.md. Workflows (describe_workflow_steps) scored 93.4% on the first fresh set.
        /// The Plan (compose_task from words, Add more words), with its per-clause rule, cleared Kane's 26 Sep bar on the
        /// second fresh set: 88.8% against the word match's 67.5%, exact McNemar p 0.0002, 5 dropped and 4 wrong steps
        /// against 27 and 11. Pro only either way: a free session is refused before anything is asked.</summary>
        internal const bool JevDraftsOnWorkflows = true;
        internal const bool JevDraftsOnPlan = true;

        public const string Include = "include";
        /// <summary>The last question of the adding rule (version 6, C): with the whole text in view, does the person want the
        /// thing an add makes to exist when the work is done. yes, no or unsure; Jev can only demote with it.</summary>
        public const string Wanted = "wanted";
        public const string None = "none";
        public const string Prefix = "step:";

        /// <summary>The most questions one call asks: every candidate's include first, then the listed arguments in
        /// template order until this is reached or the questions fill the room Jev's context leaves
        /// (<see cref="MaxQuestionBytes"/>). tools/relay/templates.mjs holds the relay's bound to it. Raised from 64 to 100 on 15 Oct 2026
        /// (m16/jev-batch): the last question for the adds goes in one request per this many, and a draft rarely has more than a handful.</summary>
        public const int MaxQuestions = 100;

        /// <summary>The most the questions of one call may weigh: what is left of the request body budget (JevTableBatch.MaxBodyBytes) once the
        /// words and the table names (at most about 20 KB) are counted.</summary>
        public const int MaxQuestionBytes = JevTableBatch.MaxBodyBytes - 30 * 1024;

        /// <summary>An argument is put to Jev only when the model lists this many options or fewer; a longer list is left
        /// for the person to choose from, because a pick among hundreds of columns is not a draft decision.</summary>
        public const int MaxPickOptions = 40;

        public const int MaxTables = 60;

        /// <summary>Which door asks, and so which templates it may draft.</summary>
        internal enum Scope
        {
            /// <summary>compose_task: a draft runs change steps and steps after the change. The two performance reads are
            /// left out, because a draft runs every read BEFORE the change, which is not what "test it" means.</summary>
            Compose,
            /// <summary>describe_workflow_steps: a workflow step for the assistant, so the reads are offered too.</summary>
            Describe,
        }

        /// <summary>The two reads a described step may use ("test its performance"). Workflows only.</summary>
        internal static readonly DraftMenu.Template[] Reads =
        {
            DraftMenu.T("performance", "Test performance", "benchmark_dax", "performance", "benchmark", "speed", "slow", "test"),
            DraftMenu.T("profile", "Profile a query", "profile_dax", "profile", "bottleneck", "storage engine"),
        };

        /// <summary>The order kept steps run in: what a step depends on comes first. Engine-owned; Jev never orders.</summary>
        private static readonly string[] Rank =
        {
            "import", "add-column", "data-type", "relate", "date-table", "calc-group", "add-measure", "change-formula",
            "format", "folder", "summarize", "sort-by", "category", "hide", "rename", "synonyms", "describe", "role",
            "performance", "profile",
            "refresh", "run-tests", "save", "publish",
        };

        /// <summary>What each step template is for, in plain words, with the kinds of words a person uses for it. This is
        /// what Jev reads to decide whether the words ask for the step: engine-written, fixed, and tuned only on the first
        /// labelled set (docs/redesign/jev-compose-replay.md), never on the fresh one.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Guide = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["add-measure"] = "creates a new measure (a calculation such as a total, count, average, ratio, rate or KPI). Words like: add a Total Sales measure, create a meaure, new KPI, calculate the average order value",
            ["describe"] = "writes a description on a table, column or measure. Words like: describe, document, explain, add descriptions for Copilot",
            ["hide"] = "hides a column or table from report authors. Words like: hide, make invisible, hide the key columns",
            ["format"] = "sets a measure's number format. Words like: format as currency, show as a percent, two decimals",
            ["folder"] = "puts measures or columns into a display folder. Words like: put in a folder, organise, tidy up into folders",
            ["rename"] = "renames a table, column or measure. Words like: rename X to Y, call it Y",
            ["synonyms"] = "adds other names Copilot and Q&A understand for an object. Words like: synonyms, also called, so Copilot understands revenue",
            ["relate"] = "creates a relationship between two tables. Words like: relate, join, link, connect Sales to Customer, create the relationship",
            ["date-table"] = "marks a table as the model's date table. Words like: mark as date table, set up the calendar",
            ["data-type"] = "changes a column's data type. Words like: set data types, make it a whole number, change to text or date, fix the types",
            ["summarize"] = "sets how a column adds up by default (sum, none, average). Words like: stop summing, do not summarize, summarize by average",
            ["sort-by"] = "sorts one column by another. Words like: sort Month Name by Month Number, order by",
            ["category"] = "sets a column's data category (city, country, URL, image). Words like: category, mark as city, geography",
            ["change-formula"] = "changes the formula of an existing measure. Words like: fix the formula, rewrite the measure, write a candidate formula, update the DAX",
            ["import"] = "imports a new table from the data source. Words like: import, load, bring in the Budget table",
            ["add-column"] = "adds a new column to a table. Words like: add a column, new column for the order year",
            ["calc-group"] = "creates a calculation group. Words like: calculation group, build the calculation group",
            ["role"] = "creates a security role. Words like: add a role, security for the sales team, row level security",
            ["refresh"] = "refreshes (reloads) the data of a table or the model. Words like: refresh Sales, reload, process, update the data, refresh the model",
            ["run-tests"] = "runs the model's saved checks (tests) after a change. Words like: run the tests, run the saved checks, check it is correct, verify the numbers, validate",
            ["save"] = "saves the model to its files. Words like: save, save the model, save changes",
            ["publish"] = "publishes (deploys) the model to a stage or workspace. Words like: publish, deploy, release, promote to test or production",
            ["performance"] = "times a query or measure to see how fast it is. Words like: test its performance, benchmark, how fast is it, time it. Not for finding out why something is slow",
            ["profile"] = "looks inside a slow query to find where the time goes. Words like: find out why it is slow, profile, find the bottleneck, where the time goes",
        };

        internal static int RankOf(string templateId)
        {
            var i = Array.IndexOf(Rank, templateId);
            return i < 0 ? Rank.Length : i;
        }

        internal static DraftMenu.Template[] Candidates(Scope scope) =>
            DraftMenu.All.Concat(DescribedSteps.AfterTheChange).Concat(scope == Scope.Describe ? Reads : Array.Empty<DraftMenu.Template>())
                .Where(t => LocalEngine.OpSurface.Methods.ContainsKey(t.Op))
                .GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.First())
                .ToArray();

        public static string Q(string templateId, string what) => Prefix + templateId + ":" + what;

        /// <summary>A required argument the engine lists from the model, with the enumerator it lists from.</summary>
        internal static IEnumerable<(string Arg, string From)> ListedArgs(DraftMenu.Template t)
        {
            foreach (var prm in OpParameterKinds.Parameters(t.Op) ?? Array.Empty<OpParameterKinds.OpParameter>())
            {
                if (!prm.Required || t.Literals.Any(l => l.Arg == prm.Name)) continue;
                var kind = OpParameterKinds.KindOf(t.Op, prm.Name) ?? "";
                if (!kind.StartsWith("pick:", StringComparison.Ordinal)) continue;
                var from = kind.Substring(5);
                if (OpParameterKinds.Enumerators.TryGetValue(from, out var spec) && spec.Source == "model") yield return (prm.Name, from);
            }
        }

        /// <summary>The enumerators a scope's candidates list from, so the engine can read them all in one pass.</summary>
        internal static string[] Sources(Scope scope) =>
            Candidates(scope).SelectMany(ListedArgs).Select(a => a.From).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>One argument put to Jev, with the options the engine listed for it.</summary>
        internal sealed class Listed
        {
            public string TemplateId;
            public string Arg;
            public string From;
            public DraftOption[] Options;
            public string Question => Q(TemplateId, Arg);
        }

        /// <summary>Everything one ask offered: what the words were, which templates, which arguments.</summary>
        internal sealed class Request
        {
            public string Words;
            public Scope Scope;
            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;
            public DraftMenu.Template[] Templates;
            public List<Listed> Listed = new List<Listed>();
        }

        /// <summary>Build the one request. <paramref name="options"/> answers an enumerator's options from the open model
        /// (null or empty when it lists none).</summary>
        internal static Request Build(string words, Scope scope, IReadOnlyList<string> tables, Func<string, DraftOption[]> options)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.ComposeDraft);
            var help = LocalEngine.OpSurface.Infos.ToDictionary(i => i.Name, i => i.Description, StringComparer.Ordinal);
            var templates = Candidates(scope);
            var r = new Request { Words = (words ?? "").Trim(), Scope = scope, Templates = templates };
            var questions = new List<DecisionQuestion>();

            foreach (var t in templates)
            {
                var what = Guide.TryGetValue(t.Id, out var g) ? g
                    : help.TryGetValue(t.Op, out var h) && !string.IsNullOrWhiteSpace(h) ? Trim(h, 140) : t.Title;
                questions.Add(new DecisionQuestion(Q(t.Id, Include), DecisionQuestionKind.Noul,
                    $"The words in draft_words describe work on a semantic model, maybe with typos and in several steps. Is one of those steps this one: {t.Title}, which {what}?",
                    new[]
                    {
                        new DecisionOption("yes", $"some part of the words asks for this: {t.Title}, which {what}"),
                        new DecisionOption("no", $"no part of the words asks for {t.Title}"),
                    }));
            }

            foreach (var t in templates)
                foreach (var (arg, from) in ListedArgs(t))
                {
                    if (questions.Count >= MaxQuestions || questions.Sum(JevTableBatch.QuestionBytes) >= MaxQuestionBytes) break;
                    var opts = (options?.Invoke(from) ?? Array.Empty<DraftOption>())
                        .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Ref) && o.Ref != None)
                        .GroupBy(o => o.Ref, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                    if (opts.Length == 0 || opts.Length > MaxPickOptions) continue;
                    var listed = new Listed { TemplateId = t.Id, Arg = arg, From = from, Options = opts };
                    r.Listed.Add(listed);
                    questions.Add(new DecisionQuestion(listed.Question, DecisionQuestionKind.Choice,
                        $"If the words ask for {t.Title}, which one does it work on? {DraftMenu.QuestionFor(arg, from)}",
                        opts.Select(o => new DecisionOption(o.Ref, "the words point to " + o.Label))
                            .Append(new DecisionOption(None, "the words point to none of these, or do not say"))));
                }

            r.Questions = questions;
            r.Snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("draft_words", r.Words),
                new KeyValuePair<string, object>("table_names", (tables ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(MaxTables).ToArray()),
            });
            return r;
        }

        /// <summary>The most of the whole text the last question sends: longer, Jev is not asked and the rule's answer stands.</summary>
        public const int MaxWantedChars = 20000;

        /// <summary>The last question for one add: <paramref name="clause"/> is the words the rule kept an add for, <paramref name="t"/>
        /// the step it makes, <paramref name="whole"/> every typing so far. One choice question, yes, no or unsure.</summary>
        internal static (IReadOnlyDictionary<string, object> Snapshot, DecisionQuestion Question) BuildWanted(
            string clause, DraftMenu.Template t, string whole, IReadOnlyList<string> tables)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.ComposeDraft);
            var what = Guide.TryGetValue(t.Id, out var g) ? g : t.Title;
            var q = new DecisionQuestion(Q(t.Id, Wanted), DecisionQuestionKind.Choice,
                $"The words in draft_words describe work on a semantic model, maybe with typos, in several typings and in several steps. One step of them reads: \"{(clause ?? "").Trim()}\". "
                + $"That step would make something new: {t.Title}, which {what}. When ALL the work in draft_words is done, does the person want that new thing to exist in the model?",
                new[]
                {
                    new DecisionOption("yes", "the words, all of them, leave the new thing wanted: nothing in draft_words takes it back, removes it, puts it on a list to remove or says it is not wanted"),
                    new DecisionOption("no", "somewhere in draft_words, before or after that step, the person takes it back, removes it, puts it on a list to remove or says it is not wanted"),
                    new DecisionOption("unsure", "draft_words do not settle whether the person wants it"),
                });
            var snapshot = DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("draft_words", (whole ?? "").Trim()),
                new KeyValuePair<string, object>("table_names", (tables ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(MaxTables).ToArray()),
            });
            return (snapshot, q);
        }

        /// <summary>The last question for EVERY add at once (m11/jev-batch): the snapshot is the same for all of them (the whole text and
        /// the table names), so it goes once with one question per add instead of one request each. Each question is exactly what
        /// <see cref="BuildWanted"/> asks for that add; the only difference is the name, which is the same "step:&lt;template&gt;:wanted" for
        /// the first add of a template and "step:&lt;template&gt;#2:wanted" for the second, because two adds of one template ("add a measure
        /// for margin and add a measure for cost") would otherwise ask the same name twice.</summary>
        internal static (IReadOnlyDictionary<string, object> Snapshot, IReadOnlyList<DecisionQuestion> Questions) BuildWantedMany(
            IReadOnlyList<(string Clause, DraftMenu.Template Template)> adds, string whole, IReadOnlyList<string> tables)
        {
            var questions = new List<DecisionQuestion>();
            IReadOnlyDictionary<string, object> snapshot = null;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (clause, template) in adds)
            {
                var (one, question) = BuildWanted(clause, template, whole, tables);
                snapshot = one;
                seen[template.Id] = seen.TryGetValue(template.Id, out var n) ? n + 1 : 1;
                questions.Add(seen[template.Id] == 1 ? question
                    : new DecisionQuestion(Q(template.Id + "#" + seen[template.Id].ToString(System.Globalization.CultureInfo.InvariantCulture), Wanted),
                        question.Kind, question.Instructions, question.Options));
            }
            return (snapshot, questions);
        }

        private static string Trim(string s, int max)
        {
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "...";
        }

        /// <summary>One step Jev asked for, read back through the engine's checks.</summary>
        internal sealed class Drafted
        {
            public DraftMenu.Template Template;
            /// <summary>True when the include cleared the bar; false when it leaned yes below the bar (arrives dropped).</summary>
            public bool Sure;
            /// <summary>Arguments Jev picked, sure, from the listed options: argument name to the option.</summary>
            public Dictionary<string, DraftOption> Picked = new Dictionary<string, DraftOption>(StringComparer.Ordinal);
            /// <summary>Listed arguments Jev was not sure of, which become questions for the person.</summary>
            public List<string> Unsure = new List<string>();
            /// <summary>The Plan's per-clause rule: Jev was not sure of this clause, so the words named the step.</summary>
            public bool FromWords;
            /// <summary>Which clause of the words asked gave this step (its place among the clauses read), so Add more
            /// words credits a step to the words that named it (R3-D6b-copy). -1 when not set.</summary>
            public int Clause = -1;
            /// <summary>The adding rule offers this step that adds unticked: never ticked, said with the person's words and
            /// one true line on why (<see cref="Note"/>). It arrives with Kept = false and is not a step Jev was unsure of.</summary>
            public bool Untick;
            /// <summary>The draft's note for an unticked step, which names its step once the step is on the draft.</summary>
            public DraftWordsWithoutStep Note;
        }

        internal sealed class Result
        {
            /// <summary>The steps in the engine's order, sure ones and unsure ones.</summary>
            public List<Drafted> Steps = new List<Drafted>();
            /// <summary>No step is kept: neither Jev (sure) nor, on the Plan, a clause's word match named one. The draft
            /// opens as the menu, and Workflows keeps the word match.</summary>
            public bool BelowBar => !Steps.Any(s => s.Sure || s.Untick);
            /// <summary>Every include was a sure no: answered no at the bar. A missing answer or a leaning no is not sure,
            /// so it leaves the step to the word match (m11/completeness D5).</summary>
            public bool SaidNone;
            /// <summary>Merged over the clauses (the Plan): every clause answered was answered surely, with a step Jev was sure
            /// of (kept, or set aside by the adding rule) or a sure no. Then a draft with no kept step does not say Jev was not
            /// sure (Sol on m11/completeness round 4): Jev was sure, and the draft's notes say why no step stands.</summary>
            public bool SureOfEach;
            /// <summary>The Plan's per-clause rule: how many steps the words name past the most one draft asks about
            /// (DescribedSteps.MaxSteps). They are not drafted; the draft names them (<see cref="NotRead"/>).</summary>
            public int LeftOut;
            /// <summary>The words of each of those steps, as typed, in order.</summary>
            public string[] NotRead = Array.Empty<string>();
        }

        /// <summary>Read the answer back. Nothing here trusts an id it did not offer, even though the answer reader has
        /// already refused one: a template is taken by the engine's own list, an option by its own listed options.</summary>
        internal static Result Read(DecisionAnswers answers, double bar, Request r)
        {
            var result = new Result();
            var anyYes = false;
            var allSureNo = true;
            foreach (var t in r.Templates)
            {
                var a = answers?.For(Q(t.Id, Include));
                if (a == null || a.Noul != true)
                {
                    // A missing answer or a leaning no is not sure: only a no at the bar says no step fits (m11/completeness D5,
                    // lost at the M11 join, where a leaning no made a Workflows step a placeholder whatever its words matched).
                    if (a == null || a.Noul != false || a.Confidence < bar) allSureNo = false;
                    continue;
                }
                anyYes = true;
                var d = new Drafted { Template = t, Sure = a.Confidence >= bar };
                foreach (var listed in r.Listed.Where(l => l.TemplateId == t.Id))
                {
                    var pick = answers.For(listed.Question);
                    var option = pick == null ? null : listed.Options.FirstOrDefault(o => string.Equals(o.Ref, pick.Choice, StringComparison.Ordinal));
                    if (option != null && pick.Confidence >= bar) d.Picked[listed.Arg] = option;
                    else if (pick == null || pick.Choice != None || pick.Confidence < bar) d.Unsure.Add(listed.Arg);
                }
                result.Steps.Add(d);
            }
            result.SaidNone = !anyYes && allSureNo;
            result.Steps = result.Steps.OrderBy(s => RankOf(s.Template.Id)).ThenBy(s => Array.IndexOf(r.Templates, s.Template)).ToList();
            return result;
        }

        /// <summary>True when Semanticus could run this op with nothing written: it is on the executable allowlist and no
        /// required parameter is one only a writer can supply. Who does a described step is this rule's, not Jev's.</summary>
        public static bool NeedsNothingWritten(string op)
        {
            if (!OpParameterKinds.IsExecutable(op)) return false;
            var ps = OpParameterKinds.Parameters(op);
            if (ps == null) return false;
            return !ps.Any(p => p.Required && OpParameterKinds.AuthoredKind(op, p.Name) != null);
        }
    }
}
