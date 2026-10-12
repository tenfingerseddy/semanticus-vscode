using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The warning for a rename that can break a report. With no report checked it is the plain sentence
        /// every rename has always given. With reports checked it also names the reports, and the report measures
        /// inside them, that use the object, so the person (or the assistant) knows what to fix. Call it BEFORE the
        /// rename, inside the model's turn: afterwards the report names no longer match.</summary>
        private string ReportRenameWarningFor(Session s, Model m, ITabularNamedObject obj)
        {
            if (!RenameTouchesReports(obj)) return null;
            var coverage = s == null ? null : CheckedCoverageFor(s);
            if (coverage == null || coverage.Parts.Count == 0) return ReportRenameWarning;

            Func<string, bool> touches;
            switch (obj)
            {
                case Table t:
                {
                    var cols = "column:" + t.Name + "/"; var msrs = "measure:" + t.Name + "/";
                    touches = r => r.StartsWith(cols, StringComparison.Ordinal) || r.StartsWith(msrs, StringComparison.Ordinal);
                    break;
                }
                case Hierarchy h:
                {
                    var levels = new HashSet<string>(h.Levels.Where(l => l.Column != null).Select(l => ObjectRefs.For(l.Column)), StringComparer.Ordinal);
                    touches = levels.Contains;
                    break;
                }
                default:
                {
                    var self = ObjectRefs.For(obj);
                    touches = r => string.Equals(r, self, StringComparison.Ordinal);
                    break;
                }
            }
            var uses = LineageGraph.ReportUsesOf(m, coverage.Parts, touches);
            if (uses.Count == 0) return ReportRenameWarning;
            string One((string Report, string[] ReportMeasures) u) => u.ReportMeasures.Length == 0 ? u.Report
                : u.Report + " (report measure " + string.Join(", ", u.ReportMeasures.Take(3)) + (u.ReportMeasures.Length > 3 ? " and " + (u.ReportMeasures.Length - 3) + " more" : "") + ")";
            var named = string.Join("; ", uses.Take(3).Select(One)) + (uses.Count > 3 ? " and " + (uses.Count - 3) + " more" : "");
            return "Names in reports are not rewritten. These checked reports use it and need the new name: " + named + ".";
        }
    }
}
