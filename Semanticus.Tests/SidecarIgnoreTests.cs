using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// PR #399 CI run 3: a Windows folder save failed with a sharing violation on <c>.semanticus/.gitignore</c> because another
    /// writer (the History, the experience log, a checkpoint) had the file open at the same moment. One helper now writes the file:
    /// it serialises callers, writes through a temp file, waits out a sharing violation and never throws. .NET on Unix gives
    /// <c>FileShare.None</c> the same effect a Windows open has (an advisory exclusive lock), so Linux reproduces the race for real.
    /// </summary>
    public sealed class SidecarIgnoreTests
    {
        private static string Scratch()
        {
            var d = Path.Combine(Path.GetTempPath(), "sg" + Guid.NewGuid().ToString("N").Substring(0, 8), LayoutStore.DirName);
            Directory.CreateDirectory(d);
            return d;
        }

        private static void Delete(string sidecar)
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar), true); } catch { }
        }

        [Fact]
        public void A_writer_holding_the_ignore_file_open_does_not_fail_the_caller_and_the_line_lands_after_it_lets_go()
        {
            var dir = Scratch();
            try
            {
                var file = Path.Combine(dir, ".gitignore");
                File.WriteAllText(file, "engine.lock\n");               // an older sidecar: the ledger lines are still to come
                using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var release = Task.Run(() => { Thread.Sleep(300); held.Dispose(); });

                LayoutStore.EnsureRuntimeIgnore(dir);                   // used to throw "being used by another process"
                release.Wait();

                var lines = File.ReadAllLines(file);
                Assert.Contains(EditLedger.FileName, lines);
                Assert.Contains("engine.lock", lines);
            }
            finally { Delete(dir); }
        }

        [Fact]
        public void A_sharing_violation_that_never_clears_does_not_throw_out_of_the_helper()
        {
            var dir = Scratch();
            try
            {
                var file = Path.Combine(dir, ".gitignore");
                File.WriteAllText(file, "engine.lock\n");
                using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Null(Record.Exception(() => LayoutStore.EnsureRuntimeIgnore(dir)));
                Assert.Null(Record.Exception(() => LayoutStore.EnsureSwitchIgnore(dir)));
            }
            finally { Delete(dir); }
        }

        [Fact]
        public void A_failing_try_is_repeated_and_the_third_try_writes_the_file()
        {
            var dir = Scratch();
            try
            {
                var file = Path.Combine(dir, ".gitignore");
                var tries = 0;
                LayoutStore.IgnoreIoHookForTest = p =>
                {
                    if (string.Equals(p, file, StringComparison.Ordinal) && Interlocked.Increment(ref tries) <= 2)
                        throw new IOException("The process cannot access the file because it is being used by another process.");
                };
                try { LayoutStore.EnsureRuntimeIgnore(dir); }
                finally { LayoutStore.IgnoreIoHookForTest = null; }
                Assert.Equal(3, tries);
                Assert.Contains(EditLedger.FileName, File.ReadAllLines(file));
                Assert.False(File.Exists(file + ".tmp"));
            }
            finally { Delete(dir); }
        }

        [Fact]
        public void Many_writers_at_once_leave_every_line_exactly_once_and_none_throws()
        {
            var dir = Scratch();
            try
            {
                var file = Path.Combine(dir, ".gitignore");
                File.WriteAllText(file, "engine.lock\n");
                var failures = 0;
                Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    try { if (i % 2 == 0) LayoutStore.EnsureRuntimeIgnore(dir); else LayoutStore.EnsureSwitchIgnore(dir); }
                    catch { Interlocked.Increment(ref failures); }
                });
                Assert.Equal(0, failures);
                var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
                Assert.Equal(lines.Length, lines.Distinct().Count());
                Assert.Contains(EditLedger.FileName, lines);
                Assert.Contains(Semanticus.Engine.Query.DataQuestionStore.FileName, lines);
            }
            finally { Delete(dir); }
        }

        [Fact]
        public void A_line_the_person_removed_is_not_put_back_and_an_up_to_date_file_is_not_rewritten()
        {
            var dir = Scratch();
            try
            {
                LayoutStore.EnsureSwitchIgnore(dir);
                var file = Path.Combine(dir, ".gitignore");
                var kept = File.ReadAllLines(file).Where(l => l != "engine.json").ToArray();
                File.WriteAllLines(file, kept);
                var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(file, stamp);

                LayoutStore.EnsureSwitchIgnore(dir);

                Assert.DoesNotContain("engine.json", File.ReadAllLines(file));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
            }
            finally { Delete(dir); }
        }
    }
}
