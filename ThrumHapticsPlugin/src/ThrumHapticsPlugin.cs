namespace Loupedeck.ThrumHapticsPlugin
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.ThrumHapticsPlugin.Config;
    using Loupedeck.ThrumHapticsPlugin.Haptics;
    using Loupedeck.ThrumHapticsPlugin.Input;
    using Loupedeck.ThrumHapticsPlugin.SettingsUi;

    // This class contains the plugin-level logic of the Loupedeck plugin.

    public class ThrumHapticsPlugin : Plugin
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
                    PluginLog.Error("[ThrumHaptics] Could not determine the plugin directory.");
                    return;
                }

                // Separate executables per platform. The Windows one is WinForms and
                // cannot run on macOS; the macOS one is built for osx-arm64 and has
                // no .exe suffix.
                var exePath = System.IO.Path.Combine(
                    pluginDir,
                    OperatingSystem.IsMacOS() ? "ThrumHapticsSettingsMac" : "ThrumHapticsSettings.exe");

                if (!System.IO.File.Exists(exePath))
                {
                    PluginLog.Error($"[ThrumHaptics] Settings application not found at '{exePath}'.");
                    return;
                }

                EnsureExecutable(exePath);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = SettingsServer.PipeName,
                    UseShellExecute = false,
                    WorkingDirectory = pluginDir,
                };

                // PROBE INSTRUMENTATION, macOS only - remove once the launch path is
                // proven. Two different failures are possible here and they need
                // telling apart: a lost execute bit throws on Start, while Gatekeeper
                // refusing an ad-hoc signed binary lets it start and then kills it.
                // Read asynchronously so a UI process that never exits cannot block us.
                if (OperatingSystem.IsMacOS())
                {
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                }

                // Started BEFORE Process.Start so the settings app's own "entered
                // Main at 0ms" can be placed on this clock. The gap between them is
                // the pre-main cost - .NET host startup, which is precisely the part
                // ReadyToRun would address and the part the app cannot measure from
                // inside itself.
                var launchClock = System.Diagnostics.Stopwatch.StartNew();

                var process = System.Diagnostics.Process.Start(startInfo);

                if (OperatingSystem.IsMacOS() && process != null)
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (!String.IsNullOrWhiteSpace(e.Data))
                        {
                            PluginLog.Info(
                                $"[ThrumHaptics] settings-app stdout (+{launchClock.ElapsedMilliseconds}ms): {e.Data}");
                        }
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!String.IsNullOrWhiteSpace(e.Data))
                        {
                            PluginLog.Error($"[ThrumHaptics] settings-app stderr: {e.Data}");
                        }
                    };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }

                PluginLog.Info($"[ThrumHaptics] Settings application launched (pid {process?.Id}).");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Failed to launch settings application: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the execute bit on the settings application, on Unix.
        /// </summary>
        /// <remarks>
        /// A .lplug4 is a zip, and ours is written on Windows - which has no Unix
        /// mode to store. If the bit does not survive the round trip, the extracted
        /// file cannot be exec'd at all and the settings window simply never opens,
        /// with nothing but a permissions error to show for it.
        ///
        /// CONFIRMED REAL on device: the extracted file came out as
        /// "OtherRead, GroupRead, UserWrite, UserRead" - no execute bit anywhere -
        /// so without this the settings window would never open on macOS, with
        /// nothing but a permissions error to explain it.
        ///
        /// Cheap to make unconditional rather than conditional on having detected
        /// the problem: reading and setting a file mode costs nothing next to
        /// launching a process, and it keeps working if the packaging tool's
        /// behaviour ever changes underneath us.
        /// </remarks>
        private static void EnsureExecutable(String path)
        {
            if (OperatingSystem.IsWindows())
            {
                return; // No Unix mode to set, and the APIs throw here.
            }

            try
            {
                var mode = System.IO.File.GetUnixFileMode(path);
                const System.IO.UnixFileMode Execute =
                    System.IO.UnixFileMode.UserExecute
                    | System.IO.UnixFileMode.GroupExecute
                    | System.IO.UnixFileMode.OtherExecute;

                if ((mode & Execute) == Execute)
                {
                    PluginLog.Info($"[ThrumHaptics] Settings application already executable (mode {mode}).");
                    return;
                }

                System.IO.File.SetUnixFileMode(path, mode | Execute);

                PluginLog.Info(
                    $"[ThrumHaptics] Settings application was NOT executable (mode {mode}); "
                    + $"execute bit added -> {System.IO.File.GetUnixFileMode(path)}.");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[ThrumHaptics] Could not set the execute bit on '{path}': {ex.Message}");
            }
        }

        // Initializes a new instance of the plugin class.
        public ThrumHapticsPlugin()
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

            PluginLog.Info($"[ThrumHaptics] Registered {Waveforms.All.Length} haptic events.");

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
            // MUST happen before any hook is created. SharpHook's native library
            // cannot be unloaded once mapped into the Plugin Service process, so
            // loading it from the plugin folder would lock a file there and make
            // uninstalling the plugin fail. No-ops off Windows, where SharpHook is
            // not used at all - see MacMouseInputSource for why.
            NativeLibraryRedirect.Configure(System.IO.Path.GetDirectoryName(this.AssemblyFilePath));

            // The two implementations are NOT interchangeable and the difference is
            // not a portability convenience. SharpHook cannot be loaded into the
            // Plugin Service on macOS at all: that process runs with the hardened
            // runtime, so library validation refuses any dylib not signed by Apple
            // or by Logitech. macOS therefore taps CoreGraphics directly and ships
            // no native binary of ours. MacMouseInputSource documents this in full.
            this._inputSources.Add(OperatingSystem.IsMacOS()
                ? new MacMouseInputSource(this._haptics, this.Settings)
                : new MouseInputSource(this._haptics, this.Settings));

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
                    PluginLog.Error($"[ThrumHaptics] Input source '{source.Name}' failed to start: {ex}");
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
                    PluginLog.Error($"[ThrumHaptics] Input source '{source.Name}' failed to dispose: {ex}");
                }
            }

            this._inputSources.Clear();

            // Stop serving settings: the pipe must be released before the reloaded
            // plugin tries to claim the same name.
            this._settingsServer?.Dispose();
            this._settingsServer = null;
            this._haptics = null;

            PluginLog.Info("[ThrumHaptics] Unloaded.");
        }
    }
}
