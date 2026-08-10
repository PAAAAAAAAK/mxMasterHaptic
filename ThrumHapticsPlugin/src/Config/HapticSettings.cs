namespace Loupedeck.ThrumHapticsPlugin.Config
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.ThrumHapticsPlugin.Haptics;

    /// <summary>
    /// Per-event haptic configuration, persisted by the Logi Plugin Service.
    /// </summary>
    /// <remarks>
    /// Uses the SDK's own settings store (SetPluginSetting/TryGetPluginSetting)
    /// rather than a hand-rolled file, and stores values GLOBALLY rather than
    /// against a bound action. That matters: the settings editor is reached
    /// through an action, but the preferences must not belong to that binding -
    /// unbinding it should never wipe your configuration.
    ///
    /// Values are cached in memory because they are read on the mouse-hook path,
    /// which must stay fast: Windows evicts a low-level hook whose callback runs
    /// long, and a hook evicted mid-session just silently stops working.
    /// </remarks>
    internal sealed class HapticSettings
    {
        private readonly Plugin _plugin;
        private readonly Dictionary<String, Boolean> _enabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, String> _waveforms = new(StringComparer.OrdinalIgnoreCase);

        public HapticSettings(Plugin plugin)
        {
            this._plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.Reload();
        }

        private static String EnabledKey(String eventId) => $"event.{eventId}.enabled";

        private static String WaveformKey(String eventId) => $"event.{eventId}.waveform";

        /// <summary>Reads every known event's settings into the in-memory cache.</summary>
        public void Reload()
        {
            foreach (var def in HapticEvents.All)
            {
                this._enabled[def.Id] =
                    this._plugin.TryGetPluginSetting(EnabledKey(def.Id), out var rawEnabled)
                    && Boolean.TryParse(rawEnabled, out var parsed)
                        ? parsed
                        : def.DefaultEnabled;

                // Guard against a stale or hand-edited waveform name. An unknown
                // name would raise an event Options+ has never heard of, and the
                // haptic would silently stop firing with no error anywhere.
                this._waveforms[def.Id] =
                    this._plugin.TryGetPluginSetting(WaveformKey(def.Id), out var rawWaveform)
                    && Array.IndexOf(Waveforms.All, rawWaveform) >= 0
                        ? rawWaveform
                        : def.DefaultWaveform;
            }
        }

        public Boolean IsEnabled(String eventId) =>
            eventId != null && this._enabled.TryGetValue(eventId, out var value) && value;

        public String WaveformFor(String eventId)
        {
            if (eventId != null && this._waveforms.TryGetValue(eventId, out var value))
            {
                return value;
            }

            return HapticEvents.Find(eventId)?.DefaultWaveform ?? Waveforms.SubtleCollision;
        }

        public void SetEnabled(String eventId, Boolean enabled)
        {
            if (HapticEvents.Find(eventId) == null)
            {
                return;
            }

            this._enabled[eventId] = enabled;
            this._plugin.SetPluginSetting(EnabledKey(eventId), enabled.ToString());
            PluginLog.Info($"[ThrumHaptics] {eventId} {(enabled ? "enabled" : "disabled")}.");
        }

        public void SetWaveform(String eventId, String waveform)
        {
            if (HapticEvents.Find(eventId) == null)
            {
                return;
            }

            if (Array.IndexOf(Waveforms.All, waveform) < 0)
            {
                PluginLog.Error($"[ThrumHaptics] Refusing unknown waveform '{waveform}'.");
                return;
            }

            this._waveforms[eventId] = waveform;
            this._plugin.SetPluginSetting(WaveformKey(eventId), waveform);
            PluginLog.Info($"[ThrumHaptics] {eventId} -> {waveform}.");
        }
    }
}
