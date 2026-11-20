import Foundation
import Observation
import UIKit

/// Состояние приложения: сохранённый адрес, последние данные и опрос компьютера.
@MainActor
@Observable
final class PulseModel {
    enum Link: Equatable {
        /// Данных ещё не было.
        case waiting
        /// Последний запрос удался.
        case live
        /// Связь потеряна, идут повторы.
        case lost
    }

    /// Шаг опроса, с.
    static let pollInterval = 1.5
    /// Предел паузы между повторами при потере связи, с.
    static let maxBackoff = 5.0

    private(set) var baseURL: URL? = nil
    private(set) var isEditingAddress = false
    private(set) var keepScreenOn = true

    private(set) var telemetry: Telemetry? = nil
    private(set) var link: Link = .waiting
    /// Счётчик удачных обновлений — по нему мигает красная точка.
    private(set) var beat = 0
    private(set) var lastSuccess: Date? = nil
    private(set) var lastError: PulseError? = nil

    private let client = PulseClient()
    @ObservationIgnored private var panelOnScreen = false

    private enum Keys {
        static let address = "pulse.baseURL"
        static let keepScreenOn = "pulse.keepScreenOn"
    }

    init() {
        let defaults = UserDefaults.standard
        if let saved = defaults.string(forKey: Keys.address) {
            baseURL = PulseAddress.normalize(saved)
        }
        if defaults.object(forKey: Keys.keepScreenOn) != nil {
            keepScreenOn = defaults.bool(forKey: Keys.keepScreenOn)
        }
    }

    var showsConnectScreen: Bool {
        baseURL == nil || isEditingAddress
    }

    /// «192.168.1.6» для шапки.
    var hostLabel: String {
        guard let baseURL, let host = baseURL.host() else { return "" }
        return host
    }

    // MARK: - Подключение

    /// Проверяет адрес запросом `<base>d` и сохраняет его. Возвращает ошибку или nil при успехе.
    func connect(to raw: String) async -> PulseError? {
        guard let url = PulseAddress.normalize(raw) else { return .badAddress }

        let first: Telemetry
        do {
            first = try await client.fetch(from: url)
        } catch let error as PulseError {
            return error
        } catch {
            return .unreachable
        }

        if url != baseURL {
            beat = 0
        }
        baseURL = url
        UserDefaults.standard.set(url.absoluteString, forKey: Keys.address)
        telemetry = first
        link = .live
        lastSuccess = Date()
        lastError = nil
        isEditingAddress = false
        return nil
    }

    func beginEditingAddress() {
        isEditingAddress = true
    }

    func cancelEditingAddress() {
        isEditingAddress = false
    }

    // MARK: - Опрос

    /// Опрашивает компьютер, пока задачу не отменят (уход с панели или в фон).
    /// Раз в 1,5 с; при обрыве — повторы с нарастающей паузой до 5 с.
    func runPolling() async {
        guard let base = baseURL else { return }
        var failures = 0

        while !Task.isCancelled {
            let started = ContinuousClock.now
            do {
                let fresh = try await client.fetch(from: base)
                // Адрес могли сменить, пока шёл запрос.
                guard base == baseURL else { return }
                telemetry = fresh
                link = .live
                beat &+= 1
                lastSuccess = Date()
                lastError = nil
                failures = 0
            } catch is CancellationError {
                return
            } catch {
                if Task.isCancelled { return }
                failures += 1
                lastError = (error as? PulseError) ?? .unreachable
                // Одна потерянная посылка — ещё не обрыв.
                if failures >= 2 || telemetry == nil {
                    link = .lost
                }
            }

            let pause = failures == 0
                ? Self.pollInterval
                : min(Self.maxBackoff, Self.pollInterval + Double(failures - 1))
            try? await Task.sleep(until: started + .seconds(pause), clock: .continuous)
        }
    }

    // MARK: - Экран

    func setKeepScreenOn(_ value: Bool) {
        keepScreenOn = value
        UserDefaults.standard.set(value, forKey: Keys.keepScreenOn)
        applyIdleTimer()
    }

    func setPanelVisible(_ visible: Bool) {
        panelOnScreen = visible
        applyIdleTimer()
    }

    private func applyIdleTimer() {
        UIApplication.shared.isIdleTimerDisabled = keepScreenOn && panelOnScreen
    }
}
