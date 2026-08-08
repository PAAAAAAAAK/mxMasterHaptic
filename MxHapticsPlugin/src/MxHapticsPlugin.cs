namespace Loupedeck.MxHapticsPlugin
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;
    using Loupedeck.MxHapticsPlugin.Input;
    using Loupedeck.MxHapticsPlugin.SettingsUi;

    // This class contains the plugin-level logic of the Loupedeck plugin.

    public class MxHapticsPlugin : Plugin
    {
        // Gets a value indicating whether this is an API-only plugin.
        public override Boolean UsesApplicationApiOnly => true;

        // Gets a value indicating whether this is a Universal plugin or an Application plugin.
        // MUST stay true: a Universal plugin is not tied to a focused application,
        // which is what lets our haptics fire system-wide.
        public override Boolean HasNoApplication => true;

        private HapticOutput _haptics;
        private readonly List<IInputSource> _inputSources = new();

        /// <summary>
        /// Per-button haptic configuration. Exposed so bindable actions in
        /// Options+ can read and change it - that is the whole config surface,
        /// deliberately, instead of a companion app.
        /// </summary>
        internal HapticSettings Settings { get; private set; }

        private SettingsServer _settingsServer;

        /// <summary>
        /// Launches the bundled settings application.
        /// </summary>
        /// <remarks>
        /// The executable sits beside this assembly inside the plugin package, so
        /// there is still exactly one thing to install. If it is already running,
        /// starting it again is harmless: it detects the existing instance and
        /// surfaces that window instead of opening a second one.
        /// </remarks>
        internal void ShowSettingsWindow()
        {
            try
            {
                // Use the SDK's AssemblyFilePath, NOT Assembly.Location. The Plugin
                // Service loads plugins into a collectible load context, which
                // leaves Location empty - GetDirectoryName then returns null and
                // Path.Combine throws.
                var assemblyPath = this.AssemblyFilePath;

                var pluginDir = String.IsNullOrEmpty(assemblyPath)
                    ? null
                    : System.IO.Path.GetDirectoryName(assemblyPath);

                if (String.IsNullOrEmpty(pluginDir))
                {
                    PluginLog.Error("[MxHaptics] Could not determine the plugin directory.");
                    return;
                }

                var exePath = System.IO.Path.Combine(pluginDir, "MxHapticsSettings.exe");

                if (!System.IO.File.Exists(exePath))
                {
                    PluginLog.Error($"[MxHaptics] Settings application not found at '{exePath}'.");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = SettingsServer.PipeName,
                    UseShellExecute = false,
                    WorkingDirectory = pluginDir,
                });

                PluginLog.Info("[MxHaptics] Settings application launched.");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[MxHaptics] Failed to launch settings application: {ex.Message}");
            }
        }

        // Initializes a new instance of the plugin class.
        public MxHapticsPlugin()
        {
            // Initialize the plugin log.
            PluginLog.Init(this.Log);

            // Initialize the plugin resources.
            PluginResources.Init(this.Assembly);
        }

        // This method is called when the plugin is loaded.
        public override void Load()
        {
            // Register every waveform as its own event. Registration must happen
            // before any RaiseEvent call, or the event name is unknown to Options+.
            foreach (var waveform in Waveforms.All)
            {
                this.PluginEvents.AddEvent(
                    Waveforms.EventNameFor(waveform),
                    waveform,
                    $"Plays the '{waveform}' haptic waveform");
            }

            PluginLog.Info($"[MxHaptics] Registered {Waveforms.All.Length} haptic events.");

            this._haptics = new HapticOutput(this);
            this.Settings = new HapticSettings(this);

            // Serves the separate settings application. Started even when no
            // settings window is open, since the window may be launched at any time.
            this._settingsServer = new SettingsServer(this.Settings, this._haptics);
            this._settingsServer.Start();

            // STAGES 1-2: click and scroll haptics.
            //
            // Stage 0 proved RaiseEvent works from a background thread with nothing
            // bound. This is the payoff - a global mouse hook drives it, so haptics
            // fire in every application rather than only when a device button bound
            // in Logi Options+ is pressed.
            this._inputSources.Add(new MouseInputSource(this._haptics, this.Settings));

            foreach (var source in this._inputSources)
            {
                try
                {
                    source.Start();
                }
                catch (Exception ex)
                {
                    // One failed source must not take down the plugin: if the mouse
                    // hook cannot be installed we still want the plugin loaded and
                    // the failure visible in the log.
                    PluginLog.Error($"[MxHaptics] Input source '{source.Name}' failed to start: {ex}");
                }
            }
        }

        // This method is called when the plugin is unloaded.
        public override void Unload()
        {
            // Deterministic teardown matters here. The Plugin Service reloads this
            // assembly on every rebuild, and a leaked global hook would keep firing
            // from an unloaded copy - and SharpHook allows only ONE global hook per
            // process, so a leak would also stop the reloaded plugin from working.
            foreach (var source in this._inputSources)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[MxHaptics] Input source '{source.Name}' failed to dispose: {ex}");
                }
            }

            this._inputSources.Clear();

            // Stop serving settings: the pipe must be released before the reloaded
            // plugin tries to claim the same name.
            this._settingsServer?.Dispose();
            this._settingsServer = null;
            this._haptics = null;

            PluginLog.Info("[MxHaptics] Unloaded.");
        }
    }
}
