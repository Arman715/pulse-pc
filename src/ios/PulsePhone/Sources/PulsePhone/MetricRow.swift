import SwiftUI

/// Строка показателя: подпись, число, тонкая шкала и, если нужно, предупреждение охрой.
struct MetricRow: View {
    let title: String
    let figure: String
    let unit: String
    var extra: String? = nil
    var figureWarns = false
    var extraWarns = false
    let share: Double?
    var warning: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Caption(title, color: warning == nil ? Palette.ink3 : Palette.ochre)
                Spacer(minLength: 8)
                Text(figure)
                    .font(.figure(30, weight: .medium))
                    .foregroundStyle(figureWarns ? Palette.ochre : Palette.ink)
                Text(unit)
                    .font(.figure(15, weight: .regular))
                    .foregroundStyle(Palette.ink3)
                if let extra {
                    Text(extra)
                        .font(.figure(17, weight: extraWarns ? .semibold : .regular))
                        .foregroundStyle(extraWarns ? Palette.ochre : Palette.ink2)
                }
            }
            .lineLimit(1)

            ScaleBar(share: share, warning: warning != nil)

            if let warning {
                HStack(spacing: 8) {
                    HatchMarker(size: 10)
                    Text(warning)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Palette.ochre)
                }
            }
        }
        .padding(.vertical, 12)
        .accessibilityElement(children: .combine)
    }
}

/// Шкала: волосяная линия с делениями по четвертям и полоса заполнения.
struct ScaleBar: View {
    let share: Double?
    let warning: Bool

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let filled = CGFloat(min(max(share ?? 0, 0), 1))
            ZStack(alignment: .leading) {
                Rectangle()
                    .fill(Palette.rule)
                    .frame(height: 1)
                ForEach(1..<4, id: \.self) { quarter in
                    Rectangle()
                        .fill(Palette.rule)
                        .frame(width: 1, height: 6)
                        .offset(x: width * CGFloat(quarter) / 4)
                }
                if share != nil {
                    Rectangle()
                        .fill(warning ? Palette.ochre : Palette.ink)
                        .frame(width: width * filled, height: 3)
                }
            }
            .frame(width: width, height: geo.size.height, alignment: .leading)
        }
        .frame(height: 6)
        .accessibilityHidden(true)
    }
}

/// Пять строк показателей: процессор, видеокарта, видеопамять, память, пинг.
/// Температуру процессора не показываем.
struct MetricsList: View {
    let telemetry: Telemetry?

    var body: some View {
        let t = telemetry
        let gpuHot = t?.gpuHot ?? false
        let vramFull = t?.vramFull ?? false
        let ramLow = t?.ramLow ?? false
        let pingHigh = t?.pingHigh ?? false

        VStack(spacing: 0) {
            MetricRow(
                title: "Загрузка процессора",
                figure: Fmt.integer(t?.cpu),
                unit: "%",
                share: Fmt.percentShare(t?.cpu)
            )
            Hairline()
            MetricRow(
                title: "Видеокарта",
                figure: Fmt.integer(t?.gpu),
                unit: "%",
                extra: "· \(Fmt.integer(t?.gpuT)) °C",
                extraWarns: gpuHot,
                share: Fmt.percentShare(t?.gpu),
                warning: gpuHot ? "Видеокарта горячая: \(Fmt.integer(t?.gpuT)) °C" : nil
            )
            Hairline()
            MetricRow(
                title: "Видеопамять",
                figure: Fmt.gigabytes(t?.vram),
                unit: "/ \(Fmt.gigabytes(t?.vramT)) ГБ",
                figureWarns: vramFull,
                share: t?.vramShare,
                warning: vramFull ? "Видеопамять почти заполнена: \(Fmt.integer((t?.vramShare).map { $0 * 100 })) %" : nil
            )
            Hairline()
            MetricRow(
                title: "Память · свободно",
                figure: Fmt.gigabytes(t?.ramF),
                unit: "/ \(Fmt.gigabytes(t?.ramT)) ГБ",
                figureWarns: ramLow,
                share: t?.ramUsedShare,
                warning: ramLow ? "Свободно меньше 1 ГБ" : nil
            )
            Hairline()
            MetricRow(
                title: "Пинг",
                figure: Fmt.integer(t?.ping),
                unit: "мс",
                figureWarns: pingHigh,
                share: (t?.ping).map { $0 / 200 },
                warning: pingHigh ? "Высокий пинг: 90 мс и больше" : nil
            )
        }
    }
}
