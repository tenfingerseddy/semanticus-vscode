// Shared cost helpers for the engine suite (lean suite, lane 0).
//
// Two things live here, and both exist so that a test class can be cheap without each class inventing its own way.
//
// 1. TestCost: the one spelling of the sweep trait. A test (method or class) that is too slow or too wide for every pull
//    request carries [Trait(TestCost.Key, TestCost.Sweep)]. CI's default run filters it out with `Cost!=sweep` and the nightly
//    sweep runs it with `Cost=sweep`. A test with no Cost trait is in the default run: VSTest reads a missing property as
//    "not equal". TestCostTests proves that, and proves nothing carries a misspelt value (which would silently stay default).
//
// 2. ModelTemplates: build a model once per test class, save it as a .bim, and give each test a COPY to open. A build is many
//    engine calls (each one a write on the single-writer dispatcher); an open is one read of one file. It removes BUILD cost only.
//    The precedent is VerifySolFixes1Round7DefectTests.HeldOutTemplate; this is the same idea made reusable and keyed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Entitlement;

namespace Semanticus.Tests
{
    public static class TestCost
    {
        /// <summary>The trait name CI filters on.</summary>
        public const string Key = "Cost";

        /// <summary>The one value that moves a test out of the default run: [Trait(TestCost.Key, TestCost.Sweep)].</summary>
        public const string Sweep = "sweep";
    }

    /// <summary>
    /// Builds a model once per key and opens a copy of it for each test. Use it as an <c>IClassFixture</c>.
    ///
    /// <code>
    /// public sealed class MyTests : IClassFixture&lt;ModelTemplates&gt;
    /// {
    ///     private readonly ModelTemplates _templates;
    ///     public MyTests(ModelTemplates templates) => _templates = templates;
    ///
    ///     [Fact] public async Task Something()
    ///     {
    ///         using var f = new DecisionFixture(TestEntitlements.Pro);
    ///         await _templates.OpenCopyAsync(f, "shop", BuildShopAsync, "Shop.bim");
    ///         await f.ConsentAsync();   // permission binds to the model's file, and this copy is a new file
    ///         ...
    ///     }
    /// }
    /// </code>
    ///
    /// What a copy is NOT: the build's undo and history steps are not in it (the engine opens it as a fresh file), so a test that
    /// counts them must keep the slow build. Consent, a live connection and a decision service are per engine, so set them on the
    /// fixture after the open, as a build-then-save would have had to anyway.
    ///
    /// The files are deleted in Dispose, which the class fixture runs when the class's last test is done, inside the test run.
    /// Nothing here runs as the test process exits (NoBrowserSignInGuardTests.No_test_runs_code_as_the_test_process_exits).
    /// </summary>
    public sealed class ModelTemplates : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sem-tpl-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        private int _builds;

        /// <summary>How many models this fixture has built (not opened). A test can assert it stays at one per key.</summary>
        public int Builds => Volatile.Read(ref _builds);

        // OpenCopyAsync and PathAsync are internal because DecisionFixture is.

        /// <summary>Copy the template for <paramref name="key"/> into <paramref name="f"/>'s workspace as <paramref name="fileName"/>
        /// and open it in <paramref name="f"/>'s engine. The first call for a key runs <paramref name="build"/> on a throwaway fixture
        /// (its own scratch home, so consent and settings never leak) and saves the result; later calls only copy and open.
        /// <paramref name="build"/> builds the model and does not save it. <paramref name="entitlement"/> is the tier the build runs
        /// under (Pro by default, like the precedent).</summary>
        internal async Task OpenCopyAsync(DecisionFixture f, string key, Func<DecisionFixture, Task> build, string fileName,
            IEntitlement entitlement = null)
        {
            var template = await PathAsync(key, build, entitlement);
            var path = Path.Combine(f.Workspace, fileName);
            File.Copy(template, path);
            await f.Engine.OpenAsync(path);
        }

        /// <summary>The saved template's path, building it on the first call for the key.</summary>
        internal async Task<string> PathAsync(string key, Func<DecisionFixture, Task> build, IEntitlement entitlement = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (_paths.TryGetValue(key, out var have) && File.Exists(have)) return have;
                using var scratch = new DecisionFixture(entitlement ?? TestEntitlements.Pro);
                await build(scratch);
                await scratch.SaveAsAsync("Template.bim");
                Directory.CreateDirectory(_dir);
                var keep = Path.Combine(_dir, _paths.Count + ".bim");
                File.Copy(Path.Combine(scratch.Workspace, "Template.bim"), keep);
                Interlocked.Increment(ref _builds);
                return _paths[key] = keep;
            }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
            _gate.Dispose();
        }
    }
}
