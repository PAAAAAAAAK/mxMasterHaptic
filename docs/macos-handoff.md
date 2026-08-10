# macOS port — handoff

Working document for continuing the macOS port **on a Mac**. The Windows side of
this work was done cross-compiling from a Windows machine, which could build and
package but never run or debug. That is the limitation this handoff exists to
lift.

Read this, then read `git log` on this branch. The commit messages carry the
reasoning behind every decision, not just the changes — they are the real
documentation and are worth reading in full before changing anything.

---

## Where the port stands

Branch `macos-support`. Verified on macOS 26.6.1, Apple Silicon, with an
MX Master 4 over Bluetooth.

**Working:** left / right / middle click, drag start and end, vertical scroll,
thumb-wheel scroll, and the settings window. Haptics fire in every application.
The event tap survives sustained use with no OS timeouts.

**Not working:** the **back and forward thumb buttons** produce nothing.

**Not implemented:** screen-edge haptics on macOS. `MouseInputSource` reads
virtual-desktop bounds via `GetSystemMetrics`, which has no macOS equivalent
here yet; `CGDisplayBounds` over `CGGetActiveDisplayList` is the replacement.
Off by default, so it was cut from the first pass rather than rushed.

---

## Established facts — do not re-derive these

Each cost real time to establish. They are recorded in commit messages too.

### 1. No native library of ours can load inside the Plugin Service

This is the constraint that shapes everything. `LogiPluginService.app` is signed
with the hardened runtime:

```
flags=0x10000(runtime)   TeamIdentifier=QED4VVPZWA
entitlements: apple-events, allow-dyld-environment-variables, allow-jit
```

No `com.apple.security.cs.disable-library-validation`. Hardened runtime enables
library validation, which permits `dlopen` only for code signed by Apple or by
the host's own team. **Our dylibs cannot load, and signing them does not help** —
they would carry our team, not Logitech's. On Apple Silicon it fails twice over,
since arm64 rejects unsigned libraries outright.

Consequences:

- **SharpHook is unusable on macOS.** Not because it lacks macOS support — it has
  it — but because it reaches libuiohook through `DllImport`. This is why
  `MacMouseInputSource` exists instead.
- Apple-signed system frameworks are fine, which is why P/Invoking CoreGraphics
  and CoreFoundation works.
- The **settings application is a separate process**, so this does not apply to
  it. Avalonia's `libSkiaSharp.dylib` loads there without trouble.

### 2. Accessibility is already granted to LogiPluginService

Both Options+ and LogiPluginService appear in System Settings → Privacy &
Security → Accessibility, enabled. `CGEventTapCreate` therefore succeeds and
macOS costs the user no extra setup step. `AXIsProcessTrusted()` is logged at
plugin load to make this visible.

### 3. Packaging strips the execute bit

A `.lplug4` is a zip written on Windows, which has no Unix mode to store. The
extracted settings executable came out `OtherRead, GroupRead, UserWrite,
UserRead` — no execute bit anywhere. `EnsureExecutable` in `MxHapticsPlugin.cs`
restores it at launch. **If the build moves to the Mac and the zip starts
preserving modes, keep this anyway** — it is cheap and it protects a failure mode
that is invisible until it happens.

### 4. Gatekeeper does not block the settings application

The .NET SDK ad-hoc signs the arm64 apphost even when cross-building from
Windows (`LC_CODE_SIGNATURE` present). Quarantine is not propagated to files the
Plugin Service unpacks, so an ad-hoc signed, un-notarized executable launches
fine. Confirmed on device.

### 5. Scroll behaves nothing like Windows

`continuous=1` always — the wheel is reported as a high-resolution scroller, so
several events arrive per physical detent. The line delta (`DeltaAxis1/2`) is
**accelerated**: the same detent reports 1 rolled slowly and 16 rolled fast, so
it measures post-acceleration distance and is never a detent count. The Windows
approach of counting rotation toward a fixed threshold (1080) is meaningless
here.

Momentum-phase events are skipped — that is inertial scrolling the OS continues
after the wheel stops, and it was the bulk of the over-firing. Current pacing is
the 50 ms cooldown, reported as good in use.

---

## The mission

### 0. Two answers still outstanding — get these first

```bash
grep 'Event tap created' ~/Library/Application\ Support/Logi/LogiPluginService/Logs/plugin_logs/MxHaptics.log
```

Must say **HID level**. If it says *session level*, the HID tap was refused and
the reasoning in step 1 below is void — start there instead.

Then: **System Settings → Privacy & Security → Input Monitoring** — is
`LogiPluginService` listed, and enabled? This decides whether the main task below
is a clean win or introduces a manual setup step.

### 1. Back / forward buttons — why they are gone

Middle click arrives as `buttonNumber=2`. Nothing with `buttonNumber` 3 or 4 has
ever been delivered, across thousands of logged events.

Two theories were considered:

- **Options+ converts them to `Cmd+[` / `Cmd+]` keystrokes** at the driver level,
  so they never become mouse events.
- **Options+ consumes them with its own event tap** before we see them.

The second looks **ruled out by elimination**: our tap is at `kCGHIDEventTap`
with `kCGHeadInsertEventTap`, and LPS starts *after* Options+, so our tap is the
newest and therefore first — ahead of any tap Options+ owns. We still see
nothing. That leaves an IOKit HID driver claiming the device and synthesising
the keystroke, with no mouse CGEvent ever created.

**Verify this rather than trusting it.** `CGGetEventTapList()` enumerates every
active tap with its process, location, mask and whether it filters — that would
settle it directly. Useful tools: `ioreg -p IOUSB -l`, `hidutil list`,
`log stream --predicate 'subsystem contains "hid"'`.

### 2. IOHIDManager — the actual prize

Reading HID reports from the device directly would solve **two** problems at
once, and no competing plugin has solved either:

- **The thumb buttons**, if they exist in the raw reports.
- **Device filtering.** Both `WH_MOUSE_LL` on Windows and `CGEventTap` on macOS
  deliver input already merged across every pointing device, so today the
  MX Master buzzes when you use the trackpad or a second mouse. Confirmed present
  in BetterClick Haptics too, so it is the state of the art rather than a defect
  of ours — which also makes it a genuine differentiator if solved.

Open questions for the spike:

- Does `IOHIDManager` work from inside LogiPluginService, given Options+ already
  has the device open? Non-exclusive read access is normally allowed, but Options+
  may hold it exclusively.
- Does it need **Input Monitoring** (a *separate* TCC permission from
  Accessibility)? If LPS does not already hold it, macOS gains a manual setup
  step and the "one install, zero configuration" promise takes a real hit. That
  is a product decision, not just a technical one — raise it rather than
  absorbing it.
- Device is **VID `0x046D`, PID `0xB042`**. Note an MX Master 3 may also be
  paired; it has no haptic motor, so device-specific logic must target the 4.

If IOHIDManager works cleanly it could replace `CGEventTap` entirely. If it needs
a permission the user has to grant by hand, it probably should not — or should be
opt-in. Do not assume; measure, then ask.

---

## Constraints — these are not up for renegotiation

- **Never install a keyboard hook or keyboard event tap.** Not on any platform,
  not temporarily for diagnosis. It is the API keyloggers use, it sees every
  password typed, and it is the single biggest reason security software flags
  software like this. The README makes this promise publicly. Two buttons do not
  justify breaking it.
- **No companion app, no local server, no browser configuration, no network
  access.** One install, working immediately, with sensible defaults. This is the
  entire reason the project exists — every competing plugin fails here.
- **Do not rewrite the settings window in SwiftUI.** It was considered and
  rejected. `HapticEvents.cs`, `Waveforms.cs` and `SettingsClient.cs` are
  compiled into *both* platform windows, so the event catalogue physically cannot
  drift. A transcribed Swift copy goes stale the first time an event is added,
  and events have been added repeatedly over this project's life. Avalonia works
  and is behaviourally identical to Windows.
- **Do not touch `MouseInputSource.cs` or the Windows build.** Windows ships
  today, is on GitHub Releases, and is pending Marketplace submission. macOS work
  must not put it at risk.
- **Keep Logitech's official waveform names.** Renaming them would mean
  maintaining a translation layer against the SDK docs and every other plugin,
  for no functional gain.
- Haptic **strength is not controllable** — verified, `PluginApi.dll` exposes
  `get_WaveformIndex` and nothing else. Do not add a strength setting; Options+
  has a global Haptic intensity control and duplicating it would give users two
  settings that fight.

---

## Building and testing on the Mac

Needs the .NET 10 SDK and `dotnet tool install --global LogiPluginTool`.

```bash
dotnet build MxHapticsPlugin/src -c Release -r osx-arm64
```

`MxHapticsPlugin.csproj` already carries the macOS paths. On a Mac,
`IsDevLoopBuild` becomes true, so the build writes a `.link` file into the
Plugin Service's plugin directory and sends a reload — the same fast loop
Windows has. **This path has never been executed**, having only ever been
cross-compiled. Expect to fix it. In particular `PluginDir` contains an escaped
space (`~/Library/Application\ Support/...`) that may not survive the `Exec`
task.

Packaging, if needed:

```bash
logiplugintool pack ./MxHapticsPlugin/bin-mac/Release ./dist/MxHaptics_mac.lplug4
logiplugintool verify ./dist/MxHaptics_mac.lplug4
```

Note `bin-mac/`, not `bin/` — macOS builds go to a separate tree so they never
clobber the Windows output.

Plugin log:

```
~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/MxHaptics.log
```

The settings application's stdout and stderr are captured into that same log, so
an Avalonia failure or a missing dylib appears there.

### Size, and why packaging is still undecided

| | unpacked | packed |
|---|---|---|
| Windows | 1.03 MB | 0.39 MB |
| macOS | 25.1 MB | 10.37 MB |

Almost all of it is `libSkiaSharp.dylib` (14.8 MB), Avalonia's renderer. Windows
and Linux backends are already stripped from the macOS build. A single
dual-platform `.lplug4` would make every Windows user download a Skia renderer
they will never run, at 26× the current size — so **two separate packages** looks
right, but that has not been settled and the Marketplace may only accept one per
listing.

---

## Git

Small commits, freely. Commit messages explain **why**, including what was tried
and rejected — the whole handoff works because past decisions are recoverable
from history. Follow the existing style on this branch.

Do not merge to `main` until macOS is genuinely tested end to end; `main` carries
the shipping Windows release.
