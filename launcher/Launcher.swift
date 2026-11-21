// "HPEngine Launcher" (macOS): installs the game data from the player's own copy of Harry Potter (2001, PC) with the
// bundled HP1Setup (launcher/HP1Setup; progress from its STEP/PROGRESS/EDITION/WARNING/ERROR/DONE lines), starts the bundled HPEngine
// with --root, and updates itself — and with it HPEngine — from GitHub releases. Built by hpengine/packaging/make_launcher.sh.
// Self-update: only a zip whose Ed25519 signature (<zip>.sig in the same release) verifies against the public key
// pinned into this build (Info.plist HPEngineUpdateKey, launcher/update_sign.swift) is unpacked; the new app must have
// our bundle id, the advertised version and a valid code signature. Command-line checks (no window):
//   Launcher --verify-update <zip> <sig file> <public key, base64> <version>   (launcher/test_update.sh)
//   Launcher --pick-release <releases.json> <current version>
import AppKit
import CryptoKit
import GameController
import SwiftUI

let bundleID = "io.github.hpengine.launcher"  // make_launcher.sh Info.plist

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
  @Published var warning: String?  // a run's override miss (Launcher.overrideWarning); HP1Setup's WARNING lines are log-only (author 2026-11-20)
  @Published var running = false
  @Published var playing = false
  @Published var installed = Launcher.isInstalled()
  @Published var edition = Launcher.installedEdition()  // "US 1.53: exact" (HP1Setup's EDITION line, kept in the marker)
  @Published var runError: String?  // the error of a failed game run: its banner offers "Open log"
  // Per game (GameScope): HP1's errors in `error`, HP2's here, the launcher's own (its update, its location) on both tabs.
  @Published var hp2Error: String?
  @Published var hp2RunError: String?
  @Published var launcherError: String?
  var runLogsBefore: [String]?, hp2RunLogsBefore: [String]?   // each game's run journals before its last run started (GameScope.logToOpen)
  @Published var modsOpen = false   // the Mods page in place of the selected tab's game page
  // Focus and gamepad (extension below): the focused target, the ring (once the pad or the keys moved focus), a pad
  // connected (the button hints), the open list (Menu, Language, Start map) and the targets the window registered.
  @Published var padFocus: String?
  @Published var padUsed = false
  @Published var padConnected = false
  @Published var popup: Popup?
  @Published var listOffset: CGFloat = 0   // the open list's scroll offset, read from its NSScrollView (its indicator)
  weak var listScroller: NSScrollView?      // the open list's NSScrollView (ScrollProbe): the indicator's drag scrolls it
  var padTargets: [PadTarget] = []
  var focusBeforePopup: String?
  var padLast: UInt32 = 0
  static let page = 6   // Page Up / Page Down in an open list: about the items a list under a control holds
  var forcedLooks: [String: Int] = [:]   // --render-states only: target id -> 1 hover, 2 pressed (the look without a pointer)

  static let home = NSHomeDirectory()
  static let dataURL = URL(fileURLWithPath: home + "/Library/Application Support/HPEngine")
  static let gameURL = dataURL.appendingPathComponent("game")  // the install root: game data, Save/, HPEngineData/ (settings, logs)
  static let savesURL = gameURL.appendingPathComponent("Save")
  static let settingsURL = gameURL.appendingPathComponent("HPEngineData")
  static let logsURL = settingsURL.appendingPathComponent("logs")
  static let launchLogURL = URL(fileURLWithPath: home + "/Library/Logs/HPEngine.log")
  // Per game: the capture of a run's console output (HPEngine.log, HPEngine-HP2.log) and the run logs HPEngine writes.
  static func launchLog(hp2: Bool) -> URL { URL(fileURLWithPath: GameScope.consoleLog(launchLogURL.path, hp2: hp2)) }
  static func logsDir(hp2: Bool) -> URL { URL(fileURLWithPath: GameScope.logsDir(gameURL.path, hp2: hp2)) }
  // The app is universal: each slice of this binary runs the engine of its own architecture (make_launcher.sh puts
  // osx-arm64 in Resources/HPEngine and osx-x64 in Resources/HPEngine-x64; Launcher --runtime-path prints the choice).
  #if arch(x86_64)
  nonisolated static let runtimeName = "HPEngine-x64"
  #else
  nonisolated static let runtimeName = "HPEngine"
  #endif
  nonisolated static var runtimeURL: URL { Bundle.main.resourceURL!.appendingPathComponent(runtimeName) }
  // HP1Setup writes <root>/.hp1-installed last: an interrupted copy does not count. root: HP1's game/ or HP2's hp2GameURL.
  static func isInstalled() -> Bool { isInstalled(gameURL) }
  static func isInstalled(_ root: URL) -> Bool { FileManager.default.fileExists(atPath: root.path + "/.hp1-installed") }
  static func installedEdition() -> String? { installedEdition(gameURL) }
  static func installedEdition(_ root: URL) -> String? {
    (try? String(contentsOfFile: root.path + "/.hp1-installed", encoding: .utf8))?
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
    setupError(nil); step = -1
  }
  // The setup's errors and arguments are the selected tab's game (the tabs do not switch while a setup runs).
  func setupError(_ e: String?) { if hp2Tab { hp2Error = e } else { error = e } }
  static func setupArguments(hp2: Bool, inputs: [String]) -> [String] {
    (hp2 ? ["--game", "hp2", "--dest", hp2GameURL.path] : ["--dest", gameURL.path]) + inputs
  }

  // A downloaded Launcher carries the quarantine flag on everything inside; macOS then refuses to run the bundled
  // HP1Setup and HPEngine. Clear it on the whole bundle (not possible while macOS runs the app from a translocated copy).
  func unquarantine() -> Bool {
    if Bundle.main.bundlePath.contains("/AppTranslocation/") {
      launcherError = "Move HPEngine Launcher to the Applications folder, open it from there and try again."
      return false
    }
    launcherError = nil
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
    p.arguments = ["-dr", "com.apple.quarantine", Bundle.main.bundlePath]
    try? p.run(); p.waitUntilExit()
    return true
  }

  func install() {
    guard !inputs.isEmpty, !running, !playing, !updating, unquarantine() else { return }  // nothing swaps the data or the app under it
    running = true; setupError(nil); warning = nil; step = 0; progress = 0
    let p = Process()
    current = p
    p.executableURL = Launcher.runtimeURL.appendingPathComponent("HP1Setup")
    p.arguments = Launcher.setupArguments(hp2: hp2Tab, inputs: inputs.map(\.path))
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
          if status != 0 && (self.hp2Tab ? self.hp2Error : self.error) == nil { self.setupError("The setup stopped unexpectedly (code \(status)).") }
          if self.hp2Tab { self.hp2Scan() }
        }
      }
    }
    do { try p.run() } catch { setupError(error.localizedDescription); running = false }
  }

  func handle(_ line: String) {
    let parts = line.split(separator: " ", maxSplits: 1).map(String.init)
    guard let cmd = parts.first else { return }
    let arg = parts.count > 1 ? parts[1] : ""
    switch cmd {
    case "STEP": if let i = steps.firstIndex(where: { $0.key == arg }) { step = i; progress = 0 }
    case "PROGRESS": progress = (Double(arg) ?? 0) / 100
    case "ERROR":  // "<key> <text>": the text is for the player; the installed edition is still the marker's
      setupError(String(arg.drop(while: { $0 != " " }).dropFirst())); edition = Launcher.installedEdition(hp2Tab ? Launcher.hp2GameURL : Launcher.gameURL)
    // WARNING (modified / localized / unverified) and REPORT lines are for the log only (author 2026-11-20): every copy runs
    case "EDITION": edition = String(arg.drop(while: { $0 != " " }).dropFirst())  // "<id> <name: tier>"; the tier is never shown
    case "DONE": step = steps.count; inputs = []; if hp2Tab { hp2Page = "Main"; hp2Scan() } else { installed = true }
    default: break
    }
  }

  func play() {
    loadMods()   // the folders as they are now
    if let gone = Launcher.missingText(mods) { error = gone; return }
    guard unquarantine() else { return }
    let fm = FileManager.default
    try? fm.createDirectory(at: Launcher.launchLogURL.deletingLastPathComponent(), withIntermediateDirectories: true)
    fm.createFile(atPath: Launcher.launchLogURL.path, contents: nil)  // the run's own console output (crashes included)
    let p = Process()
    p.executableURL = Launcher.runtimeURL.appendingPathComponent("HPEngine")
    p.arguments = Launcher.playArguments(game: Launcher.gameURL, map: startMap)
    p.currentDirectoryURL = Launcher.gameURL
    if let log = FileHandle(forWritingAtPath: Launcher.launchLogURL.path) { p.standardOutput = log; p.standardError = log }
    p.standardInput = FileHandle.nullDevice
    p.terminationHandler = { pr in
      let status = pr.terminationStatus
      DispatchQueue.main.async {
        MainActor.assumeIsolated {
          self.playing = false
          self.updateModsNotice()   // the run may have written slots
          let said = ((try? String(contentsOf: Launcher.launchLogURL, encoding: .utf8)) ?? "").components(separatedBy: "\n")
          self.warning = Launcher.overrideWarning(said)
          if status != 0 {
            self.error = Launcher.errorBanner(said, code: status)
            self.runError = self.error
          }
        }
      }
    }
    runLogsBefore = GameScope.runLogs(Launcher.logsDir(hp2: false).path)
    do { try p.run(); playing = true; error = nil } catch { self.error = error.localizedDescription; return }
    Launcher.handFocus(to: p.processIdentifier, log: Launcher.launchLogURL)
  }

  // The game is our child process, not launched through LaunchServices, so nothing activates it; from macOS 14 an app's
  // own activate request (SDL_RaiseWindow) is only honoured if the active app yields to it. The launcher is the active
  // app when Play is pressed: once the game has registered as an application (SDL video init), activate it from here.
  // Without this its window stayed inactive (no key focus, OS cursor visible, "capture on ... focus False") until a click.
  // The app registers before its window exists (HP2 shows its window seconds later, after the splash): activating it
  // then leaves the later window without key focus, and closing the splash hands activation back to the launcher (author
  // 2026-11-18). So activate the game whenever it is not the active app until its console log says, after "core warm"
  // (printed after Splash.Close), that its window has input focus ("[Focus] gained"), or the deadline / the process ends.
  // Active without a key window is the game's own half (SdlMouseCapture.MacKeyWindowWhenActive).
  // Not the first "[Focus] gained" either (author 2026-11-20: still only every other launch): run logs show "[Focus] gained" at "core
  // warm" and then "capture on ... focus False" until a click — macOS handed activation back after the game had it. So the
  // hand-off ends only when the game has stayed the active app for `settle` seconds in a row after that line.
  static func handFocus(to pid: pid_t, log: URL, deadline: Date = Date().addingTimeInterval(30), settle: TimeInterval = 2) {
    var activeSince: Date? = nil
    Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { t in
      MainActor.assumeIsolated {
        if Date() > deadline || kill(pid, 0) != 0 { t.invalidate(); return }
        guard let app = NSRunningApplication(processIdentifier: pid) else { return }
        // The game's own word that its window has input focus after the splash ("[Focus] gained" after "core warm"), not a
        // sample of isActive: HP1 prints "core warm" ~0.2 s after the splash closed, while macOS was still handing activation
        // back to the launcher, so "active" was seen once and the window was left without focus (author 2026-11-19).
        let said = (try? String(contentsOf: log, encoding: .utf8)) ?? ""
        let gained = said.range(of: "core warm").map { said[$0.upperBound...].contains("[Focus] gained") } ?? false
        if app.isActive {
          if gained {
            if activeSince == nil { activeSince = Date() }
            if Date().timeIntervalSince(activeSince!) >= settle { t.invalidate() }
          }
          return
        }
        activeSince = nil
        if #available(macOS 14.0, *) { app.activate(from: .current) } else { app.activate(options: .activateIgnoringOtherApps) }
      }
    }
  }

  // What the engine itself said when it stopped with an error: the last line of its console output that names the cause.
  // Exit code 5 (mods): its "[Mods] ..." line; 4 (no game folder): the InstallRoot message; else the crash line (the run
  // log's "[Crash] ...", or the runtime's own "Unhandled exception. ..."); 6 (no such start map): its "[Map] ..." line.
  // Same rules as launcher/Desktop/Logic.cs EngineError.
  nonisolated static func errorLine(_ lines: [String], code: Int32) -> String? {
    if code == 6 { return lines.last { $0.hasPrefix("[Map] ") } }
    if code == 5 { return lines.last { $0.hasPrefix("[Mods] ") } }
    if code == 4 { return lines.last { $0.contains("not a game install") || $0.hasPrefix("game install not found") } }
    return lines.last { $0.hasPrefix("[Crash] ") } ?? lines.last { $0.hasPrefix("Unhandled exception. ") }
  }
  // Author 2026-11-20: the only thing the launcher warns about besides a missing file. The game logs "[Overrides] WARNING: <Class.Function>
  // is not in the loaded scripts: ..." when a port script fix finds no target in this copy's scripts (mirrored by EngineError.OverrideWarning).
  nonisolated static func overrideWarning(_ lines: [String]) -> String? {
    let miss = lines.filter { $0.hasPrefix("[Overrides] WARNING: ") && $0.contains(" is not in the loaded scripts") }
      .map { String($0.dropFirst(21).prefix { $0 != " " }) }
    return miss.isEmpty ? nil : "\(miss.count) of the port's fixes do not apply to this copy of the game (\(miss.joined(separator: ", "))), so those parts may not work as intended. The log has the details."
  }
  // The banner (author 2026-11-17): what happened in plain words, the engine's line on its own line; no file path ("Open log").
  nonisolated static func errorBanner(_ lines: [String], code: Int32) -> String {
    "The game stopped with an error (code \(code))." + (errorLine(lines, code: code).map { "\n" + $0.trimmingCharacters(in: .whitespaces) } ?? "")
  }
  // The error banner's "Open log": that run's journal, else the console capture, else the logs folder (GameScope.logToOpen).
  func logToOpen(hp2: Bool) -> String {
    GameScope.logToOpen(Launcher.logsDir(hp2: hp2).path, console: Launcher.launchLog(hp2: hp2).path, before: hp2 ? hp2RunLogsBefore : runLogsBefore)
  }
  func openRunLog(hp2: Bool) {
    let f = logToOpen(hp2: hp2)
    if f == Launcher.logsDir(hp2: hp2).path { open(URL(fileURLWithPath: f)) } else { NSWorkspace.shared.open(URL(fileURLWithPath: f)) }
  }
  // The error banner's × (and pad B on its buttons): clears the shown error only (GameScope.dismiss).
  func dismissError() {
    (error, hp2Error, launcherError) = GameScope.dismiss(hp2Tab: hp2Tab, hp1: error, hp2: hp2Error, launcher: launcherError)
    if error == nil { runError = nil }
    if hp2Error == nil { hp2RunError = nil }
  }

  func resetSettings() {
    let a = NSAlert()
    a.messageText = "Reset settings?"
    a.informativeText = "Video, sound and control settings go back to the game's defaults. Saved games are kept. The old files are moved to HPEngineData/settings-backup."
    a.addButton(withTitle: "Reset"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    let fm = FileManager.default, bak = Launcher.settingsURL.appendingPathComponent("settings-backup")
    let f = DateFormatter(); f.dateFormat = "yyyyMMdd-HHmmss"; let stamp = f.string(from: Date())
    for n in ["Default.ini", "DefUser.ini"] {  // the overlay HPEngine writes; the game's own ini files are never changed
      let u = Launcher.settingsURL.appendingPathComponent(n), to = bak.appendingPathComponent("\(n).\(stamp)")
      guard fm.fileExists(atPath: u.path) else { continue }
      try? fm.createDirectory(at: bak, withIntermediateDirectories: true); try? fm.removeItem(at: to); try? fm.moveItem(at: u, to: to)
    }
  }

  // "Reset Gamepad Bindings". PAD RESET RULE (the same text in HP1.Core UEInput.Gamepad.cs and launcher/Desktop/Logic.cs
  // PadReset; the launchers edit the files, the engine only reads them): the engine's saved overlay lives in
  // <game>/HPEngineData — DefUser.ini for the User scope, Default.ini for the System scope. "Never rebound" = in DefUser.ini
  // no line of [Engine.Input] whose key starts with "Joy" (any case), and in Default.ini no GamepadBindingsSaved line in
  // [HPEngine]. Reset deletes exactly those lines and nothing else (bytes of every other line kept): the game's own
  // DefUser.ini (every Joy* line empty) shows through again and the engine's default pad bindings apply.
  nonisolated static func padDrop(_ text: String, section: String, key: (String) -> Bool) -> (text: String, removed: Int) {
    var kept: [String] = [], inside = false, removed = 0
    for l in text.components(separatedBy: "\n") {
      let t = l.trimmingCharacters(in: .whitespacesAndNewlines)
      if t.count >= 2, t.hasPrefix("["), t.hasSuffix("]") {
        inside = t.dropFirst().dropLast().trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(section) == .orderedSame
      } else if inside, let eq = l.firstIndex(of: "="), eq != l.startIndex, key(l[..<eq].trimmingCharacters(in: .whitespaces)) { removed += 1; continue }
      kept.append(l)
    }
    return (kept.joined(separator: "\n"), removed)
  }
  // Lines a reset would remove (0 = nothing was rebound; the menu item stays enabled, author 2026-11-20). reset: also write the files back.
  nonisolated static func padRebound(game: URL, reset: Bool = false) -> Int {
    let rules: [(String, String, (String) -> Bool)] = [
      ("DefUser.ini", "Engine.Input", { $0.lowercased().hasPrefix("joy") }),
      ("Default.ini", "HPEngine", { $0.caseInsensitiveCompare("GamepadBindingsSaved") == .orderedSame }),
    ]
    var n = 0
    for (file, section, key) in rules {
      let u = game.appendingPathComponent("HPEngineData").appendingPathComponent(file)
      guard let d = FileManager.default.contents(atPath: u.path), let text = String(data: d, encoding: .isoLatin1) else { continue }  // Latin-1: every byte kept
      let r = padDrop(text, section: section, key: key)
      n += r.removed
      if reset && r.removed > 0 { try? r.text.data(using: .isoLatin1)?.write(to: u, options: .atomic) }
    }
    return n
  }
  func resetPad() {
    let a = NSAlert()
    a.messageText = "Reset gamepad bindings?"
    a.informativeText = "The gamepad buttons and the left stick go back to their default actions. Keyboard and mouse bindings and all other settings are kept."
    a.addButton(withTitle: "Reset"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    _ = Launcher.padRebound(game: Launcher.gameURL, reset: true)
  }

  // Removing the installed game (<root> = Application Support/HPEngine/game). keep: everything except Save/, HPEngineData/ and
  // Mods/ goes (HP1Setup merges Save/ and leaves HPEngineData/ alone on a reinstall); all: the whole root. The marker goes
  // FIRST (an interrupted removal reads as "not installed", never as a broken install). `dispose` takes each item (the app:
  // to the Trash, which moves a folder whole and never follows links inside it; --remove-game: deleted; removeItem does not
  // follow links either). `afterMarker`: a test hook (an error thrown there simulates a failure after the marker).
  nonisolated static let keptNames: Set<String> = ["save", "hpenginedata", "mods"]
  nonisolated static func removeGame(root: URL, all: Bool, dispose: (URL) throws -> Void, afterMarker: () throws -> Void = {}) throws {
    let fm = FileManager.default
    guard fm.fileExists(atPath: root.path) else { return }
    for m in [".hp1-installed", ".hp1-installing"] where fm.fileExists(atPath: root.appendingPathComponent(m).path) {
      try fm.removeItem(at: root.appendingPathComponent(m))
    }
    try afterMarker()
    if all { try dispose(root); return }
    for e in try fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) where !keptNames.contains(e.lastPathComponent.lowercased()) {
      try dispose(e)
    }
  }

  func removeGame() {
    let hp2 = hp2Tab, root = hp2 ? Launcher.hp2GameURL : Launcher.gameURL
    guard hp2 ? Launcher.isInstalled(root) : installed, !running, !playing else { return }
    let a = NSAlert()
    a.messageText = "Remove game?"
    a.informativeText = "The installed game copy in \(root.path) is moved to the Trash. Saved games, settings and mods are kept. Your own game files are not touched."
    let cb = NSButton(checkboxWithTitle: "Also delete saved games, settings and mods", target: nil, action: nil)
    cb.sizeToFit(); a.accessoryView = cb
    a.addButton(withTitle: "Remove"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    do {
      try Launcher.removeGame(root: root, all: cb.state == .on) { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }
      // HP2's saved games and settings live beside its install root (Hp2FrontEnd.saveDir, GameScope.dataDir)
      if hp2 && cb.state == .on {
        for d in [Hp2FrontEnd.saveDir(Launcher.gameURL.path), GameScope.dataDir(Launcher.gameURL.path, hp2: true)] where FileManager.default.fileExists(atPath: d) {
          try FileManager.default.trashItem(at: URL(fileURLWithPath: d), resultingItemURL: nil)
        }
      }
      installed = Launcher.isInstalled(); edition = nil; step = -1; setupError(nil); warning = nil
    } catch {
      installed = Launcher.isInstalled()
      setupError("Could not remove the game: \(error.localizedDescription)")
    }
    if hp2 { hp2Page = "Main"; hp2Scan() }
  }

  func open(_ u: URL) {
    try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
    NSWorkspace.shared.open(u)
  }

  // ── Mods (hpengine/packaging/ModOverlay.cs): folders in game/Mods; game/HPEngineData/Mods.ini [Mods] Enabled=<name>
  // lines in order (a later mod wins). The list: enabled mods in that order, then the other folders by name.
  struct Mod: Identifiable, Equatable { let name: String; var enabled: Bool; var missing: Bool; var id: String { name } }
  static let modsIniURL = settingsURL.appendingPathComponent("Mods.ini")
  nonisolated static func modsIniURL(_ game: URL) -> URL { game.appendingPathComponent("HPEngineData/Mods.ini") }
  @Published var mods: [Mod] = []
  // The selected tab's mods, as launcher/Desktop/MainWindow.cs ModsGame / ModsData: HP1 game/Mods with
  // game/HPEngineData/Mods.ini; HP2 <its game folder>/Mods with <HP2Data>/HPEngineData/Mods.ini (the engine's InstallRoot.Hp2DataDir).
  var modsGame: URL? { hp2Tab ? Launcher.hp2Root.map { URL(fileURLWithPath: $0) } : Launcher.gameURL }
  var modsIni: URL { hp2Tab ? URL(fileURLWithPath: GameScope.dataDir(Launcher.gameURL.path, hp2: true)).appendingPathComponent("Mods.ini") : Launcher.modsIniURL }

  nonisolated static func subfolders(_ dir: URL) -> [String] {
    ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
      .filter { !$0.lastPathComponent.hasPrefix(".") && ((try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false) }
      .map(\.lastPathComponent).sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
  }

  nonisolated static func modList(game: URL, ini: URL? = nil) -> [Mod] {
    let folders = subfolders(game.appendingPathComponent("Mods"))
    let on = enabledMods(ini: (try? String(contentsOf: ini ?? modsIniURL(game), encoding: .utf8)) ?? "")
    return on.map { n in Mod(name: folders.first { $0.caseInsensitiveCompare(n) == .orderedSame } ?? n, enabled: true,
                             missing: !folders.contains { $0.caseInsensitiveCompare(n) == .orderedSame }) }
      + folders.filter { f in !on.contains { $0.caseInsensitiveCompare(f) == .orderedSame } }.map { Mod(name: $0, enabled: false, missing: false) }
  }

  func loadMods() {
    loadLanguages()
    mods = modsGame.map { Launcher.modList(game: $0, ini: modsIni) } ?? []
    updateModsNotice()
    updateStartMaps()
  }

  // Add (button or a drop on the Mods page): the bundled HP1Setup --add-mod runs hpengine/packaging/ModInstall.cs, the
  // rule launcher/Desktop calls directly (ModsIni.Add); the mod is listed switched off. One source after the other.
  @Published var addingMod = false
  nonisolated static func addModArguments(install: String, source: String) -> [String] { ["--add-mod", install, source] }
  // HP1Setup's answer: "MOD <name>" -> nil; "ERROR add_mod <why>" -> the page's error (launcher/Desktop/Logic.cs ModsIni.Add's text).
  nonisolated static func addModError(_ out: String) -> String? {
    let lines = out.components(separatedBy: "\n")
    if lines.contains(where: { $0.hasPrefix("MOD ") }) { return nil }
    if let e = lines.first(where: { $0.hasPrefix("ERROR add_mod ") }) { return "Could not add the mod: " + e.dropFirst(14) }
    return "Could not add the mod: the setup stopped unexpectedly."
  }
  func addMods(_ urls: [URL]) {
    guard let game = modsGame, !urls.isEmpty, !playing, !addingMod, unquarantine() else { return }
    addingMod = true
    let tool = Launcher.runtimeURL.appendingPathComponent("HP1Setup"), hp2 = hp2Tab
    DispatchQueue.global().async {
      var failed: String?
      for u in urls { if let e = Launcher.runAddMod(tool: tool, install: game.path, source: u.path) { failed = e; break } }
      DispatchQueue.main.async {
        MainActor.assumeIsolated {
          self.addingMod = false
          if hp2 { self.hp2Error = failed } else { self.error = failed }
          self.loadMods()
        }
      }
    }
  }
  // One HP1Setup --add-mod run (blocking: off the main thread); nil: added, else the page's error.
  nonisolated static func runAddMod(tool: URL, install: String, source: String) -> String? {
    let p = Process(), pipe = Pipe()
    p.executableURL = tool; p.arguments = addModArguments(install: install, source: source)
    p.standardInput = FileHandle.nullDevice; p.standardOutput = pipe; p.standardError = pipe
    do { try p.run() } catch { return "Could not add the mod: \(error.localizedDescription)" }
    let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    p.waitUntilExit()
    return addModError(out)
  }
  func chooseMods() {
    let p = NSOpenPanel()
    p.canChooseDirectories = true; p.canChooseFiles = true; p.allowsMultipleSelection = true
    p.message = "A mod's folder, or its archive (.zip, .7z, .rar)"
    if p.runModal() == .OK { addMods(p.urls) }
  }

  // Remove: asked first; switched off in Mods.ini, then its folder goes to the Trash (as launcher/Desktop ModsIni.Remove).
  func removeMod(_ name: String) {
    guard let game = modsGame, !playing else { return }
    let a = NSAlert()
    a.messageText = "Remove mod?"
    a.informativeText = "Remove the mod \(name)? Its folder is moved to the Trash."
    a.addButton(withTitle: "Remove"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    mods.removeAll { $0.name == name }
    saveMods()
    if let dir = Launcher.child(game.appendingPathComponent("Mods"), name, dir: true) {
      do { try FileManager.default.trashItem(at: dir, resultingItemURL: nil) } catch { setupError("Could not remove the mod: \(error.localizedDescription)") }
    }
    loadMods()
  }

  // ── Game language (NOT ORIGINAL): the languages of the installed data, chosen as the Windows/Linux Launcher does. The
  // rule's owner is hpengine/packaging/GameLanguage.cs (the engine places the chosen folder's files, ModOverlay); this
  // MIRRORS its listing and its write of HPEngineData/Default.ini, byte for byte (launcher/test_update.sh runs both on the
  // same folders and compares): each System/ and System/<digits>/ Default.ini's [Engine.Engine] Language (FConfigFile::Read:
  // no comments, no trimming, quotes stripped, the last duplicate wins; UTF-16 with a BOM, else ISO-8859-1), a 2-4 letter
  // code, the first folder of a code wins, by label without case; the choice writes [HPEngine] DataLanguage, [Engine.Engine]
  // Language and [HPBase.baseNarrator] CurLanguageName = the name in capitals, other lines kept, ISO-8859-1.
  struct LanguageChoice: Equatable { let code: String; let label: String; let folder: String? }
  nonisolated static let languageDefault = "Game default"
  nonisolated static let languageNames: [String: String] = [
    "eng": "English", "int": "English", "fin": "Finnish", "dan": "Danish", "swe": "Swedish", "nor": "Norwegian",
    "fra": "French", "fre": "French", "deu": "German", "ger": "German", "ita": "Italian", "esn": "Spanish", "esp": "Spanish",
    "rus": "Russian", "pol": "Polish", "hun": "Hungarian", "por": "Portuguese", "dut": "Dutch", "nld": "Dutch", "cze": "Czech",
  ]
  nonisolated static func languageSettings(_ game: URL) -> URL { game.appendingPathComponent("HPEngineData/Default.ini") }
  nonisolated static func loadIni(_ u: URL) -> String? {
    guard let d = FileManager.default.contents(atPath: u.path) else { return nil }
    let b = [UInt8](d)
    if b.count > 1 && b.count % 2 == 0 && b[0] == 0xff && b[1] == 0xfe { return String(data: d.dropFirst(2), encoding: .utf16LittleEndian) }
    if b.count > 1 && b.count % 2 == 0 && b[0] == 0xfe && b[1] == 0xff { return String(data: d.dropFirst(2), encoding: .utf16BigEndian) }
    return String(data: d, encoding: .isoLatin1)
  }
  nonisolated static func iniValue(_ text: String, _ section: String, _ key: String) -> String? {
    var inside = false, value: String? = nil
    for line in text.components(separatedBy: CharacterSet(charactersIn: "\r\n")) where !line.isEmpty {
      if line.hasPrefix("[") && line.hasSuffix("]") {
        inside = String(line.dropFirst().dropLast()).caseInsensitiveCompare(section) == .orderedSame; continue
      }
      guard inside, let eq = line.firstIndex(of: "="), String(line[..<eq]).caseInsensitiveCompare(key) == .orderedSame else { continue }
      var v = String(line[line.index(after: eq)...])
      if v.count > 1 && v.hasPrefix("\"") && v.hasSuffix("\"") { v = String(v.dropFirst().dropLast()) }
      value = v
    }
    return value
  }
  nonisolated static func languages(game: URL) -> [LanguageChoice] {
    guard let system = child(game, "System", dir: true) else { return [] }
    let numbered = entries(system).filter { isDir($0) && !$0.lastPathComponent.isEmpty && $0.lastPathComponent.allSatisfy { ("0"..."9").contains($0) } }
      .sorted { $0.lastPathComponent.unicodeScalars.lexicographicallyPrecedes($1.lastPathComponent.unicodeScalars) }
    var choices: [LanguageChoice] = []
    for dir in [system] + numbered {
      guard let ini = child(dir, "Default.ini", dir: false), let text = loadIni(ini),
            let code = iniValue(text, "Engine.Engine", "Language")?.lowercased(),
            (2...4).contains(code.count), code.unicodeScalars.allSatisfy({ ("a"..."z").contains($0) }),
            !choices.contains(where: { $0.code == code }) else { continue }
      choices.append(LanguageChoice(code: code, label: languageNames[code] ?? code, folder: dir == system ? nil : dir.lastPathComponent))
    }
    return choices.enumerated().sorted { a, b in   // stable, as Enumerable.OrderBy with StringComparer.OrdinalIgnoreCase
      let x = a.element.label.uppercased(), y = b.element.label.uppercased()
      return x != y ? x.unicodeScalars.lexicographicallyPrecedes(y.unicodeScalars) : a.offset < b.offset
    }.map(\.element)
  }
  nonisolated static func selectedLanguage(game: URL) -> String? {
    loadIni(languageSettings(game)).flatMap { iniValue($0, "HPEngine", "DataLanguage") }
  }
  // GameLanguage.Set: the section's key replaced (or added at the section's end, or a new section), other lines kept.
  nonisolated static func iniSet(_ text: String, _ section: String, _ key: String, _ value: String?) -> String {
    let nl = text.contains("\r\n") ? "\r\n" : "\n"
    var lines = text.components(separatedBy: "\n").map { l in var l = l; while l.hasSuffix("\r") { l.removeLast() }; return l }
    if lines.last == "" { lines.removeLast() }
    var result: [String] = [], inside = false, placed = false, found = false
    func place() { if !placed, let value { result.append(key + "=" + value) }; placed = true }
    for line in lines {
      let t = line.trimmingCharacters(in: .whitespacesAndNewlines)
      if t.hasPrefix("[") {
        if inside { place() }
        inside = t.caseInsensitiveCompare("[" + section + "]") == .orderedSame; found = found || inside
      }
      if inside, let eq = t.firstIndex(of: "="), eq != t.startIndex,
         t[..<eq].trimmingCharacters(in: .whitespacesAndNewlines).caseInsensitiveCompare(key) == .orderedSame { place(); continue }
      result.append(line)
    }
    if inside { place() }
    if !found, let value { if !result.isEmpty { result.append("") }; result.append("[" + section + "]"); result.append(key + "=" + value) }
    return result.joined(separator: nl) + nl
  }
  struct LanguageError: Error { let text: String }
  nonisolated static func saveLanguage(game: URL, _ code: String?) throws {
    var choice: LanguageChoice? = nil
    if let code {
      guard let c = languages(game: game).first(where: { $0.code == code }) else { throw LanguageError(text: "The selected language is no longer installed: " + code) }
      choice = c
    }
    let u = languageSettings(game)
    var text = FileManager.default.fileExists(atPath: u.path) ? (loadIni(u) ?? "") : ""
    text = iniSet(text, "HPEngine", "DataLanguage", choice?.code)
    text = iniSet(text, "Engine.Engine", "Language", choice?.code)
    text = iniSet(text, "HPBase.baseNarrator", "CurLanguageName", choice.flatMap { languageNames[$0.code]?.uppercased() })
    try FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
    let temp = URL(fileURLWithPath: u.path + ".language-new")
    try text.data(using: .isoLatin1, allowLossyConversion: true)!.write(to: temp)
    if FileManager.default.fileExists(atPath: u.path) { _ = try FileManager.default.replaceItemAt(u, withItemAt: temp) }
    else { try FileManager.default.moveItem(at: temp, to: u) }
  }
  // The chooser's entries: "Game default" (nil), the languages, and a pick whose folder is gone ("Unavailable (<code>)").
  nonisolated static func languageEntries(_ langs: [LanguageChoice], selected: String?) -> [(code: String?, label: String)] {
    var e: [(code: String?, label: String)] = [(nil, languageDefault)] + langs.map { ($0.code, $0.label) }
    if let s = selected, !s.isEmpty, !langs.contains(where: { $0.code == s }) { e.append((s, "Unavailable (\(s))")) }
    return e
  }
  // The Game card's edition text with a multilingual data set (launcher/Desktop/Logic.cs EditionLabel.Describe).
  nonisolated static func editionDisplay(_ edition: String, _ langs: [LanguageChoice]) -> (name: String, detail: String?) {
    let parts = edition.components(separatedBy: ": ")
    let name = parts.count > 1 ? parts.dropLast().joined(separator: ": ") : edition
    if langs.count < 2 { return (name, nil) }
    let nordic = Set(langs.map { $0.code.lowercased() }) == ["dan", "eng", "fin", "nor", "swe"]
    let detail = (name.isEmpty ? "" : "Engine family: \(name). ") + "Languages: " + langs.map(\.label).joined(separator: ", ") + "."
    return (nordic ? "Nordic multilingual data" : "Multilingual game data", detail)
  }
  @Published var languageList: [LanguageChoice] = []
  @Published var language: String?
  func loadLanguages() {
    languageList = installed ? Launcher.languages(game: Launcher.gameURL) : []
    language = installed ? Launcher.selectedLanguage(game: Launcher.gameURL) : nil
  }
  func setLanguage(_ code: String?) {
    guard !playing, !running, code != language else { return }
    do { try Launcher.saveLanguage(game: Launcher.gameURL, code); error = nil }
    catch let e as LanguageError { error = "Could not change game language: " + e.text }
    catch { self.error = "Could not change game language: " + error.localizedDescription }
    loadLanguages()
  }

  // ── Start map, next to Play: the maps the enabled mods ADD to the game (community map mods; the original starts
  // them with `HP.exe <map>`). Not saved between Launcher runs; with no such map the choice is not shown. Texts and
  // arguments as launcher/Desktop/Logic.cs StartMap.
  // addedMaps MIRRORS hpengine/packaging/ModOverlay.cs AddedMaps (the owner of the rule; with Collect and ExtDirs there):
  // per enabled mod, in order, a file in a subfolder keeps its path and a flat file goes to the game directory of its
  // extension (System/Default.ini, else System/0/Default.ini: [Core.System] Paths=../<Dir>/*.<ext>, the first line for
  // an extension wins, Save never; .int -> System); hidden files do not count. A start map is Maps/<name>.unr (no deeper)
  // with no file of that path in the install, without case; a map two mods add is the later mod's; by name. None
  // while the engine would refuse the mods (a mod without game files or folder, flat files without Default.ini).
  struct StartMap: Equatable { let mod: String; let map: String }
  @Published var startMaps: [StartMap] = []
  @Published var startMap: String?   // nil: "Game (normal start)"
  static let startMapTitle = "Start map", startMapGame = "Game (normal start)"

  func updateStartMaps() {
    startMaps = Launcher.addedMaps(game: Launcher.gameURL)
    startMap = Launcher.keptStartMap(startMap, startMaps)
  }
  // The chosen map while it is still in the list, else the normal start.
  nonisolated static func keptStartMap(_ chosen: String?, _ maps: [StartMap]) -> String? {
    maps.contains { $0.map == chosen } ? chosen : nil
  }
  // The entries after "Game": the map name, with its mod when the maps come from several mods.
  nonisolated static func startMapLabels(_ maps: [StartMap]) -> [String] {
    let several = Set(maps.map { $0.mod.lowercased() }).count > 1
    return maps.map { several ? "\($0.map)  (\($0.mod))" : $0.map }
  }
  // The engine's arguments for Play; map nil: the normal start (no --map).
  // --launched-by-launcher: the engine shows no error dialog of its own, this window does (hpengine/packaging/FatalDialog.cs)
  nonisolated static func playArguments(game: URL, map: String?) -> [String] {
    ["--root", game.path, "--launched-by-launcher"] + (map.map { ["--map", $0] } ?? [])
  }

  // Prepare's placement (hpengine/packaging/ModOverlay.cs Collect): per enabled mod, in order, the game path of each of
  // its files ("Dir/sub/name" as spelled in the mod; a flat file under the game directory of its extension). nil while
  // the engine would refuse the mods (a mod without folder or game files, flat files without Default.ini).
  nonisolated static func entries(_ dir: URL) -> [URL] {
    (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey])) ?? []
  }
  nonisolated static func isDir(_ u: URL) -> Bool { (try? u.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false }
  nonisolated static func child(_ dir: URL, _ name: String, dir wantDir: Bool) -> URL? {
    entries(dir).first { $0.lastPathComponent.caseInsensitiveCompare(name) == .orderedSame && isDir($0) == wantDir }
  }
  nonisolated static func placedFiles(game: URL, ini: URL? = nil) -> [(mod: String, rels: [String])]? {
    let fm = FileManager.default
    let mods = modList(game: game, ini: ini).filter(\.enabled)
    if mods.isEmpty { return [] }
    // extension (lower case, with the dot) -> game directory, for flat files; nil: no Default.ini
    var flatDirs: [String: String]?
    if let system = child(game, "System", dir: true),
       let ini = child(system, "Default.ini", dir: false) ?? child(system, "0", dir: true).flatMap({ child($0, "Default.ini", dir: false) }),
       let text = try? String(contentsOf: ini, encoding: .isoLatin1) {
      var map = [".int": "System"], inside = false
      for raw in text.components(separatedBy: .newlines) {
        let line = raw.trimmingCharacters(in: .whitespaces)
        if line.hasPrefix("[") { inside = line.caseInsensitiveCompare("[Core.System]") == .orderedSame; continue }
        guard inside, line.lowercased().hasPrefix("paths=") else { continue }
        let parts = line.dropFirst(6).replacingOccurrences(of: "\\", with: "/").components(separatedBy: "/")   // ../System/*.u
        guard parts.count == 3, parts[0] == "..", parts[2].hasPrefix("*.") else { continue }
        let ext = String(parts[2].dropFirst()).lowercased()
        if map[ext] == nil && parts[1].lowercased() != "save" { map[ext] = parts[1] }
      }
      flatDirs = map
    }
    var placed: [(mod: String, rels: [String])] = []
    for m in mods {
      var rels: [String] = []
      for e in entries(game.appendingPathComponent("Mods").appendingPathComponent(m.name)) {
        if isDir(e) {
          for case let f as URL in fm.enumerator(at: e, includingPropertiesForKeys: [.isRegularFileKey]) ?? FileManager.DirectoryEnumerator()
          where ((try? f.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) ?? false) && !f.lastPathComponent.hasPrefix(".") {
            rels.append(([e.lastPathComponent] + f.pathComponents.dropFirst(e.pathComponents.count)).joined(separator: "/"))
          }
        } else if !e.lastPathComponent.hasPrefix(".") {
          guard let flatDirs else { return nil }
          guard let to = flatDirs["." + e.pathExtension.lowercased()] else { continue }
          rels.append(to + "/" + e.lastPathComponent)
        }
      }
      if rels.isEmpty { return nil }
      placed.append((m.name, rels))
    }
    return placed
  }

  nonisolated static func addedMaps(game: URL) -> [StartMap] {
    guard let placed = placedFiles(game: game) else { return [] }
    let shipped = Set(entries(game).filter { isDir($0) && $0.lastPathComponent.lowercased() == "maps" }.flatMap(entries)
      .filter { !isDir($0) }.map { $0.lastPathComponent.lowercased() })
    var maps: [String: StartMap] = [:]
    for (mod, rels) in placed {
      for rel in rels {
        let parts = rel.components(separatedBy: "/")
        guard parts.count == 2, parts[0].lowercased() == "maps", parts[1].lowercased().hasSuffix(".unr"), !shipped.contains(parts[1].lowercased()) else { continue }
        maps[parts[1].lowercased()] = StartMap(mod: mod, map: String(parts[1].dropLast(4)))
      }
    }
    return maps.values.sorted { $0.map.uppercased() < $1.map.uppercased() }   // as StringComparer.OrdinalIgnoreCase
  }

  // Two enabled mods with a file at the same game path: the later mod's file is used. One line per (earlier, later) pair
  // in mod order, the paths without case. MIRRORS ModOverlay.Conflicts (the owner) and launcher/Desktop/Logic.cs
  // ModsIni.Conflicts (the texts).
  nonisolated static func conflictLines(game: URL, ini: URL? = nil) -> [String] {
    guard let placed = placedFiles(game: game, ini: ini), placed.count > 1 else { return [] }
    var owner: [String: Int] = [:], pairs: [(earlier: Int, later: Int, files: [String])] = []
    for (i, p) in placed.enumerated() {
      for rel in p.rels {
        let key = rel.lowercased()   // without case, as Prepare places it
        if let j = owner[key], j != i {
          if let at = pairs.firstIndex(where: { $0.earlier == j && $0.later == i }) { pairs[at].files.append(rel) } else { pairs.append((j, i, [rel])) }
        }
        owner[key] = i
      }
    }
    return pairs.map { p in
      let files = p.files.sorted { $0.uppercased() < $1.uppercased() }, later = placed[p.later].mod
      return "\(later) and \(placed[p.earlier].mod) both have "
        + (files.count == 1 ? files[0] : "\(files[0]) and \(files.count - 1) more file" + (files.count == 2 ? "" : "s"))
        + ": the game uses \(later)'s (later in the order)"
    }
  }

  // The apply order of an enabled mod (1 first; a higher number wins a file both have); nil for a disabled one.
  // As launcher/Desktop/MainWindow.cs ModOrder_ and Logic.cs ModsIni.OrderHint.
  nonisolated static let orderHint = "Enabled mods apply in number order: a higher number wins"
  // HP2: a mod made for a newer HP2 engine (a package above HP2's retail format) does not run. MIRROR of
  // hpengine/packaging/ModOverlay.cs Hp2PackageVersion / NewerHp2Engine / NewerPackage (Core's UEGameProfile.MaxPackageVersion
  // owns the number); launcher/test_update.sh compares --newer-package with the owner.
  nonisolated static let hp2PackageVersion = 79
  nonisolated static func newerHp2Engine(_ mod: String) -> String { "\(mod) was made for a newer HP2 engine and does not run in HPEngine" }
  // Every non-hidden file of the mod folder, any depth, in path order: the first Unreal package above max, "<rel> (version <n>)".
  nonisolated static func newerPackage(modDir: URL, max: Int) -> String? {
    let fm = FileManager.default, base = modDir.standardizedFileURL.path
    var files: [String] = []
    for case let f as URL in fm.enumerator(at: modDir, includingPropertiesForKeys: [.isRegularFileKey]) ?? FileManager.DirectoryEnumerator()
    where (try? f.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) ?? false {
      let rel = String(f.standardizedFileURL.path.dropFirst(base.count + 1))
      if !rel.split(separator: "/").contains(where: { $0.hasPrefix(".") }) { files.append(rel) }
    }
    for rel in files.sorted(by: { Array($0.utf16).lexicographicallyPrecedes(Array($1.utf16)) }) {
      guard let h = FileHandle(forReadingAtPath: base + "/" + rel) else { continue }
      let b = [UInt8]((try? h.read(upToCount: 6)) ?? Data()); try? h.close()
      guard b.count == 6, b[0] == 0xC1, b[1] == 0x83, b[2] == 0x2A, b[3] == 0x9E else { continue }
      let v = Int(b[4]) | Int(b[5]) << 8
      if v > max { return "\(rel) (version \(v))" }
    }
    return nil
  }
  // HP2 only: the line under a mod on the Mods page (launcher/Desktop/Logic.cs ModsIni.NewerEngine); nil: none.
  nonisolated static func newerEngineLine(game: URL, mod: String) -> String? {
    guard let dir = child(game.appendingPathComponent("Mods"), mod, dir: true), newerPackage(modDir: dir, max: hp2PackageVersion) != nil else { return nil }
    return newerHp2Engine(mod)
  }

  // The empty Mods page (launcher/Desktop/Logic.cs ModsIni.NoMods).
  nonisolated static let noModsText = "No mods yet. Drop a mod's folder or archive (.zip, .7z, .rar) here, or use Add Mod; it shows up here to switch on and off."
  func openModsFolder() {
    guard let game = modsGame else { return }
    let dir = game.appendingPathComponent("Mods")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    open(dir)
  }
  nonisolated static func orderNumber(_ mods: [Mod], _ m: Mod) -> Int? { mods.filter(\.enabled).firstIndex(of: m).map { $0 + 1 } }

  // Why Play cannot start: enabled mods whose folder is gone (the engine would stop with exit code 5); nil: none.
  // Same texts as launcher/Desktop/Logic.cs ModsIni.Missing.
  nonisolated static func missingText(_ mods: [Mod]) -> String? {
    let gone = mods.filter { $0.enabled && $0.missing }.map(\.name)
    guard let first = gone.first else { return nil }
    if gone.count == 1 { return "The mod \(first) is on, but its folder is gone. Turn off \(first) or put its folder back into Mods." }
    let all = gone.dropLast().joined(separator: ", ") + " and \(gone.last!)"
    return "The mods \(all) are on, but their folders are gone. Turn off \(all) or put their folders back into Mods."
  }

  // One line per enabled mod with files the game never opens: files in a subfolder of the mod that is not a game
  // directory of the install (a mod unpacked one level too deep). Same rule as ModOverlay.Result.Misplaced and
  // launcher/Desktop/Logic.cs ModsIni.Misplaced.
  nonisolated static func misplacedLines(game: URL, mods: [Mod]) -> [String] {
    let fm = FileManager.default
    let gameDirs = subfolders(game).map { $0.lowercased() }.filter { !["mods", "hpenginedata", "hpengine"].contains($0) }
    var lines: [String] = []
    for m in mods where m.enabled && !m.missing {
      let dir = game.appendingPathComponent("Mods").appendingPathComponent(m.name)
      var n = 0
      for sub in ((try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
      where ((try? sub.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false) && !gameDirs.contains(sub.lastPathComponent.lowercased()) {
        for case let f as URL in fm.enumerator(at: sub, includingPropertiesForKeys: [.isRegularFileKey]) ?? FileManager.DirectoryEnumerator()
        where ((try? f.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) ?? false) && !f.lastPathComponent.hasPrefix(".") { n += 1 }
      }
      if n > 0 {
        lines.append((n == 1 ? "1 file of \(m.name) is" : "\(n) files of \(m.name) are")
          + " not in a game folder (System, Maps, ...): unpack the mod so these folders are directly inside Mods/\(m.name)")
      }
    }
    return lines
  }

  // On: last of the enabled (it wins); off: first of the disabled.
  func setMod(_ name: String, _ on: Bool) {
    guard let i = mods.firstIndex(where: { $0.name == name }) else { return }
    var m = mods.remove(at: i); m.enabled = on
    mods.insert(m, at: mods.filter(\.enabled).count)
    saveMods()
  }

  func moveMod(_ name: String, by d: Int) {
    let on = mods.filter(\.enabled)
    guard let i = on.firstIndex(where: { $0.name == name }), on.indices.contains(i + d) else { return }
    mods.swapAt(i, i + d)
    saveMods()
  }

  // ── Mods notice: which of the player's slots were saved with another mod set than the enabled one. A slot is
  // game/Save/save<N>.hp1sav, N = 0..5, shown in the game as Slot N+1 (FESlotPage: SetText(I + 1)); the level-start autosave
  // save99 is not a slot. The mod names are read from the save itself, without the engine (HP1.Core UELevelRuntime.SaveGame.cs,
  // format 5): string "HP1SAV" (BinaryWriter: 7-bit length + UTF-8), int32 version, int32 count, count strings, in order.
  // Versions 1..4 carry none: saved without mods, and the game refuses such a save while mods are on (no level-entry progress
  // to restart with). A file that does not read (damaged, newer version) is unknown: no line. Same rules as
  // launcher/Desktop/Logic.cs ModsNotice.
  @Published var modsNotice: [String] = []
  struct SlotMods: Equatable { var mods: [String]; var legacy: Bool }
  nonisolated static func readSlot(_ b: [UInt8]) -> SlotMods? {
    var p = 0
    func int7() -> Int? {
      var v = 0, shift = 0
      while shift < 35 {
        guard p < b.count else { return nil }
        let c = b[p]; p += 1; v |= Int(c & 0x7f) << shift
        if c & 0x80 == 0 { return v <= Int(Int32.max) ? v : nil }
        shift += 7
      }
      return nil
    }
    func str() -> String? {
      guard let n = int7(), n <= b.count - p, let s = String(bytes: b[p..<p + n], encoding: .utf8) else { return nil }
      p += n; return s
    }
    func i32() -> Int? {
      guard b.count - p >= 4 else { return nil }
      let v = Int32(bitPattern: UInt32(b[p]) | UInt32(b[p + 1]) << 8 | UInt32(b[p + 2]) << 16 | UInt32(b[p + 3]) << 24)
      p += 4; return Int(v)
    }
    guard str() == "HP1SAV", let ver = i32(), (1...6).contains(ver) else { return nil }
    if ver < 5 { return SlotMods(mods: [], legacy: true) }
    guard let count = i32(), (0...256).contains(count) else { return nil }
    var mods: [String] = []
    for _ in 0..<count { guard let m = str() else { return nil }; mods.append(m) }
    return SlotMods(mods: mods, legacy: false)
  }
  nonisolated static func noticeLines(enabled: [String], slots: [(Int, SlotMods?)]) -> [String] {
    var restart: [Int] = [], legacy: [Int] = []
    for (slot, m) in slots.sorted(by: { $0.0 < $1.0 }) {
      guard let m else { continue }
      let same = m.mods.count == enabled.count && zip(m.mods, enabled).allSatisfy { $0.caseInsensitiveCompare($1) == .orderedSame }
      if same { continue }
      if m.legacy { legacy.append(slot) } else { restart.append(slot) }
    }
    func numbers(_ n: [Int]) -> String { n.count == 1 ? "\(n[0])" : n.dropLast().map(String.init).joined(separator: ", ") + " and \(n.last!)" }
    var lines: [String] = []
    if !restart.isEmpty {
      lines.append(restart.count == 1
        ? "Slot \(restart[0]) was saved with other mods: loading it restarts its level with the progress you had when you entered it."
        : "Slots \(numbers(restart)) were saved with other mods: loading them restarts their level with the progress you had when you entered it.")
    }
    if !legacy.isEmpty {
      lines.append(legacy.count == 1
        ? "Slot \(legacy[0]) was saved by an older version: it does not load while mods are on."
        : "Slots \(numbers(legacy)) were saved by an older version: they do not load while mods are on.")
    }
    return lines
  }
  nonisolated static func slotMods(game: URL) -> [(Int, SlotMods?)] {
    (0...5).compactMap { n in
      guard let h = FileHandle(forReadingAtPath: game.appendingPathComponent("Save/save\(n).hp1sav").path) else { return nil }
      defer { try? h.close() }
      let head = (try? h.read(upToCount: 65536)) ?? Data()
      return (n + 1, readSlot([UInt8](head)))
    }
  }
  func updateModsNotice() {
    guard let game = modsGame else { modsNotice = []; return }
    // HP1's slots name their mod set; HP2's slot notice is not ported (what HP2 does with such a save is not established)
    modsNotice = (hp2Tab ? [] : Launcher.noticeLines(enabled: mods.filter(\.enabled).map(\.name), slots: Launcher.slotMods(game: game)))
      + Launcher.misplacedLines(game: game, mods: mods) + Launcher.conflictLines(game: game, ini: modsIni)
  }

  func saveMods() {
    let ini = modsIni
    let old = (try? String(contentsOf: ini, encoding: .utf8)) ?? ""
    try? FileManager.default.createDirectory(at: ini.deletingLastPathComponent(), withIntermediateDirectories: true)
    do { try Launcher.withEnabledMods(ini: old, mods.filter(\.enabled).map(\.name)).write(to: ini, atomically: true, encoding: .utf8) }
    catch { setupError("Could not save the mod list: \(error.localizedDescription)") }
    updateModsNotice()
    updateStartMaps()
  }

  nonisolated static func enabledMods(ini: String) -> [String] {
    var inMods = false, out: [String] = []
    for raw in ini.components(separatedBy: .newlines) {
      let line = raw.split(separator: ";", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init)?
        .trimmingCharacters(in: .whitespaces) ?? ""
      if line.hasPrefix("[") { inMods = line.caseInsensitiveCompare("[Mods]") == .orderedSame; continue }
      guard inMods, let eq = line.firstIndex(of: "="),
            line[..<eq].trimmingCharacters(in: .whitespaces).caseInsensitiveCompare("Enabled") == .orderedSame else { continue }
      let name = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
      if !name.isEmpty && !out.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) { out.append(name) }
    }
    return out
  }

  // The same file with the [Mods] section's Enabled= lines replaced by `names` (other lines, comments and sections kept).
  nonisolated static func withEnabledMods(ini: String, _ names: [String]) -> String {
    let nl = ini.contains("\r\n") ? "\r\n" : "\n"
    var lines = ini.isEmpty ? [] : ini.components(separatedBy: nl)
    if lines.last == "" { lines.removeLast() }
    let isHeader = { (l: String) in l.trimmingCharacters(in: .whitespaces).hasPrefix("[") }
    let isEnabled = { (l: String) -> Bool in
      let t = l.trimmingCharacters(in: .whitespaces)
      guard let eq = t.firstIndex(of: "=") else { return false }
      return t[..<eq].trimmingCharacters(in: .whitespaces).caseInsensitiveCompare("Enabled") == .orderedSame
    }
    let new = names.map { "Enabled=\($0)" }
    if let h = lines.firstIndex(where: { $0.trimmingCharacters(in: .whitespaces).caseInsensitiveCompare("[Mods]") == .orderedSame }) {
      var end = lines[(h + 1)...].firstIndex(where: isHeader) ?? lines.count
      var i = h + 1
      while i < end { if isEnabled(lines[i]) { lines.remove(at: i); end -= 1 } else { i += 1 } }
      var at = end
      while at > h + 1 && lines[at - 1].trimmingCharacters(in: .whitespaces).isEmpty { at -= 1 }
      lines.insert(contentsOf: new, at: at)
    } else {
      if let l = lines.last, !l.trimmingCharacters(in: .whitespaces).isEmpty { lines.append("") }
      lines.append("[Mods]"); lines.append(contentsOf: new)
    }
    return lines.joined(separator: nl) + nl
  }

  // The game's title by installed edition (HP1Setup's EDITION text "<name>: <tier>"), by the DLL set the name stands for
  // (UEEdition.Sets): US 1.53 and the City / Modded re-releases on its DLLs "Sorcerer's Stone", UK 1.1 and the Russian EA release
  // "Philosopher's Stone" (RU: the title always shown); not identified (no game yet, "Modified release", "Unknown release": the
  // text does not say which DLL set): the neutral "Harry Potter". Same as Desktop Logic.cs GameTitle.
  nonisolated static func gameTitle(_ edition: String?) -> String {
    guard let e = edition else { return "Harry Potter" }
    switch e.components(separatedBy: ": ")[0] {
    case "US 1.53", "City re-release", "Modded re-release": return "Harry Potter and the Sorcerer's Stone"
    case "UK 1.1", "RU EA": return "Harry Potter and the Philosopher's Stone"
    default: return "Harry Potter"
    }
  }

  func about() {
    NSApp.orderFrontStandardAboutPanel(options: [
      .applicationName: "HPEngine Launcher",
      .credits: NSAttributedString(string:
        "HPEngine runs \(Launcher.gameTitle(edition)) (PC, 2001) from your own copy of the game. "
        + "No game files are included. Game data: \(Launcher.gameURL.path)"
        + (Launcher.hp2Root.map { "\n\nHarry Potter and the Chamber of Secrets: \($0). Its menu's layout is HPEngine's own (not the original dialog's look); what it offers and starts is the original's." } ?? "")),
    ])
  }

  // Versions: launcher/VERSION (Info.plist HPEngineVersion, e.g. "0.1.0-beta"; a "-" suffix marks a pre-release build)
  // against the GitHub releases. A final build is offered final releases only; a pre-release build also pre-releases.
  nonisolated static let version = Bundle.main.infoDictionary?["HPEngineVersion"] as? String ?? "0"
  static let updateKey = (Bundle.main.infoDictionary?["HPEngineUpdateKey"] as? String).flatMap { Data(base64Encoded: $0) }
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
  // The HP2 tab (extension below): the selected tab, the dialog page (Main | NewGame | LoadGame), the scanned slots.
  @Published var hp2Tab = false
  @Published var hp2Page = "Main"
  @Published var hp2Slots: [Hp2FrontEnd.Slot] = []
  @Published var hp2RootPath: String?
  static var releases: URL {  // HPENGINE_RELEASES_URL: a test feed (file:// works)
    URL(string: ProcessInfo.processInfo.environment["HPENGINE_RELEASES_URL"]
        ?? "https://api.github.com/repos/artemnikonov/hpengine/releases?per_page=30")!
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
          a.messageText = rels == nil ? "Could not check for updates." : "HPEngine Launcher \(Launcher.version) is up to date."
          a.runModal()
        }
      }
    }.resume()
  }
  // Download the new Launcher (HPEngine is inside it), verify it, put it in place of this one and start it. Game data,
  // saves and settings live outside the app and are kept. Not while the game or a setup runs from inside this app.
  func updateLauncher() {
    guard let n = newer, !playing, !running else { return }
    guard let zip = n.zip, let sig = n.sig, let key = Launcher.updateKey else {
      launcherError = "This update can't be verified automatically. Download it from the release page."
      NSWorkspace.shared.open(n.page); return
    }
    let me = Bundle.main.bundleURL
    if me.path.contains("/AppTranslocation/") {
      launcherError = "Move HPEngine Launcher to the Applications folder, open it from there and try again."; return
    }
    updating = true; launcherError = nil
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
        updating = false; launcherError = e.text
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
  // quarantined app would be translocated and macOS would refuse to run the HP1Setup and HPEngine inside it.
  nonisolated static func verifiedApp(zip: URL, sig: Data, key: Data, version: String) -> Result<URL, UpdateError> {
    let fm = FileManager.default
    let tmp = fm.temporaryDirectory.appendingPathComponent("hpengine-launcher-\(UUID().uuidString)")
    func no(_ t: String) -> Result<URL, UpdateError> { try? fm.removeItem(at: tmp); return .failure(UpdateError(text: t)) }
    guard let pk = try? Curve25519.Signing.PublicKey(rawRepresentation: key) else { return no("This Launcher's update key is invalid.") }
    let z = tmp.appendingPathComponent("u.zip")
    guard (try? fm.createDirectory(at: tmp, withIntermediateDirectories: true)) != nil, (try? fm.moveItem(at: zip, to: z)) != nil
    else { return no("Could not update: no temporary folder.") }
    guard let s = Data(base64Encoded: String(decoding: sig, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)),
          let bytes = try? Data(contentsOf: z, options: .mappedIfSafe), pk.isValidSignature(s, for: bytes)
    else { return no("The download is not signed by HPEngine, so it was not installed.") }
    guard run("/usr/bin/ditto", ["-x", "-k", z.path, tmp.path]) == 0,
          let app = (try? fm.contentsOfDirectory(at: tmp, includingPropertiesForKeys: nil))?.first(where: { $0.pathExtension == "app" }),
          let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any],
          info["CFBundleIdentifier"] as? String == bundleID
    else { return no("The downloaded file is not an HPEngine Launcher.") }
    guard info["HPEngineVersion"] as? String == version else { return no("The downloaded Launcher is not version \(version).") }
    guard run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]) == 0
    else { return no("The downloaded Launcher's code signature is broken.") }
    _ = run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
    return .success(app)
  }
}

// The HP2 tab (as launcher/Desktop/MainWindow.Hp2.cs): HP2's own front end — Main Menu, New Game, Load Game — its page
// flow from Hp2FrontEnd, drawn in the launcher's look ("not original", DECISIONS "HP2 main menu in the launcher").
extension Launcher {
  static let hp2Title = "Harry Potter and the Chamber of Secrets"
  static let hp2RootFile = dataURL.appendingPathComponent("hp2-root.txt")   // beside the HP1 data directory
  static var hp2SaveDir: String { renderSaveDir ?? Hp2FrontEnd.saveDir(gameURL.path) }
  static var renderSaveDir: String?   // --render-states only: a temporary folder with synthetic thumbnails
  // HP2's install root: HP1Setup --game hp2 installs here (<HP2Data>/game, beside HP2's saves and settings), as
  // launcher/Desktop/MainWindow.Hp2.cs Hp2Game.
  static var hp2GameURL: URL { URL(fileURLWithPath: (Hp2FrontEnd.saveDir(gameURL.path) as NSString).deletingLastPathComponent).appendingPathComponent("game") }
  // Without an installed copy: an HP2 folder the player chose (Menu "Change Game Folder…").
  static func hp2Valid(_ r: String?) -> Bool {
    guard let r else { return false }
    return FileManager.default.fileExists(atPath: r + "/System/Engine.dll") && FileManager.default.fileExists(atPath: r + "/System/Startup.int")
  }
  static var hp2Root: String? {
    guard Hp2Release.available else { return nil }   // the release switch off: the saved folder is ignored
    if isInstalled(hp2GameURL) { return hp2GameURL.path }
    let r = (try? String(contentsOf: hp2RootFile, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
    return hp2Valid(r) ? r : nil
  }

  func chooseHp2() {
    let p = NSOpenPanel()
    p.canChooseDirectories = true; p.canChooseFiles = false
    p.message = "Installed Harry Potter and the Chamber of Secrets"
    if p.runModal() == .OK, let u = p.url {
      if !Launcher.hp2Valid(u.path) { hp2Error = "Not an installed HP2 game folder (no System/Engine.dll and System/Startup.int): \(u.path)" }
      else {
        do { try FileManager.default.createDirectory(at: Launcher.dataURL, withIntermediateDirectories: true)
             try u.path.write(to: Launcher.hp2RootFile, atomically: true, encoding: .utf8); hp2Error = nil }
        catch { self.hp2Error = error.localizedDescription }
      }
    }
    hp2Page = "Main"; hp2Scan()
  }

  // OnInitDialog's slot scan — each time the menu opens, as each start of Game.exe ran it.
  func hp2Scan() {
    hp2RootPath = Launcher.hp2Root
    hp2Slots = hp2RootPath == nil ? [] : Hp2FrontEnd.scan("", Hp2FrontEnd.Files(Launcher.hp2SaveDir), saveFile: Hp2FrontEnd.portSave)
  }

  func hp2Click(_ s: Hp2FrontEnd.Slot) {
    guard let root = hp2RootPath else { return }
    if hp2Page == "NewGame" {
      // MessageBoxW(MB_YESNO, HPMenu [All] Select_Game_0001, caption Select_Game_0002) for a used slot
      let ask = {
        let a = NSAlert()
        a.messageText = Hp2FrontEnd.localize(root, "HPMenu", "All", "Select_Game_0002")
        a.informativeText = Hp2FrontEnd.localize(root, "HPMenu", "All", "Select_Game_0001")
        a.addButton(withTitle: "Yes"); a.addButton(withTitle: "No")
        return a.runModal() == .alertFirstButtonReturn
      }
      if Hp2FrontEnd.newGame(s, confirmReplace: ask, Hp2FrontEnd.Files(Launcher.hp2SaveDir)) { hp2Play(root, Hp2FrontEnd.commandLine(.newGame, s.n, "")) }
    } else if Hp2FrontEnd.loadGame(s) { hp2Play(root, Hp2FrontEnd.commandLine(.loadGame, s.n, "")) }
  }

  // The new Game.exe process the original starts, here HPEngine on the HP2 folder with the original's parameters.
  func hp2Play(_ root: String, _ cmdline: String) {
    guard !playing, unquarantine() else { return }
    loadMods()   // the folders as they are now; an enabled mod whose folder is gone stops the engine (exit code 5)
    if let gone = Launcher.missingText(mods) { hp2Error = gone; return }
    let game = URL(fileURLWithPath: root)
    if let line = mods.filter({ $0.enabled && !$0.missing }).lazy.compactMap({ Launcher.newerEngineLine(game: game, mod: $0.name) }).first {
      hp2Error = line + ". Turn it off in Mods."; return
    }
    let fm = FileManager.default, logURL = Launcher.launchLog(hp2: true)
    try? fm.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
    fm.createFile(atPath: logURL.path, contents: nil)
    let p = Process()
    p.executableURL = Launcher.runtimeURL.appendingPathComponent("HPEngine")
    p.arguments = Hp2FrontEnd.playArgs(root, Launcher.hp2SaveDir, cmdline)
    p.currentDirectoryURL = URL(fileURLWithPath: root)
    if let log = FileHandle(forWritingAtPath: logURL.path) { p.standardOutput = log; p.standardError = log }
    p.standardInput = FileHandle.nullDevice
    p.terminationHandler = { pr in
      let status = pr.terminationStatus
      DispatchQueue.main.async {
        MainActor.assumeIsolated {
          self.playing = false
          let said = ((try? String(contentsOf: logURL, encoding: .utf8)) ?? "").components(separatedBy: "\n")
          self.warning = Launcher.overrideWarning(said)
          if status != 0 {
            self.hp2Error = Launcher.errorBanner(said, code: status)
            self.hp2RunError = self.hp2Error
          }
          self.hp2Page = "Main"; self.hp2Scan()
        }
      }
    }
    hp2RunLogsBefore = GameScope.runLogs(Launcher.logsDir(hp2: true).path)
    do { try p.run(); playing = true; hp2Error = nil } catch { self.hp2Error = error.localizedDescription; return }
    Launcher.handFocus(to: p.processIdentifier, log: logURL)
  }
}

// ── HP2 in the launcher, the release switch (author 2026-11-16, release 0.4.0): a MIRROR of launcher/Desktop/Logic.cs
// Hp2Release (compared through --game-scope). Off: the HP2 tab shown but disabled ("Coming soon", a tooltip; no click, Cmd+2
// or pad RB), its saved state ignored. Build-time: Info.plist HPEngineHp2 = true (make_launcher.sh with HPENGINE_HP2=1, a
// preview build); the release has no such key.
enum Hp2Release {
  nonisolated(unsafe) static var available = Bundle.main.infoDictionary?["HPEngineHp2"] as? Bool == true
  static let soon = "Coming soon", soonTip = "Harry Potter 2 is coming soon. Wait for updates."
  static func caption(_ available: Bool, folderSet: Bool) -> String? { !available ? soon : folderSet ? nil : "not set up" }
  static func tip(_ available: Bool) -> String? { available ? nil : soonTip }
}

// ── Per game (author 2026-11-16): a MIRROR of launcher/Desktop/Logic.cs GameScope (launcher/test_update.sh compares the
// answers, --game-scope): the banner of the selected tab, the folder and logs of each game's run, the console capture.
enum GameScope {
  static func error(hp2Tab: Bool, hp1: String?, hp2: String?, launcher: String?) -> String? { launcher ?? (hp2Tab ? hp2 : hp1) }
  static func dataDir(_ hp1Game: String, hp2: Bool) -> String {
    hp2 ? ((Hp2FrontEnd.saveDir(hp1Game) as NSString).deletingLastPathComponent as NSString).appendingPathComponent("HPEngineData")
        : (hp1Game as NSString).appendingPathComponent("HPEngineData")
  }
  static func logsDir(_ hp1Game: String, hp2: Bool) -> String { (dataDir(hp1Game, hp2: hp2) as NSString).appendingPathComponent("logs") }
  static func consoleLog(_ hp1Log: String, hp2: Bool) -> String { hp2 ? String(hp1Log.dropLast(4)) + "-HP2.log" : hp1Log }
  // The banner's ×: the launcher's own error when that is shown, else the selected tab's game error; the other game's stays.
  static func dismiss(hp2Tab: Bool, hp1: String?, hp2: String?, launcher: String?) -> (String?, String?, String?) {
    launcher != nil ? (hp1, hp2, nil) : hp2Tab ? (hp1, nil, nil) : (nil, hp2, nil)
  }
  // "Open log" of a failed run: the newest run_*.log the run added (not there before it started), else the console capture,
  // else the logs folder.
  static func isRunLog(_ n: String) -> Bool { n.hasPrefix("run_") && n.hasSuffix(".log") }
  static func runLogs(_ dir: String) -> [String] { ((try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []).filter(isRunLog) }
  static func runLog(_ now: [String], before: [String]?) -> String? { let b = Set(before ?? []); return now.filter { isRunLog($0) && !b.contains($0) }.max() }
  static func logToOpen(_ logsDir: String, console: String, before: [String]?) -> String {
    if let r = runLog(runLogs(logsDir), before: before) { return (logsDir as NSString).appendingPathComponent(r) }
    return FileManager.default.fileExists(atPath: console) ? console : logsDir
  }
  static func table(_ game: String) -> [String] {
    var t: [String] = []
    for tab in [false, true] { for h1 in [nil, "e1"] { for h2 in [nil, "e2"] { for l in [nil, "eL"] {
      t.append("error hp2Tab=\(tab ? 1 : 0) hp1=\(h1 ?? "-") hp2=\(h2 ?? "-") launcher=\(l ?? "-") -> \(error(hp2Tab: tab, hp1: h1, hp2: h2, launcher: l) ?? "-")") } } } }
    for hp2 in [false, true] {
      t.append("game hp2=\(hp2 ? 1 : 0) data=\(dataDir(game, hp2: hp2)) logs=\(logsDir(game, hp2: hp2)) console=\(consoleLog("/d/HPEngine.log", hp2: hp2))") }
    for on in [false, true] { for folder in [false, true] {
      t.append("hp2tab available=\(on ? 1 : 0) folder=\(folder ? 1 : 0) caption=\(Hp2Release.caption(on, folderSet: folder) ?? "-") tip=\(Hp2Release.tip(on) ?? "-")") } }
    for tab in [false, true] { for h1 in [nil, "e1"] { for h2 in [nil, "e2"] { for l in [nil, "eL"] {
      let d = dismiss(hp2Tab: tab, hp1: h1, hp2: h2, launcher: l)
      t.append("dismiss hp2Tab=\(tab ? 1 : 0) hp1=\(h1 ?? "-") hp2=\(h2 ?? "-") launcher=\(l ?? "-") -> hp1=\(d.0 ?? "-") hp2=\(d.1 ?? "-") launcher=\(d.2 ?? "-")") } } } }
    let before = ["run_20261117_090000.log"]
    let names: [([String], [String])] = [([], []), (["run_20261117_090000.log"], before), (["run_20261117_090000.log"], []),
      (["run_20261117_090000.log", "run_20261117_090000_x.log"], before),
      (["run_20261117_090000.log", "run_20261117_100512.log", "run_20261117_100001.log", "notes.txt", "run_20261117_110000.txt"], before)]
    for (now, b) in names { t.append("runlog now=\(now.joined(separator: ",")) before=\(b.joined(separator: ",")) -> \(runLog(now, before: b) ?? "-")") }
    let fm = FileManager.default
    for c in ["run", "console", "none"] {
      let d = (game as NSString).appendingPathComponent("openlog/" + c), logs = d + "/logs", console = d + "/HPEngine.log"
      if c != "none" {
        try? fm.createDirectory(atPath: logs, withIntermediateDirectories: true)
        try? "an earlier run".write(toFile: logs + "/run_20261117_090000.log", atomically: false, encoding: .utf8); try? "console".write(toFile: console, atomically: false, encoding: .utf8)
      }
      if c == "run" { try? "[Crash] x".write(toFile: logs + "/run_20261117_100000.log", atomically: false, encoding: .utf8) }
      t.append("openlog \(c) -> \(String(logToOpen(logs, console: console, before: before).dropFirst(game.count + 1)))")
    }
    return t
  }
}

// ── The gamepad (proposal §6): a MIRROR of launcher/Desktop/Logic.cs PadMap, the one answer of both launchers
// (launcher/test_update.sh compares the two tables, --pad-map). Buttons in SDL_GamepadButton numbers; Launcher.readPad maps
// Apple's GameController buttons onto them (native, no library: the SDL3 HPEngine ships is per architecture inside the
// bundle and would add a loader for what GameController gives). popup: a list or a dialog is open; hp2: the HP2 tab.
enum PadAction: String { case none = "None", activate = "Activate", escape = "Escape", back = "Back", hp1 = "Hp1", hp2 = "Hp2",
  menu = "Menu", play = "Play", keyUp = "KeyUp", keyDown = "KeyDown", previous = "Previous", next = "Next" }
enum PadMap {
  static let a = 0, b = 1, x = 2, y = 3, back = 4, start = 6, leftStick = 7, rightStick = 8, lb = 9, rb = 10, up = 11, down = 12, left = 13, right = 14
  static func action(_ button: Int, popup: Bool, hp2: Bool) -> PadAction {
    switch button {
    case a: return .activate
    case b: return popup ? .escape : .back
    case lb: return popup || !hp2 ? .none : .hp1
    case rb: return popup || hp2 ? .none : .hp2
    case start: return popup ? .none : .menu
    case y: return popup || hp2 ? .none : .play
    case up, left: return popup ? .keyUp : .previous
    case down, right: return popup ? .keyDown : .next
    default: return .none
    }
  }
  // The left stick past half way (16000 of SDL's 32767) as d-pad bits; x right, y down, each -1..1.
  static func stick(_ x: Double, _ y: Double) -> UInt32 {
    let half = 16000 / 32767.0
    var bits: UInt32 = 0
    if y < -half { bits |= 1 << up }; if y > half { bits |= 1 << down }
    if x < -half { bits |= 1 << left }; if x > half { bits |= 1 << right }
    return bits
  }
  // The buttons pressed since the last poll, in button order (one action per press).
  static func pressed(_ now: UInt32, _ last: UInt32) -> [Int] { (0...right).filter { now & ~last & (1 << $0) != 0 } }
  // The table launcher/Desktop.Tests --pad-map prints (Program.cs PadTable), line for line.
  static func table() -> [String] {
    var t: [String] = []
    for b in 0...15 { for popup in [false, true] { for hp2 in [false, true] {
      t.append("button \(b) popup=\(popup ? 1 : 0) hp2=\(hp2 ? 1 : 0) \(action(b, popup: popup, hp2: hp2).rawValue)") } } }
    for (x, y) in [(0, 0), (16000, 0), (16001, 0), (-16000, 0), (-16001, 0), (0, 16001), (0, -16001), (0, 16000), (32767, 32767), (-32768, -32768), (-32768, 16001)] {
      t.append("stick \(x) \(y) \(stick(Double(x) / 32767.0, Double(y) / 32767.0))") }
    for (now, last): (UInt32, UInt32) in [(0, 0), (1, 0), (1, 1), (0b1011, 0b0001), (1 << 14 | 1 << 9, 1 << 14), (1 << 15, 0), (0x7FFF, 0)] {
      t.append("pressed \(now) \(last) " + pressed(now, last).map(String.init).joined(separator: ",")) }
    return t
  }
}

// A control the pad (and the arrow keys in a list) can focus: its id, whether it is the screen's primary (focus starts
// there, as Desktop's KeepFocus) and what A does. The window registers them in layout order (PadTargets preference); the
// actions read the Launcher when run, so a target is equal by id.
struct PadTarget: Equatable {
  let id: String, primary: Bool, run: () -> Void
  static func == (l: PadTarget, r: PadTarget) -> Bool { l.id == r.id && l.primary == r.primary }
}
struct PadTargets: PreferenceKey {
  static let defaultValue: [PadTarget] = []
  static func reduce(value: inout [PadTarget], nextValue: () -> [PadTarget]) { value += nextValue() }
}
// An in-window list (the Menu, Language, Start map), the one popup of mouse, keyboard and pad. A header with no title
// is a separator.
struct PopItem {
  let title: String
  var header = false, enabled = true, destructive = false, checked = false
  var run: () -> Void = {}
}
struct Popup { let items: [PopItem]; let anchor: String }   // anchor: the id of the control that opened it; the list opens at it

extension Launcher {
  var targets: [PadTarget] {
    guard let pop = popup else { return padTargets }
    return pop.items.enumerated().compactMap { i, it in it.header || !it.enabled ? nil : PadTarget(id: "pop\(i)", primary: false) { self.pick(it) } }
  }
  func target(_ id: String) -> PadTarget? { targets.first { $0.id == id } }
  // Focus gone (start, a rebuilt page, a list opened): the screen's primary, else its first target.
  func keepFocus() {
    let t = targets
    if let f = padFocus, t.contains(where: { $0.id == f }) { return }
    padFocus = t.first(where: \.primary)?.id ?? t.first?.id
  }
  func move(_ d: Int) {
    let t = targets
    guard !t.isEmpty else { return }
    let i = t.firstIndex { $0.id == padFocus } ?? (d > 0 ? -1 : t.count)
    padFocus = t[(i + d + t.count) % t.count].id   // wraps, as Tab order does on Windows/Linux
  }
  func openPopup(_ items: [PopItem], from anchor: String) {
    focusBeforePopup = padFocus; popup = Popup(items: items, anchor: anchor)
    padFocus = items.firstIndex { $0.checked && $0.enabled }.map { "pop\($0)" }; keepFocus()   // a choice list opens at its choice
  }
  // In an open list: to a position of its targets (clamped: Home, End, a page), and type-ahead (the next item whose title
  // starts with the letter typed).
  func moveTo(_ i: Int) {
    let t = targets
    if !t.isEmpty { padFocus = t[min(max(i, 0), t.count - 1)].id }
  }
  func moveBy(_ d: Int) { moveTo((targets.firstIndex { $0.id == padFocus } ?? 0) + d) }
  // The list's indicator dragged (or its track clicked) to a fraction 0...1 of its travel: the list scrolls to that fraction.
  func dragList(_ frac: CGFloat) {
    guard let sv = listScroller, let doc = sv.documentView else { return }
    let travel = max(doc.frame.height - sv.contentView.bounds.height, 0)
    sv.contentView.scroll(to: NSPoint(x: 0, y: min(max(frac, 0), 1) * travel)); sv.reflectScrolledClipView(sv.contentView)
  }
  static func itemHeight(_ it: PopItem) -> CGFloat { it.header ? (it.title.isEmpty ? 9 : 28) : 40 }
  static func popupHeight(_ pop: Popup) -> CGFloat { pop.items.reduce(0) { $0 + itemHeight($1) } }
  func typeAhead(_ c: String) {
    guard let pop = popup else { return }
    let t = targets, at = t.firstIndex { $0.id == padFocus } ?? -1
    let hits = t.indices.filter { Int(t[$0].id.dropFirst(3)).map { pop.items[$0].title.lowercased().hasPrefix(c.lowercased()) } ?? false }
    if let i = hits.first(where: { $0 > at }) ?? hits.first { padFocus = t[i].id }
  }
  func closePopup() { popup = nil; padFocus = focusBeforePopup; keepFocus() }
  func pick(_ it: PopItem) { closePopup(); it.run() }
  // B / Escape: close the list, else the visible sub-page's Back. false: nothing to go back from.
  func goBack() -> Bool {
    if popup != nil { closePopup(); return true }
    if padFocus == "dismiss" || padFocus == "openlog", let d = target("dismiss") { d.run(); return true }   // on the error banner: close it
    guard let t = target("back") else { return false }
    t.run(); return true
  }

  // One gamepad button: what PadMap says, on the focused target, the open list or a dialog of AppKit (an alert, a file
  // panel: keys posted to it, as Desktop sends them to its dialog windows).
  func pad(_ button: Int) {
    padUsed = true
    let modal = NSApp?.modalWindow
    keepFocus()
    switch PadMap.action(button, popup: popup != nil || modal != nil, hp2: hp2Tab) {
    case .activate: if modal != nil { postKey(36, "\r") } else { target(padFocus ?? "")?.run() }
    case .escape: if modal != nil { postKey(53, "\u{1b}") } else { closePopup() }
    case .back: _ = goBack()
    case .hp1: target("tab1")?.run()
    case .hp2: target("tab2")?.run()
    case .menu: target("menu")?.run()
    case .play: target("play")?.run()   // registered only while Play is shown and enabled
    case .keyUp: if modal != nil { postKey(126, String(UnicodeScalar(NSUpArrowFunctionKey)!)) } else { move(-1) }
    case .keyDown: if modal != nil { postKey(125, String(UnicodeScalar(NSDownArrowFunctionKey)!)) } else { move(1) }
    case .previous: move(-1)
    case .next: move(1)
    case .none: break
    }
  }
  func postKey(_ code: UInt16, _ ch: String) {
    guard let w = NSApp.modalWindow else { return }
    for t in [NSEvent.EventType.keyDown, .keyUp] {
      if let e = NSEvent.keyEvent(with: t, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: w.windowNumber,
                                  context: nil, characters: ch, charactersIgnoringModifiers: ch, isARepeat: false, keyCode: code) { NSApp.postEvent(e, atStart: false) }
    }
  }
  // The first connected controller's buttons as PadMap bits; nil: no controller.
  static func readPad() -> UInt32? {
    guard let g = (GCController.current ?? GCController.controllers().first)?.extendedGamepad else { return nil }
    var bits: UInt32 = 0
    let buttons: [(GCControllerButtonInput?, Int)] = [(g.buttonA, PadMap.a), (g.buttonB, PadMap.b), (g.buttonX, PadMap.x), (g.buttonY, PadMap.y),
      (g.buttonOptions, PadMap.back), (g.buttonMenu, PadMap.start), (g.leftThumbstickButton, PadMap.leftStick), (g.rightThumbstickButton, PadMap.rightStick),
      (g.leftShoulder, PadMap.lb), (g.rightShoulder, PadMap.rb), (g.dpad.up, PadMap.up), (g.dpad.down, PadMap.down), (g.dpad.left, PadMap.left), (g.dpad.right, PadMap.right)]
    for (b, n) in buttons where b?.isPressed == true { bits |= 1 << n }
    return bits | PadMap.stick(Double(g.leftThumbstick.xAxis.value), Double(-g.leftThumbstick.yAxis.value))   // GameController's y is up
  }
  // Polls the pad every 50 ms (in every run loop mode: also while an alert or a file panel is open); acts while this app
  // is in front and no game runs, as Desktop's StartGamepad.
  func startPad() {
    let t = Timer(timeInterval: 0.05, repeats: true) { _ in
      MainActor.assumeIsolated {
        let now = Launcher.readPad()
        if self.padConnected != (now != nil) { self.padConnected = now != nil }
        if let bits = now, NSApp.isActive, !self.playing { for b in PadMap.pressed(bits, self.padLast) { self.pad(b) } }
        self.padLast = now ?? 0
      }
    }
    RunLoop.main.add(t, forMode: .common)
  }
  // The keys of the window that SwiftUI shortcuts do not cover: Escape = Back (or closes the list); in an open list the
  // arrows move and Return picks. Cmd+1 / Cmd+2 / Cmd+, are the tabs' and the Menu button's shortcuts.
  func startKeys() {
    NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
      let code = e.keyCode, plain = e.modifierFlags.intersection([.command, .control, .option]).isEmpty
      let used = MainActor.assumeIsolated { () -> Bool in
        guard NSApp.modalWindow == nil, plain else { return false }
        switch code {
        case 53: return self.goBack()
        case 125, 126: guard self.popup != nil else { return false }; self.padUsed = true; self.move(code == 126 ? -1 : 1); return true
        case 36, 76: guard self.popup != nil else { return false }; self.target(self.padFocus ?? "")?.run(); return true
        case 115, 119, 116, 121:   // Home, End, Page Up, Page Down
          guard self.popup != nil else { return false }; self.padUsed = true
          switch code { case 115: self.moveTo(0); case 119: self.moveTo(Int.max); case 116: self.moveBy(-Launcher.page); default: self.moveBy(Launcher.page) }
          return true
        default:
          guard self.popup != nil, let c = e.charactersIgnoringModifiers, c.count == 1, c.first!.isLetter || c.first!.isNumber else { return false }
          self.padUsed = true; self.typeAhead(c); return true
        }
      }
      return used ? nil : e
    }
  }

  // The Menu (proposal §2): "This game" per tab (Remove Game its last item, in red), "Launcher" — the items of launcher/Desktop.
  // The setup state (chosen files, steps, warning, edition) is the selected tab's game: a switch starts the other's afresh.
  func switchTo(hp2: Bool) {
    guard hp2Tab != hp2 else { return }
    hp2Tab = hp2; inputs = []; step = -1; warning = nil
    edition = Launcher.installedEdition(hp2 ? Launcher.hp2GameURL : Launcher.gameURL)
    loadMods()   // the selected tab's mods
  }

  func menuItems() -> [PopItem] {
    var m = [PopItem(title: "This game", header: true)]
    if hp2Tab {
      m += [PopItem(title: "Open Saved Games Folder", enabled: hp2RootPath != nil) { self.open(URL(fileURLWithPath: Launcher.hp2SaveDir)) },
            PopItem(title: "Open Logs Folder") { self.open(Launcher.logsDir(hp2: true)) },
            PopItem(title: "Install Again from Other Files…", enabled: !running && !playing) { self.choose() }]
      if !Launcher.isInstalled(Launcher.hp2GameURL) { m.append(PopItem(title: "Change Game Folder…", enabled: !playing && !running) { self.chooseHp2() }) }
      m.append(PopItem(title: "Remove Game…", enabled: Launcher.isInstalled(Launcher.hp2GameURL) && !running && !playing, destructive: true) { self.removeGame() })
    } else {
      m += [PopItem(title: "Open Saved Games Folder", enabled: installed) { self.open(Launcher.savesURL) },
            PopItem(title: "Open Logs Folder") { self.open(Launcher.logsURL) },
            PopItem(title: "Install Again from Other Files…", enabled: !running && !playing) { self.choose() },
            PopItem(title: "Reset Settings…", enabled: installed) { self.resetSettings() },
            PopItem(title: "Reset Gamepad Bindings…", enabled: installed && !playing) { self.resetPad() },
            PopItem(title: "Remove Game…", enabled: installed && !running && !playing, destructive: true) { self.removeGame() }]
    }
    m += [PopItem(title: "Launcher", header: true),
          PopItem(title: "Check for Updates") { self.checkForUpdate(manual: true) },
          PopItem(title: "About HPEngine Launcher") { self.about() }]
    return m
  }
}

// ── HP2 front end: a MIRROR of launcher/Desktop/Logic.cs Hp2FrontEnd (the owner, judged against HP2 Game.exe by
// tools/hp2/frontend_judge.sh). launcher/test_update.sh pins every rule here to the owner: the judge's cases
// (--hp2-frontend, answers byte for byte) and the menu on real folders (--hp2-menu). Change the owner first.
protocol Hp2Files {
  func fileSize(_ p: String) -> Int64
  func delete(_ p: String)
  func makeDirectory(_ p: String)
  func findFiles(_ pattern: String) -> [String]
  func deleteCache(_ dir: String)   // not original: the port's <slot>/cache folder (clear)
}

enum Hp2FrontEnd {
  static let slots = 6
  static let originalSave = "Save0.usa", portSave = "save0.hp1sav"   // the slot's save file: the judge's / the port's
  static func join(_ a: String, _ b: String) -> String { a.isEmpty || a.hasSuffix("\\") ? a + b : a + "\\" + b }
  static func slotDir(_ savePath: String, _ n: Int) -> String { join(savePath, "Slot") + String(n) }
  struct Slot { let n: Int; let dir: String; let used: Bool; let thumb: String? }

  static func scan(_ savePath: String, _ fs: Hp2Files, saveFile: String = originalSave) -> [Slot] {
    (1...slots).map { n in
      let dir = slotDir(savePath, n)
      fs.makeDirectory(dir)
      let used = fs.fileSize(join(dir, saveFile)) >= 1
      if !used { clear(dir, fs) }
      return Slot(n: n, dir: dir, used: used, thumb: used ? join(dir, "Save0.bmp") : nil)
    }
  }
  static func clear(_ dir: String, _ fs: Hp2Files) {
    if dir.isEmpty { return }
    for f in fs.findFiles(join(dir, "*.*")) where fs.fileSize(join(dir, f)) != 0 { fs.delete(join(dir, f)) }
    fs.deleteCache(dir)
  }

  enum Launch { case newGame, loadGame }
  static func asksToReplace(_ s: Slot) -> Bool { s.used }
  static func newGame(_ s: Slot, confirmReplace: () -> Bool, _ fs: Hp2Files) -> Bool {
    if asksToReplace(s) && !confirmReplace() { return false }
    clear(s.dir, fs)
    return true
  }
  static func loadGame(_ s: Slot) -> Bool { s.used }
  static func commandLine(_ what: Launch, _ slot: Int, _ cmdline: String) -> String {
    let s = " -SAVESLOT=\(slot)"
    return what == .newGame ? "PrivetDr.unr " + s + cmdline : cmdline + " -LOADGAME=0 " + s
  }
  static func label(_ s: Slot, empty: String, used: String) -> String { "\(s.n) - \(s.used ? used : empty)" }

  // Path.GetFullPath on Unix: absolute against the working directory, "." and ".." resolved textually (no symlinks;
  // Foundation's standardizing would also drop a "/private" prefix, which .NET keeps).
  static func fullPath(_ p: String) -> String {
    var parts: [Substring] = []
    for c in (p.hasPrefix("/") ? p : FileManager.default.currentDirectoryPath + "/" + p).split(separator: "/") {
      if c == ".." { _ = parts.popLast() } else if c != "." { parts.append(c) }
    }
    return "/" + parts.joined(separator: "/")
  }
  // Path.GetDirectoryName(Path.GetFullPath(hp1DataDir)) + HP2Data/Save
  static func saveDir(_ hp1DataDir: String) -> String { (fullPath(hp1DataDir) as NSString).deletingLastPathComponent + "/HP2Data/Save" }

  // Files under one directory; a path resolving outside it is refused.
  struct Files: Hp2Files {
    let root: String
    init(_ dir: String) { root = fullPath(dir) }
    func host(_ p: String) -> String? {
      let q = p.replacingOccurrences(of: "\\", with: "/")
      let h = fullPath(q.hasPrefix("/") ? q : root + "/" + q)
      return h == root || h.hasPrefix(root + "/") ? h : nil
    }
    func fileSize(_ p: String) -> Int64 {
      var d: ObjCBool = false
      guard let h = host(p), FileManager.default.fileExists(atPath: h, isDirectory: &d), !d.boolValue,
            let s = (try? FileManager.default.attributesOfItem(atPath: h))?[.size] as? NSNumber else { return -1 }
      return s.int64Value
    }
    func delete(_ p: String) { if let h = host(p) { try? FileManager.default.removeItem(atPath: h) } }
    func deleteCache(_ dir: String) {
      var d: ObjCBool = false
      if let h = host(join(dir, "cache")), FileManager.default.fileExists(atPath: h, isDirectory: &d), d.boolValue { try? FileManager.default.removeItem(atPath: h) }
    }
    func makeDirectory(_ p: String) { if let h = host(p) { try? FileManager.default.createDirectory(atPath: h, withIntermediateDirectories: true) } }
    func findFiles(_ pattern: String) -> [String] {
      guard let h = host(pattern) else { return [] }
      let dir = (h as NSString).deletingLastPathComponent
      return ((try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []).filter {
        var d: ObjCBool = false
        return FileManager.default.fileExists(atPath: dir + "/" + $0, isDirectory: &d) && !d.boolValue
      }
    }
  }

  // The launcher's own English words for HP2's front-end pages (author 2026-11-20, NOT ORIGINAL): the launcher is
  // English, so its HP2 pages are too, whatever the game's language (they were the game's Startup/HPMenu Localize text,
  // Russian on the RU EA edition beside English buttons). The words are the US edition's Startup.int / HPMenu.int. Same
  // table as launcher/Desktop/Logic.cs Hp2FrontEnd.Words (launcher/test_update.sh compares --hp2-menu).
  static let words: [String: String] = [
    "Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Title": "Main Menu",
    "Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Instructions": "To Play Harry Potter click on New Game or Load Game. Click on Options for Video, Sound and Joystick options.",
    "Startup/IDDIALOG_FrontEndPageMainMenu/IDC_NewGame": "New Game",
    "Startup/IDDIALOG_FrontEndPageMainMenu/IDC_LoadGame": "Load Game",
    "Startup/IDDIALOG_FrontEndPageMainMenu/IDC_Options": "Options",
    "Startup/IDDIALOG_FrontEndPageNewGame/IDC_Title": "New Game",
    "Startup/IDDIALOG_FrontEndPageNewGame/IDC_Instructions": "Start a NEW game by clicking on the slot you wish to use.",
    "Startup/IDDIALOG_FrontEndPageNewGame/IDC_SlotEmpty": "Empty",
    "Startup/IDDIALOG_FrontEndPageNewGame/IDC_SlotUsed": "Used",
    "Startup/IDDIALOG_FrontEndPageLoadGame/IDC_Title": "Loading Game",
    "Startup/IDDIALOG_FrontEndPageLoadGame/IDC_Instructions": "Load a game by clicking on a previously used slot.",
    "Startup/IDDIALOG_FrontEndPageLoadGame/IDC_SlotEmpty": "Empty",
    "Startup/IDDIALOG_FrontEndPageLoadGame/IDC_SlotUsed": "Used",
    "HPMenu/All/Select_Game_0001": "Are you sure you want to replace this game?",
    "HPMenu/All/Select_Game_0002": "Replace Game",
  ]
  static func localize(_ gameDir: String, _ package: String, _ section: String, _ key: String) -> String {
    words[package + "/" + section + "/" + key] ?? key
  }
  static func playArgs(_ gameDir: String, _ saveDir: String, _ cmdline: String) -> [String] {
    ["--root", gameDir, "--launched-by-launcher", "--hp2-savedir", saveDir, "--hp2-cmdline", cmdline]
  }

  // The judge's case kinds (tools/hp2/frontend_judge.py), answered as launcher/Desktop.Tests --hp2-frontend does.
  final class FakeFiles: Hp2Files {
    var size: (String) -> Int64 = { _ in -1 }
    var names: [String] = []
    var finds: [String] = [], deletes: [String] = [], dirs: [String] = []
    func fileSize(_ p: String) -> Int64 { size(p) }
    func delete(_ p: String) { deletes.append(p) }
    func makeDirectory(_ p: String) { dirs.append(p) }
    func findFiles(_ pattern: String) -> [String] { finds.append(pattern); return names }
    func deleteCache(_ dir: String) {}
  }
  // "...Slot<digit><tail>" -> the digit (the C# side's regex Slot(\d)<tail>$)
  static func slotOf(_ p: String, _ tail: String) -> Int? {
    guard p.hasSuffix(tail) else { return nil }
    let head = p.dropLast(tail.count)
    guard let d = head.last, d.isASCII, d.isNumber, head.dropLast().hasSuffix("Slot") else { return nil }
    return Int(String(d))
  }
  static func answer(_ line: String) -> String {
    let f = line.components(separatedBy: "\t")
    switch f[0] {
    case "launch":
      return "open|" + commandLine(f[1] == "new" ? .newGame : .loadGame, Int(f[2])!, f.count > 3 ? f[3] : "")
    case "click":
      let n = Int(f[2])!, used = f[3] == "1", yes = f[4] == "1"
      var asked = false
      let fs = FakeFiles(), slot = Slot(n: n, dir: "S\(n)", used: used, thumb: nil)
      let go = f[1] == "new" ? newGame(slot, confirmReplace: { asked = true; return yes }, fs) : loadGame(slot)
      let code = f[1] == "new" ? "1" : "2"
      return "ask=\(asked ? 1 : 0)|clear=\(fs.finds.isEmpty ? "" : String(n))|ini=\(go ? "LaunchCode=\(code),UseSaveSlot=\(n)" : "")|end=\(go ? "0" : "")"
    case "clear":
      let fs = FakeFiles()
      let files: [(name: String, size: Int64)] = f[3] == "-" ? [] : f[3].split(separator: ",").map { x in
        let p = x.split(separator: ":"); return (String(p[0]), Int64(p[1])!) }
      fs.names = files.map(\.name)
      fs.size = { p in files.first { join(f[2], $0.name) == p }?.size ?? -1 }
      clear(f[2], fs)
      return "find=\(fs.finds.joined(separator: ","))|delete=\(fs.deletes.joined(separator: ","))|used=0"
    default:  // scan
      let sz = f[2].components(separatedBy: ",")
      let fs = FakeFiles()
      fs.size = { p in slotOf(p, "\\Save0.usa").flatMap { sz[$0 - 1] == "-" ? nil : Int64(sz[$0 - 1]) } ?? -1 }
      let slots = scan(f[1], fs)
      let cleared = fs.finds.map { p in slotOf(p, "\\*.*").map(String.init) ?? "" }
      return "slots=" + slots.map { s in "\(s.n):\(s.dir):\(s.used ? 1 : 0)" }.joined(separator: ";") + "|mkdir=" + fs.dirs.joined(separator: ",")
        + "|clear=" + cleared.joined(separator: ",") + "|thumb=" + slots.compactMap(\.thumb).joined(separator: ",")
    }
  }

  // The menu on real folders (launcher/test_update.sh against Desktop.Tests --hp2-menu): the save directory, the page
  // texts, the slots after the scan, a click's outcome and engine arguments, then the files left in the save directory.
  static func menuReport(hp1Data: String, root: String, click: (Launch, Int, Bool)?) -> [String] {
    let sd = saveDir(hp1Data), fs = Files(sd)
    func L(_ s: String, _ k: String) -> String { localize(root, "Startup", s, k) }
    let slots = scan("", fs, saveFile: portSave)
    var o = ["savedir=" + sd,
             "main=" + ["IDC_Title", "IDC_Instructions", "IDC_NewGame", "IDC_LoadGame", "IDC_Options"].map { k in L("IDDIALOG_FrontEndPageMainMenu", k) }.joined(separator: "|")]
    for p in ["NewGame", "LoadGame"] {
      let s = "IDDIALOG_FrontEndPage" + p
      o.append(p + "=" + L(s, "IDC_Title") + "|" + slots.map { x in label(x, empty: L(s, "IDC_SlotEmpty"), used: L(s, "IDC_SlotUsed")) }.joined(separator: "|"))
    }
    o.append("slots=" + slots.map { s in "\(s.n):\(s.used ? 1 : 0):\(s.thumb ?? "-")" }.joined(separator: ";"))
    o.append("ask=" + localize(root, "HPMenu", "All", "Select_Game_0002") + "|" + localize(root, "HPMenu", "All", "Select_Game_0001"))
    if let (what, n, yes) = click {
      let s = slots[n - 1]
      let go = what == .newGame ? newGame(s, confirmReplace: { yes }, fs) : loadGame(s)
      o.append("go=\(go ? 1 : 0)" + (go ? " " + playArgs(root, sd, commandLine(what, n, "")).joined(separator: "|") : ""))
    }
    let files = (FileManager.default.subpaths(atPath: sd) ?? []).filter { r in
      var d: ObjCBool = false
      return FileManager.default.fileExists(atPath: sd + "/" + r, isDirectory: &d) && !d.boolValue }
    o.append("files=" + files.sorted { a, b in Array(a.utf16).lexicographicallyPrecedes(Array(b.utf16)) }.joined(separator: ","))
    return o
  }
}

// ── Look (2026-11-16, design approved by the author; the maintainer's notes): a fixed 600×560 window; the
// selected game owns the whole window — its palette, its title, its one primary button; everything rare is in the Menu.
// The Windows/Linux Launcher (launcher/Desktop) has the same functions (author 2026-11-16). Gamepad A/B/LB/RB/Start/Y
// (PadMap); Return = the primary button, Escape = Back, Cmd+1 / Cmd+2 = the game, Cmd+, = Menu; every target at least 44 pt.
func hex(_ v: UInt32, _ a: Double = 1) -> Color {
  Color(red: Double(v >> 16 & 255) / 255, green: Double(v >> 8 & 255) / 255, blue: Double(v & 255) / 255).opacity(a)
}

// The proposal's tokens (§4, contrast checked there): HP1 "Stone", HP2 "Chamber".
struct Palette {
  let top, mid, bottom, glow, glow2, text, dim, title, accent, accentHover, accentPress, onAccent, surface, surface2, stroke, ok, warn, err, popup: Color
  let stripe: [(Color, CGFloat)], diagonal: Bool
  // HP1 "Stone" in warm gold (author 2026-11-16): text >= 4.5:1 on the lightest point (#8C6224), the title (large) >= 3:1.
  static let hp1 = Palette(top: hex(0x8C6224), mid: hex(0x5A3A17), bottom: hex(0x2B1D0E), glow: hex(0xF2C14E, 0), glow2: hex(0xC8323C, 0.10),
    text: hex(0xFFF6E6), dim: hex(0xFAF0DD), title: hex(0xF2C14E), accent: hex(0xFFE7A3), accentHover: hex(0xFFF0C4), accentPress: hex(0xF2D27F),
    onAccent: hex(0x2A1A08), surface: .black.opacity(0.20), surface2: .black.opacity(0.30), stroke: hex(0xFFE7A3, 0.35),
    ok: hex(0xDBFAD6), warn: hex(0xFFEDBD), err: hex(0xFFDDD8), popup: hex(0x2A1C0D),
    stripe: [(hex(0xC8323C), 8), (hex(0x2B4FA3), 8), (hex(0xE2AE4A), 2)], diagonal: true)
  static let hp2 = Palette(top: hex(0x4A1216), mid: hex(0x2F0C10), bottom: hex(0x14050A), glow: hex(0x8E1A1F, 0.45), glow2: hex(0x3F6B3B, 0.16),
    text: hex(0xF3EAE2), dim: hex(0xC9B29A), title: hex(0xE8C066), accent: hex(0xD4AE62), accentHover: hex(0xE0BE78), accentPress: hex(0xB8956A),
    onAccent: hex(0x1E0A06), surface: hex(0xFFF0DC, 0.06), surface2: hex(0xFFF0DC, 0.11), stroke: hex(0xB8956A, 0.28),
    ok: hex(0x86B97F), warn: hex(0xE8C066), err: hex(0xF0A3A0), popup: hex(0x22100F),
    stripe: [(hex(0x3F6B3B), 10), (hex(0x2C4E29), 2)], diagonal: false)
}
struct PaletteKey: EnvironmentKey { static let defaultValue = Palette.hp1 }
extension EnvironmentValues { var palette: Palette { get { self[PaletteKey.self] } set { self[PaletteKey.self] = newValue } } }
// The pad-focused target's id while the ring shows (Launcher.padFocus once padUsed).
struct PadFocusKey: EnvironmentKey { static let defaultValue: String? = nil }
extension EnvironmentValues { var padFocus: String? { get { self[PadFocusKey.self] } set { self[PadFocusKey.self] = newValue } } }
// What a target's button style shows besides the pointer: the ring (pad / keyboard focus) and a forced look (--render-states).
struct TargetLook { var ring = false, force = 0 }   // force: 1 hover, 2 pressed
struct TargetLookKey: EnvironmentKey { static let defaultValue = TargetLook() }
struct ForcedLooksKey: EnvironmentKey { static let defaultValue: [String: Int] = [:] }
extension EnvironmentValues {
  var targetLook: TargetLook { get { self[TargetLookKey.self] } set { self[TargetLookKey.self] = newValue } }
  var forcedLooks: [String: Int] { get { self[ForcedLooksKey.self] } set { self[ForcedLooksKey.self] = newValue } }
}
// The open list's NSScrollView, found from inside its content: SwiftUI on macOS does not re-run a GeometryReader when
// AppKit scrolls (wheel, trackpad), so the indicator read a stale offset; the clip view's bounds notifications are live.
struct ScrollProbe: NSViewRepresentable {
  let b: Launcher
  final class Probe: NSView {
    var b: Launcher?; var watched: NSClipView?
    override func viewDidMoveToWindow() {
      super.viewDidMoveToWindow()
      guard let sv = enclosingScrollView, watched !== sv.contentView else { return }
      watched = sv.contentView; sv.contentView.postsBoundsChangedNotifications = true
      NotificationCenter.default.addObserver(self, selector: #selector(moved), name: NSView.boundsDidChangeNotification, object: sv.contentView)
      b?.listScroller = sv; DispatchQueue.main.async { self.moved() }
    }
    @objc func moved() { if let c = watched, b?.listOffset != c.bounds.minY { b?.listOffset = c.bounds.minY } }
    deinit { NotificationCenter.default.removeObserver(self) }
  }
  func makeNSView(context: Context) -> Probe { let v = Probe(); v.b = b; return v }
  func updateNSView(_ v: Probe, context: Context) {}
}
// The frames of the controls that open a list (Menu, Language, Start map): the list opens anchored to its control.
struct PopupAnchors: PreferenceKey {
  static let defaultValue: [String: Anchor<CGRect>] = [:]
  static func reduce(value: inout [String: Anchor<CGRect>], nextValue: () -> [String: Anchor<CGRect>]) { value.merge(nextValue()) { $1 } }
}

// Registers a control as a pad target (only while enabled: apply before .disabled) and draws the focus ring on it.
struct PadTargetModifier: ViewModifier {
  let id: String, primary: Bool, radius: CGFloat, run: () -> Void
  @Environment(\.isEnabled) var enabled
  @Environment(\.padFocus) var focus
  @Environment(\.forcedLooks) var forced
  @Environment(\.palette) var p
  func body(content: Content) -> some View {
    content.environment(\.targetLook, TargetLook(ring: enabled && focus == id, force: forced[id] ?? 0))
      .ring(enabled && focus == id, p, radius)
      .preference(key: PadTargets.self, value: enabled ? [PadTarget(id: id, primary: primary, run: run)] : [])
  }
}

// Titles: Cormorant Garamond Bold (SIL OFL 1.1, Contents/Resources with its OFL.txt; make_launcher.sh), else the system serif.
let titleFontName = "CormorantGaramond-Bold"
func registerTitleFont() {
  if let u = Bundle.main.url(forResource: titleFontName, withExtension: "ttf") { CTFontManagerRegisterFontsForURL(u as CFURL, .process, nil) }
}
func titleFont(_ size: CGFloat) -> Font {
  NSFont(name: titleFontName, size: size) != nil ? .custom(titleFontName, size: size) : .system(size: size - 4, weight: .bold, design: .serif)
}

extension View {
  func padTarget(_ id: String, primary: Bool = false, radius: CGFloat = 10, _ run: @escaping () -> Void) -> some View {
    modifier(PadTargetModifier(id: id, primary: primary, radius: radius, run: run))
  }
  // The focus ring: 3 pt in the title colour, outside the control.
  func ring(_ on: Bool, _ p: Palette, _ r: CGFloat) -> some View {
    overlay(RoundedRectangle(cornerRadius: r + 3).strokeBorder(p.title, lineWidth: on ? 3 : 0).padding(-4))
  }
  func panel(_ p: Palette) -> some View {
    background(RoundedRectangle(cornerRadius: 12).fill(.black.opacity(0.22))).overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(p.stroke))
  }
}

// The interaction layer every button style shares (both launchers): hover brightens, a press darkens and scales to 0.97,
// pad / keyboard focus looks like hover plus the ring, the pointing-hand cursor; 150 ms ease-out (a press 80 ms), and with
// Reduce Motion the colours change without movement or animation. Disabled: none of it.
struct Interactive<Content: View>: View {
  let pressed: Bool
  @ViewBuilder let look: (_ hot: Bool, _ down: Bool) -> Content
  @State var hover = false
  @State var cursor = false
  @Environment(\.isEnabled) var enabled
  @Environment(\.isFocused) var focused
  @Environment(\.targetLook) var t
  @Environment(\.accessibilityReduceMotion) var still
  var body: some View {
    let down = enabled && (pressed || t.force == 2), hot = enabled && (hover || focused || t.ring || t.force >= 1)
    look(hot, down).scaleEffect(down && !still ? 0.97 : 1)
      .animation(still ? nil : .easeOut(duration: down ? 0.08 : 0.15), value: hot)
      .animation(still ? nil : .easeOut(duration: down ? 0.08 : 0.15), value: down)
      .onHover { h in
        hover = h
        if h && enabled && !cursor { NSCursor.pointingHand.push(); cursor = true } else if !h && cursor { NSCursor.pop(); cursor = false }
      }
      .onDisappear { if cursor { NSCursor.pop(); cursor = false } }
  }
}

// One per screen: filled with the title gold, 52 pt; busy: a spinner before the label (Installing…, Playing…).
struct PrimaryButtonStyle: ButtonStyle {
  var busy = false
  @Environment(\.accessibilityReduceMotion) var still
  @Environment(\.isEnabled) var enabled
  @Environment(\.isFocused) var focused
  @Environment(\.palette) var p
  func makeBody(configuration c: Configuration) -> some View {
    Interactive(pressed: c.isPressed) { hot, down in
      HStack(spacing: 10) {
        if busy { ProgressView().controlSize(.small).tint(p.dim) }
        c.label
      }
      .font(.system(size: 17, weight: .semibold)).foregroundStyle(enabled ? p.onAccent : p.dim)
      .padding(.horizontal, 28).frame(minWidth: 168, minHeight: 52)
      .background(RoundedRectangle(cornerRadius: 10).fill(enabled ? (down ? p.accentPress : hot ? p.accentHover : p.accent) : p.surface2))
      .shadow(color: .black.opacity(enabled ? (hot && !down ? 0.45 : 0.35) : 0), radius: hot && !down ? 12 : 9, y: hot && !down ? 7 : 6)
      .offset(y: hot && !down && !still ? -1 : 0)   // the primary lifts
      .ring(focused, p, 10).contentShape(Rectangle())
    }
  }
}

// Secondary: a translucent surface with a border, 44 pt; small: 36 pt (inside banners only).
struct SecondaryButtonStyle: ButtonStyle {
  var small = false
  var held = false   // shown pressed while what it opened is open (the Menu button)
  @Environment(\.isEnabled) var enabled
  @Environment(\.isFocused) var focused
  @Environment(\.palette) var p
  func makeBody(configuration c: Configuration) -> some View {
    Interactive(pressed: c.isPressed) { hot, down in
      c.label.font(.system(size: small ? 13 : 14, weight: .semibold)).foregroundStyle(p.text)
        .padding(.horizontal, small ? 12 : 18).frame(minWidth: 44, minHeight: small ? 36 : 44)
        .background(RoundedRectangle(cornerRadius: 10).fill(down || held || hot ? p.surface2 : p.surface))
        .overlay(RoundedRectangle(cornerRadius: 10).fill(.black.opacity(down || held ? 0.15 : 0)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(hot || held ? p.accent.opacity(0.6) : p.stroke))
        .opacity(enabled ? 1 : 0.45).ring(focused, p, 10).contentShape(Rectangle())
    }
  }
}

// A row of the rows panel (Language, Mods, HP2 options): 52 pt, the whole row is the target.
struct RowButtonStyle: ButtonStyle {
  @Environment(\.isEnabled) var enabled
  @Environment(\.isFocused) var focused
  @Environment(\.palette) var p
  func makeBody(configuration c: Configuration) -> some View {
    Interactive(pressed: c.isPressed) { hot, down in
      c.label.font(.system(size: 14)).foregroundStyle(p.text).padding(.horizontal, 16).frame(maxWidth: .infinity, minHeight: 52)
        .background(RoundedRectangle(cornerRadius: 11).fill(down ? p.surface2 : hot ? p.surface : .clear).padding(1)).contentShape(Rectangle()).opacity(enabled ? 1 : 0.5).ring(focused, p, 0)
    }
  }
}

// No box of its own (What's new, the order chevrons, the save slots, the game switcher's segments): a tint over the label
// on hover (light) and press (dark).
struct FlatButtonStyle: ButtonStyle {
  var radius: CGFloat = 8
  @Environment(\.isEnabled) var enabled
  func makeBody(configuration c: Configuration) -> some View {
    Interactive(pressed: c.isPressed) { hot, down in
      c.label.overlay(RoundedRectangle(cornerRadius: radius).fill(down ? Color.black.opacity(0.18) : .white.opacity(hot ? 0.07 : 0)).allowsHitTesting(false))
        .contentShape(Rectangle())
    }
  }
}

// An item of an open list: hover / focus in the title gold (light), pressed (stronger), a checked item marked and bold.
struct ListItemStyle: ButtonStyle {
  @Environment(\.palette) var p
  func makeBody(configuration c: Configuration) -> some View {
    Interactive(pressed: c.isPressed) { hot, down in
      c.label.background(RoundedRectangle(cornerRadius: 6).fill(p.title.opacity(down ? 0.28 : hot ? 0.16 : 0))).contentShape(Rectangle())
    }
  }
}

struct Banner: View {
  let icon: String, color: Color, text: String
  var openLog: (() -> Void)? = nil    // "Open log": a failed game run's log (Launcher.openRunLog)
  var dismiss: (() -> Void)? = nil    // the × at the top right: clears the error it shows (Launcher.dismissError)
  @Environment(\.palette) var p
  var body: some View {
    HStack(alignment: .top, spacing: 10) {
      Image(systemName: icon).foregroundStyle(color).padding(.top, 2)
      VStack(alignment: .leading, spacing: 8) {
        Text(text).foregroundStyle(p.text).textSelection(.enabled)
          .fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
        if openLog != nil {
          HStack(spacing: 8) {
            if let openLog { Button("Open log", action: openLog).buttonStyle(SecondaryButtonStyle(small: true)).padTarget("openlog", openLog) }
          }
        }
      }
      if let dismiss {
        Button(action: dismiss) { Image(systemName: "xmark").font(.system(size: 11, weight: .bold)).foregroundStyle(p.dim).frame(width: 28, height: 28) }
          .buttonStyle(FlatButtonStyle()).accessibilityLabel("Dismiss").help("Dismiss").padTarget("dismiss", radius: 8, dismiss)
          .padding(.top, -4).padding(.trailing, -4)
      }
    }
    .font(.system(size: 13)).padding(10)
    .background(RoundedRectangle(cornerRadius: 10).fill(.black.opacity(0.30)))
    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(color))
    .accessibilityElement(children: .contain)
  }
}

// The decorative 4 pt band at the bottom: harlequin (HP1), serpent green (HP2).
struct Stripe: View {
  @Environment(\.palette) var p
  var body: some View {
    Canvas { ctx, size in
      let skew = p.diagonal ? size.height : 0
      var x = -skew, i = 0
      while x < size.width {
        let (c, w) = p.stripe[i % p.stripe.count]
        var path = Path()
        path.addLines([CGPoint(x: x, y: size.height), CGPoint(x: x + w, y: size.height), CGPoint(x: x + w + skew, y: 0), CGPoint(x: x + skew, y: 0)])
        ctx.fill(path, with: .color(c))
        x += w; i += 1
      }
    }.frame(height: 4).opacity(0.85).accessibilityHidden(true)
  }
}

struct MainView: View {
  @ObservedObject var b: Launcher
  var live = true                  // false: an offscreen state picture (--render-states), no disk reads on appear
  @State var dropping = false
  @Environment(\.accessibilityReduceMotion) var still
  var p: Palette { b.hp2Tab ? .hp2 : .hp1 }
  var setup: Bool { !b.installed || b.running || !b.inputs.isEmpty }
  var hp2Setup: Bool { b.hp2RootPath == nil || b.running || !b.inputs.isEmpty }   // the HP2 tab: HP1's setup view, installing HP2
  var setupInstalled: Bool { b.hp2Tab ? Launcher.isInstalled(Launcher.hp2GameURL) : b.installed }
  var version: String { (Bundle.main.object(forInfoDictionaryKey: "HPEngineVersion") as? String).map { " " + $0 } ?? "" }

  var body: some View {
    VStack(alignment: .leading, spacing: 0) {
      topBar
      if let n = b.newer { updateBar(n) }
      ScrollView {   // the window's height is fixed: a crowded state (update + error + every row) scrolls, never clips
        VStack(alignment: .leading, spacing: 0) {
          if b.hp2Tab { if hp2Setup { setupView } else if b.modsOpen { modsView } else { hp2View } } else if setup { setupView } else if b.modsOpen { modsView } else { installedView }
          VStack(spacing: 8) {
            if let w = b.warning { Banner(icon: "exclamationmark.triangle.fill", color: p.warn, text: w).transition(banner) }   // HP1Setup's: the selected tab's (reset on a switch)
            if let e = GameScope.error(hp2Tab: b.hp2Tab, hp1: b.error, hp2: b.hp2Error, launcher: b.launcherError) {
              let hp2 = b.hp2Tab
              Banner(icon: "xmark.octagon.fill", color: p.err, text: e, openLog: e == (hp2 ? b.hp2RunError : b.runError) ? { b.openRunLog(hp2: hp2) } : nil,
                     dismiss: { b.dismissError() }).transition(banner)
            }
          }.padding(.horizontal, 24).padding(.top, 16)
          // a new warning or error slides in (the failure's micro-feedback); with Reduce Motion it fades
          .animation(live ? .easeOut(duration: 0.2) : nil, value: [b.warning ?? "", GameScope.error(hp2Tab: b.hp2Tab, hp1: b.error, hp2: b.hp2Error, launcher: b.launcherError) ?? ""])
        }.padding(.bottom, 12)
      }
      Stripe()
      HStack(spacing: 14) {
        Text("HPEngine\(version)").foregroundStyle(p.dim)
        Spacer()
        if b.padConnected {   // the button hints: only while a pad is connected (proposal §6)
          ForEach([("A", "Select"), ("B", "Back"), ("LB RB", "Game"), ("☰", "Menu")], id: \.0) { keys, what in
            HStack(spacing: 6) {
              HStack(spacing: 3) {
                ForEach(keys.split(separator: " ").map(String.init), id: \.self) { k in
                  Text(k).font(.system(size: 11, weight: .bold)).foregroundStyle(p.text).padding(.horizontal, 5).frame(minWidth: 22, minHeight: 22)
                    .background(Capsule().fill(p.surface2)).overlay(Capsule().strokeBorder(p.stroke))
                }
              }
              Text(what).foregroundStyle(p.dim)
            }
          }
        }
      }
      .font(.system(size: 12)).padding(.horizontal, 24).padding(.vertical, 10).frame(minHeight: 44)
    }
    .frame(width: 600, height: 560)
    .background(background)
    .overlayPreferenceValue(PopupAnchors.self) { popupLayer($0) }
    .environment(\.palette, p)
    .environment(\.padFocus, b.padUsed ? b.padFocus : nil)
    .environment(\.forcedLooks, b.forcedLooks)
    .environment(\.colorScheme, .dark)
    .onPreferenceChange(PadTargets.self) { t in b.padTargets = t; b.keepFocus() }
    .onAppear { if live { b.checkForUpdate(); b.loadMods(); b.startPad(); b.startKeys() } }
    .onChange(of: b.installed) { _ in b.loadMods() }
    .onDrop(of: [.fileURL], isTargeted: $dropping) { items in
      for item in items {
        _ = item.loadObject(ofClass: URL.self) { url, _ in
          if let url { Task { @MainActor in if b.modsOpen && b.modsGame != nil { b.addMods([url]) } else { b.add([url]) } } }
        }
      }
      return true
    }
  }

  var banner: AnyTransition { still ? .opacity : .opacity.combined(with: .move(edge: .top)) }

  var background: some View {
    ZStack {
      LinearGradient(colors: [p.top, p.mid, p.bottom], startPoint: .top, endPoint: .bottom)
      RadialGradient(colors: [p.glow, .clear], center: .topTrailing, startRadius: 0, endRadius: 560)
      RadialGradient(colors: [p.glow2, .clear], center: .bottomLeading, startRadius: 0, endRadius: 420)
    }
  }

  // The open list (Menu, Language, Start map): over the window, anchored to the control that opened it — under it (above
  // when there is no room below), aligned to its nearer edge — growing out of it; a click outside or Escape / B closes it.
  func popupLayer(_ anchors: [String: Anchor<CGRect>]) -> some View {
    GeometryReader { g in
      if let pop = b.popup, let a = anchors[pop.anchor] {
        let r = g[a], W = g.size.width, H = g.size.height
        let width: CGFloat = pop.anchor == "menu" ? 300 : max(r.width, 240)
        let est = popupHeight(pop) + 12   // its height before layout (items + padding)
        // under it when it fits there (or there is more room there than above); a list taller than its room scrolls
        let floor = H - 52   // above the footer
        let below = r.maxY + 6 + est <= floor || floor - r.maxY >= r.minY, right = r.midX > W / 2
        let room = (below ? floor - r.maxY - 6 : r.minY - 14) - 4
        let x = min(max(right ? r.maxX - width : r.minX, 12), W - width - 12)
        ZStack(alignment: .topLeading) {
          Color.black.opacity(0.25).contentShape(Rectangle()).onTapGesture { b.closePopup() }.transition(.opacity)
          popupList(pop, height: est > room ? room : nil).frame(width: width)
            .alignmentGuide(.leading) { _ in -x }
            .alignmentGuide(.top) { d in -(below ? r.maxY + 6 : max(8, r.minY - 6 - d.height)) }
            .transition(.asymmetric(insertion: still ? .opacity : .scale(scale: 0.94, anchor: UnitPoint(x: right ? 1 : 0, y: below ? 0 : 1)).combined(with: .opacity),
                                    removal: .opacity))
        }
        .environment(\.palette, p)
      }
    }
    .animation(live ? .easeOut(duration: b.popup == nil ? 0.1 : 0.16) : nil, value: b.popup?.anchor)
  }

  // height: the list's room when its items do not fit (then it scrolls: wheel / trackpad, and the focused item — keys, pad,
  // type-ahead — kept in view, the choice in view when it opens), nil: as tall as its items.
  func popupList(_ pop: Popup, height: CGFloat?) -> some View {
    ScrollViewReader { proxy in
      ScrollView(.vertical) {
        popupItems(pop).background(ScrollProbe(b: b))
      }
        .scrollIndicators(.never).scrollDisabled(height == nil)   // .never: .hidden still shows the system scroller when scroll bars are set to always show (a mouse attached)
        .frame(height: height ?? popupHeight(pop) + 8)
        .overlay(alignment: .topTrailing) {
          // the scroll indicator, always shown on a list that scrolls (the system's overlay scroller hides itself)
          if let h = height {
            let content = popupHeight(pop) + 8, thumb = max(24, h * h / content)
            // drawn 4 pt wide on a 14 pt track that takes the drag (the system scroller is off: .never above)
            Capsule().fill(p.text.opacity(0.45)).frame(width: 4, height: thumb)
              .offset(x: -3, y: min(max(b.listOffset / (content - h), 0), 1) * (h - thumb))
              .frame(width: 14, height: h, alignment: .topTrailing).contentShape(Rectangle())
              .gesture(DragGesture(minimumDistance: 0).onChanged { g in b.dragList((g.location.y - thumb / 2) / (h - thumb)) })
              .accessibilityHidden(true)
          }
        }
        .onAppear { if let f = b.padFocus { proxy.scrollTo(f, anchor: .center) } }
        .onChange(of: b.padFocus) { f in if let f, f.hasPrefix("pop") { proxy.scrollTo(f) } }
    }
    .padding(2)
    .background(RoundedRectangle(cornerRadius: 10).fill(p.popup)).overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.white.opacity(0.14)))
    .shadow(color: .black.opacity(0.4), radius: 12, y: 6)
  }
  func popupHeight(_ pop: Popup) -> CGFloat { Launcher.popupHeight(pop) }

  func popupItems(_ pop: Popup) -> some View {
    VStack(alignment: .leading, spacing: 0) {
      ForEach(Array(pop.items.enumerated()), id: \.offset) { i, it in
        if it.header && it.title.isEmpty { Divider().overlay(p.stroke).padding(.vertical, 4) }
        else if it.header {
          Text(it.title.uppercased()).font(.system(size: 11, weight: .semibold)).tracking(0.6).foregroundStyle(p.dim)
            .padding(.horizontal, 12).padding(.bottom, 4).frame(height: 28, alignment: .bottomLeading)
        } else {
          let ring = b.padUsed && b.padFocus == "pop\(i)"
          Button { b.pick(it) } label: {
            HStack {
              Text(it.title).fontWeight(it.checked ? .semibold : .regular).foregroundStyle(it.destructive ? p.err : p.text)
              Spacer()
              if it.checked { Image(systemName: "checkmark").foregroundStyle(p.title).accessibilityLabel("Selected") }
            }
            .font(.system(size: 14)).padding(.horizontal, 12).frame(maxWidth: .infinity, minHeight: 40, alignment: .leading)
          }
          .buttonStyle(ListItemStyle()).environment(\.targetLook, TargetLook(ring: ring, force: b.forcedLooks["pop\(i)"] ?? 0))
          .disabled(!it.enabled).opacity(it.enabled ? 1 : 0.45).ring(ring, p, 6)
          .accessibilityAddTraits(it.checked ? .isSelected : []).id("pop\(i)")
        }
      }
    }
    .padding(4)   // room for the focus ring inside the scroller
  }

  // ── Top bar: the game switcher (one window, the game a branch on the tab, as launcher/Desktop/MainWindow.Hp2.cs) + Menu.
  var topBar: some View {
    let on = Hp2Release.available, hp2Set = on && (b.hp2Tab ? b.hp2RootPath != nil : Launcher.hp2Root != nil)
    // a setup runs on the selected tab only: the tabs do not switch while it runs
    let hp1 = { guard !b.running else { return }; b.switchTo(hp2: false); b.modsOpen = false },
        hp2 = { guard Hp2Release.available, !b.running else { return }; b.switchTo(hp2: true); b.modsOpen = false; b.hp2Page = "Main"; b.hp2Scan() }
    return HStack(spacing: 8) {
      HStack(spacing: 4) {
        gameTab("Harry Potter 1", on: !b.hp2Tab, dot: b.installed ? p.ok : (b.running && !b.hp2Tab ? p.warn : nil),
                note: b.running && !b.hp2Tab ? "installing" : (b.installed ? nil : "not set up"), hp1)
          .padTarget("tab1", radius: 9, hp1).keyboardShortcut("1", modifiers: .command)
        gameTab("Harry Potter 2", on: b.hp2Tab, dot: hp2Set ? p.ok : (b.running && b.hp2Tab ? p.warn : nil),
                note: b.running && b.hp2Tab ? "installing" : Hp2Release.caption(on, folderSet: hp2Set), hp2)
          .padTarget("tab2", radius: 9, hp2).keyboardShortcut("2", modifiers: .command).disabled(!on).opacity(on ? 1 : 0.45)
          .help(Hp2Release.tip(on) ?? "")
      }
      .padding(4).background(RoundedRectangle(cornerRadius: 12).fill(.black.opacity(0.28)))
      Button { b.openPopup(b.menuItems(), from: "menu") } label: { Label("Menu", systemImage: "line.3.horizontal") }
        .buttonStyle(SecondaryButtonStyle(held: b.popup?.anchor == "menu")).padTarget("menu") { b.openPopup(b.menuItems(), from: "menu") }
        .anchorPreference(key: PopupAnchors.self, value: .bounds) { ["menu": $0] }
        .keyboardShortcut(",", modifiers: .command)
    }
    .padding(.horizontal, 16).padding(.top, 12)
  }

  func gameTab(_ name: String, on: Bool, dot: Color?, note: String?, _ go: @escaping () -> Void) -> some View {
    Button(action: go) {
      HStack(spacing: 8) {
        if let dot { Circle().fill(dot).frame(width: 8, height: 8) }
        Text(name).font(.system(size: 14, weight: on ? .semibold : .medium)).foregroundStyle(on ? p.text : p.dim)
        if let note { Text("· " + note).font(.system(size: 12, weight: .medium)).foregroundStyle(p.dim) }
      }
      .frame(maxWidth: .infinity, minHeight: 44)
      .background(RoundedRectangle(cornerRadius: 9).fill(on ? p.surface2 : .clear))
      .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(on ? p.stroke : .clear))
      .contentShape(Rectangle())
    }
    .buttonStyle(FlatButtonStyle(radius: 9)).accessibilityAddTraits(on ? .isSelected : [])
  }

  // "Update" only when this build can verify the download (as Desktop's SelfUpdate.Offered); else the release page alone.
  func updateBar(_ n: Launcher.Release) -> some View {
    HStack(spacing: 10) {
      Image(systemName: "arrow.down.circle.fill").foregroundStyle(p.title)
      Text(b.updating ? "Downloading Launcher \(n.version)…" : "HPEngine \(n.version) is available.").foregroundStyle(p.text)
      Spacer()
      if b.updating { ProgressView().controlSize(.small) } else {
        Button { NSWorkspace.shared.open(n.page) } label: { Text("What's new").foregroundStyle(p.text).frame(minHeight: 36).padding(.horizontal, 8) }.buttonStyle(FlatButtonStyle(radius: 6))
          .padTarget("whatsnew", radius: 6) { NSWorkspace.shared.open(n.page) }
        if n.zip != nil && n.sig != nil && Launcher.updateKey != nil {
          Button("Update") { b.updateLauncher() }.buttonStyle(SecondaryButtonStyle(small: true)).padTarget("update") { b.updateLauncher() }
            .disabled(b.running || b.playing).help(b.playing ? "Quit the game first" : "")
        }
      }
    }
    .font(.system(size: 13, weight: .medium)).padding(.leading, 14).padding(.trailing, 8).padding(.vertical, 6)
    .background(RoundedRectangle(cornerRadius: 10).fill(.black.opacity(0.30))).overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(p.stroke))
    .padding(.horizontal, 16).padding(.top, 12)
  }

  // The game's title, a line under it (sub), a meta line (status, edition) and a note under that (the edition's detail).
  func hero<M: View>(_ title: String, sub: String? = nil, note: String? = nil, @ViewBuilder meta: () -> M) -> some View {
    VStack(alignment: .leading, spacing: 8) {
      Text(title).font(titleFont(36)).foregroundStyle(p.title).lineLimit(2).fixedSize(horizontal: false, vertical: true)
        .accessibilityAddTraits(.isHeader)
      if let sub { Text(sub).font(.system(size: 13)).foregroundStyle(p.dim).fixedSize(horizontal: false, vertical: true) }
      HStack(spacing: 10) { meta() }.font(.system(size: 13)).foregroundStyle(p.dim)
      if let note { Text(note).font(.system(size: 12)).foregroundStyle(p.dim).fixedSize(horizontal: false, vertical: true) }
    }
    .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 24).padding(.top, 20)
  }

  func status(_ text: String, _ icon: String, _ color: Color) -> some View {
    HStack(spacing: 6) { Image(systemName: icon).foregroundStyle(color); Text(text).foregroundStyle(p.text) }
  }

  // A sub-page heading: Back (Escape, pad B: Launcher.goBack runs this target) and the page's title.
  func pageHeader(_ title: String, primary: Bool = false, back: @escaping () -> Void) -> some View {
    HStack(spacing: 14) {
      Button(action: back) { Label("Back", systemImage: "chevron.left") }.buttonStyle(SecondaryButtonStyle()).padTarget("back", primary: primary, back)
      Text(title).font(titleFont(30)).foregroundStyle(p.title).lineLimit(1).accessibilityAddTraits(.isHeader)
    }
    .padding(.horizontal, 24).padding(.top, 20)
  }

  // A drop-down: the current choice; the list opens in the window (Launcher.openPopup), for mouse, keys and pad alike.
  func dropDown(_ id: String, _ label: String, _ items: @escaping () -> [PopItem]) -> some View {
    Button { b.openPopup(items(), from: id) } label: {
      HStack(spacing: 6) { Text(label).lineLimit(1); Image(systemName: "chevron.up.chevron.down").font(.system(size: 10, weight: .semibold)).foregroundStyle(p.dim) }
    }
    .buttonStyle(SecondaryButtonStyle(held: b.popup?.anchor == id)).padTarget(id) { b.openPopup(items(), from: id) }
    .anchorPreference(key: PopupAnchors.self, value: .bounds) { [id: $0] }
  }

  // ── HP1: the game is set up
  var installedView: some View {
    let d = b.edition.map { Launcher.editionDisplay($0, b.languageList) }
    return VStack(alignment: .leading, spacing: 0) {
      hero(Launcher.gameTitle(b.edition), note: d?.detail) {
        if b.playing { status("Playing", "play.fill", p.ok) } else { status("Ready to play", "checkmark", p.ok) }
      }
      HStack(spacing: 12) {
        Button { b.play() } label: { if b.playing { Text("Playing…") } else { Label("Play", systemImage: "play.fill") } }
          .buttonStyle(PrimaryButtonStyle(busy: b.playing)).padTarget("play", primary: true) { b.play() }.keyboardShortcut(.defaultAction).disabled(b.playing)
        if !b.startMaps.isEmpty {
          HStack(spacing: 8) {
            Text(Launcher.startMapTitle).font(.system(size: 12)).foregroundStyle(p.dim)
            let labels = Launcher.startMapLabels(b.startMaps)
            dropDown("startmap", b.startMaps.firstIndex { $0.map == b.startMap }.map { labels[$0] } ?? Launcher.startMapGame) {
              [PopItem(title: Launcher.startMapGame, checked: b.startMap == nil) { b.startMap = nil }]
                + zip(b.startMaps, Launcher.startMapLabels(b.startMaps)).map { m, l in PopItem(title: l, checked: b.startMap == m.map) { b.startMap = m.map } }
            }.disabled(b.playing)
          }
        }
      }
      .padding(.horizontal, 24).padding(.top, 20)
      VStack(spacing: 0) {
        // Language: only with a choice to make (two or more languages, or a pick whose folder is gone).
        let langs = Launcher.languageEntries(b.languageList, selected: b.language)
        if langs.count > 2 {
          HStack {
            Text("Language").foregroundStyle(p.text)
            Spacer()
            dropDown("language", langs.first { $0.code == b.language }?.label ?? Launcher.languageDefault) {
              Launcher.languageEntries(b.languageList, selected: b.language).map { e in PopItem(title: e.label, checked: e.code == b.language) { b.setLanguage(e.code) } }
            }.disabled(b.playing || b.running)
          }
          .font(.system(size: 14)).padding(.leading, 16).padding(.trailing, 8).frame(minHeight: 52)
          Divider().overlay(p.stroke)
        }
        modsRow
      }
      .panel(p).padding(.horizontal, 24).padding(.top, 24)
    }
  }

  // The Mods row of a game page (HP1 installedView, HP2 hp2Main): a summary that opens the Mods panel.
  var modsRow: some View {
    Button { b.modsOpen = true } label: {
      HStack(spacing: 8) {
        Text("Mods")
        Spacer()
        if !b.modsNotice.isEmpty { Image(systemName: "exclamationmark.circle").foregroundStyle(p.warn).accessibilityLabel("Notes") }
        Text(b.mods.isEmpty ? "None" : "\(b.mods.filter(\.enabled).count) of \(b.mods.count) on").foregroundStyle(p.dim).font(.system(size: 13))
        Image(systemName: "chevron.right").foregroundStyle(p.dim)
      }
    }.buttonStyle(RowButtonStyle()).padTarget("mods", radius: 0) { b.modsOpen = true }
  }

  // ── The Mods panel of the selected tab (list, order, notes, add, remove, Mods folder)
  var modsView: some View {
    VStack(alignment: .leading, spacing: 12) {
      pageHeader("Mods", primary: true) { b.modsOpen = false }
      VStack(alignment: .leading, spacing: 8) {
        if b.mods.isEmpty {
          Text(Launcher.noModsText)
            .font(.system(size: 13)).foregroundStyle(p.dim).fixedSize(horizontal: false, vertical: true).padding(16)
        } else {
          ScrollView { VStack(spacing: 0) { ForEach(b.mods) { m in modRow(m) } } }.frame(maxHeight: 176).panel(p)
        }
        if !b.modsNotice.isEmpty {
          HStack(alignment: .top, spacing: 6) {
            Image(systemName: "info.circle").foregroundStyle(p.warn).padding(.top, 1)
            ScrollView { Text(b.modsNotice.joined(separator: "\n")).frame(maxWidth: .infinity, alignment: .leading) }.frame(maxHeight: 64)
          }.font(.system(size: 12)).foregroundStyle(p.dim)
        }
        if b.mods.contains(where: \.enabled) { Text(Launcher.orderHint).font(.system(size: 12)).foregroundStyle(p.dim) }
        HStack(spacing: 12) {
          Button { b.chooseMods() } label: { Label("Add Mod…", systemImage: "plus") }.buttonStyle(SecondaryButtonStyle())
            .padTarget("addmod") { b.chooseMods() }.disabled(b.playing || b.addingMod)
          Button { b.openModsFolder() } label: { Label("Open Mods Folder", systemImage: "folder.badge.plus") }.buttonStyle(SecondaryButtonStyle())
            .padTarget("openmods") { b.openModsFolder() }
          Button { b.loadMods() } label: { Label("Refresh", systemImage: "arrow.clockwise") }.buttonStyle(SecondaryButtonStyle())
            .padTarget("refresh") { b.loadMods() }
        }
      }
      .padding(.horizontal, 24)
    }
  }

  func modRow(_ m: Launcher.Mod) -> some View {
    let on = b.mods.filter(\.enabled)
    let i = on.firstIndex(of: m)
    let name = m.name, flip = { if let now = b.mods.first(where: { $0.name == name }) { b.setMod(name, !now.enabled) } }
    return HStack(spacing: 10) {
      Toggle(m.name, isOn: Binding(get: { m.enabled }, set: { b.setMod(m.name, $0) })).toggleStyle(.switch).labelsHidden()
        .tint(p.accent).padTarget("mod:" + name, radius: 8, flip).disabled(b.playing)
      if let n = Launcher.orderNumber(b.mods, m) { Text("\(n)").font(.system(size: 12)).foregroundStyle(p.dim).frame(minWidth: 14) }
      VStack(alignment: .leading, spacing: 2) {
        HStack(spacing: 10) {
          Text(m.name).foregroundStyle(m.enabled ? p.text : p.dim).lineLimit(1)
          if m.missing { Text("folder missing").font(.system(size: 12)).foregroundStyle(p.warn) }
        }
        // HP2: the line Play refuses with, under the mod
        if b.hp2Tab, let game = b.modsGame, let line = Launcher.newerEngineLine(game: game, mod: m.name) {
          Text(line).font(.system(size: 12)).foregroundStyle(p.warn).fixedSize(horizontal: false, vertical: true)
        }
      }
      Spacer()
      if let i {
        Button { b.moveMod(m.name, by: -1) } label: { Image(systemName: "chevron.up").frame(width: 44, height: 44).contentShape(Rectangle()) }
          .buttonStyle(FlatButtonStyle()).foregroundStyle(i > 0 ? p.text : p.dim.opacity(0.4)).padTarget("up:" + name, radius: 8) { b.moveMod(name, by: -1) }
          .disabled(i == 0 || b.playing).accessibilityLabel("Move \(m.name) up")
        Button { b.moveMod(m.name, by: 1) } label: { Image(systemName: "chevron.down").frame(width: 44, height: 44).contentShape(Rectangle()) }
          .buttonStyle(FlatButtonStyle()).foregroundStyle(i < on.count - 1 ? p.text : p.dim.opacity(0.4)).padTarget("down:" + name, radius: 8) { b.moveMod(name, by: 1) }
          .disabled(i == on.count - 1 || b.playing).accessibilityLabel("Move \(m.name) down")
      }
      if !m.missing {
        Button { b.removeMod(name) } label: { Image(systemName: "xmark").frame(width: 44, height: 44).contentShape(Rectangle()) }
          .buttonStyle(FlatButtonStyle()).foregroundStyle(p.dim).padTarget("remove:" + name, radius: 8) { b.removeMod(name) }
          .disabled(b.playing).accessibilityLabel("Remove \(m.name)").help("Remove \(m.name)")
      }
    }
    .font(.system(size: 14)).padding(.horizontal, 12).frame(minHeight: 44)
  }

  // ── HP1: no game yet / files chosen / installing
  var setupView: some View {
    VStack(alignment: .leading, spacing: 0) {
      hero(b.hp2Tab ? Launcher.hp2Title : Launcher.gameTitle(b.edition), sub: !setupInstalled && !b.running ? "Set up once from your own copy of the game. Nothing of the game is included." : nil) {
        if b.running { status("Installing…", "sparkle", p.title) }
        else if b.playing { status("Playing", "play.fill", p.ok) }
        else if setupInstalled { status("Ready to play", "checkmark", p.ok) }
        else { status("Not installed", "circle", p.dim) }
      }
      if b.running || b.step >= 0 { stepsPanel } else { dropZone }
      HStack(spacing: 12) {
        if b.running {
          Button("Installing…") {}.buttonStyle(PrimaryButtonStyle(busy: true)).disabled(true)
        } else if !b.inputs.isEmpty {
          Button(setupInstalled ? "Install Again" : "Install") { b.install() }
            .buttonStyle(PrimaryButtonStyle()).padTarget("install", primary: true) { b.install() }.keyboardShortcut(.defaultAction)
            .disabled(b.inputs.isEmpty || b.running || b.playing || b.updating)
            .help(b.playing ? "Quit the game first" : "")
          Button("Add more…") { b.choose() }.buttonStyle(SecondaryButtonStyle()).padTarget("addmore") { b.choose() }
          Button("Clear") { b.inputs = [] }.buttonStyle(SecondaryButtonStyle()).padTarget("clear") { b.inputs = [] }
        } else if b.step >= 0 {
          Button("Choose…") { b.choose() }.buttonStyle(SecondaryButtonStyle()).padTarget("choose", primary: true) { b.choose() }
        }
      }
      .padding(.horizontal, 24).padding(.top, 16)
      if b.running {
        Text("You can close the window: the copy resumes next time.").font(.system(size: 12)).foregroundStyle(p.dim).padding(.horizontal, 24).padding(.top, 10)
      }
    }
  }

  var dropZone: some View {
    VStack(spacing: 8) {
      if b.inputs.isEmpty {
        Image(systemName: "opticaldisc").font(.system(size: 26, weight: .light)).foregroundStyle(p.title)
        Text("Drop your copy of the game here").font(.system(size: 16, weight: .semibold)).foregroundStyle(p.text)
        Text("Installed game folder, game CD, disc image (.iso, .bin/.cue, .mdf), installer (Setup.exe) or .zip")
          .font(.system(size: 13)).foregroundStyle(p.dim).multilineTextAlignment(.center).frame(maxWidth: 400)
        Button("Choose…") { b.choose() }.buttonStyle(PrimaryButtonStyle()).padTarget("choose", primary: true) { b.choose() }
          .keyboardShortcut(.defaultAction).padding(.top, 6)
      } else {
        ScrollView {
          VStack(alignment: .leading, spacing: 6) {
            ForEach(b.inputs, id: \.self) { u in
              Label(u.lastPathComponent, systemImage: "doc.fill").foregroundStyle(p.text).lineLimit(1).truncationMode(.middle)
            }
          }.frame(maxWidth: .infinity, alignment: .leading)
        }.frame(maxHeight: 140)
      }
    }
    .frame(maxWidth: .infinity, minHeight: 208).padding(16)
    .background(RoundedRectangle(cornerRadius: 14).fill(dropping ? p.accent.opacity(0.10) : .black.opacity(0.18)))
    .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(dropping ? p.accent : p.stroke, style: StrokeStyle(lineWidth: 2, dash: [7, 5])))
    .padding(.horizontal, 24).padding(.top, 20)
  }

  // The four HP1Setup steps: icon + text (not colour alone); the bar while copying/checking.
  var stepsPanel: some View {
    VStack(alignment: .leading, spacing: 0) {
      ForEach(Array(steps.enumerated()), id: \.offset) { i, s in
        let done = i < b.step, now = i == b.step && b.running
        HStack(spacing: 10) {
          Image(systemName: done ? "checkmark" : (now ? "circle.dotted" : (i == b.step ? "xmark" : "circle")))
            .foregroundStyle(done ? p.ok : (now ? p.title : (i == b.step ? p.err : p.dim))).frame(width: 16)
          Text(s.text).font(.system(size: 14, weight: now ? .semibold : .regular)).foregroundStyle(now ? p.text : p.dim)
        }
        .frame(height: 34).accessibilityElement(children: .combine)
      }
      if steps.indices.contains(b.step) && !["find", "unpack"].contains(steps[b.step].key) && b.running {
        ProgressView(value: b.progress).tint(p.accent).padding(.top, 10)
        HStack { Spacer(); Text("\(Int(b.progress * 100))%") }.font(.system(size: 12)).foregroundStyle(p.dim).padding(.top, 4)
      }
    }
    .padding(16).frame(maxWidth: .infinity, alignment: .leading).panel(p)
    .padding(.horizontal, 24).padding(.top, 20)
  }

  // ── HP2: HP2's own front end (Main Menu, New Game, Load Game; its Options are the game's own menu, author 2026-11-16), its page flow from Hp2FrontEnd.
  var hp2View: some View {
    VStack(alignment: .leading, spacing: 0) {
      if let root = b.hp2RootPath {
        let section = "IDDIALOG_FrontEndPage" + (b.hp2Page == "Main" ? "MainMenu" : b.hp2Page)
        let L = { (k: String) in Hp2FrontEnd.localize(root, "Startup", section, k) }
        if b.hp2Page == "Main" { hp2Main(L) } else { hp2Page(L) }
      }
    }
  }

  // The original's three choices in its order; the accent (the primary) is Load Game when a slot is used, else New Game.
  func hp2Main(_ L: @escaping (String) -> String) -> some View {
    let used = b.hp2Slots.filter(\.used).count
    let loadFirst = used > 0
    return VStack(alignment: .leading, spacing: 0) {
      hero(Launcher.hp2Title) {
        if b.playing { status("Playing", "play.fill", p.ok) } else { status("Ready to play", "checkmark", p.ok) }
        Text(used == 1 ? "1 saved game" : "\(used) saved games")
      }
      // The original's IDC_Instructions is not shown here (both launchers): it sends the player to an Options button the launcher has not.
      HStack(spacing: 12) {
        hp2Choice("newgame", L("IDC_NewGame"), primary: !loadFirst) { b.hp2Page = "NewGame" }
        hp2Choice("loadgame", L("IDC_LoadGame"), primary: loadFirst) { b.hp2Page = "LoadGame" }
      }
      .padding(.horizontal, 24).padding(.top, 20)
      modsRow.panel(p).padding(.horizontal, 24).padding(.top, 20)   // as HP1's (installedView)
    }
  }

  @ViewBuilder func hp2Choice(_ id: String, _ title: String, primary: Bool, _ go: @escaping () -> Void) -> some View {
    if primary { Button(title, action: go).buttonStyle(PrimaryButtonStyle()).padTarget(id, primary: true, go).keyboardShortcut(.defaultAction).disabled(b.playing) }
    else { Button(title, action: go).buttonStyle(SecondaryButtonStyle()).frame(minHeight: 52).padTarget(id, go).disabled(b.playing) }
  }

  func hp2Page(_ L: @escaping (String) -> String) -> some View {
    let load = b.hp2Page == "LoadGame"
    return VStack(alignment: .leading, spacing: 0) {
      pageHeader(L("IDC_Title"), primary: !b.hp2Slots.contains { !b.playing && ($0.used || !load) }) { b.hp2Page = "Main" }
      if !L("IDC_Instructions").isEmpty && L("IDC_Instructions") != "IDC_Instructions" {
        Text(L("IDC_Instructions")).font(.system(size: 13)).foregroundStyle(p.dim).fixedSize(horizontal: false, vertical: true)
          .padding(.horizontal, 24).padding(.top, 10)
      }
      hp2SlotsGrid(L)
      .padding(.horizontal, 24).padding(.top, 16)
    }
  }

  // 3×2 slots with the save's own thumbnail; empty ones dashed (inert for Load, selectable for New Game). Focus starts on
  // the first slot that can be chosen, else on Back (as Desktop).
  func hp2SlotsGrid(_ L: @escaping (String) -> String) -> some View {
    let load = b.hp2Page == "LoadGame"
    let first = b.hp2Slots.first { $0.used || !load }?.n
    return LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: 3), spacing: 12) {
      ForEach(b.hp2Slots, id: \.n) { s in
        // The card (author 2026-11-16, both launchers): the save's thumbnail fills it (aspect-fill, cropped, clipped to the
        // rounded corners) and the label sits on it at the bottom over a dark scrim (text >= 4.5:1 on any picture).
        Button { b.hp2Click(s) } label: {
          ZStack(alignment: .bottomLeading) {
            if s.used, let t = s.thumb, let h = Hp2FrontEnd.Files(Launcher.hp2SaveDir).host(t), let img = NSImage(contentsOfFile: h) {
              Image(nsImage: img).resizable().aspectRatio(contentMode: .fill).frame(maxWidth: .infinity, maxHeight: .infinity).clipped()
            } else if !s.used {
              Text(L("IDC_SlotEmpty")).font(.system(size: 13)).foregroundStyle(p.dim).frame(maxWidth: .infinity, maxHeight: .infinity).padding(.bottom, 24)
            }   // used, no readable thumbnail: the label alone, as the original's empty bitmap
            if s.used {
              LinearGradient(stops: [.init(color: .black.opacity(0), location: 0), .init(color: .black.opacity(0.8), location: 0.55), .init(color: .black.opacity(0.85), location: 1)],
                             startPoint: .top, endPoint: .bottom).frame(height: 64)
            }
            Text(Hp2FrontEnd.label(s, empty: L("IDC_SlotEmpty"), used: L("IDC_SlotUsed")))
              .font(.system(size: 13, weight: .semibold)).foregroundStyle(s.used ? p.text : p.dim).padding(.horizontal, 10).padding(.bottom, 9)
          }
          .frame(maxWidth: .infinity).frame(height: 132)
          .background(RoundedRectangle(cornerRadius: 12).fill(s.used ? p.surface : .clear))
          .clipShape(RoundedRectangle(cornerRadius: 12))
          .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(p.stroke, style: StrokeStyle(lineWidth: 1, dash: s.used ? [] : [5, 4])))
          .contentShape(Rectangle())
        }
        .buttonStyle(FlatButtonStyle(radius: 12)).padTarget("slot\(s.n)-\(s.used)", primary: s.n == first, radius: 12) { b.hp2Click(s) }
        .disabled(b.playing || (load && !s.used))
      }
    }
  }
}

// A Launcher in a fixed state for the offscreen checks (no disk is read for it).
@MainActor func stateLauncher(_ hp2Root: String?) -> Launcher {
  let b = Launcher()
  b.installed = true; b.edition = "UK 1.1: exact"; b.newer = nil; b.error = nil; b.warning = nil; b.inputs = []; b.step = -1
  b.mods = []; b.modsNotice = []; b.languageList = []; b.language = nil; b.startMaps = []; b.hp2RootPath = hp2Root
  b.hp2Slots = (1...6).map { Hp2FrontEnd.Slot(n: $0, dir: "Slot\($0)", used: $0 <= 2, thumb: $0 <= 2 ? "Slot\($0)\\Save0.bmp" : nil) }
  return b
}
// The window's view, laid out offscreen (no window): the pad targets it registers reach the Launcher (onPreferenceChange).
@MainActor var offscreenWindows: [NSWindow] = []
@MainActor func offscreen(_ b: Launcher) -> NSHostingView<MainView> {
  let v = NSHostingView(rootView: MainView(b: b, live: false))
  v.appearance = NSAppearance(named: .darkAqua)
  v.frame = NSRect(x: 0, y: 0, width: 600, height: 560)
  // In a borderless window that is never ordered on screen: a ScrollView scrolls (ScrollViewReader) only inside a window.
  let w = NSWindow(contentRect: v.frame, styleMask: [.borderless], backing: .buffered, defer: true)
  w.isReleasedWhenClosed = false; w.contentView = v; offscreenWindows.append(w)
  settle(v)
  return v
}
@MainActor func settle(_ v: NSView) {
  for _ in 0..<4 { v.layoutSubtreeIfNeeded(); RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
}

// The pad on the window's view, offscreen, through the same Launcher.pad the poller calls (Launcher --pad-check): which
// target has focus after each press. No game starts and no folder is touched (the presses avoid Play and the tabs).
@MainActor func padCheck() {
  _ = NSApplication.shared
  // the release switch off: the HP2 tab is no target (no pad RB, no click); then the preview build for the rest
  Hp2Release.available = false
  do { let b = stateLauncher(nil); _ = offscreen(b); b.pad(PadMap.rb); print("hp2_off: tab2_target=\(b.target("tab2") == nil ? 0 : 1) hp2Tab=\(b.hp2Tab ? 1 : 0)") }
  Hp2Release.available = true
  let b = stateLauncher("/nonexistent-hp2")
  b.mods = [Launcher.Mod(name: "Alpha", enabled: true, missing: false), Launcher.Mod(name: "Beta", enabled: false, missing: false)]
  let v = offscreen(b)
  func say(_ what: String) { print("\(what): focus=\(b.padFocus ?? "-") ring=\(b.padUsed ? 1 : 0) mods=\(b.modsOpen ? 1 : 0) popup=\(b.popup == nil ? 0 : 1) page=\(b.hp2Page)") }
  func press(_ n: String, _ button: Int) { b.pad(button); settle(v); say(n) }
  say("start")
  press("up", PadMap.up); press("down", PadMap.down); press("down", PadMap.down)
  press("a_on_mods", PadMap.a); press("right", PadMap.right); press("b", PadMap.b)
  press("start", PadMap.start); press("down", PadMap.down); press("b", PadMap.b)
  b.hp2Tab = true; b.hp2Page = "Main"; settle(v); say("hp2")
  press("y_on_hp2", PadMap.y); press("a_on_load", PadMap.a); press("right", PadMap.right); press("b", PadMap.b)
  b.hp2RootPath = nil; b.edition = nil; settle(v); say("hp2_setup")
  b.inputs = [URL(fileURLWithPath: "/Volumes/HP2")]; settle(v); say("hp2_files")
  b.running = true; b.step = 3; settle(v); press("lb_while_installing", PadMap.lb)
  print("installing_tab: hp2Tab=\(b.hp2Tab ? 1 : 0)")   // LB does not switch the game while a setup runs
  b.running = false; b.step = -1; b.inputs = []; b.hp2RootPath = "/nonexistent-hp2"
  b.hp2Tab = false; b.error = "e1"; b.hp2Error = "e2"; settle(v)
  let had = b.target("dismiss") != nil
  b.padFocus = "dismiss"; b.pad(PadMap.b); settle(v)
  print("banner_b: dismiss_target=\(had ? 1 : 0) hp1=\(b.error ?? "-") hp2=\(b.hp2Error ?? "-") dismiss_after=\(b.target("dismiss") == nil ? 0 : 1)")
  print("gamecontroller: \(GCController.controllers().count) controller(s), read \(Launcher.readPad().map { "\($0)" } ?? "none")")
}

// Offscreen pictures of the window's states (no window is opened): Launcher --render-states <out dir> [HP2 root].
@MainActor func renderStates(to dir: String, hp2Root: String?) {
  // Synthetic thumbnails (plain gradients, no game image) for the two used slots, in a temporary save folder.
  let thumbs = FileManager.default.temporaryDirectory.appendingPathComponent("hpengine-render-\(UUID().uuidString)")
  defer { try? FileManager.default.removeItem(at: thumbs) }
  for n in 1...2 {
    let img = NSImage(size: NSSize(width: 128, height: 96), flipped: false) { r in
      NSGradient(colors: [NSColor(red: 0.54, green: 0.44, blue: 0.33, alpha: 1), NSColor(red: 0.17, green: 0.23, blue: 0.15, alpha: 1), NSColor(red: 0.08, green: 0.02, blue: 0.04, alpha: 1)])?.draw(in: r, angle: -35)
      return true
    }
    let d = thumbs.appendingPathComponent("Slot\(n)")
    try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
    if let t = img.tiffRepresentation, let png = NSBitmapImageRep(data: t)?.representation(using: .bmp, properties: [:]) { try? png.write(to: d.appendingPathComponent("Save0.bmp")) }
  }
  Launcher.renderSaveDir = thumbs.path
  _ = NSApplication.shared
  NSApp.appearance = NSAppearance(named: .darkAqua)
  registerTitleFont()
  func shot(_ name: String, modsOpen: Bool = false, _ set: (Launcher) -> Void, then: ((Launcher) -> Void)? = nil) {
    Hp2Release.available = name.hasPrefix("hp2")   // the HP1 shots as released (HP2 "Coming soon"), the HP2 ones as a preview build
    let b = stateLauncher(hp2Root)
    if name.hasPrefix("hp2") { b.edition = nil }   // the HP2 tab: an HP2 edition only where a shot sets one
    b.modsOpen = modsOpen
    set(b)
    let v = offscreen(b)
    if let then { then(b); settle(v) }
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1200, pixelsHigh: 1120, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = v.bounds.size
    v.cacheDisplay(in: v.bounds, to: rep)
    let out = URL(fileURLWithPath: dir).appendingPathComponent("mac_\(name).png")
    do { try rep.representation(using: .png, properties: [:])!.write(to: out); print(out.path) } catch { print("FAIL \(out.path): \(error)") }
  }
  let mods = [Launcher.Mod(name: "HD Textures", enabled: true, missing: false), Launcher.Mod(name: "Map Pack", enabled: true, missing: false),
              Launcher.Mod(name: "Classic HUD", enabled: false, missing: false), Launcher.Mod(name: "Fast Spells", enabled: false, missing: false),
              Launcher.Mod(name: "Widescreen", enabled: false, missing: false)]
  shot("hp1_ready_update") { b in
    b.newer = (version: "0.4.1", page: URL(string: "https://example.invalid/release")!, zip: nil, sig: nil); b.mods = mods
  }
  shot("hp1_multilingual_error") { b in
    b.edition = "UK 1.1: modified"
    b.languageList = ["dan": "Danish", "eng": "English", "fin": "Finnish", "nor": "Norwegian", "swe": "Swedish"].sorted { $0.value < $1.value }
      .map { Launcher.LanguageChoice(code: $0.key, label: $0.value, folder: $0.key == "eng" ? nil : "1") }
    b.mods = mods; b.startMaps = [Launcher.StartMap(mod: "Map Pack", map: "Hogsmeade")]
    b.modsNotice = ["Slot 2 was saved with other mods: loading it restarts its level with the progress you had when you entered it."]
    b.error = Launcher.errorBanner(["[Crash] Missing package \"HP_Menu\"."], code: 1)
    b.runError = b.error
  }
  shot("hp1_crowded_scrolls") { b in
    b.newer = (version: "0.4.1", page: URL(string: "https://example.invalid/release")!, zip: nil, sig: nil)
    b.edition = "UK 1.1: modified"
    b.languageList = [Launcher.LanguageChoice(code: "eng", label: "English", folder: nil), Launcher.LanguageChoice(code: "rus", label: "Russian", folder: "1")]
    b.mods = mods; b.startMaps = [Launcher.StartMap(mod: "Map Pack", map: "Hogsmeade")]
    b.warning = Launcher.overrideWarning(["[Overrides] WARNING: FEOptionsPage.IsSupportedResolution is not in the loaded scripts: the port's override of it does not apply"])
    b.error = Launcher.errorBanner(["[Crash] Missing package \"HP_Menu\"."], code: 1)
    b.runError = b.error
  }
  shot("hp1_mods", modsOpen: true) { b in
    b.mods = [Launcher.Mod(name: "Old Mod", enabled: true, missing: true)] + mods
    b.modsNotice = ["Slot 2 was saved with other mods: loading it restarts its level with the progress you had when you entered it.",
                    "Map Pack and HD Textures both have Textures/Hogwarts.utx: the game uses Map Pack's (later in the order)"]
  }
  shot("hp1_no_game") { b in b.installed = false; b.edition = nil }
  shot("hp1_files_chosen") { b in
    b.installed = false; b.edition = nil
    b.inputs = [URL(fileURLWithPath: "/Volumes/HARRY_POTTER"), URL(fileURLWithPath: "/Volumes/USB/HP1_Setup.exe")]
  }
  shot("hp1_installing") { b in
    b.installed = false; b.running = true; b.step = 3; b.progress = 0.64; b.inputs = [URL(fileURLWithPath: "/Volumes/HARRY_POTTER")]
  }
  shot("hp1_install_failed") { b in
    b.installed = false; b.edition = nil; b.step = 2; b.inputs = [URL(fileURLWithPath: "/Volumes/HARRY_POTTER")]
    b.error = "Some game files are missing or damaged: Maps/Hogwarts_Grounds.unr."
  }
  // The game-error banner (author 2026-11-17): plain text, the cause on its own line, Open log, the × (pad focus on it)
  let crash = ["[Crash] unhandled exception: System.IO.InvalidDataException: package HP_Menu: bad header"]
  shot("error_banner_hp1_crash") { b in b.mods = mods; b.error = Launcher.errorBanner(crash, code: 134); b.runError = b.error }
  shot("error_banner_hp1_focus", { b in b.mods = mods; b.padConnected = true; b.error = Launcher.errorBanner(crash, code: 134); b.runError = b.error },
       then: { b in b.padFocus = "dismiss"; b.padUsed = true })
  shot("hp2_error_banner_no_map") { b in
    b.hp2Tab = true; b.hp2Error = Launcher.errorBanner(["[Map] there is no map \"nosuch\": no Maps/nosuch.unr in the game or in an enabled mod"], code: 6); b.hp2RunError = b.hp2Error
  }
  shot("hp2_main") { b in b.hp2Tab = true }
  shot("hp2_main_no_saves") { b in b.hp2Tab = true; b.hp2Slots = b.hp2Slots.map { Hp2FrontEnd.Slot(n: $0.n, dir: $0.dir, used: false, thumb: nil) } }
  shot("hp2_load_game") { b in b.hp2Tab = true; b.hp2Page = "LoadGame" }
  shot("hp2_no_folder") { b in b.hp2Tab = true; b.hp2RootPath = nil; b.edition = nil }
  shot("hp2_installing") { b in b.hp2Tab = true; b.hp2RootPath = nil; b.running = true; b.step = 3; b.progress = 0.64; b.edition = "EN: supported"; b.inputs = [URL(fileURLWithPath: "/Volumes/HP2")] }
  shot("hp2_main_edition") { b in b.hp2Tab = true; b.edition = "RU EA: supported" }
  // the Menu open by the pad's Start (a pad connected: the hints in the footer); HP2 Load Game, focus moved by the d-pad
  shot("hp1_menu", { b in b.padConnected = true }, then: { b in b.pad(PadMap.start) })
  shot("hp2_load_pad", { b in b.hp2Tab = true; b.hp2Page = "LoadGame"; b.padConnected = true }, then: { b in b.pad(PadMap.right) })
  // Interaction states (no pointer offscreen: forcedLooks gives a target the hover / pressed look): hover, press, pad
  // focus, and each list open at its control with an item under the pointer.
  let multi = [Launcher.LanguageChoice(code: "eng", label: "English", folder: nil), Launcher.LanguageChoice(code: "rus", label: "Russian", folder: "1"),
               Launcher.LanguageChoice(code: "swe", label: "Swedish", folder: "2")]
  shot("polish_hover") { b in b.mods = mods; b.forcedLooks = ["play": 1, "menu": 1, "mods": 1] }
  shot("polish_pressed") { b in b.mods = mods; b.forcedLooks = ["play": 2, "mods": 2] }
  shot("polish_focus", { b in b.mods = mods; b.padConnected = true }, then: { b in b.pad(PadMap.down) })
  shot("polish_menu_open", { b in b.forcedLooks = ["pop2": 1] }, then: { b in b.target("menu")?.run() })
  shot("polish_language_open", { b in b.languageList = multi; b.mods = mods; b.forcedLooks = ["pop2": 1] }, then: { b in b.target("language")?.run() })
  shot("polish_startmap_open", { b in b.mods = mods; b.startMaps = [Launcher.StartMap(mod: "Map Pack", map: "Hogsmeade"), Launcher.StartMap(mod: "Map Pack", map: "Diagon")]; b.forcedLooks = ["pop1": 2] },
       then: { b in b.target("startmap")?.run() })
  shot("polish_startmap_scrolled", { b in b.startMaps = (1...30).map { Launcher.StartMap(mod: "Pack", map: String(format: "Map%02d", $0)) }; b.padConnected = true },
       then: { b in b.target("startmap")?.run(); for _ in 0..<15 { b.pad(PadMap.down) } })
  shot("polish_playing") { b in b.playing = true }
  shot("polish_installing") { b in b.installed = false; b.running = true; b.step = 3; b.progress = 0.64; b.inputs = [URL(fileURLWithPath: "/Volumes/HARRY_POTTER")] }
}

// A long list offscreen (Launcher --popup-check): the Start map list of 30 maps opens at its choice, End / Home / a page /
// type-ahead / the d-pad move the focus, and its scroller follows: where it stands (top, mid, end) after each step.
@MainActor func popupCheck() {
  _ = NSApplication.shared
  Hp2Release.available = false
  let b = stateLauncher(nil)
  b.startMaps = (1...30).map { Launcher.StartMap(mod: "Pack", map: String(format: "Map%02d", $0)) }
  let v = offscreen(b)
  // ordered in, far off every screen and never key: real mouse and wheel events reach the list only in an ordered window
  v.window?.setFrameOrigin(NSPoint(x: -30000, y: -30000)); v.window?.orderBack(nil); settle(v)
  func scrollers(_ v: NSView) -> [NSScrollView] { ((v as? NSScrollView).map { [$0] } ?? []) + v.subviews.flatMap(scrollers) }
  func line(_ step: String) {
    settle(v)
    // the list's scroller: the one whose content is taller than it
    // the list's scroller (the second: the window's own page is the first); its offset, and whether the last item is in view
    let sv = scrollers(v).dropFirst().first, total = CGFloat(b.popup?.items.count ?? 0) * 40 + 4   // the last item's bottom (under the 4 pt inset)
    let at = sv.map { "\(Int($0.contentView.bounds.minY))+\(Int($0.contentView.bounds.height)) last_in_view=\($0.contentView.bounds.maxY >= total - 1 ? 1 : 0)" } ?? "-"
    let title = b.padFocus.flatMap { Int($0.dropFirst(3)) }.flatMap { i in b.popup.map { $0.items[i].title } } ?? "-"
    // the indicator's offset (what it is drawn from) against the list's: 1 when it stands where the list is
    let ind = sv.map { Int(b.listOffset) == Int($0.contentView.bounds.minY) ? "1" : "0(\(Int(b.listOffset)))" } ?? "-"
    print("\(step): focus=\(title) popup=\(b.popup == nil ? 0 : 1) scroll=\(at.isEmpty ? "-" : at) ind=\(ind)")
  }
  b.target("startmap")?.run(); line("open")
  b.moveTo(Int.max); line("end")
  b.moveTo(0); line("home")
  b.moveBy(Launcher.page); line("page_down")
  b.typeAhead("m"); line("type_m")
  for _ in 0..<15 { b.pad(PadMap.down) }; line("pad_down_15")
  // the indicator dragged: to the middle, the end, the top (the same Launcher.dragList the gesture calls)
  for (name, f) in [("drag_mid", 0.5), ("drag_end", 1.0), ("drag_top", 0.0)] as [(String, CGFloat)] {
    b.dragList(f); line(name)
  }
  // a real mouse drag on the indicator's track (the window's event path into the SwiftUI gesture): from its top to its bottom
  if let sv = scrollers(v).dropFirst().first, let w = v.window {
    let r = sv.convert(sv.bounds, to: nil), x = r.maxX - 5
    func mouse(_ t: NSEvent.EventType, _ y: CGFloat) {
      w.sendEvent(NSEvent.mouseEvent(with: t, location: NSPoint(x: x, y: y), modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                     windowNumber: w.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1)!)
      settle(v)
    }
    mouse(.leftMouseDown, r.maxY - 2); for k in 1...8 { mouse(.leftMouseDragged, r.maxY - 2 - CGFloat(k) * (r.height - 4) / 8) }
    mouse(.leftMouseUp, r.minY + 2); line("mouse_drag_down")
  }
  b.pad(PadMap.a); line("pad_a")
  print("chosen=\(b.startMap ?? "-")")
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
    case "--remove-game" where ["keep", "all"].contains(a.count > 3 ? a[3] : "") && (a.count == 4 || (a.count == 5 && a[4] == "fail-after-marker")):
      // Deletes (no Trash) so launcher/test_update.sh can check the result; "fail-after-marker" simulates a failure after the marker.
      struct Simulated: Error {}
      do {
        try Launcher.removeGame(root: URL(fileURLWithPath: a[2]), all: a[3] == "all", dispose: { try FileManager.default.removeItem(at: $0) },
                                afterMarker: { if a.count == 5 { throw Simulated() } })
        print("OK"); exit(0)
      } catch { print("FAIL \(error)"); exit(1) }
    case "--mods-ini" where a.count >= 3:
      // No names: the enabled mods of the file, "|"-separated; with names: the file's text with exactly these enabled (launcher/test_update.sh).
      guard let s = try? String(contentsOfFile: a[2], encoding: .utf8) else { print("FAIL no file"); exit(1) }
      if a.count == 3 { print(Launcher.enabledMods(ini: s).joined(separator: "|")) }
      else { print(Launcher.withEnabledMods(ini: s, Array(a[3...])), terminator: "") }
      exit(0)
    case "--mods-notice" where a.count == 3:
      // <game dir>: the notice lines for its Save/save0..5 and Mods.ini, one per output line (launcher/test_update.sh).
      let g = URL(fileURLWithPath: a[2])
      let on = Launcher.enabledMods(ini: (try? String(contentsOf: Launcher.modsIniURL(g), encoding: .utf8)) ?? "")
      for l in Launcher.noticeLines(enabled: on, slots: Launcher.slotMods(game: g)) { print(l) }
      exit(0)
    case "--slot-mods" where a.count == 3:
      // <save file>: its mod record ("legacy", "unknown", or the names "|"-separated, "-" for none).
      guard let d = FileManager.default.contents(atPath: a[2]) else { print("unknown"); exit(0) }
      if let m = Launcher.readSlot([UInt8](d)) { print(m.legacy ? "legacy" : (m.mods.isEmpty ? "-" : m.mods.joined(separator: "|"))) } else { print("unknown") }
      exit(0)
    case "--mods-check" where a.count == 3:
      // <game dir>: why Play is blocked (an enabled mod without its folder), then the misplaced-files lines (launcher/test_update.sh).
      let g = URL(fileURLWithPath: a[2]); let mods = Launcher.modList(game: g)
      if let t = Launcher.missingText(mods) { print(t) }
      for l in Launcher.misplacedLines(game: g, mods: mods) { print(l) }
      exit(0)
    case "--start-maps" where a.count == 3 || a.count == 4:
      // <game dir> [chosen map]: the "Start map" entries after "Game" as "<map>@<mod>=<label>", "|"-separated, then the
      // engine arguments of Play for that choice (none: the normal start), one line (launcher/test_update.sh).
      let g = URL(fileURLWithPath: a[2]); let maps = Launcher.addedMaps(game: g)
      print(zip(maps, Launcher.startMapLabels(maps)).map { "\($0.map)@\($0.mod)=\($1)" }.joined(separator: "|"))
      print(Launcher.playArguments(game: URL(fileURLWithPath: "/g"), map: Launcher.keptStartMap(a.count == 4 ? a[3] : nil, maps)).joined(separator: " "))
      exit(0)
    case "--newer-package" where a.count == 4:
      // <mod dir> <max version>: newerPackage, or "-" (launcher/test_update.sh, against ModOverlay.NewerPackage).
      print(Launcher.newerPackage(modDir: URL(fileURLWithPath: a[2]), max: Int(a[3]) ?? 0) ?? "-"); exit(0)
    case "--add-mod" where a.count == 5:
      // <HP1Setup> <install> <source>: the Mods page's Add (runAddMod), its error or "OK" (launcher/test_update.sh).
      print(Launcher.runAddMod(tool: URL(fileURLWithPath: a[2]), install: a[3], source: a[4]) ?? "OK"); exit(0)
    case "--mods-order" where a.count == 3 || a.count == 4:
      // <game dir> [Mods.ini]: each listed mod as "<name>=<order number or ->", "|"-separated (launcher/test_update.sh);
      // the Mods.ini given: HP2's layout (modsIni).
      let mods = Launcher.modList(game: URL(fileURLWithPath: a[2]), ini: a.count == 4 ? URL(fileURLWithPath: a[3]) : nil)
      print(mods.map { "\($0.name)=" + (Launcher.orderNumber(mods, $0).map(String.init) ?? "-") }.joined(separator: "|")); exit(0)
    case "--mods-conflicts" where a.count == 3 || a.count == 4:
      // <game dir> [Mods.ini]: the conflict lines of the Mods card, one per output line (launcher/test_update.sh).
      for l in Launcher.conflictLines(game: URL(fileURLWithPath: a[2]), ini: a.count == 4 ? URL(fileURLWithPath: a[3]) : nil) { print(l) }
      exit(0)
    case "--language-list" where a.count == 3:
      // <game dir>: "<code>|<label>|<folder or ->" per language, then "selected=<code or ->" (launcher/test_update.sh, against
      // the same mode of launcher/Desktop.Tests, which runs GameLanguage.cs).
      let g = URL(fileURLWithPath: a[2])
      for l in Launcher.languages(game: g) { print("\(l.code)|\(l.label)|\(l.folder ?? "-")") }
      print("selected=" + (Launcher.selectedLanguage(game: g) ?? "-")); exit(0)
    case "--language-select" where a.count == 4:
      // <game dir> <code|default>: the chooser's write; "OK", or "FAIL <text>" and exit 1.
      do { try Launcher.saveLanguage(game: URL(fileURLWithPath: a[2]), a[3] == "default" ? nil : a[3]); print("OK"); exit(0) }
      catch let e as Launcher.LanguageError { print("FAIL " + e.text); exit(1) }
      catch { print("FAIL \(error)"); exit(1) }
    case "--edition-label" where a.count == 4:
      // <edition text> <game dir>: the Game card's "name|detail" with that game's languages.
      let d = Launcher.editionDisplay(a[2], Launcher.languages(game: URL(fileURLWithPath: a[3])))
      print("\(d.name)|\(d.detail ?? "-")"); exit(0)
    case "--pad-reset" where a.count == 3 || (a.count == 4 && a[3] == "reset"):
      // <game dir> [reset]: how many lines "Reset Gamepad Bindings" removes; with "reset" it removes them (launcher/test_update.sh).
      print(Launcher.padRebound(game: URL(fileURLWithPath: a[2]), reset: a.count == 4)); exit(0)
    case "--engine-error" where a.count == 4:
      // <exit code> <engine console output file>: the error banner (launcher/test_update.sh).
      guard let code = Int32(a[2]), let s = try? String(contentsOfFile: a[3], encoding: .utf8) else { print("FAIL"); exit(1) }
      print(Launcher.errorBanner(s.components(separatedBy: "\n"), code: code)); exit(0)
    case "--game-title" where a.count == 2 || a.count == 3:
      // [<edition text>]: the header title (launcher/test_update.sh); no argument: no game identified.
      print(Launcher.gameTitle(a.count == 3 ? a[2] : nil)); exit(0)
    case "--hp2-frontend" where a.count == 4:
      // <cases> <answers>: tools/hp2/frontend_judge.py's cases answered as launcher/Desktop.Tests --hp2-frontend (test_update.sh).
      guard let s = try? String(contentsOfFile: a[2], encoding: .utf8) else { print("FAIL no cases"); exit(1) }
      let out = s.split(separator: "\n", omittingEmptySubsequences: true).map { l in String(l) + "\t=\t" + Hp2FrontEnd.answer(String(l)) }
      do { try (out.joined(separator: "\n") + "\n").write(toFile: a[3], atomically: true, encoding: .utf8); exit(0) } catch { print("FAIL \(error)"); exit(1) }
    case "--hp2-menu" where a.count == 4 || (a.count == 7 && ["new", "load"].contains(a[4])):
      // <HP1 data dir> <HP2 root> [new|load <slot> <yes 0|1>]: the HP2 tab's menu on real folders (Hp2FrontEnd.menuReport).
      let click = a.count == 7 ? (a[4] == "new" ? Hp2FrontEnd.Launch.newGame : .loadGame, Int(a[5]) ?? 1, a[6] == "1") : nil
      for l in Hp2FrontEnd.menuReport(hp1Data: a[2], root: a[3], click: click) { print(l) }
      exit(0)
    case "--override-warning" where a.count == 3:
      // <log file>: the warning banner the launcher shows after a run (launcher/test_update.sh compares with Desktop.Tests).
      print(Launcher.overrideWarning(((try? String(contentsOfFile: a[2], encoding: .utf8)) ?? "").components(separatedBy: "\n")) ?? "-"); exit(0)
    case "--pad-map" where a.count == 2:
      // The gamepad table, as launcher/Desktop.Tests --pad-map prints it from Logic.cs PadMap (launcher/test_update.sh compares).
      for l in PadMap.table() { print(l) }
      exit(0)
    case "--game-scope" where a.count == 3:
      // <HP1 game dir>: the per-game answers, as launcher/Desktop.Tests --game-scope prints them from Logic.cs GameScope.
      for l in GameScope.table(a[2]) { print(l) }
      exit(0)
    case "--setup-args" where a.count >= 3 && ["0", "1"].contains(a[2]):
      // <hp2 0|1> <inputs...>: HP1Setup's arguments as the launcher passes them (launcher/test_update.sh)
      for x in Launcher.setupArguments(hp2: a[2] == "1", inputs: Array(a[3...])) { print(x) }
      exit(0)
    case "--pad-check" where a.count == 2:
      MainActor.assumeIsolated { padCheck() }; exit(0)
    case "--popup-check" where a.count == 2:
      MainActor.assumeIsolated { popupCheck() }; exit(0)
    case "--render-states" where a.count == 3 || a.count == 4:
      // <out dir> [HP2 root]: offscreen pictures of the window's states, mac_<state>.png (no window is opened).
      MainActor.assumeIsolated { renderStates(to: a[2], hp2Root: a.count == 4 ? a[3] : nil) }; exit(0)
    case "--runtime-path" where a.count == 2:
      print(Launcher.runtimeURL.path); exit(0)
    default:
      FileHandle.standardError.write(Data("usage: Launcher --verify-update <zip> <sig> <key> <version> | --pick-release <json> <version> | --mods-ini <file> [names...] | --mods-notice <game dir> | --mods-check <game dir> | --start-maps <game dir> [map] | --mods-order <game dir> | --mods-conflicts <game dir> | --language-list <game dir> | --language-select <game dir> <code|default> | --edition-label <edition> <game dir> | --engine-error <code> <file> | --slot-mods <save file> | --game-title [edition] | --remove-game <root> <keep|all> [fail-after-marker] | --hp2-frontend <cases> <answers> | --hp2-menu <hp1 data> <hp2 root> [new|load <slot> <0|1>] | --render-states <out dir> [hp2 root] | --setup-args <0|1> [inputs...] | --pad-map | --pad-check | --game-scope <game dir> | --runtime-path\n".utf8))
      exit(2)
    }
  }
}

struct LauncherApp: App {
  @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
  @StateObject var b = Launcher()
  init() { registerTitleFont() }
  var body: some Scene {
    WindowGroup { MainView(b: b) }.windowResizability(.contentSize)
  }
}
