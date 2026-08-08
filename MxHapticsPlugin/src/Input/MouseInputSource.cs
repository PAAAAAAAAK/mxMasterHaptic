namespace Loupedeck.MxHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;

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

        /// <summary>
        /// Pixels the cursor must travel while held before it counts as a drag.
        /// </summary>
        /// <remarks>
        /// Without this, a click with a few pixels of hand shake registers as a
        /// complete drag - you would feel drag start and drag end on top of the
        /// click haptic, for what was simply a click. Windows uses a similar idea
        /// for its own drag threshold (SM_CXDRAG, typically 4px).
        /// </remarks>
        private const Int32 DragThresholdPx = 5;

        /// <summary>
        /// Minimum drag duration before the "drag end" haptic is worth playing.
        /// </summary>
        /// <remarks>
        /// Start and end are meant to bracket a gesture, but on a very quick drag
        /// they land close enough together to blur into one indistinct buzz - the
        /// motor is often still finishing the first waveform when the second
        /// arrives. Below this threshold only the start plays: it has already
        /// signalled that a drag began, and one clean tap reads far better than
        /// two smeared ones.
        /// </remarks>
        private const Int64 DragMinSeparationMs = 150;

        /// <summary>Which buttons can start a drag, and the events they raise.</summary>
        /// <remarks>
        /// Back and forward are deliberately absent: holding a thumb button and
        /// moving is not a drag by any normal meaning of the word.
        /// </remarks>
        private static readonly Dictionary<MouseButton, (String Start, String End)> DragEvents = new()
        {
            [MouseButton.Button1] = (HapticEvents.DragLeftStart, HapticEvents.DragLeftEnd),
            [MouseButton.Button2] = (HapticEvents.DragRightStart, HapticEvents.DragRightEnd),
            [MouseButton.Button3] = (HapticEvents.DragMiddleStart, HapticEvents.DragMiddleEnd),
        };

        /// <summary>Per-button drag tracking. Buttons can be held simultaneously.</summary>
        private sealed class DragState
        {
            public Boolean ButtonDown;
            public Boolean Dragging;
            public Int16 PressX;
            public Int16 PressY;
            public Int64 StartMs;
        }

        private readonly Dictionary<MouseButton, DragState> _dragStates = new()
        {
            [MouseButton.Button1] = new DragState(),
            [MouseButton.Button2] = new DragState(),
            [MouseButton.Button3] = new DragState(),
        };

        /// <summary>
        /// How far the cursor must leave an edge before that edge can fire again.
        /// </summary>
        /// <remarks>
        /// Without hysteresis, a cursor parked against the edge would re-fire on
        /// every jittery pixel of movement. Requiring a clear retreat means one
        /// haptic per deliberate arrival at the boundary.
        /// </remarks>
        private const Int32 ScreenEdgeReleasePx = 8;

        private Boolean _atScreenEdge;

        // Virtual desktop bounds, cached because MouseMoved fires constantly and
        // this must stay cheap. Re-read periodically so docking, undocking or a
        // resolution change is picked up without needing a display-change hook.
        private Rectangle _virtualScreen;
        private Int64 _virtualScreenReadMs;
        private const Int64 VirtualScreenTtlMs = 5000;

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
            this._hook.MouseReleased += this.OnMouseReleased;
            this._hook.MouseDragged += this.OnMouseDragged;
            this._hook.MouseMoved += this.OnMouseMoved;
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
            if (this._dragStates.TryGetValue(e.Data.Button, out var state))
            {
                state.ButtonDown = true;
                state.Dragging = false;
                state.PressX = e.Data.X;
                state.PressY = e.Data.Y;
            }

            this.Fire(EventIdFor(e.Data.Button), cooldownMs: 0);
        }

        private void OnMouseReleased(Object sender, MouseHookEventArgs e)
        {
            if (!this._dragStates.TryGetValue(e.Data.Button, out var state))
            {
                return; // Not a drag-capable button (back / forward).
            }

            state.ButtonDown = false;

            if (!state.Dragging)
            {
                return; // Plain click - already handled on press.
            }

            state.Dragging = false;

            // Too quick for the pair to read as two distinct taps: keep the start,
            // drop the end, rather than deliver a smear.
            if ((Environment.TickCount64 - state.StartMs) < DragMinSeparationMs)
            {
                return;
            }

            this.Fire(DragEvents[e.Data.Button].End, cooldownMs: 0);
        }

        private void OnMouseDragged(Object sender, MouseHookEventArgs e)
        {
            // The drag event does not reliably say WHICH button is held, so every
            // armed button is checked. Holding two at once is legal, if unusual.
            foreach (var (button, state) in this._dragStates)
            {
                if (!state.ButtonDown || state.Dragging)
                {
                    continue;
                }

                // A press on its own is a click; it only becomes a drag once the
                // cursor travels far enough while held. Firing on any movement
                // would turn a slightly shaky click into a full drag.
                var dx = Math.Abs(e.Data.X - state.PressX);
                var dy = Math.Abs(e.Data.Y - state.PressY);

                if (dx < DragThresholdPx && dy < DragThresholdPx)
                {
                    continue;
                }

                state.Dragging = true;
                state.StartMs = Environment.TickCount64;

                this.Fire(DragEvents[button].Start, cooldownMs: 0);
            }
        }

        private void OnMouseMoved(Object sender, MouseHookEventArgs e)
        {
            // Cheapest possible early-out: this runs on every pixel of cursor
            // movement, so it must do nothing at all when the feature is off.
            if (!this._settings.IsEnabled(HapticEvents.ScreenEdge))
            {
                return;
            }

            var bounds = this.GetVirtualScreen();

            // Bounds are exclusive on the right/bottom, so the last addressable
            // pixel is one less than the edge.
            var atEdge = e.Data.X <= bounds.Left
                      || e.Data.Y <= bounds.Top
                      || e.Data.X >= bounds.Right - 1
                      || e.Data.Y >= bounds.Bottom - 1;

            if (atEdge)
            {
                if (!this._atScreenEdge)
                {
                    this._atScreenEdge = true;
                    this.Fire(HapticEvents.ScreenEdge, cooldownMs: 0);
                }

                return;
            }

            // Re-arm only after a clear retreat, so resting against the edge does
            // not machine-gun on sub-pixel jitter.
            if (this._atScreenEdge
                && e.Data.X > bounds.Left + ScreenEdgeReleasePx
                && e.Data.Y > bounds.Top + ScreenEdgeReleasePx
                && e.Data.X < bounds.Right - 1 - ScreenEdgeReleasePx
                && e.Data.Y < bounds.Bottom - 1 - ScreenEdgeReleasePx)
            {
                this._atScreenEdge = false;
            }
        }

        /// <summary>Virtual desktop bounds, cached with a short TTL.</summary>
        /// <remarks>
        /// The VIRTUAL screen, not a single display: with multiple monitors the
        /// cursor crosses between them freely, and only the outer boundary of the
        /// whole arrangement is a real wall the cursor cannot pass.
        /// </remarks>
        private Rectangle GetVirtualScreen()
        {
            var now = Environment.TickCount64;

            if (this._virtualScreen.IsEmpty || (now - this._virtualScreenReadMs) > VirtualScreenTtlMs)
            {
                this._virtualScreen = SystemInformation.VirtualScreen;
                this._virtualScreenReadMs = now;
            }

            return this._virtualScreen;
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
            this._hook.MouseReleased -= this.OnMouseReleased;
            this._hook.MouseDragged -= this.OnMouseDragged;
            this._hook.MouseMoved -= this.OnMouseMoved;
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
