// Ed25519 keys and signatures for the Launcher's self-update (launcher/Launcher.swift verifiedApp).
//   xcrun swift launcher/update_sign.swift keygen <private key file>   writes a new key (mode 600), prints the public key
//   xcrun swift launcher/update_sign.swift pub <private key file>      prints the public key (base64, 32 bytes)
//   xcrun swift launcher/update_sign.swift sign <private key file> <file>   writes <file>.sig (base64 signature)
// The private key stays with the release author, outside the repository; make_launcher.sh reads it from
// HPENGINE_UPDATE_KEY, pins the public key into the app and signs the release zip.
import CryptoKit
import Foundation

let a = CommandLine.arguments
func fail(_ m: String) -> Never { FileHandle.standardError.write(Data((m + "\n").utf8)); exit(2) }
func key(_ path: String) -> Curve25519.Signing.PrivateKey {
  guard let t = try? String(contentsOfFile: path, encoding: .utf8),
        let d = Data(base64Encoded: t.trimmingCharacters(in: .whitespacesAndNewlines)),
        let k = try? Curve25519.Signing.PrivateKey(rawRepresentation: d) else { fail("not a private key: \(path)") }
  return k
}
switch (a.count > 1 ? a[1] : "", a.count) {
case ("keygen", 3):
  guard !FileManager.default.fileExists(atPath: a[2]) else { fail("exists, not overwritten: \(a[2])") }
  let k = Curve25519.Signing.PrivateKey()
  guard FileManager.default.createFile(atPath: a[2], contents: Data(k.rawRepresentation.base64EncodedString().utf8),
                                       attributes: [.posixPermissions: 0o600]) else { fail("cannot write \(a[2])") }
  print(k.publicKey.rawRepresentation.base64EncodedString())
case ("pub", 3):
  print(key(a[2]).publicKey.rawRepresentation.base64EncodedString())
case ("sign", 4):
  guard let d = try? Data(contentsOf: URL(fileURLWithPath: a[3]), options: .mappedIfSafe),
        let s = try? key(a[2]).signature(for: d) else { fail("cannot sign \(a[3])") }
  do { try Data(s.base64EncodedString().utf8).write(to: URL(fileURLWithPath: a[3] + ".sig")) } catch { fail("\(error)") }
default:
  fail("usage: update_sign.swift keygen <key> | pub <key> | sign <key> <file>")
}
