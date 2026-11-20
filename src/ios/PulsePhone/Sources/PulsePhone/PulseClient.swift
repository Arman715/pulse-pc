import Foundation

/// Понятные ошибки подключения.
enum PulseError: Error, Equatable, Sendable {
    /// Строка не похожа на адрес панели.
    case badAddress
    /// Компьютер ответил 404: ключ в адресе не подходит.
    case wrongKey
    /// Не удалось достучаться до компьютера.
    case unreachable
    /// Компьютер не ответил за 3 секунды.
    case timeout
    /// Любой другой HTTP-код.
    case httpStatus(Int)
    /// Ответ пришёл, но это не данные «Пульса ПК».
    case badData

    init(_ error: URLError) {
        switch error.code {
        case .timedOut:
            self = .timeout
        default:
            self = .unreachable
        }
    }

    /// Заголовок для экрана подключения.
    var title: String {
        switch self {
        case .badAddress: "Это не похоже на адрес панели"
        case .wrongKey: "Неверный ключ"
        case .unreachable: "Компьютер не отвечает"
        case .timeout: "Время ожидания истекло"
        case .httpStatus(let code): "Компьютер ответил ошибкой \(code)"
        case .badData: "Непонятный ответ"
        }
    }

    /// Подсказка, что проверить.
    var hint: String {
        switch self {
        case .badAddress:
            "Адрес выглядит так: http://192.168.1.6:8765/gbnksz/ — IP-адрес компьютера, порт и ключ из шести букв в конце."
        case .wrongKey:
            "Компьютер найден, но ключ в адресе не подходит. Отсканируйте QR-код заново: ключ мог смениться."
        case .unreachable:
            "Проверьте, что телефон подключён к той же сети Wi-Fi, что и компьютер, программа «Пульс ПК» запущена, а в разделе «Система» → «Панель на телефоне» панель включена. Если iPhone спрашивал про доступ к локальной сети — разрешите его (Настройки → Пульс ПК → Локальная сеть)."
        case .timeout:
            "Компьютер не ответил за 3 секунды. Если iPhone только что спросил про доступ к локальной сети — разрешите и подключитесь ещё раз. Иначе проверьте Wi-Fi и брандмауэр Windows."
        case .httpStatus:
            "Проверьте, что адрес скопирован целиком, вместе с ключом и косой чертой в конце."
        case .badData:
            "По этому адресу отвечает не «Пульс ПК», или версия программы на компьютере устарела."
        }
    }

    /// Короткая строка для плашки на панели.
    var short: String {
        switch self {
        case .badAddress: "неверный адрес"
        case .wrongKey: "ключ не подходит — отсканируйте QR-код заново"
        case .unreachable: "компьютер не отвечает"
        case .timeout: "нет ответа за 3 с"
        case .httpStatus(let code): "ошибка \(code)"
        case .badData: "непонятный ответ"
        }
    }
}

/// Разбор адреса, введённого вручную или из QR-кода.
enum PulseAddress {
    /// Приводит строку к виду `http://host:port/key/`. Возвращает nil, если это не адрес панели.
    static func normalize(_ raw: String) -> URL? {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        let lower = text.lowercased()
        if !lower.hasPrefix("http://") && !lower.hasPrefix("https://") {
            text = "http://" + text
        }

        guard var parts = URLComponents(string: text),
              let scheme = parts.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = parts.host, !host.isEmpty
        else { return nil }

        var segments = parts.path.split(separator: "/").map(String.init)
        // Если вставили адрес самих данных (…/gbnksz/d), отрезаем «d».
        if segments.count >= 2, segments.last == "d" {
            segments.removeLast()
        }
        // Без ключа в пути компьютер всё равно ответит 404.
        guard !segments.isEmpty else { return nil }

        parts.scheme = scheme
        parts.path = "/" + segments.joined(separator: "/") + "/"
        parts.query = nil
        parts.fragment = nil
        return parts.url
    }
}

/// Запросы к компьютеру: эфемерная сессия, без кеша, тайм-аут 3 с.
struct PulseClient: Sendable {
    private let session: URLSession

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 3
        configuration.timeoutIntervalForResource = 3
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.urlCache = nil
        configuration.waitsForConnectivity = false
        configuration.httpMaximumConnectionsPerHost = 2
        session = URLSession(configuration: configuration)
    }

    /// `GET <base>d`. Бросает `PulseError` или `CancellationError`.
    func fetch(from base: URL) async throws -> Telemetry {
        let url = base.appending(path: "d")
        var request = URLRequest(
            url: url,
            cachePolicy: .reloadIgnoringLocalAndRemoteCacheData,
            timeoutInterval: 3
        )
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let payload: (Data, URLResponse)
        do {
            payload = try await session.data(for: request)
        } catch let error as URLError {
            if error.code == .cancelled { throw CancellationError() }
            throw PulseError(error)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw PulseError.unreachable
        }

        guard let response = payload.1 as? HTTPURLResponse else {
            throw PulseError.badData
        }
        switch response.statusCode {
        case 200:
            break
        case 404:
            throw PulseError.wrongKey
        default:
            throw PulseError.httpStatus(response.statusCode)
        }

        do {
            return try JSONDecoder().decode(Telemetry.self, from: payload.0)
        } catch {
            throw PulseError.badData
        }
    }
}
