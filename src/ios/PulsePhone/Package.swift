// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "PulsePhone",
    platforms: [
        .iOS(.v17),
        .macOS(.v14),
    ],
    products: [
        // An xtool project contains exactly one library product: the app itself.
        // xtool links it into the PulsePhone.app bundle (see xtool.yml).
        .library(
            name: "PulsePhone",
            targets: ["PulsePhone"]
        ),
    ],
    targets: [
        .target(
            name: "PulsePhone"
        ),
    ]
)
