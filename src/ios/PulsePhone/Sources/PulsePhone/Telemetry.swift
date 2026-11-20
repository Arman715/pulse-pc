import Foundation

/// Ответ `GET <base>d` программы «Пульс ПК».
/// Любое число может прийти как null — тогда на экране «—».
/// Температуры процессора здесь нет и не должно быть.
struct Telemetry: Decodable, Sendable, Equatable {
    /// Идёт запись игровой сессии.
    var rec: Bool
    /// Название игры.
    var game: String
    /// В игре строгая античит-защита — кадры не считаются.
    var nofps: Bool
    /// Длительность сессии, с.
    var sec: Double?
    /// Кадров в секунду сейчас.
    var fps: Double?
    /// Загрузка процессора, %.
    var cpu: Double?
    /// Загрузка видеокарты, %.
    var gpu: Double?
    /// Температура видеокарты, °C.
    var gpuT: Double?
    /// Видеопамять: занято и всего, ГБ.
    var vram: Double?
    var vramT: Double?
    /// Оперативная память: свободно и всего, ГБ.
    var ramF: Double?
    var ramT: Double?
    /// Пинг, мс.
    var ping: Double?
    /// История кадров за ~2 минуты с шагом 1,5 с.
    var h: [Double?]

    private enum CodingKeys: String, CodingKey {
        case rec, game, nofps, sec, fps, cpu, gpu, gpuT, vram, vramT, ramF, ramT, ping, h
    }

    init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        rec = try c.decodeIfPresent(Bool.self, forKey: .rec) ?? false
        game = try c.decodeIfPresent(String.self, forKey: .game) ?? ""
        nofps = try c.decodeIfPresent(Bool.self, forKey: .nofps) ?? false
        sec = try c.decodeIfPresent(Double.self, forKey: .sec)
        fps = try c.decodeIfPresent(Double.self, forKey: .fps)
        cpu = try c.decodeIfPresent(Double.self, forKey: .cpu)
        gpu = try c.decodeIfPresent(Double.self, forKey: .gpu)
        gpuT = try c.decodeIfPresent(Double.self, forKey: .gpuT)
        vram = try c.decodeIfPresent(Double.self, forKey: .vram)
        vramT = try c.decodeIfPresent(Double.self, forKey: .vramT)
        ramF = try c.decodeIfPresent(Double.self, forKey: .ramF)
        ramT = try c.decodeIfPresent(Double.self, forKey: .ramT)
        ping = try c.decodeIfPresent(Double.self, forKey: .ping)
        h = try c.decodeIfPresent([Double?].self, forKey: .h) ?? []
    }
}

// MARK: - Пороги предупреждений

extension Telemetry {
    static let gpuHotThreshold = 85.0
    static let vramFullThreshold = 0.93
    static let ramLowThreshold = 1.0
    static let pingHighThreshold = 90.0

    var gameTitle: String {
        let title = game.trimmingCharacters(in: .whitespacesAndNewlines)
        return title.isEmpty ? "Игра" : title
    }

    var gpuHot: Bool {
        guard let gpuT else { return false }
        return gpuT >= Self.gpuHotThreshold
    }

    /// Доля занятой видеопамяти, 0…1.
    var vramShare: Double? {
        guard let vram, let vramT, vramT > 0 else { return nil }
        return vram / vramT
    }

    var vramFull: Bool {
        guard let vramShare else { return false }
        return vramShare >= Self.vramFullThreshold
    }

    /// Доля занятой оперативной памяти, 0…1 (полоса показывает занятость, как и остальные).
    var ramUsedShare: Double? {
        guard let ramF, let ramT, ramT > 0 else { return nil }
        return (ramT - ramF) / ramT
    }

    var ramLow: Bool {
        guard let ramF else { return false }
        return ramF < Self.ramLowThreshold
    }

    var pingHigh: Bool {
        guard let ping else { return false }
        return ping >= Self.pingHighThreshold
    }
}

// MARK: - Форматирование

enum Fmt {
    static let russian = Locale(identifier: "ru_RU")

    /// Целое число или «—».
    static func integer(_ value: Double?) -> String {
        guard let value, value.isFinite else { return "—" }
        let clamped = min(max(value, -1_000_000), 1_000_000)
        return String(Int(clamped.rounded()))
    }

    /// Гигабайты: «6,1», «16».
    static func gigabytes(_ value: Double?) -> String {
        guard let value, value.isFinite else { return "—" }
        return value.formatted(.number.precision(.fractionLength(0...1)).locale(russian))
    }

    /// Время сессии: «12:34» или «1:02:33».
    static func duration(_ seconds: Double?) -> String {
        guard let seconds, seconds.isFinite, seconds >= 0 else { return "—" }
        let total = Int(min(seconds, 360_000))
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let secs = total % 60
        if hours > 0 {
            return String(format: "%ld:%02ld:%02ld", hours, minutes, secs)
        }
        return String(format: "%ld:%02ld", minutes, secs)
    }

    /// Процент → доля для полосы.
    static func percentShare(_ value: Double?) -> Double? {
        guard let value, value.isFinite else { return nil }
        return value / 100
    }

    /// «3 с назад», «2 мин назад».
    static func ago(_ interval: TimeInterval) -> String {
        let seconds = max(0, Int(interval))
        if seconds < 60 { return "\(seconds) с назад" }
        return "\(seconds / 60) мин назад"
    }
}
