namespace Loupedeck.MxHapticsPlugin.SettingsUi
{
    using System;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    /// <summary>
    /// Serves settings to the separate settings application over a local pipe.
    /// </summary>
    /// <remarks>
    /// WHY THE SETTINGS UI IS A SEPARATE PROCESS. The Logi plugin verifier
    /// inspects the plugin assembly with a metadata resolver that cannot load
    /// desktop framework assemblies, so any reference to System.Windows.Forms in
    /// THIS assembly fails `logiplugintool verify` and blocks Marketplace
    /// submission. Moving the window into its own executable keeps this assembly
    /// free of desktop references. It also means a crash in the settings UI can no
    /// longer take down the Plugin Service - and with it every haptic.
    ///
    /// The plugin stays the owner of the settings: only it can reach the SDK's
    /// setting store, and only it can raise haptic events. The settings app is a
    /// thin client that asks this server to read, change, and preview.
    ///
    /// A NAMED PIPE, not a socket. It is local IPC that never touches the network
    /// stack, so the plugin still opens no listening network port - which matters
    /// both for the antivirus profile of a process holding an input hook, and for
    /// the promise that this plugin makes no network connections.
    /// </remarks>
    internal sealed class SettingsServer : IDisposable
    {
        private readonly HapticSettings _settings;
        private readonly HapticOutput _haptics;

        private CancellationTokenSource _cancellation;
        private Task _acceptLoop;

        /// <summary>
        /// Pipe name, scoped to the current user.
        /// </summary>
        /// <remarks>
        /// Named pipes share one machine-wide namespace, so a fixed name would
        /// collide between users on a shared machine. The default pipe ACL already
        /// restricts access to the creating user; the name keeps them from
        /// fighting over it in the first place.
        /// </remarks>
        public static String PipeName => "MxHaptics.Settings." + Environment.UserName;

        public SettingsServer(HapticSettings settings, HapticOutput haptics)
        {
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
        }

        public void Start()
        {
            if (this._acceptLoop != null)
            {
                return;
            }

            this._cancellation = new CancellationTokenSource();
            this._acceptLoop = Task.Run(() => this.AcceptLoopAsync(this._cancellation.Token));

            PluginLog.Info($"[MxHaptics] Settings server listening on pipe '{PipeName}'.");
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // One instance: only ever one settings window at a time, and
                    // serialising access avoids two clients racing on the store.
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await this.ServeClientAsync(pipe, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return; // Normal shutdown.
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[MxHaptics] Settings server error: {ex.Message}");

                    // Back off briefly so a persistent fault cannot spin this loop.
                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };

            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                if (line == null)
                {
                    return; // Client disconnected.
                }

                var parts = line.Split('|');

                switch (parts[0])
                {
                    case "GET":
                        // Values only. Both sides compile the same HapticEvents
                        // catalogue, so the shape of the list never has to be sent.
                        this._settings.Reload();

                        // ForCurrentPlatform, not All: an event the OS cannot
                        // deliver here would arrive as a settings row wired to
                        // nothing. The plugin and both settings applications
                        // compile the same catalogue, so they filter identically.
                        foreach (var def in HapticEvents.ForCurrentPlatform)
                        {
                            await writer.WriteLineAsync(
                                $"{def.Id}|{this._settings.IsEnabled(def.Id)}|{this._settings.WaveformFor(def.Id)}")
                                .ConfigureAwait(false);
                        }

                        await writer.WriteLineAsync(".").ConfigureAwait(false);
                        break;

                    case "SET" when parts.Length >= 4:
                        this._settings.SetEnabled(parts[1], Boolean.TryParse(parts[2], out var on) && on);
                        this._settings.SetWaveform(parts[1], parts[3]);
                        await writer.WriteLineAsync("OK").ConfigureAwait(false);
                        break;

                    case "PLAY" when parts.Length >= 2:
                        // Preview has to come back through the plugin: raising a
                        // haptic event is something only the hosted plugin can do.
                        this._haptics.Play(parts[1]);
                        await writer.WriteLineAsync("OK").ConfigureAwait(false);
                        break;

                    case "PING":
                        // Lets the settings application detect that the plugin has
                        // gone away. Without it, that process outlives an unload and
                        // keeps its own executable locked inside the plugin folder -
                        // which blocks Logi Options+ from deleting the folder during
                        // uninstall, leaving a half-removed plugin behind.
                        await writer.WriteLineAsync("OK").ConfigureAwait(false);
                        break;

                    case "BYE":
                        return;

                    default:
                        await writer.WriteLineAsync("ERR").ConfigureAwait(false);
                        break;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                this._cancellation?.Cancel();

                // Bounded wait: a plugin reload must not block on a client that is
                // slow to disconnect.
                this._acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Cancellation surfaces as an aggregate exception; nothing to do.
            }
            finally
            {
                this._cancellation?.Dispose();
                this._cancellation = null;
                this._acceptLoop = null;

                PluginLog.Info("[MxHaptics] Settings server stopped.");
            }
        }
    }
}
