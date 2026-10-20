// The typed decision contract (Jev M0a).
//
// Golden rule 1 as Kane rewrote it on 2026-09-21: the person's own Claude Code authors content, and the
// engine may ask a typed decision service. "Typed" is the whole point of this file. A decision service
// answers exactly three shapes and nothing else:
//
//   choice  one option id out of a declared map of options, with probabilities over those ids
//   score   one rung id out of a declared ORDERED ladder, with probabilities over those ids
//   noul    a statement, answered with one probability that the statement is true
//
// It never returns free text, so nothing it says can become a description, a document or a DAX
// expression. Those still come from the person's assistant over MCP.
//
// Everything here is pure data plus a digest. It holds no transport, no key and no HTTP. The vendor
// client lives in Semanticus.Engine so that a test can assert the vendor host appears in exactly one file.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Decisions
{
    /// <summary>The three shapes a registered decision point may ask. There is deliberately no fourth.</summary>
    public enum DecisionQuestionKind
    {
        /// <summary>One id out of an unordered map of options.</summary>
        Choice,
        /// <summary>One id out of an ordered ladder, lowest rung first.</summary>
        Score,
        /// <summary>A statement, answered with one probability that it is true.</summary>
        Noul,
    }

    /// <summary>One option a question may be answered with. <see cref="Criterion"/> describes the world in
    /// neutral words ("the numbers in this measure are amounts of money"), never the product's own state and
    /// never the answer we are hoping for.</summary>
    public sealed class DecisionOption
    {
        public DecisionOption(string id, string criterion)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An option needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(criterion)) throw new ArgumentException($"Option '{id}' needs a criterion.", nameof(criterion));
            Id = id;
            Criterion = criterion;
        }

        public string Id { get; }
        public string Criterion { get; }
    }

    /// <summary>One question inside a request. The option ORDER is part of the request digest: a score ladder
    /// is ordered by meaning, and for a choice the order is still what was sent, so two runs that ordered the
    /// same options differently are two different pieces of evidence and must not share a cache entry.</summary>
    public sealed class DecisionQuestion
    {
        public DecisionQuestion(string name, DecisionQuestionKind kind, string instructions, IEnumerable<DecisionOption> options)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A question needs a name.", nameof(name));
            if (string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException($"Question '{name}' needs instructions.", nameof(instructions));
            Name = name;
            Kind = kind;
            Instructions = instructions;
            Options = new List<DecisionOption>(options ?? Enumerable.Empty<DecisionOption>()).AsReadOnly();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in Options)
                if (!ids.Add(o.Id))
                    throw new ArgumentException($"Question '{name}' declares the option id '{o.Id}' twice.", nameof(options));

            if (kind == DecisionQuestionKind.Noul)
            {
                // A noul is a statement, so its two criteria are fixed: what makes it true and what makes it false.
                if (Options.Count != 2 || !ids.Contains("yes") || !ids.Contains("no"))
                    throw new ArgumentException($"Noul question '{name}' must declare exactly the options 'yes' and 'no'.", nameof(options));
            }
            else if (Options.Count < 2)
            {
                throw new ArgumentException($"Question '{name}' needs at least two options.", nameof(options));
            }
        }

        public string Name { get; }
        public DecisionQuestionKind Kind { get; }
        public string Instructions { get; }
        public IReadOnlyList<DecisionOption> Options { get; }

        /// <summary>The wire word for this shape, matching what the vendor accepts.</summary>
        public string WireType =>
            Kind == DecisionQuestionKind.Choice ? "choice"
            : Kind == DecisionQuestionKind.Score ? "score"
            : "noul";

        public bool HasOption(string id) => id != null && Options.Any(o => string.Equals(o.Id, id, StringComparison.Ordinal));
    }

    /// <summary>One request to a decision service: which registered point is asking, the typed snapshot it is
    /// allowed to send, and the questions. The digest is computed once at construction and is the identity of
    /// this piece of evidence: the cache, the recorded oracle and the replay gate all key on it.</summary>
    public sealed class DecisionRequest
    {
        public DecisionRequest(string pointName, int schemaVersion, IReadOnlyDictionary<string, object> snapshot, IEnumerable<DecisionQuestion> questions,
                               bool perQuestion = false)
        {
            PerQuestion = perQuestion;
            if (string.IsNullOrWhiteSpace(pointName)) throw new ArgumentException("A request needs a point name.", nameof(pointName));
            PointName = pointName;
            SchemaVersion = schemaVersion;
            Snapshot = snapshot ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Questions = new List<DecisionQuestion>(questions ?? Enumerable.Empty<DecisionQuestion>()).AsReadOnly();
            if (Questions.Count == 0) throw new ArgumentException($"Point '{pointName}' sent no questions.", nameof(questions));

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in Questions)
                if (!names.Add(q.Name))
                    throw new ArgumentException($"Point '{pointName}' asks '{q.Name}' twice.", nameof(questions));

            Digest = DecisionDigest.Of(this);
        }

        public string PointName { get; }
        public int SchemaVersion { get; }
        public IReadOnlyDictionary<string, object> Snapshot { get; }
        public IReadOnlyList<DecisionQuestion> Questions { get; }

        /// <summary>A BATCHED request: one question per object of a table, so one malformed answer must not void the others.
        /// The reader then accepts answer by answer (<see cref="DecisionAnswerReader.Read"/>) and reports what it refused in
        /// <see cref="DecisionAnswers.Rejected"/>. Not part of the digest: the digest is the evidence, and this is only how
        /// a reply to it is read.</summary>
        public bool PerQuestion { get; }

        /// <summary>SHA-256 over the point name, the schema version, the canonical snapshot, and every question
        /// with its options in the order they were declared. Nothing else is in it, and nothing in it is
        /// optional, so two requests with the same digest really are the same question about the same world.</summary>
        public string Digest { get; }

        public DecisionQuestion Question(string name) => Questions.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.Ordinal));
    }

    /// <summary>One answer, already validated against the question that was asked. Exactly one of
    /// <see cref="Choice"/>, <see cref="Score"/> and <see cref="Noul"/> is set, matching <see cref="Kind"/>.</summary>
    public sealed class DecisionAnswer
    {
        internal DecisionAnswer(string question, DecisionQuestionKind kind, string choice, string score, bool? noul,
                                double confidence, IReadOnlyDictionary<string, double> probabilities, double? expected = null)
        {
            Expected = expected;
            Question = question;
            Kind = kind;
            Choice = choice;
            Score = score;
            Noul = noul;
            Confidence = confidence;
            Probabilities = probabilities ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }

        public string Question { get; }
        public DecisionQuestionKind Kind { get; }

        /// <summary>The chosen option id, for a choice question. Null otherwise.</summary>
        public string Choice { get; }

        /// <summary>The chosen rung id, for a score question. Null otherwise.</summary>
        public string Score { get; }

        /// <summary>True when the statement was answered yes, for a noul question. Null otherwise.</summary>
        public bool? Noul { get; }

        /// <summary>How sure the service is, from 0 to 1.
        ///
        /// Honest note, measured from the pilot answers in docs/redesign/jev-pilot/pilot2-raw-a-run1.json: a
        /// choice answer carries its own confidence field, and a NOUL answer carries no confidence at all, only
        /// the probability that the statement is true. For a noul this is therefore derived as the distance from
        /// an even split, max(p, 1 - p), which is the honest reading of one probability and is NOT a second
        /// number the service sent.</summary>
        public double Confidence { get; }

        /// <summary>Probability per option id, as returned. Empty when the service sent none.</summary>
        public IReadOnlyDictionary<string, double> Probabilities { get; }

        /// <summary>For a score question: the expected position on the ladder, counted from zero, as Jev's reply carries it (its
        /// "score", a probability-weighted mean, not a rung), or worked out from the probabilities when the reply carried
        /// none. Null for every other shape. The most probable rung is <see cref="Score"/>; this is the finer number, and
        /// it is what a graded ranking sorts on (plan.rank).</summary>
        public double? Expected { get; }

        /// <summary>The same answer under another question name. A batched request names one question per object; the
        /// family that reads one object's answer reads it under the plain question name it always used.</summary>
        internal DecisionAnswer Renamed(string question) =>
            new DecisionAnswer(question, Kind, Choice, Score, Noul, Confidence, Probabilities, Expected);

        /// <summary>The answer as an option id, whatever the shape: the choice, the rung, or "yes"/"no".</summary>
        public string OptionId =>
            Kind == DecisionQuestionKind.Choice ? Choice
            : Kind == DecisionQuestionKind.Score ? Score
            : Noul == true ? "yes" : Noul == false ? "no" : null;
    }

    /// <summary>What one ask produced. Either it was decided, and every question has a validated answer, or it
    /// was not decided and <see cref="NotDecidedReason"/> says why in plain words. There is no half state: an
    /// answer that fails validation makes the whole ask "not decided", because a caller that got one good
    /// answer and one malformed one has no way to know which is which.</summary>
    public sealed class DecisionAnswers
    {
        private DecisionAnswers(string pointName, string requestDigest, string servedModelVersion, bool decided,
                                string notDecidedReason, IReadOnlyDictionary<string, DecisionAnswer> answers,
                                int inputTokens, int outputTokens, bool overBudget = false,
                                IReadOnlyDictionary<string, string> rejected = null)
        {
            Rejected = rejected ?? new Dictionary<string, string>(StringComparer.Ordinal);
            OverBudget = overBudget;
            PointName = pointName;
            RequestDigest = requestDigest;
            ServedModelVersion = servedModelVersion;
            Decided = decided;
            NotDecidedReason = notDecidedReason;
            Answers = answers ?? new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
        }

        public string PointName { get; }

        /// <summary>The digest of the request these answers belong to. A caller compares it with the request it
        /// sent before believing anything, so a mixed-up reply cannot be attributed to the wrong evidence.</summary>
        public string RequestDigest { get; }

        /// <summary>The model version that actually served the answer, for example "jev-1.13.0". The client asks
        /// for a moving alias, so this is the only record of what answered, and it is part of the cache key.</summary>
        public string ServedModelVersion { get; }

        public bool Decided { get; }
        public string NotDecidedReason { get; }

        /// <summary>Not decided because the session's budget could not fit this ask, so it was not sent (a retry, not
        /// sent again). The client that enforces the budget says so here, not only in its words, because it holds each
        /// ask's estimate before sending and so stops before the budget reads as exhausted: the service turns this into
        /// its own "budget spent" skip, and a caller sees one signal however the budget stopped.</summary>
        public bool OverBudget { get; }
        public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; }

        /// <summary>Questions of a BATCHED request (<see cref="DecisionRequest.PerQuestion"/>) whose answer was refused, or that
        /// the reply left out, with the reason. Empty for every other request, and for a batched one that came back whole.</summary>
        public IReadOnlyDictionary<string, string> Rejected { get; }

        /// <summary>Every question was answered and accepted. Only a complete reply is kept in a cache: an answer that was
        /// refused deserves another try, and a cache hit would serve the gap back forever.</summary>
        public bool Complete { get { return Decided && Rejected.Count == 0; } }

        /// <summary>One object's share of a batched reply, under the plain question names its family reads: each entry of
        /// <paramref name="names"/> maps a batched question name to the plain one. Decided when at least one of them was
        /// answered, so an object whose only answer was refused reads as not decided and keeps whatever it had.</summary>
        public DecisionAnswers Narrow(IEnumerable<KeyValuePair<string, string>> names)
        {
            var mine = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            string why = null;
            foreach (var pair in names ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (Answers.TryGetValue(pair.Key, out var answer)) mine[pair.Value] = answer.Renamed(pair.Value);
                else if (why == null && Rejected.TryGetValue(pair.Key, out var refused)) why = refused;
            }
            if (!Decided || mine.Count == 0)
                return NotDecided(PointName, RequestDigest, Decided ? (why ?? "the reply left this object out") : NotDecidedReason);
            return new DecisionAnswers(PointName, RequestDigest, ServedModelVersion, true, null, mine, InputTokens, OutputTokens);
        }

        /// <summary>The answers of one request that was asked as several smaller ones (a relay older than the client refused it as too big).
        /// A whole-ask request (no <paramref name="perQuestion"/>) is decided only when every part was, since it has no half state. A batched one is
        /// decided when any part was, and a question whose part was not answered is listed in <see cref="Rejected"/> with that part's reason,
        /// so an answer is never dropped without a trace. The digest is the whole request's.</summary>
        public static DecisionAnswers Merge(DecisionRequest whole, IReadOnlyList<DecisionQuestion> questions, IReadOnlyList<(IReadOnlyList<DecisionQuestion> Asked, DecisionAnswers Answers)> parts, bool perQuestion)
        {
            var merged = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
            string served = null, firstWhy = null;
            int input = 0, output = 0, decided = 0;
            foreach (var (asked, a) in parts)
            {
                input += a.InputTokens; output += a.OutputTokens;
                if (a.Decided)
                {
                    decided++;
                    served = served ?? a.ServedModelVersion;
                    foreach (var kv in a.Answers) merged[kv.Key] = kv.Value;
                    foreach (var kv in a.Rejected) rejected[kv.Key] = kv.Value;
                }
                else
                {
                    firstWhy = firstWhy ?? a.NotDecidedReason;
                    foreach (var q in asked) rejected[q.Name] = a.NotDecidedReason;
                }
            }
            if (decided == 0 || (!perQuestion && decided < parts.Count))
                return NotDecided(whole?.PointName, whole?.Digest, firstWhy);
            return new DecisionAnswers(whole.PointName, whole.Digest, served, true, null, merged, input, output, rejected: perQuestion ? rejected : null);
        }

        /// <summary>Token counts as reported, for the budget counter. Zero when nothing was spent.</summary>
        public int InputTokens { get; }
        public int OutputTokens { get; }

        public DecisionAnswer For(string question)
        {
            if (question == null) return null;
            DecisionAnswer a;
            return Answers.TryGetValue(question, out a) ? a : null;
        }

        /// <summary>The answer to <paramref name="question"/>, but only when it cleared <paramref name="bar"/>.
        /// Below the bar a point says nothing new, which is the behaviour the plan commits to.</summary>
        public DecisionAnswer AboveBar(string question, double bar)
        {
            var a = For(question);
            return a != null && a.Confidence >= bar ? a : null;
        }

        public static DecisionAnswers NotDecided(DecisionRequest request, string reason) =>
            new DecisionAnswers(request?.PointName, request?.Digest, null, false,
                string.IsNullOrWhiteSpace(reason) ? "not decided" : reason, null, 0, 0);

        /// <summary>Not decided because the session's budget could not fit it (<see cref="OverBudget"/>).</summary>
        public static DecisionAnswers NotDecidedOverBudget(DecisionRequest request, string reason) =>
            new DecisionAnswers(request?.PointName, request?.Digest, null, false,
                string.IsNullOrWhiteSpace(reason) ? "not decided" : reason, null, 0, 0, overBudget: true);

        public static DecisionAnswers NotDecided(string pointName, string requestDigest, string reason) =>
            new DecisionAnswers(pointName, requestDigest, null, false,
                string.IsNullOrWhiteSpace(reason) ? "not decided" : reason, null, 0, 0);

        public static DecisionAnswers Decide(DecisionRequest request, string servedModelVersion,
                                             IReadOnlyDictionary<string, DecisionAnswer> answers,
                                             int inputTokens = 0, int outputTokens = 0,
                                             IReadOnlyDictionary<string, string> rejected = null) =>
            new DecisionAnswers(request.PointName, request.Digest, servedModelVersion, true, null, answers, inputTokens, outputTokens,
                                rejected: rejected);

        /// <summary>The same answers under a different served version stamp, used when a cached entry is replayed.</summary>
        public DecisionAnswers WithoutSpend() =>
            new DecisionAnswers(PointName, RequestDigest, ServedModelVersion, Decided, NotDecidedReason, Answers, 0, 0, OverBudget, Rejected);
    }
}
