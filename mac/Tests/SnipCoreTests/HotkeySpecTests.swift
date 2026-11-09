import XCTest
@testable import SnipCore

final class HotkeySpecTests: XCTestCase {
    func testParsesShiftCommandS() {
        for text in ["cmd+shift+s", "Shift+Command+S", "shift-cmd-s", "⇧+⌘+S"] {
            let h = HotkeySpec.parse(text)
            XCTAssertNotNil(h, text)
            XCTAssertEqual(h?.keyCode, 0x01, text)
            XCTAssertEqual(h?.display, "⇧⌘S", text)
            XCTAssertEqual(h?.carbonModifiers, HotkeySpec.carbonCommand | HotkeySpec.carbonShift, text)
            XCTAssertEqual(h?.spec, "shift+cmd+s", text)
        }
    }

    func testRoundTripsThroughSpecAndCarbonMask() {
        let h = HotkeySpec.parse("ctrl+opt+f5")!
        XCTAssertEqual(HotkeySpec.parse(h.spec), h)
        XCTAssertEqual(HotkeySpec(keyCode: h.keyCode, carbonModifiers: h.carbonModifiers), h)
        XCTAssertEqual(h.display, "⌃⌥F5")
    }

    func testRejectsMissingOrUnknownKeys() {
        XCTAssertNil(HotkeySpec.parse("cmd+shift"))
        XCTAssertNil(HotkeySpec.parse("cmd+foo"))
        XCTAssertNil(HotkeySpec.parse("cmd+a+b"))
        XCTAssertNil(HotkeySpec.parse(""))
    }

    func testRequiresCommandOptionOrControl() {
        XCTAssertFalse(HotkeySpec.parse("s")!.hasRequiredModifier)
        XCTAssertFalse(HotkeySpec.parse("shift+s")!.hasRequiredModifier)
        XCTAssertTrue(HotkeySpec.parse("cmd+shift+s")!.hasRequiredModifier)
        XCTAssertTrue(HotkeySpec.parse("ctrl+2")!.hasRequiredModifier)
    }

    func testFlagsWellKnownAppShortcuts() {
        XCTAssertTrue(HotkeySpec.parse("cmd+shift+s")!.commonlyUsedByApps)
        XCTAssertTrue(HotkeySpec.parse("cmd+c")!.commonlyUsedByApps)
        XCTAssertFalse(HotkeySpec.parse("cmd+shift+2")!.commonlyUsedByApps)
        XCTAssertFalse(HotkeySpec.parse("ctrl+opt+s")!.commonlyUsedByApps)
    }

    func testDefaultIsCommandShift2() {
        XCTAssertEqual(HotkeySpec.defaultSpec.display, "⇧⌘2")
    }
}
