using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The runtime enforcement of the TOM-bump gate (hole #4, docs/tom-bump-gate.md): every loaded AMO/TOM assembly
    /// must be the pinned 19.114.x. A bump — even a PARTIAL one (only one csproj edited) — surfaces here as a version
    /// mismatch and goes RED, forcing the change through the gate before it can land. FormulaFixup + the process-wide
    /// Singleton ride on this exact TOM, so the pin is load-bearing, not cosmetic. Update PinnedMajor/Minor only as
    /// the LAST step of a gate that has actually passed.
    /// </summary>
    public sealed class TomVersionPinTests
    {
        private const int PinnedMajor = 19;
        private const int PinnedMinor = 114;

        [Fact]
        public async Task All_loaded_TOM_AMO_assemblies_are_pinned_to_19_114()
        {
            // Build a handler so the AMO/TOM assemblies are actually loaded into the AppDomain before we inspect.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("PinCheck", 1604);

                var amo = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetName())
                    .Where(n => n.Name != null && n.Name.StartsWith("Microsoft.AnalysisServices", StringComparison.Ordinal))
                    .ToList();

                Assert.NotEmpty(amo);   // TOM must be loaded — otherwise this guard is asserting nothing
                foreach (var n in amo)
                    Assert.True(n.Version != null && n.Version.Major == PinnedMajor && n.Version.Minor == PinnedMinor,
                        $"{n.Name} is {n.Version} — expected {PinnedMajor}.{PinnedMinor}.x. " +
                        "If you bumped TOM, pass docs/tom-bump-gate.md (JDK + cross-platform smokes + FormulaFixup " +
                        "differential) and update PinnedMajor/Minor here as the LAST step.");
            }
            finally { engine.Dispose(); }
        }
    }
}
