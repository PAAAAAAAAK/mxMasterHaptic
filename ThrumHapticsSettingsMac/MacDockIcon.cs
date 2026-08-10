namespace ThrumHapticsSettingsMac
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Sets the macOS Dock icon for this process.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NEEDED AT ALL. macOS normally takes the Dock icon from a bundle's
    /// Info.plist, and this application is a bare executable rather than a .app -
    /// the Plugin Service launches it by path from inside the plugin folder. With no
    /// bundle there is no icon to read, so the Dock falls back to the generic
    /// "exec" tile.
    ///
    /// Shipping a real .app bundle would fix it properly, but it changes the shape
    /// of the package, and packaging is the one part of this project that took eight
    /// failed attempts to get right. Setting the icon at runtime touches nothing
    /// else.
    ///
    /// Only Apple's own libraries are called - libobjc and, indirectly, AppKit - so
    /// nothing here has to survive the hardened runtime's library validation the way
    /// a bundled dylib would. That is the same constraint that shaped the whole
    /// macOS input path; see MacMouseInputSource.
    /// </remarks>
    internal static class MacDockIcon
    {
        private const String ObjC = "/usr/lib/libobjc.dylib";

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] String name);

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] String name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendString(
            IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] String argument);

        /// <summary>The icon file shipped beside this executable, or null.</summary>
        public static String IconPath
        {
            get
            {
                var path = Path.Combine(AppContext.BaseDirectory, "AppIcon.png");

                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// Replaces the generic Dock tile with the plugin's icon.
        /// </summary>
        /// <remarks>
        /// MUST run after Avalonia has initialised: it asks AppKit for the shared
        /// NSApplication, and before Avalonia starts there is no application object
        /// to ask. Failure is swallowed - a wrong Dock icon is not worth failing to
        /// open the settings window over.
        /// </remarks>
        public static void Apply()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            try
            {
                var path = IconPath;

                if (path == null)
                {
                    return;
                }

                var nsString = SendString(
                    GetClass("NSString"), GetSelector("stringWithUTF8String:"), path);

                if (nsString == IntPtr.Zero)
                {
                    return;
                }

                var image = Send(
                    Send(GetClass("NSImage"), GetSelector("alloc")),
                    GetSelector("initWithContentsOfFile:"),
                    nsString);

                if (image == IntPtr.Zero)
                {
                    return;
                }

                var application = Send(GetClass("NSApplication"), GetSelector("sharedApplication"));

                if (application != IntPtr.Zero)
                {
                    Send(application, GetSelector("setApplicationIconImage:"), image);
                }
            }
            catch (Exception ex)
            {
                // Captured by the plugin, which reads this process's stdout/stderr.
                Console.Error.WriteLine("Could not set the Dock icon: " + ex.Message);
            }
        }
    }
}
