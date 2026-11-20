import SwiftUI

/// Главный экран «Панель».
@MainActor
struct PanelView: View {
    @Environment(PulseModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var showSettings = false

    /// Опрос перезапускается при смене этого ключа и останавливается в фоне.
    private struct PollKey: Equatable {
        let running: Bool
        let address: URL?
    }

    var body: some View {
        let running = scenePhase != .background
        let lost = model.link == .lost

        VStack(spacing: 0) {
            PanelHeader(showSettings: $showSettings)
            Hairline()
            if lost {
                ConnectionBanner(error: model.lastError, lastSuccess: model.lastSuccess)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            GeometryReader { geo in
                Group {
                    if geo.size.width > geo.size.height {
                        landscape
                    } else {
                        portrait
                    }
                }
                .opacity(lost ? 0.35 : 1)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: lost)
        .background(Palette.paper.ignoresSafeArea())
        .task(id: PollKey(running: running, address: model.baseURL)) { [model] in
            guard running else { return }
            await model.runPolling()
        }
        .onAppear { model.setPanelVisible(true) }
        .onDisappear { model.setPanelVisible(false) }
        .sheet(isPresented: $showSettings) {
            SettingsSheet()
                .environment(model)
        }
    }

    // MARK: - Портрет: всё в одну колонку

    private var portrait: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                SessionBlock(telemetry: model.telemetry)
                Hairline()
                FPSBlock(telemetry: model.telemetry, size: 112)
                    .padding(.top, 14)
                chart
                    .frame(height: 170)
                    .padding(.top, 10)
                legend
                    .padding(.top, 8)
                    .padding(.bottom, 14)
                Hairline()
                MetricsList(telemetry: model.telemetry)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 24)
        }
        .scrollIndicators(.hidden)
    }

    // MARK: - Ландшафт: кадры и график слева, показатели справа

    private var landscape: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                SessionBlock(telemetry: model.telemetry)
                Hairline()
                FPSBlock(telemetry: model.telemetry, size: 64)
                    .padding(.top, 8)
                chart
                    .frame(minHeight: 40, maxHeight: .infinity)
                    .padding(.top, 6)
                legend
                    .padding(.vertical, 8)
            }
            .padding(.horizontal, 20)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)

            Hairline(axis: .vertical)

            ScrollView {
                MetricsList(telemetry: model.telemetry)
                    .padding(.horizontal, 20)
            }
            .scrollIndicators(.hidden)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    @ViewBuilder
    private var chart: some View {
        if let t = model.telemetry, t.nofps {
            Color.clear
        } else {
            FPSChart(history: model.telemetry?.h ?? [])
        }
    }

    @ViewBuilder
    private var legend: some View {
        if let t = model.telemetry, !t.nofps {
            FPSLegend(stats: FPSHistory.stats(from: t.h), current: t.fps)
        }
    }
}

// MARK: - Шапка

@MainActor
struct PanelHeader: View {
    @Environment(PulseModel.self) private var model
    @Binding private var showSettings: Bool

    init(showSettings: Binding<Bool>) {
        _showSettings = showSettings
    }

    var body: some View {
        HStack(spacing: 10) {
            LiveDot(link: model.link, beat: model.beat)
            Text("ПУЛЬС ПК")
                .font(.system(size: 13, weight: .bold))
                .tracking(1.6)
                .foregroundStyle(Palette.ink)
            Text(status)
                .font(.figure(14, weight: .regular))
                .foregroundStyle(Palette.ink3)
                .lineLimit(1)
            Spacer(minLength: 8)
            Button {
                showSettings = true
            } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 19, weight: .regular))
                    .foregroundStyle(Palette.ink2)
                    .frame(width: 44, height: 44)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel("Настройки")
        }
        .padding(.leading, 20)
        .padding(.trailing, 8)
        .frame(height: 50)
    }

    private var status: String {
        switch model.link {
        case .waiting: "подключение…"
        case .live: model.hostLabel
        case .lost: "нет связи"
        }
    }
}

/// Красная точка вспыхивает на каждом удачном обновлении; при потере связи — пустой кружок.
struct LiveDot: View {
    let link: PulseModel.Link
    let beat: Int

    var body: some View {
        ZStack {
            if link == .live {
                Circle()
                    .fill(Palette.signal)
                    .phaseAnimator([false, true], trigger: beat) { dot, flash in
                        dot
                            .scaleEffect(flash ? 1.6 : 1)
                            .opacity(flash ? 0.75 : 1)
                    } animation: { flash in
                        flash ? Animation.easeOut(duration: 0.12) : Animation.easeIn(duration: 0.5)
                    }
            } else {
                Circle()
                    .strokeBorder(link == .lost ? Palette.ink2 : Palette.ink4, lineWidth: 1.5)
            }
        }
        .frame(width: 10, height: 10)
        .accessibilityLabel(link == .live ? "На связи" : "Нет связи")
    }
}

/// Плашка сверху при обрыве связи.
struct ConnectionBanner: View {
    let error: PulseError?
    let lastSuccess: Date?

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Нет связи с компьютером")
                    .font(.system(size: 15, weight: .semibold))
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    Text(detail(now: context.date))
                        .font(.system(size: 12))
                        .opacity(0.75)
                        .lineLimit(2)
                }
            }
            Spacer(minLength: 8)
            ProgressView()
                .tint(Palette.paper)
        }
        .foregroundStyle(Palette.paper)
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.ink)
        .accessibilityElement(children: .combine)
    }

    private func detail(now: Date) -> String {
        var parts: [String] = [error?.short ?? "компьютер не отвечает", "повторяю"]
        if let lastSuccess {
            parts.append("данные \(Fmt.ago(now.timeIntervalSince(lastSuccess)))")
        }
        return parts.joined(separator: " · ")
    }
}

// MARK: - Игра и сессия

struct SessionBlock: View {
    let telemetry: Telemetry?

    var body: some View {
        let playing = telemetry?.rec ?? false

        HStack(alignment: .firstTextBaseline, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Caption("Игра")
                if let t = telemetry, playing {
                    Text(t.gameTitle)
                        .font(.system(size: 20, weight: .semibold))
                        .foregroundStyle(Palette.ink)
                        .lineLimit(1)
                        .truncationMode(.tail)
                } else {
                    Text(telemetry == nil ? "ожидание данных" : "игра не запущена")
                        .font(.system(size: 20))
                        .foregroundStyle(Palette.ink3)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            if let t = telemetry, playing {
                VStack(alignment: .trailing, spacing: 4) {
                    Caption("Сессия")
                    Text(Fmt.duration(t.sec))
                        .font(.figure(22, weight: .medium))
                        .foregroundStyle(Palette.ink)
                }
            }
        }
        .padding(.vertical, 14)
    }
}

// MARK: - Кадры

struct FPSBlock: View {
    let telemetry: Telemetry?
    let size: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Caption("Кадров в секунду")
            if let t = telemetry, t.nofps {
                Text("кадры не считаются")
                    .font(.figure(size * 0.34, weight: .semibold))
                    .foregroundStyle(Palette.ink2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                    .padding(.top, 8)
                Text("В игре строгая защита от читов — к её кадрам программа не подключается.")
                    .font(.system(size: 13))
                    .foregroundStyle(Palette.ink3)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 4)
            } else {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(Fmt.integer(telemetry?.fps))
                        .font(.figure(size, weight: .semibold))
                        .foregroundStyle(Palette.ink)
                        .lineLimit(1)
                        .minimumScaleFactor(0.5)
                    Text("кадр/с")
                        .font(.figure(size * 0.18, weight: .regular))
                        .foregroundStyle(Palette.ink3)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }
}
