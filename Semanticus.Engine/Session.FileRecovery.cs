using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine
{
    public sealed partial class Session
    {
        private FileRecoveryRecord _fileRecovery;
        private Timer _recoveryTimer;
        private string _recoverySource;
        private bool _discardRecovery;
        private bool _ownsRecovery;
        private bool _recoveryStarted;
        private int _recoveryStopped;
        private bool RecoveryEligible => !string.IsNullOrEmpty(SourcePath) && LiveOrigin == null && SnapshotOf == null;

        internal FileRecoveryInfo FileRecovery => RecoveryEligible && _fileRecovery != null
            ? new FileRecoveryInfo { Source = _fileRecovery.Source, CapturedUtc = _fileRecovery.CapturedUtc } : null;

        internal void StartFileRecovery()
        {
            if (_recoveryStarted) return;
            _recoverySource = SourcePath;
            if (!RecoveryEligible) return;
            _recoveryStarted = true;
            try { _fileRecovery = RestorePointStore.ReadUnsaved(SourcePath); }
            catch (Exception ex) { RecoveryFailed(ex); }
            try
            {
                _recoveryTimer = new Timer(_ => _ = WriteFileRecoveryAsync(), null, Timeout.Infinite, Timeout.Infinite);
                _bus.Changed += RecoveryChanged;
            }
            catch (Exception ex) { RecoveryFailed(ex); }
        }

        private void RecoveryChanged(ChangeNotification change)
        {
            if (change.SessionId != Id || Volatile.Read(ref _recoveryStopped) != 0) return;
            if (change.IsCommit) _discardRecovery = false;
            try { _recoveryTimer?.Change(3000, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private async Task WriteFileRecoveryAsync()
        {
            try
            {
                await RunAsync(() =>
                {
                    if (Volatile.Read(ref _recoveryStopped) == 0) WriteFileRecovery();
                    return true;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { if (Volatile.Read(ref _recoveryStopped) == 0) RecoveryFailed(ex); }
        }

        private void WriteFileRecovery()
        {
            if (!RecoveryEligible || _discardRecovery) return;
            // While an offer is open it stays untouched and the new edits go beside it: neither set is lost.
            var beside = _fileRecovery != null;
            if (HasUnsavedEdits)
            {
                RestorePointStore.WriteUnsaved(SourcePath, RecoveryJson(), beside);
                _ownsRecovery = true;
            }
            else if (_ownsRecovery)
            {
                RestorePointStore.DeleteUnsaved(SourcePath, latestOnly: beside);
                _ownsRecovery = false;
            }
        }

        private string RecoveryJson()
        {
            // Use the save serializer's datasource scrub, with restricted information always disabled.
            // The model's SaveSensitive annotation must never opt the unattended recovery copy into secrets.
            var json = JObject.Parse(Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(TomDatabase,
                new Microsoft.AnalysisServices.Tabular.SerializeOptions { IncludeRestrictedInformation = false }));
            foreach (var source in json.SelectTokens("model.dataSources[*]").OfType<JObject>())
            foreach (var property in source.Descendants().OfType<JProperty>().ToArray())
            {
                if (property.Name.Equals("connectionString", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var builder = new DbConnectionStringBuilder { ConnectionString = property.Value.Value<string>() };
                        var keys = builder.Keys.Cast<string>().Where(SensitiveKey).ToArray();
                        if (keys.Length != 0)
                        {
                            foreach (var key in keys) builder.Remove(key);
                            property.Value = builder.ConnectionString;
                        }
                    }
                    catch { property.Value = ""; }
                }
                else if (SensitiveKey(property.Name)) property.Remove();
            }
            return json.ToString();
        }

        // The one compound-key-aware credential classifier (AccountKey, SharedAccessSignature, ClientSecret, ApiKey, access token ...).
        private static bool SensitiveKey(string key) => XmlaAuthHint.IsSecretKeyName(key);

        internal void ClearFileRecoveryAfterSave(bool onlyIfPathMoved = false)
        {
            // A save runs this twice (once when the file is marked clean, once after the path is rebased). On the same path
            // the second run must not settle the older copy the first one just offered.
            if (onlyIfPathMoved && SourcePath == _recoverySource) return;
            _fileRecovery = null;
            _ownsRecovery = false;
            try
            {
                // A save settles the set that was offered. A parked older copy of the same file is offered next.
                // On Save As the first run (before the path moved) already settled the old file's set and promoted its older copy.
                // This run leaves that old file alone: nobody saw the promoted copy, and the old file offers it when it opens again.
                var same = SourcePath == _recoverySource;
                if (same)
                {
                    RestorePointStore.DeleteUnsaved(_recoverySource, keepOlder: true);
                    _fileRecovery = RestorePointStore.PromoteOlder(_recoverySource);
                }
                else RestorePointStore.DeleteUnsaved(SourcePath);
            }
            catch (Exception ex) { RecoveryFailed(ex); }
            _recoverySource = SourcePath;
        }

        internal void DiscardReplacedFileRecovery()
        {
            // A successful model replacement has already settled the person's discard decision.
            // Keep an untouched offer for a later reopen, but never flush discarded working edits.
            try { OnDispatcher(() => { if (HasUnsavedEdits) DiscardFileRecovery(); return true; }).GetAwaiter().GetResult(); }
            catch (Exception ex) { RecoveryFailed(ex); }
        }

        internal void DiscardFileRecovery()
        {
            _discardRecovery = true;
            _fileRecovery = null;
            _ownsRecovery = false;
            RestorePointStore.DeleteUnsaved(_recoverySource);
        }

        internal async Task<bool> ResolveFileRecoveryAsync(string expectedSession, bool restore, string origin)
        {
            if (expectedSession != Id) throw new InvalidOperationException("The open model changed. Reopen its recovery offer.");
            if (!restore)
            {
                return await RunAsync(() =>
                {
                    if (!RecoveryEligible) return false;
                    if (_fileRecovery == null && !HasUnsavedEdits) return false;
                    var hadOffer = _fileRecovery != null;
                    RestorePointStore.DeleteUnsaved(SourcePath, keepOlder: hadOffer);
                    if (!hadOffer) _discardRecovery = true;
                    // Dismissing the newer set shows the older kept copy next.
                    _fileRecovery = hadOffer ? RestorePointStore.PromoteOlder(SourcePath) : null;
                    _ownsRecovery = false;
                    return true;
                }).ConfigureAwait(false);
            }
            await WithLifetimeLeaseAsync(() => TrackAsync(origin, "Restore unsaved changes", () =>
            {
                if (!RecoveryEligible || _fileRecovery == null) throw new InvalidOperationException("There are no kept changes for this model.");
                var depth = _model.BatchDepth;
                _model.BeginUpdate("Restore unsaved changes");
                try
                {
                    ModelRecovery.Apply(Model, _fileRecovery.BimJson);
                    RecordDelta(new ChangeDelta { Ref = "model", Kind = "structure", Props = new[] { "Recovery" } });
                    _model.EndUpdate();
                    _ownsRecovery = true;
                    // The older kept copy is offered next. The restored work goes beside it first, so it is on disk
                    // before the copy it came from is replaced.
                    _fileRecovery = RestorePointStore.PeekOlder(SourcePath);
                    if (_fileRecovery != null)
                    {
                        _discardRecovery = false;
                        WriteFileRecovery();
                        _fileRecovery = RestorePointStore.PromoteOlder(SourcePath);
                    }
                }
                catch { RollBackTo(depth); throw; }
            })).ConfigureAwait(false);
            return true;
        }

        private void StopFileRecovery()
        {
            if (!_recoveryStarted) return;
            Volatile.Write(ref _recoveryStopped, 1);
            _bus.Changed -= RecoveryChanged;
            _recoveryTimer?.Dispose();
            // Admission is already closed and drained. Flush on the still-live dispatcher before teardown.
            try { OnDispatcher(() => { WriteFileRecovery(); return true; }).GetAwaiter().GetResult(); }
            catch (Exception ex) { RecoveryFailed(ex); }
        }

        private static void RecoveryFailed(Exception ex)
        {
            try { Console.Error.WriteLine("[recovery] Could not keep the unsaved changes: " + ex.GetType().Name); } catch { }
        }
    }
}
