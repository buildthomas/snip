import XCTest
import CoreGraphics
@testable import SnipCore

final class GeometryTests: XCTestCase {
    func testResizeMovesOnlyGrabbedEdges() {
        let r = CGRect(x: 0, y: 0, width: 100, height: 100)
        XCTAssertEqual(Geometry.resize(r, handle: 0, dx: -10, dy: -10).size, CGSize(width: 110, height: 110))
        XCTAssertEqual(Geometry.resize(r, handle: 3, dx: 5, dy: 99).size, CGSize(width: 105, height: 100))
        XCTAssertEqual(Geometry.resize(r, handle: 1, dx: 0, dy: -20).size, CGSize(width: 100, height: 120))
    }

    func testResizeFlipsPastOppositeEdge() {
        XCTAssertEqual(Geometry.resize(CGRect(x: 0, y: 0, width: 100, height: 100), handle: 3, dx: -150, dy: 0),
                       CGRect(x: -50, y: 0, width: 50, height: 100))
    }

    func testSnap45() {
        let p = Geometry.snap45(from: .zero, to: CGPoint(x: 100, y: 3))
        XCTAssertEqual(p.y, 0, accuracy: 0.001)
        let d = Geometry.snap45(from: .zero, to: CGPoint(x: 100, y: 90))
        XCTAssertEqual(d.x, d.y, accuracy: 0.001)
    }

    func testBoxSquares() {
        let b = Geometry.box(CGPoint(x: 50, y: 50), CGPoint(x: 10, y: 30), square: true)
        XCTAssertEqual(b.width, 40); XCTAssertEqual(b.height, 40)
        XCTAssertEqual(b.maxX, 50); XCTAssertEqual(b.maxY, 50)
    }

    func testTrimWord() {
        XCTAssertEqual(Geometry.trimWord("hello world"), "hello ")
        XCTAssertEqual(Geometry.trimWord("hello world  "), "hello ")
        XCTAssertEqual(Geometry.trimWord("one"), "")
    }
}
