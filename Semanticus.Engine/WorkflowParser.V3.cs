using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    // ============================================================================================
    // Format v3, the workflow composer's keys (composer contract section 1; lane L1).
    //
    // v3 is the v2 ruleset and reader plus these keys, so everything here runs on the model YamlDotNet
    // already built and every refusal names the key, the scope and the line, exactly as v2's do. The split
    // of labour is the v2 one: what the FILE alone decides (shapes, values, references between its own
    // parts) is refused here, at parse, so save_workflow never writes it; what needs the op table
    // (OpParameterKinds: which ops a step Semanticus runs may name, which arguments each takes, which need
    // a blank) is a check_workflow finding in V3Findings, because that is where the lane puts placement,
    // required blanks and outward ops, and because it is a judgement about the file against the engine.
    //
    // Nothing here RUNS a composed step. Until the run path lands, UnexecutableControlFields names every v3
    // field a guided run would otherwise ignore, and admission refuses to start such a file.
    // ============================================================================================
    public static partial class WorkflowParser
    {
        // A reference inside an argument, a question's options or a default: {{inputs.x}}, {{questions.x}},
        // {{blanks.x}} or {{steps.<id>.<result>}}. A plain {{name}} (no dot) is a template slot and is not
        // read here; [[loop.*]] is a loop value and uses its own delimiters.
        private static readonly Regex ComposerRef = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\.([^{}\s]*)\s*\}\}", RegexOptions.Compiled);
        private static readonly Regex StepsRefPath = new Regex(@"^([a-z][a-z0-9]*(?:-[a-z0-9]+)*)\.([A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.Compiled);

        // ---- value helpers ---------------------------------------------------------------------------

        /// <summary>A closed value, refused naming the scope, the key, the value and the line.</summary>
        private static string EnumAt(string value, IEnumerable<string> allowed, string ctx, string key, int line)
        {
            var set = allowed.ToArray();
            if (value != null && set.Contains(value)) return value;
            throw new FormatException($"{ctx}: {key} '{value}' is not one of: {string.Join(" | ", set)} (line {line}).");
        }

        /// <summary>assistantNotes and the Brief: one text value, 2000 characters or fewer (contract 1.2, 1.3).</summary>
        private static string CappedText(YEntry e, string ctx)
        {
            var text = ScalarOf(e, ctx);
            if (text != null && text.Length > MaxBriefChars)
                throw new FormatException($"{ctx}: '{e.Key}:' is {text.Length} characters, and the limit is {MaxBriefChars} (line {e.Line}). Shorten it.");
            return text;
        }

        private static int FirstLine(FlatMap it) => it.Lines.Count == 0 ? 0 : it.Lines.Values.Min();

        // ---- action -----------------------------------------------------------------------------------

        private static StepAction ToAction(YEntry entry, WorkflowStep step)
        {
            var ctx = $"Step {step.Number} action";
            var map = BlockOf(entry, $"Step {step.Number}",
                "It takes 'op:' naming the op Semanticus runs, then that op's arguments, each on its own indented line.",
                bareIsEmpty: false);
            var action = new StepAction { Line = entry.Line };
            var args = new List<ActionArgument>();
            foreach (var e in map.Entries)
            {
                if (e.Key == "op") { action.Op = ScalarOf(e, ctx); continue; }
                // The argument names are the op's own, so this map is open in spelling the way `with:` is; the
                // names are judged against the op by check_workflow, which has the op table.
                if (e.Value is YMap)
                    throw new FormatException($"{ctx}: '{e.Key}' takes a single value or a list, not a block of keys (line {e.Line}).");
                if (e.Value is YSeq)
                {
                    args.Add(new ActionArgument { Name = e.Key, Values = ListOf(e, $"{ctx}: {e.Key}"), Line = e.Line });
                    continue;
                }
                var value = ScalarOf(e, ctx);
                if (value == null)
                    throw new FormatException($"{ctx}: '{e.Key}' has no value (line {e.Line}). Give it a value, or leave it out.");
                args.Add(new ActionArgument { Name = e.Key, Value = value, Line = e.Line });
            }
            if (string.IsNullOrWhiteSpace(action.Op))
                throw new FormatException($"Step {step.Number}: 'action:' needs an 'op:' naming the op Semanticus runs (line {entry.Line}).");
            action.Arguments = args.ToArray();
            return action;
        }

        // ---- questions, blanks, checks ------------------------------------------------------------------

        private static WorkflowQuestion ToQuestion(FlatMap it, string ctx)
        {
            var q = new WorkflowQuestion { Line = FirstLine(it) };
            foreach (var kv in it.Values)
            {
                if (QuestionKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                var line = it.LineOf(kv.Key);
                switch (kv.Key)
                {
                    case "name": q.Name = kv.Value; break;
                    case "question": q.Question = kv.Value; break;
                    case "type": q.Type = EnumAt(kv.Value, QuestionTypes, ctx, "type", line); break;
                    case "required": q.Required = EnumAt(kv.Value, QuestionRequireds, ctx, "required", line); break;
                    case "by": q.By = EnumAt(kv.Value, QuestionBys, ctx, "by", line); break;
                    case "values": q.Values = it.List("values"); break;
                    case "from": q.From = kv.Value; break;
                    case "jev": q.Jev = kv.Value; break;
                    case "default": q.Default = kv.Value; break;
                    default: throw Unknown(ctx, "Question", kv.Key, line, QuestionKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(q.Name))
                throw new FormatException($"{ctx}: a question is missing 'name' (line {q.Line}).");
            if (!SlotName.IsMatch(q.Name))
                throw new FormatException($"{ctx}: question name '{q.Name}' must be a plain name of letters, digits and underscores (line {it.LineOf("name")}), so '{{{{questions.{q.Name}}}}}' can find it.");
            if (q.Values.Length > 0 && q.Type != "enum")
                throw new FormatException($"{ctx}: '{q.Name}' lists 'values:', which only an enum question has (line {it.LineOf("values")}). Write 'type: enum', or remove the values.");
            if (q.Type == "enum" && q.Values.Length == 0 && string.IsNullOrWhiteSpace(q.From))
                throw new FormatException($"{ctx}: '{q.Name}' is an enum question with nothing to choose from (line {it.LineOf("type")}). Give it 'values:', or 'from:' naming a list Semanticus reads.");
            return q;
        }

        private static WorkflowBlank ToBlank(FlatMap it, WorkflowStep step)
        {
            var ctx = $"Step {step.Number} blank";
            var b = new WorkflowBlank { Line = FirstLine(it) };
            foreach (var kv in it.Values)
            {
                if (BlankKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                var line = it.LineOf(kv.Key);
                switch (kv.Key)
                {
                    case "name": b.Name = kv.Value; break;
                    case "kind": b.Kind = EnumAt(kv.Value, OpParameterKinds.BlankKinds, ctx, "kind", line); break;
                    case "fills": b.Fills = kv.Value; break;
                    case "by": b.By = EnumAt(kv.Value, BlankBys, ctx, "by", line); break;
                    case "dependsOn": b.DependsOn = it.List("dependsOn"); break;
                    case "maxChars":
                        if (!int.TryParse(kv.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < 1)
                            throw new FormatException($"{ctx}: maxChars '{kv.Value}' is not a whole number above zero (line {line}).");
                        b.MaxChars = max;
                        break;
                    case "choices": b.Choices = kv.Value; break;
                    case "generator": b.Generator = kv.Value; break;
                    default: throw Unknown(ctx, "Blank", kv.Key, line, BlankKeys);
                }
            }
            if (string.IsNullOrWhiteSpace(b.Name))
                throw new FormatException($"{ctx}: a blank is missing 'name' (line {b.Line}).");
            if (!SlotName.IsMatch(b.Name))
                throw new FormatException($"{ctx}: blank name '{b.Name}' must be a plain name of letters, digits and underscores (line {it.LineOf("name")}).");
            if (b.Kind == null)
                throw new FormatException($"{ctx}: blank '{b.Name}' is missing 'kind' (line {b.Line}). It is one of: {string.Join(" | ", OpParameterKinds.BlankKinds)}.");
            if (string.IsNullOrWhiteSpace(b.Fills))
                throw new FormatException($"{ctx}: blank '{b.Name}' is missing 'fills', the argument of the action its text becomes (line {b.Line}).");
            if (b.Kind == "choice" && string.IsNullOrWhiteSpace(b.Choices))
                throw new FormatException($"{ctx}: blank '{b.Name}' is a choice, so it needs 'choices:' naming the list it picks from (line {it.LineOf("kind")}).");
            if (b.Kind != "choice" && b.Choices != null)
                throw new FormatException($"{ctx}: blank '{b.Name}' has 'choices:', which only a choice blank has (line {it.LineOf("choices")}).");
            return b;
        }

        private static WorkflowCheck ToCheck(FlatMap it, WorkflowStep step)
        {
            var ctx = $"Step {step.Number} check";
            var c = new WorkflowCheck { Line = FirstLine(it) };
            foreach (var kv in it.Values)
            {
                if (CheckKeys.Contains(kv.Key)) it.RequireShape(kv.Key);
                var line = it.LineOf(kv.Key);
                switch (kv.Key)
                {
                    case "kind": c.Kind = EnumAt(kv.Value, CheckPhases.Keys, ctx, "kind", line); break;
                    case "phase": c.Phase = EnumAt(kv.Value, new[] { "before", "written", "after" }, ctx, "phase", line); break;
                    case "target": c.Target = kv.Value; break;
                    case "min":
                    case "max":
                        if (!double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            throw new FormatException($"{ctx}: {kv.Key} '{kv.Value}' is not a number (line {line}).");
                        if (kv.Key == "min") c.Min = kv.Value; else c.Max = kv.Value;
                        break;
                    case "of": c.Of = it.List("of"); break;
                    case "when":
                        if (string.IsNullOrWhiteSpace(kv.Value))
                            throw new FormatException($"{ctx}: 'when:' has no condition (line {line}).");
                        if (WorkflowPredicate.Parse(kv.Value, out var perr) == null)
                            throw new FormatException($"{ctx}: when '{kv.Value}' (line {line}): {perr}");
                        c.When = kv.Value;
                        break;
                    default: throw Unknown(ctx, "Check", kv.Key, line, CheckKeys);
                }
            }
            if (c.Kind == null)
                throw new FormatException($"{ctx}: a check is missing 'kind' (line {c.Line}).");
            // Contract 1.7: every kind runs in exactly one phase, so the phase is implied and a different one
            // written beside it is a mistake, not a choice.
            var phase = CheckPhases[c.Kind];
            if (c.Phase != null && c.Phase != phase)
                throw new FormatException($"{ctx}: '{c.Kind}' is a check that runs {PhaseWords(phase)}, so it cannot take phase '{c.Phase}' (line {it.LineOf("phase")}). Write 'phase: {phase}', or leave the phase out.");
            c.Phase = phase;

            var takesRange = c.Kind == "result_range" || c.Kind == "row_count";
            if ((c.Min != null && !takesRange) || (c.Max != null && !takesRange && c.Kind != "max_chars"))
                throw new FormatException($"{ctx}: 'min:' and 'max:' apply only to result_range and row_count, and 'max:' to max_chars (got '{c.Kind}', line {c.Line}).");
            if (c.Kind == "result_range" && c.Min == null && c.Max == null)
                throw new FormatException($"{ctx}: a result_range check needs 'min:', 'max:' or both (line {c.Line}).");
            if (c.Kind == "max_chars" && (c.Max == null || !int.TryParse(c.Max, NumberStyles.None, CultureInfo.InvariantCulture, out var mx) || mx < 1))
                throw new FormatException($"{ctx}: a max_chars check needs 'max:' as a whole number above zero (line {c.Line}).");
            // 'of' names the tables a refs_within check allows, or the one question whose answer a dax_probe compares with.
            if (c.Of.Length > 0 && c.Kind != "refs_within" && c.Kind != "dax_probe")
                throw new FormatException($"{ctx}: 'of:' applies only to a refs_within check, or to a dax_probe naming the question it compares with (got '{c.Kind}', line {it.LineOf("of")}).");
            if (c.Kind == "dax_probe" && c.Of.Length > 1)
                throw new FormatException($"{ctx}: a dax_probe compares with one question's answer, so 'of:' names one (line {it.LineOf("of")}).");
            if (c.Kind == "refs_within" && c.Of.Length == 0)
                throw new FormatException($"{ctx}: a refs_within check needs 'of:' listing the tables the text may use (line {c.Line}).");
            return c;
        }

        private static string PhaseWords(string phase) =>
            phase == "before" ? "before anything changes" : phase == "written" ? "when the text is written" : "after the change";

        // ---- one step, once its fences are read -------------------------------------------------------------

        /// <summary>Contract 1.3: what each performer may carry. A step Semanticus runs has an action and no
        /// gate; a step it does not run has neither an action nor blanks, which fill an action's arguments.</summary>
        private static void FinishV3Step(WorkflowStep step, bool sawGate)
        {
            var n = step.Number;
            if (step.Performer == WorkflowPerformers.Semanticus)
            {
                if (step.Action == null)
                    throw new FormatException($"Step {n}: a step Semanticus runs needs an 'action:' naming the op it runs. If your assistant does this step, write 'performer: assistant' in its step block; to stop and ask a person here, write 'performer: person'.");
                if (sawGate)
                    throw new FormatException($"Step {n}: A step Semanticus runs uses questions, blanks and checks, not a gate. Remove the 'yaml gate' block, or write 'performer: assistant' if your assistant does this step.");
            }
            else
            {
                if (step.Action != null)
                    throw new FormatException($"Step {n}: 'action:' is only for a step Semanticus runs, and this step's performer is {step.Performer} (line {step.Action.Line}). Remove 'action:', or write 'performer: semanticus'.");
                if (step.Blanks.Length > 0)
                    throw new FormatException($"Step {n}: a blank fills an argument of an action, and only a step Semanticus runs has one (line {step.Blanks[0].Line}). Remove 'blanks:', or write 'performer: semanticus' with an action.");
            }
            // Contract 1.6: the phase follows from the op when the file does not say. An op the table does not
            // know gets none, and check_workflow says why.
            if (!step.PhaseWritten && step.Action != null)
                step.Phase = OpParameterKinds.GroupOf(step.Action.Op);
        }

        // ---- the whole file -----------------------------------------------------------------------------

        /// <summary>The references between the file's own parts: every {{...}} names something the file
        /// declares, earlier, and every blank is tied to exactly the argument it fills.</summary>
        private static void ValidateV3(WorkflowDef def)
        {
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in def.Inputs)
                if (!inputs.Add(i.Name))
                    throw new FormatException($"inputs: '{i.Name}' is asked twice (line {i.Line}). Each input needs a name of its own.");
            foreach (var i in def.Inputs)
                foreach (var text in new[] { i.From, i.Default })
                    foreach (Match m in ComposerRef.Matches(text ?? ""))
                        if (m.Groups[1].Value != "inputs" || !inputs.Contains(m.Groups[2].Value))
                            throw new FormatException($"inputs: '{i.Name}' reads '{m.Value}' (line {i.Line}). An input is asked before any step runs, so it can only read another input.");

            var askedSoFar = new HashSet<string>(StringComparer.Ordinal);
            var questionStep = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var s = 0; s < def.Steps.Length; s++)
            {
                var st = def.Steps[s];
                var earlier = def.Steps.Take(s).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var q in st.Questions)
                {
                    if (questionStep.TryGetValue(q.Name, out var first))
                        throw new FormatException($"Step {st.Number}: the question '{q.Name}' is also asked on Step {first} (line {q.Line}). A question name has to be unique within the file, so '{{{{questions.{q.Name}}}}}' means one thing.");
                    questionStep[q.Name] = st.Number;
                    askedSoFar.Add(q.Name);
                }

                string Where(string what) => $"Step {st.Number}: {what}";
                void Resolve(Match m, string what, int line)
                {
                    var root = m.Groups[1].Value; var rest = m.Groups[2].Value;
                    switch (root)
                    {
                        case "inputs":
                            if (!inputs.Contains(rest))
                                throw new FormatException(Where($"{what} reads '{m.Value}', but the frontmatter's 'inputs:' has no '{rest}' (line {line}). Add the input, or give it a value."));
                            return;
                        case "questions":
                            if (!askedSoFar.Contains(rest))
                                throw new FormatException(Where($"{what} reads '{m.Value}', but no question named '{rest}' is asked on this step or an earlier one (line {line})."));
                            return;
                        case "steps":
                            var sm = StepsRefPath.Match(rest);
                            if (!sm.Success)
                                throw new FormatException(Where($"{what} reads '{m.Value}', which is not a step result. Write it as '{{{{steps.<step id>.<result>}}}}' (line {line})."));
                            if (!earlier.Contains(sm.Groups[1].Value))
                                throw new FormatException(Where($"{what} reads '{m.Value}', but '{sm.Groups[1].Value}' is not an earlier step (line {line}). A step can only read what an earlier step returned."));
                            return;
                        case "blanks":
                            throw new FormatException(Where($"{what} reads '{m.Value}', and only an action's argument can be filled by a blank (line {line})."));
                        default:
                            throw new FormatException(Where($"{what} reads '{m.Value}', which is not a reference this format has (line {line}). A reference starts with inputs, questions, blanks or steps."));
                    }
                }

                var blanks = new Dictionary<string, WorkflowBlank>(StringComparer.Ordinal);
                foreach (var b in st.Blanks)
                    if (!blanks.TryAdd(b.Name, b))
                        throw new FormatException(Where($"the blank '{b.Name}' is declared twice (line {b.Line}). Each blank needs a name of its own."));

                foreach (var arg in st.Action?.Arguments ?? Array.Empty<ActionArgument>())
                    foreach (var value in arg.Value != null ? new[] { arg.Value } : arg.Values ?? Array.Empty<string>())
                        foreach (Match m in ComposerRef.Matches(value))
                        {
                            if (m.Groups[1].Value != "blanks") { Resolve(m, $"argument '{arg.Name}'", arg.Line); continue; }
                            var name = m.Groups[2].Value;
                            if (arg.Value == null || m.Value != arg.Value.Trim())
                                throw new FormatException(Where($"argument '{arg.Name}' mixes '{m.Value}' with other text (line {arg.Line}). A blank's text becomes the whole argument, so write the argument as '{m.Value}' alone."));
                            if (!blanks.TryGetValue(name, out var blank))
                                throw new FormatException(Where($"argument '{arg.Name}' reads '{m.Value}', but this step has no blank named '{name}' (line {arg.Line}). Add the blank, or give '{arg.Name}' a value."));
                            if (!string.Equals(blank.Fills, arg.Name, StringComparison.Ordinal))
                                throw new FormatException(Where($"argument '{arg.Name}' is filled by the blank '{name}', but that blank fills '{blank.Fills}' (line {blank.Line}). A blank's text becomes the argument its 'fills:' names, so the two have to match."));
                        }

                foreach (var b in st.Blanks)
                {
                    var arg = st.Action?.Argument(b.Fills);
                    if (arg != null && !string.Equals(arg.Value?.Trim(), "{{blanks." + b.Name + "}}", StringComparison.Ordinal))
                        throw new FormatException(Where($"the blank '{b.Name}' fills '{b.Fills}', but that argument already has a value (line {arg.Line}). Write the argument as '{{{{blanks.{b.Name}}}}}', or remove it and let the blank fill it."));
                    foreach (var dep in b.DependsOn)
                        if (!askedSoFar.Contains(dep) && !inputs.Contains(dep))
                            throw new FormatException(Where($"the blank '{b.Name}' dependsOn '{dep}', which is not a question this step or an earlier one asks, nor one of the inputs (line {b.Line})."));
                }

                foreach (var q in st.Questions)
                    foreach (var text in new[] { q.From, q.Default })
                        foreach (Match m in ComposerRef.Matches(text ?? ""))
                            Resolve(m, $"question '{q.Name}'", q.Line);
            }
        }

        /// <summary>The v3 check_workflow findings: the judgements that need the op table. Each is a warn,
        /// so a file carrying one is not Ok. Parse-level refusals never reach here.</summary>
        public static List<CheckFinding> V3Findings(WorkflowDef def)
        {
            var findings = new List<CheckFinding>();
            if (def == null || def.SchemaVersion < 3) return findings;
            void Warn(string message) => findings.Add(new CheckFinding { Severity = "warn", Message = message });

            foreach (var i in def.Inputs)
            {
                // An input's 'jev:' is the same promise as a question's (below): nothing answers it, so the run
                // would ask the person as if the line were not there. compose_task refuses it; this says so first.
                if (i.Jev != null)
                    Warn($"input '{i.Name}' says 'jev: {i.Jev}', but Jev does not answer workflow inputs yet, so the line would do nothing. Remove it. The input is asked when the run starts.");
                if (i.From != null && !OpParameterKinds.IsEnumerator(i.From, out _))
                    Warn($"input '{i.Name}' lists its options from '{i.From}', which is not a list Semanticus reads. The lists are: {string.Join(", ", OpParameterKinds.Enumerators.Keys)}.");
            }

            WorkflowStep firstAfter = null;
            foreach (var st in def.Steps)
            {
                // Placement (contract 1.6): every change step comes before every step after the change.
                if (st.Phase == OpParameterKinds.After) firstAfter ??= st;
                else if (st.Phase == OpParameterKinds.Change && firstAfter != null)
                    Warn($"{st.Id} changes the model, but it comes after '{firstAfter.Id}', which runs after the change. Every change step comes before every step after the change, so move {st.Id} above {firstAfter.Id}.");

                foreach (var q in st.Questions)
                {
                    // 'jev:' is parsed so the format can hold it, but no decision point answers a workflow question
                    // yet, so a run would ask the person as if the line were not there. Refused until one backs it.
                    if (q.Jev != null)
                        Warn($"{st.Id} question '{q.Name}' says 'jev: {q.Jev}', but Jev does not answer workflow questions yet, so the line would do nothing. Remove it. The question is asked as 'by:' says.");
                    if (q.From != null && !OpParameterKinds.IsEnumerator(q.From, out _))
                        Warn($"{st.Id} question '{q.Name}' lists its options from '{q.From}', which is not a list Semanticus reads. The lists are: {string.Join(", ", OpParameterKinds.Enumerators.Keys)}.");
                    // Contract 1.4: every question is asked before the change, so options that exist only once an
                    // earlier step has written cannot be listed in time, unless the author stops the run there.
                    if (st.Performer == WorkflowPerformers.Person) continue;
                    foreach (Match m in ComposerRef.Matches(q.From ?? ""))
                    {
                        if (m.Groups[1].Value != "steps") continue;
                        var id = StepsRefPath.Match(m.Groups[2].Value).Groups[1].Value;
                        var writer = def.Steps.FirstOrDefault(p => p.Id == id);
                        if (writer?.Phase == OpParameterKinds.Change)
                            Warn($"{st.Id} asks '{q.Name}' from options that exist only after '{id}' changes the model, but every question is asked before the change. Ask it on a step that stops for you (performer: person).");
                    }
                }

                if (st.Performer != WorkflowPerformers.Semanticus || st.Action == null) continue;
                var op = st.Action.Op;
                if (!OpParameterKinds.IsExecutable(op))
                {
                    Warn($"{st.Id} runs '{op}', which is not one of the ops a step Semanticus runs may name. Give this step to your assistant (performer: assistant), or choose an op from that list.");
                    continue;
                }
                var group = OpParameterKinds.GroupOf(op);
                if (st.PhaseWritten && group == OpParameterKinds.After && st.Phase == OpParameterKinds.Change)
                    Warn($"{st.Id} puts '{op}' in the change group, but {op} leaves the model, so Undo cannot reach it. Write 'phase: after'.");
                if (st.PhaseWritten && group == OpParameterKinds.Change && st.Phase == OpParameterKinds.After)
                    Warn($"{st.Id} puts '{op}' after the change, but {op} changes the model and belongs in the change group, where the whole change is one Undo. Write 'phase: change'.");

                var parameters = OpParameterKinds.Parameters(op) ?? Array.Empty<OpParameterKinds.OpParameter>();
                foreach (var arg in st.Action.Arguments)
                {
                    var p = parameters.FirstOrDefault(x => x.Name == arg.Name);
                    if (p == null)
                        Warn($"{st.Id} gives {op} an argument '{arg.Name}' it does not take. It takes: {string.Join(", ", parameters.Select(x => x.Name))}.");
                    else if (OpParameterKinds.KindOf(op, p.Name) == "internal")
                        Warn($"{st.Id} sets '{arg.Name}' of {op}, which Semanticus fills itself from the session. Remove it.");
                    else if (arg.Values != null && !p.IsList)
                        Warn($"{st.Id} gives '{arg.Name}' of {op} a list, and it takes one value.");
                }

                foreach (var b in st.Blanks)
                {
                    var kind = OpParameterKinds.KindOf(op, b.Fills);
                    if (parameters.All(x => x.Name != b.Fills))
                    {
                        Warn($"{st.Id} blank '{b.Name}' fills '{b.Fills}', which is not an argument of {op}.");
                        continue;
                    }
                    var authored = OpParameterKinds.AuthoredKind(op, b.Fills);
                    if (authored != null ? authored != b.Kind : !(b.Kind == "choice" && kind != null && kind.StartsWith("pick:", StringComparison.Ordinal)))
                        Warn($"{st.Id} blank '{b.Name}' is kind '{b.Kind}', but '{b.Fills}' of {op} is {(authored != null ? "'" + authored + "' text" : "not text anyone writes")}."
                            + (authored != null ? $" Write 'kind: {authored}'." : ""));
                    if (b.Choices != null && !OpParameterKinds.IsEnumerator(b.Choices, out _))
                        Warn($"{st.Id} blank '{b.Name}' picks from '{b.Choices}', which is not a list Semanticus reads.");
                    if (b.Generator != null)
                    {
                        if (!OpParameterKinds.Generators.TryGetValue(b.Generator, out var gen))
                            Warn($"{st.Id} blank '{b.Name}' names the generator '{b.Generator}', which Semanticus does not have. It has: {string.Join(", ", OpParameterKinds.Generators.Keys)}.");
                        else if (gen.Op != op || !gen.Writes.Contains(b.Fills))
                            Warn($"{st.Id} blank '{b.Name}' names the generator '{b.Generator}', which does not write '{b.Fills}' of {op}.");
                    }
                }

                // Contract 1.5: the engine, not the author, decides a step needs a blank. A required argument
                // that only a writer can supply, with no value and no blank, needs one.
                foreach (var p in parameters.Where(x => x.Required))
                {
                    if (st.Action.Argument(p.Name) != null || st.Blanks.Any(b => b.Fills == p.Name)) continue;
                    var authored = OpParameterKinds.AuthoredKind(op, p.Name);
                    var kind = OpParameterKinds.KindOf(op, p.Name);
                    if (authored != null)
                        Warn($"{st.Id}: '{p.Name}' of {op} is text that neither Semanticus nor Jev can write, so this step needs a blank for it. Add a blank with 'kind: {authored}' and 'fills: {p.Name}'.");
                    else if (kind != "internal")
                        Warn($"{st.Id}: {op} needs a value for '{p.Name}'. Give it a value, an input or a question.");
                }
            }
            return findings;
        }

        /// <summary>The v3 fields a guided run cannot honour yet. Starting a file that carries one would run
        /// it as if the field were not there, which is the silent absorption schemaVersion exists to stop.</summary>
        /// <summary>The v3 fields NO run path honours yet (lane L4 narrowed this as the composed run landed). A file with a
        /// step Semanticus runs is a COMPOSED file: compose_task drafts it and runs its action, questions, blanks, the
        /// checks a draft runs, the brief, the inputs and the notes. Everything else is a GUIDED file for start_workflow,
        /// which runs none of the composer fields. So what is left: a person's stop in a guided file (a draft runs one;
        /// start_workflow has nothing to pause at), a gate on a person's stop inside a composed file (an assistant's gated
        /// step runs in the draft's guided sub-run, lane l9-finish), a check kind a draft does not run, and any composer
        /// field on a guided file.</summary>
        private static IEnumerable<(string StepId, int StepNumber, string Field)> V3UnrunFields(WorkflowDef def)
        {
            if (def.SchemaVersion < 3) yield break;
            var composed = IsComposed(def);
            foreach (var st in def.Steps)
            {
                if (composed)
                {
                    // A draft runs a person's stop and an assistant's step too (lane author-gaps, B9), and an assistant's
                    // step that carries a gate runs it in the draft's guided sub-run, exactly as a guided step does (lane
                    // l9-finish, composer contract 1.3). A person's stop changes nothing, so a gate on one checks nothing
                    // a draft could run.
                    if (st.Performer == WorkflowPerformers.Person && st.Gate != null) { yield return (st.Id, st.Number, "gate"); continue; }
                    if (st.Checks.Any(c => !Composer.DraftCheckKinds.Runs(c.Phase, c.Kind))) yield return (st.Id, st.Number, "checks");
                    continue;
                }
                // A guided file runs a person's stop by no path: start_workflow has no stop to pause at.
                if (st.Performer == WorkflowPerformers.Person) { yield return (st.Id, st.Number, "performer"); continue; }
                if (st.Action != null) yield return (st.Id, st.Number, "action");
                if (st.Questions.Length > 0) yield return (st.Id, st.Number, "questions");
                if (st.Blanks.Length > 0) yield return (st.Id, st.Number, "blanks");
                if (st.Checks.Length > 0) yield return (st.Id, st.Number, "checks");
                if (st.Brief != null) yield return (st.Id, st.Number, "instructions");
            }
            if (!composed && def.Inputs.Length > 0) yield return ("frontmatter", 0, "inputs");
            if (!composed && def.AssistantNotes != null) yield return ("frontmatter", 0, "assistantNotes");
        }

        /// <summary>True when a v3 file runs as a draft: compose_task drafts it; start_workflow does not run it. That is a
        /// file with a step Semanticus runs, and (N3, lane l9-finish) any v3 file that uses a field only a draft runs: a
        /// person's stop, an action, questions, blanks, checks, a Brief, inputs or notes for the assistant. A draft runs
        /// the assistant's steps too (author-gaps B9), so such a file needs no step of Semanticus's own; Author and
        /// Describe it write exactly these files, and none of them may read as a field this version does not run.</summary>
        public static bool IsComposed(WorkflowDef def) =>
            def != null && def.SchemaVersion >= 3
            && (def.Steps.Any(s => s.Performer == WorkflowPerformers.Semanticus || s.Performer == WorkflowPerformers.Person
                                   || s.Action != null || s.Questions.Length > 0 || s.Blanks.Length > 0 || s.Checks.Length > 0 || s.Brief != null)
                || def.Inputs.Length > 0 || def.AssistantNotes != null);

        /// <summary>Every author-written string of the v3 fields, for the rules that read all of them.</summary>
        private static IEnumerable<string> V3AuthoredStrings(WorkflowStep st)
        {
            yield return st.Brief;
            if (st.Action != null)
            {
                yield return st.Action.Op;
                foreach (var a in st.Action.Arguments)
                {
                    yield return a.Name; yield return a.Value;
                    foreach (var v in a.Values ?? Array.Empty<string>()) yield return v;
                }
            }
            foreach (var q in st.Questions)
            {
                yield return q.Name; yield return q.Question; yield return q.From; yield return q.Jev; yield return q.Default;
                foreach (var v in q.Values) yield return v;
            }
            foreach (var b in st.Blanks)
            {
                yield return b.Name; yield return b.Fills; yield return b.Choices; yield return b.Generator;
                foreach (var d in b.DependsOn) yield return d;
            }
            foreach (var c in st.Checks)
            {
                yield return c.Target; yield return c.When;
                foreach (var o in c.Of) yield return o;
            }
        }
    }
}
