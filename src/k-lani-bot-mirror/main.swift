// Use case: the macOS node operates a real iPhone through iPhone Mirroring. This helper, bundled next to the node, finds the
// app's window through the window server, captures only that window, recognizes its text with Apple Vision and brings the app
// to the front. Every call prints one JSON object; failures print a reason to standard error and exit with 1.
//
//   k-lani-bot-mirror window --bundle <bundle identifier>
//   k-lani-bot-mirror read --window <window id>
//   k-lani-bot-mirror capture --window <window id> --out <png path>
//   k-lani-bot-mirror activate --pid <process id>

import AppKit
import CoreGraphics
import Foundation
import ImageIO
import Vision

// A failure the node reports to the agent as it is.
struct HelperFailure: Error {
    let reason: String
}

func option(_ name: String) throws -> String {
    let arguments = CommandLine.arguments
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else {
        throw HelperFailure(reason: "Missing option " + name + ".")
    }
    return arguments[index + 1]
}

func printJson(_ value: [String: Any]) throws {
    let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
    FileHandle.standardOutput.write(data)
    FileHandle.standardOutput.write("\n".data(using: .utf8)!)
}

// The largest on-screen, normal-layer window of a running app with this bundle identifier, in global points.
func findWindow(bundle: String) throws -> [String: Any] {
    let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
    guard let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
        throw HelperFailure(reason: "The window server did not answer.")
    }
    var best: [String: Any] = ["found": false]
    var bestArea = 0.0
    for window in windows {
        guard let layer = window[kCGWindowLayer as String] as? Int, layer == 0,
              let pid = window[kCGWindowOwnerPID as String] as? Int32,
              let number = window[kCGWindowNumber as String] as? Int,
              let boundsValue = window[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: boundsValue),
              NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == bundle else {
            continue
        }
        let area = Double(bounds.width * bounds.height)
        if area > bestArea {
            bestArea = area
            best = ["found": true, "window_id": number, "pid": Int(pid), "x": Double(bounds.origin.x),
                    "y": Double(bounds.origin.y), "width": Double(bounds.width), "height": Double(bounds.height)]
        }
    }
    return best
}

// screencapture -l captures one window without its shadow, so the image covers exactly the window bounds.
func capture(window: String, to path: String) throws {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    process.arguments = ["-x", "-o", "-t", "png", "-l", window, path]
    try process.run()
    process.waitUntilExit()
    guard process.terminationStatus == 0, FileManager.default.fileExists(atPath: path) else {
        throw HelperFailure(reason: "The window could not be captured; Screen Recording may not be granted.")
    }
}

// Vision boxes are normalized with the origin at the bottom left; the node expects 0 to 1 from the top left.
func recognize(path: String) throws -> [[String: Any]] {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
        throw HelperFailure(reason: "The captured window image could not be read.")
    }
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.recognitionLanguages = ["de-DE", "en-US"]
    request.usesLanguageCorrection = true
    try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
    var lines: [[String: Any]] = []
    for observation in request.results ?? [] {
        guard let candidate = observation.topCandidates(1).first else {
            continue
        }
        let box = observation.boundingBox
        lines.append(["text": candidate.string, "confidence": Double(candidate.confidence), "x": Double(box.minX),
                      "y": Double(1 - box.maxY), "width": Double(box.width), "height": Double(box.height)])
    }
    return lines
}

func activate(pid: String) throws {
    guard let id = Int32(pid), let app = NSRunningApplication(processIdentifier: id) else {
        throw HelperFailure(reason: "No running app has the process id " + pid + ".")
    }
    guard app.activate() else {
        throw HelperFailure(reason: "The app could not be brought to the front.")
    }
}

func run() throws {
    guard CommandLine.arguments.count >= 2 else {
        throw HelperFailure(reason: "Name a command: window, read, capture or activate.")
    }
    switch CommandLine.arguments[1] {
    case "window":
        try printJson(try findWindow(bundle: try option("--bundle")))
    case "read":
        let path = NSTemporaryDirectory() + "k-lani-bot-mirror-" + UUID().uuidString + ".png"
        defer { try? FileManager.default.removeItem(atPath: path) }
        try capture(window: try option("--window"), to: path)
        try printJson(["lines": try recognize(path: path)])
    case "capture":
        let path = try option("--out")
        try capture(window: try option("--window"), to: path)
        try printJson(["written": path])
    case "activate":
        try activate(pid: try option("--pid"))
        try printJson(["active": true])
    default:
        throw HelperFailure(reason: "Unknown command " + CommandLine.arguments[1] + ".")
    }
}

do {
    try run()
} catch let failure as HelperFailure {
    FileHandle.standardError.write((failure.reason + "\n").data(using: .utf8)!)
    exit(1)
} catch {
    FileHandle.standardError.write((String(describing: error) + "\n").data(using: .utf8)!)
    exit(1)
}
