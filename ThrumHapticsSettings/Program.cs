namespace ThrumHapticsSettings
{
    using System;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows.Forms;

    internal static class Program
    {
        private const String WindowTitle = "Thrum Haptics - Settings";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(String className, String windowName);

        [DllImport("user32.dll")]
        private static extern Boolean SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern Boolean ShowWindow(IntPtr hWnd, Int32 cmd);

        private const Int32 SW_RESTORE = 9;

        [STAThread]
        private static void Main(String[] args)
        {
            // The pipe name is passed by the plugin, so both sides agree on it
            // without either hard-coding a value the other might not share.
            var pipeName = args.Length > 0 ? args[0] : "ThrumHaptics.Settings." + Environment.UserName;

            // Single instance: pressing the bound action again should surface the
            // window already open, not stack up copies that disagree about state.
            using var singleInstance = new Mutex(true, @"Local\ThrumHapticsSettings", out var isFirst);

            if (!isFirst)
            {
                var existing = FindWindow(null, WindowTitle);

                if (existing != IntPtr.Zero)
                {
                    ShowWindow(existing, SW_RESTORE);
                    SetForegroundWindow(existing);
                }

                return;
            }

            ApplicationConfiguration.Initialize();

            using var client = SettingsClient.TryConnect(pipeName);

            if (client == null)
            {
                // Almost always means Options+ is not running, or the plugin is
                // mid-reload. Say which, rather than showing an empty window.
                MessageBox.Show(
                    "Could not reach the Thrum Haptics plugin." + Environment.NewLine + Environment.NewLine
                    + "Make sure Logi Options+ is running, then try again.",
                    "Thrum Haptics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var values = client.GetAll();

            var form = new SettingsForm(client, values);

            // Launched by a background service, so without this the window can
            // open behind whatever the user was looking at. Windows blocks a
            // background process from taking focus, so briefly marking it topmost
            // is what actually raises it.
            form.Shown += (_, _) =>
            {
                form.TopMost = true;
                form.Activate();
                form.TopMost = false;
            };

            Application.Run(form);
        }
    }
}
