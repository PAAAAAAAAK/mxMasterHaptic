namespace Loupedeck.MxHapticsPlugin.Haptics
{
    using System;

    /// <summary>
    /// The fixed catalogue of haptic waveforms the MX Master 4 can play.
    /// </summary>
    /// <remarks>
    /// These names are Logitech's own, kept verbatim on purpose: they are the
    /// vocabulary used by the SDK docs, by eventMapping.yaml, and by every other
    /// plugin. Inventing our own would mean maintaining a translation layer for
    /// no functional gain.
    ///
    /// The waveform set is FIXED - the SDK exposes no way to author custom
    /// waveforms, and no intensity or duration control. Selection is the only
    /// lever we have. The grouping below is ours, and reflects what actually
    /// decides where a waveform is usable: how long and how sharp it is.
    /// </remarks>
    internal static class Waveforms
    {
        // --- Click-grade: short, single, repeatable at speed ---------------------
        public const String SubtleCollision = "subtle_collision";
        public const String DampCollision = "damp_collision";
        public const String SharpCollision = "sharp_collision";
        public const String SharpStateChange = "sharp_state_change";
        public const String Square = "square";

        // --- Accent: short/medium, occasional ------------------------------------
        public const String DampStateChange = "damp_state_change";
        public const String HappyAlert = "happy_alert";
        public const String Knock = "knock";
        public const String Wave = "wave";

        // --- Event: long, attention-grabbing, rare -------------------------------
        public const String AngryAlert = "angry_alert";
        public const String Completed = "completed";
        public const String Firework = "firework";
        public const String Mad = "mad";
        public const String Jingle = "jingle";
        public const String Ringing = "ringing";

        /// <summary>All 15 waveforms, ordered subtlest -> most dramatic.</summary>
        /// <remarks>
        /// MUST stay in sync with package/events/DefaultEventSource.yaml and
        /// package/events/extra/eventMapping.yaml. A name present here but missing
        /// from the YAML raises an event Options+ has never heard of, and nothing
        /// happens - silently.
        /// </remarks>
        public static readonly String[] All =
        {
            SubtleCollision, DampCollision, SharpCollision, SharpStateChange, Square,
            DampStateChange, HappyAlert, Knock, Wave,
            AngryAlert, Completed, Firework, Mad, Jingle, Ringing,
        };

        /// <summary>
        /// How a waveform behaves, and therefore where it is usable.
        /// </summary>
        /// <remarks>
        /// Logitech groups these by intent ("alert", "state change"). That is the
        /// wrong axis for us: what actually decides whether a waveform works on a
        /// left click is how LONG and how SHARP it is. A click repeats thousands of
        /// times a day, so anything lengthy becomes intolerable; a rare system
        /// event can afford something elaborate.
        ///
        /// Surfaced in the settings dropdown so the choice is informed rather than
        /// a guess from the name. Deliberately advisory - nothing is forbidden,
        /// because feel is subjective and the hardware allows it.
        /// </remarks>
        public static String CharacterOf(String waveform) => waveform switch
        {
            SubtleCollision or DampCollision or SharpCollision or SharpStateChange or Square => "short",
            DampStateChange or HappyAlert or Knock or Wave => "medium",
            _ => "long",
        };

        /// <summary>True if a waveform is short enough to repeat on clicks and scroll.</summary>
        public static Boolean IsClickGrade(String waveform) => CharacterOf(waveform) == "short";

        /// <summary>
        /// Maps a waveform to the plugin event name that plays it.
        /// </summary>
        /// <remarks>
        /// eventMapping.yaml binds a waveform to an event NAME, statically, so one
        /// event can only ever play one waveform. Declaring one event per waveform
        /// turns that static binding into a palette we can choose from at runtime.
        /// </remarks>
        public static String EventNameFor(String waveform) => "hx_" + waveform;
    }
}
