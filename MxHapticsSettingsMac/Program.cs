// Seed of the macOS settings application.
//
// Right now it does nothing but prove it can RUN, because two separate things
// can stop a bundled executable from launching on macOS and neither is obvious
// from Windows:
//
//   1. THE EXECUTE BIT. A .lplug4 is a zip written on Windows, which has no
//      concept of the Unix mode. If the bit is lost in the round trip, the
//      extracted file cannot be exec'd at all - "Permission denied" - no matter
//      how it is signed.
//
//   2. GATEKEEPER. The apphost is ad-hoc signed but NOT notarized. If the
//      Plugin Service propagates com.apple.quarantine to the files it unpacks,
//      launching is refused with "cannot be opened because the developer cannot
//      be verified".
//
// Both would sink the separate-process design on macOS regardless of which UI
// framework goes on top, so they get settled before any UI is written. The two
// failures look different from the plugin side: the first throws on Process.Start
// with a permissions error, the second starts and is killed by the OS.
//
// Once this launches cleanly, the Avalonia window replaces this file and the
// pipe client from MxHapticsSettings comes across with it.

using System;
using System.IO;

var marker = Path.Combine(Path.GetTempPath(), "MxHaptics-mac-launch-probe.txt");

var report =
    $"launched at {DateTime.Now:O}{Environment.NewLine}"
    + $"executable  {Environment.ProcessPath}{Environment.NewLine}"
    + $"arch        {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}"
    + $"pipe arg    {(args.Length > 0 ? args[0] : "(none)")}{Environment.NewLine}";

File.WriteAllText(marker, report);

// Written to stdout as well as to the file: the plugin captures this directly,
// so a successful launch is visible in the plugin log without anyone having to
// go looking for the marker.
Console.WriteLine(report);
Console.Out.Flush();

return 0;
