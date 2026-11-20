import SwiftUI

/// Настройки: адрес компьютера, смена адреса, «Не гасить экран».
@MainActor
struct SettingsSheet: View {
    @Environment(PulseModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var keepScreenOn = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Настройки")
                        .font(.figure(30, weight: .semibold))
                        .foregroundStyle(Palette.ink)
                    Spacer()
                    Button("Готово") { dismiss() }
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(Palette.ink)
                }
                .padding(.top, 24)
                .padding(.bottom, 14)

                Hairline()

                VStack(alignment: .leading, spacing: 8) {
                    Caption("Адрес компьютера")
                    Text(model.baseURL?.absoluteString ?? "—")
                        .font(.system(size: 15, weight: .regular, design: .monospaced))
                        .foregroundStyle(Palette.ink)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.vertical, 16)

                Button {
                    dismiss()
                    model.beginEditingAddress()
                } label: {
                    Label("Сменить адрес / сканировать заново", systemImage: "qrcode.viewfinder")
                }
                .buttonStyle(BlueprintButtonStyle())
                .padding(.bottom, 16)

                Hairline()

                Toggle(isOn: $keepScreenOn) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Не гасить экран")
                            .font(.system(size: 16, weight: .medium))
                            .foregroundStyle(Palette.ink)
                        Text("Пока открыта панель, телефон не засыпает.")
                            .font(.system(size: 13))
                            .foregroundStyle(Palette.ink3)
                    }
                }
                .tint(Palette.ink)
                .padding(.vertical, 14)

                Hairline()

                Text("Данные идут напрямую с компьютера по домашней сети и никуда больше не отправляются.")
                    .font(.system(size: 12))
                    .foregroundStyle(Palette.ink4)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 14)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 20)
        }
        .scrollIndicators(.hidden)
        .background(Palette.paper.ignoresSafeArea())
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        .presentationBackground(Palette.paper)
        .preferredColorScheme(.light)
        .onAppear { keepScreenOn = model.keepScreenOn }
        .onChange(of: keepScreenOn) { _, newValue in
            if newValue != model.keepScreenOn {
                model.setKeepScreenOn(newValue)
            }
        }
    }
}
