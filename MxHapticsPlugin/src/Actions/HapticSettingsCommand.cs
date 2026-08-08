namespace Loupedeck.MxHapticsPlugin.Actions
{
    using System;

    /// <summary>
    /// Opens the MX Haptics settings window.
    /// </summary>
    /// <remarks>
    /// This replaced an Action Editor based version. The Action Editor configures
    /// ONE action instance at a time and presents Save/Cancel semantics, which is
    /// the wrong model for global preferences - it made every event look like it
    /// needed its own binding, and Cancel could not actually undo anything because
    /// settings are stored globally.
    ///
    /// So the action does one job: open the settings window. Bind it once - to a
    /// spare key or an Actions Ring slot - and it covers every event, now and as
    /// later stages add more. One binding for the whole plugin.
    /// </remarks>
    public class HapticSettingsCommand : PluginDynamicCommand
    {
        public HapticSettingsCommand()
            : base(displayName: "Haptic Settings",
                   description: "Open MX Haptics settings",
                   groupName: "Haptics")
        {
        }

        protected override void RunCommand(String actionParameter) =>
            (this.Plugin as MxHapticsPlugin)?.ShowSettingsWindow();

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            "Haptic" + Environment.NewLine + "Settings";
    }
}
