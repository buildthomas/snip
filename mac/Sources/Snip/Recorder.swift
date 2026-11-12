import AppKit
import AVFoundation
import CoreImage
import ScreenCaptureKit
import SnipCore

struct RecorderError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct RecordingResult {
    let url: URL
    let duration: Double
    let bytes: Int64
    let lastFrame: CGImage?
    let stopReason: String?
}

/// Recordings wait in the per-user temp folder until they're saved or discarded; leftovers go at the next launch.
enum RecordingFiles {
    static var directory: URL { FileManager.default.temporaryDirectory.appendingPathComponent("Snip", isDirectory: true) }

    static func newURL() throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let name = "Snip \(f.string(from: Date()))"
        var i = 1
        while true {
            let url = directory.appendingPathComponent(i == 1 ? "\(name).mp4" : "\(name) (\(i)).mp4")
            if !fm.fileExists(atPath: url.path) { return url }
            i += 1
        }
    }

    static func cleanUp() {
        let fm = FileManager.default
        let values = try? directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
        guard values?.isDirectory == true, values?.isSymbolicLink != true,
              let files = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else { return }
        for f in files where f.pathExtension == "mp4" { _ = delete(f) }
    }

    /// Returns the error, or nil when the file is gone.
    @discardableResult
    static func delete(_ url: URL) -> String? {
        do {
            if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
            return nil
        } catch {
            Log.error("couldn't delete \(url.lastPathComponent): \(error)")
            return error.localizedDescription
        }
    }
}

/// Records an area of one display to H.264/AAC MP4: ScreenCaptureKit for frames and system sound, AVCaptureSession for
/// the microphone, AVAssetWriter (hardware encoder) for the file. Snip's own windows and sounds are excluded.
final class ScreenRecorder: NSObject, SCStreamOutput, SCStreamDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    let screen: NSScreen
    let area: CGRect
    let url: URL
    let systemSound: Bool
    let microphone: Bool
    private let displayID: CGDirectDisplayID?
    private let scale: CGFloat

    var onStarted: (() -> Void)?
    var onFailed: ((String) -> Void)?
    var onAutoStopped: ((String) -> Void)?

    private let queue = DispatchQueue(label: "snip.recorder")
    private var stream: SCStream?
    private var mic: AVCaptureSession?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var audioFormat: CMAudioFormatDescription?
    private let mixer = AudioMixer()
    private var converters: [Int: AudioConverter] = [:]
    private var nextFrame: [Int: Int64] = [:]
    private var sessionStart: CMTime?
    private var startHost = CMTime.zero
    private var lastPTS = CMTime.zero
    private var lastArrival = CMTime.zero
    private var lastSample: CMSampleBuffer?
    private var audioError: String?
    private var micObserver: NSObjectProtocol?
    private var frames = 0
    private var timer: DispatchSourceTimer?
    private var ticks = 0
    private var stopping = false
    private var failed = false
    private var stopReason: String?

    private static let systemSource = 0, micSource = 1

    init(screen: NSScreen, area: CGRect, url: URL, systemSound: Bool, microphone: Bool) {
        self.screen = screen
        self.area = area
        self.url = url
        self.systemSound = systemSound
        self.microphone = microphone
        displayID = Capture.displayID(of: screen)
        scale = screen.backingScaleFactor
    }

    private static var hostNow: CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    // MARK: - Start

    func start() {
        let t = DispatchTime.now()
        // Not only on-screen windows: the border and bar may not be registered yet, and Snip must be found to be excluded.
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: false) { [weak self] content, error in
            guard let self else { return }
            self.queue.async {
                do {
                    guard let content else { throw error ?? RecorderError("Screen content isn't available.") }
                    try self.setUp(content)
                    Log.debug("recording setup \(Log.ms(since: t)) ms")
                    let stream = self.stream
                    stream?.startCapture { error in
                        self.queue.async {
                            if let error { self.fail("Couldn't start capturing: \(error.localizedDescription)"); return }
                            Log.debug("capture started \(Log.ms(since: t)) ms after start")
                            // Stopped while starting: make sure capture doesn't keep running.
                            if self.stopping || self.failed { stream?.stopCapture(completionHandler: nil) }
                        }
                    }
                } catch {
                    self.fail(error.localizedDescription)
                }
            }
        }
    }

    private func setUp(_ content: SCShareableContent) throws {
        guard !stopping else { return }
        guard let id = displayID, let display = content.displays.first(where: { $0.displayID == id }) else {
            throw RecorderError("The display is no longer available.")
        }
        let own = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        if own.isEmpty { Log.error("Snip isn't in the shareable content; its windows rely on sharingType to stay out of the video") }
        let filter = SCContentFilter(display: display, excludingApplications: own, exceptingWindows: [])
        let size = RecordingMath.even(Int((area.width * scale).rounded()), Int((area.height * scale).rounded()))
        if let problem = RecordingMath.sizeProblem(width: size.width, height: size.height) { throw RecorderError(problem) }

        let config = SCStreamConfiguration()
        config.sourceRect = area
        config.width = size.width
        config.height = size.height
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(RecordingMath.fps))
        config.showsCursor = true
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.colorSpaceName = CGColorSpace.sRGB
        config.queueDepth = 6
        config.capturesAudio = systemSound
        config.sampleRate = AudioMixer.rate
        config.channelCount = AudioMixer.channels
        config.excludesCurrentProcessAudio = true

        try setUpWriter(width: size.width, height: size.height)
        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if systemSound { try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        self.stream = stream
        if microphone { try startMicrophone() }
        Log.info("recording \(size.width)x\(size.height) of display \(id), sound: system=\(systemSound) mic=\(microphone)")
    }

    private func setUpWriter(width: Int, height: Int) throws {
        RecordingFiles.delete(url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: RecordingMath.bitrate(width: width, height: height),
                AVVideoMaxKeyFrameIntervalKey: RecordingMath.fps * 10,
                AVVideoMaxKeyFrameIntervalDurationKey: 10,
                AVVideoExpectedSourceFrameRateKey: RecordingMath.fps,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
                AVVideoAllowFrameReorderingKey: false,
            ],
        ])
        video.expectsMediaDataInRealTime = true
        guard writer.canAdd(video) else { throw RecorderError("The video encoder isn't available.") }
        writer.add(video)
        videoInput = video

        if systemSound || microphone {
            var asbd = AudioStreamBasicDescription(
                mSampleRate: Float64(AudioMixer.rate), mFormatID: kAudioFormatLinearPCM,
                mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
                mBytesPerPacket: UInt32(2 * AudioMixer.channels), mFramesPerPacket: 1, mBytesPerFrame: UInt32(2 * AudioMixer.channels),
                mChannelsPerFrame: UInt32(AudioMixer.channels), mBitsPerChannel: 16, mReserved: 0)
            var format: CMAudioFormatDescription?
            CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: &asbd, layoutSize: 0, layout: nil, magicCookieSize: 0,
                                           magicCookie: nil, extensions: nil, formatDescriptionOut: &format)
            let audio = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: AudioMixer.rate,
                AVNumberOfChannelsKey: AudioMixer.channels,
                AVEncoderBitRateKey: 96_000,
            ], sourceFormatHint: format)
            audio.expectsMediaDataInRealTime = true
            guard writer.canAdd(audio) else { throw RecorderError("The sound encoder isn't available.") }
            writer.add(audio)
            audioInput = audio
            audioFormat = format
        }
        guard writer.startWriting() else { throw writer.error ?? RecorderError("Couldn't create the video file.") }
        self.writer = writer
    }

    private func startMicrophone() throws {
        guard let device = AVCaptureDevice.default(for: .audio) else { throw RecorderError("No microphone found.") }
        let session = AVCaptureSession()
        let input = try AVCaptureDeviceInput(device: device)
        let output = AVCaptureAudioDataOutput()
        guard session.canAddInput(input), session.canAddOutput(output) else { throw RecorderError("The microphone can't be used right now.") }
        session.addInput(input)
        session.addOutput(output)
        output.setSampleBufferDelegate(self, queue: queue)
        micObserver = NotificationCenter.default.addObserver(forName: .AVCaptureSessionRuntimeError, object: session, queue: nil) { [weak self] note in
            let error = note.userInfo?[AVCaptureSessionErrorKey] as? Error
            self?.queue.async { self?.audioError = self?.audioError ?? "Microphone: \(error?.localizedDescription ?? "stopped working")" }
        }
        session.startRunning()
        mic = session
    }

    // MARK: - Samples (all on `queue`)

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard !stopping, !failed, sampleBuffer.isValid else { return }
        switch type {
        case .screen: handleFrame(sampleBuffer)
        case .audio: handleAudio(sampleBuffer, source: Self.systemSource, pts: sampleBuffer.presentationTimeStamp)
        default: break
        }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard !stopping, !failed, sampleBuffer.isValid else { return }
        var pts = sampleBuffer.presentationTimeStamp
        if let clock = mic?.synchronizationClock { pts = CMSyncConvertTime(pts, from: clock, to: CMClockGetHostTimeClock()) }
        handleAudio(sampleBuffer, source: Self.micSource, pts: pts)
    }

    private func handleFrame(_ sb: CMSampleBuffer) {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete,
              sb.imageBuffer != nil, let writer, let videoInput else { return }
        let pts = sb.presentationTimeStamp
        if sessionStart == nil {
            writer.startSession(atSourceTime: pts)
            sessionStart = pts
            startHost = Self.hostNow
            startTimer()
            let started = onStarted
            DispatchQueue.main.async { started?() }
        }
        guard videoInput.isReadyForMoreMediaData else { return }
        if !videoInput.append(sb) {
            fail("Couldn't encode the video: \(writer.error?.localizedDescription ?? "unknown error")")
            return
        }
        frames += 1
        lastPTS = pts
        lastArrival = Self.hostNow
        lastSample = sb
    }

    /// Frames only arrive when the screen changes; like Windows, a still screen gets the last frame again every second.
    private func keepAlive() {
        guard let last = lastSample, let videoInput, videoInput.isReadyForMoreMediaData else { return }
        let gap = Self.hostNow - lastArrival
        guard gap.seconds >= 1 else { return }
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: lastPTS + gap, decodeTimeStamp: .invalid)
        var copy: CMSampleBuffer?
        guard CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: last, sampleTimingEntryCount: 1,
                                                    sampleTimingArray: &timing, sampleBufferOut: &copy) == noErr, let copy else { return }
        if videoInput.append(copy) {
            lastPTS = timing.presentationTimeStamp
            lastArrival = Self.hostNow
        }
    }

    private func handleAudio(_ sb: CMSampleBuffer, source: Int, pts: CMTime) {
        guard let start = sessionStart, audioInput != nil else { return }
        let converter = converters[source] ?? AudioConverter()
        converters[source] = converter
        guard let samples = converter.convert(sb) else {
            audioError = audioError ?? "Some sound couldn't be read."
            return
        }
        guard !samples.isEmpty else { return }
        let count = samples.count / AudioMixer.channels
        let rate = Double(AudioMixer.rate)
        let arrival = Int64(((Self.hostNow - startHost).seconds * rate).rounded())
        var stamped = Int64(((pts - start).seconds * rate).rounded())
        // Should be the same clock; if a source ever stamps on another one, its arrival time is the best guess.
        if abs(stamped - arrival) > Int64(rate) { stamped = arrival - Int64(count) }
        let (at, skip) = AudioTimeline.place(next: nextFrame[source] ?? -1, stamped: stamped, frames: count, discontinuity: false, arrival: arrival)
        nextFrame[source] = at + Int64(count)
        samples.withUnsafeBufferPointer { p in
            mixer.add(UnsafeBufferPointer(rebasing: p[(skip * AudioMixer.channels)...]), firstFrame: at + Int64(skip))
        }
    }

    private func startTimer() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + .milliseconds(100), repeating: .milliseconds(100))
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        timer = t
    }

    private func tick() {
        guard !stopping, !failed else { return }
        let behind = Int64(AudioMixer.rate / 5)
        writeAudio(until: Int64(((Self.hostNow - startHost).seconds * Double(AudioMixer.rate)).rounded()) - behind)
        keepAlive()
        ticks += 1
        if ticks % 20 == 0, let free = try? RecordingFiles.directory.resourceValues(forKeys: [.volumeAvailableCapacityKey])
            .volumeAvailableCapacity, free < 1 << 30 {
            autoStop("Recording stopped: less than 1 GB of disk space left.")
        }
    }

    private func writeAudio(until frame: Int64) {
        guard let start = sessionStart, let audioInput, let audioFormat, frame > mixer.written else { return }
        guard audioInput.isReadyForMoreMediaData else { return }
        let first = mixer.written
        let pcm = mixer.take(until: frame)
        let count = pcm.count / AudioMixer.channels
        guard count > 0 else { return }
        let length = pcm.count * MemoryLayout<Int16>.size
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: length, blockAllocator: kCFAllocatorDefault,
                                                 customBlockSource: nil, offsetToData: 0, dataLength: length, flags: kCMBlockBufferAssureMemoryNowFlag,
                                                 blockBufferOut: &block) == noErr, let block else { return }
        _ = pcm.withUnsafeBytes { CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: length) }
        var sample: CMSampleBuffer?
        let pts = start + CMTime(value: first, timescale: CMTimeScale(AudioMixer.rate))
        guard CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: audioFormat,
                                                                   sampleCount: count, presentationTimeStamp: pts, packetDescriptions: nil,
                                                                   sampleBufferOut: &sample) == noErr, let sample else { return }
        if !audioInput.append(sample) {
            Log.error("audio append failed: \(writer?.error.map { "\($0)" } ?? "?")")
            audioError = audioError ?? "The sound couldn't be encoded."
        }
    }

    // MARK: - Failure and stop

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { [weak self] in
            guard let self, !self.stopping else { return }
            if self.sessionStart == nil { self.fail(error.localizedDescription) }
            else { self.autoStop("Recording stopped: \(error.localizedDescription)") }
        }
    }

    private func autoStop(_ reason: String) {
        guard stopReason == nil else { return }
        stopReason = reason
        Log.info(reason)
        let handler = onAutoStopped
        DispatchQueue.main.async { handler?(reason) }
    }

    /// Before the first frame: tears everything down, deletes the file and reports.
    private func fail(_ message: String) {
        guard !failed, !stopping else { return }
        if sessionStart != nil {
            // The writer is broken; stop feeding it. Stopping then reports why and removes the file.
            failed = true
            teardown()
            autoStop("Recording stopped: \(message)")
            return
        }
        failed = true
        Log.error("recording failed to start: \(message)")
        teardown()
        writer?.cancelWriting()
        RecordingFiles.delete(url)
        let handler = onFailed
        DispatchQueue.main.async { handler?(message) }
    }

    private func stopMicrophone() {
        mic?.stopRunning()
        mic = nil
        if let micObserver { NotificationCenter.default.removeObserver(micObserver) }
        micObserver = nil
    }

    private func teardown() {
        timer?.cancel()
        timer = nil
        stopMicrophone()
        stream?.stopCapture(completionHandler: nil)
        stream = nil
    }

    /// Finishes the file; calls back on the main thread.
    func stop(completion: @escaping (Result<RecordingResult, Error>) -> Void) {
        queue.async { [self] in
            guard !stopping else { return }
            stopping = true
            let stopHost = Self.hostNow
            timer?.cancel()
            timer = nil
            stopMicrophone()
            let finish = { self.queue.async { self.finalize(stopHost: stopHost, completion) } }
            if let stream, !failed { stream.stopCapture { _ in finish() } } else { finish() }
        }
    }

    private func finalize(stopHost: CMTime, _ completion: @escaping (Result<RecordingResult, Error>) -> Void) {
        func done(_ r: Result<RecordingResult, Error>) { DispatchQueue.main.async { completion(r) } }
        stream = nil
        guard let writer, let start = sessionStart, frames > 0, !failed, writer.status == .writing else {
            writer?.cancelWriting()
            RecordingFiles.delete(url)
            done(.failure(writer?.error ?? RecorderError(stopReason ?? "The recording produced no video.")))
            return
        }
        let end = lastPTS + CMTimeMaximum(CMTime(value: 1, timescale: CMTimeScale(RecordingMath.fps)), stopHost - lastArrival)
        writeAudio(until: Int64(((end - start).seconds * Double(AudioMixer.rate)).rounded()))
        videoInput?.markAsFinished()
        audioInput?.markAsFinished()
        writer.endSession(atSourceTime: end)
        let thumbnail = lastSample?.imageBuffer.flatMap { Self.image(from: $0) }
        lastSample = nil
        let reason = stopReason ?? audioError.map { "Sound: \($0)" }
        writer.finishWriting { [url] in
            guard writer.status == .completed else {
                RecordingFiles.delete(url)
                done(.failure(writer.error ?? RecorderError("Couldn't finish the video.")))
                return
            }
            let bytes = ((try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.int64Value ?? 0
            Log.info("recording finished: \(self.frames) frames, \(String(format: "%.1f", (end - start).seconds)) s, \(bytes) bytes")
            done(.success(RecordingResult(url: url, duration: (end - start).seconds, bytes: bytes, lastFrame: thumbnail, stopReason: reason)))
        }
    }

    /// Stops right away and deletes the file (discard, or Snip quitting).
    func abort() {
        queue.sync {
            stopping = true
            teardown()
            writer?.cancelWriting()
            writer = nil
            lastSample = nil
        }
        RecordingFiles.delete(url)
    }

    private static func image(from pixels: CVPixelBuffer) -> CGImage? {
        let image = CIImage(cvPixelBuffer: pixels)
        return CIContext(options: [.useSoftwareRenderer: false]).createCGImage(image, from: image.extent)
    }
}

/// Converts any PCM sample buffer to interleaved 48 kHz stereo float.
final class AudioConverter {
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?
    private var monoIn = false

    func convert(_ sb: CMSampleBuffer) -> [Float]? {
        guard let desc = sb.formatDescription else { return nil }
        let format = AVAudioFormat(cmAudioFormatDescription: desc)
        let frames = AVAudioFrameCount(sb.numSamples)
        guard frames > 0, let input = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return nil }
        input.frameLength = frames
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sb, at: 0, frameCount: Int32(frames), into: input.mutableAudioBufferList) == noErr else { return nil }
        if format != inputFormat {
            inputFormat = format
            monoIn = format.channelCount == 1
            guard let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(AudioMixer.rate),
                                             channels: monoIn ? 1 : AVAudioChannelCount(AudioMixer.channels), interleaved: true) else { return nil }
            converter = AVAudioConverter(from: format, to: target)
            converter?.downmix = true
        }
        guard let converter else { return nil }
        let capacity = AVAudioFrameCount(Double(frames) * Double(AudioMixer.rate) / format.sampleRate) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: capacity) else { return nil }
        var fed = false
        var error: NSError?
        _ = converter.convert(to: out, error: &error) { _, status in
            if fed { status.pointee = .noDataNow; return nil }
            fed = true
            status.pointee = .haveData
            return input
        }
        guard error == nil, let data = out.floatChannelData?[0] else { return nil }
        let n = Int(out.frameLength)
        if !monoIn { return Array(UnsafeBufferPointer(start: data, count: n * AudioMixer.channels)) }
        var stereo = [Float](repeating: 0, count: n * 2)
        for i in 0..<n { stereo[i * 2] = data[i]; stereo[i * 2 + 1] = data[i] }
        return stereo
    }
}
