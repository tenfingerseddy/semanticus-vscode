using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// Discovers running local Analysis Services instances behind open Power BI Desktop windows by
    /// reading each workspace's msmdsrv.port.txt and confirming the port is actually listening
    /// (managed APIs only — no P/Invoke). Good enough for the "edit my open .pbix" workflow; for SSAS
    /// or an explicit port, pass a Data Source to connect_local directly.
    /// </summary>
    public static class LocalDiscovery
    {
        public static LocalInstance[] List()
        {
            // Only bother if a local AS engine is actually running.
            var running = Process.GetProcessesByName("msmdsrv").Length > 0;
            if (!running) return Array.Empty<LocalInstance>();

            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
            if (!Directory.Exists(baseDir)) return Array.Empty<LocalInstance>();

            HashSet<int> listening;
            try { listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet(); }
            catch { listening = new HashSet<int>(); }

            var result = new List<LocalInstance>();
            foreach (var ws in Directory.GetDirectories(baseDir))
            {
                var portFile = Path.Combine(ws, "Data", "msmdsrv.port.txt");
                if (!File.Exists(portFile)) continue;
                int port;
                try
                {
                    var txt = File.ReadAllText(portFile, Encoding.Unicode);
                    var digits = new string(txt.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out port) || port <= 0) continue;
                }
                catch { continue; }

                if (listening.Count > 0 && !listening.Contains(port)) continue; // stale workspace
                result.Add(new LocalInstance { Port = port, Title = Path.GetFileName(ws) });
            }
            return result.ToArray();
        }
    }
}
