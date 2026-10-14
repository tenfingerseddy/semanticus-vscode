using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    /// <summary>A connection to one live database with its model already read. A publish preview opens one and keeps it for
    /// the commit, so the slow part (connect, then read the whole live model) happens once, not twice. Before the commit
    /// writes, <see cref="IsCurrent"/> asks one cheap question: has the live model changed since it was read? If it has,
    /// or the answer is unclear, the caller reads it again. A held read is never written over a change it did not see.</summary>
    internal abstract class LiveHandle : IDisposable
    {
        /// <summary>When the sign-in token this connection used stops working. Local instances carry no token (MaxValue).</summary>
        public DateTimeOffset ExpiresOn { get; protected set; } = DateTimeOffset.MaxValue;

        /// <summary>One cheap check that the live model is the one that was read. False on any doubt.</summary>
        public abstract bool IsCurrent();

        public abstract DeployReport Sync(string sessionBimPath, string endpoint, string database, bool commit,
            IReadOnlyCollection<LiveDeleteTarget> explicitDeleteTargets, bool identityStrict, bool captureRestorePoint,
            IReadOnlyCollection<string> explicitDeleteRefs, DeployTimer timer);

        public abstract void Dispose();
    }

    /// <summary>The real thing: an AMO server connection and the live database it read.</summary>
    internal sealed class AmoLiveHandle : LiveHandle
    {
        private readonly TOM.Server _server;
        private readonly TOM.Database _liveDb;
        private readonly string _endpoint;
        private readonly string _token;
        private readonly LiveStamp _stamp;
        /// <summary>Test seam: what the live service says now. Null in production (the real question is asked).</summary>
        private readonly Func<LiveStamp> _readNow;
        private bool _unclean;   // a preview could not be undone in memory: the next use must read again

        /// <summary>What says "the live model is the one I read". The model row's two times can stay the same when another writer
        /// edits one measure, so the database's own version and last-update time are compared too (Tabular Editor checks
        /// Database.Version for this case, TabularModelHandler.Database.cs). Any one differing means the model changed.</summary>
        internal readonly record struct LiveStamp(DateTime Modified, DateTime Structure, long Version, DateTime LastUpdate);

        private AmoLiveHandle(TOM.Server server, TOM.Database liveDb, string endpoint, string token, DateTimeOffset expiresOn, LiveStamp stamp)
        {
            _server = server; _liveDb = liveDb; _endpoint = endpoint; _token = token; ExpiresOn = expiresOn; _stamp = stamp;
        }

        private AmoLiveHandle(LiveStamp stamp, Func<LiveStamp> readNow, DateTimeOffset expiresOn)
        {
            _stamp = stamp; _readNow = readNow; ExpiresOn = expiresOn;
        }

        /// <summary>Test seam: a handle that holds no connection and asks <paramref name="readNow"/> instead of the service.</summary>
        internal static AmoLiveHandle ForTest(LiveStamp read, Func<LiveStamp> readNow, DateTimeOffset expiresOn) => new AmoLiveHandle(read, readNow, expiresOn);

        public static AmoLiveHandle Open(string endpoint, string database, string token, DateTimeOffset expiresOn, DeployTimer timer)
        {
            var server = new TOM.Server();
            try
            {
                // A token = cloud XMLA (AMO managed auth via Server.AccessToken). No token = a LOCAL instance
                // (Power BI Desktop): connect with the current Windows identity, as Tabular Editor does.
                if (!string.IsNullOrEmpty(token))
                    server.AccessToken = new AS.AccessToken(token, expiresOn);
                timer.Begin("connect", "Connecting to the service");
                server.Connect("Data Source=" + endpoint);
                var liveDb = server.Databases.FindByName(database) ?? throw new InvalidOperationException($"Database '{database}' not found on the endpoint.");
                // The database's version and last-update time are taken BEFORE the model is read, so a change made during the read
                // shows as a difference at the check and the model is read again.
                var version = liveDb.Version; var lastUpdate = liveDb.LastUpdate;
                // The model is read on first touch. Touch it here so the time is named "read", not hidden in the compare.
                timer.Begin("read", "Reading the live model");
                _ = liveDb.Model.Tables.Count;
                timer.End();
                var stamp = new LiveStamp(liveDb.Model.ModifiedTime, liveDb.Model.StructureModifiedTime, version, lastUpdate);
                return new AmoLiveHandle(server, liveDb, endpoint, token, string.IsNullOrEmpty(token) ? DateTimeOffset.MaxValue : expiresOn, stamp);
            }
            catch { try { server.Dispose(); } catch { /* the original error is the one to show */ } throw; }
        }

        public override bool IsCurrent()
        {
            if (_unclean) return false;
            try
            {
                var now = (_readNow ?? ReadFromService)();
                var same = now == _stamp;
                try { Console.Error.WriteLine($"[deploy] check current={same}"); } catch { /* ignore */ }
                return same;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("[deploy] check failed, reading again: " + ex.GetType().Name + ": " + FabricRest.Scrub(ex.Message)); } catch { /* ignore */ }
                return false;
            }
        }

        /// <summary>The XMLA command for the model row's two times. Same shape as Tabular Editor's DBSCHEMA_CATALOGS read (DatabasePage.cs).</summary>
        internal static readonly string ModelTimesStatement =
            new System.Xml.Linq.XElement("Statement", "SELECT [ModifiedTime], [StructureModifiedTime] FROM $SYSTEM.TMSCHEMA_MODEL").ToString();

        /// <summary>Reads the model row's two times from the reply. A reply with no row is an error with its own words.</summary>
        internal static (DateTime Modified, DateTime Structure) ReadModelTimes(System.Data.IDataReader reader)
        {
            if (reader == null || !reader.Read()) throw new InvalidOperationException("no model row");
            return (Convert.ToDateTime(reader.GetValue(reader.GetOrdinal("ModifiedTime"))),
                    Convert.ToDateTime(reader.GetValue(reader.GetOrdinal("StructureModifiedTime"))));
        }

        private LiveStamp ReadFromService()
        {
            // One row from the model table of the live database's own schema rowset: when the model was last changed.
            // AMO's ExecuteReader takes XMLA, so the text goes in a <Statement>, the way Tabular Editor sends it. Sent bare, the call
            // threw on every check and every commit read the live model again (hero19 D4).
            using var reader = _server.ExecuteReader(ModelTimesStatement, out var results,
                new Dictionary<string, string> { ["Catalog"] = _liveDb.Name });
            if (results != null && results.ContainsErrors)
                throw new InvalidOperationException("the service refused the model time question: " + (results[0].Messages.OfType<AS.XmlaError>().FirstOrDefault()?.Description ?? "no reason given"));
            var (modified, structure) = ReadModelTimes(reader);
            if (modified != _stamp.Modified || structure != _stamp.Structure) return new LiveStamp(modified, structure, -1, default);
            // The model row can stay the same when one measure changes, so ask the database itself, on a fresh short connection
            // (this connection's own copy of the database is only as new as its read). Same question Tabular Editor asks.
            using var fresh = new TOM.Server();
            if (!string.IsNullOrEmpty(_token)) fresh.AccessToken = new AS.AccessToken(_token, ExpiresOn);
            fresh.Connect("Data Source=" + _endpoint);
            var db = fresh.Databases.FindByName(_liveDb.Name) ?? throw new InvalidOperationException("database gone");
            return new LiveStamp(modified, structure, db.Version, db.LastUpdate);
        }

        public override DeployReport Sync(string sessionBimPath, string endpoint, string database, bool commit,
            IReadOnlyCollection<LiveDeleteTarget> explicitDeleteTargets, bool identityStrict, bool captureRestorePoint,
            IReadOnlyCollection<string> explicitDeleteRefs, DeployTimer timer)
        {
            var src = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(sessionBimPath), null, AS.CompatibilityMode.PowerBI).Model;
            var liveDb = _liveDb;

            // Whole-model Save to Live had no restore point (D-003). Capture the live metadata HERE, after
            // connect and before SyncAndApply mutates it, so production pays one connection. The selective
            // push already wrote its own restore point and leaves captureRestorePoint false.
            RestorePointRecord restorePoint = null;
            var tickedDeletes = explicitDeleteRefs != null && explicitDeleteRefs.Count > 0
                && (explicitDeleteTargets == null || explicitDeleteTargets.Count == 0);
            if (captureRestorePoint && commit)
            {
                timer.Begin("restore", "Saving a restore point");
                try
                {
                    restorePoint = RestorePointStore.Write(endpoint, database,
                        TOM.JsonSerializer.SerializeDatabase(liveDb), "deploy_live",
                        tickedDeletes ? explicitDeleteRefs.Count + " delete(s)" : "before the live write",
                        Array.Empty<string>(),
                        tickedDeletes ? explicitDeleteRefs.ToArray() : Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    // A ticked delete cannot be undone without a restore point, so a failed snapshot is a wall.
                    // A whole-model save with no deletes is a warning, not a wall (D-003).
                    if (tickedDeletes)
                    {
                        timer.End();
                        return new DeployReport { Endpoint = endpoint, Database = database,
                            Error = LiveMatchCopy.DeleteRefusedNoRestore(FabricRest.Scrub(ex.Message)) };
                    }
                }
            }

            // The SERVER-FREE orchestration lives in SyncAndApply (offline-testable). We pass the live model's real
            // SaveChanges + calc-recalc as callbacks, a test passes in-memory recorders instead.
            timer.Begin("compare", "Comparing with your copy");
            DeployReport rep;
            try
            {
                rep = LiveDeploy.SyncAndApply(src, liveDb.Model, commit, endpoint, database, explicitDeleteTargets,
                    saveChanges: () => liveDb.Model.SaveChanges(),
                    recalcCalcTables: names =>
                    {
                        timer.Begin("recalc", "Updating new calculated tables");
                        foreach (var name in names) liveDb.Model.Tables.Find(name)?.RequestRefresh(TOM.RefreshType.Calculate);
                        liveDb.Model.SaveChanges();
                    },
                    reloadCalcTables: names =>
                    {
                        // The service made the new tables' columns on the Calculate pass; this copy of the model learns of them only on a re-read.
                        if (names.Any(n => liveDb.Model.Tables.Find(n)?.Columns.Count == 0)) liveDb.Refresh();
                        return liveDb.Model;
                    },
                    identityStrict: identityStrict, explicitDeleteRefs: explicitDeleteRefs,
                    beforeSend: n => timer.Begin("send", n == 1 ? "Sending 1 change" : $"Sending {n} changes"),
                    raiseCompatibility: n => { timer.Begin("level", "Raising the compatibility level"); liveDb.CompatibilityLevel = n; liveDb.Update(AS.UpdateOptions.Default); });
            }
            finally
            {
                timer.End();
                // A preview changes nothing live. Put the in-memory model back exactly as it was read, so the commit that
                // reuses it starts from what the service holds. If that cannot be done, the next use reads again.
                if (!commit)
                {
                    try { liveDb.Model.UndoLocalChanges(); }
                    catch { _unclean = true; }
                }
            }
            if (rep != null && restorePoint != null) rep.RestorePointId = restorePoint.Id;
            return rep;
        }

        public override void Dispose()
        {
            try { _server?.Dispose(); } catch { /* closing a dead connection is not a failure */ }
        }
    }
}
