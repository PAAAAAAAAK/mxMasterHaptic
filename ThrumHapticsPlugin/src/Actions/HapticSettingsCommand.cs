namespace Loupedeck.ThrumHapticsPlugin.Actions
{
    using System;

    /// <summary>
    /// Opens the Thrum Haptics settings window.
    /// </summary>
    /// <remarks>
    /// The window is a separate executable bundled in the same plugin package, so
    /// this launches a process rather than showing a form. From the user's side
    /// nothing differs: bind this action once, press it, settings appear.
    ///
    /// It is one action rather than one per setting deliberately. An earlier
    /// version exposed each toggle as its own bindable parameter, which spent a
    /// scarce Actions Ring slot per option; and an Action Editor version was worse
    /// still, since that editor configures a single bound action and offers
    /// Save/Cancel semantics that global preferences cannot honour.
    /// </remarks>
    public class HapticSettingsCommand : PluginDynamicCommand
    {
        public HapticSettingsCommand()
            : base(displayName: "Haptic Settings",
                   description: "Open Thrum Haptics settings",
                   groupName: "Haptics")
        {
        }

        protected override void RunCommand(String actionParameter) =>
            (this.Plugin as ThrumHapticsPlugin)?.ShowSettingsWindow();

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) =>
            "Haptic" + Environment.NewLine + "Settings";
    }
}
