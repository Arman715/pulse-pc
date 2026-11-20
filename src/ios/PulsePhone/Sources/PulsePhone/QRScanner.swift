@preconcurrency import AVFoundation
import SwiftUI
import UIKit

/// Полноэкранный сканер QR-кода с меткой-видоискателем и кнопкой «Отмена».
@MainActor
struct ScannerScreen: View {
    private let onCode: @MainActor (String) -> Void
    private let onUnavailable: @MainActor () -> Void

    @Environment(\.dismiss) private var dismiss

    init(
        onCode: @escaping @MainActor (String) -> Void,
        onUnavailable: @escaping @MainActor () -> Void
    ) {
        self.onCode = onCode
        self.onUnavailable = onUnavailable
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            QRScannerView(onCode: onCode, onFailure: onUnavailable)
                .ignoresSafeArea()

            VStack(spacing: 0) {
                HStack {
                    Caption("Сканирование", color: Palette.paper.opacity(0.8))
                    Spacer()
                    Button("Отмена") { dismiss() }
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(Palette.paper)
                        .frame(minHeight: 44)
                }
                .padding(.horizontal, 20)

                Spacer()

                ViewfinderMarks()
                    .stroke(Palette.paper, style: StrokeStyle(lineWidth: 2, lineCap: .square))
                    .frame(width: 240, height: 240)
                    .accessibilityHidden(true)

                Spacer()

                Text("Наведите камеру на QR-код в окне «Панель на телефоне» на компьютере")
                    .font(.system(size: 15))
                    .foregroundStyle(Palette.paper)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 32)
                    .padding(.bottom, 28)
            }
        }
    }
}

/// Уголки видоискателя — как метки совмещения на чертеже.
struct ViewfinderMarks: Shape {
    var length: CGFloat = 28

    func path(in rect: CGRect) -> Path {
        var path = Path()
        let l = min(length, rect.width / 2, rect.height / 2)
        // Левый верхний
        path.move(to: CGPoint(x: rect.minX, y: rect.minY + l))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.minX + l, y: rect.minY))
        // Правый верхний
        path.move(to: CGPoint(x: rect.maxX - l, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY + l))
        // Правый нижний
        path.move(to: CGPoint(x: rect.maxX, y: rect.maxY - l))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.maxX - l, y: rect.maxY))
        // Левый нижний
        path.move(to: CGPoint(x: rect.minX + l, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.minX, y: rect.maxY - l))
        return path
    }
}

/// Обёртка UIKit-контроллера камеры для SwiftUI.
@MainActor
struct QRScannerView: UIViewControllerRepresentable {
    let onCode: @MainActor (String) -> Void
    let onFailure: @MainActor () -> Void

    func makeUIViewController(context: Context) -> ScannerViewController {
        let controller = ScannerViewController()
        controller.onCode = onCode
        controller.onFailure = onFailure
        return controller
    }

    func updateUIViewController(_ controller: ScannerViewController, context: Context) {
        controller.onCode = onCode
        controller.onFailure = onFailure
    }
}

/// Показывает изображение с камеры и отдаёт первый найденный QR-код.
final class ScannerViewController: UIViewController {
    var onCode: (@MainActor (String) -> Void)?
    var onFailure: (@MainActor () -> Void)?

    private var capture: CaptureController?
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var delivered = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black

        let capture = CaptureController(
            onCode: { [weak self] code in self?.deliver(code) },
            onFailure: { [weak self] in self?.onFailure?() },
            onReady: { [weak self] in self?.updatePreviewRotation() }
        )
        self.capture = capture

        let layer = AVCaptureVideoPreviewLayer(session: capture.session)
        layer.videoGravity = .resizeAspectFill
        layer.frame = view.bounds
        view.layer.addSublayer(layer)
        previewLayer = layer
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
        updatePreviewRotation()
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        capture?.start()
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        capture?.stop()
    }

    /// Поворачивает картинку под ориентацию интерфейса (портрет и оба ландшафта).
    private func updatePreviewRotation() {
        guard let connection = previewLayer?.connection,
              let orientation = view.window?.windowScene?.interfaceOrientation
        else { return }

        let angle: CGFloat
        switch orientation {
        case .landscapeRight: angle = 0
        case .landscapeLeft: angle = 180
        case .portraitUpsideDown: angle = 270
        default: angle = 90
        }
        if connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
    }

    private func deliver(_ code: String) {
        guard !delivered else { return }
        delivered = true
        onCode?(code)
    }
}

/// Сессия камеры. Всё изменяемое состояние живёт на собственной последовательной очереди,
/// поэтому класс помечен `@unchecked Sendable`.
final class CaptureController: NSObject, AVCaptureMetadataOutputObjectsDelegate, @unchecked Sendable {
    let session = AVCaptureSession()

    private let queue = DispatchQueue(label: "ru.pulsepc.phone.capture")
    private let onCode: @MainActor @Sendable (String) -> Void
    private let onFailure: @MainActor @Sendable () -> Void
    private let onReady: @MainActor @Sendable () -> Void

    // Доступ только с `queue`.
    private var configured = false
    private var delivered = false

    init(
        onCode: @escaping @MainActor @Sendable (String) -> Void,
        onFailure: @escaping @MainActor @Sendable () -> Void,
        onReady: @escaping @MainActor @Sendable () -> Void
    ) {
        self.onCode = onCode
        self.onFailure = onFailure
        self.onReady = onReady
        super.init()
    }

    func start() {
        queue.async { [self] in
            if !configured {
                guard configure() else {
                    let failure = onFailure
                    Task { @MainActor in failure() }
                    return
                }
                configured = true
            }
            delivered = false
            if !session.isRunning {
                session.startRunning()
            }
            let ready = onReady
            Task { @MainActor in ready() }
        }
    }

    func stop() {
        queue.async { [self] in
            if session.isRunning {
                session.stopRunning()
            }
        }
    }

    /// Вызывается на `queue`.
    private func configure() -> Bool {
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device)
        else { return false }

        session.beginConfiguration()
        defer { session.commitConfiguration() }

        guard session.canAddInput(input) else { return false }
        session.addInput(input)

        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else { return false }
        session.addOutput(output)

        output.setMetadataObjectsDelegate(self, queue: queue)
        guard output.availableMetadataObjectTypes.contains(.qr) else { return false }
        output.metadataObjectTypes = [.qr]
        return true
    }

    // MARK: AVCaptureMetadataOutputObjectsDelegate — приходит на `queue`.

    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        guard !delivered else { return }
        for object in metadataObjects {
            guard let readable = object as? AVMetadataMachineReadableCodeObject,
                  readable.type == .qr,
                  let value = readable.stringValue,
                  !value.isEmpty
            else { continue }

            delivered = true
            let handler = onCode
            Task { @MainActor in handler(value) }
            // Останавливаем после возврата из колбэка, чтобы не блокировать доставку.
            queue.async { [self] in
                if session.isRunning {
                    session.stopRunning()
                }
            }
            return
        }
    }
}
