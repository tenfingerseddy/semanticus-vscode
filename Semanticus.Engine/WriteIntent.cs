using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// The sentences about changes that the assistant's gate (<see cref="AssistantEngine"/>) and the History share: <see cref="WalkWords"/> and
    /// <see cref="DescribeWalk"/> (the rows an undo or redo crosses, which the gate asks with) and the one phrase every change reads as.
    /// The gate asks from the request, never from a rehearsal of the edit on the live model.
    /// </summary>
    internal static class WriteIntent
    {
        private const int MostListed = 4;
        private const int MostWords = 160;

        /// <summary>The sentence and the basis for a walk ("Undo to here" or "Redo to here"): the rows it crosses, their
        /// ids and the step count, at the revision it was asked about. Two walks to rows that share a label differ here
        /// by id and by length, so one approval can never be spent on the other (F1).</summary>
        public static (string Plain, string Basis) DescribeWalk(bool redo, string target, int steps, long revision,
            IReadOnlyList<(string Id, string Label, ChangeDelta[] Deltas)> rows)
        {
            var basis = string.Join("|", "walk", redo ? "redo" : "undo", target ?? "", steps.ToString(CultureInfo.InvariantCulture),
                revision.ToString(CultureInfo.InvariantCulture), string.Join(",", rows.Select(r => r.Id)));
            return ("Your assistant wants to " + WalkWords(redo, steps, rows) + ".", basis);
        }

        /// <summary>The words for an undo or redo, after "Your assistant wants to": how many changes it crosses and what each one is,
        /// newest first, the order an undo takes them back. The assistant's door asks with these, read off the history rows the walk
        /// would cross and never off a rehearsal.</summary>
        public static string WalkWords(bool redo, int steps, IReadOnlyList<(string Id, string Label, ChangeDelta[] Deltas)> rows)
        {
            var verb = redo ? "redo" : "undo";
            var phrases = new List<string>();
            foreach (var r in rows.Reverse())   // newest first: the order an undo takes them back
            {
                var first = (r.Deltas ?? Array.Empty<ChangeDelta>()).FirstOrDefault(d => d != null);
                var p = first != null ? Phrase(first) : string.IsNullOrWhiteSpace(r.Label) ? null : r.Label;
                if (p != null && !phrases.Contains(p)) phrases.Add(p);
            }
            var shown = phrases.Take(MostListed).ToList();
            var count = steps == 1 ? "one change" : steps.ToString(CultureInfo.InvariantCulture) + " changes";
            var text = verb + " " + count + " to the open model";
            if (shown.Count > 0) text += (redo ? ", bringing back: " : ", taking back: ") + string.Join("; ", shown);
            if (phrases.Count > shown.Count)
                text += string.Format(CultureInfo.InvariantCulture, "; and {0} more", phrases.Count - shown.Count);
            return text;
        }

        private static string Phrase(ChangeDelta d)
        {
            var name = Name(d.Ref);
            if (d.Kind == "update" && d.Props != null && d.Props.Length > 0 && d.Props[0] == "InPerspective")
                return d.After != null ? "add " + name + " to the perspective " + d.After : "take " + name + " out of the perspective " + d.Before;
            switch (d.Kind)
            {
                case "add": return "add " + name;
                case "remove": return "delete " + name;
                case "structure": return "change the structure of " + name;
            }
            var prop = d.Props != null && d.Props.Length > 0 ? d.Props[0] : null;
            var value = d.After == null ? "(empty)" : "\"" + Cut(d.After) + "\"";
            switch (prop)
            {
                case "Description": return "set the description of " + name + " to: " + value;
                case "Expression": return "set the formula of " + name + " to: " + value;
                case "Name": return "rename " + name + " to: " + value;
                case null: return "change " + name;
                default: return "set " + Words(prop) + " of " + name + " to: " + value;
            }
        }

        /// <summary>"measure:Sales/Total" reads "[Total]"; other kinds keep their kind so the person knows what is touched.</summary>
        private static string Name(string objRef)
        {
            if (string.IsNullOrEmpty(objRef)) return "the model";
            var sep = objRef.IndexOf(':');
            if (sep < 0) return objRef;
            var kind = objRef.Substring(0, sep);
            var rest = objRef.Substring(sep + 1);
            var slash = rest.IndexOf('/');
            var leaf = slash >= 0 ? rest.Substring(slash + 1) : rest;
            switch (kind)
            {
                case "measure": return "[" + leaf + "]";
                case "column": return slash >= 0 ? "the column " + rest.Substring(0, slash) + "[" + leaf + "]" : "the column " + leaf;
                case "table": return "the table " + rest;
                case "model": return "the model";
                default: return "the " + kind + " " + leaf;
            }
        }

        private static string Cut(string s)
        {
            var one = s.Replace("\r", " ").Replace("\n", " ");
            return one.Length <= MostWords ? one : one.Substring(0, MostWords) + "...";
        }

        // "SummarizeBy" reads "summarize by"; a plain word, not a property name.
        private static string Words(string prop)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < prop.Length; i++)
            {
                if (i > 0 && char.IsUpper(prop[i]) && !char.IsUpper(prop[i - 1])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(prop[i]));
            }
            return sb.ToString();
        }
    }
}
