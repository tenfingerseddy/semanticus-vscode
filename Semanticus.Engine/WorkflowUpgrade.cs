using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace Semanticus.Engine
{
    public sealed class WorkflowUpgradeResult
    {
        public string Name { get; set; }
        public bool DryRun { get; set; }
        public bool Changed { get; set; }
        public bool CanApply { get; set; }
        public string Reason { get; set; }
        public WorkflowDocumentResult Document { get; set; }
        public string ProposedText { get; set; }
        public string Diff { get; set; }
        public string ParseError { get; set; }
        public int AddedLines { get; set; }
        [JsonPropertyName("suggested_next_action")]
        [Newtonsoft.Json.JsonProperty("suggested_next_action")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorkflowUpgradeNextAction SuggestedNextAction { get; set; }
    }

    public sealed class WorkflowUpgradeNextAction
    {
        public string Op { get; set; }
        public WorkflowUpgradeApplyArgs Args { get; set; }
        public string Why { get; set; }
    }

    public sealed class WorkflowUpgradeApplyArgs
    {
        public string Name { get; set; }
        public bool DryRun { get; set; }
        public string ExpectByteHash { get; set; }
        public string ExpectPath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SessionId { get; set; }
    }

    /// <summary>Source edits only. Definitions are compared for semantic drift, never rendered back to text.</summary>
    internal static class WorkflowUpgrade
    {
        private sealed record Line(string Text, string Ending);
        private sealed record DiffLine(char Kind, Line Line);

        internal static WorkflowUpgradeResult Preview(WorkflowDocumentResult document, bool dryRun)
        {
            var result = new WorkflowUpgradeResult
            {
                Name = document.Name, DryRun = dryRun, Document = document,
                ProposedText = document.ExactText, Diff = "",
            };
            if (!document.Metadata.Parses)
            {
                result.ParseError = document.Metadata.ParseError;
                result.Reason = "The saved workflow does not parse. Repair it before upgrading. " + result.ParseError;
                return result;
            }
            if (document.Metadata.SchemaVersion == 2)
            {
                result.Reason = "This workflow already uses version 2. Nothing was written.";
                return result;
            }
            var text = document.ExactText;
            var parserText = text.Length > 0 && text[0] == (char)0xfeff ? text.Substring(1) : text;
            var original = WorkflowParser.Parse(parserText);
            if (original.HasUnreadSchemaVersion)
            {
                result.Reason = "This file has an inactive or ambiguous schemaVersion declaration. Resolve it before upgrading. Nothing was written.";
                return result;
            }
            var lines = ReadLines(text);
            var (versionLine, stepLines) = WorkflowParser.UpgradeSourceAnchors(parserText);
            if (stepLines.Length != original.Steps.Length)
                throw new InvalidOperationException("The workflow's parsed step boundaries do not match its source.");
            var defaultEnding = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? ((char)10).ToString();
            var moveVersion = versionLine > 0 && lines.Skip(1).Take(versionLine - 1)
                .Any(line => !string.IsNullOrWhiteSpace(line.Text) && !line.Text.TrimStart().StartsWith("#", StringComparison.Ordinal));
            var version = versionLine < 0 ? new Line("schemaVersion: 2", lines[0].Ending) : PromoteVersion(lines[versionLine]);
            var stepNumbers = stepLines.Select((line, index) => (line, index))
                .ToDictionary(pair => pair.line, pair => original.Steps[pair.index].Number);
            var diff = new List<DiffLine>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (i == versionLine)
                {
                    diff.Add(new DiffLine('-', line));
                    if (!moveVersion) diff.Add(new DiffLine('+', version));
                }
                else if (stepNumbers.ContainsKey(i) && line.Ending.Length == 0)
                {
                    // A heading at EOF still needs a delimiter before its new fence; keep EOF unterminated.
                    diff.Add(new DiffLine('-', line));
                    diff.Add(new DiffLine('+', line with { Ending = defaultEnding }));
                }
                else diff.Add(new DiffLine(' ', line));

                if (i == 0 && (versionLine < 0 || moveVersion)) diff.Add(new DiffLine('+', version));
                if (stepNumbers.TryGetValue(i, out var number))
                {
                    var ending = line.Ending.Length > 0 ? line.Ending : defaultEnding;
                    diff.Add(new DiffLine('+', new Line("```yaml step", ending)));
                    diff.Add(new DiffLine('+', new Line("id: step-" + number, ending)));
                    diff.Add(new DiffLine('+', new Line("```", line.Ending.Length > 0 ? ending : "")));
                }
            }
            result.ProposedText = string.Concat(diff.Where(line => line.Kind != '-')
                .Select(line => line.Line.Text + line.Line.Ending));
            result.Diff = RenderDiff(document.Name, diff);
            result.AddedLines = diff.Count(line => line.Kind == '+');
            var proposedParserText = result.ProposedText.Length > 0 && result.ProposedText[0] == (char)0xfeff
                ? result.ProposedText.Substring(1) : result.ProposedText;
            var upgraded = WorkflowParser.Parse(proposedParserText);
            if (upgraded.Error != null)
            {
                result.ParseError = upgraded.Error;
                result.Reason = "Version 2 refuses this source. Nothing was written. " + upgraded.Error;
                return result;
            }
            var difference = SemanticDifference(original, upgraded, "workflow", versionLine >= 0);
            if (difference != null)
            {
                result.Reason = "Upgrading would change existing workflow content or gate settings at " + difference + ". Nothing was written.";
                return result;
            }
            result.CanApply = document.Library == "user";
            result.Reason = result.CanApply
                ? "The preview preserves the workflow's existing meaning. Apply it with this document's path and byteHash."
                : "Stock workflows are read-only. Create a project copy, then preview that copy.";
            return result;
        }

        private static Line PromoteVersion(Line line)
        {
            var start = line.Text.IndexOf(':') + 1;
            while (start < line.Text.Length && char.IsWhiteSpace(line.Text[start])) start++;
            var end = line.Text.Length;
            if (start < end && (line.Text[start] == '"' || line.Text[start] == (char)39))
            {
                end = line.Text.IndexOf(line.Text[start], start + 1);
                start++;
            }
            else
            {
                var comment = line.Text.IndexOf(" #", start, StringComparison.Ordinal);
                if (comment >= 0) end = comment;
            }
            while (end > start && char.IsWhiteSpace(line.Text[end - 1])) end--;
            if (end <= start || line.Text[end - 1] != '1')
                throw new InvalidOperationException("The active version 1 declaration cannot be promoted without rewriting its value.");
            return line with { Text = line.Text.Substring(0, end - 1) + "2" + line.Text.Substring(end) };
        }

        private static List<Line> ReadLines(string text)
        {
            var lines = new List<Line>();
            for (var start = 0; start < text.Length;)
            {
                var newline = text.IndexOf((char)10, start);
                if (newline < 0) { lines.Add(new Line(text.Substring(start), "")); break; }
                var end = newline > start && text[newline - 1] == (char)13 ? newline - 1 : newline;
                lines.Add(new Line(text.Substring(start, end - start), text.Substring(end, newline + 1 - end)));
                start = newline + 1;
            }
            return lines;
        }

        private static string RenderDiff(string name, List<DiffLine> lines)
        {
            var lf = (char)10;
            var text = new StringBuilder();
            text.Append("--- a/").Append(name).Append(".md").Append(lf);
            text.Append("+++ b/").Append(name).Append(".md").Append(lf);
            text.Append("@@ -1,").Append(lines.Count(line => line.Kind != '+'))
                .Append(" +1,").Append(lines.Count(line => line.Kind != '-')).Append(" @@").Append(lf);
            foreach (var line in lines)
            {
                text.Append(line.Kind).Append(line.Line.Text).Append(line.Line.Ending);
                if (line.Line.Ending.Length == 0)
                    text.Append(lf).Append((char)92).Append(" No newline at end of file").Append(lf);
            }
            return text.ToString();
        }

        private static string SemanticDifference(object before, object after, string path, bool promotedVersion)
        {
            if (ReferenceEquals(before, after)) return null;
            if (before == null || after == null || before.GetType() != after.GetType()) return path;
            var type = before.GetType();
            if (type.IsValueType || before is string) return before.Equals(after) ? null : path;
            if (before is IDictionary left && after is IDictionary right)
            {
                var ignored = promotedVersion && path == "workflow.Provenance" ? "schemaVersion" : null;
                var leftKeys = left.Keys.Cast<object>().Where(key => !Equals(key, ignored)).ToArray();
                var rightKeys = right.Keys.Cast<object>().Where(key => !Equals(key, ignored)).ToArray();
                if (leftKeys.Length != rightKeys.Length) return path;
                foreach (var key in leftKeys)
                {
                    if (!right.Contains(key)) return path;
                    var difference = SemanticDifference(left[key], right[key], path + "[" + key + "]", promotedVersion);
                    if (difference != null) return difference;
                }
                return null;
            }
            if (before is IEnumerable sequence && after is IEnumerable other)
            {
                var leftItems = sequence.Cast<object>().ToArray();
                var rightItems = other.Cast<object>().ToArray();
                if (leftItems.Length != rightItems.Length) return path;
                for (var i = 0; i < leftItems.Length; i++)
                {
                    var difference = SemanticDifference(leftItems[i], rightItems[i], path + "[" + i + "]", promotedVersion);
                    if (difference != null) return difference;
                }
                return null;
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (type == typeof(WorkflowDef) && property.Name == nameof(WorkflowDef.SchemaVersion)) continue;
                if (type == typeof(WorkflowStep) && property.Name == nameof(WorkflowStep.HasExplicitId)) continue;
                var difference = SemanticDifference(property.GetValue(before), property.GetValue(after), path + "." + property.Name, promotedVersion);
                if (difference != null) return difference;
            }
            return null;
        }
    }
}
