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
    }
}
