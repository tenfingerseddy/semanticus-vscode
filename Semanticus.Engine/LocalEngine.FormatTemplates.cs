using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Format-string template library (docs scratchpad format-string-template-library.md). The curated catalog is
    /// an engine-EMBEDDED JSON resource — the single source of truth served identically to the webview picker (RPC)
    /// and the user's Claude (MCP), so both author correct, idiomatic NUMBER format strings instead of guessing
    /// glyphs / scaling commas. Colour is report-side and out of scope. Two ops:
    ///   • list_format_templates — read the catalog (offline, no session; category/search filter).
    ///   • set_measure_format_expression — write a measure's DYNAMIC format string ("Format = Dynamic"), the gap
    ///     that set_measure_format is static-only, so the dynamic templates land on a plain measure not just calc items.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private static FormatTemplateInfo[] _formatTemplates;   // parse-once cache (immutable static data)
        private static readonly object _formatTemplatesGate = new object();

        /// <summary>Load + cache the embedded catalog. Fail-loud if the resource is missing (a packaging bug), never
        /// silently return an empty library.</summary>
        private static FormatTemplateInfo[] FormatTemplates()
        {
            if (_formatTemplates != null) return _formatTemplates;
            lock (_formatTemplatesGate)
            {
                if (_formatTemplates != null) return _formatTemplates;
                var asm = typeof(LocalEngine).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("format-templates.json", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("format-templates.json is not embedded in Semanticus.Engine: the format-template catalog is missing from the build.");
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var json = reader.ReadToEnd();
                var doc = JsonSerializer.Deserialize<Catalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _formatTemplates = doc?.Templates ?? Array.Empty<FormatTemplateInfo>();
                return _formatTemplates;
            }
        }

        private sealed class Catalog { public FormatTemplateInfo[] Templates { get; set; } }

        /// <summary>The curated format-string catalog, optionally narrowed. <paramref name="category"/> matches a
        /// category (equals or contains, case-insensitive); <paramref name="search"/> is fuzzy over id/name/category/
        /// format-string/description/note. Read-only, no open model needed — the agent can pick a correct string
        /// before a model is even open. Catalog order is preserved (categories stay grouped).</summary>
        public Task<FormatTemplateInfo[]> ListFormatTemplatesAsync(string category, string search)
        {
            IEnumerable<FormatTemplateInfo> q = FormatTemplates();
            var cat = category?.Trim();
            if (!string.IsNullOrEmpty(cat))
                q = q.Where(t => t.Category != null && (
                    t.Category.Equals(cat, StringComparison.OrdinalIgnoreCase) ||
                    t.Category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) >= 0));
            var s = search?.Trim();
            if (!string.IsNullOrEmpty(s))
                q = q.Where(t => Has(t.Id, s) || Has(t.Name, s) || Has(t.Category, s)
                    || Has(t.FormatString, s) || Has(t.Description, s) || Has(t.Note, s));
            return Task.FromResult(q.ToArray());

            static bool Has(string hay, string needle) =>
                hay != null && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Set (or clear) a measure's DYNAMIC format-string expression — the DAX that returns a format string,
        /// evaluated per filter context (Power BI's "Format = Dynamic"). This is what makes the auto-scale / change-
        /// indicator / duration / bps DYNAMIC templates work on a plain measure, not just calculation items. Empty
        /// clears it (the measure falls back to its static format / model format). A non-blank expression AUTO-CLEARS
        /// any static FormatString (Analysis Services forbids both at once). Needs compatibility level 1601+. Undoable.</summary>
        public async Task<SetResult> SetMeasureFormatExpressionAsync(string objRef, string formatExpression, string origin, string expectedFormatExpression = null, string expectedIdentity = null)
        {
            var s = _sessions.Require();
            var changed = false;
            long rev;
            try
            {
                rev = await s.MutateAsync(origin, "set measure format expression", m =>
                {
                    FenceModelIdentity(s, expectedIdentity);
                    changed = ApplyMeasureFormatExpression(m, objRef, formatExpression, expectedFormatExpression);
                    if (!changed) throw new NoopMutationException();
                });
            }
            catch (NoopMutationException) { return Noop(s); }
            return new SetResult { Revision = rev, Changed = changed };
        }
    }
}
