using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// The sentence a person is asked about, read off the REQUEST: the method the assistant called and the arguments it
    /// passed, never off a rehearsal of the edit on the live model. A registry entry carries a template such as
    /// <c>set the description of {objRef} to: {text}</c>; each <c>{name}</c> is filled from the argument of that name, an
    /// object reference read as its plain name ("[Total]", "the table Sales"), any other text quoted and cut, a list joined.
    /// <c>{name:raw}</c> leaves text unquoted. The same arguments, whole and uncut, are what the grant is bound to
    /// (<see cref="AssistantEngine"/>), so what the person reads and what is approved never come apart.
    /// </summary>
    internal static class GateWords
    {
        public const int MostListed = 4;
        public const int MostWords = 160;

        public static string Sentence(string template, ParameterInfo[] ps, object[] args)
        {
            var sb = new StringBuilder("Your assistant wants to ");
            sb.Append(Fill(template, ps, args));
            sb.Append('.');
            return sb.ToString();
        }

        /// <summary>The question a person reads for a held call: "Your assistant wants to {words}." and then the model it is about. The queue is read
        /// away from the model tree, and a person may have more than one model open over a day, so a question that names only an object ("the model",
        /// "table Sales") does not say where the change would land (UAT-J7-S3-001 to 007). No name known means no sentence, never a guessed one.</summary>
        public static string Question(string words, string model) => WithModel("Your assistant wants to " + (words ?? "") + ".", model);

        /// <summary>A question sentence that already stands, with the model it is about added after it.</summary>
        public static string WithModel(string sentence, string model)
        {
            sentence = (sentence ?? "").TrimEnd();
            if (sentence.EndsWith("..", StringComparison.Ordinal) && !sentence.EndsWith("...", StringComparison.Ordinal)) sentence = sentence.Substring(0, sentence.Length - 1);   // a value that ends in a full stop, then the sentence's own
            if (string.IsNullOrWhiteSpace(model)) return sentence;
            var marker = "the model \"" + model.Trim() + "\"";
            if (sentence.Contains(marker, StringComparison.Ordinal)) return sentence;
            return sentence + " This is about the model \"" + model.Trim() + "\".";
        }

        public static string Fill(string template, ParameterInfo[] ps, object[] args)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c != '{') { sb.Append(c); continue; }
                var end = template.IndexOf('}', i);
                if (end < 0) { sb.Append(template, i, template.Length - i); break; }
                var token = template.Substring(i + 1, end - i - 1);
                i = end;
                // A choice: "{add?add|remove}" reads the first word when the true/false parameter "add" is true and the second when it is false, so the person reads "add ... to"
                // or "remove ... from" and never the template (join 8, K16).
                var q = token.IndexOf('?');
                if (q > 0)
                {
                    var choice = token.Substring(q + 1).Split('|');
                    var flag = Array.FindIndex(ps, p => string.Equals(p.Name, token.Substring(0, q), StringComparison.Ordinal));
                    if (flag >= 0 && choice.Length == 2 && args[flag] is bool on) { sb.Append(on ? choice[0] : choice[1]); continue; }
                }
                var raw = false;
                var colon = token.IndexOf(':');
                if (colon >= 0) { raw = token.Substring(colon + 1) == "raw"; token = token.Substring(0, colon); }
                var idx = Array.FindIndex(ps, p => string.Equals(p.Name, token, StringComparison.Ordinal));
                sb.Append(idx < 0 ? "{" + token + "}" : Value(ps[idx].Name, args[idx], raw));
            }
            return sb.ToString();
        }

        /// <summary>A method name as plain words: "SaveWorkflowTemplateAsync" reads "save workflow template".</summary>
        public static string Humanize(string method)
        {
            if (string.IsNullOrEmpty(method)) return "a change";
            if (method.EndsWith("Async", StringComparison.Ordinal)) method = method.Substring(0, method.Length - 5);
            var sb = new StringBuilder();
            foreach (var c in method)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>One argument as the person reads it.</summary>
        public static string Value(string name, object value, bool raw = false)
        {
            if (value == null) return "(empty)";
            var isRef = IsRefName(name);
            switch (value)
            {
                case string s:
                    return isRef ? NameOf(s) : raw ? Cut(s) : "\"" + Cut(s) + "\"";
                case bool b:
                    return b ? "on" : "off";
                case IFormattable f when value is int || value is long || value is double || value is decimal || value is float:
                    return f.ToString(null, CultureInfo.InvariantCulture);
                case Enum e:
                    return e.ToString();
            }
            if (value is IEnumerable list && value is not IDictionary)
            {
                var items = new List<string>();
                foreach (var item in list) items.Add(item is string t ? (isRef ? NameOf(t) : raw ? Cut(t) : "\"" + Cut(t) + "\"") : Value(name, item, raw));
                if (items.Count == 0) return "(none)";
                var shown = items.Take(MostListed).ToList();
                var text = string.Join(", ", shown);
                if (items.Count > shown.Count)
                    text += string.Format(CultureInfo.InvariantCulture, ", and {0} more", items.Count - shown.Count);
                return text;
            }
            try { return Cut(JsonSerializer.Serialize(value)); } catch { return value.ToString(); }
        }

        public static bool IsRefName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            return n.EndsWith("ref") || n.EndsWith("refs") || n == "refs" || n == "parentref";
        }

        /// <summary>"measure:Sales/Total" reads "[Total]"; other kinds keep their kind so the person knows what is touched.</summary>
        public static string NameOf(string objRef)
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

        public static string Cut(string s)
        {
            if (s == null) return "";
            var one = s.Replace("\r", " ").Replace("\n", " ");
            return one.Length <= MostWords ? one : one.Substring(0, MostWords) + "... (" + one.Length.ToString(CultureInfo.InvariantCulture) + " characters in all)";
        }
    }
}
