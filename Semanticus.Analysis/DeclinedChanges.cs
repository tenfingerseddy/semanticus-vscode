// The changes the person declined by accepting findings (m11/rule-clashes round 6).
//
// A waiver declines a CHANGE, an (object, property) write, not a rule id. It is read from the STORED acceptances, the
// Semanticus waiver records and Tabular Editor's own ignore annotations, and from nothing else: not from which findings
// fire now. A finding can stop firing for an unrelated reason (the plan's own Visibility step hides a key, and a rule that
// only looks at visible columns stops seeing it), and the decision the person recorded outlives that. What a stored
// acceptance declines comes from the rule it names (RuleAbout.cs), so a rule added later is covered with no edit here.
//
// Every path that writes or queues a change asks this one set: the change plan, Run the plan's rows, apply_safe_fixes,
// make_model_ai_ready (its safe fixes and its work queue), BPA Fix all, and the assistant's bpa_fix change set.
//
// TWO SEPARATE THINGS, NEVER MIXED (round 8). This set decides what no path may WRITE: (object, property), and for an own-ask
// rule the value. It does NOT decide what the pages call accepted or what the deploy gate lets pass. Those read the findings the
// person accepted, by their own rule id and object: BpaViolation.IsAccepted and ReadinessFinding.IsAccepted. A finding whose change
// another acceptance covers is left alone by every path and stays OPEN everywhere that counts (the score, the counts, the gate,
// the accepted list); its row says "You accepted a related finding. Accept this one too, or fix it." (TwinReason). The matrix
// (Semanticus.Tests/RuleClashMatrix.cs, docs/rule-clash-matrix.md) proves both halves for every bundled rule and every path.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    public sealed class DeclinedChanges
    {
        /// <summary>One stored acceptance that declines something.</summary>
        public sealed class Source
        {
            public string System { get; init; }          // "bpa" | "air"
            public string RuleId { get; init; }
            public string RuleTitle { get; init; }       // the rule's name as a person reads it, when it is known
            public string ObjectRef { get; init; }       // null = rule-level (every object of the rule's kinds); the model is WaiverStore.ModelRef
            public string Reason { get; init; }
            public bool TeIgnore { get; init; }          // Tabular Editor's own ignore annotation, not a Semanticus record
            public RuleAbout About { get; init; }
            public bool RuleLevel => ObjectRef == null;

            internal bool Covers(string objectRef)
            {
                if (string.IsNullOrEmpty(objectRef)) return false;
                if (!RuleLevel)
                {
                    if (WaiverStore.SameRef(ObjectRef, objectRef)) return true;
                    // A finding on the model whose write is on an object of the rule's kinds ("no marked date table"): the person
                    // accepted "do not mark a date table" for the model, which covers that write on any table.
                    if (!(About.FromModel && WaiverStore.SameRef(ObjectRef, WaiverStore.ModelRef))) return false;
                }
                foreach (var k in About.Kinds)
                    if (objectRef.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            /// <summary>The name of this acceptance as a person reads it: the rule's title and the page it is on.</summary>
            public string Label => Regex.Replace(string.IsNullOrWhiteSpace(RuleTitle) ? RuleId : RuleTitle, @"^\[[^\]]+\]\s*", "")
                + " (" + (System == "air" ? "AI readiness" : "Model quality") + ")";
        }

        public static readonly DeclinedChanges None = new DeclinedChanges(new List<Source>());

        /// <summary>The words on a finding another acceptance covers. It is not accepted: it is scored, counted and (when it blocks) gated
        /// like any open finding, and every path leaves its change alone.</summary>
        public const string TwinReason = "You accepted a related finding. Accept this one too, or fix it.";

        /// <summary>The name of the row that stands for a rule-level acceptance, on both pages. It is listed whether or not the
        /// rule fires anywhere, so an acceptance that keeps declining is never invisible and can always be reopened.</summary>
        public const string WholeModelName = "Accepted for the whole model";

        /// <summary>What a rule-level acceptance reaches, in plain words. A rule that declines a property keeps it on every object of
        /// its kinds (the person cannot be asked object by object for objects that do not exist yet), which is wider than the
        /// objects the rule flags, and the row says so.</summary>
        public static string WholeRuleMessage(RuleAbout about)
        {
            if (about == null || !about.DeclinesSomething) return "Every finding of this rule is accepted, across the whole model. Reopen the rule to change that.";
            // A rule about the model itself has no "every model in the model": the whole model is the one object.
            if (about.Kinds.All(k => k == "obj"))
                return about.OwnAsk
                    ? "This rule's own change to the model is left alone. Reopen the rule to change that."
                    : "The model keeps its " + PropertyWord(about.Property) + ". Reopen the rule to change that.";
            var kinds = Words(about.Kinds.Select(KindWord).Distinct().ToList());
            if (about.OwnAsk) return "This rule's own change is left alone on every " + kinds + " in the model. Reopen the rule to change that.";
            return "Every " + kinds + " in the model keeps its " + PropertyWord(about.Property) + ", not only the ones this rule flags. Reopen the rule to change that.";
        }

        private static string Words(IReadOnlyList<string> items) =>
            items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

        private static string KindWord(string kind) => kind switch
        {
            "obj" => "model", "calcitem" => "calculation item", "role" => "role", "perspective" => "perspective",
            _ => kind,
        };

        private static string PropertyWord(string property) => property switch
        {
            "isHidden" => "visibility", "summarizeBy" => "summarize setting", "formatString" => "format", "dataCategory" => "data category",
            "name" => "name", "description" => "description", "synonyms" => "synonyms", "sortByColumn" => "sort column", "dataType" => "data type",
            "isKey" => "key mark", "crossFilteringBehavior" => "filter direction", "cardinality" => "cardinality", "isActive" => "active setting",
            "sourceColumn" => "source column", "isAvailableInMDX" => "Excel availability", "linguisticSchema" => "synonym store",
            "aiInstructions" => "AI instructions", "expression" => "formula",
            _ => property,
        };

        /// <summary>The line shown for an acceptance whose finding no longer fires.</summary>
        public const string NoLongerFiresText = "This finding no longer fires. Your acceptance still keeps this change from being made.";

        private readonly List<Source> _sources;
        private readonly Dictionary<string, List<Source>> _byProperty = new Dictionary<string, List<Source>>(StringComparer.OrdinalIgnoreCase);

        private DeclinedChanges(List<Source> sources)
        {
            _sources = sources;
            foreach (var s in sources)
            {
                if (!_byProperty.TryGetValue(s.About.Property, out var list)) _byProperty[s.About.Property] = list = new List<Source>();
                list.Add(s);
            }
        }

        public bool IsEmpty => _sources.Count == 0;
        public IReadOnlyList<Source> Sources => _sources;

        /// <summary>The changes every stored acceptance on the model declines. A model with no waiver and no Tabular Editor
        /// ignore has none and is answered without touching anything else. No scan is run: the answer depends on the stored
        /// records and the rule definitions alone.</summary>
        public static DeclinedChanges Read(Model m, IReadOnlyList<BpaRule> rules = null)
        {
            var waivers = WaiverStore.Load(m).Where(w => !w.Unresolved).ToList();   // an acceptance whose object is gone declines nothing
            var ignores = WaiverStore.TeIgnores(m).ToList();
            if (waivers.Count == 0 && ignores.Count == 0) return None;
            // The rules in force on the model, with any the caller holds laid over them (a caller may hold a narrower list, and
            // an acceptance of a rule outside it must still be understood).
            var byId = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in BpaRuleSet.ForModel(m)) byId[r.ID] = r;
            if (rules != null) foreach (var r in rules) byId[r.ID] = r;

            RuleAbout AboutOf(string system, string ruleId)
            {
                if (string.Equals(system, "air", StringComparison.OrdinalIgnoreCase)) return RuleAboutTable.ForReadiness(ruleId);
                return byId.TryGetValue(ruleId, out var rule) ? RuleAboutTable.ForBpa(rule) : null;
            }

            var sources = new List<Source>();
            foreach (var w in waivers)
            {
                var about = AboutOf(w.System, w.RuleId);
                if (about == null || !about.DeclinesSomething) continue;
                sources.Add(new Source
                {
                    System = w.System.ToLowerInvariant(), RuleId = w.RuleId,
                    RuleTitle = TitleOf(w.System, w.RuleId, byId),
                    ObjectRef = WaiverStore.IsRuleLevel(w.ObjectRef) ? null : WaiverStore.CanonicalRef(w.ObjectRef),
                    Reason = w.Reason, About = about,
                });
            }
            foreach (var (obj, ids) in ignores)
            {
                var objRef = WaiverStore.CanonicalRef(Refs.For(obj));
                foreach (var id in ids)
                {
                    var about = AboutOf("bpa", id);
                    if (about == null || !about.DeclinesSomething) continue;
                    sources.Add(new Source { System = "bpa", RuleId = id, RuleTitle = TitleOf("bpa", id, byId), ObjectRef = objRef, Reason = WaiverStore.TeReason, TeIgnore = true, About = about });
                }
            }
            return sources.Count == 0 ? None : new DeclinedChanges(sources);
        }

        private static readonly Lazy<Dictionary<string, string>> ReadinessTitles = new Lazy<Dictionary<string, string>>(() =>
            ReadinessRuleSet.Default().Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats()))
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase));

        private static string TitleOf(string system, string ruleId, Dictionary<string, BpaRule> bpaById)
        {
            if (string.Equals(system, "air", StringComparison.OrdinalIgnoreCase)) return ReadinessTitles.Value.TryGetValue(ruleId, out var t) ? t : null;
            return bpaById.TryGetValue(ruleId, out var r) ? r.Name : null;
        }

        private IEnumerable<Source> Match(string objectRef, string property)
        {
            if (string.IsNullOrEmpty(property) || !_byProperty.TryGetValue(property, out var list)) yield break;
            foreach (var s in list) if (s.Covers(objectRef)) yield return s;
        }

        /// <summary>Is ANY write to (object, property) declined? True when an accepted rule's ask was "keep what is there".
        /// That is the question a safe fix asks: it writes one fixed value, so the value does not matter.</summary>
        public bool Declines(string objectRef, string property)
        {
            if (IsEmpty) return false;
            foreach (var s in Match(objectRef, property)) if (!s.About.OwnAsk) return true;
            return false;
        }

        /// <summary>Is a write of this VALUE to (object, property) declined? A keep-what-is-there acceptance declines any
        /// value; an own-ask acceptance declines the value its rule would write, when that value can be named. A null value
        /// (an assistant line not yet authored) is declined only by the first kind.</summary>
        public bool Declines(string objectRef, string property, string value)
        {
            if (IsEmpty) return false;
            foreach (var s in Match(objectRef, property))
            {
                if (!s.About.OwnAsk) return true;
                if (value == null || s.About.OwnValue == null) continue;
                var declined = s.About.OwnValue(objectRef);
                if (declined != null && string.Equals(declined.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Is THIS rule's ask about (object, property) declined? True when an accepted rule's ask was "keep what is
        /// there", or when this very rule was accepted for the object (its own ask, however narrow). Another rule's own-ask
        /// acceptance leaves this ask standing.</summary>
        public bool DeclinesAsk(string system, string ruleId, string objectRef, string property) =>
            CoverFor(system, ruleId, objectRef, property) != null;

        /// <summary>The acceptance that covers this rule's ask about (object, property), or null. Another rule's acceptance is
        /// named before this rule's own, so a twin row can say which acceptance it stands under.</summary>
        public Source CoverFor(string system, string ruleId, string objectRef, string property)
        {
            if (IsEmpty) return null;
            Source own = null;
            foreach (var s in Match(objectRef, property))
            {
                var same = string.Equals(s.System, system, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase);
                if (!s.About.OwnAsk && !same) return s;
                if (!s.About.OwnAsk || same) own ??= s;
            }
            return own;
        }
    }
}
