using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private static KeyIntegrityCounts KeyCounts(ResultSet result)
    {
        if (result == null || !string.IsNullOrEmpty(result.Error) || result.Truncated || result.Rows?.Length != 1)
            return new KeyIntegrityCounts { Error = !string.IsNullOrEmpty(result?.Error) ? result.Error : "The count query did not return a complete result." };
        int Index(string name) => Array.FindIndex(result.Columns, c =>
            string.Equals(c.Name?.Trim('[', ']'), name, StringComparison.OrdinalIgnoreCase));
        var unmatched = Index("UnmatchedRows");
        var duplicates = Index("DuplicateRows");
        var row = result.Rows[0];
        if (unmatched < 0 || duplicates < 0 || row.Length <= Math.Max(unmatched, duplicates))
            return new KeyIntegrityCounts { Error = "The count query did not return both key counts." };
        try
        {
            if (row[unmatched] == null || row[duplicates] == null) throw new FormatException();
            var u = Convert.ToInt64(row[unmatched], CultureInfo.InvariantCulture);
            var d = Convert.ToInt64(row[duplicates], CultureInfo.InvariantCulture);
            if (u < 0 || d < 0) throw new FormatException();
            return new KeyIntegrityCounts { UnmatchedRows = u, DuplicateRows = d };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { return new KeyIntegrityCounts { Error = "The count query returned an unreadable count." }; }
    }

    private async Task<FixPrompt> RatioFixPromptAsync(string objRef)
    {
        var context = _sessions.CurrentContext;
        var session = context.RequireSession();
        var snapshot = await session.ReadAsync(model =>
        {
            if (ObjectRefs.Resolve(model, objRef) is not Measure measure || !WrongNumberReadiness.AverageOfRatio(measure))
                throw new InvalidOperationException("This measure no longer averages row percentages. Read its current formula before choosing a change.");
            var expression = WrongNumberReadiness.RatioOfTotals(measure);
            var offer = new RatioFixOffer
            {
                SessionId = session.Id, Revision = session.Revision,
                OriginalExpression = measure.Expression, Expression = expression,
            };
            // Compare a few groups and the grand total. This is a choice of meaning, never an equivalence proof.
            // Only a directly related dimension is useful here. Prefer Category, then a visible text label.
            var labels = model.Relationships.OfType<SingleColumnRelationship>()
                .Where(r => r.IsActive && r.FromTable == measure.Table && r.ToCardinality == RelationshipEndCardinality.One)
                .SelectMany(r => r.ToTable.Columns)
                .Where(c => !c.IsHidden && c.DataType == DataType.String && c.Type != ColumnType.RowNumber)
                .OrderBy(c => string.Equals(c.Name, "Category", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.Ordinal).ToArray();
            var before = "(" + measure.Expression + Environment.NewLine + ")";
            string query = null;
            if (expression != null)
            {
                var total = $"ROW(\"Scope\", \"All data\", \"Before\", {before}, \"After\", {expression})";
                // Unique aliases cannot bind to a model measure with the same name.
                var prefix = "__r1_" + Guid.NewGuid().ToString("N");
                var b = prefix + "b";
                var a = prefix + "a";
                if (labels.Length > 0)
                {
                    var column = WrongNumberReadiness.ColumnDax(labels[0]);
                    offer.GroupedBy = column;
                    var groups = $"SELECTCOLUMNS(TOPN(12, SUMMARIZECOLUMNS({column}, \"{b}\", {before}, \"{a}\", {expression}), {column}, ASC), \"Scope\", {column}, \"Before\", [{b}], \"After\", [{a}])";
                    query = "EVALUATE UNION(" + total + ", " + groups + ")";
                }
                else query = "EVALUATE " + total;
            }
            return (offer, query);
        });
        var result = snapshot.offer;
        if (result.Expression == null)
            result.Problem = "This formula needs a candidate written by your assistant. Compare the current and candidate numbers before choosing.";
        else if (context.Live == null)
            result.Problem = "Connect to the model to compare the numbers before choosing.";
        else
        {
            try
            {
                var rows = await context.Live.ExecuteAsync(snapshot.query, 13, 60);
                if (!string.IsNullOrEmpty(rows.Error) || rows.Truncated || rows.Rows.Length == 0)
                    result.Problem = "The numbers could not be compared. " + (rows.Error ?? "The result was incomplete.");
                else
                    result.Rows = rows.Rows.Select(row => new RatioComparisonRow
                    {
                        Scope = row[0] == null || row[0] == DBNull.Value ? "(Blank)" : Convert.ToString(row[0], CultureInfo.InvariantCulture),
                        Before = RatioNumber(row[1]), After = RatioNumber(row[2])
                    }).ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result.Problem = "The numbers could not be compared. Connect again and retry."; }
        }
        if (!ReferenceEquals(context, _sessions.CurrentContext) || session.Revision != result.Revision)
        {
            result.Rows = Array.Empty<RatioComparisonRow>();
            result.Problem = "The model changed during the comparison. Compare again before choosing.";
        }
        var prompt = "This measure averages row percentages. Dividing totals gives larger rows more weight. Either can be right for the intended question. ";
        prompt += "Current formula: " + result.OriginalExpression + ". ";
        if (result.Expression != null) prompt += "Candidate formula: " + result.Expression + ". ";
        if (result.Problem != null) prompt += result.Problem + " ";
        else
        {
            prompt += "Read-only comparison, all data and up to 12 groups" + (result.GroupedBy == null ? "" : " by " + result.GroupedBy) + ": ";
            prompt += string.Join("; ", result.Rows.Select(r => r.Scope + ": before " + NumberText(r.Before) + ", after " + NumberText(r.After))) + ". ";
        }
        prompt += "Choose the meaning with the person, or use their stated aim. Keep the current formula if row percentages should have equal weight. "
            + "If totals are intended, review these numbers and use update_measure with objRef " + objRef
            + ", the candidate formula, and sessionId " + result.SessionId
            + ". Read the current formula again if another edit has landed. This changes the answer and is undoable. Never apply it as an equivalent rewrite.";
        return new FixPrompt { ObjectRef = objRef, RuleId = "AVG-OF-RATIO", Tool = "update_measure", Prompt = prompt, RatioOffer = result };
    }

    private static double? RatioNumber(object value) => value == null || value == DBNull.Value ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    private static string NumberText(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "blank";
}
