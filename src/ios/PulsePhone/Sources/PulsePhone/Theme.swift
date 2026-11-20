import SwiftUI

/// Палитра «Чертёж» — те же значения, что и в программе на компьютере.
/// Вычисляемые свойства, а не хранимые: так нет глобального состояния для строгой проверки параллелизма.
enum Palette {
    /// Бумага — фон всех экранов.
    static var paper: Color { Color(rgb: 0xF2EEE5) }
    /// Тушь — основной текст и линии.
    static var ink: Color { Color(rgb: 0x1A1917) }
    static var ink2: Color { Color(rgb: 0x45423C) }
    static var ink3: Color { Color(rgb: 0x6F6A60) }
    static var ink4: Color { Color(rgb: 0xA8A194) }
    /// Тонкие линии сетки и разделители.
    static var rule: Color { Color(rgb: 0xCFC7B8) }
    /// Красный — только живой сигнал и главное действие.
    static var signal: Color { Color(rgb: 0xC23B22) }
    /// Охра — предупреждения.
    static var ochre: Color { Color(rgb: 0x95580F) }
}

extension Color {
    init(rgb: UInt32) {
        self.init(
            .sRGB,
            red: Double((rgb >> 16) & 0xFF) / 255,
            green: Double((rgb >> 8) & 0xFF) / 255,
            blue: Double(rgb & 0xFF) / 255,
            opacity: 1
        )
    }
}

extension Font {
    /// Цифры: системный шрифт в узком начертании с моноширинными цифрами.
    static func figure(_ size: CGFloat, weight: Font.Weight = .medium) -> Font {
        Font.system(size: size, weight: weight).width(.condensed).monospacedDigit()
    }
}

/// Мелкая подпись прописными буквами с разрядкой: «ЗАГРУЗКА ПРОЦЕССОРА».
struct Caption: View {
    private let text: String
    private let color: Color

    init(_ text: String, color: Color = Palette.ink3) {
        self.text = text
        self.color = color
    }

    var body: some View {
        Text(text.uppercased())
            .font(.system(size: 11, weight: .semibold))
            .tracking(1.1)
            .foregroundStyle(color)
            .lineLimit(1)
    }
}

/// Волосяная линия вместо карточек и рамок.
struct Hairline: View {
    private let color: Color
    private let axis: Axis

    @Environment(\.displayScale) private var displayScale

    init(color: Color = Palette.rule, axis: Axis = .horizontal) {
        self.color = color
        self.axis = axis
    }

    var body: some View {
        let thickness = max(1 / displayScale, 0.5)
        if axis == .horizontal {
            Rectangle().fill(color).frame(height: thickness)
        } else {
            Rectangle().fill(color).frame(width: thickness)
        }
    }
}

/// Диагональная штриховка, как на чертеже.
struct Hatch: Shape {
    var step: CGFloat = 3

    func path(in rect: CGRect) -> Path {
        var path = Path()
        let spacing = max(step, 1)
        var x = rect.minX - rect.height
        while x <= rect.maxX {
            path.move(to: CGPoint(x: x, y: rect.maxY))
            path.addLine(to: CGPoint(x: x + rect.height, y: rect.minY))
            x += spacing
        }
        return path
    }
}

/// Заштрихованный квадратик охрой — знак предупреждения.
struct HatchMarker: View {
    var size: CGFloat = 10

    var body: some View {
        Hatch(step: 2.5)
            .stroke(Palette.ochre, lineWidth: 1)
            .frame(width: size, height: size)
            .clipped()
            .overlay(Rectangle().strokeBorder(Palette.ochre, lineWidth: 1))
            .accessibilityHidden(true)
    }
}

/// Кнопка без «пузыря»: прямоугольник в тонкой рамке. Главное действие — красная заливка.
struct BlueprintButtonStyle: ButtonStyle {
    enum Kind {
        case primary
        case secondary
    }

    var kind: Kind = .secondary

    func makeBody(configuration: Configuration) -> some View {
        BlueprintButtonBody(configuration: configuration, kind: kind)
    }
}

private struct BlueprintButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let kind: BlueprintButtonStyle.Kind

    @Environment(\.isEnabled) private var isEnabled

    init(configuration: ButtonStyleConfiguration, kind: BlueprintButtonStyle.Kind) {
        self.configuration = configuration
        self.kind = kind
    }

    var body: some View {
        let primary = kind == .primary
        configuration.label
            .font(.system(size: 16, weight: .semibold))
            .foregroundStyle(primary ? Palette.paper : Palette.ink)
            .frame(maxWidth: .infinity, minHeight: 50)
            .background(primary ? Palette.signal : Color.clear)
            .overlay(Rectangle().strokeBorder(primary ? Palette.signal : Palette.ink, lineWidth: 1))
            .contentShape(Rectangle())
            .opacity(isEnabled ? (configuration.isPressed ? 0.7 : 1) : 0.4)
    }
}
