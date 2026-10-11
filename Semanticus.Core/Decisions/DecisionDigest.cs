// The evidence digest.
//
// One request is one piece of evidence, and its digest is that evidence's identity. The cache, the recorded
// oracle used by every test, and the replay gate all key on THIS value, so the rules here are load bearing:
//
//   1. Only typed values go in. A snapshot may hold strings, whole numbers, decimals, true/false, null,
//      lists of those, and nested objects of those. Anything else throws rather than being stringified,
//      because a stringified object is a digest that silently changes with a runtime format.
//   2. Object keys are sorted, so dictionary iteration order cannot move the digest.
//   3. Question order and OPTION order are preserved, because a score ladder is ordered by meaning and a
//      different option order is a different question.
//   4. Numbers are written round-trip, so 0.1 is one digest on every machine.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Decisions
{
    public static class DecisionDigest
    {
        /// <summary>The digest of a whole request: point, schema version, canonical snapshot, questions in order
        /// with their options in order.</summary>
        public static string Of(DecisionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var sb = new StringBuilder();
            sb.Append("{\"point\":");
            WriteString(sb, request.PointName);
            sb.Append(",\"schemaVersion\":").Append(request.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"snapshot\":");
            WriteValue(sb, request.Snapshot, "snapshot", 0);
            sb.Append(",\"questions\":[");
            for (var i = 0; i < request.Questions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var q = request.Questions[i];
                sb.Append("{\"name\":");
                WriteString(sb, q.Name);
                sb.Append(",\"type\":");
                WriteString(sb, q.WireType);
                sb.Append(",\"instructions\":");
                WriteString(sb, q.Instructions);
                sb.Append(",\"options\":[");
                for (var j = 0; j < q.Options.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append("{\"id\":");
                    WriteString(sb, q.Options[j].Id);
                    sb.Append(",\"criterion\":");
                    WriteString(sb, q.Options[j].Criterion);
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return Sha256Hex(sb.ToString());
        }

        /// <summary>The digest of a snapshot on its own. Used where a record wants the snapshot identity without
        /// the questions, for example a log line that must never carry the snapshot itself.</summary>
        public static string OfSnapshot(IReadOnlyDictionary<string, object> snapshot)
        {
            var sb = new StringBuilder();
            WriteValue(sb, snapshot ?? new Dictionary<string, object>(StringComparer.Ordinal), "snapshot", 0);
            return Sha256Hex(sb.ToString());
        }

        /// <summary>The canonical JSON text of a snapshot. This is exactly what goes on the wire as the state, so
        /// the thing that is digested and the thing that is sent can never drift apart.</summary>
        public static string CanonicalSnapshotJson(IReadOnlyDictionary<string, object> snapshot)
        {
            var sb = new StringBuilder();
            WriteValue(sb, snapshot ?? new Dictionary<string, object>(StringComparer.Ordinal), "snapshot", 0);
            return sb.ToString();
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private const int MaxDepth = 8;

        private static void WriteValue(StringBuilder sb, object value, string path, int depth)
        {
            if (depth > MaxDepth)
                throw new ArgumentException($"The snapshot nests deeper than {MaxDepth} levels at '{path}'. Flatten it: a snapshot is a short list of declared facts, not a model dump.");

            if (value == null) { sb.Append("null"); return; }

            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }

            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is sbyte || value is byte || value is short || value is ushort
                || value is int || value is uint || value is long)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new ArgumentException($"The snapshot holds a number that is not a number at '{path}'.");
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var map = AsMap(value);
            if (map != null)
            {
                sb.Append('{');
                var first = true;
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    WriteValue(sb, map[key], path + "." + key, depth + 1);
                }
                sb.Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                var first = true;
                var i = 0;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]", depth + 1);
                    i++;
                }
                sb.Append(']');
                return;
            }

            throw new ArgumentException(
                $"The snapshot holds a {value.GetType().Name} at '{path}'. A snapshot may only hold text, whole numbers, decimals, true or false, null, lists and nested objects of those.");
        }

        private static IDictionary<string, object> AsMap(object value)
        {
            var typed = value as IDictionary<string, object>;
            if (typed != null) return typed;

            var ro = value as IReadOnlyDictionary<string, object>;
            if (ro == null) return null;

            var copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in ro) copy[kv.Key] = kv.Value;
            return copy;
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < ' ' || c == '\u007F')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
