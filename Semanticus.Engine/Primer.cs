using System;
using System.Linq;

namespace Semanticus.Engine
{
    public sealed class PrimerDocument
    {
        public string ModelName { get; set; }
        public string ModelIdentity { get; set; }
        public string FilePath { get; set; }
        public string Markdown { get; set; }
        public string UpdatedUtc { get; set; }
        public bool Exists { get; set; }
        public string Note { get; set; }
    }

    public sealed class PrimerSuggestion
    {
        public string Id { get; set; }
        public string Section { get; set; }
        public string Markdown { get; set; }
        public string CapturedUtc { get; set; }
        public string Origin { get; set; }
        public string[] SourceRunIds { get; set; } = Array.Empty<string>();
        public int EvidenceCount { get; set; }
        public string Provenance { get; set; }
    }

    public sealed class PrimerSectionFreshness
    {
        public string Section { get; set; }
        public int SuggestedAdditions { get; set; }
        public string PrimerUpdatedUtc { get; set; }
    }

    public sealed class PrimerSuggestionList
    {
        public bool IsPro { get; set; }
        public PrimerSuggestion[] Suggestions { get; set; } = Array.Empty<PrimerSuggestion>();
        public PrimerSectionFreshness[] Sections { get; set; } = Array.Empty<PrimerSectionFreshness>();
        public string Note { get; set; }
    }

    public sealed class PrimerSuggestionDecision
    {
        public bool Changed { get; set; }
        public string Decision { get; set; }
        public PrimerDocument Primer { get; set; }
        public string Note { get; set; }
    }

    public static class PrimerContract
    {
        public static readonly string[] Sections = { "Overview", "Business context", "Gotchas", "Patterns", "Known issues", "History" };

        public static string Template(string modelName)
            => "# " + (string.IsNullOrWhiteSpace(modelName) ? "Model" : modelName) + " Primer\n\n"
             + string.Join("\n\n", Array.ConvertAll(Sections, x => "## " + x + "\n\n_Add what people and the AI Assistant should know._")) + "\n";

        public static void Validate(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) throw new InvalidOperationException("The Primer cannot be blank.");
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var found = new System.Collections.Generic.List<string>();
            foreach (var line in lines)
                if (line.StartsWith("## ", StringComparison.Ordinal)) found.Add(line.Substring(3).Trim());
            if (found.Count != Sections.Length)
                throw new InvalidOperationException("The Primer needs exactly these six sections, in order: " + string.Join(" · ", Sections) + ".");
            for (var i = 0; i < Sections.Length; i++)
                if (!string.Equals(found[i], Sections[i], StringComparison.Ordinal))
                    throw new InvalidOperationException("Primer section " + (i + 1) + " must be '" + Sections[i] + "'. Required order: " + string.Join(" · ", Sections) + ".");
        }

        public static string SuggestionSection(InsightRecord insight)
        {
            foreach (var key in insight?.Keys ?? Array.Empty<string>())
            {
                if (!key.StartsWith("primer:", StringComparison.OrdinalIgnoreCase)) continue;
                var requested = key.Substring("primer:".Length).Replace('-', ' ').Trim();
                var exact = Sections.FirstOrDefault(x => string.Equals(x, requested, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            return string.Equals(insight?.Kind, "post-mortem", StringComparison.OrdinalIgnoreCase) ? "Known issues" : "Patterns";
        }

        public static string ApplySuggestion(string markdown, PrimerSuggestion suggestion)
        {
            Validate(markdown);
            if (suggestion == null || !Sections.Contains(suggestion.Section, StringComparer.Ordinal))
                throw new InvalidOperationException("The Primer suggestion does not name one of the six fixed sections.");
            var normalized = markdown.Replace("\r\n", "\n");
            var marker = "## " + suggestion.Section;
            var start = normalized.IndexOf(marker, StringComparison.Ordinal);
            var bodyStart = normalized.IndexOf('\n', start) + 1;
            var next = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
            if (next < 0) next = normalized.Length;
            var body = normalized.Substring(bodyStart, next - bodyStart).Trim();
            if (body == "_Add what people and the AI Assistant should know._") body = string.Empty;
            var addition = suggestion.Markdown?.Trim();
            if (string.IsNullOrWhiteSpace(addition)) throw new InvalidOperationException("The Primer suggestion is blank.");
            var replacement = marker + "\n\n" + (body.Length == 0 ? addition : body + "\n\n" + addition) + "\n";
            return normalized.Substring(0, start) + replacement + normalized.Substring(next).TrimStart('\n') + (next == normalized.Length ? "" : "\n");
        }
    }
}
