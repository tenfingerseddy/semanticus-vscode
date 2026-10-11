using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>Strips Analysis Services object-markup tags (olii, ccon, and similar) from a query error so both
    /// doors show the names a person can read, not the raw tags.</summary>
    public static class DaxErrorText
    {
        static readonly Regex Markup = new Regex(@"</?[A-Za-z]{2,8}>", RegexOptions.Compiled);

        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Markup.Replace(text, "");
        }

        // "Cannot convert value '2018-06-25T00:00:00' of type Text to type Date while processing column - [StaticColumn] → Customer[JoinDate]."
        static readonly Regex Conversion = new Regex(@"Cannot convert value '[^']*' of type (?<from>\w+) to type (?<to>\w+)(?: while processing column[^\[]*(?:\[[^\]]*\]\s*(?:→|->)\s*)?(?<col>[A-Za-z0-9_' ]+\[[^\]]+\]))?", RegexOptions.Compiled);

        /// <summary>One error from the engine, said in plain words: the markup stripped, the first line only, and a refused conversion told as what it means
        /// (no internal column names, no arrow).</summary>
        public static string Sentence(string text)
        {
            var first = Plain((text ?? "").Split('\n')[0].Trim());
            var m = Conversion.Match(first);
            if (!m.Success) return first;
            var col = m.Groups["col"].Success ? m.Groups["col"].Value.Trim() : null;
            return $"The selected value could not be read as a {m.Groups["to"].Value.ToLowerInvariant()}{(col == null ? "" : " for " + col)}.";
        }
    }
}
