// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Snip",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "Snip", targets: ["Snip"]),
    ],
    targets: [
        // Pure logic (geometry, redaction), no AppKit: unit tested on CI.
        .target(name: "SnipCore"),
        .executableTarget(name: "Snip", dependencies: ["SnipCore"]),
        .testTarget(name: "SnipCoreTests", dependencies: ["SnipCore"]),
    ]
)
