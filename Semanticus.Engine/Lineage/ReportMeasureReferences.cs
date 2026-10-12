using System;
using System.Collections.Generic;
using Semanticus.Analysis;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// The model names a report measure's DAX points at. A report measure (reportExtensions.json) is DAX the report
    /// owns, written against the model, so a rename or a delete in the model can break it. The reader is the engine's
    /// own DAX tokenizer (<see cref="DaxScan"/>, the one DaxValidator reads formulas with), so a name inside a string
    /// or a comment is never read, and ]] and '' inside a name are unescaped. It names what the formula says and judges
    /// nothing: LineageGraph resolves each name against the open model.
    /// </summary>
    internal static class ReportMeasureReferences
    {
        /// <summary>One name in the formula. <see cref="Table"/> is null for a bare [Name].</summary>
        public sealed class NameRef
        {
            public string Table { get; set; }
            public string Name { get; set; }
            /// <summary>The formula also writes this name as a string ("Net"), so it may be a column the formula makes
            /// itself. It still counts as a use when the model has it; when the model has not, it is not a gap.</summary>
            public bool MaybeOwn { get; set; }
        }

        // Words that can stand before a bracketed name without being a table (as DaxValidator reads them).
        private static readonly HashSet<string> NotATable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "RETURN", "NOT", "IN", "VAR", "ASC", "DESC", "AND", "OR" };

        public static List<NameRef> Read(string dax)
        {
            var found = new List<NameRef>();
            if (string.IsNullOrWhiteSpace(dax)) return found;
            var toks = DaxScan.Tokenize(dax);

            // A name the formula makes itself (the "Net" of ADDCOLUMNS ( t, "Net", ... )) may not be a model object.
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in toks)
                if (t.Kind == DaxScan.Kind.String && !string.IsNullOrEmpty(t.Inner)) own.Add(t.Inner);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string table, string name)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (seen.Add((table ?? "") + "\u0001" + name))
                    found.Add(new NameRef { Table = table, Name = name, MaybeOwn = table == null && own.Contains(name) });
            }

            for (var k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                var next = k + 1 < toks.Count ? toks[k + 1] : null;
                var nextIsName = next != null && next.Kind == DaxScan.Kind.Name && next.Delim == '[';

                // 'Table'[Name] (a space is allowed) or Table[Name] (it must touch).
                if (t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && nextIsName) { Add(t.Inner, next.Inner); k++; continue; }
                if (t.Kind == DaxScan.Kind.Word && !NotATable.Contains(t.Text) && nextIsName && next.Start == t.End) { Add(t.Text, next.Inner); k++; continue; }

                if (t.Kind == DaxScan.Kind.Name && t.Delim == '[')
                {
                    // [Date].[Year]: a level of a date variation, not a column of its own.
                    if (k > 0 && toks[k - 1].Kind == DaxScan.Kind.Other && toks[k - 1].Text == ".") continue;
                    Add(null, t.Inner);
                }
            }
            return found;
        }
    }
}
