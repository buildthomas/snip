import Foundation
import CoreGraphics

/// Recording limits as in the Windows app's `RecordingMath`; the bitrate is an average here (Windows uses a quality target).
public enum RecordingMath {
    public static let fps = 30
    public static let maxWidth = 4096, maxHeight = 4096, maxPixels = 4096 * 2304
    public static let minSide = 16

    /// H.264 needs even dimensions; drops the odd row or column.
    public static func even(_ w: Int, _ h: Int) -> (width: Int, height: Int) { (w & ~1, h & ~1) }

    public static func sizeProblem(width w: Int, height h: Int) -> String? {
        if w < minSide || h < minSide { return "The selection is too small to record." }
        if w > maxWidth || h > maxHeight || w * h > maxPixels {
            return "Recording is limited to about 4K (\(maxWidth)×2304). Select a smaller area."
        }
        return nil
    }

    /// Average bitrate for the encoder; frames only arrive when the screen changes, so still screens cost little.
    public static func bitrate(width w: Int, height h: Int) -> Int { min(max(w * h * fps / 40, 500_000), 12_000_000) }

    public static func formatDuration(_ seconds: Double) -> String {
        let s = max(0, Int(seconds))
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
    }

    public static func formatSize(_ bytes: Int64) -> String {
        bytes >= 1 << 20 ? String(format: "%.1f MB", Double(bytes) / 1_048_576) : "\(max(1, bytes / 1024)) KB"
    }
}

/// Sums sources on the recording's timeline (a ring buffer of the next 4 s); anything missing is silence.
public final class AudioMixer {
    public static let rate = 48_000, channels = 2
    private static let windowFrames = rate * 4
    private var mix = [Float](repeating: 0, count: windowFrames * channels)
    private var head = 0
    public private(set) var written: Int64 = 0

    public init() {}

    /// Adds interleaved stereo samples starting at the given frame; late or far-future parts are dropped.
    public func add(_ samples: UnsafeBufferPointer<Float>, firstFrame: Int64) {
        let frames = samples.count / Self.channels
        for i in 0..<frames {
            let at = firstFrame + Int64(i) - written
            if at < 0 { continue }
            if at >= Int64(Self.windowFrames) { break }
            let j = (head + Int(at)) % Self.windowFrames * Self.channels
            mix[j] += samples[i * Self.channels]
            mix[j + 1] += samples[i * Self.channels + 1]
        }
    }

    public func add(_ samples: [Float], firstFrame: Int64) {
        samples.withUnsafeBufferPointer { add($0, firstFrame: firstFrame) }
    }

    /// Takes the mixed audio up to (not including) the given frame as 16-bit PCM.
    public func take(until frame: Int64) -> [Int16] {
        let frames = Int(min(max(frame - written, 0), Int64(Self.windowFrames)))
        var pcm = [Int16](repeating: 0, count: frames * Self.channels)
        for f in 0..<frames {
            let j = (head + f) % Self.windowFrames * Self.channels
            for c in 0..<Self.channels {
                pcm[f * Self.channels + c] = Int16((min(max(mix[j + c], -1), 1) * 32767).rounded())
                mix[j + c] = 0
            }
        }
        head = (head + frames) % Self.windowFrames
        written += Int64(frames)
        return pcm
    }
}

/// Packets follow on from the previous one; the stamp wins on a flagged pause, or an unflagged one from a source that
/// pauses while silent. A microphone streams on, even when it starts late. Same rules as Windows.
public enum AudioTimeline {
    public static let maxLagFrames = Int64(AudioMixer.rate * 150 / 1000)
    public static let driftFrames = Int64(AudioMixer.rate / 5)

    public static func place(next: Int64, stamped: Int64, frames: Int, discontinuity: Bool, arrival: Int64,
                             canPause: Bool = true) -> (at: Int64, skip: Int) {
        let n = Int64(frames)
        if next < 0 || discontinuity && stamped > next { return (stamped, 0) }
        if canPause && next + n < arrival - maxLagFrames { return (max(stamped, arrival - n), 0) }
        if stamped < next - driftFrames { return (next, Int(min(n, next - stamped))) }
        return (next, 0)
    }
}

public enum SelectionMath {
    public static func move(_ r: CGRect, by d: CGSize, within bounds: CGRect) -> CGRect {
        let x = min(max(r.minX + d.width, bounds.minX), max(bounds.minX, bounds.maxX - r.width))
        let y = min(max(r.minY + d.height, bounds.minY), max(bounds.minY, bounds.maxY - r.height))
        return CGRect(x: x, y: y, width: r.width, height: r.height)
    }

    /// Below the area, else above it, else inside its bottom-right corner; always within the visible area.
    /// Top-left origin, y down, like the Windows `RecordingBar.Place`.
    public static func placeBar(area: CGRect, visible work: CGRect, size: CGSize, gap: CGFloat) -> CGPoint {
        var x = area.maxX - size.width, y = area.maxY + gap
        if y + size.height > work.maxY { y = area.minY - gap - size.height }
        if y < work.minY {
            y = min(area.maxY, work.maxY) - size.height - gap
            x = min(area.maxX, work.maxX) - size.width - gap
        }
        return CGPoint(x: min(max(x, work.minX), max(work.minX, work.maxX - size.width)),
                       y: min(max(y, work.minY), max(work.minY, work.maxY - size.height)))
    }
}
