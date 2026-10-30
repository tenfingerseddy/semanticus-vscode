#nullable enable
using System;
using System.Collections.Generic;

namespace Semanticus.Engine.Checks;

// The query key includes the grouping columns and filters.
// The structure hash also includes relationships, calendar marks/objects and calculation groups.
public sealed record MemberReadKey(string BaseExpression, string StructureHash, string ConnectionId, string DataAsOf,
    int MaxRows = PlaceQueries.RowCap);

public sealed class MemberReadCache
{
    private const int RowBudget = 50000, EntryCap = 128;
    private readonly Dictionary<MemberReadKey, ResultSet> entries = new();
    private int rows;
    public bool TryGet(MemberReadKey key, out ResultSet result)
    {
        if (!string.IsNullOrEmpty(key.DataAsOf) && entries.TryGetValue(key, out var stored))
        {
            result = Copy(stored);
            return true;
        }
        result = null!;
        return false;
    }
    public void Put(MemberReadKey key, ResultSet result, bool cacheCapped = false)
    {
        // ReadMulti discards every row on a cap. Retain that small, unusable envelope only for Grouped reads.
        var capped = cacheCapped && result.Truncated && result.Rows.Length == 0 && result.RowCount == 0
            && !result.Cancelled && !result.AuthFailed;
        if (string.IsNullOrEmpty(key.DataAsOf) || !capped && (result.Error != null || result.Truncated
            || result.Rows.Length > PlaceQueries.RowCap)) return;
        var previous = entries.TryGetValue(key, out var stored) ? stored.Rows.Length : 0;
        // X12 has 72 grouped entries. Bound empty entries as well as the total stored rows.
        if ((entries.Count >= EntryCap && stored == null) || rows - previous + result.Rows.Length > RowBudget)
        {
            Clear();
            previous = 0;
        }
        entries[key] = Copy(result);
        rows += result.Rows.Length - previous;
    }
    public void Clear() { entries.Clear(); rows = 0; }

    // Decode can clear an envelope's rows, and callers can edit returned row arrays.
    // Keep both array levels separate on insertion and retrieval.
    private static ResultSet Copy(ResultSet result) => new() {
        Columns = result.Columns, Rows = Array.ConvertAll(result.Rows, row => (object[])row.Clone()), RowCount = result.RowCount,
        Error = result.Error, Truncated = result.Truncated, Cancelled = result.Cancelled, AuthFailed = result.AuthFailed
    };
}
