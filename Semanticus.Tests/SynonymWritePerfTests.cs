using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// One synonym write must not rescan the whole linguistic schema once per object (Kane's live session, 11 Oct
    /// 2026: 885 synonyms and 308 waivers written one call at a time fell from 21 to 4.8 writes a minute). Every commit
    /// runs the health probe's readiness rescan, and SYN-FIELD / SYN-TABLE read each visible field's synonyms through a
    /// reader that parsed the whole schema per object: about 900 parses per write, each growing with every word
    /// written. Driven through LocalEngine so the commit path the person pays for (write, health probe) is what is timed.
    /// </summary>
    public sealed class SynonymWritePerfTests
    {
        private readonly ITestOutputHelper _out;
        public SynonymWritePerfTests(ITestOutputHelper output) => _out = output;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const int Tables = 30, ColumnsPerTable = 30;   // 900 visible fields

        [Fact]
        public async Task A_synonym_write_costs_the_same_however_many_were_written_before_it()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("SynonymPerf", 1604);
            await sessions.Require().MutateAsync("human", "seed 900 fields", m =>
            {
                for (var t = 0; t < Tables; t++)
                {
                    var table = m.AddTable($"Table {t:00}");
                    for (var c = 0; c < ColumnsPerTable; c++) table.AddDataColumn($"Field {t:00}-{c:00}");
                }
            });
            await engine.CreateLinguisticSchemaAsync("en-US", "human");

            var fields = Enumerable.Range(0, Tables * ColumnsPerTable)
                .Select(i => $"column:Table {i / ColumnsPerTable:00}/Field {i / ColumnsPerTable:00}-{i % ColumnsPerTable:00}").ToList();
            async Task<(long Ms, int Parses)> WriteAsync(int i)
            {
                var parses = SynonymIndex.Parses;
                var sw = Stopwatch.StartNew();
                var r = await engine.SetSynonymsAsync(fields[i], new[] { $"alias {i} one", $"alias {i} two", $"alias {i} three" }, "en-US", "agent");
                sw.Stop();
                Assert.True(r.Changed);
                return (sw.ElapsedMilliseconds, SynonymIndex.Parses - parses);
            }

            for (var i = 0; i < 5; i++) await WriteAsync(i);   // warm: JIT and the health probe's first full scan
            var timed = new List<(long Ms, int Parses)>();
            for (var i = 5; i < 55; i++) timed.Add(await WriteAsync(i));

            static double Median(IEnumerable<long> xs) { var a = xs.OrderBy(x => x).ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2.0; }
            var first = Median(timed.Take(10).Select(x => x.Ms));
            var last = Median(timed.Skip(timed.Count - 10).Select(x => x.Ms));
            _out.WriteLine($"per-write ms: first10 median {first}, last10 median {last}; parses per write max {timed.Max(x => x.Parses)}; all ms: {string.Join(",", timed.Select(x => x.Ms))}");

            // The schema is read a bounded number of times per commit, not once per field.
            Assert.All(timed, x => Assert.InRange(x.Parses, 0, 2));
            // And the write does not slow down as words pile up.
            Assert.True(last <= first * 2 + 25, $"per-write time grew from {first} ms to {last} ms over 50 writes");
            Assert.Equal(Tables * ColumnsPerTable, await sessions.Require().ReadAsync(m => m.AllColumns.Count()));
        }
    }
}
