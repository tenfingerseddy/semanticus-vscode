// Surgical edits of another assistant's config file (connect-any, 1.5).
//
// The rule is "touch only the semanticus entry": every other byte of the file stays exactly as the person (or the
// assistant itself) wrote it. So nothing here parses the file and writes it back. It parses to FIND the span of the
// entry, then splices new bytes into that one span (or after the last member when there is no entry yet).
// A file that does not parse is refused with a plain sentence and nothing is written.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>What an edit did: the new bytes, or why it refused. Exactly one of the two is set.</summary>
    internal sealed class ConfigEditResult
    {
        public byte[] Bytes;          // the whole new file (null when Refusal is set, or when nothing needs to change)
        public bool Changed;          // false: the entry is already there, in this shape
        public string Refusal;        // plain sentence; the file is left as it was
    }

    internal static class AssistantConfigEdit
    {
        private static readonly JsonSerializerOptions StringEncoding = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        // ----------------------------------------------------------------------------------------------- JSON

        /// <summary>Set <c>&lt;serversKey&gt;.semanticus</c> in a JSON config (Claude Desktop, Cursor and Claude Code all
        /// use <c>mcpServers</c>). <paramref name="existing"/> is the file's bytes, or null for a file that does not exist yet.</summary>
        public static ConfigEditResult SetJsonServer(byte[] existing, string fileLabel, JsonObject entry, string serversKey = "mcpServers", string name = "semanticus")
        {
            if (existing == null || IsBlank(existing))
            {
                var fresh = "{\n  \"" + serversKey + "\": {\n    \"" + name + "\": " + Render(entry, "    ", "  ", "\n") + "\n  }\n}\n";
                return new ConfigEditResult { Changed = true, Bytes = Encoding.UTF8.GetBytes(fresh) };
            }

            var bomLen = existing.Length >= 3 && existing[0] == Bom[0] && existing[1] == Bom[1] && existing[2] == Bom[2] ? 3 : 0;
            var body = existing.AsSpan(bomLen);
            Layout lay;
            try { lay = Scan(body, serversKey, name); }
            catch (JsonException) { return Refuse("The " + fileLabel + " is not valid JSON, so Semanticus left it alone. Fix the file, then press Connect again."); }
            if (!lay.RootIsObject) return Refuse("The " + fileLabel + " does not hold a JSON object at the top, so Semanticus left it alone.");
            if (lay.ServersPresent && !lay.ServersIsObject)
                return Refuse("The \"" + serversKey + "\" setting in the " + fileLabel + " is not a list of servers, so Semanticus left it alone.");

            var eol = Encoding.UTF8.GetString(existing).Contains("\r\n") ? "\r\n" : "\n";
            byte[] spliced;
            if (lay.EntryStart >= 0)
            {
                var current = JsonNode.Parse(body.Slice(lay.EntryStart, lay.EntryEnd - lay.EntryStart).ToArray());
                if (JsonNode.DeepEquals(current, entry)) return new ConfigEditResult { Changed = false };
                var inner = LineIndent(body, lay.EntryMemberStart);
                spliced = Splice(existing, bomLen + lay.EntryStart, bomLen + lay.EntryEnd, Render(entry, inner, lay.Unit, eol));
            }
            else if (lay.ServersPresent && lay.ServersMemberCount > 0)
            {
                var inner = LineIndent(body, lay.ServersFirstMemberStart);
                var text = "," + eol + inner + "\"" + name + "\": " + Render(entry, inner, lay.Unit, eol);
                spliced = Splice(existing, bomLen + lay.ServersLastMemberEnd, bomLen + lay.ServersLastMemberEnd, text);
            }
            else if (lay.ServersPresent)
            {
                // "mcpServers": {} : open the braces up.
                var outer = LineIndent(body, lay.ServersStart);
                var inner = outer + lay.Unit;
                var text = eol + inner + "\"" + name + "\": " + Render(entry, inner, lay.Unit, eol) + eol + outer;
                spliced = Splice(existing, bomLen + lay.ServersStart + 1, bomLen + lay.ServersEnd - 1, text);
            }
            else if (lay.RootMemberCount > 0)
            {
                var inner = LineIndent(body, lay.RootFirstMemberStart);
                var block = "\"" + serversKey + "\": {" + eol + inner + lay.Unit + "\"" + name + "\": " + Render(entry, inner + lay.Unit, lay.Unit, eol) + eol + inner + "}";
                spliced = Splice(existing, bomLen + lay.RootLastMemberEnd, bomLen + lay.RootLastMemberEnd, "," + eol + inner + block);
            }
            else
            {
                // {} : open the braces up.
                var inner = lay.Unit;
                var block = "\"" + serversKey + "\": {" + eol + inner + lay.Unit + "\"" + name + "\": " + Render(entry, inner + lay.Unit, lay.Unit, eol) + eol + inner + "}";
                spliced = Splice(existing, bomLen + lay.RootStart + 1, bomLen + lay.RootEnd - 1, eol + inner + block + eol);
            }

            // Belt and braces: the result must still parse, and it must hold exactly our entry. Anything else is a bug here, not the file.
            try
            {
                var check = JsonNode.Parse(spliced.AsSpan(bomLen).ToArray(), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (!JsonNode.DeepEquals(check?[serversKey]?[name], entry)) return Refuse("Semanticus could not edit the " + fileLabel + " safely, so it left the file alone.");
            }
            catch (Exception) { return Refuse("Semanticus could not edit the " + fileLabel + " safely, so it left the file alone."); }
            return new ConfigEditResult { Changed = true, Bytes = spliced };
        }

        /// <summary>The current <c>semanticus</c> entry in a JSON config, or null when there is none or the file does not parse.</summary>
        public static JsonNode ReadJsonServer(byte[] existing, string serversKey = "mcpServers", string name = "semanticus")
        {
            if (existing == null || IsBlank(existing)) return null;
            try
            {
                var bomLen = existing.Length >= 3 && existing[0] == Bom[0] && existing[1] == Bom[1] && existing[2] == Bom[2] ? 3 : 0;
                var root = JsonNode.Parse(existing.AsSpan(bomLen).ToArray(), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                return (root as JsonObject)?[serversKey] is JsonObject servers && servers.TryGetPropertyValue(name, out var v) ? v : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>True when the file is JSON that parses (or is empty). Used to say "not valid JSON" before anything else.</summary>
        public static bool IsBlank(byte[] b)
        {
            foreach (var c in b)
                if (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\r' && c != (byte)'\n' && c != 0xEF && c != 0xBB && c != 0xBF) return false;
            return true;
        }

        private static ConfigEditResult Refuse(string why) => new ConfigEditResult { Refusal = why };

        private static byte[] Splice(byte[] src, int from, int to, string text)
        {
            var mid = Encoding.UTF8.GetBytes(text);
            var outp = new byte[from + mid.Length + (src.Length - to)];
            Buffer.BlockCopy(src, 0, outp, 0, from);
            Buffer.BlockCopy(mid, 0, outp, from, mid.Length);
            Buffer.BlockCopy(src, to, outp, from + mid.Length, src.Length - to);
            return outp;
        }

        /// <summary>The whitespace at the start of the line that holds <paramref name="at"/>, but only when nothing else is
        /// before it on that line (a one-line file has no indent to copy).</summary>
        private static string LineIndent(ReadOnlySpan<byte> body, int at)
        {
            var i = at;
            while (i > 0 && body[i - 1] != (byte)'\n') i--;
            var j = i;
            while (j < at && (body[j] == (byte)' ' || body[j] == (byte)'\t')) j++;
            return j == at ? Encoding.UTF8.GetString(body.Slice(i, j - i)) : "  ";
        }

        private sealed class Layout
        {
            public bool RootIsObject;
            public int RootStart, RootEnd, RootMemberCount, RootFirstMemberStart, RootLastMemberEnd;
            public bool ServersPresent, ServersIsObject;
            public int ServersStart, ServersEnd, ServersMemberCount, ServersFirstMemberStart, ServersLastMemberEnd;
            public int EntryStart = -1, EntryEnd = -1, EntryMemberStart;
            public string Unit = "  ";
        }

        /// <summary>Walk the document once and record the byte spans the edit needs. Comments are skipped; a trailing comma
        /// is NOT allowed (it throws), because an insert after the last member would then be wrong.</summary>
        private static Layout Scan(ReadOnlySpan<byte> body, string serversKey, string name)
        {
            var lay = new Layout();
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read()) throw new JsonException("empty");
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                if (reader.Read()) throw new JsonException("trailing content");
                return lay;
            }
            lay.RootIsObject = true;
            lay.RootStart = (int)reader.TokenStartIndex;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var memberStart = (int)reader.TokenStartIndex;
                var key = reader.GetString();
                reader.Read();
                var valueStart = (int)reader.TokenStartIndex;
                var type = reader.TokenType;
                if (type == JsonTokenType.StartObject || type == JsonTokenType.StartArray) reader.Skip();
                var valueEnd = (int)reader.BytesConsumed;
                if (lay.RootMemberCount == 0) { lay.RootFirstMemberStart = memberStart; lay.Unit = GuessUnit(body, memberStart); }
                lay.RootMemberCount++;
                lay.RootLastMemberEnd = valueEnd;
                if (key == serversKey && !lay.ServersPresent)
                {
                    lay.ServersPresent = true;
                    lay.ServersStart = valueStart;
                    lay.ServersEnd = valueEnd;
                    lay.ServersIsObject = type == JsonTokenType.StartObject;
                    if (lay.ServersIsObject) ScanServers(body.Slice(valueStart, valueEnd - valueStart), valueStart, name, lay);
                }
            }
            lay.RootEnd = (int)reader.BytesConsumed;
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("bad object");
            if (reader.Read()) throw new JsonException("trailing content");
            return lay;
        }

        private static void ScanServers(ReadOnlySpan<byte> slice, int offset, string name, Layout lay)
        {
            var reader = new Utf8JsonReader(slice, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            reader.Read();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var memberStart = offset + (int)reader.TokenStartIndex;
                var key = reader.GetString();
                reader.Read();
                var valueStart = offset + (int)reader.TokenStartIndex;
                if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) reader.Skip();
                var valueEnd = offset + (int)reader.BytesConsumed;
                if (lay.ServersMemberCount == 0) lay.ServersFirstMemberStart = memberStart;
                lay.ServersMemberCount++;
                lay.ServersLastMemberEnd = valueEnd;
                if (key == name && lay.EntryStart < 0) { lay.EntryStart = valueStart; lay.EntryEnd = valueEnd; lay.EntryMemberStart = memberStart; }
            }
        }

        /// <summary>One indent step, copied from the file: the first member's own indent when it is tabs or spaces on its own line.</summary>
        private static string GuessUnit(ReadOnlySpan<byte> body, int firstMemberStart)
        {
            var indent = LineIndent(body, firstMemberStart);
            return indent.Length > 0 && indent.All(c => c == ' ' || c == '\t') && (indent[0] == '\t' || indent.Length <= 8) ? indent : "  ";
        }

        // Render a small JSON value the way a person's editor would lay it out: one member per line.
        private static string Render(JsonNode node, string indent, string unit, string eol)
        {
            switch (node)
            {
                case null: return "null";
                case JsonObject o:
                    if (o.Count == 0) return "{}";
                    return "{" + eol + string.Join("," + eol, o.Select(kv =>
                        indent + unit + JsonSerializer.Serialize(kv.Key, StringEncoding) + ": " + Render(kv.Value, indent + unit, unit, eol)))
                        + eol + indent + "}";
                case JsonArray a:
                    if (a.Count == 0) return "[]";
                    return "[" + eol + string.Join("," + eol, a.Select(v => indent + unit + Render(v, indent + unit, unit, eol))) + eol + indent + "]";
                default:
                    return node.ToJsonString(StringEncoding);
            }
        }

        // ----------------------------------------------------------------------------------------------- TOML

        private static readonly Regex OurHeader = new Regex(@"^[ \t]*\[[ \t]*mcp_servers[ \t]*\.[ \t]*(?:semanticus|""semanticus""|'semanticus')[ \t]*\][ \t]*(?:#.*)?$", RegexOptions.Compiled);
        private static readonly Regex AnyHeader = new Regex(@"^[ \t]*\[", RegexOptions.Compiled);
        private static readonly Regex McpTableHeader = new Regex(@"^[ \t]*\[[ \t]*mcp_servers[ \t]*\][ \t]*(?:#.*)?$", RegexOptions.Compiled);
        private static readonly Regex OtherShapes = new Regex(@"^[ \t]*mcp_servers[ \t]*(?:=|\.[ \t]*(?:semanticus|""semanticus""|'semanticus')\b)", RegexOptions.Compiled);
        private static readonly Regex KeyLine = new Regex(@"^[ \t]*(?<k>[A-Za-z0-9_\-]+)[ \t]*=", RegexOptions.Compiled);

        /// <summary>Set <c>[mcp_servers.semanticus]</c> in Codex's config.toml. Keys the person added to that table stay.</summary>
        public static ConfigEditResult SetCodexServer(string existing, string command, string[] args, int startupSeconds)
        {
            var bom = existing != null && existing.Length > 0 && existing[0] == '﻿';
            var text = bom ? existing.Substring(1) : (existing ?? string.Empty);
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = Regex.Split(text, "\r?\n").ToList();
            var ours = new Dictionary<string, string>
            {
                ["command"] = TomlString(command),
                ["args"] = "[" + string.Join(", ", args.Select(TomlString)) + "]",
                ["startup_timeout_sec"] = startupSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            // Walk the lines once, remembering which are inside a multi-line string so a "[" there is not read as a header.
            var inString = new bool[lines.Count];
            string open = null;
            for (var i = 0; i < lines.Count; i++)
            {
                inString[i] = open != null;
                foreach (var q in new[] { "\"\"\"", "'''" })
                {
                    var n = Regex.Matches(lines[i], Regex.Escape(q)).Count;
                    if (open == null && n % 2 == 1) open = q;
                    else if (open == q && n % 2 == 1) open = null;
                }
            }

            int headerAt = -1, tableAt = -1, otherShape = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (inString[i]) continue;
                if (OurHeader.IsMatch(lines[i])) { headerAt = i; break; }
                if (McpTableHeader.IsMatch(lines[i])) tableAt = i;
                if (OtherShapes.IsMatch(lines[i])) otherShape = i;
            }
            if (headerAt < 0 && otherShape >= 0)
                return Refuse("Codex already has a semanticus entry written in a form Semanticus does not edit. Change that line by hand or remove it, then press Connect again.");
            if (headerAt < 0 && tableAt >= 0)
            {
                // [mcp_servers] followed by "semanticus = { ... }" lines.
                for (var i = tableAt + 1; i < lines.Count && !(AnyHeader.IsMatch(lines[i]) && !inString[i]); i++)
                    if (!inString[i] && Regex.IsMatch(lines[i], @"^[ \t]*(?:semanticus|""semanticus""|'semanticus')[ \t]*="))
                        return Refuse("Codex already has a semanticus entry written in a form Semanticus does not edit. Change that line by hand or remove it, then press Connect again.");
            }

            string result;
            if (headerAt < 0)
            {
                var section = "[mcp_servers.semanticus]" + eol + string.Join(eol, ours.Select(kv => kv.Key + " = " + kv.Value)) + eol;
                var head = text;
                if (head.Length > 0 && !head.EndsWith("\n")) head += eol;
                if (head.Length > 0 && !head.EndsWith(eol + eol) && head.Trim().Length > 0) head += eol;
                result = head + section;
            }
            else
            {
                var end = headerAt + 1;
                while (end < lines.Count && !(AnyHeader.IsMatch(lines[end]) && !inString[end])) end++;
                var body = lines.GetRange(headerAt + 1, end - headerAt - 1);
                var done = new HashSet<string>();
                var rebuilt = new List<string>();
                for (var i = 0; i < body.Count; i++)
                {
                    var m = KeyLine.Match(body[i]);
                    if (!m.Success || !ours.ContainsKey(m.Groups["k"].Value)) { rebuilt.Add(body[i]); continue; }
                    var key = m.Groups["k"].Value;
                    // A value can run over several lines (an array written one item per line): skip to its closing bracket.
                    var span = 0;
                    if (key == "args") { var depth = 0; var j = i; do { depth += body[j].Count(c => c == '[') - body[j].Count(c => c == ']'); j++; } while (depth > 0 && j < body.Count); span = j - i - 1; }
                    if (done.Add(key)) rebuilt.Add(key + " = " + ours[key]);
                    i += span;
                }
                // Missing keys go after the last line that holds something.
                var last = rebuilt.FindLastIndex(l => l.Trim().Length > 0);
                var at = last + 1;
                foreach (var kv in ours)
                    if (!done.Contains(kv.Key)) rebuilt.Insert(at++, kv.Key + " = " + kv.Value);
                var all = new List<string>(lines.GetRange(0, headerAt + 1));
                all.AddRange(rebuilt);
                all.AddRange(lines.GetRange(end, lines.Count - end));
                result = string.Join(eol, all);
            }
            if (result == text) return new ConfigEditResult { Changed = false };
            return new ConfigEditResult { Changed = true, Bytes = Encoding.UTF8.GetBytes((bom ? "﻿" : "") + result) };
        }

        /// <summary>True when Codex's config already has the table (any content).</summary>
        public static bool CodexHasServer(string existing) => existing != null && existing.Split('\n').Any(l => OurHeader.IsMatch(l.TrimEnd('\r')));

        /// <summary>The command and args of the table, read loosely, or (null, null).</summary>
        public static (string command, string[] args) ReadCodexServer(string existing)
        {
            if (existing == null) return (null, null);
            var lines = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var h = lines.FindIndex(l => OurHeader.IsMatch(l));
            if (h < 0) return (null, null);
            string command = null; string argsText = null;
            for (var i = h + 1; i < lines.Count && !AnyHeader.IsMatch(lines[i]); i++)
            {
                var m = KeyLine.Match(lines[i]);
                if (!m.Success) continue;
                var rest = lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
                if (m.Groups["k"].Value == "command") command = TomlUnquote(rest);
                else if (m.Groups["k"].Value == "args")
                {
                    var sb = new StringBuilder(rest);
                    while (sb.ToString().Count(c => c == '[') > sb.ToString().Count(c => c == ']') && ++i < lines.Count) sb.Append(' ').Append(lines[i].Trim());
                    argsText = sb.ToString();
                }
            }
            string[] args = null;
            if (argsText != null)
                args = Regex.Matches(argsText, @"""((?:[^""\\]|\\.)*)""|'([^']*)'").Select(m => m.Groups[1].Success && m.Value.StartsWith("\"") ? TomlUnescape(m.Groups[1].Value) : m.Groups[2].Value).ToArray();
            return (command, args);
        }

        private static string TomlUnquote(string v)
        {
            v = Regex.Replace(v, @"\s+#.*$", "").Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return TomlUnescape(v.Substring(1, v.Length - 2));
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v.Substring(1, v.Length - 2);
            return v;
        }

        private static string TomlUnescape(string s) => s.Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\u0001", "\\");

        private static string TomlString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
