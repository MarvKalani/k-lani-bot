import XCTest
import Foundation

// Native Apple test runner. No Appium, Python, downloaded package or private API.
// The simulator's runner container is the IPC boundary; only its owner may write it.
final class KLaniBotIosBridge: XCTestCase {
    func testBridge() throws {
        continueAfterFailure = false
        let bundle = ProcessInfo.processInfo.environment["K_LANI_BOT_BUNDLE_ID"] ?? "com.k-lani.bot.fixture"
        let app = XCUIApplication(bundleIdentifier: bundle)
        app.launch()
        let root = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("k-lani-bot-ios-bridge")
        let requests = root.appendingPathComponent("requests")
        let replies = root.appendingPathComponent("replies")
        try FileManager.default.createDirectory(at: requests, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try FileManager.default.createDirectory(at: replies, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        func write(_ value: [String: Any], _ path: URL) throws {
            try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]).write(to: path, options: [.atomic])
        }
        try write(["protocol": 1, "bundle": bundle, "ready": true], root.appendingPathComponent("ready.json"))
        defer { try? FileManager.default.removeItem(at: root.appendingPathComponent("ready.json")) }
        print("K_LANI_BOT_BRIDGE_READY \(root.path)")
        var completed = Set<String>()
        let deadline = Date().addingTimeInterval(900)
        func role(_ element: XCUIElement) -> String {
            switch element.elementType {
            case .textField: return "TextField"
            case .secureTextField: return "SecureTextField"
            case .textView: return "TextArea"
            case .button: return "Button"
            case .staticText: return "Text"
            case .alert: return "Alert"
            default: return "XCUI-\(element.elementType.rawValue)"
            }
        }
        func describe(_ element: XCUIElement) -> [String: Any] {
            let frame = element.frame
            let value = element.elementType == .staticText ? element.label : element.value as? String ?? ""
            return ["Role": role(element), "Name": element.label, "AutoId": element.identifier, "Value": value, "IsEnabled": element.isEnabled, "IsOffscreen": !element.isHittable, "Bounds": ["x": frame.minX, "y": frame.minY, "width": frame.width, "height": frame.height]]
        }
        while Date() < deadline {
            let pending = try FileManager.default.contentsOfDirectory(at: requests, includingPropertiesForKeys: nil).filter { $0.pathExtension == "json" && !completed.contains($0.lastPathComponent) }.sorted { $0.lastPathComponent < $1.lastPathComponent }
            if pending.isEmpty { Thread.sleep(forTimeInterval: 0.03); continue }
            for path in pending {
                let data = try Data(contentsOf: path)
                guard data.count <= 4 * 1024 * 1024, let req = try JSONSerialization.jsonObject(with: data) as? [String: Any], let id = req["request_id"] as? String, UUID(uuidString: id) != nil, id == path.deletingPathExtension().lastPathComponent, id.split(separator: "-")[2].first == "7" else { throw NSError(domain: "k-lani-bot", code: 1, userInfo: [NSLocalizedDescriptionKey: "Invalid UUIDv7 request"] ) }
                let action = req["action"] as? String ?? ""
                var reply: [String: Any] = ["success": false, "message": "Unsupported bridge action"]
                if action == "observe" {
                    let elements = app.descendants(matching: .any).allElementsBoundByIndex
                    if elements.count > 2000 { reply = ["success": false, "message": "Hierarchy exceeds limit"] }
                    else { reply = ["success": true, "elements": elements.map(describe)] }
                } else if action == "screenshot" {
                    let png = app.screenshot().pngRepresentation
                    reply = png.count <= 2 * 1024 * 1024 ? ["success": true, "png": png.base64EncodedString()] : ["success": false, "message": "Screenshot exceeds limit"]
                } else if action == "stop" {
                    try write(["success": true, "request_id": id], replies.appendingPathComponent(path.lastPathComponent)); return
                } else {
                    let identifier = req["auto_id"] as? String ?? ""
                    let matches = app.descendants(matching: .any).matching(identifier: identifier)
                    if identifier.isEmpty || matches.count != 1 { reply = ["success": false, "message": "A unique accessibility identifier is required"] }
                    else {
                        let target = matches.element(boundBy: 0)
                        let expectedRole = req["role"] as? String ?? ""
                        let expectedName = req["name"] as? String ?? ""
                        if role(target) != expectedRole || (expectedRole != "TextField" && expectedRole != "TextArea" && expectedRole != "Text" && target.label != expectedName) { reply = ["success": false, "message": "Target identity changed"] }
                        else if action == "current" { reply = ["success": true, "element": describe(target)] }
                        else if !target.isEnabled || !target.isHittable { reply = ["success": false, "message": "Target is not actionable"] }
                        else if action == "tap" { target.tap(); reply = ["success": true, "message": "XCUI tap dispatched"] }
                        else if action == "type" && (expectedRole == "TextField" || expectedRole == "TextArea") {
                            // XCUI typeText interprets Unicode literally. No key grammar is accepted.
                            // Replacement is deliberately unsupported until reliable selection exists.
                            if req["clear"] as? Bool == true { reply = ["success": false, "message": "Reliable replacement is unsupported; append to an empty field"] }
                            else { target.tap(); target.typeText(req["text"] as? String ?? ""); reply = ["success": true, "message": "Literal XCUI input dispatched"] }
                        }
                    }
                }
                reply["request_id"] = id
                try write(reply, replies.appendingPathComponent(path.lastPathComponent))
                completed.insert(path.lastPathComponent)
                if completed.count > 10000 { throw NSError(domain: "k-lani-bot", code: 2, userInfo: [NSLocalizedDescriptionKey: "Bridge request limit exceeded"]) }
            }
        }
        XCTFail("Bridge lifetime expired without explicit stop")
    }
}
