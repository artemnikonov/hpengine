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
  @Published var warning: String?  // HP1Setup's WARNING line (an unverified edition): shown, the install goes on
  @Published var running = false
  @Published var playing = false
  @Published var installed = Launcher.isInstalled()
  @Published var edition = Launcher.installedEdition()  // "US 1.53: exact" (HP1Setup's EDITION line, kept in the marker)

  static let home = NSHomeDirectory()
  static let dataURL = URL(fileURLWithPath: home + "/Library/Application Support/HPEngine")
  static let gameURL = dataURL.appendingPathComponent("game")  // the install root: game data, Save/, HPEngineData/ (settings, logs)
  static let savesURL = gameURL.appendingPathComponent("Save")
  static let settingsURL = gameURL.appendingPathComponent("HPEngineData")
  static let logsURL = settingsURL.appendingPathComponent("logs")
  static let launchLogURL = URL(fileURLWithPath: home + "/Library/Logs/HPEngine.log")
  // The app is universal: each slice of this binary runs the engine of its own architecture (make_launcher.sh puts
  // osx-arm64 in Resources/HPEngine and osx-x64 in Resources/HPEngine-x64; Launcher --runtime-path prints the choice).
  #if arch(x86_64)
  nonisolated static let runtimeName = "HPEngine-x64"
  #else
  nonisolated static let runtimeName = "HPEngine"
  #endif
  nonisolated static var runtimeURL: URL { Bundle.main.resourceURL!.appendingPathComponent(runtimeName) }
  // HP1Setup writes game/.hp1-installed last: an interrupted copy does not count.
  static func isInstalled() -> Bool { FileManager.default.fileExists(atPath: gameURL.path + "/.hp1-installed") }
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
  // HP1Setup and HPEngine. Clear it on the whole bundle (not possible while macOS runs the app from a translocated copy).
  func unquarantine() -> Bool {
    if Bundle.main.bundlePath.contains("/AppTranslocation/") {
      error = "Move HPEngine Launcher to the Applications folder, open it from there and try again."
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
    case "DONE": step = steps.count; installed = true; inputs = []
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
          if status != 0 {
            let said = ((try? String(contentsOf: Launcher.launchLogURL, encoding: .utf8)) ?? "").components(separatedBy: "\n")
            self.error = Launcher.errorBanner(said, code: status, log: "More → Open Logs.")
          }
        }
      }
    }
    do { try p.run(); playing = true; error = nil } catch { self.error = error.localizedDescription; return }
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
  nonisolated static func errorBanner(_ lines: [String], code: Int32, log: String) -> String {
    "The game stopped with an error (code \(code))" + (errorLine(lines, code: code).map { ": " + $0.trimmingCharacters(in: .whitespaces) } ?? ".") + " See the log: " + log
  }

  func resetSettings() {
    let a = NSAlert()
    a.messageText = "Reset settings?"
    a.informativeText = "Video, sound and control settings go back to the game's defaults. Saved games are kept."
    a.addButton(withTitle: "Reset"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    for n in ["Default.ini", "DefUser.ini"] {  // the overlay HPEngine writes; the game's own ini files are never changed
      let u = Launcher.settingsURL.appendingPathComponent(n)
      if FileManager.default.fileExists(atPath: u.path) { try? FileManager.default.trashItem(at: u, resultingItemURL: nil) }
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
  // Lines a reset would remove (0 = nothing was rebound: the menu item is off). reset: also write the files back.
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
    guard installed, !running, !playing else { return }
    let a = NSAlert()
    a.messageText = "Remove game?"
    a.informativeText = "The installed game copy in \(Launcher.gameURL.path) is moved to the Trash. Saved games, settings and mods are kept. Your own game files are not touched."
    let cb = NSButton(checkboxWithTitle: "Also delete saved games, settings and mods", target: nil, action: nil)
    cb.sizeToFit(); a.accessoryView = cb
    a.addButton(withTitle: "Remove"); a.addButton(withTitle: "Cancel")
    guard a.runModal() == .alertFirstButtonReturn else { return }
    do {
      try Launcher.removeGame(root: Launcher.gameURL, all: cb.state == .on) { try FileManager.default.trashItem(at: $0, resultingItemURL: nil) }
      installed = Launcher.isInstalled(); edition = nil; step = -1; error = nil; warning = nil
    } catch {
      installed = Launcher.isInstalled()
      self.error = "Could not remove the game: \(error.localizedDescription)"
    }
  }

  func open(_ u: URL) {
    try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
    NSWorkspace.shared.open(u)
  }

  // ── Mods (hpengine/packaging/ModOverlay.cs): folders in game/Mods; game/HPEngineData/Mods.ini [Mods] Enabled=<name>
  // lines in order (a later mod wins). The list: enabled mods in that order, then the other folders by name.
  struct Mod: Identifiable, Equatable { let name: String; var enabled: Bool; var missing: Bool; var id: String { name } }
  static let modsURL = gameURL.appendingPathComponent("Mods")
  static let modsIniURL = settingsURL.appendingPathComponent("Mods.ini")
  nonisolated static func modsIniURL(_ game: URL) -> URL { game.appendingPathComponent("HPEngineData/Mods.ini") }
  @Published var mods: [Mod] = []

  nonisolated static func subfolders(_ dir: URL) -> [String] {
    ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey])) ?? [])
      .filter { !$0.lastPathComponent.hasPrefix(".") && ((try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false) }
      .map(\.lastPathComponent).sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
  }

  nonisolated static func modList(game: URL) -> [Mod] {
    let folders = subfolders(game.appendingPathComponent("Mods"))
    let on = enabledMods(ini: (try? String(contentsOf: modsIniURL(game), encoding: .utf8)) ?? "")
    return on.map { n in Mod(name: folders.first { $0.caseInsensitiveCompare(n) == .orderedSame } ?? n, enabled: true,
                             missing: !folders.contains { $0.caseInsensitiveCompare(n) == .orderedSame }) }
      + folders.filter { f in !on.contains { $0.caseInsensitiveCompare(f) == .orderedSame } }.map { Mod(name: $0, enabled: false, missing: false) }
  }

  func loadMods() {
    mods = Launcher.modList(game: Launcher.gameURL)
    updateModsNotice()
    updateStartMaps()
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

  nonisolated static func addedMaps(game: URL) -> [StartMap] {
    let fm = FileManager.default
    func entries(_ dir: URL) -> [URL] { (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isDirectoryKey])) ?? [] }
    func isDir(_ u: URL) -> Bool { (try? u.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false }
    func child(_ dir: URL, _ name: String, dir wantDir: Bool) -> URL? {
      entries(dir).first { $0.lastPathComponent.caseInsensitiveCompare(name) == .orderedSame && isDir($0) == wantDir }
    }
    let mods = modList(game: game).filter(\.enabled)
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
    let shipped = Set(entries(game).filter { isDir($0) && $0.lastPathComponent.lowercased() == "maps" }.flatMap(entries)
      .filter { !isDir($0) }.map { $0.lastPathComponent.lowercased() })
    var maps: [String: StartMap] = [:]
    for m in mods {
      let dir = game.appendingPathComponent("Mods").appendingPathComponent(m.name)
      var files = 0, mine: [URL] = []
      for e in entries(dir) {
        if isDir(e) {
          for case let f as URL in fm.enumerator(at: e, includingPropertiesForKeys: [.isRegularFileKey]) ?? FileManager.DirectoryEnumerator()
          where ((try? f.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) ?? false) && !f.lastPathComponent.hasPrefix(".") {
            files += 1
            if e.lastPathComponent.lowercased() == "maps" && f.deletingLastPathComponent().standardizedFileURL == e.standardizedFileURL { mine.append(f) }
          }
        } else if !e.lastPathComponent.hasPrefix(".") {
          guard let flatDirs else { return [] }
          guard let to = flatDirs["." + e.pathExtension.lowercased()] else { continue }
          files += 1
          if to.lowercased() == "maps" { mine.append(e) }
        }
      }
      if files == 0 { return [] }
      for f in mine where f.pathExtension.lowercased() == "unr" && !shipped.contains(f.lastPathComponent.lowercased()) {
        maps[f.lastPathComponent.lowercased()] = StartMap(mod: m.name, map: f.deletingPathExtension().lastPathComponent)
      }
    }
    return maps.values.sorted { $0.map.uppercased() < $1.map.uppercased() }   // as StringComparer.OrdinalIgnoreCase
  }

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
    guard str() == "HP1SAV", let ver = i32(), (1...5).contains(ver) else { return nil }
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
    modsNotice = Launcher.noticeLines(enabled: mods.filter(\.enabled).map(\.name), slots: Launcher.slotMods(game: Launcher.gameURL))
      + Launcher.misplacedLines(game: Launcher.gameURL, mods: mods)
  }

  func saveMods() {
    let old = (try? String(contentsOf: Launcher.modsIniURL, encoding: .utf8)) ?? ""
    try? FileManager.default.createDirectory(at: Launcher.settingsURL, withIntermediateDirectories: true)
    do { try Launcher.withEnabledMods(ini: old, mods.filter(\.enabled).map(\.name)).write(to: Launcher.modsIniURL, atomically: true, encoding: .utf8) }
    catch { self.error = "Could not save the mod list: \(error.localizedDescription)" }
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

  func about() {
    NSApp.orderFrontStandardAboutPanel(options: [
      .applicationName: "HPEngine Launcher",
      .credits: NSAttributedString(string:
        "HPEngine runs Harry Potter and the Philosopher's Stone (PC, 2001) from your own copy of the game. "
        + "No game files are included. Game data: \(Launcher.gameURL.path)"),
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
      error = "This update can't be verified automatically. Download it from the release page."
      NSWorkspace.shared.open(n.page); return
    }
    let me = Bundle.main.bundleURL
    if me.path.contains("/AppTranslocation/") {
      error = "Move HPEngine Launcher to the Applications folder, open it from there and try again."; return
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

// ── Look (2026-11-12): dark warm background, content in cards, gold accent (the game's own gold), the burgundy header.
// The same design is used by the Windows/Linux Launcher (launcher/Desktop/MainWindow.cs).
enum Theme {
  static let bg = Color(red: 0.090, green: 0.078, blue: 0.098)        // #17141A
  static let card = Color(red: 0.133, green: 0.118, blue: 0.145)      // #221E25
  static let stroke = Color.white.opacity(0.08)
  static let gold = Color(red: 0.851, green: 0.663, blue: 0.333)      // #D9A955
  static let goldDark = Color(red: 0.745, green: 0.565, blue: 0.259)  // #BE9042
  static let text = Color(red: 0.933, green: 0.910, blue: 0.875)      // #EEE8DF
  static let dim = Color(red: 0.639, green: 0.612, blue: 0.576)       // #A39C93
  static let green = Color(red: 0.482, green: 0.769, blue: 0.498)     // #7BC47F
  static let amber = Color(red: 0.878, green: 0.647, blue: 0.290)     // #E0A54A
  static let red = Color(red: 0.902, green: 0.420, blue: 0.400)       // #E66B66
  static let header = LinearGradient(colors: [Color(red: 0.42, green: 0.10, blue: 0.10), Color(red: 0.12, green: 0.05, blue: 0.08)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing)
}

struct Card<Content: View>: View {
  var title: String? = nil
  @ViewBuilder var content: Content
  var body: some View {
    VStack(alignment: .leading, spacing: 10) {
      if let title { Text(title.uppercased()).font(.system(size: 11, weight: .semibold)).tracking(0.8).foregroundStyle(Theme.dim) }
      content
    }
    .frame(maxWidth: .infinity, alignment: .leading).padding(14)
    .background(RoundedRectangle(cornerRadius: 12).fill(Theme.card))
    .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.stroke))
  }
}

struct GoldButtonStyle: ButtonStyle {
  @Environment(\.isEnabled) var enabled
  func makeBody(configuration c: Configuration) -> some View {
    c.label.font(.system(size: 15, weight: .semibold)).foregroundStyle(Color(red: 0.16, green: 0.10, blue: 0.06))
      .padding(.horizontal, 22).frame(height: 40)
      .background(RoundedRectangle(cornerRadius: 10).fill(c.isPressed ? Theme.goldDark : Theme.gold))
      .opacity(enabled ? 1 : 0.45)
  }
}

struct QuietButtonStyle: ButtonStyle {
  @Environment(\.isEnabled) var enabled
  func makeBody(configuration c: Configuration) -> some View {
    c.label.font(.system(size: 13, weight: .medium)).foregroundStyle(Theme.text)
      .padding(.horizontal, 12).frame(height: 30)
      .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(c.isPressed ? 0.14 : 0.07)))
      .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Theme.stroke))
      .opacity(enabled ? 1 : 0.45)
  }
}

struct Banner: View {
  let icon: String, color: Color, text: String
  @State var open = false
  var body: some View {
    VStack(alignment: .leading, spacing: 6) {
      HStack(alignment: .firstTextBaseline, spacing: 8) {
        Image(systemName: icon).foregroundStyle(color)
        Text(text).lineLimit(open ? nil : 1).foregroundStyle(Theme.text).textSelection(.enabled)
          .fixedSize(horizontal: false, vertical: open)
        Spacer(minLength: 4)
        Button(open ? "Less" : "More") { open.toggle() }.buttonStyle(.plain).foregroundStyle(color).font(.system(size: 12, weight: .medium))
      }
    }
    .padding(.horizontal, 12).padding(.vertical, 9)
    .background(RoundedRectangle(cornerRadius: 10).fill(color.opacity(0.12)))
    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(color.opacity(0.35)))
  }
}

// "US 1.53: exact" (HP1Setup EDITION line) -> name and tier; exact = verified against, others shown as they are.
func editionChip(_ ed: String) -> some View {
  let parts = ed.components(separatedBy: ": ")
  let name = parts.dropLast().joined(separator: ": "), tier = parts.count > 1 ? parts.last! : ""
  let (color, label): (Color, String) = switch tier {
    case "exact": (Theme.green, "verified")
    case "supported": (Theme.gold, "supported")
    default: (Theme.amber, tier.isEmpty ? "unknown" : tier)
  }
  return HStack(spacing: 8) {
    Text(name.isEmpty ? ed : name).foregroundStyle(Theme.text).font(.system(size: 14, weight: .medium))
    Text(label).font(.system(size: 11, weight: .semibold)).foregroundStyle(color)
      .padding(.horizontal, 8).padding(.vertical, 3).background(Capsule().fill(color.opacity(0.15)))
  }
}

struct MainView: View {
  @StateObject var b = Launcher()
  @State var dropping = false
  var setup: Bool { !b.installed || b.running || !b.inputs.isEmpty }
  var status: (String, Color) {
    if b.running { return ("Installing…", Theme.gold) }
    if b.playing { return ("Playing", Theme.green) }
    return b.installed ? ("Ready to play", Theme.green) : ("Not installed", Theme.dim)
  }
  var version: String { (Bundle.main.object(forInfoDictionaryKey: "HPEngineVersion") as? String).map { " " + $0 } ?? "" }

  var body: some View {
    VStack(spacing: 0) {
      header
      if let n = b.newer { updateBar(n) }
      VStack(alignment: .leading, spacing: 12) {
        if setup { setupView } else { installedView }
        if let w = b.warning { Banner(icon: "exclamationmark.triangle.fill", color: Theme.amber, text: w) }
        if let e = b.error { Banner(icon: "xmark.octagon.fill", color: Theme.red, text: e) }
        footer.padding(.top, 6)
      }
      .padding(20)
    }
    .background(Theme.bg)
    .environment(\.colorScheme, .dark)
    .frame(width: 600).fixedSize(horizontal: false, vertical: true)
    .onAppear { b.checkForUpdate(); b.loadMods() }
    .onChange(of: b.installed) { _ in b.loadMods() }
    .onDrop(of: [.fileURL], isTargeted: $dropping) { items in
      for item in items {
        _ = item.loadObject(ofClass: URL.self) { url, _ in
          if let url { Task { @MainActor in b.add([url]) } }
        }
      }
      return true
    }
  }

  var header: some View {
    HStack(alignment: .bottom) {
      VStack(alignment: .leading, spacing: 6) {
        Text("Harry Potter and the Philosopher's Stone").font(.system(size: 24, weight: .bold, design: .serif)).foregroundStyle(.white)
        HStack(spacing: 8) {
          Text("HPEngine\(version)").foregroundStyle(.white.opacity(0.7))
          Circle().fill(status.1).frame(width: 7, height: 7)
          Text(status.0).foregroundStyle(.white.opacity(0.85))
        }.font(.system(size: 13))
      }
      Spacer()
    }
    .padding(.horizontal, 22).padding(.top, 26).padding(.bottom, 18)
    .background(Theme.header)
  }

  func updateBar(_ n: Launcher.Release) -> some View {
    HStack(spacing: 10) {
      Image(systemName: "arrow.down.circle.fill").foregroundStyle(Theme.gold)
      Text("HPEngine \(n.version) is available").foregroundStyle(Theme.text).font(.system(size: 13, weight: .medium))
      Spacer()
      if b.updating { ProgressView().controlSize(.small) } else {
        Link("What's new", destination: n.page).foregroundStyle(Theme.gold).font(.system(size: 13))
        Button("Update") { b.updateLauncher() }.buttonStyle(QuietButtonStyle()).disabled(b.running || b.playing)
          .help(b.playing ? "Quit the game first" : "")
      }
    }
    .padding(.horizontal, 20).frame(height: 44).background(Theme.gold.opacity(0.08))
  }

  var setupView: some View {
    VStack(alignment: .leading, spacing: 12) {
      VStack(spacing: 10) {
        if b.inputs.isEmpty {
          Image(systemName: "tray.and.arrow.down").font(.system(size: 30, weight: .light)).foregroundStyle(Theme.gold)
          Text("Drop your copy of the game here").font(.system(size: 15, weight: .semibold)).foregroundStyle(Theme.text)
          Text("Installed game folder, CD, disc image (.iso, .bin/.cue, .mdf), installer or .zip")
            .font(.system(size: 12)).foregroundStyle(Theme.dim).multilineTextAlignment(.center)
        } else {
          ForEach(b.inputs, id: \.self) { u in
            Label(u.lastPathComponent, systemImage: "doc.fill").foregroundStyle(Theme.text).lineLimit(1).truncationMode(.middle)
          }
        }
        HStack {
          Button(b.inputs.isEmpty ? "Choose…" : "Add more…") { b.choose() }.buttonStyle(QuietButtonStyle()).disabled(b.running)
          if !b.inputs.isEmpty { Button("Clear") { b.inputs = [] }.buttonStyle(QuietButtonStyle()).disabled(b.running) }
        }
      }
      .frame(maxWidth: .infinity, minHeight: 150).padding(14)
      .background(RoundedRectangle(cornerRadius: 14).fill(dropping ? Theme.gold.opacity(0.08) : Theme.card))
      .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(dropping ? Theme.gold : Theme.gold.opacity(0.35),
                                                               style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])))
      if b.running || b.step >= 0 {
        Card {
          HStack(spacing: 0) {
            ForEach(Array(steps.enumerated()), id: \.offset) { i, s in
              HStack(spacing: 6) {
                Image(systemName: i < b.step ? "checkmark.circle.fill" : (i == b.step && b.running ? "circle.dotted" : "circle"))
                  .foregroundStyle(i < b.step ? Theme.green : (i == b.step ? Theme.gold : Theme.dim))
                Text(s.text).font(.system(size: 12)).foregroundStyle(i <= b.step ? Theme.text : Theme.dim).lineLimit(1)
              }
              if i < steps.count - 1 { Spacer(minLength: 6) }
            }
          }
          if steps.indices.contains(b.step) && !["find", "unpack"].contains(steps[b.step].key) && b.running {
            ProgressView(value: b.progress).tint(Theme.gold)
          }
        }
      } else {
        Text("Supported: US 1.53 (exact) · UK 1.1 · Russian EA release. Game data is copied from your own copy; nothing of the game is included.")
          .font(.system(size: 12)).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
      }
    }
  }

  var installedView: some View {
    VStack(alignment: .leading, spacing: 12) {
      Card(title: "Game") {
        if let ed = b.edition { editionChip(ed) } else { Text("Installed").foregroundStyle(Theme.text) }
        HStack(spacing: 8) {
          Button { b.open(Launcher.savesURL) } label: { Label("Saved Games", systemImage: "folder") }.buttonStyle(QuietButtonStyle())
          Button { b.open(Launcher.logsURL) } label: { Label("Logs", systemImage: "doc.text") }.buttonStyle(QuietButtonStyle())
          Spacer()
          Text("Drop other game files here to reinstall").font(.system(size: 11)).foregroundStyle(Theme.dim)
        }
      }
      Card(title: "Mods") {
        if b.mods.isEmpty {
          Text("No mods yet. Put a mod's folder into the Mods folder; it shows up here to switch on and off.")
            .font(.system(size: 12)).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
        } else {
          ScrollView {
            VStack(spacing: 2) {
              ForEach(b.mods) { m in modRow(m) }
            }
          }.frame(maxHeight: 128)
        }
        if !b.modsNotice.isEmpty {
          HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: "info.circle").foregroundStyle(Theme.amber)
            Text(b.modsNotice.joined(separator: "\n"))
              .font(.system(size: 12)).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
          }
        }
        HStack {
          Button { b.open(Launcher.modsURL) } label: { Label("Open Mods Folder", systemImage: "folder.badge.plus") }
            .buttonStyle(QuietButtonStyle())
          Button { b.loadMods() } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(QuietButtonStyle()).help("Refresh")
          Spacer()
          if b.mods.contains(where: \.enabled) {
            Text("Lower in the list wins").font(.system(size: 11)).foregroundStyle(Theme.dim)
          }
        }
      }
    }
  }

  func modRow(_ m: Launcher.Mod) -> some View {
    let on = b.mods.filter(\.enabled)
    let i = on.firstIndex(of: m)
    return HStack(spacing: 10) {
      Toggle("", isOn: Binding(get: { m.enabled }, set: { b.setMod(m.name, $0) })).toggleStyle(.switch).labelsHidden()
        .controlSize(.small).tint(Theme.gold).disabled(b.playing)
      Text(m.name).foregroundStyle(m.enabled ? Theme.text : Theme.dim).lineLimit(1)
      if m.missing { Text("folder missing").font(.system(size: 11)).foregroundStyle(Theme.amber) }
      Spacer()
      if let i {
        Button { b.moveMod(m.name, by: -1) } label: { Image(systemName: "chevron.up") }.buttonStyle(.plain)
          .foregroundStyle(i > 0 ? Theme.text : Theme.dim.opacity(0.4)).disabled(i == 0 || b.playing)
        Button { b.moveMod(m.name, by: 1) } label: { Image(systemName: "chevron.down") }.buttonStyle(.plain)
          .foregroundStyle(i < on.count - 1 ? Theme.text : Theme.dim.opacity(0.4)).disabled(i == on.count - 1 || b.playing)
      }
    }
    .padding(.horizontal, 8).padding(.vertical, 5)
    .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(m.enabled ? 0.04 : 0)))
  }

  var footer: some View {
    HStack {
      Menu {
        Button("Open Saved Games Folder") { b.open(Launcher.savesURL) }.disabled(!b.installed)
        Button("Open Logs") { b.open(Launcher.logsURL) }
        Button("Reset Settings…") { b.resetSettings() }.disabled(!b.installed)
        Button("Reset Gamepad Bindings…") { b.resetPad() }.disabled(!b.installed || b.playing || Launcher.padRebound(game: Launcher.gameURL) == 0)
        Button("Remove Game…") { b.removeGame() }.disabled(!b.installed || b.running || b.playing)
        Divider()
        Button("Check for Updates") { b.checkForUpdate(manual: true) }
        Button("About HPEngine Launcher") { b.about() }
      } label: { Label("More", systemImage: "ellipsis.circle") }
        .menuStyle(.borderlessButton).fixedSize().foregroundStyle(Theme.dim)
      Spacer()
      if b.installed && !b.running && b.inputs.isEmpty {
        if !b.startMaps.isEmpty {
          Picker(Launcher.startMapTitle, selection: $b.startMap) {
            Text(Launcher.startMapGame).tag(String?.none)
            ForEach(Array(zip(b.startMaps, Launcher.startMapLabels(b.startMaps))), id: \.0.map) { m, label in Text(label).tag(String?.some(m.map)) }
          }.pickerStyle(.menu).fixedSize().foregroundStyle(Theme.dim).disabled(b.playing)
        }
        Button { b.play() } label: { Label(b.playing ? "Playing…" : "Play", systemImage: "play.fill").frame(minWidth: 110) }
          .buttonStyle(GoldButtonStyle()).keyboardShortcut(.defaultAction).disabled(b.playing)
      } else {
        Button { b.install() } label: { Text(b.installed ? "Install Again" : "Install").frame(minWidth: 110) }
          .buttonStyle(GoldButtonStyle()).keyboardShortcut(.defaultAction)
          .disabled(b.inputs.isEmpty || b.running || b.playing || b.updating)
          .help(b.playing ? "Quit the game first" : "")
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
    case "--pad-reset" where a.count == 3 || (a.count == 4 && a[3] == "reset"):
      // <game dir> [reset]: how many lines "Reset Gamepad Bindings" removes; with "reset" it removes them (launcher/test_update.sh).
      print(Launcher.padRebound(game: URL(fileURLWithPath: a[2]), reset: a.count == 4)); exit(0)
    case "--engine-error" where a.count == 4:
      // <exit code> <engine console output file>: the error banner (launcher/test_update.sh).
      guard let code = Int32(a[2]), let s = try? String(contentsOfFile: a[3], encoding: .utf8) else { print("FAIL"); exit(1) }
      print(Launcher.errorBanner(s.components(separatedBy: "\n"), code: code, log: "L.")); exit(0)
    case "--runtime-path" where a.count == 2:
      print(Launcher.runtimeURL.path); exit(0)
    default:
      FileHandle.standardError.write(Data("usage: Launcher --verify-update <zip> <sig> <key> <version> | --pick-release <json> <version> | --mods-ini <file> [names...] | --mods-notice <game dir> | --mods-check <game dir> | --start-maps <game dir> [map] | --engine-error <code> <file> | --slot-mods <save file> | --remove-game <root> <keep|all> [fail-after-marker] | --runtime-path\n".utf8))
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
