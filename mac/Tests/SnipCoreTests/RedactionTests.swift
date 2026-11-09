import XCTest
@testable import SnipCore

final class RedactionTests: XCTestCase {
    private let area = IntRect(x: 30, y: 20, width: 48, height: 40)
    private let canvas = IntRect(x: 0, y: 0, width: 120, height: 90)

    private func image(_ paint: (inout PixelBuffer) -> Void) -> PixelBuffer {
        var b = PixelBuffer(width: canvas.width, height: canvas.height, fill: 0xFF28_5AA0)
        paint(&b)
        return b
    }

    private func fill(_ b: inout PixelBuffer, _ r: IntRect, _ c: UInt32) {
        for y in r.y..<r.maxY { for x in r.x..<r.maxX { b[x, y] = c } }
    }

    private func mask(_ r: IntRect) -> [Bool] {
        var m = [Bool](repeating: false, count: canvas.width * canvas.height)
        for y in r.y..<r.maxY { for x in r.x..<r.maxX { m[y * canvas.width + x] = true } }
        return m
    }

    private func secure(_ src: PixelBuffer, seed: UInt64 = 99, cell: Int = 8, smooth: Bool = false) -> PixelBuffer {
        Redaction.secure(source: src, sourceRect: canvas, inside: mask(area), target: area, seed: seed, cell: cell, smooth: smooth, edges: true)
    }

    func testSecureOutputDependsOnlyOnColourDistributionNotPositions() {
        for smooth in [false, true] {
            let leftRight = image { b in
                fill(&b, IntRect(x: area.x, y: area.y, width: area.width / 2, height: area.height), 0xFFFF_FFFF)
                fill(&b, IntRect(x: area.x + area.width / 2, y: area.y, width: area.width / 2, height: area.height), 0xFF00_0000)
            }
            let topBottom = image { b in
                fill(&b, IntRect(x: area.x, y: area.y, width: area.width, height: area.height / 2), 0xFFFF_FFFF)
                fill(&b, IntRect(x: area.x, y: area.y + area.height / 2, width: area.width, height: area.height / 2), 0xFF00_0000)
            }
            XCTAssertEqual(secure(leftRight, smooth: smooth).pixels, secure(topBottom, smooth: smooth).pixels, "smooth=\(smooth)")
        }
    }

    func testEveryPixelOpaque() {
        let out = secure(image { fill(&$0, area, 0xFF01_FE03) })
        XCTAssertTrue(out.pixels.allSatisfy { $0 >> 24 == 0xFF })
        XCTAssertLessThan(out.pixels.filter { $0 == 0xFF01_FE03 }.count, out.pixels.count / 4)
    }

    func testSameSeedStableDifferentSeedDiffers() {
        let src = image { fill(&$0, area, 0xFFFF_A500) ; fill(&$0, IntRect(x: 40, y: 30, width: 10, height: 10), 0xFF00_0000) }
        XCTAssertEqual(secure(src, seed: 5).pixels, secure(src, seed: 5).pixels)
        XCTAssertNotEqual(secure(src, seed: 5).pixels, secure(src, seed: 6).pixels)
    }

    func testFlatAreaStillLooksRedacted() {
        let white = PixelBuffer(width: canvas.width, height: canvas.height, fill: 0xFFFF_FFFF)
        let lums = secure(white).pixels.map { RGB(argb: $0).lum }
        XCTAssertGreaterThanOrEqual((lums.max() ?? 0) - (lums.min() ?? 0), 40)
    }

    func testCellsAreUniformOnTheAbsoluteGrid() {
        let src = image { fill(&$0, area, 0xFFFF_A500) }
        let target = IntRect(x: 16, y: 16, width: 64, height: 64)
        let m = { () -> [Bool] in
            var v = [Bool](repeating: false, count: canvas.width * canvas.height)
            for y in target.y..<target.maxY { for x in target.x..<target.maxX { v[y * canvas.width + x] = true } }
            return v
        }()
        for cell in [8, 16] {
            let out = Redaction.secure(source: src, sourceRect: canvas, inside: m, target: target, seed: 4, cell: cell, smooth: false, edges: true)
            var cy = 0
            while cy < target.height {
                var cx = 0
                while cx < target.width {
                    XCTAssertEqual(out[cx, cy], out[cx + cell - 1, cy + cell - 1], "cell \(cell) at \(cx),\(cy)")
                    cx += cell
                }
                cy += cell
            }
        }
    }

    func testInsecureModesReallyFilterTheContent() {
        let leftRight = image { b in
            fill(&b, IntRect(x: area.x, y: area.y, width: area.width / 2, height: area.height), 0xFFFF_FFFF)
            fill(&b, IntRect(x: area.x + area.width / 2, y: area.y, width: area.width / 2, height: area.height), 0xFF00_0000)
        }
        let pix = Redaction.realPixelate(source: leftRight, sourceRect: canvas, target: area, cell: 8)
        XCTAssertGreaterThan(RGB(argb: pix[1, 20]).lum, RGB(argb: pix[area.width - 2, 20]).lum)
        let blur = Redaction.realBlur(source: leftRight, sourceRect: canvas, target: area, radius: 4)
        XCTAssertGreaterThan(RGB(argb: blur[2, 20]).lum, RGB(argb: blur[area.width - 3, 20]).lum)
    }

    func testIntRectIntersection() {
        XCTAssertEqual(IntRect(x: 0, y: 0, width: 10, height: 10).intersection(IntRect(x: 5, y: 5, width: 10, height: 10)),
                       IntRect(x: 5, y: 5, width: 5, height: 5))
        XCTAssertTrue(IntRect(x: 0, y: 0, width: 2, height: 2).intersection(IntRect(x: 5, y: 5, width: 1, height: 1)).isEmpty)
    }
}
