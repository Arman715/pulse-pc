import SwiftUI

@main
@MainActor
struct PulsePhoneApp: App {
    @State private var model = PulseModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .preferredColorScheme(.light)
                .tint(Palette.ink)
        }
    }
}

/// Без сохранённого адреса — экран подключения, иначе — панель.
@MainActor
struct RootView: View {
    @Environment(PulseModel.self) private var model

    var body: some View {
        ZStack {
            Palette.paper.ignoresSafeArea()
            if model.showsConnectScreen {
                ConnectView()
            } else {
                PanelView()
            }
        }
    }
}
