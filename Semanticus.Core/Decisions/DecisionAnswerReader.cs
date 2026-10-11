// Reading a reply, and refusing everything that is not exactly what we asked for.
//
// This is the trust boundary. Whatever a decision service sends back is untrusted text until this file has
// checked it against the request we sent, so the rule is total: an answer is accepted only when its shape,
// its question name and its option id are all ones we declared. Anything else makes the WHOLE ask "not
// decided". Not the one bad answer: the whole ask. A caller holding one good answer and one rejected one
// has no honest way to reason about the pair, so it is never given that position.
//
// ONE EXCEPTION, BY DECLARATION (m11/jev-batch): a request built PER QUESTION (DecisionRequest.PerQuestion) asks one
// question for each object of a table, and there the pair IS separable: each answer is about its own object, so an answer
// that fails every check above is refused ON ITS OWN, recorded in DecisionAnswers.Rejected, and the others stand. The
// checks themselves are not loosened by a hair; only what one failure costs changes. A reply with nothing usable in it is
// still "not decided".
//
// Measured against the pilot replies in docs/redesign/jev-pilot/pilot2-raw-a-run1.json and
// pilot4-raw-s1-run1.json, which is where the two wire facts below come from:
//   a choice answer carries type, choice, confidence and probabilities
//   a noul answer carries type and noul only, with NO confidence field
// The score shape was measured on 28 Sep 2026 (the plan.rank 422, lane m11/plan-harm), from a live reply of
// jev-1.13.0 to a five-rung ladder:
//   {"type":"score","score":1.25,"confidence":0.64,"legend":{"0":"<rung 1 criterion>",...,"4":"..."},
//    "probabilities":{"0":0.09,"1":0.59,"2":0.31,"3":0.01,"4":0.0}}
// so a score answer names its rungs by ZERO-BASED POSITION in the ladder that was sent, its score is the
// expected position (a mean, not a rung), and the legend repeats each position's criterion. The reader maps each
// position back to the option sent there, refuses a legend that does not repeat our own criteria word for word,
// and takes the most probable rung, because the mean of "surely none" and "surely urgent" is a rung nobody chose.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Decisions
{
    public static class DecisionAnswerReader
    {
        /// <summary>Validate a raw answers object against the request that produced it. Returns a decided
        /// result only when every question has a well-formed, in-range answer.</summary>
        public static DecisionAnswers Read(DecisionRequest request, JsonElement answers, string servedModelVersion,
                                           int inputTokens = 0, int outputTokens = 0)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (answers.ValueKind != JsonValueKind.Object)
                return DecisionAnswers.NotDecided(request, "the reply held no answers object");

            var read = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

            // A BATCHED request (one question per object of a table) is read answer by answer: one refused or missing answer is
            // recorded and the rest stand. Every other request keeps the total rule.
            Dictionary<string, string> rejected = request.PerQuestion ? new Dictionary<string, string>(StringComparer.Ordinal) : null;

            foreach (var property in answers.EnumerateObject())
            {
                var question = request.Question(property.Name);
                if (question == null)
                {
                    if (rejected == null) return DecisionAnswers.NotDecided(request, $"the reply answered '{property.Name}', which was not asked");
                    rejected[property.Name] = "the reply answered a question that was not asked";
                    continue;
                }

                string why;
                var answer = ReadOne(question, property.Value, out why);
                if (answer == null)
                {
                    if (rejected == null) return DecisionAnswers.NotDecided(request, $"the answer to '{question.Name}' was rejected: {why}");
                    rejected[question.Name] = $"the answer to '{question.Name}' was rejected: {why}";
                    continue;
                }

                read[question.Name] = answer;
            }

            foreach (var question in request.Questions)
                if (!read.ContainsKey(question.Name))
                {
                    if (rejected == null) return DecisionAnswers.NotDecided(request, $"the reply did not answer '{question.Name}'");
                    if (!rejected.ContainsKey(question.Name)) rejected[question.Name] = $"the reply did not answer '{question.Name}'";
                }

            // A batched reply with nothing usable in it is simply not decided, for the first reason found.
            if (rejected != null && read.Count == 0)
                return DecisionAnswers.NotDecided(request, rejected.Count == 0 ? "the reply held no answers" : rejected.Values.First());

            return DecisionAnswers.Decide(request, servedModelVersion, read, inputTokens, outputTokens, rejected);
        }

        private static DecisionAnswer ReadOne(DecisionQuestion question, JsonElement value, out string why)
        {
            why = null;
            if (value.ValueKind != JsonValueKind.Object) { why = "it was not an object"; return null; }

            JsonElement typeElement;
            if (!value.TryGetProperty("type", out typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                why = "it named no shape";
                return null;
            }

            var type = typeElement.GetString();
            if (!string.Equals(type, question.WireType, StringComparison.Ordinal))
            {
                why = $"it came back as '{type}' where '{question.WireType}' was asked";
                return null;
            }

            if (question.Kind == DecisionQuestionKind.Score && !LegendMatches(question, value, out why)) return null;
            var probabilities = ReadProbabilities(question, value, out why);
            if (why != null) return null;

            switch (question.Kind)
            {
                case DecisionQuestionKind.Choice:
                case DecisionQuestionKind.Score:
                {
                    var field = question.Kind == DecisionQuestionKind.Choice ? "choice" : "score";
                    JsonElement picked;
                    if (!value.TryGetProperty(field, out picked)) { why = $"it carried no '{field}'"; return null; }

                    string id = null;
                    if (picked.ValueKind == JsonValueKind.String)
                    {
                        id = picked.GetString();
                    }
                    else if (question.Kind == DecisionQuestionKind.Score && picked.ValueKind == JsonValueKind.Number)
                    {
                        // A score comes back as the expected ZERO-BASED position on the ladder (measured, see the
                        // top of this file). Out of range is a rejection, never a clamp. With probabilities the
                        // most probable rung is the answer; without them, the nearest position.
                        double rung;
                        if (!TryNumber(picked, out rung)) { why = $"'{field}' was not a number this reader can hold"; return null; }
                        var index = (int)Math.Round(rung, MidpointRounding.AwayFromZero);
                        if (rung < 0 || index < 0 || index >= question.Options.Count)
                        {
                            why = $"the rung {rung.ToString(CultureInfo.InvariantCulture)} is outside the ladder of {question.Options.Count}";
                            return null;
                        }
                        id = question.Options[index].Id;
                        var best = -1.0;
                        foreach (var p in probabilities)
                            if (p.Value > best) { best = p.Value; id = p.Key; }
                    }
                    else
                    {
                        why = $"'{field}' was not text";
                        return null;
                    }

                    if (!question.HasOption(id))
                    {
                        why = $"'{id}' is not one of the options that were sent";
                        return null;
                    }

                    double confidence;
                    if (!ReadConfidence(value, probabilities, id, out confidence, out why)) return null;

                    double? expected = null;
                    if (question.Kind == DecisionQuestionKind.Score)
                    {
                        // Jev's own expected position, a mean over the ladder. Its "score" number when the reply sent one, else
                        // worked out from the probabilities; a reply that names the rung by id and sends no probabilities has none.
                        double mean;
                        if (picked.ValueKind == JsonValueKind.Number && TryNumber(picked, out mean)) expected = mean;
                        else if (probabilities.Count > 0)
                        {
                            var sum = 0.0;
                            for (var i = 0; i < question.Options.Count; i++)
                                if (probabilities.TryGetValue(question.Options[i].Id, out var pr)) sum += i * pr;
                            expected = sum;
                        }
                    }

                    return new DecisionAnswer(question.Name, question.Kind,
                        question.Kind == DecisionQuestionKind.Choice ? id : null,
                        question.Kind == DecisionQuestionKind.Score ? id : null,
                        null, confidence, probabilities, expected);
                }

                case DecisionQuestionKind.Noul:
                {
                    JsonElement noul;
                    if (!value.TryGetProperty("noul", out noul) || noul.ValueKind != JsonValueKind.Number)
                    {
                        why = "it carried no 'noul' number";
                        return null;
                    }

                    double p;
                    if (!TryNumber(noul, out p) || double.IsNaN(p) || p < 0 || p > 1)
                    {
                        why = "the 'noul' probability is outside 0 to 1";
                        return null;
                    }

                    // A noul reply carries no confidence field. The honest reading of one probability is how far
                    // it sits from an even split, so a 0.5 is no confidence at all and a 0.02 is high confidence
                    // in "no". This number is derived here, not sent by the service.
                    var confidence = Math.Max(p, 1 - p);
                    var yes = p >= 0.5;
                    var derived = new Dictionary<string, double>(StringComparer.Ordinal) { { "yes", p }, { "no", 1 - p } };
                    return new DecisionAnswer(question.Name, question.Kind, null, null, yes, confidence,
                        probabilities.Count > 0 ? probabilities : derived);
                }

                default:
                    why = "unknown shape";
                    return null;
            }
        }

        private static Dictionary<string, double> ReadProbabilities(DecisionQuestion question, JsonElement value, out string why)
        {
            why = null;
            var result = new Dictionary<string, double>(StringComparer.Ordinal);

            JsonElement probabilities;
            if (!value.TryGetProperty("probabilities", out probabilities)) return result;
            if (probabilities.ValueKind == JsonValueKind.Null) return result;
            if (probabilities.ValueKind != JsonValueKind.Object) { why = "'probabilities' was not an object"; return result; }

            foreach (var p in probabilities.EnumerateObject())
            {
                // A score names its rungs by position (see the top of this file); every other shape by option id.
                var option = question.Kind == DecisionQuestionKind.Score ? OptionAtPosition(question, p.Name) ?? p.Name : p.Name;
                if (!question.HasOption(option))
                {
                    why = $"'probabilities' names '{p.Name}', which is not one of the options that were sent";
                    return result;
                }
                if (p.Value.ValueKind != JsonValueKind.Number)
                {
                    why = $"the probability for '{p.Name}' was not a number";
                    return result;
                }
                double v;
                if (!TryNumber(p.Value, out v))
                {
                    why = $"the probability for '{p.Name}' was not a number this reader can hold";
                    return result;
                }
                if (double.IsNaN(v) || v < 0 || v > 1)
                {
                    why = $"the probability for '{p.Name}' is outside 0 to 1";
                    return result;
                }
                result[option] = v;
            }

            return result;
        }

        /// <summary>The option sent at a zero-based position written as text ("0", "1"), or null when the text is not
        /// a position on this ladder.</summary>
        private static string OptionAtPosition(DecisionQuestion question, string position)
        {
            int index;
            if (!int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return null;
            return index >= 0 && index < question.Options.Count ? question.Options[index].Id : null;
        }

        /// <summary>A score's legend, when it carries one, must repeat the criterion we sent at every position it
        /// names. A legend that says something else is an answer to a different ladder, so the whole ask is refused.</summary>
        private static bool LegendMatches(DecisionQuestion question, JsonElement value, out string why)
        {
            why = null;
            JsonElement legend;
            if (!value.TryGetProperty("legend", out legend) || legend.ValueKind == JsonValueKind.Null) return true;
            if (legend.ValueKind != JsonValueKind.Object) { why = "'legend' was not an object"; return false; }
            foreach (var entry in legend.EnumerateObject())
            {
                var option = OptionAtPosition(question, entry.Name);
                var sent = option == null ? null : question.Options.First(o => string.Equals(o.Id, option, StringComparison.Ordinal)).Criterion;
                if (sent == null || entry.Value.ValueKind != JsonValueKind.String
                    || !string.Equals(entry.Value.GetString(), sent, StringComparison.Ordinal))
                {
                    why = $"the legend's rung '{entry.Name}' is not the ladder that was sent";
                    return false;
                }
            }
            return true;
        }

        private static bool ReadConfidence(JsonElement value, IReadOnlyDictionary<string, double> probabilities,
                                           string picked, out double confidence, out string why)
        {
            why = null;
            confidence = 0;

            JsonElement c;
            if (value.TryGetProperty("confidence", out c))
            {
                if (c.ValueKind != JsonValueKind.Number) { why = "'confidence' was not a number"; return false; }
                if (!TryNumber(c, out confidence)) { why = "'confidence' was not a number this reader can hold"; return false; }
                if (double.IsNaN(confidence) || confidence < 0 || confidence > 1)
                {
                    why = "'confidence' is outside 0 to 1";
                    return false;
                }
                return true;
            }

            // No confidence field. Fall back to the probability of the option it picked, which is the same number
            // the service reports as confidence on every pilot answer that carried both.
            double p;
            if (probabilities.TryGetValue(picked, out p)) { confidence = p; return true; }

            why = "it carried neither a confidence nor a probability for the option it picked";
            return false;
        }

        /// <summary>Read a JSON number the ONLY way this file is allowed to. GetDouble throws FormatException
        /// on a number that is syntactically valid JSON but that no double can hold, such as 1e400, and a throw
        /// out of here is a reply deciding the fate of a model operation. Every number read in this file goes
        /// through here; a number that cannot be held is simply not a number we accept. (Codex on PR 384.)</summary>
        private static bool TryNumber(JsonElement value, out double number)
        {
            number = 0;
            if (value.ValueKind != JsonValueKind.Number) return false;
            if (!value.TryGetDouble(out number)) { number = 0; return false; }
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }
}
