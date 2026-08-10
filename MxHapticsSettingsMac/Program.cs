namespace MxHapticsSettingsMac
{
    using System;
    using System.Threading;

    using Avalonia;
    using Avalonia.Controls;
    using Avalonia.Controls.ApplicationLifetimes;
    using Avalonia.Themes.Fluent;

    using MxHapticsSettings;

    /// <summary>
    /// Entry point for the macOS settings window.
    /// </summary>
    /// <remarks>
    /// Mirrors the Windows Program.cs: take the pipe name from the plugin, refuse
    /// to open twice, connect before showing anything, and fail loudly rather than
    /// presenting an empty window if the plugin cannot be reached.
    /// </remarks>
    internal static class Program
    {
        private static void Main(String[] args)
        {
            // The pipe name is passed by the plugin, so both sides agree on it
            // without either hard-coding a value the other might not share.
            var pipeName = args.Length > 0 ? args[0] : "MxHaptics.Settings." + Environment.UserName;

            // Single instance: pressing the bound action again must not stack up
            // copies that disagree about state. The plugin's pipe server accepts
            // ONE connection at a time, so a second window could not read settings
            // anyway - it would show the "cannot reach the plugin" path and look
            // like a fault rather than a duplicate.
            //
            // Unlike Windows there is no FindWindow to raise the existing window
            // with, so a second launch exits quietly. Worth revisiting if it proves
            // annoying in use.
            using var singleInstance = new Mutex(true, "MxHapticsSettingsMac", out var isFirst);

            if (!isFirst)
            {
                Console.WriteLine("settings window already open; not opening a second one");
                return;
            }

            var client = SettingsClient.TryConnect(pipeName);

            if (client == null)
            {
                // Written to stderr rather than shown in a dialog: the plugin
                // captures both streams, so this lands in the plugin log where
                // anyone diagnosing it is already looking.
                Console.Error.WriteLine(
                    "Could not reach the MX Haptics plugin over pipe '" + pipeName
                    + "'. Is Logi Options+ running?");

                return;
            }

            try
            {
                App.Client = client;
                App.Values = client.GetAll();

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                client.Dispose();
            }
        }

        private static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // Launched by a background service rather than by the user, so
                // without a Dock presence the window can open behind whatever they
                // were looking at, with no way to bring it forward.
                .With(new MacOSPlatformOptions { ShowInDock = true });
    }

    internal sealed class App : Application
    {
        // Avalonia constructs this itself and requires a parameterless
        // constructor, so the connection is handed over statically.
        public static SettingsClient Client;

        public static System.Collections.Generic.Dictionary<String, (Boolean Enabled, String Waveform)> Values;

        public override void Initialize() =>
            // Fluent follows the system light/dark appearance, so the window
            // matches macOS without us tracking the theme ourselves.
            this.Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new SettingsWindow(Client, Values);

                window.Opened += (_, _) => window.Activate();

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
