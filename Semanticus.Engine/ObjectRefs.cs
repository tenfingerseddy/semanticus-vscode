using System;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Stable, human/agent-readable object references used across both doors:
    ///   model:            table:&lt;Table&gt;            measure:&lt;Table&gt;/&lt;Measure&gt;            column:&lt;Table&gt;/&lt;Column&gt;
    /// (M1 supports tables, measures and columns. Names containing '/' are an accepted M1 limitation.)
    /// </summary>
    public static class ObjectRefs
    {
        public static string For(ITabularObject obj)
        {
            switch (obj)
            {
                case Model _: return "model:";   // one model per session: identity needs no mutable name
                case Measure m: return "measure:" + m.Table?.Name + "/" + m.Name;
                case Column c: return "column:" + c.Table?.Name + "/" + c.Name;
                case Table t: return "table:" + t.Name;
                case Function f: return "function:" + f.Name;
                case SingleColumnRelationship r: return "relationship:" + r.Name;
                case Hierarchy h: return "hierarchy:" + h.Table?.Name + "/" + h.Name;
                case Level lv: return "level:" + lv.Table?.Name + "/" + lv.Hierarchy?.Name + "/" + lv.Name;
                case CalculationItem ci: return "calcitem:" + ci.CalculationGroupTable?.Name + "/" + ci.Name;
                case ModelRole role: return "role:" + role.Name;
                case Perspective p: return "perspective:" + p.Name;
                case Partition pt: return "partition:" + pt.Table?.Name + "/" + pt.Name;
                default: return (obj is ITabularNamedObject n) ? "obj:" + obj.ObjectType + "/" + n.Name : "obj:" + obj.ObjectType;
            }
        }

        public static string KindOf(ITabularObject obj)
        {
            switch (obj)
            {
                case Model _: return "model";
                case Measure _: return "measure";
                case Column _: return "column";
                case Table _: return "table";
                case Function _: return "function";
                case SingleColumnRelationship _: return "relationship";
                case Hierarchy _: return "hierarchy";
                case Level _: return "level";
                case CalculationItem _: return "calcitem";   // align with For() ("calcitem:") + the tree's kind
                case ModelRole _: return "role";
                case Perspective _: return "perspective";
                case Partition _: return "partition";
                default: return obj.ObjectType.ToString();
            }
        }

        /// <summary>Resolve a ref to its wrapper, or null if not found. Must run on the dispatcher thread.</summary>
        public static ITabularNamedObject Resolve(Model model, string objRef)
        {
            if (model == null || string.IsNullOrEmpty(objRef)) return null;
            var sep = objRef.IndexOf(':');
            if (sep < 0) return null;
            var kind = objRef.Substring(0, sep);
            var rest = objRef.Substring(sep + 1);

            switch (kind)
            {
                case "model":
                    return model;   // singleton session root; ignore any legacy/display suffix

                case "table":
                    return model.Tables.Contains(rest) ? model.Tables[rest] : null;

                case "function":
                    return model.Functions.Contains(rest) ? model.Functions[rest] : null;

                case "relationship":
                    return model.Relationships.OfType<SingleColumnRelationship>().FirstOrDefault(r => r.Name == rest);

                case "role":
                    return model.Roles.Contains(rest) ? model.Roles[rest] : null;

                case "perspective":
                    return model.Perspectives.Contains(rest) ? model.Perspectives[rest] : null;

                case "measure":
                {
                    if (!Split(rest, out var t, out var n)) return null;
                    if (!model.Tables.Contains(t)) return null;
                    var table = model.Tables[t];
                    return table.Measures.Contains(n) ? table.Measures[n] : null;
                }

                case "column":
                {
                    if (!Split(rest, out var t, out var n)) return null;
                    if (!model.Tables.Contains(t)) return null;
                    var table = model.Tables[t];
                    return table.Columns.Contains(n) ? table.Columns[n] : null;
                }

                case "hierarchy":
                {
                    if (!Split(rest, out var t, out var n)) return null;
                    if (!model.Tables.Contains(t)) return null;
                    var table = model.Tables[t];
                    return table.Hierarchies.Contains(n) ? table.Hierarchies[n] : null;
                }

                case "calcitem":
                {
                    if (!Split(rest, out var t, out var n)) return null;
                    if (!model.Tables.Contains(t) || !(model.Tables[t] is CalculationGroupTable cg)) return null;
                    return cg.CalculationItems.Contains(n) ? cg.CalculationItems[n] : null;
                }

                case "partition":
                {
                    if (!Split(rest, out var t, out var n)) return null;
                    if (!model.Tables.Contains(t)) return null;
                    var table = model.Tables[t];
                    return table.Partitions.Contains(n) ? table.Partitions[n] : null;
                }

                case "level":
                {
                    // level:<Table>/<Hierarchy>/<Level> — three parts (the only 3-part ref).
                    var slash1 = rest.IndexOf('/');
                    if (slash1 < 0) return null;
                    var t = rest.Substring(0, slash1);
                    var slash2 = rest.IndexOf('/', slash1 + 1);
                    if (slash2 < 0) return null;
                    var h = rest.Substring(slash1 + 1, slash2 - slash1 - 1);
                    var n = rest.Substring(slash2 + 1);
                    if (!model.Tables.Contains(t)) return null;
                    var table = model.Tables[t];
                    if (!table.Hierarchies.Contains(h)) return null;
                    var hier = table.Hierarchies[h];
                    return hier.Levels.Contains(n) ? hier.Levels[n] : null;
                }

                default:
                    return null;
            }
        }

        private static bool Split(string rest, out string table, out string name)
        {
            var slash = rest.IndexOf('/');
            if (slash < 0) { table = null; name = null; return false; }
            table = rest.Substring(0, slash);
            name = rest.Substring(slash + 1);
            return true;
        }
    }
}
