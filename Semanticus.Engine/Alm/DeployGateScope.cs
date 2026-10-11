using System;
using System.Collections.Generic;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// Which findings belong to the change being published. Older findings on other objects stay
    /// visible as a count; they do not block.
    /// </summary>
    internal static class DeployGateScope
    {
        internal static bool IsWholeModelSession(string sourcePath, object liveOrigin) =>
            string.IsNullOrEmpty(sourcePath) && liveOrigin == null;

        /// <summary>No session edits, or a model-level edit (rule load, annotation): score the whole model.
        /// A non-empty object change set is what D-005 scopes.</summary>
        internal static bool ForcesWholeModel(IReadOnlyCollection<string> changed)
        {
            if (changed == null || changed.Count == 0) return true;
            foreach (var c in changed)
                if (IsModelLevel(c)) return true;
            return false;
        }

        /// <summary>Why the gate judged the whole model, as the one sentence the check line opens with; null when it
        /// scored a change set. Same three cases as the wholeModel flag the gate computes.</summary>
        internal static string WholeModelReason(string sourcePath, object liveOrigin, IReadOnlyCollection<string> changed)
        {
            if (IsWholeModelSession(sourcePath, liveOrigin))
                return "This model has no saved or published copy yet, so the whole model was checked.";
            if (changed == null || changed.Count == 0)
                return "No edits in this session yet, so the whole model was checked.";
            return ForcesWholeModel(changed) ? "An edit in this session changed the whole model, so the whole model was checked." : null;
        }

        internal static bool IsModelLevel(string objectRef)
        {
            if (string.IsNullOrEmpty(objectRef)) return true;
            if (objectRef.Equals("model:", StringComparison.OrdinalIgnoreCase)) return true;
            if (objectRef.StartsWith("model:", StringComparison.OrdinalIgnoreCase)) return true;
            if (objectRef.StartsWith("obj:Model", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool Touches(string objectRef, IReadOnlyCollection<string> changed, bool wholeModel)
        {
            if (wholeModel) return true;
            if (changed == null || changed.Count == 0) return false;
            if (IsModelLevel(objectRef))
            {
                foreach (var c in changed)
                    if (c != null && (c.Equals("model:", StringComparison.OrdinalIgnoreCase)
                        || c.StartsWith("model:", StringComparison.OrdinalIgnoreCase)))
                        return true;
                return false;
            }
            foreach (var c in changed)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (string.Equals(c, objectRef, StringComparison.Ordinal)) return true;
                if (IsParentRef(c, objectRef)) return true;
            }
            return false;
        }

        internal static bool UnknownTouches(string scope, IReadOnlyCollection<string> changed, bool wholeModel)
        {
            if (wholeModel) return true;
            if (changed == null || changed.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(scope)) return true;
            foreach (var raw in scope.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                var kind = KindFromScope(token);
                foreach (var c in changed)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    if (c.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        internal static bool ReadinessGateTouches(string gatedBy, IReadOnlyList<ReadinessFinding> findings,
            IReadOnlyCollection<string> changed, bool wholeModel)
        {
            if (wholeModel) return true;
            if (string.IsNullOrEmpty(gatedBy)) return false;
            var ruleId = RuleIdForGate(gatedBy);
            if (ruleId == null) return false;
            if (findings == null) return false;
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                if (f == null || f.IsAccepted()) continue;
                if (!string.Equals(f.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)) continue;
                if (Touches(f.ObjectRef, changed, wholeModel)) return true;
            }
            return false;
        }

        private static string RuleIdForGate(string gatedBy)
        {
            if (gatedBy.IndexOf("undescribed", StringComparison.OrdinalIgnoreCase) >= 0) return "DESC-MEASURE";
            if (gatedBy.IndexOf("scale ceiling", StringComparison.OrdinalIgnoreCase) >= 0) return "LIMIT-SCALE";
            return null;
        }

        private static string KindFromScope(string token)
        {
            if (token.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0) return "column";
            if (token.IndexOf("Table", StringComparison.OrdinalIgnoreCase) >= 0) return "table";
            return token.ToLowerInvariant();
        }

        private static bool IsParentRef(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
            if (!parent.StartsWith("table:", StringComparison.OrdinalIgnoreCase)) return false;
            var name = parent.Substring("table:".Length);
            if (name.Length == 0) return false;
            var sep = child.IndexOf(':');
            if (sep < 0 || sep == child.Length - 1) return false;
            var rest = child.Substring(sep + 1);
            return rest.StartsWith(name + "/", StringComparison.Ordinal);
        }
    }
}
