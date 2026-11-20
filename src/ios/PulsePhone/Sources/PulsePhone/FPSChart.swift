import Charts
import SwiftUI

/// Точка истории кадров. `seconds` — сколько секунд назад (≤ 0), `segment` — номер непрерывного отрезка:
/// пропуски (null) разрывают линию.
struct FPSPoint: Identifiable, Sendable {
    let id: Int
    let seconds: Double
    let fps: Double
    let segment: Int
}

struct HistoryStats: Sendable, Equatable {
    let average: Double
    let minimum: Double
    let maximum: Double
}

enum FPSHistory {
    /// Шаг истории на компьютере, с.
    static let step = 1.5

    static func points(from history: [Double?]) -> [FPSPoint] {
        var result: [FPSPoint] = []
        var segment = 0
        var gap = false
        let last = history.count - 1
        for (index, value) in history.enumerated() {
            guard let value, value.isFinite else {
                if !result.isEmpty { gap = true }
                continue
            }
            if gap {
                segment += 1
                gap = false
            }
            result.append(FPSPoint(
                id: index,
                seconds: Double(index - last) * step,
                fps: value,
                segment: segment
            ))
        }
        return result
    }

    static func stats(from history: [Double?]) -> HistoryStats? {
        let values = history.compactMap { $0 }.filter { $0.isFinite }
        guard let minimum = values.min(), let maximum = values.max() else { return nil }
        let average = values.reduce(0, +) / Double(values.count)
        return HistoryStats(average: average, minimum: minimum, maximum: maximum)
    }

    /// Верх шкалы с запасом, кратный удобному шагу.
    static func top(for maximum: Double?) -> Double {
        let raw = max((maximum ?? 60) * 1.12, 30)
        let unit: Double = raw > 240 ? 60 : 30
        return (raw / unit).rounded(.up) * unit
    }

    static func yTicks(top: Double) -> [Double] {
        let tick: Double
        switch top {
        case ...60: tick = 15
        case ...150: tick = 30
        case ...300: tick = 60
        default: tick = 120
        }
        return Array(stride(from: 0, through: top, by: tick))
    }

    static func xTicks(span: Double) -> [Double] {
        Array(stride(from: 0, through: -span, by: -30))
    }

    static func xLabel(_ seconds: Double) -> String {
        if seconds >= 0 { return "сейчас" }
        let ago = Int((-seconds).rounded())
        if ago % 60 == 0 { return "\(ago / 60) мин" }
        return "\(ago) с"
    }
}

/// График кадров: линия тушью, пунктир среднего, последняя точка красным, светлая сетка.
@MainActor
struct FPSChart: View {
    let history: [Double?]

    var body: some View {
        let points = FPSHistory.points(from: history)
        let stats = FPSHistory.stats(from: history)
        let span = max(Double(max(history.count - 1, 1)) * FPSHistory.step, 30)
        let top = FPSHistory.top(for: stats?.maximum)

        if points.count < 2 {
            placeholder
        } else {
            Chart {
                ForEach(points) { point in
                    LineMark(
                        x: .value("Время", point.seconds),
                        y: .value("Кадры", point.fps),
                        series: .value("Отрезок", point.segment)
                    )
                    .foregroundStyle(Palette.ink)
                    .lineStyle(StrokeStyle(lineWidth: 1.6, lineCap: .round, lineJoin: .round))
                    .interpolationMethod(.linear)
                }

                if let stats {
                    RuleMark(y: .value("Среднее", stats.average))
                        .foregroundStyle(Palette.ink3)
                        .lineStyle(StrokeStyle(lineWidth: 1, dash: [4, 3]))
                }

                if let last = points.last {
                    PointMark(
                        x: .value("Время", last.seconds),
                        y: .value("Кадры", last.fps)
                    )
                    .foregroundStyle(Palette.signal)
                    .symbolSize(40)
                }
            }
            .chartXScale(domain: -span...0)
            .chartYScale(domain: 0...top)
            .chartLegend(.hidden)
            .chartXAxis {
                AxisMarks(values: FPSHistory.xTicks(span: span)) { value in
                    AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5))
                        .foregroundStyle(Palette.rule)
                    AxisValueLabel {
                        Text(FPSHistory.xLabel(value.as(Double.self) ?? 0))
                            .font(.figure(11, weight: .regular))
                            .foregroundStyle(Palette.ink4)
                    }
                }
            }
            .chartYAxis {
                AxisMarks(position: .trailing, values: FPSHistory.yTicks(top: top)) { value in
                    AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5))
                        .foregroundStyle(Palette.rule)
                    AxisValueLabel {
                        Text(Fmt.integer(value.as(Double.self)))
                            .font(.figure(11, weight: .regular))
                            .foregroundStyle(Palette.ink4)
                    }
                }
            }
            .accessibilityLabel("График кадров за последние минуты")
        }
    }

    private var placeholder: some View {
        ZStack {
            Rectangle()
                .strokeBorder(Palette.rule, style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
            Text("История кадров появится через несколько секунд")
                .font(.system(size: 13))
                .foregroundStyle(Palette.ink4)
                .multilineTextAlignment(.center)
                .padding(16)
        }
    }
}

/// Подпись под графиком: пунктир — среднее, красная точка — сейчас.
struct FPSLegend: View {
    let stats: HistoryStats?
    let current: Double?

    var body: some View {
        HStack(spacing: 16) {
            HStack(spacing: 6) {
                DashSample()
                    .stroke(Palette.ink3, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                    .frame(width: 18, height: 1)
                Text("сред. \(Fmt.integer(stats?.average))")
            }
            Text("мин. \(Fmt.integer(stats?.minimum))")
            Text("макс. \(Fmt.integer(stats?.maximum))")
            Spacer(minLength: 0)
            HStack(spacing: 6) {
                Circle()
                    .fill(Palette.signal)
                    .frame(width: 6, height: 6)
                Text("сейчас \(Fmt.integer(current))")
            }
        }
        .font(.figure(13, weight: .regular))
        .foregroundStyle(Palette.ink3)
        .lineLimit(1)
    }
}

private struct DashSample: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.midY))
        path.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
        return path
    }
}
