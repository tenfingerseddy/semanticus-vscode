using System;

namespace Semanticus.Engine
{
    /// <summary>Conservative static format-string checks shared by set_measure_format and a FormatString
    /// property write. Catches an unclosed colour/condition bracket or quote; does not pretend to be Excel.</summary>
    public static class FormatStringRules
    {
        /// <summary>A fixable reason, or null when the string is empty or structurally fine.</summary>
        public static string Problem(string formatString)
        {
            if (string.IsNullOrEmpty(formatString)) return null;
            var inQuote = false;
            var brackets = 0;
            for (var i = 0; i < formatString.Length; i++)
            {
                var c = formatString[i];
                if (inQuote)
                {
                    if (c != '"') continue;
                    if (i + 1 < formatString.Length && formatString[i + 1] == '"') { i++; continue; }
                    inQuote = false;
                    continue;
                }
                if (c == '"') { inQuote = true; continue; }
                if (c == '[') { brackets++; continue; }
                if (c == ']')
                {
                    if (brackets == 0) return "This format string has a ']' with no matching '['.";
                    brackets--;
                }
            }
            if (inQuote) return "This format string has an unclosed quote.";
            if (brackets > 0) return "This format string has an unclosed '['. Add the missing ']'.";
            return null;
        }
    }
}
