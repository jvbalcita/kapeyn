import AppKit

// Coffee cup icon: steaming = awake, outline = off.
enum IconArt {
    static let amber = NSColor(srgbRed: 222 / 255, green: 128 / 255, blue: 44 / 255, alpha: 1)

    /// Cup artwork, designed on a 32x32 grid (y down).
    static func cup(size: CGFloat, on: Bool, color: NSColor) -> NSImage {
        return NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            NSGraphicsContext.saveGraphicsState()
            defer { NSGraphicsContext.restoreGraphicsState() }

            let t = NSAffineTransform()
            t.scale(by: rect.width / 32)
            t.translateX(by: 1.5, yBy: 0)
            t.concat()

            color.set()
            let stroke: CGFloat = 2.8

            // handle (right half only, so it doesn't show inside the hollow cup)
            let handle = NSBezierPath()
            handle.appendArc(withCenter: NSPoint(x: 21.75, y: 18.5), radius: 4.15,
                             startAngle: -100, endAngle: 100, clockwise: false)
            handle.lineWidth = stroke
            handle.lineCapStyle = .round
            handle.stroke()

            // body: flat top, rounded bottom
            let body = NSBezierPath()
            body.move(to: NSPoint(x: 3, y: 12))
            body.line(to: NSPoint(x: 21, y: 12))
            body.line(to: NSPoint(x: 21, y: 22))
            body.appendArc(withCenter: NSPoint(x: 16, y: 22), radius: 5, startAngle: 0, endAngle: 90, clockwise: false)
            body.line(to: NSPoint(x: 8, y: 27))
            body.appendArc(withCenter: NSPoint(x: 8, y: 22), radius: 5, startAngle: 90, endAngle: 180, clockwise: false)
            body.close()
            if on {
                body.fill()
            } else {
                body.lineWidth = stroke
                body.lineJoinStyle = .round
                body.stroke()
            }

            // saucer
            let saucer = NSBezierPath()
            saucer.move(to: NSPoint(x: 1.5, y: 29.5))
            saucer.line(to: NSPoint(x: 23.5, y: 29.5))
            saucer.lineWidth = stroke
            saucer.lineCapStyle = .round
            saucer.stroke()

            // steam
            if on {
                for i in 0..<3 {
                    let x = 7 + CGFloat(i) * 5
                    let steam = NSBezierPath()
                    steam.move(to: NSPoint(x: x, y: 9.5))
                    steam.curve(to: NSPoint(x: x, y: 2),
                                controlPoint1: NSPoint(x: x - 2.2, y: 7),
                                controlPoint2: NSPoint(x: x + 2.2, y: 4.5))
                    steam.lineWidth = stroke * 0.8
                    steam.lineCapStyle = .round
                    steam.stroke()
                }
            }
            return true
        }
    }

    /// Monochrome template image so it matches the light/dark menu bar automatically.
    static func menuBarIcon(on: Bool) -> NSImage {
        let img = cup(size: 18, on: on, color: .black)
        img.isTemplate = true
        img.accessibilityDescription = on ? "Kapeyn: awake" : "Kapeyn: off"
        return img
    }

    /// App icon: amber cup on an espresso rounded square.
    static func appIconPNG(pixels: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = NSSize(width: pixels, height: pixels)

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let s = CGFloat(pixels)
        let inset = s * 0.1
        let tile = NSRect(x: inset, y: inset, width: s - 2 * inset, height: s - 2 * inset)
        let shape = NSBezierPath(roundedRect: tile, xRadius: tile.width * 0.225, yRadius: tile.width * 0.225)
        let top = NSColor(srgbRed: 0.33, green: 0.20, blue: 0.13, alpha: 1)
        let bottom = NSColor(srgbRed: 0.16, green: 0.09, blue: 0.06, alpha: 1)
        NSGradient(starting: top, ending: bottom)?.draw(in: shape, angle: -90)
        let c = tile.width * 0.72
        cup(size: c, on: true, color: amber)
            .draw(in: NSRect(x: (s - c) / 2, y: (s - c) / 2, width: c, height: c))
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:])
    }

    /// Build helper: writes a .iconset folder for `iconutil`.
    static func exportIconset(to dir: String) {
        let url = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        for base in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
                if let data = appIconPNG(pixels: base * scale) {
                    try? data.write(to: url.appendingPathComponent(name))
                }
            }
        }
    }
}
