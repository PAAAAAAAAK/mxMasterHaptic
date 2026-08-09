namespace Loupedeck.MxHapticsPlugin.Input
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Makes SharpHook load its native library from outside the plugin folder.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS. SharpHook reaches libuiohook through DllImport, which
    /// loads uiohook.dll into the Logi Plugin Service process. Disposing the
    /// managed hook stops the hook but CANNOT unload that native library - .NET
    /// pins it for the life of the host process, and the Plugin Service outlives
    /// the plugin.
    ///
    /// If it is loaded from the plugin's own folder, Windows then refuses to
    /// delete the file, so uninstalling produces:
    ///
    ///   Access to the path 'uiohook.dll' is denied.
    ///   Plugin 'MxHaptics' uninstallation failed - Plugin was not fully uninstalled
    ///
    /// leaving a half-removed plugin behind. That happens to any user who
    /// uninstalls without first quitting Logi Options+.
    ///
    /// So the DLL is copied to a private directory and loaded from there instead.
    /// That copy stays pinned - harmless, since nothing needs to delete it - while
    /// the copy inside the plugin folder is never opened, and uninstall can remove
    /// the folder cleanly.
    /// </remarks>
    internal static class NativeLibraryRedirect
    {
        private const String LibraryName = "uiohook";

        private static Boolean _configured;

        /// <summary>
        /// Points SharpHook's native loads at a copy outside the plugin folder.
        /// Must run before any hook is created, since a native library cannot be
        /// redirected once it has been loaded.
        /// </summary>
        /// <param name="pluginBinDirectory">Folder holding the shipped uiohook.dll.</param>
        public static void Configure(String pluginBinDirectory)
        {
            if (_configured)
            {
                return;
            }

            _configured = true;

            try
            {
                var source = Path.Combine(pluginBinDirectory, LibraryName + ".dll");

                if (!File.Exists(source))
                {
                    PluginLog.Error($"[MxHaptics] Native library not found at '{source}'.");
                    return;
                }

                var target = PrepareCopy(source);

                if (target == null)
                {
                    return; // Fall back to default probing; logged in PrepareCopy.
                }

                // Resolves only our own library name; everything else falls through
                // to the runtime's normal probing.
                NativeLibrary.SetDllImportResolver(
                    typeof(SharpHook.SimpleGlobalHook).Assembly,
                    (name, assembly, searchPath) =>
                        name == LibraryName ? NativeLibrary.Load(target) : IntPtr.Zero);

                PluginLog.Info($"[MxHaptics] Native library redirected to '{target}'.");
            }
            catch (InvalidOperationException)
            {
                // A resolver is already registered for this assembly, which happens
                // if the plugin reloaded without SharpHook being unloaded. The
                // existing resolver already points outside the plugin folder.
            }
            catch (Exception ex)
            {
                // Never block loading over this: worst case the library loads from
                // the plugin folder as before, and only uninstall is affected.
                PluginLog.Error($"[MxHaptics] Could not redirect native library: {ex.Message}");
            }
        }

        /// <summary>
        /// Copies the native library to a private directory, returning its path.
        /// </summary>
        /// <remarks>
        /// The version is part of the path so an upgraded plugin never reuses an
        /// older DLL that a still-running process has pinned.
        /// </remarks>
        private static String PrepareCopy(String source)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
            var directory = Path.Combine(Path.GetTempPath(), "MxHaptics", "native", version);

            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, LibraryName + ".dll");

            try
            {
                // Copy only when needed. If a previous run already loaded this file
                // it will be locked, which is fine - it is the same build, so the
                // existing copy is exactly what we would have written.
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                {
                    File.Copy(source, target, overwrite: true);
                }

                return target;
            }
            catch (IOException)
            {
                // Locked by a previous load of the same version - reuse it.
                return File.Exists(target) ? target : null;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[MxHaptics] Could not stage native library: {ex.Message}");
                return null;
            }
        }
    }
}
