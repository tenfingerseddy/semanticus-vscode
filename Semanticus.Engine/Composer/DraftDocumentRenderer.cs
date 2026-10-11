// A draft as a format-3 workflow file (composer contract 10.1 and 6; lane author-gaps, B8, the L7 finish line).
//
// "Two doors, one draft": the Plan's quick path and Author's full path hold the same draft. Author opens it with its
// own editor, so the draft has to become the document Author edits. That is this renderer: edit_draft open_in_author
// returns the draft written as a workflow file, not saved anywhere, and Author opens it as a new, unsaved workflow.
// Save workflow writes it to .semanticus/workflows/ like any other new workflow.
//
// WHAT IS KEPT, AND WHAT IS NOT (contract 10.1: "Save workflow ... drops the run state").
//   Kept: every KEPT step in order, with who does it, its action, its questions, its blanks, its Brief and its
//   checks; the draft's own questions as the workflow's inputs ("asked each time it starts"); the notes for the
//   assistant; the title; the person's words as the description.
//   Not kept: the answers, the words a blank was filled with, keep or drop, the approval and anything that ran. A
//   workflow asks its questions and has its blanks written on every run; that is what makes it reusable. A dropped
//   step is the person's choice, so it is left out.
//
// THE FORMAT'S OWN RULES, KEPT BY CONSTRUCTION. A question or blank name is letters, digits and underscores, so a
// menu question's id ("add-measure-tableRef") is written "add_measure_tableRef" and every reference to it follows.
// A v3 step runs one action, so a draft step with several becomes one step per action; each blank goes with the
// action it fills, and the Brief and checks with the first step that has a blank (else the last). Every string is
// written double-quoted, so no value can change meaning as YAML. The result is parsed before it is returned: a draft
// that does not make a valid file says so instead of handing Author something that will not open.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The draft as a workflow file, with a name free in this model's library.</summary>
        internal DraftDocument RenderDraftDocument(Draft d)
        {
            var taken = new HashSet<string>(LoadWorkflowDefs().Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
            var basis = DraftDocumentRenderer.NameFrom(d.Title);
            var name = basis;
            for (var n = 2; taken.Contains(name); n++) name = basis + "-" + n.ToString(CultureInfo.InvariantCulture);
            var text = DraftDocumentRenderer.Render(d, name);
            var def = WorkflowParser.Parse(text);
            if (def.Error != null)
                throw new InvalidOperationException("This draft could not be written as a workflow: " + def.Error);
            return new DraftDocument { Name = name, Text = text };
        }
    }
}

namespace Semanticus.Engine.Composer
{
    internal static class DraftDocumentRenderer
    {
        private const string Lf = "\n";

        private static readonly JsonSerializerOptions Quote = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly Regex NotName = new Regex("[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex Ref = new Regex(@"\{\{\s*(inputs|questions)\.([^{}\s]+)\s*\}\}", RegexOptions.Compiled);

        /// <summary>A workflow name from a title: lower case, words joined by hyphens, 48 characters at most.</summary>
        internal static string NameFrom(string title)
        {
            var name = NotName.Replace((title ?? "").ToLowerInvariant(), "-").Trim('-');
            if (name.Length > 48) name = name.Substring(0, 48).TrimEnd('-');
            return name.Length == 0 ? "from-a-draft" : name;
        }

        /// <summary>A plain name for a question or blank: letters, digits and underscores, starting with a letter.</summary>
        private static string Plain(string name)
        {
            var s = Regex.Replace(name ?? "", "[^A-Za-z0-9_]", "_");
            return s.Length == 0 || char.IsDigit(s[0]) ? "q_" + s : s;
        }

        /// <summary>Always double-quoted, so no value can read as another YAML type or break the line.</summary>
        private static string Q(string value) => JsonSerializer.Serialize(value ?? "", Quote);

        private static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Q)) + "]";

        internal static string Render(Draft d, string name)
        {
            // The question names as the file writes them, and where each is asked.
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in d.Questions)
            {
                var n = Plain(q.Name);
                var candidate = n;
                for (var i = 2; !used.Add(candidate); i++) candidate = n + "_" + i.ToString(CultureInfo.InvariantCulture);
                names[q.Name] = candidate;
            }
            var inputs = d.Questions.Where(q => q.Step == null || !d.Steps.Any(s => s.Id == q.Step && s.Kept)).ToList();
            var inputNames = new HashSet<string>(inputs.Select(q => q.Name), StringComparer.Ordinal);
            string Fill(string value) => value == null ? null : Ref.Replace(value, m =>
            {
                var raw = m.Groups[2].Value;
                if (!names.TryGetValue(raw, out var plain)) return m.Value;
                return "{{" + (inputNames.Contains(raw) ? "inputs." : "questions.") + plain + "}}";
            });

            var b = new StringBuilder();
            b.Append("---").Append(Lf);
            b.Append("schemaVersion: 3").Append(Lf);
            b.Append("name: ").Append(name).Append(Lf);
            b.Append("title: ").Append(Q(OneLine(d.Title))).Append(Lf);
            b.Append("description: ").Append(Q(OneLine(string.IsNullOrWhiteSpace(d.Words) ? "Made from a draft on the Plan." : d.Words))).Append(Lf);
            b.Append("version: 1").Append(Lf);
            if (!string.IsNullOrWhiteSpace(d.AssistantNotes)) b.Append("assistantNotes: ").Append(Q(d.AssistantNotes.Trim())).Append(Lf);
            if (inputs.Count > 0)
            {
                b.Append("inputs:").Append(Lf);
                foreach (var q in inputs) Question(b, "  ", q, names, Fill);
            }
            b.Append("---").Append(Lf);

            var number = 0;
            foreach (var st in d.Steps.Where(s => s.Kept).OrderBy(s => s.Number))
            {
                var pieces = st.BySemanticus && st.Actions.Count > 1 ? st.Actions.Select(a => new List<ComposedAction> { a }).ToList()
                           : new List<List<ComposedAction>> { st.Actions.ToList() };
                var carrier = pieces.FindIndex(piece => st.Blanks.Any(bl => piece.Any(a => FillsOf(a, bl))));
                if (carrier < 0) carrier = pieces.Count - 1;
                for (var i = 0; i < pieces.Count; i++)
                {
                    number++;
                    var id = i == 0 ? st.Id : st.Id + "-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    var action = pieces[i].FirstOrDefault();
                    var title = pieces.Count > 1 && i > 0 ? st.Title + ", part " + (i + 1).ToString(CultureInfo.InvariantCulture) : st.Title;
                    b.Append(Lf).Append("## Step ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(OneLine(title)).Append(Lf).Append(Lf);
                    if (i == 0 && !string.IsNullOrWhiteSpace(st.Body)) b.Append(OneLine(st.Body.Trim())).Append(Lf).Append(Lf);
                    b.Append("```yaml step").Append(Lf);
                    b.Append("id: ").Append(id).Append(Lf);
                    b.Append("performer: ").Append(st.BySemanticus ? WorkflowPerformers.Semanticus : st.Performer).Append(Lf);
                    // The phase follows from the op; it is written only where it does not (a step someone else does, after
                    // the change).
                    var derived = action == null ? DraftPhases.Change : OpParameterKinds.GroupOf(action.Op);
                    if (st.Phase == DraftPhases.After && derived != OpParameterKinds.After) b.Append("phase: after").Append(Lf);
                    if (action != null)
                    {
                        b.Append("action:").Append(Lf);
                        b.Append("  op: ").Append(action.Op).Append(Lf);
                        foreach (var kv in action.Args.Where(kv => kv.Value != null))
                            b.Append("  ").Append(kv.Key).Append(": ").Append(ArgValue(Fill(kv.Value))).Append(Lf);
                    }
                    if (i == 0)
                    {
                        var questions = d.Questions.Where(q => q.Step == st.Id && !inputNames.Contains(q.Name)).ToList();
                        if (questions.Count > 0)
                        {
                            b.Append("questions:").Append(Lf);
                            foreach (var q in questions) Question(b, "  ", q, names, Fill);
                        }
                    }
                    var blanks = st.Blanks.Where(bl => action != null && FillsOf(action, bl)).ToList();
                    if (blanks.Count > 0)
                    {
                        b.Append("blanks:").Append(Lf);
                        foreach (var bl in blanks)
                        {
                            b.Append("  - name: ").Append(Plain(bl.Name)).Append(Lf);
                            b.Append("    kind: ").Append(bl.Kind).Append(Lf);
                            b.Append("    fills: ").Append(bl.Fills).Append(Lf);
                            if (bl.By == "you") b.Append("    by: you").Append(Lf);
                            var deps = (bl.DependsOn ?? Array.Empty<string>()).Select(x => names.TryGetValue(x, out var nn) ? nn : Plain(x)).ToArray();
                            if (deps.Length > 0) b.Append("    dependsOn: ").Append(List(deps)).Append(Lf);
                            if (bl.MaxChars is int max) b.Append("    maxChars: ").Append(max.ToString(CultureInfo.InvariantCulture)).Append(Lf);
                            if (bl.Choices is { Length: > 0 }) b.Append("    choices: ").Append(Q(bl.Choices[0])).Append(Lf);
                            if (!string.IsNullOrWhiteSpace(bl.Generator)) b.Append("    generator: ").Append(bl.Generator).Append(Lf);
                        }
                    }
                    if (i == carrier)
                    {
                        if (!string.IsNullOrWhiteSpace(st.Brief)) b.Append("instructions: ").Append(Q(st.Brief.Trim())).Append(Lf);
                        if (st.Checks.Count > 0)
                        {
                            b.Append("checks:").Append(Lf);
                            foreach (var c in st.Checks)
                            {
                                b.Append("  - kind: ").Append(c.Kind).Append(Lf);
                                if (!string.IsNullOrWhiteSpace(c.Phase)) b.Append("    phase: ").Append(c.Phase).Append(Lf);
                                if (!string.IsNullOrWhiteSpace(c.Target)) b.Append("    target: ").Append(Q(Fill(c.Target))).Append(Lf);
                                if (c.Min is double mn) b.Append("    min: ").Append(mn.ToString("R", CultureInfo.InvariantCulture)).Append(Lf);
                                if (c.Max is double mx) b.Append("    max: ").Append(mx.ToString("R", CultureInfo.InvariantCulture)).Append(Lf);
                                if (c.Of is { Length: > 0 }) b.Append("    of: ").Append(List(c.Of.Select(o => names.TryGetValue(o, out var on) ? on : o))).Append(Lf);
                                if (!string.IsNullOrWhiteSpace(c.When)) b.Append("    when: ").Append(Q(Fill(c.When))).Append(Lf);
                            }
                        }
                    }
                    b.Append("```").Append(Lf);
                }
            }
            return b.ToString();
        }

        private static bool FillsOf(ComposedAction a, DraftBlank bl) =>
            a.Args.TryGetValue(bl.Fills ?? "", out var v) && v == "{{blanks." + bl.Name + "}}";

        private static void Question(StringBuilder b, string indent, DraftQuestion q, Dictionary<string, string> names, Func<string, string> fill)
        {
            b.Append(indent).Append("- name: ").Append(names[q.Name]).Append(Lf);
            var inner = indent + "  ";
            b.Append(inner).Append("question: ").Append(Q(OneLine(q.Question ?? q.Name))).Append(Lf);
            if (!string.IsNullOrWhiteSpace(q.Type) && q.Type != "text") b.Append(inner).Append("type: ").Append(q.Type).Append(Lf);
            if (!q.Required) b.Append(inner).Append("required: optional").Append(Lf);
            if (!string.IsNullOrWhiteSpace(q.By) && q.By != "you") b.Append(inner).Append("by: ").Append(q.By).Append(Lf);
            if (q.Values is { Length: > 0 }) b.Append(inner).Append("values: ").Append(List(q.Values)).Append(Lf);
            if (!string.IsNullOrWhiteSpace(q.From)) b.Append(inner).Append("from: ").Append(Q(fill(q.From))).Append(Lf);
            if (!string.IsNullOrWhiteSpace(q.Default)) b.Append(inner).Append("default: ").Append(Q(fill(q.Default))).Append(Lf);
        }

        /// <summary>An argument as the file writes it: a list the draft keeps as JSON stays a list, anything else is a
        /// quoted string (the op table reads the value, so its type is the op's business, as it is for a written file).</summary>
        private static string ArgValue(string value)
        {
            var t = value.Trim();
            if (t.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var items = JsonSerializer.Deserialize<string[]>(t);
                    if (items != null) return List(items);
                }
                catch (JsonException) { }
            }
            return Q(value);
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
