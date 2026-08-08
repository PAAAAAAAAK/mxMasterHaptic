# MX Haptics

Universal haptic feedback for the **Logitech MX Master 4** — clicks and scroll,
in every application.

The MX Master 4 has a haptic motor, but stock Logi Options+ only uses it for a
narrow set of built-in interactions, and the haptic plugins on the Logi
Marketplace are scoped to a single app or to web browsing. This one is not
scoped to anything: it fires on your actual mouse input, in whatever you happen
to be using.

It is also **one install**. No companion app, no browser tab, no second
download — the settings window ships inside the plugin.

## What it does

| Input | Default waveform | Notes |
|---|---|---|
| Left click | `subtle_collision` | |
| Right click | `damp_collision` | |
| Middle click | `sharp_collision` | |
| Back (thumb) | `sharp_collision` | stronger — further from the motor |
| Forward (thumb) | `sharp_collision` | stronger — further from the motor |
| Scroll wheel | `subtle_collision` | rate-limited; works in ratchet and free-spin |
| Thumb wheel | `sharp_collision` | synthesized detents (see below) |

Every entry can be switched off or given any of the 15 waveforms, from the
built-in settings window.

### Synthesized detents on the thumb wheel

The vertical wheel has physical detents, so in ratchet mode the hardware paces
the haptics for us. The thumb wheel has none — it rolls smoothly — so pacing it
by time produces a constant buzz regardless of how fast you roll it.

Instead it is paced by **distance**: rotation accumulates and a haptic fires once
per notch-equivalent. Roll slowly, ticks come slowly; roll fast, they come fast.
The haptic supplies the detents the hardware doesn't have.

## Requirements

- Logitech **MX Master 4** (the only Logitech mouse with a controllable haptic motor)
- **Logi Options+** (installs the Logi Plugin Service)
- Windows x64

## Settings

Bind the **Haptic Settings** action (Options+ → *All Actions → MxHaptics →
Haptics*) to a key or an Actions Ring slot. Pressing it opens a window listing
every event with an enable toggle and a waveform dropdown. Selecting a waveform
plays it immediately, so you can tune by feel rather than by name. Changes save
as you make them.

Overall haptic **strength** is Logitech's own setting — Options+ → *Haptic
intensity* (Subtle / Low / Medium / High). This plugin does not duplicate it.

## Building

```
dotnet tool install --global LogiPluginTool
dotnet build MxHapticsPlugin/src
```

`dotnet build` writes a `.link` file into the Plugin Service's plugin directory
and asks it to reload, so the plugin loads straight from your build output. Use
`dotnet watch build` from `src/` while iterating.

### Two things that will bite you

Both are already handled in the csproj, but they cost real time to diagnose:

1. **`LogiPluginTool` scaffolds `net8.0`, which will not compile.** The installed
   `PluginApi.dll` (6.4.x) is built against .NET 10, so referencing it from a
   `net8.0` project fails with `CS1705`. The target framework must match the
   Plugin Service's runtime.

2. **SharpHook ships native binaries for every platform**, and the Plugin
   Service's assembly resolver picks the first `runtimes/*/native/` match it
   finds — observed loading `win-arm64` on an x64 machine. The resulting
   `DllNotFoundException` is **fatal and kills LogiPluginService outright**.
   Fixed by pinning `RuntimeIdentifier=win-x64` together with
   `AppendRuntimeIdentifierToOutputPath=false` (the latter because setting a RID
   otherwise shifts the output path and breaks `pluginFolderWin: bin`).

## Design notes

- **Mouse hooks only, never keyboard.** The hook is created with
  `GlobalHookType.Mouse`, so on Windows only `WH_MOUSE_LL` is installed. The
  keyboard hook (`WH_KEYBOARD_LL`) is the same API keyloggers use and sees every
  keystroke including passwords; nothing here needs keystrokes, so it is never
  requested. The plugin also makes no network connections.
- **One global hook per process.** SharpHook/libuiohook keeps its callback in
  static state, so buttons and scroll share a single hook in `MouseInputSource`.
- **Config is keyed on events, not buttons.** New events are entries in
  `HapticEvents.All`; the settings window renders from that list, so adding a
  feature requires no UI work.
- Haptics are raised from a background thread with no action bound — the SDK
  tutorial only shows `RaiseEvent` inside `RunCommand`, but that is not a
  constraint, and it is what makes system-wide feedback possible.

## Roadmap

- [x] Click haptics (5 buttons)
- [x] Scroll wheel and thumb wheel
- [x] Settings window
- [ ] System events (drag, screen edges, window snap, virtual desktop switch)
- [ ] Hover feedback over buttons / links / text fields (UI Automation)
- [ ] Marketplace release

## License

MIT — see [LICENSE](LICENSE).

Free, and staying free. If it's useful to you, a coffee is always welcome.
<!-- Add your GitHub Sponsors / Buy Me a Coffee link here. -->
