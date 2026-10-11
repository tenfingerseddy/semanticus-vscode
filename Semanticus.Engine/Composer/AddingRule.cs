// THE ADDING RULE, version 5 (the coordinator, 29 Sep 19:05, from the advisor's analysis; it replaces the take-away veto of
// versions 2 to 4, and version 3 a and c and version 4 g, h, i and l stand). This file is the whole rule, pure and on its own,
// so m11/completeness and m11/sol-fixes-1 can carry it word for word and read words the same way.
//
// The harm is a KEPT add of the thing the words take away, put on a list to take away, or take back. Words cannot list every
// way to say that, so the rule stops counting take-away words and reads the shape of the ask. A NEW thing ("a role", "a new
// measure") cannot be the thing a person wants taken away; a thing that already exists ("the Managers role", "Total Sales",
// "the same", "them"), usually going "to" a set, can be.
//
// An add is KEPT (ticked, runs on Run) only when all four hold:
//  K1 ASKS. An adding verb leads the clause after a lead-in ("can you add", "please create", "we would like a new ...",
//     "is it possible to add", "feel free to create", "hi, could you ..."). No lead-in may negate. Adding verbs: add, create,
//     make (never "make sure"), build, set up, define, import, introduce, give ... a new, relate, join, link, connect,
//     calculate, in their present forms; or a want of a NEW thing ("we need a new ...", h). Participles and past forms never
//     ask ("was added"). g: an adding verb whose object is a job, task, step, list, note, reminder, plan, check or ticket
//     asks for work, never for a model object. i: "add no new roles", "create zero measures", "don't add a role" never add.
//     l: "give X a new name, description, format, folder, display folder or label" is an edit.
//  K2 NEW AND NAMED. The verb's object starts with a, an, another, new, one, a number or some (never "some more of", and no
//     less, fewer or smaller before the head), and the clause names the kind the step makes: measure, KPI or calculation
//     (Add a measure); role or security (Add a role); column; calculation group; relationship. Import may name a definite
//     table ("import the Budget table"). Relate, join, link and connect may name tables only. An add of a kind the words did
//     not name is not the kept step.
//  K3 NOT INTO A SET. After the thing: no "to", "into", "onto" or "among" before a determiner or pronoun, except a table the
//     model has ("to the Sales table"); the kind is not named again ("a role ... the roles"); no them, those, these or ones;
//     and no word from the take-away sign list after the thing (the belt). The list is FROZEN: no review round adds to it.
//  K4 NOTHING UNREAD AFTER IT. Every later clause of the words, in this typing or after it, got a kept step, is a
//     pleasantry, or only names objects the model has, and no words the draft holds are still unread. A later clause that
//     is in a later sentence or holds an action of its own has to be one of those; a bare noun phrase of the same sentence
//     ("for the finance team") does not.
// UNTICKED (offered with the person's own words and one true line on why, never ticked) when the clause holds an adding verb
// or a want of a new thing and any of K1 to K4 fails. Jev never lifts an add above unticked: the engine proves, Jev chooses.
// NOT DRAFTED otherwise: no adding verb, or g, i, l.
//
// Callers pass what only they know, per clause: whether it got a kept step of its own (K4), the model's table names (K3), the
// model's object names (K2), whether words after it are still unread (K4), and whether the frozen sign list holds anywhere in
// the WHOLE text (all typings). Everything else is read from the words here. A clause that fails the rule is kept with a true
// reason, never as a ticked step.
//
// VERSION 6 (the coordinator, 30 Sep 00:10, after the version 5 held-out run failed both ways; v5 stands except):
//  A  THE WHOLE TEXT, EVERY TIME. Every typing re-reads all the words so far as one text and re-tiers every add already
//     drafted, ticked ones included (the callers do it; this file reads the text they hand it).
//  B  WHOLE-TEXT BELT, DEMOTE ONLY. The frozen sign list is read over the whole text: any sign anywhere demotes a KEPT add to
//     UNTICKED, never the reverse (WholeSigned; Read's wholeSigned).
//  C  JEV AS THE LAST CHECK, DEMOTE ONLY. Asked by the caller for each add this rule keeps; it can only make an add unticked.
//  D  PLAIN ADDS READ AS PEOPLE WRITE THEM. K1: any words before the adding verb are a lead-in unless they negate it or make it
//     a question about ability ("can you", "could we" still ask); adding verbs include "pull in", "bring in" and a bare want
//     of a new thing ("new X", "I'd love a X", "we need a X"). K2: the object is new when marked new, or when it is BARE (no
//     determiner) and names a kind and a thing the model does not have ("add measure X"); "calc group" is a kind word. K4: a
//     trailing clause that explains or describes ("which ...", "that's why ...", "since ...", "no filters", "they see
//     everything") and asks for no change is not unread.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Composer
{
    internal static class AddingRule
    {
        /// <summary>What the rule makes of a clause.</summary>
        internal enum Tier
        {
            /// <summary>The clause holds no adding verb (or asks for work, none, an edit): no add is drafted for it.</summary>
            NotDrafted,
            /// <summary>The clause holds an adding verb or a want of a new thing and a check failed: the add is offered, not ticked.</summary>
            Unticked,
            /// <summary>All of K1 to K4 hold: the add is ticked.</summary>
            Kept,
        }

        /// <summary>What the rule reads in one clause.</summary>
        internal readonly struct Reading
        {
            public Reading(Tier tier, string kind, string reason, bool takesAway)
            {
                Tier = tier; Kind = kind; Reason = reason; TakesAway = takesAway;
            }
            public Tier Tier { get; }
            /// <summary>The op of the step the clause names by its kind ("create_role"), or null when it names none the
            /// composer makes. An add is kept, or offered unticked, only as this step.</summary>
            public string Kind { get; }
            /// <summary>For an unticked clause, the one true line on why it is not ticked. Null otherwise.</summary>
            public string Reason { get; }
            /// <summary>The clause asks to take something away (a take-away sign in its words, or beside it when it has no
            /// action of its own). It only decides which reason a placeholder gives and where the menu lists the adds.</summary>
            public bool TakesAway { get; }
            /// <summary>The clause may draft a step that adds, ticked.</summary>
            public bool AsksToAdd => Tier == Tier.Kept;
            /// <summary>The clause holds an adding verb or a want of a new thing: an add is kept or offered for it.</summary>
            public bool HoldsAdd => Tier != Tier.NotDrafted;
        }

        /// <summary>A clause that asks to take something away, kept in place of a step.</summary>
        public const string TakeAwayReason = "These words ask to take something away, and no step here does that yet.";

        /// <summary>A clause whose only step would add, when its words do not ask to add.</summary>
        public const string NoAddReason = "These words do not ask to add anything, so no step that adds was chosen for them.";

        // The one true line for an unticked add, by the check that failed. Each is the note's second sentence.
        public const string NotAskedReason = "These words mention adding something but do not plainly ask for it. Tick it if you want it.";
        /// <summary>Words that ask to take something away, where the only step on offer would add (join-2 item B): NotAskedReason would be
        /// false for them, because they do not mention adding at all.</summary>
        public const string TakeAwayOfferedReason = "These words ask to take something away, and no step here does that. This step would add something instead, so tick it only if you want a new one.";
        public const string NotNewReason = "These words name something the model may already have. Tick it if you want a new one.";
        public const string NoKindReason = "These words do not say which kind of thing to make. Tick it if this is the step you want.";
        public const string IntoASetReason = "These words put it with other things, and those may be the ones to take away. Tick it if you want a new one.";
        public const string SignReason = "These words hold a word that can mean taking something away. Tick it if you want a new one.";
        public const string UnreadReason = "Words after it were not read as a step and could change it. Tick it if you want it as it stands.";
        /// <summary>Jev, asked last with the whole text in view, was not sure the person still wants it (C).</summary>
        public const string NotSureWantedReason = "Read as a whole, your words do not make clear that you still want this. Tick it if you do.";
        /// <summary>Jev could not be asked, or did not answer, the last question, so nothing checked that the person still wants it.</summary>
        public const string NotCheckedReason = "Nothing could check that you still want this, so it is offered, not ticked. Tick it if you do.";

        /// <summary>Whether a step's op adds something to the model.</summary>
        public static bool Adds(string op) => op != null && op.StartsWith("create_", StringComparison.Ordinal);

        /// <summary>The reason for a clause that gets no step at all (not drafted).</summary>
        public static string ReasonFor(Reading r) => r.TakesAway ? TakeAwayReason : NoAddReason;

        /// <summary>The rule over every clause of the words, in the order typed. <paramref name="sentences"/> gives each
        /// clause's sentence (see <see cref="Sentences"/>); null reads every clause as a sentence of its own, the strictest.
        /// <paramref name="gotKeptStep"/> says, per clause, whether it got a kept step that does not add (its Jev pick or
        /// its word match): K4 needs it for the clauses after an add; null reads every clause as having none.
        /// <paramref name="tables"/> are the model's table names (K3); null reads the model as having none.
        /// <paramref name="unreadAfter"/> says, per clause, that words after it are still unread (a clause past the limit
        /// one ask reads, or kept words waiting); null reads none.</summary>
        public static Reading[] Read(IReadOnlyList<string> clauses, IReadOnlyList<int> sentences = null,
            IReadOnlyList<bool> gotKeptStep = null, IReadOnlyCollection<string> tables = null, IReadOnlyList<bool> unreadAfter = null,
            IReadOnlyCollection<string> objects = null, bool wholeSigned = false)
        {
            var n = clauses.Count;
            int SentenceOf(int i) => sentences == null || i >= sentences.Count ? i : sentences[i];
            var words = new List<string>[n];
            var parsed = new Clause[n];
            var asks = new Ask[n];
            for (var i = 0; i < n; i++) { parsed[i] = Parse(clauses[i]); words[i] = parsed[i].Words; asks[i] = K1(words[i]); }
            var names = TableWords(tables);
            var have = ObjectNames(objects);

            // K1 to K3 read a clause on its own; K4 reads what comes after it.
            var shape = new Shaped[n];
            for (var i = 0; i < n; i++) shape[i] = Shape(parsed[i], asks[i], names, have);

            var tokens = new Dictionary<int, List<string>>();
            for (var i = 0; i < n; i++)
            {
                if (!tokens.TryGetValue(SentenceOf(i), out var list)) tokens[SentenceOf(i)] = list = new List<string>();
                list.AddRange(words[i]);
            }
            var signed = tokens.ToDictionary(x => x.Key, x => Signs(x.Value));
            var carries = new bool[n];   // the clause holds a take-away sign, or a word of one whose rest sits in a clause beside it
            for (var i = 0; i < n; i++)
            {
                var would = asks[i].Act is Act.Adds or Act.Job;
                carries[i] = signed[SentenceOf(i)] && (Signs(words[i]) || !would && HasSignWord(words[i]));
            }

            var read = new Reading[n];
            var inheritSign = false;   // a clause with no action of its own takes the take-away of the clause it follows
            for (var i = 0; i < n; i++)
            {
                var noAction = asks[i].Act is Act.None or Act.Pending;
                // A clause that asks for work ("create a task to take Total Sales") in a sentence that takes away says so.
                var takes = carries[i] || asks[i].Act == Act.Job && signed[SentenceOf(i)] || noAction && (signed[SentenceOf(i)] || inheritSign || i + 1 < n && carries[i + 1]);
                inheritSign = asks[i].Act == Act.None ? carries[i] || inheritSign : carries[i];

                var s = shape[i];
                if (s.Tier == Tier.Kept)
                {
                    var unread = unreadAfter != null && i < unreadAfter.Count && unreadAfter[i];
                    if (unread) s = new Shaped(Tier.Unticked, s.Kind, UnreadReason);
                    else
                        for (var j = i + 1; j < n; j++)
                        {
                            var later = SentenceOf(j) != SentenceOf(i) || HasAction(words[j], asks[j]) || Retracts(words[j]);
                            if (!later) continue;
                            var mapped = shape[j].Tier != Tier.NotDrafted ? shape[j].Tier == Tier.Kept
                                : gotKeptStep != null && j < gotKeptStep.Count && gotKeptStep[j] && LeadsWithAction(words[j]) || Pleasantry(words[j]) || NamesOnlyModelObjects(words[j], names);
                            if (mapped) continue;
                            s = new Shaped(Tier.Unticked, s.Kind, UnreadReason);
                            break;
                        }
                }
                // B: a sign of the frozen list anywhere in the whole text (all typings) demotes a kept add, never the reverse.
                if (s.Tier == Tier.Kept && wholeSigned) s = new Shaped(Tier.Unticked, s.Kind, SignReason);
                // NotAskedReason says the words mention adding; words that take something away do not, so they get a line that is true.
                var reason = s.Tier == Tier.Unticked ? (takes && s.Reason == NotAskedReason ? TakeAwayOfferedReason : s.Reason) : null;
                read[i] = new Reading(s.Tier, s.Kind, reason, takes);
            }
            return read;
        }

        /// <summary>Whether clause <paramref name="i"/> of <paramref name="clauses"/> may draft a step that adds, ticked.</summary>
        public static bool AsksToAdd(IReadOnlyList<string> clauses, int i, IReadOnlyList<int> sentences = null,
            IReadOnlyList<bool> gotKeptStep = null) => Read(clauses, sentences, gotKeptStep)[i].AsksToAdd;

        /// <summary>One clause on its own: whether it may draft a step that adds, ticked. <paramref name="sentence"/> is the
        /// rest of its sentence (null when it is a sentence on its own), read as the clause before it. When it may not,
        /// <paramref name="reason"/> is the true reason for its placeholder or its unticked step; null when it may.</summary>
        public static bool MayAdd(string clause, string sentence, out string reason)
        {
            var r = sentence == null ? Read(new[] { clause })[0] : Read(new[] { sentence, clause }, new[] { 0, 0 })[1];
            reason = r.AsksToAdd ? null : r.Tier == Tier.Unticked ? r.Reason : ReasonFor(r);
            return r.AsksToAdd;
        }

        /// <summary>Each clause's sentence, from where its words sit in <paramref name="text"/> (start and end, as cut): a
        /// sentence ends at ! or ?, at ";", at a full stop before a space or the end ("1.5" does not end one, nor the stops
        /// of "e.g." and "i.e."), and at a line break. A clause whose own words run over such an end joins the sentences it
        /// touches into one (the safe side).</summary>
        public static int[] Sentences(string text, IReadOnlyList<(int Start, int End)> clauses)
        {
            text ??= "";
            var ends = new int[text.Length + 1];   // ends[p]: how many sentence ends come before position p
            for (var p = 0; p < text.Length; p++)
            {
                var c = text[p];
                var end = c is '!' or '?' or ';' or (char)10 or (char)13
                    || c == '.' && (p + 1 == text.Length || char.IsWhiteSpace(text[p + 1])) && !IsAbbreviationStop(text, p);
                ends[p + 1] = ends[p] + (end ? 1 : 0);
            }
            var parent = Enumerable.Range(0, ends[text.Length] + 1).ToArray();
            int Find(int s) { while (parent[s] != s) s = parent[s] = parent[parent[s]]; return s; }
            var spans = new List<(int, int)>();
            foreach (var (start, stop) in clauses)
            {
                // The clause's own words: from its first letter or digit to its last, so the stop that ends it is not in it.
                int a = Math.Clamp(start, 0, text.Length), b = Math.Clamp(stop, a, text.Length) - 1;
                while (a <= b && !char.IsLetterOrDigit(text[a])) a++;
                while (b >= a && !char.IsLetterOrDigit(text[b])) b--;
                var first = ends[Math.Min(a, text.Length)];
                var last = b >= a ? ends[b] : first;
                for (var s = first + 1; s <= last; s++) parent[Find(s)] = Find(first);
                spans.Add((first, last));
            }
            return spans.Select(x => Find(x.Item1)).ToArray();
        }

        /// <summary>Whether the full stop at <paramref name="p"/> is the last of "e.g." or "i.e.", which never ends a sentence.</summary>
        private static bool IsAbbreviationStop(string text, int p)
        {
            if (p < 3 || text[p - 2] != '.') return false;
            var pair = string.Concat(char.ToLowerInvariant(text[p - 3]), char.ToLowerInvariant(text[p - 1]));
            return (pair == "eg" || pair == "ie") && (p == 3 || !char.IsLetterOrDigit(text[p - 4]));
        }

        /// <summary>The text with the stops of "e.g." and "i.e." blanked to a middle dot, the same length, so a cut at a
        /// full stop passes them.</summary>
        internal static string MaskAbbreviations(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0) return text ?? "";
            var chars = text.ToCharArray();
            for (var p = 3; p < chars.Length; p++)
            {
                if (chars[p] != '.' || !IsAbbreviationStop(text, p)) continue;
                chars[p] = '·';
                chars[p - 2] = '·';
            }
            return new string(chars);
        }

        // ---- K1: reading one clause's ask ----------------------------------------------------------------------------

        /// <summary>A clause's own action: an adding one, one that asks for work (a job, a list ...), another one (none, an
        /// edit, some other verb), one still to come after a framing verb ("make sure the old measures"), or none.</summary>
        private enum Act { Adds, Job, Other, Pending, None }

        /// <summary>Which adding verb asks: import, relate (and join, link, connect), a want of a new thing, or any other.</summary>
        private enum Family { Create, Import, Relate, Want }

        /// <summary>The clause's ask: its action, where its object starts (a word index, -1 when it has none) and which
        /// family of adding verb asked.</summary>
        private readonly struct Ask
        {
            public Ask(Act act, int at = -1, Family family = Family.Create) { Act = act; At = at; Family = family; }
            public Act Act { get; }
            public int At { get; }
            public Family Family { get; }
        }

        private static readonly Regex WordPattern = new Regex(@"[A-Za-z0-9]+(?:'[A-Za-z]+)*", RegexOptions.CultureInvariant);

        private static List<string> WordsOf(string clause) =>
            WordPattern.Matches((clause ?? "").Replace((char)0x2019, (char)39).Replace((char)0x2018, (char)39))
                .Select(m => m.Value.ToLowerInvariant()).ToList();

        /// <summary>K1: the clause's action, after its lead-ins.</summary>
        private static Ask K1(List<string> w)
        {
            var i = SkipLeadIn(w, 0);
            // A framing verb: the action is what follows it ("make sure to add ...", "make sure the old role is removed").
            var framed = Framing(w, i);
            if (framed > 0)
            {
                i = SkipLeadIn(w, framed);
                if (i < w.Count && AddingAt(w, i) is Ask framedAsk) return framedAsk;
                return new Ask(HasVerb(w, i) ? Act.Other : Act.Pending);
            }
            if (i >= w.Count) return new Ask(Act.None);
            if (AddingAt(w, i) is Ask adding) return adding;
            // "we need a new measure", "I'd like a brand new role": a want of a NEW thing, with nothing else said of it (h).
            // Version 6 (D): a bare want reads as an ask too: "new column in Products", "I'd love a role", "we need a role".
            if (w[i] == "new" && !HasHelping(w, i + 1)) return ObjectAct(w, i, Family.Want);
            if (Wanting.Contains(w[i]))
            {
                var k = i + 1;
                var det = -1;
                while (k < w.Count && WantFillers.Contains(w[k])) { if (det < 0 && w[k] is "a" or "an") det = k; k++; }
                if (k < w.Count && w[k] == "new" && !HasHelping(w, k + 1)) return ObjectAct(w, k + 1, Family.Want);
                if (k < w.Count && w[k] == "another" && !HasHelping(w, k + 1)) return ObjectAct(w, k, Family.Want);
                if (det >= 0 && k < w.Count && !HasHelping(w, k)) return ObjectAct(w, det, Family.Want);
            }
            // Any other verb is an action that does not add; a clause with no verb only names a thing.
            return new Ask(HasVerb(w, i) ? Act.Other : Act.None);
        }

        /// <summary>Whether a helping verb ("should", "is", "might") is among the words from <paramref name="i"/>: what a want of a
        /// new thing may not have after it. A name that happens to be a verb ("Currency Display") is not one.</summary>
        private static bool HasHelping(List<string> w, int i) => w.Skip(Math.Max(i, 0)).Any(Helping.Contains);

        /// <summary>At word i, an adding verb that asks (its present form, never a participle), read by what it is said of.
        /// Null when the word is not an adding verb here ("make the keys hidden" changes what is there).</summary>
        private static Ask? AddingAt(List<string> w, int i)
        {
            var lead = w[i];
            if (lead is "set" or "sets") return i + 1 < w.Count && w[i + 1] == "up" ? ObjectAct(w, i + 2, Family.Create) : null;
            if (lead is "pull" or "pulls" or "bring" or "brings") return i + 1 < w.Count && w[i + 1] == "in" ? ObjectAct(w, i + 2, Family.Import) : null;
            if (lead is "give" or "gives")
            {
                var at = w.FindIndex(i + 1, x => x is "new" or "another");
                if (at < 0) return null;
                var next = at + 1;
                while (next < w.Count && w[next] == "brand") next++;
                // The object starts at "new" or "another", which is what marks it new (K2).
                return next < w.Count && Edited.Contains(w[next]) ? new Ask(Act.Other, next) : ObjectAct(w, at, Family.Create);
            }
            if (lead is "make" or "makes" or "build" or "builds")
            {
                var k = i + 1;
                if (k < w.Count && w[k] is "me" or "us" or "them" or "it") k++;
                return k < w.Count && (Indefinite.Contains(w[k]) || w[k].All(char.IsDigit)) ? ObjectAct(w, k, Family.Create) : null;
            }
            if (!AddingVerbs.Contains(lead)) return null;
            var stem = lead.EndsWith("es", StringComparison.Ordinal) && AddingStems.Contains(lead.Substring(0, lead.Length - 2)) ? lead.Substring(0, lead.Length - 2)
                     : lead.EndsWith("s", StringComparison.Ordinal) && AddingStems.Contains(lead.Substring(0, lead.Length - 1)) ? lead.Substring(0, lead.Length - 1) : lead;
            var family = stem == "import" ? Family.Import : stem is "relate" or "join" or "link" or "connect" ? Family.Relate : Family.Create;
            return ObjectAct(w, i + 1, family);
        }

        /// <summary>What the words from <paramref name="k"/> (the verb's object) ask for: nothing (none, zero, "no new
        /// roles", "some more of"), work (the object is a job, task, list ...), or a model object.</summary>
        private static Ask ObjectAct(List<string> w, int k, Family family)
        {
            while (k < w.Count && w[k] is "me" or "us" or "them" or "it" or "for") k++;
            if (k >= w.Count) return new Ask(Act.Adds, k, family);
            if (w[k] is "no" or "none" or "zero" or "nothing" or "nobody" or "neither") return new Ask(Act.Other, k, family);
            if (w[k] == "some" && k + 2 < w.Count && w[k + 1] == "more" && w[k + 2] == "of") return new Ask(Act.Other, k, family);
            var head = k;
            while (head < w.Count && Determiners.Contains(w[head])) head++;
            // The head of the phrase: "a job that ...", "a list of ..."; "a job cost measure" is a measure.
            if (head < w.Count && Work.Contains(w[head]) && (head + 1 >= w.Count || Connectors.Contains(w[head + 1]) || Work.Contains(w[head + 1])))
                return new Ask(Act.Job, k, family);
            return new Ask(Act.Adds, k, family);
        }

        private static bool HasVerb(List<string> w, int i)
        {
            for (var k = Math.Max(i, 0); k < w.Count; k++)
            {
                // "group" after "calculation" or "calc" is the kind's own word, not the verb "to group".
                if (w[k] is "group" or "groups" && k > 0 && w[k - 1] is "calculation" or "calculations" or "calc") continue;
                if (Helping.Contains(w[k]) || Verbs.Contains(w[k]) || AddingForms.Contains(w[k])) return true;
            }
            return false;
        }

        /// <summary>Whether the clause holds an adding verb (as a verb, not "make sure") or a want of a new thing, wherever
        /// it sits: what makes a clause that fails K1 an unticked add rather than no add at all.</summary>
        private static bool HoldsAddingVerb(List<string> w)
        {
            for (var i = 0; i < w.Count; i++)
            {
                if (w[i] is "make" or "makes" && i + 1 < w.Count && w[i + 1] is "sure" or "certain" or "it") continue;
                // A word after "the", "a", "our" and the like is a noun ("the import", "a link"), not a verb that asks.
                if (i > 0 && NounMakers.Contains(w[i - 1])) continue;
                if (AddingVerbs.Contains(w[i])) return true;
                if (w[i] is "set" or "sets" && i + 1 < w.Count && w[i + 1] == "up") return true;
                if (w[i] is "pull" or "pulls" or "bring" or "brings" && i + 1 < w.Count && w[i + 1] == "in") return true;
                if (w[i] is "give" or "gives" && w.Skip(i + 1).Any(x => x is "new" or "another")) return true;
            }
            return w.Contains("new") && w.Any(Wanting.Contains);
        }

        private static int SkipLeadIn(List<string> w, int i)
        {
            for (var moved = true; moved && i < w.Count;)
            {
                moved = false;
                foreach (var phrase in LeadIns)
                {
                    if (!At(w, i, phrase)) continue;
                    i += phrase.Length;
                    moved = true;
                    break;
                }
            }
            return i;
        }

        /// <summary>At word i, a framing verb ("make sure", "ensure that", "check that"): the index after it, else 0.</summary>
        private static int Framing(List<string> w, int i)
        {
            foreach (var phrase in Framings)
            {
                if (!At(w, i, phrase)) continue;
                var end = i + phrase.Length;
                return end < w.Count && w[end] == "that" ? end + 1 : end;
            }
            return 0;
        }

        private static bool At(List<string> w, int i, string[] phrase)
        {
            if (i + phrase.Length > w.Count) return false;
            for (var k = 0; k < phrase.Length; k++) if (w[i + k] != phrase[k]) return false;
            return true;
        }

        /// <summary>Whether the clause has an action of its own (any verb).</summary>
        private static bool HasAction(List<string> w, Ask ask) => ask.Act != Act.None || HasVerb(w, 0);

        // ---- K2 and K3: the object's shape ---------------------------------------------------------------------------

        private readonly struct Shaped
        {
            public Shaped(Tier tier, string kind, string reason) { Tier = tier; Kind = kind; Reason = reason; }
            public Tier Tier { get; }
            public string Kind { get; }
            public string Reason { get; }
        }

        /// <summary>K1 to K3 for one clause: kept when the shape holds, unticked when the clause holds an adding verb and a
        /// check fails, else not drafted. K4 is read over the clauses after it.</summary>
        private static Shaped Shape(Clause c, Ask ask, HashSet<string> tables, HashSet<string> have)
        {
            var w = c.Words;
            var notDrafted = new Shaped(Tier.NotDrafted, null, null);
            if (ask.Act != Act.Adds)
            {
                // g, i, l and "make sure": work, none, zero, an edit. And no adding verb at all.
                if (ask.Act == Act.Job || ask.Act == Act.Other && ask.At >= 0) return notDrafted;
                return HoldsAddingVerb(w) && !w.Any(Negations.Contains) ? new Shaped(Tier.Unticked, NamedOp(w), NotAskedReason) : notDrafted;
            }

            // K2: new and named.
            var j = ask.At;
            while (j < w.Count && w[j] is "me" or "us" or "them" or "it" or "for") j++;
            var first = j < w.Count ? w[j] : "";
            var head = KindHead(w, j, exact: true);
            if (head < 0) head = KindHead(w, j, exact: false);
            var kind = head < 0 ? null : KindOf(w, head);
            string op;
            if (ask.Family == Family.Relate)
            {
                if (w.Any(x => KindWords.TryGetValue(x, out var k) && k is not ("table" or "relationship")))
                    return new Shaped(Tier.Unticked, null, NoKindReason);
                op = "create_relationship";
                if (head < 0) head = j;
            }
            else if (ask.Family == Family.Import)
            {
                if (j >= w.Count) return new Shaped(Tier.Unticked, null, NoKindReason);   // "cancel the Suppliers import": nothing to import
                if (kind != null && kind != "table") return new Shaped(Tier.Unticked, null, NoKindReason);
                op = "create_import_table";
                if (head < 0) head = j;
            }
            else
            {
                op = kind == null ? null : KindOps.GetValueOrDefault(kind);
                var marked = MarkedNew.Contains(first) || first.All(char.IsDigit) && first.Length > 0 || ask.Family == Family.Want;
                // Version 6 (D): a BARE object (no determiner: "add measure X", "create role X", "add Order Year column") that
                // names a kind is new when the model has no thing of that name. "the X", "that X" and "them" still are not.
                if (!marked && head >= 0 && op != null && j < w.Count && !MarkedNew.Contains(first) && !SetWords.Contains(first) && !NounMakers.Contains(first))
                    marked = !have.Contains(NameOfBare(c, j, head));
                if (!marked) return new Shaped(Tier.Unticked, op, NotNewReason);
                if (head < 0) return new Shaped(Tier.Unticked, null, NoKindReason);
                if (w.Skip(j).Take(head - j).Any(Reducing.Contains)) return notDrafted;
                if (op == null) return new Shaped(Tier.Unticked, null, NoKindReason);
            }

            // K3: not into a set, and no take-away word after the thing (the belt).
            var rest = head + 1 <= w.Count ? w.Skip(head + 1).ToList() : new List<string>();
            if (ask.Family != Family.Relate)
            {
                for (var q = 0; q < rest.Count; q++)
                {
                    if (rest[q] is not ("to" or "into" or "onto" or "among")) continue;
                    if (q + 1 >= rest.Count || !SetWords.Contains(rest[q + 1])) continue;
                    if (NamesAModelTable(rest, q + 1, tables)) continue;
                    return new Shaped(Tier.Unticked, op, IntoASetReason);
                }
                if (kind != null && rest.Any(x => KindWords.TryGetValue(x, out var again) && again == kind)) return new Shaped(Tier.Unticked, op, IntoASetReason);
                if (rest.Any(Pointers.Contains)) return new Shaped(Tier.Unticked, op, IntoASetReason);
            }
            if (rest.Count > 0 && Signs(rest)) return new Shaped(Tier.Unticked, op, SignReason);
            return new Shaped(Tier.Kept, op, null);
        }

        /// <summary>The first word from <paramref name="j"/> that names a kind, within six words and before a connector: as
        /// written, or (not <paramref name="exact"/>) with a slip of the fingers ("meaure"), tried only when no word names one
        /// as written, so "a measured column" is a column. A slip is one letter, on a word of six letters or more.</summary>
        private static int KindHead(List<string> w, int j, bool exact)
        {
            for (var h = j; h < Math.Min(w.Count, j + 6); h++)
            {
                if (exact ? KindWords.ContainsKey(w[h]) : SlipOfKind(w[h]) != null) return h;
                if (Connectors.Contains(w[h]) && h > j) break;
            }
            return -1;
        }

        private static string SlipOfKind(string word) =>
            word.Length < 6 ? null : SlippableKinds.FirstOrDefault(k => Math.Abs(k.Length - word.Length) <= 1 && OneSlip(k, word));

        private static readonly string[] SlippableKinds = { "measure", "measures", "column", "columns", "relationship", "relationships" };

        /// <summary>Whether <paramref name="b"/> is <paramref name="a"/> with one letter changed, dropped, added or swapped.</summary>
        private static bool OneSlip(string a, string b)
        {
            if (a == b) return false;
            var i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            if (a.Length == b.Length)
            {
                if (a.Substring(i + 1) == b.Substring(i + 1)) return true;   // one letter changed
                return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.Substring(i + 2) == b.Substring(i + 2);   // two swapped
            }
            return a.Length > b.Length ? a.Substring(i + 1) == b.Substring(i) : a.Substring(i) == b.Substring(i + 1);
        }

        /// <summary>The kind the word at <paramref name="at"/> names: "calculation group" is its own kind, and "calculation"
        /// alone is a measure.</summary>
        private static string KindOf(List<string> w, int at) =>
            !KindWords.TryGetValue(w[at], out var kind) ? SlipKindName(w[at])
            : kind == "calculation" && at + 1 < w.Count && w[at + 1] is "group" or "groups" ? "calculation group" : kind;

        private static string SlipKindName(string word) => SlipOfKind(word) is string k ? KindWords[k] : null;

        /// <summary>The step the first kind a clause names makes, for an add offered unticked when the clause did not lead with
        /// an adding verb (null when it names none the composer makes).</summary>
        private static string NamedOp(List<string> w)
        {
            var at = w.FindIndex(KindWords.ContainsKey);
            if (at >= 0 && KindOps.GetValueOrDefault(KindOf(w, at)) is string named) return named;
            // Import and relate name their step by the verb: "before I forget, relate ..." offers Relate two tables.
            for (var i = 0; i < w.Count; i++)
            {
                if (i > 0 && NounMakers.Contains(w[i - 1])) continue;
                if (AddingAt(w, i) is Ask a && a.Act == Act.Adds && a.Family != Family.Create && a.Family != Family.Want)
                    return a.Family == Family.Import ? "create_import_table" : "create_relationship";
            }
            return null;
        }

        // ---- the clause as typed: its words and where they sit ----------------------------------------------------------

        /// <summary>A clause's words, each with where it sits in the clause as typed (K2 reads the name of a bare object from
        /// the text between its words).</summary>
        private sealed class Clause
        {
            public string Raw;
            public List<string> Words;
            public List<(int Start, int End)> Spans;
        }

        private static Clause Parse(string clause)
        {
            var raw = (clause ?? "").Replace((char)0x2019, (char)39).Replace((char)0x2018, (char)39);
            var c = new Clause { Raw = raw, Words = new List<string>(), Spans = new List<(int, int)>() };
            foreach (Match m in WordPattern.Matches(raw)) { c.Words.Add(m.Value.ToLowerInvariant()); c.Spans.Add((m.Index, m.Index + m.Length)); }
            return c;
        }

        /// <summary>The name a BARE object gives its thing, as typed and lower-cased: the words after the kind word ("add measure
        /// Total Sales"), up to a connector or a sign that starts a formula ("=", "(", "["); or, when the kind word comes after
        /// them ("add Order Year column"), the words before it.</summary>
        private static string NameOfBare(Clause c, int first, int head)
        {
            if (head > first) return NameOf(c.Raw.Substring(c.Spans[first].Start, c.Spans[head].Start - c.Spans[first].Start));
            var from = c.Spans[head].End;
            var k = head + 1;
            if (k < c.Words.Count && c.Words[k] is "called" or "named") { from = c.Spans[k].End; k++; }
            var stop = c.Raw.Length;
            var brk = c.Raw.IndexOfAny(new[] { '=', '(', '[', '<', '>', '{' }, from);
            if (brk >= 0) stop = brk;
            for (; k < c.Words.Count && c.Spans[k].Start < stop; k++)
                if (Connectors.Contains(c.Words[k]) || NameStops.Contains(c.Words[k])) { stop = c.Spans[k].Start; break; }
            return NameOf(c.Raw.Substring(from, Math.Max(0, stop - from)));
        }

        /// <summary>Words that end a bare object's name beside the connectors ("add measure Rebate because the team asked").</summary>
        private static readonly HashSet<string> NameStops = new HashSet<string>(StringComparer.Ordinal)
            { "because", "since", "while", "although", "though", "but", "or", "then", "also", "too", "yes", "thanks" };

        private static string NameOf(string text) =>
            string.Join(" ", text.Split(new[] { ' ', (char)9, (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim('"', '\'', ',', '.', ';', ':', '-', '[', ']', '(', ')')).Where(x => x.Length > 0)).ToLowerInvariant();

        /// <summary>The model's object names (measures, columns, roles, tables), as <see cref="NameOf"/> writes them.</summary>
        private static HashSet<string> ObjectNames(IReadOnlyCollection<string> objects) =>
            objects == null ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(objects.Select(NameOf).Where(x => x.Length > 0), StringComparer.Ordinal);

        /// <summary>Whether the words from <paramref name="from"/> in <paramref name="rest"/> name a table the model has
        /// ("the Sales table"): the words before the next "table", less their determiners, are one of its table names.</summary>
        private static bool NamesAModelTable(List<string> rest, int from, HashSet<string> tables)
        {
            if (tables == null || tables.Count == 0) return false;
            var stop = rest.FindIndex(from, x => x == "table");
            if (stop < 0 || rest.Skip(from).Take(6).Contains("tables")) return false;
            var name = string.Join(" ", rest.Skip(from).Take(stop - from).SkipWhile(x => x is "the" or "a" or "an" or "this" or "that"));
            return name.Length > 0 && tables.Contains(name);
        }

        private static HashSet<string> TableWords(IReadOnlyCollection<string> tables) =>
            tables == null ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(tables.Select(t => string.Join(" ", WordsOf(t))).Where(t => t.Length > 0), StringComparer.Ordinal);

        // ---- K4: what comes after ------------------------------------------------------------------------------------

        private static readonly HashSet<string> Pleasantries = new HashSet<string>(StringComparer.Ordinal)
            { "thanks", "thank you", "please", "cheers", "ta", "thx", "ty", "much appreciated", "thank you so much", "thanks a lot", "thanks so much" };

        private static bool Pleasantry(List<string> w) => Pleasantries.Contains(string.Join(" ", w));

        private static readonly HashSet<string> ObjectFillers = new HashSet<string>(StringComparer.Ordinal)
            { "between", "and", "or", "the", "a", "an", "to", "with", "from", "on", "in", "of", "for", "table", "tables", "model" };

        /// <summary>Whether the clause only names objects the model has ("between Sales and Date"): every word is a
        /// connector or part of a table's name, and at least one is.</summary>
        private static bool NamesOnlyModelObjects(List<string> w, HashSet<string> tables)
        {
            if (tables.Count == 0 || w.Count == 0) return false;
            var known = new HashSet<string>(tables.SelectMany(t => t.Split(' ')), StringComparer.Ordinal);
            return w.All(x => ObjectFillers.Contains(x) || known.Contains(x)) && w.Any(known.Contains);
        }

        /// <summary>K4 (the M11 join): a later clause that only retracts what was said ("just kidding", "jk") has no action of its own, so it
        /// was ignored as a bare noun phrase of the same sentence and left a bare want ("we need a role X, just kidding") kept. It is an
        /// unread clause like any other: the add is offered unticked.</summary>
        private static bool Retracts(List<string> w) => w.Any(x => x is "kidding" or "joking" or "jk" or "nvm");

        /// <summary>Whether a clause leads with an action of its own, after its lead-ins ("hide Total Cost", "then format it"). A word
        /// match that lands on a word deeper in the clause ("never mind the currency formatting calc group") is not a step the
        /// clause asks for (K4).</summary>
        private static bool LeadsWithAction(List<string> w)
        {
            var i = SkipLeadIn(w, 0);
            return i < w.Count && (Verbs.Contains(w[i]) || AddingForms.Contains(w[i]) || AddingAt(w, i) != null);
        }

        /// <summary>Whether a sign of the frozen list holds anywhere in the typings taken as one text (version 6, B).</summary>
        internal static bool WholeSigned(IEnumerable<string> typings) =>
            Signs(WordsOf(string.Join((char)10, typings ?? Enumerable.Empty<string>())));

        // ---- take-away signs: the frozen list, now only a belt and a reason ------------------------------------------

        /// <summary>Whether a sentence's words (all its clauses, in order) hold a take-away sign anywhere.</summary>
        private static bool Signs(List<string> words)
        {
            var text = " " + string.Join(" ", words) + " ";
            return TakeAway.IsMatch(text)
                || NoLonger.IsMatch(text) && Needs.IsMatch(text)
                || NotThis.IsMatch(text) && NeededForms.IsMatch(text)
                || DoNot.IsMatch(text) && NeedWant.IsMatch(text);
        }

        /// <summary>Whether a clause holds a word of a take-away sign whose rest may sit in a clause beside it ("take Total
        /// Sales" of "take Total Sales and Total Cost out"). It decides only which reason a placeholder gives.</summary>
        private static bool HasSignWord(List<string> words) => SignWords.IsMatch(" " + string.Join(" ", words) + " ");

        private static readonly Regex TakeAway = new Regex(
            @" (?:remov(?:e|es|ed|ing|al|als)|delet(?:e|es|ed|ing|ion|ions)|drop(?:s|ped|ping)?|retir(?:e|es|ed|ing|ement)"
            + @"|clear(?:s|ed|ing)?|clean(?:s|ed|ing)?|cleanups?|exclu(?:de|des|ded|ding|sion)|deprecat(?:e|es|ed|ing|ion)|bin(?:s|ned|ning)?"
            + @"|dump(?:s|ed|ing)?|los(?:e|es|ing|t)|cut(?:s|ting)?|strip(?:s|ped|ping)?|unused|obsolete|redundant|unneeded|unnecessary"
            + @"|eras(?:e|es|ed|ing)|purg(?:e|es|ed|ing)|discard(?:s|ed|ing)?|eliminat(?:e|es|ed|ing|ion)|scrap(?:s|ped|ping)?"
            + @"|ditch(?:es|ed|ing)?|trash(?:es|ed|ing)?|wip(?:e|es|ed|ing)|disappear(?:s|ed|ing)?|prun(?:e|es|ed|ing)"
            + @"|trim(?:s|med|ming)?|decommission(?:s|ed|ing)?|sunset(?:s|ted|ting)?|archiv(?:e|es|ed|ing)|rid|less|fewer"
            + @"|(?:get|gets|got|gotten|getting|take|takes|took|taken|taking|throw|throws|threw|thrown|throwing) (?:\S+ )*?(?:out|away|off)"
            + @"|(?:go|goes|going|gone|went) away|(?:be|is|are|been) gone"
            + @"|(?:should|must|can|could|will|may|might|shall|would|(?:have|has|had|need|needs|ought|got) to)"
            + @" (?:(?:all|both|just|now|then|really|also|to|be) )*go|all go) ",
            RegexOptions.CultureInvariant);

        private static readonly Regex NoLonger = new Regex(@" no longer ", RegexOptions.CultureInvariant);
        private static readonly Regex Needs = new Regex(@" (?:need|needs|needed|used|use|required|wanted|want|wants) ", RegexOptions.CultureInvariant);
        private static readonly Regex NotThis = new Regex(@" (?:not|isn't|aren't|wasn't|weren't) ", RegexOptions.CultureInvariant);
        private static readonly Regex NeededForms = new Regex(@" (?:needed|required|wanted|used) ", RegexOptions.CultureInvariant);
        private static readonly Regex DoNot = new Regex(@" (?:don't|doesn't|didn't|do not|does not|did not) ", RegexOptions.CultureInvariant);
        private static readonly Regex NeedWant = new Regex(@" (?:need|needs|want|wants) ", RegexOptions.CultureInvariant);

        private static readonly Regex SignWords = new Regex(
            @" (?:take|takes|took|taken|taking|throw|throws|threw|thrown|throwing|get|gets|got|gotten|getting|go|goes|gone|going|went|away"
            + @"|out|off|not|no|longer|don't|doesn't|didn't|isn't|aren't|wasn't|weren't|needed|required|wanted) ",
            RegexOptions.CultureInvariant);

        // ---- the words ---------------------------------------------------------------------------------------------

        /// <summary>The kinds a clause may name, as the word for them: several words for one kind.</summary>
        private static readonly Dictionary<string, string> KindWords = new[]
        {
            ("measure", "measure"), ("measures", "measure"), ("kpi", "measure"), ("kpis", "measure"), ("calculation", "calculation"),
            ("calculations", "calculation"), ("calc", "calculation"), ("calcs", "calculation"), ("column", "column"), ("columns", "column"), ("role", "role"), ("roles", "role"),
            ("security", "role"), ("table", "table"), ("tables", "table"), ("relationship", "relationship"),
            ("relationships", "relationship"),
        }.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

        /// <summary>The step a kind of thing makes (the composer's add templates).</summary>
        private static readonly Dictionary<string, string> KindOps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["measure"] = "create_measure", ["calculation"] = "create_measure", ["role"] = "create_role", ["column"] = "create_column",
            ["relationship"] = "create_relationship", ["calculation group"] = "create_calculation_group",
        };

        /// <summary>What an object may start with to be a NEW thing (K2).</summary>
        private static readonly HashSet<string> MarkedNew = new HashSet<string>(StringComparer.Ordinal)
            { "a", "an", "another", "new", "one", "some", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

        private static readonly HashSet<string> Reducing = new HashSet<string>(StringComparer.Ordinal) { "less", "fewer", "smaller" };

        /// <summary>What "to", "into", "onto" or "among" may be followed by to put the thing into a set (K3): a determiner or
        /// a pronoun.</summary>
        private static readonly HashSet<string> SetWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "this", "that", "these", "those", "my", "our", "your", "their", "its", "his", "her", "all", "every", "each",
            "any", "both", "some", "another", "other", "no", "them", "it", "they", "him", "us", "you", "me", "one", "ones", "everything",
            "everyone", "anything", "whatever", "whichever", "same", "such",
        };

        /// <summary>A word before an adding-verb word that makes it a noun ("the import", "each link").</summary>
        private static readonly HashSet<string> NounMakers = new HashSet<string>(StringComparer.Ordinal)
            { "the", "a", "an", "this", "that", "these", "those", "each", "every", "our", "your", "my", "their", "its", "his", "her", "of" };

        private static readonly HashSet<string> Pointers = new HashSet<string>(StringComparer.Ordinal) { "them", "those", "these", "ones" };

        private static readonly HashSet<string> Negations = new HashSet<string>(StringComparer.Ordinal)
        {
            "no", "none", "zero", "not", "never", "don't", "doesn't", "didn't", "won't", "can't", "cannot", "shouldn't", "wouldn't",
            "nothing", "nobody", "neither", "nor",
        };

        private static readonly string[] AddingStems =
            { "add", "create", "make", "build", "define", "import", "introduce", "calculate", "relate", "join", "link", "connect" };

        /// <summary>The adding verbs that ask, in their present forms only ("add", "adds"): a closed, small set, matched as
        /// written. "added", "adding" and the rest describe a thing; they never ask to make one.</summary>
        private static readonly HashSet<string> AddingVerbs = new HashSet<string>(
            AddingStems.SelectMany(s => new[] { s, s + "s", s + "es" }), StringComparer.Ordinal);

        /// <summary>Every form of the adding verbs, participles too: any of them means the clause has a verb.</summary>
        private static readonly HashSet<string> AddingForms = Forms(AddingStems.Concat(new[] { "give", "set" }).ToArray());

        private static readonly HashSet<string> Wanting = new HashSet<string>(StringComparer.Ordinal)
            { "need", "needs", "want", "wants", "require", "requires", "like", "love" };

        /// <summary>Words that may stand between a want and "new" ("I'd like a brand new measure").</summary>
        private static readonly HashSet<string> WantFillers = new HashSet<string>(StringComparer.Ordinal)
            { "a", "an", "some", "brand", "really", "whole", "completely", "totally" };

        private static readonly HashSet<string> Indefinite = new HashSet<string>(StringComparer.Ordinal)
            { "a", "an", "another", "new", "one", "some", "more", "extra", "two", "three", "four", "five" };

        /// <summary>Words before the head of an object's phrase.</summary>
        private static readonly HashSet<string> Determiners = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "the", "another", "some", "more", "extra", "new", "brand", "one", "two", "three", "four", "five", "separate",
            "single", "quick", "small", "simple", "short", "big", "little", "fresh", "second", "next", "whole", "full",
        };

        /// <summary>What an adding verb may ask for that is work, not a model object (g).</summary>
        private static readonly HashSet<string> Work = new HashSet<string>(StringComparer.Ordinal)
        {
            "job", "jobs", "task", "tasks", "step", "steps", "list", "lists", "note", "notes", "reminder", "reminders", "plan", "plans",
            "check", "checks", "ticket", "tickets",
        };

        /// <summary>Words that follow the head of an object's phrase and start what is said of it.</summary>
        private static readonly HashSet<string> Connectors = new HashSet<string>(StringComparer.Ordinal)
        {
            "for", "of", "that", "which", "to", "about", "with", "in", "on", "from", "so", "and", "by", "when", "as", "if", "who",
            "where", "at", "into", "called", "named", "using", "before", "after", "every", "each", "please",
        };

        /// <summary>What "give ... a new" may name that changes what is there (l).</summary>
        private static readonly HashSet<string> Edited = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "names", "description", "descriptions", "format", "formats", "folder", "folders", "display", "label", "labels",
            "title", "titles", "caption", "captions", "colour", "color", "style", "look", "value", "values",
        };

        /// <summary>Helping verbs: what follows says what happens to the thing.</summary>
        private static readonly HashSet<string> Helping = new HashSet<string>(StringComparer.Ordinal)
        {
            "is", "are", "was", "were", "be", "been", "being", "am", "should", "must", "will", "would", "can", "could", "may",
            "might", "shall", "has", "have", "had", "do", "does", "did", "get", "gets", "got", "ought", "isn't", "aren't",
            "won't", "can't", "shouldn't", "don't", "doesn't", "needs", "need", "wants", "want",
        };

        /// <summary>Other actions a clause can have, in their forms: any of them means the clause has an action of its own
        /// that is not an add.</summary>
        private static readonly HashSet<string> Verbs = new HashSet<string>(
            Forms("hide", "show", "unhide", "format", "rename", "describe", "document", "explain", "refresh", "reload",
                "process", "publish", "deploy", "release", "save", "check", "test", "validate", "run", "change", "update",
                "fix", "move", "sort", "mark", "group", "organise", "organize", "display", "copy", "split", "merge",
                "replace", "use", "keep", "turn", "convert", "summarise", "summarize", "aggregate", "rewrite", "tidy", "label",
                "flag", "apply", "stay", "remain", "appear", "exist", "go", "undo", "revert", "restore",
                "remove", "delete", "drop", "retire", "clear", "clean", "exclude", "deprecate", "dump", "strip", "erase",
                "purge", "discard", "eliminate", "scrap", "trash", "wipe", "disappear", "prune", "trim", "decommission",
                "sunset", "archive", "bin", "vanish", "ditch", "pull", "bring")
            .Concat(new[] { "hidden", "hid", "shown", "ran", "kept", "put", "puts", "putting", "gone",
                "went", "done", "lost", "lose", "loses", "losing", "cut", "cuts", "cutting", "taken", "took", "take", "takes",
                "thrown", "threw", "throw", "throws", "made", "built" }),
            StringComparer.Ordinal);

        /// <summary>Words that only lead in to the action, tried longest first. None of them may negate.</summary>
        private static readonly string[][] LeadIns = new[]
        {
            "please", "kindly", "now", "also", "just", "then", "and", "so", "ok", "okay", "next", "first", "firstly", "finally",
            "lastly", "still", "maybe", "perhaps", "really", "i", "we", "you", "i'd", "we'd", "you'd", "let's", "lets", "let us",
            "let me", "can you", "could you", "would you", "will you", "can we", "could we", "shall we", "should we",
            "want to", "wants to", "would like to", "like to", "love to", "need to", "needs to", "have to", "has to", "got to",
            "gotta", "wanna", "should", "must", "can", "could", "will", "try to", "remember to", "don't forget to",
            "do not forget to", "go ahead and", "help me", "help me to", "it's time to", "time to", "want you to",
            "need you to", "would like you to", "like you to", "love you to", "think", "guess", "to",
            // the verifier's L1 list (round 6): polite forms that only move where the verb is found
            "be able to", "is it possible to", "would it be possible to", "feel free to", "i'd really appreciate it if you could",
            "appreciate it if you could", "if you could", "hi", "hello", "hey", "would", "we'll", "i'll", "you'll",
        }.Select(p => p.Split(' ')).OrderByDescending(p => p.Length).ToArray();

        /// <summary>Framing verbs: the action is what follows them.</summary>
        private static readonly string[][] Framings = new[]
        {
            "make sure", "makes sure", "making sure", "make certain", "be sure", "be certain", "ensure", "ensures",
            "check that", "check whether", "check if", "double check that", "see that", "see to it", "confirm that",
            "verify that", "make it so",
        }.Select(p => p.Split(' ')).OrderByDescending(p => p.Length).ToArray();

        /// <summary>A verb's forms: as written, with -s or -es, -d or -ed, and -ing (a final "e" dropped, a final
        /// consonant doubled).</summary>
        private static HashSet<string> Forms(params string[] stems)
        {
            var forms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in stems)
            {
                forms.Add(s);
                forms.Add(s + "s");
                forms.Add(s + "es");
                forms.Add(s + "ing");
                forms.Add(s.EndsWith("e", StringComparison.Ordinal) ? s + "d" : s + "ed");
                if (s.EndsWith("e", StringComparison.Ordinal)) forms.Add(s.Substring(0, s.Length - 1) + "ing");
                var last = s[s.Length - 1];
                if (s.Length > 2 && "bdgmnprt".IndexOf(last) >= 0 && "aeiou".IndexOf(s[s.Length - 2]) >= 0 && "aeiou".IndexOf(s[s.Length - 3]) < 0)
                {
                    forms.Add(s + last + "ed");
                    forms.Add(s + last + "ing");
                }
            }
            return forms;
        }
    }
}
