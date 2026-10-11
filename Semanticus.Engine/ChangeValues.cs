using System;
using System.Globalization;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Before and after values on a change delta (Plan-heart contract 8.3): what TOM's own change event carries
    /// (<c>ObjectChangedEventArgs.OldValue</c> and <c>NewValue</c>), rendered as text, capped, and redacted BEFORE it
    /// leaves the dispatcher, so neither the broadcast, the MCP fold nor the ledger file ever holds a secret.
    ///
    /// Redaction is by what the value is, never by who asked: every property of a data source object, any property
    /// whose name says Password, Credential, Secret or ConnectionString, and all Power Query (M) text. M is hidden
    /// because a string inside it can carry a secret and the write check only tests syntax (Sol 24 Sep); the person
    /// reads the real text in the Power Query editor.
    /// </summary>
    public static class ChangeValues
    {
        /// <summary>The most characters a value carries on the wire.</summary>
        public const int WireCap = 2000;
        /// <summary>The most characters a value keeps in the persisted ledger.</summary>
        public const int LedgerCap = 400;
        public const string Cut = "(cut)";
        public const string Hidden = "(hidden)";

        /// <summary>The wire values for one TOM change event.</summary>
        public static (string Before, string After) For(ITabularObject obj, string property, object oldValue, object newValue)
        {
            if (IsSecret(obj, property)) return (oldValue == null ? null : Hidden, newValue == null ? null : Hidden);
            var before = Render(oldValue);
            var after = Render(newValue);
            if (IsPowerQuery(obj, property))
                return (before == null ? null : PowerQuery(before), after == null ? null : PowerQuery(after));
            return (Cap(before, WireCap), Cap(after, WireCap));
        }

        /// <summary>"(Power Query changed, N characters)": what an M value is replaced with.</summary>
        public static string PowerQuery(string text) =>
            "(Power Query changed, " + (text?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " characters)";

        public static string Cap(string value, int max) =>
            value == null || value.Length <= max ? value : value.Substring(0, max) + Cut;

        private static bool IsSecret(ITabularObject obj, string property)
        {
            if (obj is DataSource) return true;
            if (string.IsNullOrEmpty(property)) return false;
            return property.IndexOf("Password", StringComparison.OrdinalIgnoreCase) >= 0
                || property.IndexOf("Credential", StringComparison.OrdinalIgnoreCase) >= 0
                || property.IndexOf("Secret", StringComparison.OrdinalIgnoreCase) >= 0
                || property.IndexOf("ConnectionString", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>M text: a partition's source text on any partition that is not a DAX calculated one (M, policy
        /// range, and legacy query partitions too, which is the cautious side), a shared or named M expression, and
        /// a table's incremental refresh M.</summary>
        private static bool IsPowerQuery(ITabularObject obj, string property)
        {
            switch (obj)
            {
                case Partition p:
                    return (property == "Expression" || property == "Query" || property == "MExpression")
                        && p.SourceType != PartitionSourceType.Calculated;
                case NamedExpression ne:
                    return property == "Expression" && ne.Kind == ExpressionKind.M;
                case Table _:
                    return property == "SourceExpression" || property == "PollingExpression";
            }
            return false;
        }

        /// <summary>A value as the text it is written as, uncapped and unredacted: for hashing only, never for display.</summary>
        internal static string Text(object value) => Render(value);

        private static string Render(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case ITabularNamedObject named: return named.Name;
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }
    }
}
