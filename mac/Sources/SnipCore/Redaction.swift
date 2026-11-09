import Foundation

/// Integer pixel rectangle (origin top-left, y down).
public struct IntRect: Equatable {
    public var x, y, width, height: Int
    public init(x: Int, y: Int, width: Int, height: Int) { self.x = x; self.y = y; self.width = width; self.height = height }
    public var maxX: Int { x + width }
    public var maxY: Int { y + height }
    public var isEmpty: Bool { width <= 0 || height <= 0 }
    public func insetBy(_ d: Int) -> IntRect { IntRect(x: x - d, y: y - d, width: width + 2 * d, height: height + 2 * d) }
    public func intersection(_ o: IntRect) -> IntRect {
        let l = max(x, o.x), t = max(y, o.y), r = min(maxX, o.maxX), b = min(maxY, o.maxY)
        return IntRect(x: l, y: t, width: max(0, r - l), height: max(0, b - t))
    }
}

/// Opaque pixels as 0xAARRGGBB, row-major, top row first.
public struct PixelBuffer {
    public var width: Int
    public var height: Int
    public var pixels: [UInt32]
    public init(width: Int, height: Int, pixels: [UInt32]) { self.width = width; self.height = height; self.pixels = pixels }
    public init(width: Int, height: Int, fill: UInt32) { self.init(width: width, height: height, pixels: Array(repeating: fill, count: width * height)) }
    public subscript(x: Int, y: Int) -> UInt32 {
        get { pixels[y * width + x] }
        set { pixels[y * width + x] = newValue }
    }
}

struct RGB: Equatable {
    var r, g, b: Double
    init(_ r: Double, _ g: Double, _ b: Double) { self.r = r; self.g = g; self.b = b }
    init(argb p: UInt32) { r = Double((p >> 16) & 255); g = Double((p >> 8) & 255); b = Double(p & 255) }
    var lum: Double { (r * 299 + g * 587 + b * 114) / 1000 }
    var argb: UInt32 {
        func c(_ v: Double) -> UInt32 { UInt32(max(0, min(255, v.rounded()))) }
        return 0xFF00_0000 | c(r) << 16 | c(g) << 8 | c(b)
    }
    /// `t` of self, (1 - t) of other.
    func mix(_ other: RGB, _ t: Double) -> RGB { RGB(r * t + other.r * (1 - t), g * t + other.g * (1 - t), b * t + other.b * (1 - t)) }
}

/// Port of the Windows app's redaction (see docs/architecture.md).
///
/// Secure pixelate/blur are generated: each cell's colour comes from hashed value noise (per-mark random seed)
/// mapped onto a 4-tone palette of the covered pixels' brightness quantiles, blended near the edge toward the
/// colours just outside the shape. Where things were never influences the output.
public enum Redaction {
    public static let minContrast = 70.0
    public static let cellSteps = [4, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48]

    /// - Parameters:
    ///   - source: pixels of `sourceRect` (absolute pixel coordinates), which should extend past `target` by the edge reach.
    ///   - inside: true for pixels of `sourceRect` covered by the shape.
    ///   - target: the area to produce (absolute pixels), normally the shape's bounds.
    public static func secure(source: PixelBuffer, sourceRect: IntRect, inside: [Bool], target: IntRect, seed: UInt64,
                              cell: Int, smooth: Bool, edges: Bool, edgeReach: Int? = nil, edgeStrength: Double = 0.7) -> PixelBuffer {
        precondition(source.width == sourceRect.width && source.height == sourceRect.height && inside.count == source.pixels.count)
        let reach = max(2, min(edgeReach ?? Int(2.5 * Double(cell)), min(target.width, target.height) / 4))
        let palette = ensureContrast(tones(source.pixels, inside: inside, count: 4, lo: 0.15, hi: 0.85), minRange: minContrast)

        let bx0 = floorDiv(target.x, cell), by0 = floorDiv(target.y, cell)
        let bx1 = floorDiv(target.maxX - 1, cell), by1 = floorDiv(target.maxY - 1, cell)
        let nbx = bx1 - bx0 + 1, nby = by1 - by0 + 1
        var field = [Double](repeating: 0, count: nbx * nby)
        for j in 0..<nby { for i in 0..<nbx { field[j * nbx + i] = noise(seed: seed, bx: bx0 + i, by: by0 + j) } }
        let lo = field.min() ?? 0, hi = field.max() ?? 1

        var colors = [RGB](repeating: RGB(0, 0, 0), count: nbx * nby)
        for j in 0..<nby {
            for i in 0..<nbx {
                let t = hi > lo ? (field[j * nbx + i] - lo) / (hi - lo) : 0.5
                var c = ramp(palette, t)
                if edges {
                    let cx = (bx0 + i) * cell + cell / 2 - sourceRect.x
                    let cy = (by0 + j) * cell + cell / 2 - sourceRect.y
                    if let (dist, outside) = nearestOutside(source, inside, cx, cy, reach) {
                        let k = 1 - dist / Double(reach)
                        c = outside.mix(c, edgeStrength * k * k * (3 - 2 * k))
                    }
                }
                colors[j * nbx + i] = c
            }
        }

        var out = PixelBuffer(width: target.width, height: target.height, fill: 0xFF00_0000)
        for y in 0..<target.height {
            for x in 0..<target.width {
                let ax = target.x + x, ay = target.y + y
                let c: RGB
                if !smooth {
                    c = colors[(floorDiv(ay, cell) - by0) * nbx + (floorDiv(ax, cell) - bx0)]
                } else {
                    let gx = min(max((Double(ax) + 0.5) / Double(cell) - 0.5 - Double(bx0), 0), Double(nbx - 1))
                    let gy = min(max((Double(ay) + 0.5) / Double(cell) - 0.5 - Double(by0), 0), Double(nby - 1))
                    let x0 = Int(gx), y0 = Int(gy), x1 = min(x0 + 1, nbx - 1), y1 = min(y0 + 1, nby - 1)
                    let tx = gx - Double(x0), ty = gy - Double(y0)
                    let top = colors[y0 * nbx + x1].mix(colors[y0 * nbx + x0], tx)
                    let bottom = colors[y1 * nbx + x1].mix(colors[y1 * nbx + x0], tx)
                    c = bottom.mix(top, ty)
                }
                out[x, y] = c.argb
            }
        }
        return out
    }

    /// NOT secure: real block averages of the covered pixels on the absolute grid.
    public static func realPixelate(source: PixelBuffer, sourceRect: IntRect, target: IntRect, cell: Int) -> PixelBuffer {
        var out = PixelBuffer(width: target.width, height: target.height, fill: 0xFF00_0000)
        var by = floorDiv(target.y, cell) * cell
        while by < target.maxY {
            var bx = floorDiv(target.x, cell) * cell
            while bx < target.maxX {
                let block = IntRect(x: bx, y: by, width: cell, height: cell).intersection(sourceRect)
                var r = 0.0, g = 0.0, b = 0.0, n = 0.0
                for y in block.y..<block.maxY {
                    for x in block.x..<block.maxX {
                        let c = RGB(argb: source[x - sourceRect.x, y - sourceRect.y])
                        r += c.r; g += c.g; b += c.b; n += 1
                    }
                }
                let avg = n > 0 ? RGB(r / n, g / n, b / n).argb : 0xFF00_0000
                let fill = IntRect(x: bx, y: by, width: cell, height: cell).intersection(target)
                for y in fill.y..<fill.maxY { for x in fill.x..<fill.maxX { out[x - target.x, y - target.y] = avg } }
                bx += cell
            }
            by += cell
        }
        return out
    }

    /// NOT secure: three box-blur passes of the covered pixels (approximates a Gaussian).
    public static func realBlur(source: PixelBuffer, sourceRect: IntRect, target: IntRect, radius: Int) -> PixelBuffer {
        let w = source.width, h = source.height
        var channels = (0..<3).map { shift in source.pixels.map { Int(($0 >> UInt32(16 - shift * 8)) & 255) } }
        for c in 0..<3 {
            for _ in 0..<3 {
                boxBlur(&channels[c], w, h, radius, horizontal: true)
                boxBlur(&channels[c], w, h, radius, horizontal: false)
            }
        }
        var out = PixelBuffer(width: target.width, height: target.height, fill: 0xFF00_0000)
        for y in 0..<target.height {
            for x in 0..<target.width {
                let i = (target.y - sourceRect.y + y) * w + (target.x - sourceRect.x + x)
                out[x, y] = 0xFF00_0000 | UInt32(channels[0][i]) << 16 | UInt32(channels[1][i]) << 8 | UInt32(channels[2][i])
            }
        }
        return out
    }

    // MARK: - internals

    static func floorDiv(_ a: Int, _ b: Int) -> Int { Int((Double(a) / Double(b)).rounded(.down)) }

    private static func boxBlur(_ ch: inout [Int], _ w: Int, _ h: Int, _ radius: Int, horizontal: Bool) {
        let lines = horizontal ? h : w, len = horizontal ? w : h
        var buf = [Int](repeating: 0, count: len)
        for line in 0..<lines {
            func idx(_ k: Int) -> Int { horizontal ? line * w + k : k * w + line }
            var sum = 0, lo = 0, hi = -1
            for i in 0..<len {
                let want0 = max(0, i - radius), want1 = min(len - 1, i + radius)
                while hi < want1 { hi += 1; sum += ch[idx(hi)] }
                while lo < want0 { sum -= ch[idx(lo)]; lo += 1 }
                buf[i] = sum / (hi - lo + 1)
            }
            for i in 0..<len { ch[idx(i)] = buf[i] }
        }
    }

    private static func nearestOutside(_ px: PixelBuffer, _ inside: [Bool], _ cx: Int, _ cy: Int, _ reach: Int) -> (Double, RGB)? {
        var d = 1
        while d <= reach {
            var r = 0.0, g = 0.0, b = 0.0, n = 0.0
            func sample(_ x: Int, _ y: Int) {
                guard x >= 0, y >= 0, x < px.width, y < px.height else { return }
                let i = y * px.width + x
                guard !inside[i] else { return }
                let c = RGB(argb: px.pixels[i])
                r += c.r; g += c.g; b += c.b; n += 1
            }
            var k = -d
            while k <= d {
                sample(cx + k, cy - d); sample(cx + k, cy + d); sample(cx - d, cy + k); sample(cx + d, cy + k)
                k += 2
            }
            if n > 0 { return (Double(d), RGB(r / n, g / n, b / n)) }
            d += 2
        }
        return nil
    }

    /// Two-octave hashed value noise on the absolute block grid (stable while a shape grows).
    static func noise(seed: UInt64, bx: Int, by: Int) -> Double {
        func s(_ t: Double) -> Double { t * t * (3 - 2 * t) }
        func lattice(_ x: Int, _ y: Int, _ salt: UInt64) -> Double { Double(hash(seed ^ salt, x, y) >> 11) * (1.0 / Double(1 << 53)) }
        func octave(_ spacing: Int, _ salt: UInt64) -> Double {
            let fx = Double(bx) / Double(spacing), fy = Double(by) / Double(spacing)
            let ix = Int(fx.rounded(.down)), iy = Int(fy.rounded(.down))
            let tx = s(fx - Double(ix)), ty = s(fy - Double(iy))
            let top = lattice(ix, iy, salt) * (1 - tx) + lattice(ix + 1, iy, salt) * tx
            let bottom = lattice(ix, iy + 1, salt) * (1 - tx) + lattice(ix + 1, iy + 1, salt) * tx
            return top * (1 - ty) + bottom * ty
        }
        let jitter = Double(hash(seed ^ 0x51, bx, by) >> 11) * (1.0 / Double(1 << 53)) - 0.5
        return 0.7 * octave(4, 0xA1) + 0.3 * octave(2, 0xB2) + 0.08 * jitter
    }

    static func hash(_ seed: UInt64, _ x: Int, _ y: Int) -> UInt64 {
        var z = seed ^ (UInt64(UInt32(truncatingIfNeeded: x)) &* 0x9E37_79B9_7F4A_7C15) ^ (UInt64(UInt32(truncatingIfNeeded: y)) &* 0xC2B2_AE3D_27D4_EB4F)
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Representative colours at evenly spaced brightness quantiles (between lo and hi) of the covered pixels.
    static func tones(_ pixels: [UInt32], inside: [Bool], count n: Int, lo: Double, hi: Double) -> [RGB] {
        var list: [UInt32] = []
        let covered = inside.lazy.filter { $0 }.count
        let stride = max(1, covered / 60_000)
        var k = 0
        for i in 0..<pixels.count where inside[i] {
            if k % stride == 0 { list.append(pixels[i] | 0xFF00_0000) }
            k += 1
        }
        guard !list.isEmpty else { return Array(repeating: RGB(128, 128, 128), count: n) }
        list.sort { a, b in
            let la = RGB(argb: a).lum, lb = RGB(argb: b).lum
            return la != lb ? la < lb : a < b
        }
        let from = Int(lo * Double(list.count)), to = max(from + 1, Int(hi * Double(list.count)))
        let span = Array(list[from..<min(to, list.count)])
        return (0..<n).map { k in
            let a = k * span.count / n, z = max(a + 1, (k + 1) * span.count / n)
            var r = 0.0, g = 0.0, b = 0.0
            for i in a..<min(z, span.count) { let c = RGB(argb: span[i]); r += c.r; g += c.g; b += c.b }
            let m = Double(min(z, span.count) - a)
            return RGB((r / m).rounded(.down), (g / m).rounded(.down), (b / m).rounded(.down))
        }
    }

    /// A redaction must always look like one: tones too close together are spread apart.
    static func ensureContrast(_ palette: [RGB], minRange: Double) -> [RGB] {
        let lums = palette.map(\.lum)
        guard let mx = lums.max(), let mn = lums.min(), mx - mn < minRange else { return palette }
        let avg = lums.reduce(0, +) / Double(lums.count)
        let center = min(max(avg, minRange / 2 + 10), 245 - minRange / 2)
        return palette.enumerated().map { i, c in
            let shift = (center + (Double(i) / Double(palette.count - 1) - 0.5) * minRange - lums[i]).rounded()
            return RGB(min(max(c.r + shift, 0), 255), min(max(c.g + shift, 0), 255), min(max(c.b + shift, 0), 255))
        }
    }

    static func ramp(_ palette: [RGB], _ t: Double) -> RGB {
        let x = min(max(t, 0), 1) * Double(palette.count - 1)
        let i = min(Int(x), palette.count - 2)
        return palette[i + 1].mix(palette[i], x - Double(i))
    }

    public static func newSeed() -> UInt64 {
        var g = SystemRandomNumberGenerator()
        return g.next()
    }
}
