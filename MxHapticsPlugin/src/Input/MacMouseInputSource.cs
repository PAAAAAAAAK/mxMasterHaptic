namespace Loupedeck.MxHapticsPlugin.Input
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Loupedeck.MxHapticsPlugin.Config;
    using Loupedeck.MxHapticsPlugin.Haptics;

    /// <summary>
    /// Fires haptics on mouse buttons and scroll on macOS, in every application.
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
        private const UInt32 EventRightMouseDown = 3;
        private const UInt32 EventScrollWheel = 22;
        private const UInt32 EventOtherMouseDown = 25;

        // Delivered in place of a real event when the OS switches the tap off.
        private const UInt32 EventTapDisabledByTimeout = 0xFFFFFFFE;
        private const UInt32 EventTapDisabledByUserInput = 0xFFFFFFFF;

        private const UInt32 FieldMouseEventButtonNumber = 3;
        private const UInt32 FieldScrollWheelEventDeltaAxis1 = 11; // vertical
        private const UInt32 FieldScrollWheelEventDeltaAxis2 = 12; // horizontal

        private static UInt64 MaskOf(params UInt32[] types)
        {
            UInt64 mask = 0;

            foreach (var t in types)
            {
                mask |= 1UL << (Int32)t;
            }

            return mask;
        }

        // --- Pacing -----------------------------------------------------------

        /// <summary>Minimum gap between two scroll haptics, per direction.</summary>
        /// <remarks>
        /// Same reasoning and same value as the Windows source: a floor of 50ms
        /// caps scroll at ~20 taps/second, which is roughly the fastest a ratchet
        /// still reads as separate ticks rather than one continuous buzz.
        ///
        /// NOTE: this duplicates ScrollCooldownMs in MouseInputSource. Left
        /// duplicated on purpose until the tap is proven to work - extracting
        /// shared pacing logic before we know the macOS side is viable would be
        /// refactoring around a design that might not survive.
        /// </remarks>
        private const Int64 ScrollCooldownMs = 50;

        private readonly Dictionary<String, Int64> _lastFiredMs = new();

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
                    EventLeftMouseDown, EventRightMouseDown,
                    EventOtherMouseDown, EventScrollWheel);

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
                        this.Fire(HapticEvents.MouseLeft, cooldownMs: 0);
                        break;

                    case EventRightMouseDown:
                        this.Fire(HapticEvents.MouseRight, cooldownMs: 0);
                        break;

                    case EventOtherMouseDown:
                        this.Fire(
                            EventIdForOtherButton(
                                CGEventGetIntegerValueField(@event, FieldMouseEventButtonNumber)),
                            cooldownMs: 0);
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
        /// Maps macOS "other" button numbers onto our event ids.
        /// </summary>
        /// <remarks>
        /// Left and right arrive as their own event types; everything else comes
        /// through OtherMouseDown carrying a button number. 2 is the wheel button,
        /// 3 and 4 are the thumb buttons. Anything higher is a button this mouse
        /// does not have, and is ignored rather than guessed at.
        /// </remarks>
        private static String EventIdForOtherButton(Int64 buttonNumber) => buttonNumber switch
        {
            2 => HapticEvents.MouseMiddle,
            3 => HapticEvents.MouseBack,
            4 => HapticEvents.MouseForward,
            _ => null,
        };

        private void OnScroll(IntPtr @event)
        {
            var vertical = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis1);
            var horizontal = CGEventGetIntegerValueField(@event, FieldScrollWheelEventDeltaAxis2);

            // Logged so the thumb-wheel pacing can be tuned against what the device
            // actually reports on macOS rather than assumed. Windows reports rotation
            // in units of 120 (and 360 per thumb-wheel event); macOS reports LINES,
            // typically +/-1 per detent, so the Windows detent threshold of 1080 is
            // meaningless here and the real value has to be measured.
            PluginLog.Verbose($"[MxHaptics] scroll axis1={vertical} axis2={horizontal}");

            if (horizontal != 0)
            {
                // Thumb wheel. Paced by time for now; the Windows source paces by
                // DISTANCE to synthesize the detents the hardware lacks, and that
                // wants porting once the measurements above tell us the scale.
                this.Fire(HapticEvents.ScrollHorizontal, ScrollCooldownMs);
                return;
            }

            if (vertical != 0)
            {
                this.Fire(HapticEvents.ScrollVertical, ScrollCooldownMs);
            }
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
