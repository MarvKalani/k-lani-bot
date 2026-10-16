import SwiftUI
@main struct KLaniBotFixtureApp: App {
    var body: some Scene { WindowGroup { FixtureView() } }
}
struct FixtureView: View {
    @State private var input = ""
    @State private var saved = "Nothing saved"
    @State private var showDialog = false
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("k-lani-bot · Apple E2E").font(.largeTitle).accessibilityIdentifier("heading")
            Text("Isolated native test fixture").foregroundStyle(.secondary)
            TextField("Literal Unicode input", text: $input).textFieldStyle(.roundedBorder).accessibilityIdentifier("literal-input")
            Button("Save result") { saved = input }.accessibilityIdentifier("save-button")
            Text(saved).accessibilityIdentifier("saved-output")
            Button("Open modal") { showDialog = true }.accessibilityIdentifier("modal-button")
            Text("UUIDv7 requests · exact assertions · native UI").font(.caption)
        }.padding(32)
        .alert("k-lani-bot · Dialog", isPresented: $showDialog) { Button("Dismiss", role: .cancel) {} } message: { Text("An explicit modal interaction") }
#if os(macOS)
        .frame(minWidth: 600, minHeight: 320)
#endif
    }
}
