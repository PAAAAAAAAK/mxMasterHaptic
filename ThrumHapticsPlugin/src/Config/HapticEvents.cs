namespace Loupedeck.ThrumHapticsPlugin.Config
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Loupedeck.ThrumHapticsPlugin.Haptics;

    /// <summary>
    /// One configurable thing that can trigger a haptic.
    /// </summary>
    /// <remarks>
    /// Config is keyed on EVENTS, not on mouse buttons. That distinction is what
    /// keeps the settings UI from growing a new control every time we add a
    /// feature: scroll detents, hovering a link, and a window snapping are all
    /// just more entries in <see cref="HapticEvents.All"/>. The editor renders a
    /// list of whatever is in that array, so later stages add rows rather than
    /// new actions, new bindings, or new UI.
    /// </remarks>
    internal sealed class HapticEventDef
    {
        /// <summary>Stable id, used as the settings key. Never change these - a
        /// rename silently orphans a user's saved preference.</summary>
        public String Id { get; }

        /// <summary>Group heading shown in the editor, e.g. "Clicks".</summary>
        public String Category { get; }

        public String DisplayName { get; }

        public String DefaultWaveform { get; }

        public Boolean DefaultEnabled { get; }

        /// <summary>
        /// Events that form one gesture, e.g. a drag's start and end.
        /// </summary>
        /// <remarks>
        /// Purely presentational. Grouped events stay INDEPENDENTLY toggleable and
        /// separately configurable on purpose - the same button does different jobs
        /// in different applications, and wanting the "grab" without the "let go"
        /// is a reasonable preference. The group only tells the settings window to
        /// show them as belonging together, so the relationship is visible without
        /// taking the choice away.
        /// </remarks>
        public String GroupKey { get; }

        /// <summary>
        /// Events the operating system cannot deliver on macOS.
        /// </summary>
        /// <remarks>
        /// Not a feature gate - these are physically undeliverable there, so a
        /// settings row for one would be a switch wired to nothing. See
        /// MacMouseInputSource and docs/macos-handoff.md fact 6 for the evidence.
        /// </remarks>
        public Boolean WindowsOnly { get; }

        public HapticEventDef(
            String id, String category, String displayName,
            String defaultWaveform, Boolean defaultEnabled = true, String groupKey = null,
            Boolean windowsOnly = false)
        {
            this.Id = id;
            this.Category = category;
            this.DisplayName = displayName;
            this.DefaultWaveform = defaultWaveform;
            this.DefaultEnabled = defaultEnabled;
            this.GroupKey = groupKey;
            this.WindowsOnly = windowsOnly;
        }

        /// <summary>Label shown in the editor's event dropdown.</summary>
        public String EditorLabel => $"{this.Category}: {this.DisplayName}";
    }

    /// <summary>
    /// The catalogue of everything that can fire a haptic.
    /// </summary>
    /// <remarks>
    /// GROWTH POINT: Stage 2 (scroll), Stage 3 (system events) and Stage 4 (hover)
    /// each append entries here. Nothing in the settings UI needs to change.
    /// </remarks>
    internal static class HapticEvents
    {
        // --- Stage 1: clicks -----------------------------------------------------
        public const String MouseLeft = "mouse.left";
        public const String MouseRight = "mouse.right";
        public const String MouseMiddle = "mouse.middle";
        public const String MouseBack = "mouse.back";
        public const String MouseForward = "mouse.forward";

        // --- Stage 2: scroll -----------------------------------------------------
        public const String ScrollVertical = "scroll.vertical";
        public const String ScrollHorizontal = "scroll.horizontal";

        // --- Stage 3: gestures and system events ---------------------------------
        // Drag is per-button: left is the everyday case, while middle and right
        // drag are orbit/pan gestures in CAD and 3D tools where constant haptics
        // would be unwelcome. Ids are stable - see HapticEventDef.Id.
        public const String DragLeftStart = "drag.left.start";
        public const String DragLeftEnd = "drag.left.end";
        public const String DragMiddleStart = "drag.middle.start";
        public const String DragMiddleEnd = "drag.middle.end";
        public const String DragRightStart = "drag.right.start";
        public const String DragRightEnd = "drag.right.end";
        public const String ScreenEdge = "cursor.screenEdge";

        // NOTE: hover-over-element feedback is deliberately absent, and it is NOT
        // a technical limitation - UI Automation can identify clickable elements
        // universally. It is left out because it fires on every element the
        // cursor lands on, including elements that arrive under a STATIONARY
        // cursor while scrolling a list, which no dwell delay can filter out.
        // Before attempting it, read this note's own commit history: it records
        // the two traps that made an earlier attempt look impossible.

        public static readonly HapticEventDef[] All =
        {
            new(MouseLeft, "Clicks", "Left click", Waveforms.SubtleCollision),
            new(MouseRight, "Clicks", "Right click", Waveforms.DampCollision),
            new(MouseMiddle, "Clicks", "Middle click", Waveforms.SharpCollision),

            // The haptic motor sits near the main click buttons, so a thumb resting
            // on the side buttons is further from the source and feels the same
            // waveform noticeably more weakly. These get the strongest click-grade
            // waveform to compensate, rather than simply being switched off.
            //
            // WINDOWS ONLY, and not by choice. On Windows these arrive as
            // XBUTTON1/XBUTTON2, ordinary mouse buttons the hook sees. On macOS
            // they never enter the HID interface at all - measured at four
            // observation points, down to raw HID reports, all absent - because
            // Options+ converts them over a private channel. Nothing to hook, at
            // any level, in any framework.
            //
            // They stay in the catalogue rather than being deleted so Windows keeps
            // working and saved preferences are not orphaned; ForCurrentPlatform
            // filters them out where they cannot fire.
            new(MouseBack, "Clicks", "Back (thumb)", Waveforms.SharpCollision, windowsOnly: true),
            new(MouseForward, "Clicks", "Forward (thumb)", Waveforms.SharpCollision, windowsOnly: true),

            // Scroll fires FAR more often than clicks, so it gets the lightest
            // waveform available - anything heavier becomes exhausting within
            // seconds. See ScrollCooldownMs in MouseInputSource for the rate limit
            // that stops fast scrolling turning into a continuous buzz.
            new(ScrollVertical, "Scroll", "Scroll wheel", Waveforms.SubtleCollision),

            // The thumb wheel gets a sharper waveform than the main wheel for two
            // reasons: it sits at the side of the mouse, further from the haptic
            // motor (the same reason the thumb buttons feel weak), and it has no
            // physical detents, so the haptic is the ONLY thing providing a sense
            // of discrete steps. A soft waveform there just reads as vibration.
            new(ScrollHorizontal, "Scroll", "Thumb wheel", Waveforms.SharpCollision),

            // Drag start/end bracket a gesture, so they want to feel like a pair:
            // a firmer "grab" and a softer "let go". Grouped for display, but each
            // stays independently switchable.
            new(DragLeftStart, "Gestures", "Drag (left)  -  start", Waveforms.SharpStateChange,
                groupKey: "drag.left"),
            new(DragLeftEnd, "Gestures", "Drag (left)  -  end", Waveforms.DampStateChange,
                groupKey: "drag.left"),

            // Middle and right drag are OFF by default. In CAD and 3D modelling
            // they are the orbit and pan gestures - held down and moved constantly -
            // so haptics there would be relentless for exactly the people who use
            // them most. Available for anyone who wants them, imposed on no one.
            new(DragMiddleStart, "Gestures", "Drag (middle)  -  start", Waveforms.SharpStateChange,
                defaultEnabled: false, groupKey: "drag.middle"),
            new(DragMiddleEnd, "Gestures", "Drag (middle)  -  end", Waveforms.DampStateChange,
                defaultEnabled: false, groupKey: "drag.middle"),
            new(DragRightStart, "Gestures", "Drag (right)  -  start", Waveforms.SharpStateChange,
                defaultEnabled: false, groupKey: "drag.right"),
            new(DragRightEnd, "Gestures", "Drag (right)  -  end", Waveforms.DampStateChange,
                defaultEnabled: false, groupKey: "drag.right"),

            // Hitting the edge of the desktop is the one place a mouse has a real
            // physical boundary with no feedback at all. Off by default: it fires
            // during ordinary cursor movement rather than on a deliberate action,
            // so it should be opted into rather than sprung on people.
            new(ScreenEdge, "Gestures", "Screen edge", Waveforms.SubtleCollision, defaultEnabled: false),
        };

        /// <summary>
        /// The events this operating system can actually deliver.
        /// </summary>
        /// <remarks>
        /// Use this for anything user-facing. <see cref="All"/> is the full
        /// catalogue and stays complete on every platform, so ids remain stable
        /// and a preference saved on one machine is never orphaned by opening the
        /// settings on another.
        ///
        /// This file is compiled into the plugin AND both settings applications,
        /// so all three filter identically without anything crossing the pipe.
        /// </remarks>
        public static readonly HapticEventDef[] ForCurrentPlatform =
            All.Where(e => !e.WindowsOnly || OperatingSystem.IsWindows()).ToArray();

        private static readonly Dictionary<String, HapticEventDef> ById =
            All.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        public static HapticEventDef Find(String id) =>
            id != null && ById.TryGetValue(id, out var def) ? def : null;
    }
}
