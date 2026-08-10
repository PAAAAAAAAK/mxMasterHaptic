namespace Loupedeck.ThrumHapticsPlugin.Input
{
    using System;

    /// <summary>
    /// A source of user-interaction events that can trigger haptics.
    /// </summary>
    /// <remarks>
    /// This interface exists to keep OS-specific code at the edges. Clicks and
    /// scroll are portable (SharpHook covers Windows, macOS and Linux), but later
    /// sources are not: Windows system events use WinEvent hooks and hover
    /// detection uses UI Automation, neither of which has a macOS analogue -
    /// macOS would need AXUIElement instead.
    ///
    /// Sources own their own lifecycle and must tear down cleanly: the Plugin
    /// Service reloads this assembly on every rebuild, and a leaked global hook
    /// would keep firing from an unloaded copy of the plugin.
    /// </remarks>
    internal interface IInputSource : IDisposable
    {
        /// <summary>Human-readable name, used in logs.</summary>
        String Name { get; }

        /// <summary>
        /// Begins delivering events. Must not block - it is called during plugin
        /// load, and a slow Start would stall the Plugin Service.
        /// </summary>
        void Start();

        /// <summary>Stops delivering events. Must be safe to call more than once.</summary>
        void Stop();
    }
}
