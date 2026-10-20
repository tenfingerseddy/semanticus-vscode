// Two things the assistant can ask for that only the person's VS Code window can do (F-292, F-293; golden rule 2: every
// capability has both doors). The window owns the personality, sound and volume settings (VS Code user settings) and the
// Pro trial's email round trip (the billing service, the link in the email, the licence swap). The engine holds the last
// settings the window reported, and asks the window by notification: host/settings and host/startTrial. Nothing here
// touches the model, its files or a licence directly.

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>The Studio's personality, sounds and volume, as the window last reported them. Read by get_personality_settings.</summary>
    public sealed class HostSettingsView
    {
        /// <summary>"on" or "quiet".</summary>
        public string Personality { get; set; } = "on";
        public bool Sounds { get; set; }
        /// <summary>0 to 100.</summary>
        public int SoundVolume { get; set; } = 40;
        /// <summary>False until a VS Code window has told the engine its settings. The values are then the defaults.</summary>
        public bool Reported { get; set; }
        /// <summary>For set_personality_settings: false when no VS Code window was open to write them.</summary>
        public bool Applied { get; set; }
        public string Note { get; set; }
    }

    /// <summary>What start_trial answers.</summary>
    public sealed class TrialStartResult
    {
        /// <summary>"requested" (the window sends the email now), "needsYes" (the person has not said yes), "alreadyPro", "noWindow" or "badEmail".</summary>
        public string State { get; set; }
        public string Message { get; set; }
    }

    public sealed partial class LocalEngine
    {
        private static readonly Regex TrialEmail = new Regex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);
        private readonly object _hostSettingsGate = new object();
        private HostSettingsView _hostSettings = new HostSettingsView();

        public Task<HostSettingsView> GetHostSettingsAsync() => Task.FromResult(HostSettingsCopy(applied: false, note: null));

        private HostSettingsView HostSettingsCopy(bool applied, string note)
        {
            lock (_hostSettingsGate)
                return new HostSettingsView { Personality = _hostSettings.Personality, Sounds = _hostSettings.Sounds, SoundVolume = _hostSettings.SoundVolume, Reported = _hostSettings.Reported, Applied = applied, Note = note };
        }

        /// <summary>The assistant sets any of the three. Each one left out stays as it is. The window writes them to the person's VS Code user settings.</summary>
        public Task<HostSettingsView> SetHostSettingsAsync(string personality, bool? sounds, int? soundVolume, string origin = "human")
        {
            try { GuardOutsideWrite(origin); } catch (Exception ex) { return Task.FromException<HostSettingsView>(ex); }
            var p = string.IsNullOrWhiteSpace(personality) ? null : personality.Trim().ToLowerInvariant();
            if (p != null && p != "on" && p != "quiet")
                throw new ArgumentException($"personality '{personality}' is not one of on, quiet.");
            if (soundVolume is int v && (v < 0 || v > 100))
                throw new ArgumentException($"soundVolume {v} is not between 0 and 100.");
            if (p == null && sounds == null && soundVolume == null)
                throw new ArgumentException("Say at least one of personality (on or quiet), sounds (true or false), soundVolume (0 to 100).");
            HostSettingsView next;
            lock (_hostSettingsGate)
            {
                if (p != null) _hostSettings.Personality = p;
                if (sounds is bool s) _hostSettings.Sounds = s;
                if (soundVolume is int sv) _hostSettings.SoundVolume = sv;
                next = new HostSettingsView { Personality = _hostSettings.Personality, Sounds = _hostSettings.Sounds, SoundVolume = _hostSettings.SoundVolume, Reported = _hostSettings.Reported };
            }
            var window = _sessions.Bus.HumanClientAttached?.Invoke() == true;
            if (window) _sessions.Bus.PublishHostSettings(next);
            return Task.FromResult(HostSettingsCopy(window, window ? "Set in the person's VS Code settings." : "No VS Code window is open to write it. It is applied the next time one opens."));
        }

        /// <summary>The window reports what the person's settings are now (on connect, and on every change). Person-only.</summary>
        public Task ReportHostSettingsAsync(string personality, bool sounds, int soundVolume)
        {
            lock (_hostSettingsGate)
            {
                _hostSettings = new HostSettingsView
                {
                    Personality = personality == "quiet" ? "quiet" : "on", Sounds = sounds,
                    SoundVolume = Math.Min(100, Math.Max(0, soundVolume)), Reported = true,
                };
            }
            return Task.CompletedTask;
        }

        /// <summary>start_trial: ask the window to start the free Pro month for this email. The person's yes in chat is the approval
        /// (<paramref name="personSaidYes"/>); without it nothing is sent. The window sends the confirm email, waits for the
        /// person to press the link, and turns Pro on by itself; get_entitlement shows when it has.</summary>
        public async Task<TrialStartResult> StartTrialAsync(string email, bool personSaidYes, string origin = "human")
        {
            GuardOutsideWrite(origin);
            var address = (email ?? "").Trim();
            if (address.Length > 254 || !TrialEmail.IsMatch(address))
                return new TrialStartResult { State = "badEmail", Message = "That does not look like an email address. Ask the person for the address they want the confirm email sent to." };
            if (!personSaidYes)
                return new TrialStartResult { State = "needsYes", Message = $"Nothing was sent. Ask the person in chat: \"Start your free Pro month? A confirm email goes to {address}.\" When they say yes, call start_trial again with personSaidYes true." };
            var now = await GetEntitlementAsync();
            if (string.Equals(now.Tier, "pro", StringComparison.OrdinalIgnoreCase))
                return new TrialStartResult { State = "alreadyPro", Message = "Pro is already on. Nothing was sent." };
            if (_sessions.Bus.HumanClientAttached?.Invoke() != true)
                return new TrialStartResult { State = "noWindow", Message = "The free month starts from the person's VS Code window, and none is open. Ask them to open Semanticus in VS Code, then call start_trial again." };
            _sessions.Bus.PublishHostStartTrial(address);
            return new TrialStartResult { State = "requested", Message = $"A confirm email is going to {address}. Tell the person to press the link in it. Pro turns on by itself; get_entitlement shows tier pro when it has." };
        }
    }
}
