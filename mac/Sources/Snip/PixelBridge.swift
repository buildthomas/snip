import CoreGraphics
import SnipCore

/// Converts between CGImage and SnipCore's 0xAARRGGBB buffers (BGRA little-endian in memory, top row first).
enum PixelBridge {
    static let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    static let bitmapInfo = CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue

    /// Reads `rect` (pixels, top-left origin) of `image`. Pixels outside the image come back black.
    static func read(_ image: CGImage, _ rect: IntRect) -> PixelBuffer {
        var buffer = PixelBuffer(width: rect.width, height: rect.height, fill: 0xFF00_0000)
        buffer.pixels.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(data: raw.baseAddress, width: rect.width, height: rect.height, bitsPerComponent: 8,
                                      bytesPerRow: rect.width * 4, space: colorSpace, bitmapInfo: bitmapInfo) else { return }
            // Bitmap memory is top row first; CG's origin is bottom-left, so place the image accordingly.
            ctx.draw(image, in: CGRect(x: -rect.x, y: rect.maxY - image.height, width: image.width, height: image.height))
        }
        return buffer
    }

    static func image(_ buffer: PixelBuffer) -> CGImage? {
        var pixels = buffer.pixels
        return pixels.withUnsafeMutableBytes { raw in
            CGContext(data: raw.baseAddress, width: buffer.width, height: buffer.height, bitsPerComponent: 8,
                      bytesPerRow: buffer.width * 4, space: colorSpace, bitmapInfo: bitmapInfo)?.makeImage()
        }
    }

    /// Rasterizes `path` (point coordinates, y down) into a coverage mask over `rect` (pixels) at `scale` pixels per point.
    static func mask(_ path: CGPath, rect: IntRect, scale: CGFloat) -> [Bool] {
        var bytes = [UInt8](repeating: 0, count: rect.width * rect.height)
        bytes.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(data: raw.baseAddress, width: rect.width, height: rect.height, bitsPerComponent: 8,
                                      bytesPerRow: rect.width, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return }
            ctx.setShouldAntialias(false)
            ctx.translateBy(x: 0, y: CGFloat(rect.height))
            ctx.scaleBy(x: 1, y: -1)
            ctx.translateBy(x: CGFloat(-rect.x), y: CGFloat(-rect.y))
            ctx.scaleBy(x: scale, y: scale)
            ctx.addPath(path)
            ctx.setFillColor(gray: 1, alpha: 1)
            ctx.fillPath(using: .winding)
        }
        return bytes.map { $0 > 127 }
    }
}
