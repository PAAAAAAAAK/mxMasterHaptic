namespace Loupedeck.MxHapticsPlugin.Haptics
{
    using System;

    /// <summary>
    /// The single place where haptics actually leave this plugin.
    /// </summary>
    /// <remarks>
    /// Everything upstream (input sources, rules) deals in waveform names and
    /// never touches the SDK. That isolation is deliberate: if the official
    /// route ever proves too slow or gets blocked, swapping to direct HID++
    /// (vendor collection COL02, feature 0x0B4E, waveform IDs 0-14) is a change
    /// to this one file rather than a rewrite.
    /// </remarks>
    internal sealed class HapticOutput
    {
        private readonly Plugin _plugin;

        public HapticOutput(Plugin plugin) =>
            this._plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

        /// <summary>
        /// Plays a waveform. Safe to call from any thread.
        /// </summary>
        /// <remarks>
        /// This is the crux of the whole project. The SDK tutorial only ever calls
        /// RaiseEvent from inside an action's RunCommand, which makes it look like
        /// haptics require a device button bound in Logi Options+. They don't -
        /// RaiseEvent is an ordinary method call, and Stage 0 confirmed on-device
        /// that firing it from a ThreadPool thread with nothing bound works.
        /// That is what lets a global mouse hook drive haptics in every app.
        /// </remarks>
        public void Play(String waveform)
        {
            try
            {
                this._plugin.PluginEvents.RaiseEvent(Waveforms.EventNameFor(waveform));
            }
            catch (Exception ex)
            {
                // Never let a haptic failure propagate into an input-hook callback.
                // Windows silently evicts a low-level hook whose callback misbehaves
                // or runs long, which would kill every haptic rather than just one.
                PluginLog.Error($"[MxHaptics] Failed to play '{waveform}': {ex.Message}");
            }
        }
    }
}
