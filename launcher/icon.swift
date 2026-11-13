// Draws the HPEngine launcher icon (original artwork, GPL-3.0-or-later like the rest of the code): a red and gold shield with a
// wand and sparkles. No crest, no characters, no studio artwork.
//   xcrun swift launcher/icon.swift <out.iconset>     writes the PNG sizes iconutil expects
import AppKit

func draw(_ s: CGFloat) -> NSImage {
    NSImage(size: NSSize(width: s, height: s), flipped: false) { _ in
        let k = s / 1024
        func p(_ x: CGFloat, _ y: CGFloat) -> NSPoint { NSPoint(x: x * k, y: y * k) }
        let red = NSColor(srgbRed: 0.55, green: 0.07, blue: 0.09, alpha: 1)
        let gold = NSColor(srgbRed: 0.93, green: 0.72, blue: 0.22, alpha: 1)
        let dark = NSColor(srgbRed: 0.18, green: 0.10, blue: 0.06, alpha: 1)

        // Shield: flat top, curved sides meeting in a point.
        let shield = NSBezierPath()
        shield.move(to: p(172, 900)); shield.line(to: p(852, 900))
        shield.curve(to: p(512, 96), controlPoint1: p(852, 470), controlPoint2: p(700, 230))
        shield.curve(to: p(172, 900), controlPoint1: p(324, 230), controlPoint2: p(172, 470))
        shield.close()
        NSGraphicsContext.saveGraphicsState()
        shield.addClip()
        red.setFill(); NSRect(x: 0, y: 0, width: s, height: s).fill()
        let half = NSBezierPath(); half.move(to: p(512, 1024)); half.line(to: p(1024, 1024))
        half.line(to: p(1024, 0)); half.line(to: p(512, 0)); half.close()
        gold.setFill(); half.fill()
        NSGraphicsContext.restoreGraphicsState()
        dark.setStroke(); shield.lineWidth = 40 * k; shield.stroke()

        // Wand: a tapered stick from lower left to upper right.
        let wand = NSBezierPath()
        wand.move(to: p(318, 300)); wand.line(to: p(352, 270)); wand.line(to: p(706, 660)); wand.line(to: p(690, 676))
        wand.close()
        dark.setFill(); wand.fill()
        NSColor.white.withAlphaComponent(0.9).setStroke(); wand.lineWidth = 10 * k; wand.stroke()

        // Sparkles at the tip.
        func star(_ cx: CGFloat, _ cy: CGFloat, _ r: CGFloat) {
            let st = NSBezierPath()
            for i in 0..<8 {
                let a = CGFloat(i) * .pi / 4 + .pi / 2, rr = i % 2 == 0 ? r : r * 0.32
                let pt = p(cx + cos(a) * rr, cy + sin(a) * rr)
                i == 0 ? st.move(to: pt) : st.line(to: pt)
            }
            st.close(); NSColor.white.setFill(); st.fill()
        }
        star(728, 700, 92); star(612, 790, 44); star(800, 600, 36)
        return true
    }
}

let out = CommandLine.arguments.count == 2 ? CommandLine.arguments[1] : { FileHandle.standardError.write(
    "usage: icon.swift <out.iconset>\n".data(using: .utf8)!); exit(2) }()
try FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let px = CGFloat(base * scale)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(px), pixelsHigh: Int(px), bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                   bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        draw(px).draw(in: NSRect(x: 0, y: 0, width: px, height: px))
        NSGraphicsContext.restoreGraphicsState()
        let name = "icon_\(base)x\(base)\(scale == 2 ? "@2x" : "").png"
        try rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/\(name)"))
    }
}
