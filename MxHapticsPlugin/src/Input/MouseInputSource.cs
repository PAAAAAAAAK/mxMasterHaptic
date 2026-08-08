namespace Loupedeck.MxHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    using SharpHook;
    using SharpHook.Data;

    /// <summary>
    /// Fires haptics on mouse buttons and scroll, in every application.
    /// </summary>
    /// <remarks>
    /// Buttons and scroll live in ONE source deliberately. SharpHook allows only a
    /// single IGlobalHook per process - libuiohook keeps its callback in static
    /// state, so starting a second hook corrupts the first. Splitting these into
    /// two IInputSource implementations would mean two hooks and a broken plugin,
    /// so anything driven by the global mouse hook belongs here. Stage 4's hover
    /// detection uses UI Automation instead, so that genuinely does get its own
    /// source.
    /// </remarks>
    internal sealed class MouseInputSource : IInputSource
    {
        private readonly HapticOutput _haptics;
        private readonly HapticSettings _settings;
        private IGlobalHook _hook;
        private Boolean _disposed;

        /// <summary>
        /// Minimum gap between two scroll haptics, per direction.
        /// </summary>
        /// <remarks>
        /// A scroll wheel produces events far faster than a human can register
        /// distinct taps, and the MX Master's free-spin mode can emit hundreds per
        /// second. Without a floor here the motor is asked to play a new waveform
        /// before the previous one has finished, which stops feeling like discrete
        /// detents and turns into one continuous buzz.
        ///
        /// 50ms caps it at ~20/second, which is roughly the fastest a ratchet still
        /// reads as separate ticks. Tuned by feel - adjust if fast scrolling still
        /// smears together.
        /// </remarks>
        private const Int64 ScrollCooldownMs = 50;

        /// <summary>
        /// Rotation units that count as one synthesized detent on the thumb wheel.
        /// </summary>
        /// <remarks>
        /// The vertical wheel has PHYSICAL detents, so in ratchet mode the hardware
        /// paces the events for us and a time-based cooldown is enough. The thumb
        /// wheel has no detents at all - it rolls smoothly - so pacing it by time
        /// produces a constant buzz at a fixed rate no matter how fast you roll it.
        /// That reads as vibration rather than ticks.
        ///
        /// Pacing by DISTANCE instead synthesizes the detents the hardware lacks:
        /// roll slowly and ticks come slowly, roll fast and they come fast, which is
        /// how a real ratchet behaves.
        ///
        /// MEASURED ON DEVICE: this mouse reports rotation = +/-360 per thumb-wheel
        /// event (delta = 120, the standard WHEEL_DELTA, so rotation runs at 3x
        /// that). One event therefore equals one scroll unit, and the wheel emits
        /// them faster than they read as separate taps - which is why an earlier
        /// threshold of 120, being below a single event's rotation, fired on every
        /// event and produced a continuous buzz.
        ///
        /// 1080 = 3 scroll units per tick, spacing ticks far enough apart to feel
        /// like discrete detents. This is THE tuning knob for thumb-wheel feel:
        /// lower for more ticks, higher for fewer.
        /// </remarks>
        private const Int32 ThumbWheelDetentUnits = 1080;

        private readonly Dictionary<String, Int64> _lastFiredMs = new();
        private Int32 _thumbWheelAccumulator;

        public String Name => "Mouse";

        public MouseInputSource(HapticOutput haptics, HapticSettings settings)
        {
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Maps SharpHook's physical button numbering onto our event ids.
        /// </summary>
        /// <remarks>
        /// SharpHook numbers buttons physically, not semantically:
        ///   Button1 = left, Button2 = right, Button3 = middle/wheel,
        ///   Button4 = back (thumb), Button5 = forward (thumb).
        /// Everything downstream deals in event ids, so the settings store and the
        /// editor never need to know about SharpHook's enum.
        /// </remarks>
        private static String EventIdFor(MouseButton button) => button switch
        {
            MouseButton.Button1 => HapticEvents.MouseLeft,
            MouseButton.Button2 => HapticEvents.MouseRight,
            MouseButton.Button3 => HapticEvents.MouseMiddle,
            MouseButton.Button4 => HapticEvents.MouseBack,
            MouseButton.Button5 => HapticEvents.MouseForward,
            _ => null,
        };

        public void Start()
        {
            if (this._hook != null)
            {
                return;
            }

            // GlobalHookType.Mouse is a hard requirement, not a preference.
            //
            // On Windows this installs ONLY the low-level mouse hook (WH_MOUSE_LL).
            // The alternative, GlobalHookType.All, would also install WH_KEYBOARD_LL
            // - the exact API keyloggers use, which sees every keystroke including
            // passwords. That is the single biggest reason security software flags
            // software like this. We need button states and coordinates, never
            // keystrokes, so we never ask for the keyboard hook at all.
            //
            // EventLoopGlobalHook (rather than SimpleGlobalHook) runs our handlers
            // on its own event-loop thread instead of the hook callback thread.
            // That matters: Windows silently EVICTS a low-level hook whose callback
            // takes too long (LowLevelHooksTimeout, 300ms by default). Doing work
            // on the callback thread risks killing the hook for the whole session,
            // with no error - clicks would just stop buzzing.
            this._hook = new EventLoopGlobalHook(GlobalHookType.Mouse);

            this._hook.MousePressed += this.OnMousePressed;
            this._hook.MouseWheel += this.OnMouseWheel;

            // HookEnabled is the only trustworthy confirmation that the OS actually
            // installed the hook. Logging straight after RunAsync would only prove
            // we ASKED for it - installation can still fail (permissions, another
            // libuiohook instance already running in this process).
            this._hook.HookEnabled += (_, _) =>
                PluginLog.Info("[MxHaptics] Mouse hook ENABLED - now receiving system-wide button and scroll events.");

            // Windows can evict a low-level hook without warning if a callback runs
            // long. Without this line that would look like "haptics randomly stopped".
            this._hook.HookDisabled += (_, _) =>
                PluginLog.Info("[MxHaptics] Mouse hook DISABLED.");

            // RunAsync so plugin loading is never blocked. Run() would block until
            // the hook is disposed, which would hang the Plugin Service on load.
            //
            // The returned Task must be observed: a plain fire-and-forget would
            // swallow any startup exception, leaving a plugin that loads "fine" and
            // silently never buzzes.
            var hookTask = this._hook.RunAsync();

            hookTask.ContinueWith(
                t => PluginLog.Error($"[MxHaptics] Mouse hook terminated with an error: {t.Exception}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            PluginLog.Info("[MxHaptics] Mouse hook starting (mouse-only, no keyboard hook)...");
        }

        private void OnMousePressed(Object sender, MouseHookEventArgs e)
        {
            // Fire on PRESS rather than release: the haptic should coincide with the
            // physical switch actuating, which is what makes it feel like part of
            // the click instead of a delayed response to it.
            this.Fire(EventIdFor(e.Data.Button), cooldownMs: 0);
        }

        private void OnMouseWheel(Object sender, MouseWheelHookEventArgs e)
        {
            // Logged so the detent threshold can be tuned against what the device
            // actually reports rather than assumed - coarse vs high-resolution
            // wheels send very different rotation magnitudes.
            PluginLog.Verbose(
                $"[MxHaptics] wheel dir={e.Data.Direction} type={e.Data.Type} " +
                $"rotation={e.Data.Rotation} delta={e.Data.Delta}");

            if (e.Data.Direction == MouseWheelScrollDirection.Horizontal)
            {
                this.FireThumbWheel(e.Data.Rotation);
                return;
            }

            // Vertical wheel: paced by time. Its physical detents already provide
            // the rhythm in ratchet mode, and this reads well in free-spin too.
            this.Fire(HapticEvents.ScrollVertical, ScrollCooldownMs);
        }

        /// <summary>
        /// Paces thumb-wheel haptics by distance rolled, synthesizing detents.
        /// </summary>
        private void FireThumbWheel(Int32 rotation)
        {
            // Direction is irrelevant to the accumulator - rolling back and forth
            // should still tick. Reset on reversal would suppress ticks exactly
            // when the user is scrubbing, which is when feedback matters most.
            this._thumbWheelAccumulator += Math.Abs(rotation);

            if (this._thumbWheelAccumulator < ThumbWheelDetentUnits)
            {
                return;
            }

            // Subtract rather than zero, so leftover rotation carries into the next
            // detent. Zeroing would slowly lose distance and drift slower than the
            // real movement.
            this._thumbWheelAccumulator -= ThumbWheelDetentUnits;

            // Still honour a time floor: a very fast flick could otherwise cross
            // several detents' worth between events and outrun the motor.
            this.Fire(HapticEvents.ScrollHorizontal, ScrollCooldownMs);
        }

        /// <summary>
        /// Plays an event's waveform, honouring its enable flag and rate limit.
        /// </summary>
        private void Fire(String eventId, Int64 cooldownMs)
        {
            if (eventId == null || !this._settings.IsEnabled(eventId))
            {
                return; // Unsupported input, or turned off by the user.
            }

            if (cooldownMs > 0)
            {
                // Cooldown is tracked PER EVENT, so scrolling the main wheel never
                // suppresses a thumbwheel tick (or a click) that happens to land in
                // the same window. Environment.TickCount64 is used rather than
                // DateTime.UtcNow because this runs on the input path and needs to
                // be cheap and monotonic - DateTime jumps when the clock changes.
                var now = Environment.TickCount64;

                if (this._lastFiredMs.TryGetValue(eventId, out var last) && (now - last) < cooldownMs)
                {
                    return;
                }

                this._lastFiredMs[eventId] = now;
            }

            var waveform = this._settings.WaveformFor(eventId);

            // Scroll fires orders of magnitude more often than clicks - one short
            // roll produced several hundred events - so it logs at Verbose to keep
            // the shared Plugin Service log readable. Clicks stay at Info.
            if (cooldownMs > 0)
            {
                PluginLog.Verbose($"[MxHaptics] {eventId} -> {waveform}");
            }
            else
            {
                PluginLog.Info($"[MxHaptics] {eventId} -> {waveform}");
            }

            this._haptics.Play(waveform);
        }

        public void Stop()
        {
            if (this._hook == null)
            {
                return;
            }

            this._hook.MousePressed -= this.OnMousePressed;
            this._hook.MouseWheel -= this.OnMouseWheel;

            // Dispose rather than Stop: a disposed hook cannot be restarted, and we
            // always build a fresh one in Start(). Leaving a hook alive across a
            // plugin reload would leak a global hook into an unloaded assembly - and
            // since only one hook may exist per process, that would also stop the
            // reloaded plugin from ever working.
            this._hook.Dispose();
            this._hook = null;

            PluginLog.Info("[MxHaptics] Mouse hook stopped.");
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.Stop();
        }
    }
}
