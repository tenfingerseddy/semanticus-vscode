#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Checks
{
    public enum PlaceKind { GrandTotal, AxisAlone, SingleMember, SeveralMembers, EmptySelection }
    public enum PlaceStatus { Required, Dormant, Added }
    public enum KeyKind { Total, Blank, Value }
    public record Axis(string Table, string Column);
    public record Slicer(string Table, string Column, KeyPart[] Values);
    public record KeyPart(KeyKind Kind, string? Value);
    public record Place(string Id, PlaceKind Kind, PlaceStatus Status, string Words, Axis[] Rows, Slicer[] Slicers);
    public record PlaceQuery(string Id, string Dax, MultiSetSpec Spec);

    // Detached model definitions and typed filters are reused from the former candidate query runner.
    // These queries return observations. The caller decides what those observations establish.
    public static class PlaceQueries
    {
        public const int RowCap = 10000;

        public static string Define(ModelSnapshot snapshot)
        {
            var query = new StringBuilder("DEFINE").AppendLine();
            foreach (var measure in snapshot.Dependencies.Append(snapshot.Candidate)
                .OrderBy(m => m.Table, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal))
                query.Append("MEASURE ").Append(PlaceDax.Table(measure.Table)).Append(PlaceDax.Name(measure.Name))
                    .Append(" = ").Append(measure.Expression).AppendLine();
            return query.ToString();
        }

        public static PlaceQuery[] Build(ModelSnapshot snapshot, IReadOnlyList<Place> places)
        {
            if (places.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != places.Count)
                throw new ArgumentException("Each place must have a different id.", nameof(places));
            return places.Where(p => p.Status != PlaceStatus.Dormant).Select(place => {
                var args = new List<string>();
                foreach (var axis in place.Rows)
                {
                    var column = snapshot.Structure.Columns.FirstOrDefault(c => c.Axis == axis)
                        ?? throw new ArgumentException("The grouping column is not in the model.");
                    if (column.GroupByColumns.Any(g => g != axis))
                        throw new ArgumentException("The grouping column needs another column with it.");
                }
                if (place.Rows.Length > 0)
                    args.Add("ROLLUPADDISSUBTOTAL(" + string.Join(", ", place.Rows.Select((axis, index) =>
                        PlaceDax.Column(axis) + ", " + PlaceDax.Text("__total" + index.ToString(CultureInfo.InvariantCulture)))) + ")");
                args.AddRange(place.Slicers.Select(s => PlaceDax.Filter(s, snapshot.Structure)));
                args.Add("\"__value\", " + PlaceDax.Table(snapshot.Candidate.Table) + PlaceDax.Name(snapshot.Candidate.Name));
                args.Add("\"__keep\", 1");
                return new PlaceQuery(place.Id, "SUMMARIZECOLUMNS(" + string.Join(", ", args) + ")",
                    new MultiSetSpec(place.Id, RowCap, new[] { place.Id }));
            }).ToArray();
        }

        public static Task<MultiResult> ExecuteAsync(LiveConnection connection, ModelSnapshot snapshot,
            IReadOnlyList<Place> places, CancellationToken ct = default)
        {
            var queries = Build(snapshot, places);
            if (queries.Length == 0) return Task.FromResult(new MultiResult(Array.Empty<MultiSetResult>(), 0, 0));
            return connection.ExecuteMultiAsync(Define(snapshot) + string.Join(Environment.NewLine,
                queries.Select(q => "EVALUATE " + q.Dax)), queries.Select(q => q.Spec).ToArray(), 10, ct);
        }
    }

    public record MemberSample(KeyPart Key, long Rows);
    public record MemberProfile(Axis Axis, MemberSample[] Members, bool Complete, string? Error);

    public static class MemberReader
    {
        // Keep the original high/low count, first/last key, and blank-member sample.
        public static string Query(ModelStructure structure, Axis axis, string fact)
        {
            var info = structure.Columns.FirstOrDefault(c => c.Axis == axis)
                ?? throw new ArgumentException("The member column is not in the model.");
            var column = PlaceDax.Column(axis);
            var grouping = new[] { axis }.Concat(info.GroupByColumns).Distinct().ToArray();
            var counted = HasFilterPath(structure, axis.Table, fact) ? fact : axis.Table;
            var members = "FILTER(SUMMARIZECOLUMNS(" + string.Join(", ", grouping.Select(PlaceDax.Column))
                + ", \"__rows\", COUNTROWS(" + PlaceDax.Table(counted) + ")), [__rows] > 0)";
            var sample = "VAR __members = " + members + " RETURN DISTINCT(UNION("
                + "TOPN(1, __members, [__rows], DESC, " + column + ", ASC),"
                + "TOPN(1, __members, [__rows], ASC, " + column + ", ASC),"
                + "TOPN(1, __members, " + column + ", ASC),"
                + "TOPN(1, __members, " + column + ", DESC),"
                + "FILTER(__members, ISBLANK(" + column + "))))";
            return "EVALUATE SELECTCOLUMNS((" + sample + "), \"__member\", " + column + ", \"__rows\", [__rows])";
        }

        public static async Task<MemberProfile> ReadAsync(LiveConnection connection, ModelStructure structure,
            Axis axis, string fact, CancellationToken ct = default)
        {
            var result = await connection.ExecuteMultiAsync(Query(structure, axis, fact),
                new[] { new MultiSetSpec("members", 5, Array.Empty<string>(), new[] { 0 }) }, 10, ct).ConfigureAwait(false);
            var set = result.Sets.Single().Result;
            return new(axis, set.Error != null || set.Truncated ? Array.Empty<MemberSample>() : set.Rows.Select(row =>
                new MemberSample(PlaceDax.Key(row[0]), row[1] == null ? 0 : Convert.ToInt64(row[1], CultureInfo.InvariantCulture))).ToArray(),
                set.Error == null && !set.Truncated, set.Error);
        }

        public static bool HasFilterPath(ModelStructure structure, string from, string to)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
            var pending = new Queue<string>();
            pending.Enqueue(from);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (ModelSnapshots.Eq(current, to)) return true;
                foreach (var path in structure.Relationships.Where(p => p.Active))
                {
                    if (ModelSnapshots.Eq(path.To.Table, current) && visited.Add(path.From.Table)) pending.Enqueue(path.From.Table);
                    if (path.TwoWay && ModelSnapshots.Eq(path.From.Table, current) && visited.Add(path.To.Table)) pending.Enqueue(path.To.Table);
                }
            }
            return false;
        }
    }
}

namespace Semanticus.Engine
{
    public sealed record MultiSetSpec(string Id, int MaxRows, string[] PlaceIds, int[]? KeyColumns = null);
}
