// Lean suite (8 Oct), lane E: open a saved copy of a model a test class builds once, in an engine the test already made.
//
// ModelTemplates (TestTemplates.cs) hands a copy to a DecisionFixture. Many older classes make their own SessionManager and
// LocalEngine (a free tier, a custom workspace, a fake entitlement) and cannot move to DecisionFixture without changing what they
// prove. This adds the one missing step for them: copy the template where the test wants the file, then open it in that engine.
//
// What a copy is NOT is the same as for ModelTemplates: the build's undo and history steps are not in it, and the engine opens it as a
// file. A test that counts undo steps or reads the build's ledger rows keeps its own build.

using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;

namespace Semanticus.Tests
{
    internal static class TemplateOpen
    {
        /// <summary>Open a copy of the model <paramref name="build"/> makes for <paramref name="key"/> in <paramref name="engine"/>.
        /// The first call per key builds the model once (on a throwaway engine, saved as a .bim) and every later call copies it.
        /// <paramref name="copyTo"/> is where this test's copy goes; leave it null to put it beside the template, which the class
        /// fixture deletes when the class is done.</summary>
        internal static async Task OpenCopyAsync(this ModelTemplates templates, LocalEngine engine, string key, Func<LocalEngine, Task> build,
            IEntitlement entitlement = null, string copyTo = null)
        {
            var template = await templates.PathAsync(key, f => build(f.Engine), entitlement);
            var path = copyTo ?? Path.Combine(Path.GetDirectoryName(template), Guid.NewGuid().ToString("N") + ".bim");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Copy(template, path, true);
            await engine.OpenAsync(path);
        }
    }
}
