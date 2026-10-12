using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>Who the attached assistant is, for the small sign in the Studio's driver bar. The MCP <c>initialize</c> clientInfo name is
    /// mapped to a plain name and a two-letter tile that Semanticus draws itself (no company logo). Names the assistants send are not
    /// verified: a client we do not know keeps its own name when that is plain, else the sign says "Your assistant".</summary>
    public sealed class AssistantInfo
    {
        /// <summary>claude | codex | copilot | cursor | other | unknown.</summary>
        public string Key { get; set; }
        public string Name { get; set; }
        /// <summary>Two letters for the tile; null for an assistant with no usable name (the tile draws three dots).</summary>
        public string Initials { get; set; }
        public string Version { get; set; }
    }

    public static class AssistantIdentity
    {
        private const int MaxOwnName = 28;

        public static AssistantInfo Map(string clientName, string version)
        {
            var raw = (clientName ?? "").Trim();
            var v = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            var lower = raw.ToLowerInvariant();
            // Claude Code names itself "claude-code"; Claude Desktop names itself "claude-ai". Both contain "claude", so tell them apart by "code".
            if (lower.Contains("claude")) return lower.Contains("code") ? Known("claude", "Claude Code", "CC", v) : Known("claude", "Claude", "CL", v);
            if (lower.Contains("codex")) return Known("codex", "Codex", "CX", v);
            if (lower.Contains("copilot")) return Known("copilot", "GitHub Copilot", "GC", v);
            if (lower.Contains("cursor")) return Known("cursor", "Cursor", "CU", v);
            if (IsPlain(raw))
                return new AssistantInfo { Key = "other", Name = raw, Initials = InitialsOf(raw), Version = v };
            return new AssistantInfo { Key = "unknown", Name = "Your assistant", Initials = null, Version = v };
        }

        private static AssistantInfo Known(string key, string name, string initials, string version) =>
            new AssistantInfo { Key = key, Name = name, Initials = initials, Version = version };

        /// <summary>Plain: letters, digits, spaces, dots, dashes and underscores, short, with a letter in it. Nothing that looks like a link or markup.</summary>
        private static bool IsPlain(string s) =>
            s.Length > 0 && s.Length <= MaxOwnName && s.Any(char.IsLetter)
            && s.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.');

        private static string InitialsOf(string name)
        {
            var firsts = name.Split(new[] { ' ', '-', '_', '.' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.FirstOrDefault(char.IsLetter)).Where(c => c != default(char)).ToArray();
            if (firsts.Length >= 2) return new string(new[] { firsts[0], firsts[1] }).ToUpperInvariant();
            var letters = new string(name.Where(char.IsLetter).Take(2).ToArray());
            return letters.Length == 0 ? null : letters.ToUpperInvariant();
        }
    }
}
