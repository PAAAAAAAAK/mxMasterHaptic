// Second probe: full element/report-descriptor dump, plus a RAW INPUT REPORT
// listener.
//
// The value callback in hidprobe only surfaces PARSED elements. Logitech's HID++
// notifications ride on vendor-specific report IDs and would never appear there,
// so "no value callback" does not yet mean "no data". This registers
// IOHIDDeviceRegisterInputReportCallback, which delivers raw report bytes,
// and prints every report the device sends.

import Foundation
import IOKit
import IOKit.hid
import Darwin

func usagePageName(_ page: Int) -> String {
    switch page {
    case 0x01: return "GenericDesktop"
    case 0x07: return "Keyboard"
    case 0x08: return "LED"
    case 0x09: return "Button"
    case 0x0C: return "Consumer"
    case 0x0D: return "Digitizer"
    default:
        if page >= 0xFF00 { return String(format: "VENDOR(0x%04X)", page) }
        return String(format: "page:0x%02X", page)
    }
}

func elementTypeName(_ t: IOHIDElementType) -> String {
    switch t.rawValue {
    case 1:   return "Input/Misc"
    case 2:   return "Input/Button"
    case 3:   return "Input/Axis"
    case 4:   return "Input/ScanCodes"
    case 129: return "Output"
    case 257: return "Feature"
    case 513: return "Collection"
    default:  return "type:\(t.rawValue)"
    }
}

let mgr = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
IOHIDManagerSetDeviceMatching(mgr, nil)

guard let all = IOHIDManagerCopyDevices(mgr) as? Set<IOHIDDevice> else {
    print("no devices"); exit(1)
}

let logitech = all.filter {
    (IOHIDDeviceGetProperty($0, kIOHIDVendorIDKey as CFString) as? Int) == 0x046D
}

guard let dev = logitech.first else { print("MX Master not found"); exit(1) }

print("========================================================")
print(" Full element dump - MX Master 4 (VID 0x046D PID 0xB042)")
print("========================================================\n")

if let elements = IOHIDDeviceCopyMatchingElements(dev, nil, IOOptionBits(kIOHIDOptionsTypeNone)) as? [IOHIDElement] {
    var byPage: [Int: [(Int, IOHIDElementType, UInt32)]] = [:]
    var reportIDs = Set<UInt32>()

    for e in elements {
        let page = Int(IOHIDElementGetUsagePage(e))
        let usage = Int(IOHIDElementGetUsage(e))
        let rid = IOHIDElementGetReportID(e)
        byPage[page, default: []].append((usage, IOHIDElementGetType(e), rid))
        if IOHIDElementGetType(e).rawValue != 513 { reportIDs.insert(rid) }
    }

    for page in byPage.keys.sorted() {
        print("  \(usagePageName(page))  (0x\(String(format: "%04X", page)))")
        let items = byPage[page]!.sorted { $0.0 < $1.0 }
        for (usage, type, rid) in items {
            print(String(format: "     usage 0x%04X (%4d)  %-14@  reportID=%d",
                         usage, usage, elementTypeName(type) as NSString, rid))
        }
        print("")
    }

    print("  Input report IDs present: \(reportIDs.sorted().map(String.init).joined(separator: ", "))\n")
}

// --- raw input report listening ---

let r = IOHIDDeviceOpen(dev, IOOptionBits(kIOHIDOptionsTypeNone))
guard r == kIOReturnSuccess else {
    print("  open failed: \(String(format: "0x%08x", UInt32(bitPattern: r)))")
    exit(1)
}

let bufSize = 64
let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: bufSize)
buffer.initialize(repeating: 0, count: bufSize)

let reportCallback: IOHIDReportCallback = { _, _, _, type, reportID, report, length in
    var bytes: [String] = []
    for i in 0..<min(Int(length), 32) {
        bytes.append(String(format: "%02X", report[i]))
    }
    // Report IDs 0x10 and 0x11 are HID++ short and long notifications.
    let note: String
    switch reportID {
    case 0x10: note = "  <- HID++ SHORT"
    case 0x11: note = "  <- HID++ LONG"
    default:   note = ""
    }
    print(String(format: "  reportID=%-3d len=%-3d  %@%@",
                 reportID, length, bytes.joined(separator: " ") as NSString, note as NSString))
    fflush(stdout)
}

IOHIDDeviceRegisterInputReportCallback(dev, buffer, bufSize, reportCallback, nil)
IOHIDDeviceScheduleWithRunLoop(dev, CFRunLoopGetCurrent(), CFRunLoopMode.defaultMode.rawValue)

let seconds = CommandLine.arguments.count > 1 ? (Double(CommandLine.arguments[1]) ?? 30) : 30

print("""
  ========================================================
   RAW input reports - listening \(Int(seconds))s
  ========================================================
   Every report the device sends is printed, including
   vendor HID++ notifications the parsed-element callback
   cannot see. Move the mouse as little as possible.

   PRESS: back, pause, forward, pause, then middle click
   (middle is the control - it is known to report).

""")
fflush(stdout)

CFRunLoopRunInMode(.defaultMode, seconds, false)
print("\n  Done.")
IOHIDDeviceClose(dev, IOOptionBits(kIOHIDOptionsTypeNone))
