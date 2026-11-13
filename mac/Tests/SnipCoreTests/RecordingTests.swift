import XCTest
import CoreGraphics
@testable import SnipCore

final class RecordingMathTests: XCTestCase {
    func testEvenDropsOddRowAndColumn() {
        XCTAssertTrue(RecordingMath.even(641, 361) == (640, 360))
    }

    func testSizeProblemRejectsTinyAndHugeAreas() {
        XCTAssertNotNil(RecordingMath.sizeProblem(width: 8, height: 100))
        XCTAssertNil(RecordingMath.sizeProblem(width: 1920, height: 1080))
        XCTAssertNil(RecordingMath.sizeProblem(width: 3840, height: 2160))
        XCTAssertNotNil(RecordingMath.sizeProblem(width: 7680, height: 1440))
        XCTAssertNotNil(RecordingMath.sizeProblem(width: 4096, height: 4096))
    }

    func testFormatting() {
        XCTAssertEqual(RecordingMath.formatDuration(75.9), "1:15")
        XCTAssertEqual(RecordingMath.formatDuration(3725), "1:02:05")
        XCTAssertEqual(RecordingMath.formatSize(1_572_864), "1.5 MB")
        XCTAssertEqual(RecordingMath.formatSize(100), "1 KB")
    }
}

final class AudioMixerTests: XCTestCase {
    private func stereo(_ frames: Int, _ v: Float) -> [Float] { [Float](repeating: v, count: frames * 2) }

    func testGapsBecomeSilenceAndPacketsLandAtTheirTime() {
        let m = AudioMixer()
        m.add(stereo(10, 0.5), firstFrame: 100)
        let pcm = m.take(until: 120)
        XCTAssertEqual(pcm.count, 240)
        XCTAssertTrue(pcm[0..<200].allSatisfy { $0 == 0 })
        XCTAssertTrue(pcm[200..<220].allSatisfy { $0 == 16384 })
        XCTAssertTrue(pcm[220...].allSatisfy { $0 == 0 })
        XCTAssertEqual(m.written, 120)
    }

    func testSourcesAreSummedAndClipped() {
        let m = AudioMixer()
        m.add(stereo(4, 0.25), firstFrame: 0)
        m.add(stereo(4, 0.25), firstFrame: 0)
        m.add(stereo(4, 0.9), firstFrame: 2)
        let pcm = m.take(until: 4)
        XCTAssertEqual(pcm[0], 16384)
        XCTAssertEqual(pcm[4], 32767)
    }

    func testLateAudioIsDroppedNotShifted() {
        let m = AudioMixer()
        _ = m.take(until: 50)
        m.add(stereo(20, 0.5), firstFrame: 40)
        let pcm = m.take(until: 70)
        XCTAssertEqual(pcm.count, 40)
        XCTAssertTrue(pcm[0..<20].allSatisfy { $0 == 16384 })
        XCTAssertTrue(pcm[20...].allSatisfy { $0 == 0 })
    }

    func testRingWrapsAroundManyTimes() {
        let m = AudioMixer()
        var frame: Int64 = 0
        for _ in 0..<500 {
            m.add(stereo(480, 0.5), firstFrame: frame)
            let pcm = m.take(until: frame + 480)
            XCTAssertEqual(pcm.count, 960)
            XCTAssertTrue(pcm.allSatisfy { $0 == 16384 })
            frame += 480
        }
    }
}

final class AudioTimelineTests: XCTestCase {
    func testFirstPacketGoesWhereItsStampSays() {
        XCTAssertTrue(AudioTimeline.place(next: -1, stamped: 11_328, frames: 480, discontinuity: true, arrival: 0) == (11_328, 0))
    }

    func testBurstWithTheSameStampIsLaidOutInSequence() {
        var next: Int64 = -1
        var starts: [Int64] = []
        for _ in 0..<8 {
            let (at, skip) = AudioTimeline.place(next: next, stamped: 50_000, frames: 480, discontinuity: false, arrival: 0)
            XCTAssertEqual(skip, 0)
            starts.append(at)
            next = at + 480
        }
        XCTAssertEqual(starts, (0..<8).map { 50_000 + Int64($0) * 480 })
    }

    func testJitterIsIgnored() {
        XCTAssertTrue(AudioTimeline.place(next: 10_480, stamped: 11_180, frames: 480, discontinuity: false, arrival: 0) == (10_480, 0))
        XCTAssertTrue(AudioTimeline.place(next: 10_480, stamped: 7_480, frames: 480, discontinuity: false, arrival: 0) == (10_480, 0))
    }

    func testFlaggedGapLeavesSilence() {
        XCTAssertTrue(AudioTimeline.place(next: 10_480, stamped: 200_000, frames: 480, discontinuity: true, arrival: 0) == (200_000, 0))
    }

    func testUnflaggedPauseResyncs() {
        XCTAssertTrue(AudioTimeline.place(next: 130_000, stamped: 200_000, frames: 480, discontinuity: false, arrival: 195_000) == (200_000, 0))
    }

    func testMicrophoneThatStartsLateStreamsOnInsteadOfLeavingGaps() {
        XCTAssertTrue(AudioTimeline.place(next: 29_793, stamped: 29_892, frames: 480, discontinuity: false, arrival: 40_000, canPause: false) == (29_793, 0))
        XCTAssertEqual(AudioTimeline.place(next: 29_793, stamped: 29_892, frames: 480, discontinuity: false, arrival: 40_000).at, 40_000 - 480)
    }

    func testMicrophoneThatLostSamplesCatchesUp() {
        let ahead = 100_000 + AudioTimeline.maxLagFrames + 1
        XCTAssertTrue(AudioTimeline.place(next: 100_000, stamped: ahead, frames: 480, discontinuity: false, arrival: 0, canPause: false) == (ahead, 0))
        XCTAssertTrue(AudioTimeline.place(next: 100_000, stamped: 102_000, frames: 480, discontinuity: false, arrival: 0, canPause: false) == (100_000, 0))
    }

    func testFarBehindDropsTheOverlap() {
        let r = AudioTimeline.place(next: 100_000, stamped: 100_000 - AudioTimeline.driftFrames - 100, frames: 480, discontinuity: false, arrival: 0)
        XCTAssertEqual(r.at, 100_000)
        XCTAssertEqual(r.skip, 480)
    }
}

final class SelectionMathTests: XCTestCase {
    private let work = CGRect(x: 0, y: 25, width: 2560, height: 1390)
    private let bar = CGSize(width: 150, height: 36)

    private func assertVisible(_ p: CGPoint, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertTrue(work.contains(CGRect(origin: p, size: bar)), "bar at \(p) is outside", file: file, line: line)
    }

    func testMoveStaysOnScreen() {
        let r = SelectionMath.move(CGRect(x: 0, y: 0, width: 200, height: 100), by: CGSize(width: -5000, height: 5000),
                                   within: CGRect(x: 0, y: 0, width: 1440, height: 900))
        XCTAssertEqual(r, CGRect(x: 0, y: 800, width: 200, height: 100))
    }

    func testBarBelowTheAreaWhenThereIsRoom() {
        XCTAssertEqual(SelectionMath.placeBar(area: CGRect(x: 600, y: 300, width: 640, height: 400), visible: work, size: bar, gap: 8),
                       CGPoint(x: 1090, y: 708))
    }

    func testBarAboveWhenTheDockIsBelow() {
        XCTAssertEqual(SelectionMath.placeBar(area: CGRect(x: 600, y: 300, width: 640, height: 1080), visible: work, size: bar, gap: 8).y, 256)
    }

    func testBarInsideForAWholeScreen() {
        let p = SelectionMath.placeBar(area: CGRect(x: 0, y: 0, width: 2560, height: 1440), visible: work, size: bar, gap: 8)
        assertVisible(p)
        XCTAssertEqual(p.y, work.maxY - 36 - 8)
    }

    func testBarInsideForAFullSizeWindow() {
        assertVisible(SelectionMath.placeBar(area: work, visible: work, size: bar, gap: 8))
    }
}

final class RecordHotkeyTests: XCTestCase {
    func testDefaultRecordHotkeyIsCaptureHotkeyPlusOption() {
        XCTAssertEqual(HotkeySpec.defaultRecordSpec.display, "⌥⇧⌘2")
        XCTAssertNotEqual(HotkeySpec.defaultRecordSpec, HotkeySpec.defaultSpec)
        XCTAssertTrue(HotkeySpec.defaultRecordSpec.hasRequiredModifier)
    }
}
