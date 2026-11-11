// "OpenHP1 Launcher" (macOS): installs the game data from the player's own copy of Harry Potter (2001, PC) with the
// bundled HP1Setup (launcher/HP1Setup; progress from its STEP/PROGRESS/EDITION/WARNING/ERROR/DONE lines), starts the bundled OpenHP1
// with --root, and updates itself — and with it OpenHP1 — from GitHub releases. Built by openhp1/packaging/make_launcher.sh.
// Self-update: only a zip whose Ed25519 signature (<zip>.sig in the same release) verifies against the public key
// pinned into this build (Info.plist OpenHP1UpdateKey, launcher/update_sign.swift) is unpacked; the new app must have
// our bundle id, the advertised version and a valid code signature. Command-line checks (no window):
//   Launcher --verify-update <zip> <sig file> <public key, base64> <version>   (launcher/test_update.sh)
//   Launcher --pick-release <releases.json> <current version>
import AppKit
import CryptoKit
import SwiftUI

let bundleID = "io.github.openhp1.launcher"  // make_launcher.sh Info.plist

let steps: [(key: String, text: String)] = [
  ("find", "Looking for the game"),
  ("unpack", "Unpacking"),
  ("check", "Checking the game files"),
  ("copy", "Copying game data"),
]

// A running setup is stopped when the Launcher quits (HP1Setup detaches disc images on SIGTERM).
nonisolated(unsafe) var current: Process?

final class AppDelegate: NSObject, NSApplicationDelegate {
  func applicationShouldTerminateAfterLastWindowClosed(_ app: NSApplication) -> Bool { true }
  func applicationDidFinishLaunching(_ n: Notification) { Launcher.applyGameIcon() }
  func applicationWillTerminate(_ n: Notification) {
    guard let p = current, p.isRunning else { return }
    p.terminate()
    p.waitUntilExit()
  }
}

// Splits a pipe's output into lines; called from the pipe's handler thread only, one call at a time.
final class LineReader: @unchecked Sendable {
  private var buf = Data()
  private let onLine: (String) -> Void
  init(_ onLine: @escaping (String) -> Void) { self.onLine = onLine }
  func feed(_ d: Data) {
    buf.append(d)
    while let nl = buf.firstIndex(of: 10) {
      onLine(String(decoding: buf[..<nl], as: UTF8.self))
      buf.removeSubrange(...nl)
    }
  }
}

@MainActor final class Launcher: ObservableObject {
  @Published var inputs: [URL] = []
  @Published var step = -1
  @Published var progress = 0.0
  @Published var error: String?
  @Published var warning: String?  // HP1Setup's WARNING line (an unverified edition): shown, the install goes on
  @Published var running = false
  @Published var playing = false
  @Published var installed = Launcher.isInstalled()
  @Published var edition = Launcher.installedEdition()  // "US 1.53: exact" (HP1Setup's EDITION line, kept in the marker)

  static let home = NSHomeDirectory()
  static let dataURL = URL(fileURLWithPath: home + "/Library/Application Support/OpenHP1")
  static let gameURL = dataURL.appendingPathComponent("game")  // the install root: game data, Save/, OpenHP1Data/ (settings, logs)
  static let savesURL = gameURL.appendingPathComponent("Save")
  static let settingsURL = gameURL.appendingPathComponent("OpenHP1Data")
  static let logsURL = settingsURL.appendingPathComponent("logs")
  static let launchLogURL = URL(fileURLWithPath: home + "/Library/Logs/OpenHP1.log")
  static var runtimeURL: URL { Bundle.main.resourceURL!.appendingPathComponent("OpenHP1") }
  // HP1Setup writes game/.hp1-installed last: an interrupted copy does not count.
  static func isInstalled() -> Bool { FileManager.default.fileExists(atPath: gameURL.path + "/.hp1-installed") }
  // The game's own icon (System/hp.ico of the player's installed copy, author decision 2026-11-09: used, never shipped)
  // on the Launcher in the Dock and in Finder, once the game is installed; before that the Launcher's own shield. The
  // icon is 32x32, scaled up by whole pixels. Finder's custom icon is a file in the bundle (codesign --strict then
  // reports "detritus"; the ad-hoc signed, unquarantined app still runs), and a self-update brings a fresh bundle:
  // set again at every start.
  static func applyGameIcon() {
    guard isInstalled(), let ico = NSImage(contentsOf: gameURL.appendingPathComponent("System/hp.ico")) else { return }
    let s = 512
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: s, pixelsHigh: s, bitsPerSample: 8, samplesPerPixel: 4,
                                     hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
    else { return }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .none
    ico.draw(in: NSRect(x: 0, y: 0, width: s, height: s))
    NSGraphicsContext.restoreGraphicsState()
    let img = NSImage(size: NSSize(width: s, height: s)); img.addRepresentation(rep)
    NSApp.applicationIconImage = img
    NSWorkspace.shared.setIcon(img, forFile: Bundle.main.bundlePath, options: [])
  }
  static func installedEdition() -> String? {
    (try? String(contentsOfFile: gameURL.path + "/.hp1-installed", encoding: .utf8))?
      .split(separator: "\n").first { $0.hasPrefix("edition=") }.map { String($0.dropFirst(8)) }
  }

  func choose() {
    let p = NSOpenPanel()
    p.canChooseDirectories = true; p.canChooseFiles = true; p.allowsMultipleSelection = true
    p.message = "The installed game folder, the game CD or its image (.iso, .bin/.cue, .mdf), its installer, or a .zip of the game"
    if p.runModal() == .OK { add(p.urls) }
  }

  func add(_ urls: [URL]) {
    for u in urls where !inputs.contains(u) { inputs.append(u) }
    error = nil; step = -1
  }

  // A downloaded Launcher carries the quarantine flag on everything inside; macOS then refuses to run the bundled
  // HP1Setup and OpenHP1. Clear it on the whole bundle (not possible while macOS runs the app from a translocated copy).
  func unquarantine() -> Bool {
    if Bundle.main.bundlePath.contains("/AppTranslocation/") {
      error = "Move OpenHP1 Launcher to the Applications folder, open it from there and try again."
      return false
    }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
    p.arguments = ["-dr", "com.apple.quarantine", Bundle.main.bundlePath]
    try? p.run(); p.waitUntilExit()
    return true
  }

  func install() {
    guard !inputs.isEmpty, !running, !playing, !updating, unquarantine() else { return }  // nothing swaps the data or the app under it
    running = true; error = nil; warning = nil; step = 0; progress = 0
    let p = Process()
    current = p
    p.executableURL = Launcher.runtimeURL.appendingPathComponent("HP1Setup")
    p.arguments = ["--dest", Launcher.gameURL.path] + inputs.map(\.path)
    p.standardInput = FileHandle.nullDevice
    let pipe = Pipe(); p.standardOutput = pipe; p.standardError = pipe
    let reader = LineReader { line in
      DispatchQueue.main.async { MainActor.assumeIsolated { self.handle(line) } }  // in line order
    }
    pipe.fileHandleForReading.readabilityHandler = { h in
      let d = h.availableData
      if d.isEmpty { h.readabilityHandler = nil; return }
      reader.feed(d)
    }
    p.terminationHandler = { pr in
      let status = pr.terminationStatus
      DispatchQueue.main.async {  // after the queued output lines
        MainActor.assumeIsolated {
          self.running = false
          if status != 0 && self.error == nil { self.error = "The setup stopped unexpectedly (code \(status))." }
        }
      }
    }
    do { try p.run() } catch { self.error = error.localizedDescription; running = false }
  }

  func handle(_ line: String) {
    let parts = line.split(separator: " ", maxSplits: 1).map(String.init)
    guard let cmd = parts.first else { return }
    let arg = parts.count > 1 ? parts[1] : ""
    switch cmd {
    case "STEP": if let i = steps.firstIndex(where: { $0.key == arg }) { step = i; progress = 0 }
    case "PROGRESS": progress = (Double(arg) ?? 0) / 100
    case "ERROR":  // "<key> <text>": the text is for the player; the installed edition is still the marker's
      error = String(arg.drop(while: { $0 != " " }).dropFirst()); edition = Launcher.installedEdition()
    case "WARNING": warning = String(arg.drop(while: { $0 != " " }).dropFirst())  // "<key> <text>"
    case "EDITION": edition = String(arg.drop(while: { $0 != " " }).dropFirst())  // "<id> <edition: tier>"
    case "DONE": step = steps.count; installed = true; inputs = []; Launcher.applyGameIcon()
    default: break
    }
  }

  func play() {
    guard unquarantine() else { return }
    let fm = FileManager.default
    try? fm.createDirectory(at: Launcher.launchLogURL.deletingLastPathComponent(), withIntermediateDirectories: true)
    fm.createFile(atPath: Launcher.launchLogURL.path, contents: nil)  // the run's own console output (crashes included)
    let p = Process()
    p.executableURL = Launcher.runtimeURL.appendingPathComponent("OpenHP1")
    p.arguments = ["--root", Launcher.gameURL.path]
    p.currentDirectoryURL = Launcher.gameURL
    if let log = FileHandle(forWritingAtPath: Launcher.launchLogURL.path) { p.standardOutput = log; p.standardError = log }
    p.standardInput = FileHandle.nullDevice
    p.terminationHandler = { pr in
      let status = pr.terminationStatus
      DispatchQueue.main.async {
        MainActor.assumeIsolated {
          self.playing = false
          if status != 0 { self.error = "The game stopped with an error (code \(status)). See the log: More → Open Logs." }
        }
      }
    }
    do { try p.run(); playing = true; error = nil } catch { self.error = error.localizedDescription }
  }

  func resetSettings() {
    let a = NSAlert()
    a.messageText = "Reset settings?"
    a.informativeText = "Video, sound and control settings go back to the game's defaults. Saved games are kept."
    a.addButton(withTitle: "Reset"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    for n in ["Default.ini", "DefUser.ini"] {  // the overlay OpenHP1 writes; the game's own ini files are never changed
      let u = Launcher.settingsURL.appendingPathComponent(n)
      if FileManager.default.fileExists(atPath: u.path) { try? FileManager.default.trashItem(at: u, resultingItemURL: nil) }
    }
  }

  func open(_ u: URL) {
    try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
    NSWorkspace.shared.open(u)
  }

  func about() {
    NSApp.orderFrontStandardAboutPanel(options: [
      .applicationName: "OpenHP1 Launcher",
      .credits: NSAttributedString(string:
        "OpenHP1 runs Harry Potter and the Philosopher's Stone (PC, 2001) from your own copy of the game. "
        + "No game files are included. Game data: \(Launcher.gameURL.path)"),
    ])
  }

  // Versions: launcher/VERSION (Info.plist OpenHP1Version, e.g. "0.1.0-beta"; a "-" suffix marks a pre-release build)
  // against the GitHub releases. A final build is offered final releases only; a pre-release build also pre-releases.
  nonisolated static let version = Bundle.main.infoDictionary?["OpenHP1Version"] as? String ?? "0"
  static let updateKey = (Bundle.main.infoDictionary?["OpenHP1UpdateKey"] as? String).flatMap { Data(base64Encoded: $0) }
  // a < b by semver precedence (semver.org, item 11): dot-separated numbers; then a pre-release ("-" suffix) is below its
  // release, and pre-releases compare identifier by identifier — numeric ones as numbers and below alphanumeric ones,
  // those in ASCII order, a shorter list below a longer one with the same start (1.0-beta < 1.0-beta.2 < 1.0-rc1 < 1.0).
  // "+" build metadata is ignored.
  nonisolated static func older(_ a: String, _ b: String) -> Bool {
    func parts(_ v: String) -> (nums: [Int], pre: [Substring]) {
      let v = v.prefix(while: { $0 != "+" })
      let dash = v.firstIndex(of: "-")
      let pre = dash.map { v[v.index(after: $0)...].split(separator: ".", omittingEmptySubsequences: false) } ?? []
      return (v[..<(dash ?? v.endIndex)].split(separator: ".").map { Int($0) ?? 0 }, pre)
    }
    let (x, xp) = parts(a), (y, yp) = parts(b)
    for i in 0..<max(x.count, y.count) {
      let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
      if p != q { return p < q }
    }
    if xp.isEmpty || yp.isEmpty { return !xp.isEmpty && yp.isEmpty }
    for (p, q) in zip(xp, yp) where p != q {
      switch (Int(p), Int(q)) {
      case let (m?, n?): return m < n
      case (.some, nil): return true
      case (nil, .some): return false
      case (nil, nil): return p.unicodeScalars.lexicographicallyPrecedes(q.unicodeScalars)
      }
    }
    return xp.count < yp.count
  }
  typealias Release = (version: String, page: URL, zip: URL?, sig: URL?)
  // The newest release in the feed (GitHub lists newest first) this build may update to, if it is newer than `current`.
  nonisolated static func pick(_ rels: [[String: Any]], current: String) -> Release? {
    for rel in rels {
      if rel["draft"] as? Bool == true || (rel["prerelease"] as? Bool == true && !current.contains("-")) { continue }
      guard let tag = rel["tag_name"] as? String, let page = (rel["html_url"] as? String).flatMap(URL.init) else { continue }
      let v = String(tag.drop(while: { $0 == "v" }))
      guard older(current, v) else { return nil }
      let assets = rel["assets"] as? [[String: Any]] ?? []  // the release also carries the Windows/Linux packages
      func url(_ name: String?) -> URL? {
        assets.first { $0["name"] as? String == name }.flatMap { ($0["browser_download_url"] as? String).flatMap(URL.init) }
      }
      let zip = assets.compactMap { $0["name"] as? String }.first { $0.contains("Launcher") && $0.hasSuffix(".zip") }
      return (v, page, url(zip), url(zip.map { $0 + ".sig" }))
    }
    return nil
  }
  @Published var newer: Release?
  @Published var updating = false
  static var releases: URL {  // OPENHP1_RELEASES_URL: a test feed (file:// works)
    URL(string: ProcessInfo.processInfo.environment["OPENHP1_RELEASES_URL"]
        ?? "https://api.github.com/repos/artemnikonov/openhp1/releases?per_page=30")!
  }
  // manual: tell the player the result even when there is nothing new or the check failed
  func checkForUpdate(manual: Bool = false) {
    URLSession.shared.dataTask(with: Launcher.releases) { data, _, _ in
      let rels = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [[String: Any]] }
      let found = rels.flatMap { Launcher.pick($0, current: Launcher.version) }
      DispatchQueue.main.async {
        MainActor.assumeIsolated {
          if let f = found { self.newer = f; return }
          guard manual else { return }
          let a = NSAlert()
          a.messageText = rels == nil ? "Could not check for updates." : "OpenHP1 Launcher \(Launcher.version) is up to date."
          a.runModal()
        }
      }
    }.resume()
  }
  // Download the new Launcher (OpenHP1 is inside it), verify it, put it in place of this one and start it. Game data,
  // saves and settings live outside the app and are kept. Not while the game or a setup runs from inside this app.
  func updateLauncher() {
    guard let n = newer, !playing, !running else { return }
    guard let zip = n.zip, let sig = n.sig, let key = Launcher.updateKey else {
      error = "This update can't be verified automatically. Download it from the release page."
      NSWorkspace.shared.open(n.page); return
    }
    let me = Bundle.main.bundleURL
    if me.path.contains("/AppTranslocation/") {
      error = "Move OpenHP1 Launcher to the Applications folder, open it from there and try again."; return
    }
    updating = true; error = nil
    Task {
      let r: Result<URL, UpdateError>
      do {
        let (s, _) = try await URLSession.shared.data(from: sig)
        let (file, _) = try await URLSession.shared.download(from: zip)
        r = await Task.detached { Launcher.verifiedApp(zip: file, sig: s, key: key, version: n.version) }.value
          .flatMap { new in
            defer { try? FileManager.default.removeItem(at: new.deletingLastPathComponent()) }
            do { _ = try FileManager.default.replaceItemAt(me, withItemAt: new); return .success(me) }
            catch { return .failure(UpdateError(text: "Could not update: \(error.localizedDescription)")) }
          }
      } catch { r = .failure(UpdateError(text: "Download failed: \(error.localizedDescription)")) }
      switch r {
      case .success(let app):
        let p = Process()  // start the new copy once this one has quit
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", app.path]
        try? p.run()
        NSApp.terminate(nil)
      case .failure(let e):
        updating = false; error = e.text
      }
    }
  }
  struct UpdateError: Error { let text: String }
  nonisolated static func run(_ tool: String, _ args: [String]) -> Int32 {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: tool); p.arguments = args
    p.standardOutput = FileHandle.nullDevice; p.standardError = FileHandle.nullDevice
    do { try p.run() } catch { return -1 }
    p.waitUntilExit()
    return p.terminationStatus
  }
  // The new app, unpacked into a temporary folder, only if every check passes. The signature is checked before the zip
  // is opened. Quarantine: the verified copy is installed without the quarantine flag (cleared here on purpose): a
  // quarantined app would be translocated and macOS would refuse to run the HP1Setup and OpenHP1 inside it.
  nonisolated static func verifiedApp(zip: URL, sig: Data, key: Data, version: String) -> Result<URL, UpdateError> {
    let fm = FileManager.default
    let tmp = fm.temporaryDirectory.appendingPathComponent("openhp1-launcher-\(UUID().uuidString)")
    func no(_ t: String) -> Result<URL, UpdateError> { try? fm.removeItem(at: tmp); return .failure(UpdateError(text: t)) }
    guard let pk = try? Curve25519.Signing.PublicKey(rawRepresentation: key) else { return no("This Launcher's update key is invalid.") }
    let z = tmp.appendingPathComponent("u.zip")
    guard (try? fm.createDirectory(at: tmp, withIntermediateDirectories: true)) != nil, (try? fm.moveItem(at: zip, to: z)) != nil
    else { return no("Could not update: no temporary folder.") }
    guard let s = Data(base64Encoded: String(decoding: sig, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)),
          let bytes = try? Data(contentsOf: z, options: .mappedIfSafe), pk.isValidSignature(s, for: bytes)
    else { return no("The download is not signed by OpenHP1, so it was not installed.") }
    guard run("/usr/bin/ditto", ["-x", "-k", z.path, tmp.path]) == 0,
          let app = (try? fm.contentsOfDirectory(at: tmp, includingPropertiesForKeys: nil))?.first(where: { $0.pathExtension == "app" }),
          let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any],
          info["CFBundleIdentifier"] as? String == bundleID
    else { return no("The downloaded file is not an OpenHP1 Launcher.") }
    guard info["OpenHP1Version"] as? String == version else { return no("The downloaded Launcher is not version \(version).") }
    guard run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]) == 0
    else { return no("The downloaded Launcher's code signature is broken.") }
    _ = run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
    return .success(app)
  }
}

struct MainView: View {
  @StateObject var b = Launcher()
  var setup: Bool { !b.installed || b.running || !b.inputs.isEmpty }
  var status: String {
    if b.running { return "Installing…" }
    if b.playing { return "Playing" }
    return b.installed ? "Installed" : "Not installed"
  }
  var body: some View {
    VStack(spacing: 0) {
      HStack(spacing: 16) {
        VStack(alignment: .leading, spacing: 4) {
          Text("Harry Potter and the Philosopher's Stone").font(.system(size: 22, weight: .bold, design: .serif))
            .foregroundStyle(.white)
          Text("OpenHP1 · \(status)").foregroundStyle(.white.opacity(0.75))
        }
        Spacer()
      }
      .padding(.horizontal, 24).padding(.vertical, 18)
      .background(LinearGradient(colors: [Color(red: 0.42, green: 0.10, blue: 0.10), Color(red: 0.12, green: 0.05, blue: 0.08)],
                                 startPoint: .topLeading, endPoint: .bottomTrailing))
      if let n = b.newer {
        HStack(spacing: 10) {
          Image(systemName: "arrow.down.circle.fill").foregroundStyle(.blue)
          Text("Launcher \(n.version) is available").fontWeight(.medium)
          Spacer()
          if b.updating { ProgressView().controlSize(.small) } else {
            Link("What's new", destination: n.page)
            Button("Update Launcher") { b.updateLauncher() }.disabled(b.running || b.playing)
              .help(b.playing ? "Quit the game first" : "")
          }
        }
        .padding(.horizontal, 24).padding(.vertical, 10).background(Color.blue.opacity(0.1))
      }
      VStack(alignment: .leading, spacing: 14) {
        if setup { setupView } else {
          Text("Ready to play.").font(.title3)
          Text("Saved games and settings are kept in \(Launcher.gameURL.path). To install again from other game files, drop them here.")
            .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        if let ed = b.edition { Text("Game: \(ed)").foregroundStyle(.secondary) }
        if let w = b.warning { Text(w).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true).textSelection(.enabled) }
        if let e = b.error { Text(e).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true).textSelection(.enabled) }
        Spacer(minLength: 0)
        HStack {
          Menu("More") {
            Button("Open Saved Games Folder") { b.open(Launcher.savesURL) }.disabled(!b.installed)
            Button("Open Logs") { b.open(Launcher.logsURL) }
            Button("Reset Settings…") { b.resetSettings() }.disabled(!b.installed)
            Divider()
            Button("Check for Updates") { b.checkForUpdate(manual: true) }
            Button("About OpenHP1 Launcher") { b.about() }
          }.menuStyle(.borderlessButton).fixedSize()
          Spacer()
          if b.installed && !b.running && b.inputs.isEmpty {
            Button { b.play() } label: { Text("Play").frame(minWidth: 90) }
              .keyboardShortcut(.defaultAction).controlSize(.large).disabled(b.playing)
          } else {
            Button(b.installed ? "Install Again" : "Install") { b.install() }
              .keyboardShortcut(.defaultAction).controlSize(.large).disabled(b.inputs.isEmpty || b.running || b.playing || b.updating)
              .help(b.playing ? "Quit the game first" : "")
          }
        }
      }
      .padding(24)
    }
    .frame(width: 560, height: setup ? 500 : (b.newer == nil ? 300 : 342))
    .onAppear { b.checkForUpdate() }
    .onDrop(of: [.fileURL], isTargeted: nil) { items in
      for item in items {
        _ = item.loadObject(ofClass: URL.self) { url, _ in
          if let url { Task { @MainActor in b.add([url]) } }
        }
      }
      return true
    }
  }

  var setupView: some View {
    VStack(alignment: .leading, spacing: 14) {
      Text("Add your copy of the game: the installed game folder, the game CD or its image (.iso, .bin/.cue, .mdf), its installer (Setup.exe with its .bin files, or the CD's setup folder), or a .zip of the game folder. Supported: US 1.53 (exact), UK 1.1 and the Russian EA release (supported). The game data is copied to your Mac from your own copy; nothing of the game is included here.")
        .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
      VStack(alignment: .leading, spacing: 4) {
        if b.inputs.isEmpty { Text("Drop files here, or").foregroundStyle(.secondary) }
        ForEach(b.inputs, id: \.self) { u in
          Label(u.lastPathComponent, systemImage: "doc").lineLimit(1).truncationMode(.middle)
        }
        HStack {
          Button("Add…") { b.choose() }.disabled(b.running)
          if !b.inputs.isEmpty { Button("Clear") { b.inputs = [] }.disabled(b.running) }
        }
      }
      .frame(maxWidth: .infinity, alignment: .leading).padding(12)
      .background(RoundedRectangle(cornerRadius: 10).strokeBorder(.secondary.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [5])))
      if b.running || b.step >= 0 {
        VStack(alignment: .leading, spacing: 6) {
          ForEach(Array(steps.enumerated()), id: \.offset) { i, s in
            HStack(spacing: 8) {
              Image(systemName: i < b.step ? "checkmark.circle.fill" : (i == b.step && b.running ? "circle.dotted" : "circle"))
                .foregroundStyle(i < b.step ? .green : .secondary)
              Text(s.text).foregroundStyle(i <= b.step ? .primary : .secondary)
            }
          }
        }
        if steps.indices.contains(b.step) && !["find", "unpack"].contains(steps[b.step].key) && b.running { ProgressView(value: b.progress) }
      }
    }
  }
}

// Command-line checks never start the app (and never open a window): any first argument starting with "--".
@main enum Entry {
  static func main() {
    let a = CommandLine.arguments
    guard a.count > 1, a[1].hasPrefix("--") else { LauncherApp.main(); return }
    switch a[1] {
    case "--verify-update" where a.count == 6:
      guard let sig = FileManager.default.contents(atPath: a[3]), let key = Data(base64Encoded: a[4]) else { print("FAIL no sig or key"); exit(1) }
      switch Launcher.verifiedApp(zip: URL(fileURLWithPath: a[2]), sig: sig, key: key, version: a[5]) {
      case .success(let app): print("OK \(app.path)"); exit(0)
      case .failure(let e): print("FAIL \(e.text)"); exit(1)
      }
    case "--pick-release" where a.count == 4:
      let rels = FileManager.default.contents(atPath: a[2]).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [[String: Any]] }
      guard let rels else { print("FAIL feed"); exit(1) }
      if let r = Launcher.pick(rels, current: a[3]) { print("\(r.version) zip=\(r.zip?.lastPathComponent ?? "-") sig=\(r.sig?.lastPathComponent ?? "-")") }
      else { print("none") }
      exit(0)
    default:
      FileHandle.standardError.write(Data("usage: Launcher --verify-update <zip> <sig> <key> <version> | --pick-release <json> <version>\n".utf8))
      exit(2)
    }
  }
}

struct LauncherApp: App {
  @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
  var body: some Scene {
    WindowGroup { MainView() }.windowResizability(.contentSize)
  }
}
