import Cocoa
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8,
                          samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
NSColor.clear.setFill(); NSRect(x: 0, y: 0, width: 1024, height: 1024).fill()
let tile = NSBezierPath(roundedRect: NSRect(x: 38, y: 38, width: 948, height: 948), xRadius: 215, yRadius: 215)
NSGradient(starting: NSColor(red: 0.14, green: 0.48, blue: 1, alpha: 1), ending: NSColor(red: 0.04, green: 0.19, blue: 0.63, alpha: 1))!.draw(in: tile, angle: -65)
let shield = NSBezierPath()
shield.move(to: NSPoint(x: 512, y: 800)); shield.line(to: NSPoint(x: 750, y: 704))
shield.line(to: NSPoint(x: 750, y: 490))
shield.curve(to: NSPoint(x: 512, y: 221), controlPoint1: NSPoint(x: 750, y: 350), controlPoint2: NSPoint(x: 630, y: 275))
shield.curve(to: NSPoint(x: 274, y: 490), controlPoint1: NSPoint(x: 395, y: 275), controlPoint2: NSPoint(x: 274, y: 350))
shield.line(to: NSPoint(x: 274, y: 704)); shield.close()
NSColor.white.setFill(); shield.fill()
let check = NSBezierPath(); check.move(to: NSPoint(x: 390, y: 510)); check.line(to: NSPoint(x: 485, y: 420)); check.line(to: NSPoint(x: 640, y: 620))
check.lineWidth = 52; check.lineCapStyle = .round; check.lineJoinStyle = .round
NSColor(red: 0.08, green: 0.35, blue: 0.87, alpha: 1).setStroke(); check.stroke()
NSGraphicsContext.restoreGraphicsState()
try rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
