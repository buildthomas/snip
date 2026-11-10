import XCTest
@testable import SnipCore

final class EscapeGuardTests: XCTestCase {
    func testWithoutAnnotationsFirstPressCloses() {
        var g = EscapeGuard()
        XCTAssertTrue(g.shouldClose(hasAnnotations: false, nowMs: 0))
    }

    func testWithAnnotationsNeedsSecondPressWithinWindow() {
        var g = EscapeGuard()
        XCTAssertFalse(g.shouldClose(hasAnnotations: true, nowMs: 1000))
        XCTAssertTrue(g.shouldClose(hasAnnotations: true, nowMs: 1000 + EscapeGuard.windowMs))
    }

    func testWithAnnotationsSlowSecondPressRearms() {
        var g = EscapeGuard()
        XCTAssertFalse(g.shouldClose(hasAnnotations: true, nowMs: 0))
        XCTAssertFalse(g.shouldClose(hasAnnotations: true, nowMs: EscapeGuard.windowMs + 1))
        XCTAssertTrue(g.shouldClose(hasAnnotations: true, nowMs: EscapeGuard.windowMs + 500))
    }

    func testClosingResetsSoNextPressStartsDisarmed() {
        var g = EscapeGuard()
        _ = g.shouldClose(hasAnnotations: true, nowMs: 0)
        XCTAssertTrue(g.shouldClose(hasAnnotations: true, nowMs: 100))
        XCTAssertFalse(g.shouldClose(hasAnnotations: true, nowMs: 200))
    }
}
