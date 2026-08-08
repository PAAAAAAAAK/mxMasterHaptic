namespace Loupedeck.MxHapticsPlugin.SettingsUi
{
    using System;
    using System.Threading;
    using System.Windows.Forms;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    /// <summary>
    /// Owns the settings window's lifetime and its UI thread.
    /// </summary>
    /// <remarks>
    /// We are a class library loaded into LogiPluginService, a background process
    /// whose threads are neither STA nor running a message pump. WinForms needs
    /// both, so the window gets its own dedicated STA thread with its own
    /// Application.Run loop.
    ///
    /// Everything here is defensive on purpose. A plugin fault can take the whole
    /// Plugin Service down - we watched a native DLL load failure kill it outright
    /// during Stage 1 - and losing the service would stop haptics everywhere, not
    /// just break a settings dialog. So no exception from the UI is ever allowed
    /// to escape into the host.
    /// </remarks>
    internal sealed class SettingsWindowHost : IDisposable
    {
        private readonly HapticSettings _settings;
        private readonly HapticOutput _haptics;
        private readonly Object _gate = new();

        private Thread _uiThread;
        private SettingsForm _form;

        public SettingsWindowHost(HapticSettings settings, HapticOutput haptics)
        {
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
        }

        /// <summary>
        /// Shows the settings window, or brings it to the front if already open.
        /// </summary>
        /// <remarks>
        /// Called from the plugin's action-execution thread, so it must return
        /// immediately - blocking here would stall the Plugin Service.
        /// </remarks>
        public void Show()
        {
            lock (this._gate)
            {
                try
                {
                    // Already open: bring it forward rather than opening a second
                    // copy, which would let two windows disagree about state.
                    if (this._form is { IsDisposed: false })
                    {
                        var existing = this._form;

                        existing.BeginInvoke(new Action(() =>
                        {
                            if (existing.WindowState == FormWindowState.Minimized)
                            {
                                existing.WindowState = FormWindowState.Normal;
                            }

                            existing.Activate();
                            existing.BringToFront();
                        }));

                        return;
                    }

                    this.StartUiThread();
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[MxHaptics] Failed to open settings window: {ex}");
                }
            }
        }

        private void StartUiThread()
        {
            var ready = new ManualResetEventSlim(false);

            this._uiThread = new Thread(() =>
            {
                try
                {
                    // Re-read from the store on open so the window always reflects
                    // current state, even if something changed it since load.
                    this._settings.Reload();

                    this._form = new SettingsForm(this._settings, this._haptics);
                    this._form.FormClosed += (_, _) => this._form = null;

                    ready.Set();

                    // Blocks this thread only, pumping messages until the form closes.
                    Application.Run(this._form);
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[MxHaptics] Settings window crashed: {ex}");
                }
                finally
                {
                    ready.Set(); // Never leave Show() waiting if construction threw.
                    this._form = null;
                }
            })
            {
                // STA is required by WinForms. IsBackground means this thread can
                // never keep the Plugin Service alive at shutdown.
                IsBackground = true,
                Name = "MxHaptics.SettingsUi",
            };

            this._uiThread.SetApartmentState(ApartmentState.STA);
            this._uiThread.Start();

            // Brief bounded wait purely so a failure is logged promptly; we do not
            // depend on the window being ready.
            ready.Wait(TimeSpan.FromSeconds(5));

            PluginLog.Info("[MxHaptics] Settings window opened.");
        }

        public void Dispose()
        {
            lock (this._gate)
            {
                try
                {
                    var form = this._form;

                    if (form is { IsDisposed: false })
                    {
                        // Close on the UI thread that owns it. The plugin reloads on
                        // every rebuild, and a window left behind would belong to an
                        // unloaded assembly.
                        form.BeginInvoke(new Action(() => form.Close()));
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[MxHaptics] Failed to close settings window: {ex}");
                }
                finally
                {
                    this._form = null;
                    this._uiThread = null;
                }
            }
        }
    }
}
