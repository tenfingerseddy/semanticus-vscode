using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// export_vpax (Phase 2, "VertiPaq → Dax.Vpax"): the open model exports to a VertiPaq-Analyzer .vpax via
    /// Microsoft/SQLBI's official Dax.Vpax — the interchange format for VertiPaq Analyzer / DAX Studio / the SQLBI
    /// ecosystem (a swiss-army-knife interop). This proves the METADATA export round-trips: we write a real .vpax
    /// and read it back with the official importer. (Storage statistics need a live model — a graceful follow-up.)
    /// </summary>
    public sealed class VpaxExportTests
    {
        [Fact]
        public async Task Export_vpax_writes_a_vpax_the_official_importer_can_read_back()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            var path = Path.Combine(Path.GetTempPath(), "semanticus_vpax_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".vpax");
            try
            {
                await engine.OpenAsync(TestModels.FindBim());

                var result = await engine.ExportVpaxAsync(path);
                Assert.True(result.Exported, result.Error);   // surfaces the Error message if it failed
                Assert.True(result.Tables > 0);
                Assert.Equal(path, result.Path);
                Assert.True(File.Exists(path));

                // Read it back with the official Dax.Vpax importer — the round-trip proves it is a valid .vpax that
                // carries our model metadata (not just that a file was written).
                var content = Dax.Vpax.Tools.VpaxTools.ImportVpax(path, importDatabase: false);
                Assert.NotNull(content.DaxModel);
                Assert.NotEmpty(content.DaxModel.Tables);
            }
            finally { engine.Dispose(); try { File.Delete(path); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task Export_vpax_is_door_safe_on_a_bad_path()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var result = await engine.ExportVpaxAsync("   ");   // blank path → in-band Error, not a throw
                Assert.False(result.Exported);
                Assert.False(string.IsNullOrEmpty(result.Error));
            }
            finally { engine.Dispose(); }
        }
    }
}
