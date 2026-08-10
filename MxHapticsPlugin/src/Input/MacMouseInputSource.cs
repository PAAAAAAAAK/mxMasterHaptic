namespace Loupedeck.MxHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    /// <summary>
    /// Fires haptics on mouse buttons, scroll and drag on macOS, in every application.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS INSTEAD OF SHARPHOOK. SharpHook works fine on macOS in an
    /// application you control, but it cannot be used here. It reaches libuiohook
    /// through DllImport, which means dlopen of our own uiohook.dylib - and the
    /// Logi Plugin Service is signed with the hardened runtime:
    ///
    ///   flags=0x10000(runtime)   TeamIdentifier=QED4VVPZWA
    ///   entitlements: apple-events, allow-dyld-environment-variables, allow-jit
    ///
    /// Hardened runtime enables LIBRARY VALIDATION unless
    /// com.apple.security.cs.disable-library-validation is present, and it is not.
    /// Library validation permits loading only code signed by Apple or by the
    /// host's own Team ID. Our dylib is neither, and signing it ourselves cannot
    /// help - it would carry our team, not Logitech's. On Apple Silicon it is
    /// blocked twice over, since arm64 rejects unsigned libraries outright.
    ///
    /// That is also why every macOS haptic plugin on the Marketplace is pure
    /// managed code: .NET assemblies are loaded by CoreCLR, never dlopen'd, so
    /// library validation never inspects them.
    ///
    /// The way through is to ship NO native binary of our own. CoreGraphics and
    /// CoreFoundation are Apple-signed, so P/Invoke into them is explicitly
    /// permitted, and CGEventTap gives us exactly the system-wide mouse events
    /// libuiohook would have provided.
    ///
    /// MEASURED ON DEVICE (macOS 26.6.1, Apple Silicon): the tap is created and
    /// enabled inside the Plugin Service, events arrive on a run loop we own on a
    /// background thread, and no kCGEventTapDisabledByTimeout was observed across
    /// several minutes of continuous clicking and scrolling.
    /// </remarks>
    internal sealed class MacMouseInputSource : IInputSource
    {
        private const String CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        private const String CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const String ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        private readonly HapticOutput _haptics;
        private readonly HapticSettings _settings;

        private IntPtr _tap;
        private IntPtr _runLoopSource;
        private IntPtr _runLoop;
        private Thread _thread;
        private Boolean _disposed;

        // The delegate must be held in a field. Marshalling it produces a raw
        // function pointer that the GC knows nothing about, so a local would be
        // collected while CoreGraphics still holds the pointer - producing a crash
        // inside the Plugin Service minutes after everything looked fine.
        private CGEventTapCallBack _callback;

        public String Name => "Mouse (macOS)";

        public MacMouseInputSource(HapticOutput haptics, HapticSettings settings)
        {
            this._haptics = haptics ?? throw new ArgumentNullException(nameof(haptics));
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        // --- CoreGraphics / CoreFoundation ------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CGEventTapCallBack(
            IntPtr proxy, UInt32 type, IntPtr @event, IntPtr userInfo);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGEventTapCreate(
            UInt32 tap, UInt32 place, UInt32 options, UInt64 eventsOfInterest,
            CGEventTapCallBack callback, IntPtr userInfo);

        [DllImport(CoreGraphics)]
        private static extern void CGEventTapEnable(IntPtr tap, Boolean enable);

        [DllImport(CoreGraphics)]
        private static extern Int64 CGEventGetIntegerValueField(IntPtr @event, UInt32 field);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFMachPortCreateRunLoopSource(
            IntPtr allocator, IntPtr port, IntPtr order);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopRun();

        [DllImport(CoreFoundation)]
        private static extern void CFRunLoopStop(IntPtr runLoop);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        /// <summary>Whether this process holds macOS Accessibility permission.</summary>
        /// <remarks>
        /// The grant attaches to the HOST process - LogiPluginService - not to us,
        /// so we can only report it, never request it usefully. Logged at load
        /// because a tap that silently never fires looks identical to a bug in our
        /// own code, and this distinguishes the two immediately.
        /// </remarks>
        [DllImport(ApplicationServices)]
        private static extern Boolean AXIsProcessTrusted();

        // Event tap placement and behaviour.
        private const UInt32 SessionEventTap = 1;
        private const UInt32 HeadInsertEventTap = 0;

        // LISTEN ONLY, deliberately. The alternative lets a tap alter or swallow
        // events, which would put us in the path of every click on the system -
        // a far worse failure mode, and a far worse thing to ask users to trust.
        // We only ever read.
        private const UInt32 EventTapOptionListenOnly = 1;

        private const UInt32 EventLeftMouseDown = 1;
        private const UInt32 EventLeftMouseUp = 2;
        private const UInt32 EventRightMouseDown = 3;
        private const UInt32 EventRightMouseUp = 4;
        private const UInt32 EventLeftMouseDragged = 6;
        private const UInt32 EventRightMouseDragged = 7;
        private const UInt32 EventScrollWheel = 22;
        private const UInt32 EventOtherMouseDown = 25;
        private const UInt32 EventOtherMouseUp = 26;
        private const UInt32 EventOtherMouseDragged = 27;

        // Delivered in place of a real event when the OS switches the tap off.
        private const UInt32 EventTapDisabledByTimeout = 0xFFFFFFFE;
        private const UInt32 EventTapDisabledByUserInput = 0xFFFFFFFF;

        private const UInt32 FieldMouseEventButtonNumber = 3;
        private const UInt32 FieldMouseEventDeltaX = 4;
        private const UInt32 FieldMouseEventDeltaY = 5;
        private const UInt32 FieldScrollWheelEventDeltaAxis1 = 11; // vertical, LINES, accelerated
        private const UInt32 FieldScrollWheelEventDeltaAxis2 = 12; // horizontal, LINES, accelerated

        // Diagnostics for detent reconstruction. Windows gives one event per
        // physical detent; macOS reports the same wheel at high resolution, so
        // several events arrive per detent and something here has to stand in for
        // the detent boundary. Logged until we know which field actually tracks
        // physical movement rather than post-acceleration distance.
        private const UInt32 FieldScrollWheelEventIsContinuous = 88;
        private const UInt32 FieldScrollWheelEventPointDeltaAxis1 = 96;
        private const UInt32 FieldScrollWheelEventPointDeltaAxis2 = 97;
        private const UInt32 FieldScrollWheelEventScrollPhase = 99;
        private const UInt32 FieldScrollWheelEventMomentumPhase = 123;

        private static UInt64 MaskOf(params UInt32[] types)
        {
            UInt64 mask = 0;

            foreach (var t in types)
            {
                mask |= 1UL << (Int32)t;
            }

            return mask;
        }

        // --- Buttons ----------------------------------------------------------

        /// <summary>
        /// The buttons we recognise, independent of how macOS delivers them.
        /// </summary>
        /// <remarks>
        /// Left and right arrive as their own event types; everything else comes
        /// through the OtherMouse* types carrying a button number. Keeping a
        /// logical id lets drag tracking work the same way for all three
        /// drag-capable buttons without caring which route the event took.
        /// </remarks>
        private enum Button
        {
            None,
            Left,
            Right,
            Middle,
            Back,
            Forward,
        }

        /// <summary>Maps macOS "other" button numbers onto logical buttons.</summary>
        /// <remarks>
        /// 2 is the wheel button; 3 and 4 are the thumb buttons. Anything else is
        /// a button this mouse does not have and is ignored rather than guessed at
        /// - but see OnEvent, which logs every unmapped number so a wrong
        /// assumption here shows up in the log instead of failing silently.
        /// </remarks>
        private static Button ButtonForOtherNumber(Int64 number) => number switch
        {
            2 => Button.Middle,
            3 => Button.Back,
            4 => Button.Forward,
            _ => Button.None,
        };

        private static String EventIdFor(Button button) => button switch
        {
            Button.Left => HapticEvents.MouseLeft,
            Button.Right => HapticEvents.MouseRight,
            Button.Middle => HapticEvents.MouseMiddle,
            Button.Back => HapticEvents.MouseBack,
            Button.Forward => HapticEvents.MouseForward,
            _ => null,
        };

        /// <summary>Which buttons can start a drag, and the events they raise.</summary>
        /// <remarks>
        /// Back and forward are deliberately absent, exactly as on Windows:
        /// holding a thumb button and moving is not a drag by any normal meaning
        /// of the word.
        /// </remarks>
        private static readonly Dictionary<Button, (String Start, String End)> DragEvents = new()
        {
            [Button.Left] = (HapticEvents.DragLeftStart, HapticEvents.DragLeftEnd),
            [Button.Right] = (HapticEvents.DragRightStart, HapticEvents.DragRightEnd),
            [Button.Middle] = (HapticEvents.DragMiddleStart, HapticEvents.DragMiddleEnd),
        };

        // --- Pacing and thresholds --------------------------------------------
        //
        // These mirror MouseInputSource exactly, and the reasoning behind each is
        // documented there rather than repeated. They are duplicated on purpose
        // for now: extracting shared pacing logic is worth doing, but not before
        // the macOS side has settled enough to know what genuinely IS shared.

        private const Int64 ScrollCooldownMs = 50;
        private const Int32 DragThresholdPx = 5;
        private const Int64 DragMinSeparationMs = 150;

        private readonly Dictionary<String, Int64> _lastFiredMs = new();

        /// <summary>Per-button drag tracking. Buttons can be held simultaneously.</summary>
        /// <remarks>
        /// Displacement is accumulated from the event stream rather than read as an
        /// absolute cursor position. CGEventGetLocation returns a CGPoint by value,
        /// and summing the signed per-event deltas gives the same answer - net
        /// distance from where the button went down - without marshalling a struct
        /// back across the boundary.
        /// </remarks>
        private sealed class DragState
        {
            public Boolean ButtonDown;
            public Boolean Dragging;
            public Int64 DisplacementX;
            public Int64 DisplacementY;
            public Int64 StartMs;
        }

        private readonly Dictionary<Button, DragState> _dragStates = new()
        {
            [Button.Left] = new DragState(),
            [Button.Right] = new DragState(),
            [Button.Middle] = new DragState(),
        };

        public void Start()
        {
            if (this._thread != null)
            {
                return;
            }

            PluginLog.Info(
                $"[MxHaptics] macOS Accessibility permission for this process: {AXIsProcessTrusted()}");

            // The tap and its run loop must live on the SAME thread, and CFRunLoopRun
            // blocks forever, so this needs a thread of its own. Background so it can
            // never hold up process shutdown.
            this._thread = new Thread(this.RunTapThread)
            {
                IsBackground = true,
                Name = "MxHaptics.MacEventTap",
            };

            this._thread.Start();
        }

        private void RunTapThread()
        {
            try
            {
                this._callback = this.OnEvent;

                var mask = MaskOf(
                    EventLeftMouseDown, EventLeftMouseUp, EventLeftMouseDragged,
                    EventRightMouseDown, EventRightMouseUp, EventRightMouseDragged,
                    EventOtherMouseDown, EventOtherMouseUp, EventOtherMouseDragged,
                    EventScrollWheel);

                this._tap = CGEventTapCreate(
                    SessionEventTap, HeadInsertEventTap, EventTapOptionListenOnly,
                    mask, this._callback, IntPtr.Zero);

                if (this._tap == IntPtr.Zero)
                {
                    // Overwhelmingly the Accessibility permission: CGEventTapCreate
                    // returns null rather than failing loudly when it is missing.
                    PluginLog.Error(
                        "[MxHaptics] CGEventTapCreate returned NULL - the event tap could not be "
                        + "created. This is almost always missing Accessibility permission for "
                        + "LogiPluginService (System Settings > Privacy & Security > Accessibility).");
                    return;
                }

                this._runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, this._tap, IntPtr.Zero);

                if (this._runLoopSource == IntPtr.Zero)
                {
                    PluginLog.Error("[MxHaptics] CFMachPortCreateRunLoopSource returned NULL.");
                    return;
                }

                this._runLoop = CFRunLoopGetCurrent();

                CFRunLoopAddSource(this._runLoop, this._runLoopSource, GetCommonModes());
                CGEventTapEnable(this._tap, true);

                PluginLog.Info("[MxHaptics] macOS event tap ENABLED - now receiving system-wide mouse events.");

                // Blocks until CFRunLoopStop is called from Stop().
                CFRunLoopRun();

                PluginLog.Info("[MxHaptics] macOS event tap run loop exited.");
            }
            catch (Exception ex)
            {
                // A P/Invoke failure here would otherwise vanish silently on a
                // background thread, leaving a plugin that loads fine and never buzzes.
                PluginLog.Error($"[MxHaptics] macOS event tap failed: {ex}");
            }
        }

        /// <summary>Reads kCFRunLoopCommonModes, an exported CFStringRef variable.</summary>
        /// <remarks>
        /// It is DATA, not a function, so DllImport cannot reach it. Load the
        /// address of the variable and dereference it to get the CFStringRef.
        /// Common modes rather than default mode, so the tap keeps delivering
        /// while the run loop is in a modal or tracking mode.
        /// </remarks>
        private static IntPtr GetCommonModes()
        {
            var handle = NativeLibrary.Load(CoreFoundation);
            var symbol = NativeLibrary.GetExport(handle, "kCFRunLoopCommonModes");

            return Marshal.ReadIntPtr(symbol);
        }

        private IntPtr OnEvent(IntPtr proxy, UInt32 type, IntPtr @event, IntPtr userInfo)
        {
            try
            {
                // macOS switches a tap OFF if its callback is too slow, which is the
                // direct analogue of Windows evicting a low-level hook on
                // LowLevelHooksTimeout. Without re-enabling here, haptics would just
                // stop mid-session with no error anywhere.
                if (type == EventTapDisabledByTimeout || type == EventTapDisabledByUserInput)
                {
                    PluginLog.Info($"[MxHaptics] Event tap disabled by the OS (type={type}); re-enabling.");
                    CGEventTapEnable(this._tap, true);
                    return @event;
                }

                switch (type)
                {
                    case EventLeftMouseDown:
                        this.OnButtonDown(Button.Left);
                        break;

                    case EventRightMouseDown:
                        this.OnButtonDown(Button.Right);
                        break;

                    case EventLeftMouseUp:
                        this.OnButtonUp(Button.Left);
                        break;

                    case EventRightMouseUp:
                        this.OnButtonUp(Button.Right);
                        break;

                    case EventLeftMouseDragged:
                        this.OnDragged(Button.Left, @event);
                        break;

                    case EventRightMouseDragged:
                        this.OnDragged(Button.Right, @event);
                        break;

                    case EventOtherMouseDown:
                    case EventOtherMouseUp:
                    case EventOtherMouseDragged:
                        this.OnOtherMouse(type, @event);
                        break;

                    case EventScrollWheel:
                        this.OnScroll(@event);
                        break;
                }
            }
            catch (Exception ex)
            {
                // NEVER let an exception escape into CoreGraphics. Unwinding through
                // native frames would take down the whole Plugin Service.
                PluginLog.Error($"[MxHaptics] Event tap callback error: {ex.Message}");
            }

            // Listen-only, so the return value is ignored - but returning the event
            // unchanged is the documented contract and costs nothing.
            return @event;
        }

        /// <summary>
        /// Handles the middle and thumb buttons, which all arrive as OtherMouse*.
        /// </summary>
        /// <remarks>
        /// The raw button number is logged for EVERY such event, including numbers
        /// we do not map. An earlier build mapped them silently, so a button that
        /// produced no haptic was indistinguishable from a button whose events
        /// never arrived at all - and those two have completely different fixes.
        /// </remarks>
        private void OnOtherMouse(UInt32 type, IntPtr @event)
        {
            var number = CGEventGetIntegerValueField(@event, FieldMouseEventButtonNumber);
            var button = ButtonForOtherNumber(number);

            PluginLog.Info($"[MxHaptics] other-mouse type={type} buttonNumber={number} -> {button}");

            if (button == Button.None)
            {
                return;
            }

            switch (type)
            {
                case EventOtherMouseDown:
                    this.OnButtonDown(button);
                    break;

                case EventOtherMouseUp:
                    this.OnButtonUp(button);
                    break;

                case EventOtherMouseDragged:
                    this.OnDragged(button, @event);
                    break;
            }
        }

        private void OnButtonDown(Button button)
        {
            if (this._dragStates.TryGetValue(button, out var state))
            {
                state.ButtonDown = true;
                state.Dragging = false;
                state.DisplacementX = 0;
                state.DisplacementY = 0;
            }

            // Fire on PRESS rather than release, so the haptic coincides with the
            // physical switch actuating instead of trailing it.
            this.Fire(EventIdFor(button), cooldownMs: 0);
        }

        private void OnButtonUp(Button button)
        {
            if (!this._dragStates.TryGetValue(button, out var state))
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

            this.Fire(DragEvents[button].End, cooldownMs: 0);
        }

        private void OnDragged(Button button, IntPtr @event)
        {
            if (!this._dragStates.TryGetValue(button, out var state) || !state.ButtonDown || state.Dragging)
            {
                return;
            }

            // Signed accumulation, so this measures net displacement from the press
            // point rather than total path length. Summing absolute values would let
            // a slow wiggle in place cross the threshold and report a drag that
            // never went anywhere.
            state.DisplacementX += CGEventGetIntegerValueField(@event, FieldMouseEventDeltaX);
            state.DisplacementY += CGEventGetIntegerValueField(@event, FieldMouseEventDeltaY);

            if (Math.Abs(state.DisplacementX) < DragThresholdPx
                && Math.Abs(state.DisplacementY) < DragThresholdPx)
            {
                return;
            }

            state.Dragging = true;
            state.StartMs = Environment.TickCount64;

            this.Fire(DragEvents[button].Start, cooldownMs: 0);
        }

        private void OnScroll(IntPtr @event)
        {
            var vertical = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis1);
            var horizontal = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis2);

            // MOST scroll events carry zero on both line axes. The MX Master's wheel
            // reports at high resolution, so macOS emits a stream of sub-line events
            // and only ticks the line counter once a full line accumulates. Those are
            // not scrolls we can act on, and an earlier build logged hundreds of them
            // per flick for nothing.
            if (vertical == 0 && horizontal == 0)
            {
                return;
            }

            // MOMENTUM events are inertial scrolling the OS continues AFTER the
            // wheel has stopped moving. A haptic there would be feedback for input
            // the user is no longer providing, which is worse than no haptic at all.
            var momentumPhase = CGEventGetIntegerValueField(@event, FieldScrollWheelEventMomentumPhase);

            if (momentumPhase != 0)
            {
                return;
            }

            // NOTE the line delta is ACCELERATED by macOS: the same physical detent
            // reports 1 line rolled slowly and 16 rolled fast, so it is a distance,
            // never a detent count. That rules out the Windows approach of counting
            // rotation units towards a fixed threshold - 1080 there means nothing
            // here.
            //
            // Worse, the wheel reports at HIGH RESOLUTION, so several events arrive
            // per physical detent - which makes the ScrollCooldownMs floor the main
            // thing pacing haptics rather than a rare safety net, and it fires
            // faster than the detents you can feel under your finger. Everything
            // below is logged to find a field that tracks physical movement.
            PluginLog.Verbose(
                $"[MxHaptics] scroll axis1={vertical} axis2={horizontal}"
                + $" pt1={CGEventGetIntegerValueField(@event, FieldScrollWheelEventPointDeltaAxis1)}"
                + $" pt2={CGEventGetIntegerValueField(@event, FieldScrollWheelEventPointDeltaAxis2)}"
                + $" continuous={CGEventGetIntegerValueField(@event, FieldScrollWheelEventIsContinuous)}"
                + $" phase={CGEventGetIntegerValueField(@event, FieldScrollWheelEventScrollPhase)}");

            if (horizontal != 0)
            {
                this.Fire(HapticEvents.ScrollHorizontal, ScrollCooldownMs);
                return;
            }

            this.Fire(HapticEvents.ScrollVertical, ScrollCooldownMs);
        }

        /// <summary>Plays an event's waveform, honouring its enable flag and rate limit.</summary>
        private void Fire(String eventId, Int64 cooldownMs)
        {
            if (eventId == null || !this._settings.IsEnabled(eventId))
            {
                return;
            }

            if (cooldownMs > 0)
            {
                var now = Environment.TickCount64;

                if (this._lastFiredMs.TryGetValue(eventId, out var last) && (now - last) < cooldownMs)
                {
                    return;
                }

                this._lastFiredMs[eventId] = now;
            }

            var waveform = this._settings.WaveformFor(eventId);

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
            if (this._thread == null)
            {
                return;
            }

            try
            {
                if (this._tap != IntPtr.Zero)
                {
                    CGEventTapEnable(this._tap, false);
                }

                // Breaks CFRunLoopRun on the tap thread so it can exit.
                if (this._runLoop != IntPtr.Zero)
                {
                    CFRunLoopStop(this._runLoop);
                }

                // Bounded: a plugin reload must never block on this.
                this._thread.Join(TimeSpan.FromSeconds(2));

                if (this._runLoopSource != IntPtr.Zero)
                {
                    CFRelease(this._runLoopSource);
                }

                if (this._tap != IntPtr.Zero)
                {
                    CFRelease(this._tap);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"[MxHaptics] macOS event tap teardown error: {ex.Message}");
            }
            finally
            {
                this._runLoopSource = IntPtr.Zero;
                this._runLoop = IntPtr.Zero;
                this._tap = IntPtr.Zero;
                this._thread = null;

                // Released only after the run loop has stopped: CoreGraphics still
                // holds the function pointer until then.
                this._callback = null;

                PluginLog.Info("[MxHaptics] macOS event tap stopped.");
            }
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
