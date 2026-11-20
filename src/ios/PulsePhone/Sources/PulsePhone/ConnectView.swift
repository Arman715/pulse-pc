@preconcurrency import AVFoundation
import SwiftUI
import UIKit

/// Экран «Подключение»: инструкция, сканер QR-кода и поле адреса.
@MainActor
struct ConnectView: View {
    @Environment(PulseModel.self) private var model
    @Environment(\.openURL) private var openURL

    @State private var address = ""
    @State private var checking = false
    @State private var error: PulseError?
    @State private var showScanner = false
    @State private var camera: CameraProblem?
    @State private var scans = 0
    @FocusState private var addressFocused: Bool

    enum CameraProblem: Equatable {
        case denied
        case unavailable
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                header
                Hairline()
                steps
                Hairline()
                scanSection
                Hairline()
                addressSection
                if let error {
                    ErrorNote(title: error.title, text: error.hint)
                        .padding(.bottom, 16)
                }
                connectButton
                if model.baseURL != nil {
                    Button("Отмена") { model.cancelEditingAddress() }
                        .buttonStyle(BlueprintButtonStyle())
                        .padding(.top, 10)
                }
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 32)
            .frame(maxWidth: 560)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .background(Palette.paper.ignoresSafeArea())
        .sensoryFeedback(.success, trigger: scans)
        .onAppear {
            if address.isEmpty, let saved = model.baseURL {
                address = saved.absoluteString
            }
        }
        .fullScreenCover(isPresented: $showScanner) {
            ScannerScreen(
                onCode: { code in handleScanned(code) },
                onUnavailable: {
                    showScanner = false
                    camera = .unavailable
                }
            )
        }
    }

    // MARK: - Части экрана

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            Caption("Пульс ПК · для iPhone")
            Text("Подключение")
                .font(.figure(40, weight: .semibold))
                .foregroundStyle(Palette.ink)
        }
        .padding(.top, 28)
        .padding(.bottom, 18)
    }

    private var steps: some View {
        VStack(alignment: .leading, spacing: 14) {
            StepLine(number: "1", text: "На компьютере откройте «Пульс ПК» → «Система» → «Панель на телефоне» и включите панель.")
            StepLine(number: "2", text: "Телефон и компьютер должны быть в одной сети Wi-Fi.")
            StepLine(number: "3", text: "Отсканируйте QR-код из окна программы или введите адрес, который написан под ним.")
        }
        .padding(.vertical, 20)
    }

    private var scanSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button { startScan() } label: {
                Label("Сканировать QR-код", systemImage: "qrcode.viewfinder")
            }
            .buttonStyle(BlueprintButtonStyle())
            .disabled(checking)

            if let camera {
                cameraNote(camera)
            }
        }
        .padding(.vertical, 20)
    }

    private var addressSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Caption("Или введите адрес")
            TextField(
                "Адрес",
                text: $address,
                prompt: Text(verbatim: "http://192.168.1.6:8765/ключ/").foregroundStyle(Palette.ink4)
            )
            .font(.system(size: 17, weight: .regular, design: .monospaced))
            .foregroundStyle(Palette.ink)
            .keyboardType(.URL)
            .textContentType(.URL)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .submitLabel(.go)
            .focused($addressFocused)
            .onSubmit { connect() }
            .padding(.vertical, 10)
            Hairline(color: addressFocused ? Palette.ink : Palette.rule)
        }
        .padding(.top, 20)
        .padding(.bottom, 20)
    }

    private var connectButton: some View {
        Button { connect() } label: {
            HStack(spacing: 10) {
                if checking {
                    ProgressView()
                        .tint(Palette.paper)
                    Text("Проверяю…")
                } else {
                    Text("Подключиться")
                }
            }
        }
        .buttonStyle(BlueprintButtonStyle(kind: .primary))
        .disabled(address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
    }

    @ViewBuilder
    private func cameraNote(_ problem: CameraProblem) -> some View {
        switch problem {
        case .denied:
            VStack(alignment: .leading, spacing: 10) {
                ErrorNote(
                    title: "Нет доступа к камере",
                    text: "Разрешите его в Настройках → Пульс ПК → Камера или введите адрес вручную."
                )
                Button("Открыть настройки") {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        openURL(url)
                    }
                }
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Palette.ink)
            }
        case .unavailable:
            ErrorNote(
                title: "Камера недоступна",
                text: "Не удалось включить камеру. Введите адрес вручную — он написан под QR-кодом."
            )
        }
    }

    // MARK: - Действия

    private func startScan() {
        error = nil
        camera = nil
        addressFocused = false
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            showScanner = true
        case .notDetermined:
            Task {
                let granted = await AVCaptureDevice.requestAccess(for: .video)
                if granted {
                    showScanner = true
                } else {
                    camera = .denied
                }
            }
        case .denied, .restricted:
            camera = .denied
        @unknown default:
            camera = .denied
        }
    }

    private func handleScanned(_ code: String) {
        showScanner = false
        scans += 1
        address = code.trimmingCharacters(in: .whitespacesAndNewlines)
        connect()
    }

    private func connect() {
        guard !checking else { return }
        let raw = address
        guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        addressFocused = false
        error = nil
        camera = nil
        checking = true
        Task {
            let result = await model.connect(to: raw)
            checking = false
            error = result
        }
    }
}

/// Пункт инструкции с номером в узких цифрах.
private struct StepLine: View {
    let number: String
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            Text(number)
                .font(.figure(20, weight: .semibold))
                .foregroundStyle(Palette.ink3)
                .frame(width: 16, alignment: .leading)
            Text(text)
                .font(.system(size: 16))
                .foregroundStyle(Palette.ink)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// Предупреждение охрой со штриховкой: заголовок и подсказка.
struct ErrorNote: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Palette.ochre)
            Text(text)
                .font(.system(size: 14))
                .foregroundStyle(Palette.ink2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.leading, 18)
        .overlay(alignment: .leading) {
            Hatch(step: 3)
                .stroke(Palette.ochre, lineWidth: 1)
                .frame(width: 6)
                .clipped()
                .accessibilityHidden(true)
        }
        .accessibilityElement(children: .combine)
    }
}
