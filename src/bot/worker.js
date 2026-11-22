// Бот «Пульс ПК» для Telegram — Cloudflare Worker, один файл, без сторонних библиотек.
//
// Что умеет:
//   • «Скачать» — последняя версия, ссылка на установщик и контрольная сумма (из update.json выпуска на GitHub);
//   • частые вопросы — ответы кнопками;
//   • «Сообщить об ошибке» — сообщения и файлы пересылаются разработчику, его ответ возвращается человеку;
//   • автопост в канал, когда на GitHub выходит новая версия (проверка по расписанию);
//   • посты канала по расписанию (список POSTS ниже, собирается из promo/telegram/КАНАЛ.md);
//   • проверка скачанного файла по SHA-256, уведомления о новых версиях в личку (по просьбе),
//     выжимка из присланного файла отчёта, /stats для разработчика, оценка версии в канале.
//
// Настройки Worker'а (Settings → Variables and Secrets):
//   BOT_TOKEN — секрет: токен от @BotFather. В коде и в репозитории его нет.
//   ADMIN_ID  — число: ваш id в Telegram (бот покажет его по команде /id). Без него бот не принимает сообщения об ошибках.
//   CHANNEL   — необязательно: канал для автопоста, по умолчанию @pulsepc_official.
// Привязка KV (вкладка Bindings → Add binding → KV namespace): имя BOT_KV.
// Расписание (Triggers → Cron): например «*/30 * * * *».
// После публикации один раз откройте https://<адрес-worker'а>/setup — бот подключится к Telegram.

const REPO = "Arman715/pulse-pc";
const DEFAULT_CHANNEL = "@pulsepc_official";
const UA = { "user-agent": "PulsePC-telegram-bot" };
const REPORTS_PER_DAY = 20;            // сообщений об ошибках от одного человека в сутки
const REPORT_MODE_SEC = 3600;          // сколько ждать сообщения после «Сообщить об ошибке»
const REPLY_LINK_SEC = 60 * 60 * 24 * 30; // сколько помнить, кому отвечать на пересланное сообщение
const RELEASE_CACHE_SEC = 600;

// ─────────────────────────────── тексты ───────────────────────────────

const BOT_LINK = "https://t.me/pulsepc_official_bot";
const LINKS = {
  channel: "https://t.me/pulsepc_official",
  github: `https://github.com/${REPO}`,
  latest: `https://github.com/${REPO}/releases/latest`,
  guideRu: `https://github.com/${REPO}/blob/main/docs/guide.md`,
  guideEn: `https://github.com/${REPO}/blob/main/docs/guide.en.md`,
  youtube: "https://www.youtube.com/@pulsepc-w7d",
};

const T = {
  ru: {
    welcome:
      "<b>Пульс ПК</b> — бесплатная программа для игрового ПК на Windows 10 и 11. " +
      "Записывает каждую игру и после неё объясняет простыми словами, что ограничивало FPS и откуда рывки.\n\n" +
      "Здесь можно:\n" +
      "⬇ скачать последнюю версию\n" +
      "❓ найти ответ на частый вопрос\n" +
      "✉ написать разработчику об ошибке\n" +
      "🔒 проверить, что скачанный файл — настоящий\n" +
      "🔔 узнавать о новых версиях здесь, в личке",
    bDownload: "⬇ Скачать",
    bFaq: "❓ Частые вопросы",
    bReport: "✉ Сообщить об ошибке",
    bChannel: "Канал",
    bSubscribe: "📢 Подписаться на канал",
    subLine: "\n\n📢 Подпишитесь на канал @pulsepc_official — там новые версии, разборы «почему тормозит игра» и советы.",
    nudge: "Хотите первыми узнавать о новых версиях и советах? Подпишитесь на канал — больше напоминать не буду.",
    bGithub: "GitHub",
    bBack: "← Все вопросы",
    bMenu: "← Меню",
    bRelease: "Страница выпуска — все файлы",
    bCancel: "Отмена",
    faqTitle: "<b>Частые вопросы</b>\n\nВыберите вопрос. Подробно обо всём — в <a href=\"" + LINKS.guideRu + "\">инструкции</a>.",
    download: (r) =>
      `<b>Пульс ПК ${esc(r.version)}</b>` + (r.date ? ` · ${dateRu(r.date)}` : "") + "\n" +
      "Windows 10 и 11, 64 бита · бесплатно, без рекламы\n\n" +
      `⬇ <a href="${installerUrl(r)}">Скачать установщик</a> (${esc(r.url)})\n\n` +
      `SHA-256:\n<code>${esc(r.sha256)}</code>\n\n` +
      "Если SmartScreen покажет «Windows защитила ваш компьютер» — нажмите «Подробнее» → «Выполнить в любом случае»: " +
      "у установщика нет платного сертификата. Установщик проверен на VirusTotal, код программы открыт.\n\n" +
      "Уже стоит программа? «Система» → «Новые версии» → «Обновить».",
    downloadFail:
      "Не получилось узнать последнюю версию — GitHub не ответил. Скачать можно на странице выпусков:\n" + LINKS.latest,
    reportAsk:
      "<b>Сообщить об ошибке</b>\n\n" +
      "Опишите, что случилось: что вы делали, что ожидали увидеть и что произошло. Если была ошибка — её текст. " +
      "Можно приложить снимок экрана.\n\n" +
      "Очень помогает файл отчёта. В программе: <b>«Сообщить о проблеме»</b> в заголовке окна → отметьте " +
      "«Приложить сведения о программе и компьютере» → когда всё готово, нажмите «Показать архив» и пришлите сюда файл " +
      "<code>PulsePC-report-….zip</code>. Имя пользователя в путях программа скрывает сама.\n\n" +
      "Сообщение увидит только разработчик. Ответ придёт в этот чат.",
    reportSent:
      "Отправлено разработчику, спасибо! Ответ придёт сюда.\n" +
      "Можно дослать ещё — текст, снимок или файл отчёта — в течение часа. Закончить: /cancel",
    reportCantCopy: "Такое сообщение Telegram переслать не даёт. Пришлите текст, снимок экрана или файл.",
    reportLimit: "На сегодня сообщений уже много — разработчик их получил. Напишите завтра, пожалуйста.",
    reportOff:
      "Приём сообщений ещё не настроен. Пока можно написать в Issues на GitHub: https://github.com/" + REPO + "/issues",
    reportCancel: "Хорошо, ничего не отправляю.",
    unknown: "Я отвечаю на кнопки ниже. Чтобы написать разработчику, нажмите «Сообщить об ошибке».",
    adminReply: "<b>Ответ разработчика:</b>",
    id: (id) => `Ваш id в Telegram: <code>${id}</code>`,
    faq: [
      {
        k: "rec",
        q: "Игра не записывается",
        a:
          "<b>Игра не записывается</b>\n\n" +
          "1. Программа должна быть запущена — значок у часов. Окно можно свернуть.\n" +
          "2. Сессии короче двух минут не сохраняются: это обычно случайный запуск.\n" +
          "3. Программа не узнала игру? На вкладке «Сессии» нажмите «Добавить игру вручную» и укажите её файл .exe. " +
          "Запись начнётся при следующем запуске игры.\n\n" +
          "Когда запись идёт, внизу окна видна строка «● запись», а после игры у часов появляется уведомление «Сессия записана».",
      },
      {
        k: "fps",
        q: "В отчёте нет FPS",
        a:
          "<b>В отчёте нет FPS</b>\n\n" +
          "1. Проверьте, что включено «Записывать FPS в играх» в меню значка у часов.\n" +
          "2. В играх со строгим античитом (VALORANT, Call of Duty, Genshin Impact и другие) работает осторожный режим: " +
          "FPS и сервер игры не записываются намеренно, чтобы не рисковать вашим аккаунтом. Остальное — температура, " +
          "загрузка, пинг — записывается.\n\n" +
          "В играх с EasyAntiCheat и BattlEye запись по умолчанию полная. Режим для любой игры можно поменять в отчёте " +
          "её сессии: «Записывать полностью» или «Включить осторожный режим для этой игры».",
      },
      {
        k: "ovl",
        q: "Не видно оверлея",
        a:
          "<b>Не видно оверлея</b>\n\n" +
          "1. Включите его: «Система» → «Оверлей» → «Показывать поверх игры».\n" +
          "2. Оверлей виден, только пока идёт запись игры и её окно на переднем плане.\n" +
          "3. Ctrl+Shift+O прячет и показывает его — возможно, он просто спрятан.\n" +
          "4. В старом исключительном полноэкранном режиме Windows не показывает другие окна поверх игры. " +
          "Включите в настройках игры режим экрана «Без рамки» или «Полноэкранный в окне».\n\n" +
          "Оверлей — отдельное окно, в игру он не встраивается, поэтому античиты его не трогают.",
      },
      {
        k: "bat",
        q: "Не показывается заряд мыши или наушников",
        a:
          "<b>Не показывается заряд мыши или наушников</b>\n\n" +
          "Откройте раздел «Заряд устройств»: у каждого устройства в строке написана причина.\n\n" +
          "• Спящая мышь или выключенная гарнитура не отвечает — тогда полчаса показывается последний известный заряд.\n" +
          "• Программа спрашивает заряд раз в 4 минуты, поэтому после включения устройства он появляется не сразу.\n" +
          "• Устройствам, протокол которых программа не знает, она запросов не отправляет: неизвестная команда могла бы " +
          "сбить их настройки. Их заряд показывает фирменная программа производителя.\n\n" +
          "Ваше устройство не определилось? Нажмите «Сообщить об ошибке» и напишите его точную модель — " +
          "так поддержка появляется быстрее.",
      },
      {
        k: "av",
        q: "Антивирус или SmartScreen ругается",
        a:
          "<b>Антивирус или SmartScreen ругается</b>\n\n" +
          "У установщика нет платного сертификата, поэтому SmartScreen может показать «Windows защитила ваш компьютер». " +
          "Нажмите «Подробнее» → «Выполнить в любом случае».\n\n" +
          "Как проверить, что файл настоящий:\n" +
          "• скачивайте только с GitHub — кнопка «Скачать» здесь ведёт туда;\n" +
          "• контрольная сумма SHA-256 есть в «Скачать» и в файле <code>.sha256</code> рядом с установщиком. Проверка в PowerShell: " +
          "<code>Get-FileHash .\\PulsePC-Setup-….exe</code>;\n" +
          "• или пришлите сюда строку <b>Hash</b> из этой команды — бот скажет, официальный ли это файл;\n" +
          "• установщик проверен на VirusTotal, весь код программы открыт на GitHub.\n\n" +
          "Отключать защитник или SmartScreen не нужно — и программа сама их никогда не трогает.",
      },
      {
        k: "adm",
        q: "Кнопки серые, «только с правами администратора»",
        a:
          "<b>Кнопки серые</b>\n\n" +
          "Программе нужны права администратора: без них Windows не даёт читать датчики и менять настройки. " +
          "Запускайте её ярлыком из установки — он сам просит права.",
      },
      {
        k: "undo",
        q: "Как вернуть всё как было",
        a:
          "<b>Как вернуть всё как было</b>\n\n" +
          "Программа ничего не меняет без вашей кнопки. В каждом разделе, где она что-то меняла, есть «Вернуть как было».\n\n" +
          "Собираетесь удалить программу — сначала верните настройки этими кнопками. Удаление: «Параметры» Windows → " +
          "«Приложения» → «Установленные приложения» → «Пульс ПК» → «Удалить».",
      },
      {
        k: "upd",
        q: "Как обновить программу",
        a:
          "<b>Как обновить программу</b>\n\n" +
          "Программа раз в сутки сама проверяет, нет ли новой версии, и показывает уведомление у часов. " +
          "Обновить: «Система» → «Новые версии» → «Обновить». История и настройки сохранятся.\n\n" +
          "Перед установкой программа сверяет контрольную сумму и подпись автора у скачанного файла.",
      },
    ],
    commands: [
      ["start", "Меню"],
      ["download", "Скачать последнюю версию"],
      ["help", "Частые вопросы"],
      ["report", "Сообщить об ошибке"],
      ["check", "Проверить скачанный файл"],
      ["notify", "Сообщать о новых версиях"],
      ["cancel", "Отменить сообщение"],
    ],
    description:
      "Бот программы «Пульс ПК» для игрового ПК на Windows: скачать последнюю версию, проверить файл, ответы на частые вопросы и сообщение об ошибке разработчику.",
    shortDescription: "Пульс ПК: скачать, проверить файл, вопросы, сообщить об ошибке.",
    bCheck: "🔒 Проверить файл",
    bNotifyOn: "🔔 Сообщать о версиях",
    bNotifyOff: "🔕 Не сообщать",
    checkHelp: (file) =>
      "<b>Проверить скачанный файл</b>\n\n" +
      "Так вы убедитесь, что это настоящий Пульс ПК, а не подделка с чужого сайта.\n\n" +
      "1. Откройте PowerShell в папке с файлом (в Проводнике: Shift + правый щелчок по пустому месту → «Открыть окно PowerShell здесь»).\n" +
      `2. Выполните: <code>Get-FileHash .\\${esc(file)}</code>\n` +
      "3. Скопируйте длинную строку из колонки <b>Hash</b> и пришлите сюда.",
    hashOk: (f, v, latest) =>
      `✅ <b>Это официальный файл Пульс ПК.</b>\n${esc(f)} — версия ${esc(v)}.` +
      (latest && latest !== v ? `\n\nЕсть версия новее — ${esc(latest)}: кнопка «Скачать» ниже.` : "\n\nЭто последняя версия."),
    hashBad:
      "❌ <b>Такого файла среди выпусков Пульс ПК нет.</b>\n\n" +
      "Не запускайте его. Возможно, он скачан не с GitHub или повреждён. Скачайте программу заново — кнопка «Скачать» ниже.",
    hashUnknown: (v) =>
      `⚠️ <b>Файл не совпадает с последней версией (${esc(v)}).</b>\n\n` +
      "Старые версии сейчас проверить не получилось — GitHub не ответил. Надёжнее всего скачать программу заново по кнопке ниже " +
      "или прислать сумму ещё раз через полчаса.",
    notifyOn: "🔔 Готово: когда выйдет новая версия, я напишу сюда. Отключить — та же кнопка в меню или /notify.",
    notifyOff: "🔕 Хорошо, больше не буду писать о новых версиях.",
    newVersion: (v) =>
      `🆕 <b>Вышел Пульс ПК ${esc(v)}.</b>\n\nОбновиться: «Система» → «Новые версии» → «Обновить» — или скачайте по кнопке ниже.`,
    bWhatsNew: "Что нового",
  },

  en: {
    welcome:
      "<b>Pulse PC</b> is a free app for gaming PCs on Windows 10 and 11. " +
      "It records every game and then explains in plain words what limited your FPS and where each stutter came from.\n\n" +
      "Here you can:\n" +
      "⬇ download the latest version\n" +
      "❓ find answers to common questions\n" +
      "✉ report a problem to the developer\n" +
      "🔒 check that a downloaded file is genuine\n" +
      "🔔 get new-version messages right here",
    bDownload: "⬇ Download",
    bFaq: "❓ Common questions",
    bReport: "✉ Report a problem",
    bChannel: "Channel (RU)",
    bSubscribe: "📢 Join the channel (RU)",
    subLine: "\n\n📢 Join the channel @pulsepc_official (in Russian) — new versions, stutter breakdowns and tips.",
    nudge: "Want to hear about new versions and tips first? Join the channel — I won't remind you again.",
    bGithub: "GitHub",
    bBack: "← All questions",
    bMenu: "← Menu",
    bRelease: "Release page — all files",
    bCancel: "Cancel",
    faqTitle: "<b>Common questions</b>\n\nPick a question. Everything in detail is in the <a href=\"" + LINKS.guideEn + "\">user guide</a>.",
    download: (r) =>
      `<b>Pulse PC ${esc(r.version)}</b>` + (r.date ? ` · ${dateEn(r.date)}` : "") + "\n" +
      "Windows 10 and 11, 64-bit · free, no ads\n\n" +
      `⬇ <a href="${installerUrl(r)}">Download the installer</a> (${esc(r.url)})\n\n` +
      `SHA-256:\n<code>${esc(r.sha256)}</code>\n\n` +
      "If SmartScreen says “Windows protected your PC”, click “More info” → “Run anyway”: " +
      "the installer has no paid certificate. It was scanned on VirusTotal, and the app's code is open.\n\n" +
      "Already installed? System → Updates (app) → Update.",
    downloadFail: "Couldn't get the latest version — GitHub didn't answer. You can download it here:\n" + LINKS.latest,
    reportAsk:
      "<b>Report a problem</b>\n\n" +
      "Describe what happened: what you did, what you expected and what you got. If there was an error, include its text. " +
      "You can attach a screenshot.\n\n" +
      "A report file helps a lot. In the app: <b>Report a problem</b> in the window title → tick " +
      "“Attach app and PC details” → when it's ready, click “Show archive” and send the " +
      "<code>PulsePC-report-….zip</code> file here. The app hides your user name in paths.\n\n" +
      "Only the developer will see it. The reply will come to this chat.",
    reportSent:
      "Sent to the developer, thank you! The reply will come here.\n" +
      "You can add more — text, a screenshot or the report file — within an hour. To finish: /cancel",
    reportCantCopy: "Telegram doesn't allow forwarding this kind of message. Please send text, a screenshot or a file.",
    reportLimit: "That's a lot of messages for today — the developer has them. Please write again tomorrow.",
    reportOff: "Messages aren't set up yet. For now, please use GitHub Issues: https://github.com/" + REPO + "/issues",
    reportCancel: "OK, nothing will be sent.",
    unknown: "I answer the buttons below. To write to the developer, tap “Report a problem”.",
    adminReply: "<b>Reply from the developer:</b>",
    id: (id) => `Your Telegram id: <code>${id}</code>`,
    faq: [
      {
        k: "rec",
        q: "A game isn't recorded",
        a:
          "<b>A game isn't recorded</b>\n\n" +
          "1. The app must be running — the icon by the clock. The window can be minimized.\n" +
          "2. Sessions shorter than two minutes aren't saved: that's usually an accidental launch.\n" +
          "3. The app didn't recognize the game? On the Sessions tab click “Add a game manually” and pick its .exe file. " +
          "Recording starts the next time the game runs.\n\n" +
          "While a game is recorded, a recording mark shows at the bottom of the window, and after the game a “Session recorded” notification appears by the clock.",
      },
      {
        k: "fps",
        q: "No FPS in the report",
        a:
          "<b>No FPS in the report</b>\n\n" +
          "1. Check that “Record FPS in games” is on in the tray icon menu.\n" +
          "2. Games with a strict anti-cheat (VALORANT, Call of Duty, Genshin Impact and others) use cautious mode: " +
          "FPS and the game server aren't recorded on purpose, so your account isn't at risk. Everything else — temperature, " +
          "load, ping — is recorded.\n\n" +
          "EasyAntiCheat and BattlEye games are recorded in full by default. You can switch the mode for any game in the " +
          "report of its session: “Record fully” or “Turn on cautious mode for this game”.",
      },
      {
        k: "ovl",
        q: "The overlay isn't visible",
        a:
          "<b>The overlay isn't visible</b>\n\n" +
          "1. Turn it on: System → Overlay → “Show over the game”.\n" +
          "2. It's shown only while a game is being recorded and its window is in front.\n" +
          "3. Ctrl+Shift+O hides and shows it — it may simply be hidden.\n" +
          "4. In old exclusive fullscreen mode Windows doesn't show other windows over the game. " +
          "Switch the game's display mode to Borderless or Fullscreen windowed.\n\n" +
          "The overlay is a separate window, nothing is injected into the game, so anti-cheats leave it alone.",
      },
      {
        k: "bat",
        q: "Mouse or headset battery isn't shown",
        a:
          "<b>Mouse or headset battery isn't shown</b>\n\n" +
          "Open the Device battery section: each device row says why.\n\n" +
          "• A sleeping mouse or a switched-off headset doesn't answer — then the last known charge is shown for half an hour.\n" +
          "• The app asks every 4 minutes, so the charge doesn't appear right after you turn a device on.\n" +
          "• The app sends no requests to devices whose protocol it doesn't know: an unknown command could mess up their " +
          "settings. Their charge is shown by the maker's own app.\n\n" +
          "Your device isn't detected? Tap “Report a problem” and write its exact model — that's how support gets added faster.",
      },
      {
        k: "av",
        q: "Antivirus or SmartScreen warning",
        a:
          "<b>Antivirus or SmartScreen warning</b>\n\n" +
          "The installer has no paid certificate, so SmartScreen may say “Windows protected your PC”. " +
          "Click “More info” → “Run anyway”.\n\n" +
          "How to check the file is genuine:\n" +
          "• download only from GitHub — the Download button here leads there;\n" +
          "• the SHA-256 checksum is in Download and in the <code>.sha256</code> file next to the installer. In PowerShell: " +
          "<code>Get-FileHash .\\PulsePC-Setup-….exe</code>;\n" +
          "• or send the <b>Hash</b> line from that command here — the bot will tell you whether it's the official file;\n" +
          "• the installer was scanned on VirusTotal, and all of the app's code is open on GitHub.\n\n" +
          "There's no need to turn off Defender or SmartScreen — and the app never touches them.",
      },
      {
        k: "adm",
        q: "Buttons are greyed out (“administrator only”)",
        a:
          "<b>Buttons are greyed out</b>\n\n" +
          "The app needs administrator rights: without them Windows doesn't let it read sensors or change settings. " +
          "Start it with the shortcut from the installer — it asks for the rights itself.",
      },
      {
        k: "undo",
        q: "How to undo changes",
        a:
          "<b>How to undo changes</b>\n\n" +
          "The app changes nothing without your click. Every section where it changed something has “Restore previous”.\n\n" +
          "Going to uninstall? Restore the settings with those buttons first. To uninstall: Windows Settings → " +
          "Apps → Installed apps → Pulse PC → Uninstall.",
      },
      {
        k: "upd",
        q: "How to update",
        a:
          "<b>How to update</b>\n\n" +
          "The app checks for a new version once a day and shows a notification by the clock. " +
          "To update: System → Updates (app) → Update. History and settings are kept.\n\n" +
          "Before installing, the app checks the checksum and the author's signature of the downloaded file.",
      },
    ],
    commands: [
      ["start", "Menu"],
      ["download", "Download the latest version"],
      ["help", "Common questions"],
      ["report", "Report a problem"],
      ["check", "Check a downloaded file"],
      ["notify", "Notify me of new versions"],
      ["cancel", "Cancel the message"],
    ],
    description:
      "Bot of the Pulse PC app for gaming PCs on Windows: download the latest version, check a file, answers to common questions and problem reports to the developer.",
    shortDescription: "Pulse PC: download, check a file, questions, report a problem.",
    bCheck: "🔒 Check a file",
    bNotifyOn: "🔔 Notify of versions",
    bNotifyOff: "🔕 Stop notifying",
    checkHelp: (file) =>
      "<b>Check a downloaded file</b>\n\n" +
      "This makes sure it's the real Pulse PC and not a fake from another site.\n\n" +
      "1. Open PowerShell in the folder with the file (in File Explorer: Shift + right-click an empty spot → “Open PowerShell window here”).\n" +
      `2. Run: <code>Get-FileHash .\\${esc(file)}</code>\n` +
      "3. Copy the long line from the <b>Hash</b> column and send it here.",
    hashOk: (f, v, latest) =>
      `✅ <b>This is an official Pulse PC file.</b>\n${esc(f)} — version ${esc(v)}.` +
      (latest && latest !== v ? `\n\nA newer version is out — ${esc(latest)}: tap Download below.` : "\n\nThis is the latest version."),
    hashBad:
      "❌ <b>No Pulse PC release has this file.</b>\n\n" +
      "Don't run it. It may not come from GitHub, or it's damaged. Download the app again — Download button below.",
    hashUnknown: (v) =>
      `⚠️ <b>The file doesn't match the latest version (${esc(v)}).</b>\n\n` +
      "Older versions couldn't be checked right now — GitHub didn't answer. The safest option is to download the app again with the button below " +
      "or send the hash again in half an hour.",
    notifyOn: "🔔 Done: when a new version comes out, I'll write here. To turn it off — the same menu button or /notify.",
    notifyOff: "🔕 OK, no more new-version messages.",
    newVersion: (v) =>
      `🆕 <b>Pulse PC ${esc(v)} is out.</b>\n\nTo update: System → Updates (app) → Update — or download with the button below.`,
    bWhatsNew: "What's new",
  },
};

const RU_LANGS = ["ru", "uk", "be", "kk", "uz", "ky", "tg", "hy", "az", "ka"];
const langOf = (user) => (user && user.language_code && !RU_LANGS.includes(user.language_code.slice(0, 2)) ? "en" : "ru");

// ─── ПОСТЫ КАНАЛА: генерируются из promo/telegram/КАНАЛ.md (gen_posts.py), руками не править ───
// Время — UTC (16:00 UTC = 19:00 по Москве). Уже вышедший пост повторно не публикуется (ключ post:<id> в KV).
const POSTS = [
  {"id": "p1", "at": "2026-11-22T21:00:00Z", "kind": "photo", "title": "Знакомство (закрепить)", "media": "zakrep.png", "text": "<b>Пульс ПК</b> — бесплатная программа для игрового ПК на Windows 10 и 11.\n\nПока вы играете, она записывает FPS, худший 1 % кадров, загрузку видеокарты и процессора, память и пинг. А после игры объясняет простыми словами: что ограничивало FPS, откуда каждый рывок и что с этим сделать.\n\nЧто ещё умеет:\n• оверлей поверх игры и заряд беспроводных мышей, наушников и геймпадов\n• диагностика: синие экраны, ошибки дисков, сеть и пинг\n• чертёж вашего компьютера и совет, что докупить — по вашим играм\n• у каждой настройки есть «Вернуть как было», «Безопасность Windows» программа не трогает\n\nБез рекламы, открытый код.\n\n⬇ Скачать: https://github.com/Arman715/pulse-pc/releases/latest\n▶ Обзор: https://www.youtube.com/@pulsepc-w7d\n📷 Instagram: https://www.instagram.com/pulsepc_official/\n🎵 TikTok: https://www.tiktok.com/@pulse.pc6\n🤖 Бот: @pulsepc_official_bot — скачать, частые вопросы, сообщить об ошибке"},
  {"id": "p2", "at": "2026-11-22T21:00:00Z", "kind": "photo", "title": "Что нового в 1.5", "media": "novoe-1.5.png", "text": "<b>Вышел Пульс ПК 1.5</b> — самое большое обновление.\n\n• <b>Оверлей в игре</b> — FPS, худший 1 %, видеокарта, процессор, пинг и заряд устройств. Отдельное окно: в игру ничего не встраивается. Ctrl+Shift+O — спрятать и показать.\n• <b>Заряд беспроводных устройств</b> — Logitech, HyperX, SteelSeries, Corsair, Razer, геймпады Xbox и PlayStation. Программа читает заряд сама, без фирменных программ.\n• <b>Отчёт после каждой игры</b> — время кадра, оценка плавности, причина каждого рывка и сравнение с прошлыми сессиями.\n• <b>«Подробнее и что делать»</b> у каждой ошибки из журналов Windows: какой диск, какая программа, план по шагам и кнопка «Открыть в «Просмотре событий»».\n• <b>Осторожный режим</b> для игр со строгим античитом.\n• Новые разделы «Сеть», «Ноутбук», «Почему тормозит прямо сейчас», «Здоровье ПК» и переносная версия.\n\nУже стоит программа? «Система» → «Новые версии» → «Обновить». Настройки и история сохранятся.\n\n⬇ https://github.com/Arman715/pulse-pc/releases/latest"},
  {"id": "p3", "at": "2026-11-22T21:00:00Z", "kind": "video", "title": "Обзор 1.5 в 1 минуту (видео)", "media": "obzor-1.5.mp4", "text": "Пульс ПК 1.5 за минуту: оверлей, заряд устройств и отчёт после игры.\n\nПолный обзор на 8 минут — на YouTube: https://www.youtube.com/@pulsepc-w7d"},
  {"id": "p4", "at": "2026-11-25T16:00:00Z", "kind": "photo", "title": "Почему игра тормозит", "media": "tormozit.png", "text": "FPS вроде нормальный, а картинка дёргается? Средний FPS прячет рывки.\n\nПульс ПК пишет и средний FPS, и <b>худший 1 % кадров</b> — сколько было в самые тяжёлые моменты. Если он в 2–3 раза ниже среднего, вы это чувствуете.\n\nПосле игры программа сама скажет, что мешало: «ограничивает видеокарта — 88 % времени», «в 21:14 не хватило памяти», «в момент рывка диск был занят». И что с этим сделать — с самого бесплатного шага.\n\n⬇ https://github.com/Arman715/pulse-pc/releases/latest"},
  {"id": "p5", "at": "2026-11-27T16:00:00Z", "kind": "photo", "title": "Оверлей", "media": "overlay.png", "text": "<b>Оверлей поверх игры</b> в Пульс ПК 1.5.\n\nСами выбираете, что показывать: FPS, худший 1 %, загрузку и температуру видеокарты, загрузку процессора, видеопамять, пинг, время и заряд мыши или наушников.\n\nКрасным — только тревога: FPS упал заметно ниже обычного, видеокарта нагрелась до 85 °C, пинг от 150 мс, заряд 15 % и ниже.\n\nРаботает в режимах «Без рамки» и «Полноэкранный в окне». Если игра в исключительном полноэкранном режиме — программа подскажет, что переключить.\n\nВключается: «Система» → «Оверлей». Ctrl+Shift+O — спрятать и показать."},
  {"id": "p6", "at": "2026-11-30T16:00:00Z", "kind": "video", "title": "Наушники сели на шагах (видео)", "media": "zaryad.mp4", "text": "Самое обидное — когда наушники садятся посреди раунда.\n\nПульс ПК 1.5 сам читает заряд беспроводных мышей, наушников, клавиатур и геймпадов — Logitech, HyperX, SteelSeries, Corsair, Razer, Xbox, PlayStation и Bluetooth-устройства. Без фирменных программ, только запросами на чтение.\n\nЗаряд видно в разделе «Заряд устройств» и в оверлее, а перед игрой программа напомнит, если что-то пора зарядить.\n\n⬇ https://github.com/Arman715/pulse-pc/releases/latest"},
  {"id": "p7", "at": "2026-12-02T16:00:00Z", "kind": "photo", "title": "Вернуть как было", "media": "vernut.png", "text": "Боитесь «оптимизаторов», после которых Windows ведёт себя странно? Мы тоже.\n\nВ Пульс ПК ничего не меняется без вашей кнопки, и у каждой настройки есть «Вернуть как было». Защитник, брандмауэр и остальную «Безопасность Windows» программа не трогает вообще — не читает и не меняет.\n\nИ честно говорит, когда настройка не поможет."},
  {"id": "p8", "at": "2026-12-04T16:00:00Z", "kind": "text", "title": "Полный обзор на YouTube", "preview": true, "text": "<b>Полный обзор Пульс ПК — 8 минут</b>, все разделы по порядку: оверлей, отчёт после игры, диагностика, сеть, чертёж компьютера и совет по апгрейду.\n\n▶ https://www.youtube.com/@pulsepc-w7d"},
  {"id": "p9", "at": "2026-12-07T16:00:00Z", "kind": "text", "title": "Проверено антивирусами", "text": "Откуда знать, что программа безопасна?\n\n1. <b>Открытый код</b> — весь код Пульс ПК лежит на GitHub, его может прочитать любой: https://github.com/Arman715/pulse-pc\n2. <b>VirusTotal</b> — установщик 1.5.0 проверен на VirusTotal, ни один антивирус не нашёл угроз: https://www.virustotal.com/gui/file/1933a3b8bbfa4cb3788fdfbc756eab515b4a77d8ea13cf932c262e659169b1bc\n3. <b>Подпись</b> — обновления подписаны ключом автора, программа не поставит файл с чужой подписью или изменённой контрольной суммой.\n\nСкачивайте только с GitHub — не с файлообменников и не из чужих каналов."},
];
// ─── /ПОСТЫ ───

// ─────────────────────────────── вход ───────────────────────────────

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (!env.BOT_TOKEN) return new Response("BOT_TOKEN is not set", { status: 500 });

    if (request.method === "GET" && url.pathname === "/setup") return setup(env, url.origin);
    if ((request.method === "GET" || request.method === "HEAD") && url.pathname.startsWith("/media/")) return media(url.pathname.slice(7));

    if (request.method === "POST" && url.pathname === "/tg") {
      if (request.headers.get("x-telegram-bot-api-secret-token") !== (await webhookSecret(env))) {
        return new Response("forbidden", { status: 403 });
      }
      let update;
      try { update = await request.json(); } catch { return new Response("bad request", { status: 400 }); }
      try { await handleUpdate(env, update, ctx); } catch (e) { console.log("update failed", e && e.stack || e); }
      return new Response("ok"); // всегда 200 — иначе Telegram будет повторять одно и то же
    }

    return new Response("Pulse PC bot is running.");
  },

  async scheduled(event, env, ctx) {
    if (!env.BOT_TOKEN) return;
    try { await checkRelease(env); } catch (e) { console.log("release check failed", e && e.stack || e); }
    const now = (event && event.scheduledTime) || Date.now();
    try { await publishPosts(env, now); } catch (e) { console.log("posts failed", e && e.stack || e); }
    try { await notifyBatch(env); } catch (e) { console.log("notify failed", e && e.stack || e); }
    try { await pollCheck(env, now); } catch (e) { console.log("poll failed", e && e.stack || e); }
  },
};

// Секрет вебхука выводится из токена: настраивать отдельно ничего не нужно, а чужие запросы на /tg не проходят.
async function webhookSecret(env) {
  const data = new TextEncoder().encode("pulsepc-webhook:" + env.BOT_TOKEN);
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", data));
  return [...hash].map((b) => b.toString(16).padStart(2, "0")).join("").slice(0, 48);
}

// /setup безопасно открывать кому угодно: бот подключается только к этому же адресу, очередь сообщений не сбрасывается,
// а повторный вызов чаще раза в минуту ничего не делает.
async function setup(env, origin) {
  const lines = [];
  if (await kvGet(env, "setup-lock")) return new Response("Already done less than a minute ago.", { status: 429 });
  await kvPut(env, "setup-lock", "1", 60);
  const hook = await tg(env, "setWebhook", {
    url: origin + "/tg",
    secret_token: await webhookSecret(env),
    allowed_updates: ["message", "callback_query"],
    // По одному запросу за раз: сообщения одного человека (например, альбом снимков) не обрабатываются наперегонки.
    max_connections: 1,
  });
  lines.push("setWebhook: " + (hook.ok ? "ok" : "ERROR " + hook.description));
  // Свой адрес нужен расписанию постов: по нему Telegram забирает картинки и видео (/media/…).
  await kvPut(env, "origin", origin);
  // Без кода языка — английский (его видят все, чей язык не указан ниже), для русскоязычных кодов — русский.
  const targets = [["en", undefined], ...RU_LANGS.map((c) => ["ru", c])];
  const errors = [];
  for (const [lang, code] of targets) {
    const t = T[lang];
    const extra = code ? { language_code: code } : {};
    for (const [method, body] of [
      ["setMyCommands", { commands: t.commands.map(([command, description]) => ({ command, description })) }],
      ["setMyDescription", { description: t.description }],
      ["setMyShortDescription", { short_description: t.shortDescription }],
    ]) {
      const r = await tg(env, method, { ...body, ...extra });
      if (!r.ok) errors.push(`${method} ${code || "default"}: ${r.description}`);
    }
  }
  lines.push("commands and descriptions: " + (errors.length ? "ERROR\n  " + errors.join("\n  ") : "ok"));
  if (!env.BOT_KV) lines.push("WARNING: KV binding BOT_KV is missing — reports and release posts won't work.");
  lines.push(env.ADMIN_ID ? "ADMIN_ID: set" : "ADMIN_ID: not set — send /id to the bot and add the number as the ADMIN_ID variable.");
  return new Response(lines.join("\n"), { headers: { "content-type": "text/plain; charset=utf-8" } });
}

// ─────────────────────────────── сообщения ───────────────────────────────

async function handleUpdate(env, u, ctx) {
  if (u.callback_query) return handleCallback(env, u.callback_query);
  const m = u.message;
  if (!m || !m.chat || m.chat.type !== "private" || !m.from || m.from.is_bot) return;

  const lang = langOf(m.from);
  const t = T[lang];
  const chat = m.chat.id;
  const isAdmin = env.ADMIN_ID && String(m.from.id) === String(env.ADMIN_ID).trim();
  const text = (m.text || "").trim();
  const cmd = text.startsWith("/") ? text.split(/[\s@]/)[0].slice(1).toLowerCase() : null;
  const arg = text.includes(" ") ? text.slice(text.indexOf(" ") + 1).trim() : "";

  // Ответ разработчика: reply на пересланное сообщение уходит человеку.
  if (isAdmin && m.reply_to_message && !cmd) return adminReply(env, m);

  if (cmd === "start") await seenUser(env, chat);
  if (cmd === "start" && arg === "report") { await bump(env, "rep"); return startReport(env, chat, t); }
  if (cmd === "start" || cmd === "menu") { await bump(env, "start"); await setMode(env, chat, null); return sendWelcome(env, chat, m.from.id, t); }
  if (cmd === "download") { await bump(env, "dl"); return sendDownload(env, chat, t, m.from.id); }
  if (cmd === "help" || cmd === "faq") { await bump(env, "faq"); return send(env, chat, t.faqTitle, faqMenu(t)); }
  if (cmd === "report") { await bump(env, "rep"); return startReport(env, chat, t); }
  if (cmd === "check") return sendCheckHelp(env, chat, t);
  if (cmd === "notify") return toggleNotify(env, chat, m.from.id, t);
  if (cmd === "cancel") { await setMode(env, chat, null); return send(env, chat, t.reportCancel, menu(t)); }
  if (cmd === "id") return send(env, chat, t.id(m.from.id));
  if (cmd === "stats" && isAdmin) return sendStats(env, chat);

  const mode = cmd ? null : await getMode(env, chat);
  if (mode === "report") return forwardReport(env, m, t, ctx);
  // Прислали контрольную сумму (строку Hash из Get-FileHash) — сверяем с выпусками.
  const hash = !cmd && /(?:^|[^0-9a-f])([0-9a-f]{64})(?![0-9a-f])/i.exec(text);
  if (hash) return checkHash(env, chat, hash[1].toLowerCase(), t);
  return send(env, chat, t.unknown, menu(t));
}

async function handleCallback(env, q) {
  const t = T[langOf(q.from)];
  const msg = q.message;
  const data = q.data || "";
  await tg(env, "answerCallbackQuery", { callback_query_id: q.id });
  if (!msg || !msg.chat || msg.chat.type !== "private") return;
  const chat = msg.chat.id;

  if (data === "menu") {
    const [sub, nt] = await Promise.all([isSubscribed(env, q.from.id), notifyState(env, chat)]);
    return edit(env, msg, t.welcome + (sub ? "" : t.subLine), menu(t, sub, nt));
  }
  if (data === "faq") { await bump(env, "faq"); return edit(env, msg, t.faqTitle, faqMenu(t)); }
  if (data.startsWith("q:")) {
    const item = t.faq.find((f) => f.k === data.slice(2));
    if (item) return edit(env, msg, item.a, { inline_keyboard: [[{ text: t.bBack, callback_data: "faq" }], [{ text: t.bReport, callback_data: "rep" }]] });
    return;
  }
  if (data === "dl") { await bump(env, "dl"); return sendDownload(env, chat, t, q.from.id); }
  if (data === "rep") { await bump(env, "rep"); return startReport(env, chat, t); }
  if (data === "chk") return sendCheckHelp(env, chat, t);
  if (data === "notify") return toggleNotify(env, chat, q.from.id, t);
  if (data === "cancel") { await setMode(env, chat, null); return edit(env, msg, t.reportCancel, menu(t)); }
}

// Неподписанным первой идёт кнопка «Подписаться на канал»; ничего не блокируется.
// notify: true/false — подписан ли человек на уведомления о версиях (null — не знаем, кнопку не показываем).
function menu(t, subscribed = true, notify = null) {
  return {
    inline_keyboard: [
      ...(subscribed ? [] : [[{ text: t.bSubscribe, url: LINKS.channel }]]),
      [{ text: t.bDownload, callback_data: "dl" }, { text: t.bFaq, callback_data: "faq" }],
      [{ text: t.bReport, callback_data: "rep" }],
      notify === null ? [{ text: t.bCheck, callback_data: "chk" }]
        : [{ text: t.bCheck, callback_data: "chk" }, { text: notify ? t.bNotifyOff : t.bNotifyOn, callback_data: "notify" }],
      subscribed ? [{ text: t.bChannel, url: LINKS.channel }, { text: t.bGithub, url: LINKS.github }] : [{ text: t.bGithub, url: LINKS.github }],
    ],
  };
}

async function sendWelcome(env, chat, userId, t) {
  const [sub, nt] = await Promise.all([isSubscribed(env, userId), notifyState(env, chat)]);
  return send(env, chat, t.welcome + (sub ? "" : t.subLine), menu(t, sub, nt));
}

// Подписан ли человек на канал. Если Telegram не ответил — считаем, что подписан: лучше не напомнить, чем надоесть.
async function isSubscribed(env, userId) {
  if (!userId) return true;
  const r = await tg(env, "getChatMember", { chat_id: (env.CHANNEL || DEFAULT_CHANNEL).trim(), user_id: userId });
  if (!r.ok || !r.result) return true;
  const st = r.result.status;
  return st === "creator" || st === "administrator" || st === "member" || (st === "restricted" && !!r.result.is_member);
}

// Одно напоминание о канале на человека (после «Скачать» или отправленного сообщения), только неподписанным.
async function subscribeNudge(env, chat, userId, t) {
  if (!env.BOT_KV || (await kvGet(env, `n:${chat}`))) return;
  if (await isSubscribed(env, userId)) return;
  if (!(await kvPut(env, `n:${chat}`, "1", 60 * 60 * 24 * 180))) return;
  return send(env, chat, t.nudge, { inline_keyboard: [[{ text: t.bSubscribe, url: LINKS.channel }]] });
}

function faqMenu(t) {
  return { inline_keyboard: [...t.faq.map((f) => [{ text: f.q, callback_data: "q:" + f.k }]), [{ text: t.bMenu, callback_data: "menu" }]] };
}

async function sendDownload(env, chat, t, userId) {
  let r;
  try { r = await latestRelease(env, false); } catch (e) { console.log("download info failed", e && e.message); }
  if (!r) return send(env, chat, t.downloadFail);
  const res = await send(env, chat, t.download(r), {
    inline_keyboard: [[{ text: t.bRelease, url: releaseUrl(r.version) }], [{ text: t.bMenu, callback_data: "menu" }]],
  });
  await subscribeNudge(env, chat, userId, t);
  return res;
}

// ─────────────────────────────── сообщения об ошибках ───────────────────────────────

async function startReport(env, chat, t) {
  if (!env.ADMIN_ID || !env.BOT_KV) return send(env, chat, t.reportOff, menu(t));
  if (!(await setMode(env, chat, "report"))) return send(env, chat, t.reportOff, menu(t));
  return send(env, chat, t.reportAsk, { inline_keyboard: [[{ text: t.bCancel, callback_data: "cancel" }]] });
}

async function forwardReport(env, m, t, ctx) {
  const chat = m.chat.id;
  if (!env.ADMIN_ID || !env.BOT_KV) return send(env, chat, t.reportOff, menu(t));

  // Альбом снимков приходит отдельными сообщениями с общим media_group_id — он считается одним сообщением.
  const group = m.media_group_id ? !!(await kvGet(env, `g:${m.media_group_id}`)) : false;
  const day = new Date().toISOString().slice(0, 10);
  const countKey = `c:${chat}:${day}`;
  const count = parseInt((await kvGet(env, countKey)) || "0", 10);
  if (!group && count >= REPORTS_PER_DAY) return send(env, chat, t.reportLimit);
  if (m.media_group_id && !group) await kvPut(env, `g:${m.media_group_id}`, "1", 120);

  const f = m.from;
  if (!group) {
    const who = [esc([f.first_name, f.last_name].filter(Boolean).join(" ") || "—"), f.username ? "@" + esc(f.username) : null, `id ${f.id}`, f.language_code || null]
      .filter(Boolean).join(" · ");
    await tg(env, "sendMessage", {
      chat_id: env.ADMIN_ID,
      text: `✉ <b>Сообщение об ошибке</b>\n${who}\n<i>Чтобы ответить, нажмите «Ответить» (reply) на сообщение ниже — ответ уйдёт человеку.</i>`,
      parse_mode: "HTML",
      link_preview_options: { is_disabled: true },
    });
  }
  const copy = await tg(env, "copyMessage", { chat_id: env.ADMIN_ID, from_chat_id: chat, message_id: m.message_id });
  if (!copy.ok) {
    // Некоторые сообщения Telegram копировать не даёт (опросы, платные материалы и т. п.) — честно говорим об этом.
    return send(env, chat, t.reportCantCopy);
  }
  await kvPut(env, `r:${copy.result.message_id}`, JSON.stringify({ chat, lang: langOf(f) }), REPLY_LINK_SEC);
  // Файл отчёта программы — разработчику короткая выжимка ответом на этот файл. Делается уже после ответа Telegram
  // (waitUntil): даже если разбор упадёт, Telegram не станет повторять сообщение.
  if (m.document && REPORT_NAME.test(m.document.file_name || "")) {
    const job = summarizeReport(env, m.document, copy.result.message_id).catch((e) => console.log("report summary failed", e && e.message));
    if (ctx && ctx.waitUntil) ctx.waitUntil(job); else await job;
  }
  if (group) return; // остальные снимки альбома: без повторных заголовков, счётчика и «Отправлено»
  await bump(env, "msg");
  await kvPut(env, countKey, String(count + 1), 60 * 60 * 48);
  const res = await send(env, chat, t.reportSent);
  await subscribeNudge(env, chat, f.id, t);
  return res;
}

async function adminReply(env, m) {
  const link = await kvGet(env, `r:${m.reply_to_message.message_id}`, "json");
  if (!link) return send(env, m.chat.id, "Не знаю, кому ответить: отвечайте (reply) на пересланное сообщение — ссылка хранится 30 дней.");
  const t = T[link.lang] || T.ru;
  await send(env, link.chat, t.adminReply);
  const r = await tg(env, "copyMessage", { chat_id: link.chat, from_chat_id: m.chat.id, message_id: m.message_id });
  return send(env, m.chat.id, r.ok ? "✓ Отправлено." : "Не отправилось: " + esc(r.description || "ошибка Telegram") + " (возможно, человек остановил бота).");
}

const getMode = (env, chat) => kvGet(env, `s:${chat}`);

// Удаляем только то, что есть: у бесплатного KV 1000 записей и удалений в сутки, /start не должен их тратить.
async function setMode(env, chat, mode) {
  if (mode) return kvPut(env, `s:${chat}`, mode, REPORT_MODE_SEC);
  if (await getMode(env, chat)) return kvDel(env, `s:${chat}`);
}

// KV не должен ронять ответ: при ошибке (лимит, 429) бот продолжает без него.
async function kvGet(env, key, type) {
  if (!env.BOT_KV) return null;
  try { return await env.BOT_KV.get(key, type); } catch (e) { console.log("kv get", key, e && e.message); return null; }
}
async function kvPut(env, key, value, ttl) {
  if (!env.BOT_KV) return false;
  try { await env.BOT_KV.put(key, value, ttl ? { expirationTtl: ttl } : undefined); return true; }
  catch (e) { console.log("kv put", key, e && e.message); return false; }
}
async function kvDel(env, key) {
  if (!env.BOT_KV) return false;
  try { await env.BOT_KV.delete(key); return true; } catch (e) { console.log("kv delete", key, e && e.message); return false; }
}

// ─────────────────────────────── проверка файла, уведомления, отчёты, статистика, оценки ───────────────────────────────

const NOTIFY_BATCH = 25;             // сообщений о новой версии за один запуск расписания (лимит запросов Worker'а)
const STATS_CAP = 100;               // событий в сутки, после которых счётчики не пишутся (бережём лимит записей KV: 1000/сутки)
const HASHES_CACHE_SEC = 6 * 3600;
const DAY_MS = 24 * 3600 * 1000;

// ── статистика: один ключ на день; новые люди — ключ u:<чат> (тоже в пределах суточного лимита) ──

const dayKey = (ms) => "st:" + new Date(ms).toISOString().slice(0, 10);
const dayTotal = (s) => Object.values(s || {}).reduce((a, b) => a + b, 0);

async function bump(env, field) {
  if (!env.BOT_KV) return;
  const key = dayKey(Date.now());
  const s = (await kvGet(env, key, "json")) || {};
  if (dayTotal(s) >= STATS_CAP) return;
  s[field] = (s[field] || 0) + 1;
  await kvPut(env, key, JSON.stringify(s), 60 * 60 * 24 * 40);
}

async function seenUser(env, chat) {
  if (!env.BOT_KV || (await kvGet(env, `u:${chat}`))) return;
  if (dayTotal(await kvGet(env, dayKey(Date.now()), "json")) >= STATS_CAP) return;
  await kvPut(env, `u:${chat}`, "1");
  await bump(env, "new");
}

async function countKeys(env, prefix, maxPages = 20) {
  let n = 0, cursor;
  try {
    for (let i = 0; i < maxPages; i++) {
      const r = await env.BOT_KV.list({ prefix, cursor });
      n += r.keys.length;
      if (r.list_complete) return { n, more: false };
      cursor = r.cursor;
    }
    return { n, more: true };
  } catch (e) {
    console.log("kv list", prefix, e && e.message);
    return { n, more: true, failed: true };
  }
}

async function sendStats(env, chat) {
  if (!env.BOT_KV) return send(env, chat, "Хранилище не подключено.");
  const now = Date.now();
  const days = [];
  for (let i = 0; i < 7; i++) days.push((await kvGet(env, dayKey(now - i * DAY_MS), "json")) || {});
  const sum = (list, f) => list.reduce((a, d) => a + (d[f] || 0), 0);
  const row = (name, f) => `${name}: ${sum(days.slice(0, 1), f)} сегодня · ${sum(days, f)} за 7 дней`;
  const [users, notif, members] = await Promise.all([
    countKeys(env, "u:"), countKeys(env, "nt:"),
    tg(env, "getChatMemberCount", { chat_id: (env.CHANNEL || DEFAULT_CHANNEL).trim() }),
  ]);
  const num = (c) => (c.failed ? "—" : c.n + (c.more ? "+" : ""));
  const lines = [
    "<b>Статистика бота</b>",
    "",
    `Людей запускали бота: ${num(users)}`,
    `Подписчиков канала: ${members.ok ? members.result : "—"}`,
    `Просили сообщать о версиях: ${num(notif)}`,
    "",
    row("Новые люди", "new"),
    row("Запуски", "start"),
    row("«Скачать»", "dl"),
    row("Частые вопросы", "faq"),
    row("Открыли «Сообщить об ошибке»", "rep"),
    row("Отправили сообщений", "msg"),
    row("Проверки файла", "chk"),
  ];
  if (days.some((d) => dayTotal(d) >= STATS_CAP)) {
    lines.push("", `<i>В какой-то из дней событий было больше ${STATS_CAP} — дальше счёт не вёлся, чтобы не тратить лимит хранилища. Числа — нижняя граница.</i>`);
  }
  return send(env, chat, lines.join("\n"));
}

// ── уведомления о новых версиях в личку (только по просьбе человека) ──

async function notifyState(env, chat) {
  if (!env.BOT_KV) return null;
  return !!(await kvGet(env, `nt:${chat}`));
}

async function toggleNotify(env, chat, userId, t) {
  if (!env.BOT_KV) return send(env, chat, t.downloadFail);
  const lang = t === T.en ? "en" : "ru";
  const sub = await isSubscribed(env, userId);
  if (await kvGet(env, `nt:${chat}`)) {
    await kvDel(env, `nt:${chat}`);
    return send(env, chat, t.notifyOff, menu(t, sub, false));
  }
  // Язык — в метаданных ключа: при рассылке он приходит вместе со списком, без отдельных чтений.
  let saved = false;
  try { await env.BOT_KV.put(`nt:${chat}`, lang, { metadata: { l: lang } }); saved = true; } catch (e) { console.log("kv put nt", e && e.message); }
  if (!saved) return send(env, chat, t.downloadFail);
  return send(env, chat, t.notifyOn, menu(t, sub, true));
}

// Рассылка идёт частями по NOTIFY_BATCH за запуск расписания. Кто остановил бота — убирается из списка;
// кому не удалось отправить по другой причине (перегрузка, лимит) — повторяется в следующий раз.
async function notifyBatch(env) {
  const kv = env.BOT_KV;
  if (!kv) return;
  const q = await kv.get("ntq", "json");
  if (!q || !q.v) return;
  const targets = [];
  const retry = (q.retry || []).slice(0, NOTIFY_BATCH);
  for (const r of retry) targets.push(r);
  let next = { v: q.v, cursor: q.cursor || null, done: !!q.done, retry: (q.retry || []).slice(retry.length) };
  if (!next.done && targets.length < NOTIFY_BATCH) {
    const page = await kv.list({ prefix: "nt:", cursor: q.cursor || undefined, limit: NOTIFY_BATCH - targets.length });
    for (const k of page.keys) targets.push({ c: k.name.slice(3), l: k.metadata && k.metadata.l });
    next.cursor = page.cursor || null;
    next.done = !!page.list_complete;
  }
  // Сначала сохраняем, докуда дошли: если запуск оборвётся, лучше кому-то не написать, чем написать дважды.
  const finished = () => next.done && next.retry.length === 0;
  if (finished()) await kv.delete("ntq");
  else await kv.put("ntq", JSON.stringify(next));
  const failed = [];
  for (const x of targets) {
    const lang = x.l || (await kvGet(env, `nt:${x.c}`));
    if (!lang) continue; // человек успел отключить уведомления
    const t = T[lang === "en" ? "en" : "ru"];
    const r = await send(env, x.c, t.newVersion(q.v), {
      inline_keyboard: [[{ text: t.bDownload, url: LINKS.latest }, { text: t.bWhatsNew, url: releaseUrl(q.v) }]],
    });
    if (r.ok) continue;
    if (/blocked|deactivated|chat not found|kicked/i.test(r.description || "")) await kvDel(env, `nt:${x.c}`);
    else if ((x.n || 0) < 3) failed.push({ c: x.c, l: lang, n: (x.n || 0) + 1 });
  }
  if (failed.length) await kvPut(env, "ntq", JSON.stringify({ ...next, retry: [...next.retry, ...failed] }));
}

// ── проверка скачанного файла по SHA-256 ──

async function sendCheckHelp(env, chat, t) {
  let file = "PulsePC-Setup-….exe";
  try { file = (await latestRelease(env, false)).url; } catch {}
  return send(env, chat, t.checkHelp(file), { inline_keyboard: [[{ text: t.bMenu, callback_data: "menu" }]] });
}

// Все официальные файлы выпусков (установщики и переносные версии) — по контрольным суммам, которые считает сам GitHub.
async function knownHashes(env) {
  const cached = await kvGet(env, "hashes", "json");
  if (cached && cached.map) return { ...cached, full: true };
  if (!(await kvGet(env, "hashfail"))) {
    try {
      const res = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=30`, {
        headers: { ...UA, accept: "application/vnd.github+json" }, signal: AbortSignal.timeout(8000),
      });
      if (res.ok) {
        const list = await res.json();
        const map = {};
        let latest = null;
        for (const rel of list) {
          if (rel.draft) continue;
          const v = String(rel.tag_name || "").replace(/^v/, "");
          if (!latest && !rel.prerelease) latest = v;
          for (const a of rel.assets || []) {
            const m = /^sha256:([0-9a-f]{64})$/.exec(a.digest || "");
            if (m && /\.(exe|zip)$/i.test(a.name)) map[m[1]] = { f: a.name, v };
          }
        }
        if (Object.keys(map).length) {
          const out = { map, latest };
          await kvPut(env, "hashes", JSON.stringify(out), HASHES_CACHE_SEC);
          return { ...out, full: true };
        }
      }
    } catch (e) { console.log("hashes failed", e && e.message); }
    // GitHub не ответил (часто — лимит запросов): 10 минут не спрашиваем его снова.
    await kvPut(env, "hashfail", "1", 600);
  }
  return { map: {}, latest: null, full: false };
}

async function checkHash(env, chat, hash, t) {
  await bump(env, "chk");
  let k = { map: {}, latest: null, full: false };
  try { k = await knownHashes(env); } catch (e) { console.log("check failed", e && e.message); }
  // Последний выпуск сверяем всегда отдельно: список в памяти мог появиться до выхода новой версии.
  let rel = null;
  try { rel = await latestRelease(env, false); } catch {}
  const latest = [k.latest, rel && rel.version].filter(Boolean).sort(cmpVersion).pop() || null;
  const hit = k.map[hash] || (rel && rel.sha256 === hash ? { f: rel.url, v: rel.version } : null);
  let text;
  if (hit) text = t.hashOk(hit.f, hit.v, latest);
  else if (k.full) text = t.hashBad;
  else if (rel) text = t.hashUnknown(rel.version);
  else return send(env, chat, t.downloadFail);
  return send(env, chat, text, {
    inline_keyboard: [[{ text: t.bDownload, url: LINKS.latest }], [{ text: t.bMenu, callback_data: "menu" }]],
  });
}

// ── выжимка из файла отчёта PulsePC-report-….zip ──

const REPORT_NAME = /^PulsePC-(report|review)-[\w.-]*\.zip$/i;

async function summarizeReport(env, doc, replyTo) {
  if (!env.ADMIN_ID || !doc.file_id || !REPORT_NAME.test(doc.file_name || "") || (doc.file_size || 0) > 20 * 1024 * 1024) return;
  const f = await tg(env, "getFile", { file_id: doc.file_id });
  if (!f.ok || !f.result || !f.result.file_path) return;
  const res = await fetch(`https://api.telegram.org/file/bot${env.BOT_TOKEN}/${f.result.file_path}`, { signal: AbortSignal.timeout(10000) });
  if (!res.ok) return;
  const zip = await unzipText(new Uint8Array(await res.arrayBuffer()), ["description.txt", "report.txt"]);
  if (!zip || (!zip.files["description.txt"] && !zip.files["report.txt"])) return;
  const text = reportSummary(zip.files["description.txt"] || "", zip.files["report.txt"] || "", zip.names);
  return tg(env, "sendMessage", {
    chat_id: env.ADMIN_ID, text, parse_mode: "HTML", link_preview_options: { is_disabled: true },
    reply_parameters: { message_id: replyTo, allow_sending_without_reply: true },
  });
}

function reportSummary(desc, rep, names) {
  const line = (re, s) => { const m = re.exec(s); return m ? m[1].trim() : ""; };
  const cut = (s, n) => (s.length > n ? s.slice(0, n - 1) + "…" : s);
  // Каждое поле обрезается ДО экранирования — разметка не может сломаться посередине.
  const f = (s, n) => esc(cut(s, n));
  const version = line(/^(?:Версия|Version):\s*(.+)$/m, rep) || line(/^Pulse PC (\S+) ·/m, desc);
  const windows = line(/^(Windows .+)$/m, rep) || line(/^Pulse PC \S+ · (Windows \d+)/m, desc);
  const admin = line(/^(?:Права администратора|Administrator rights):\s*(.+)$/m, rep);
  const what = line(/^(?:Что случилось|What happened):\s*(.+)$/m, desc);
  const contact = line(/^(?:Ответить на|Reply to):\s*(.+)$/m, desc);
  // Текст человека — всё, кроме служебных строк.
  const body = desc.split(/\r?\n/)
    .filter((l) => l.trim() && !/^(Что случилось|What happened|Ответить на|Reply to|Pulse PC \S+ ·|Вложение|Attachment)/.test(l.trim()))
    .join(" ").trim();
  const partsBlock = /^(?:Части программы|App components):\s*$([\s\S]*?)(?:\r?\n\s*\r?\n|$(?![\s\S]))/m.exec(rep);
  const parts = partsBlock ? partsBlock[1].split(/\r?\n/).map((l) => l.trim()).filter((l) => l.startsWith("—")) : [];
  const bad = parts.filter((l) => !/(Ошибок не было|No errors|ждёт игры|waiting for a game|ни одна часть|no component)/i.test(l));
  const logHead = /^(?:Журнал, последние строки|Log, last lines) \((\d+)\):\s*$/m.exec(rep);
  const logLines = logHead ? rep.slice(logHead.index + logHead[0].length).split(/\r?\n/).filter((l) => l.trim()) : [];
  const errLines = logLines.filter((l) => /(ERR|ошибк|error|exception|исключени|сбой|fail)/i.test(l));
  const shots = names.filter((n) => /^screenshot-\d+\.png$/i.test(n)).length;

  const head = [version && `Пульс ПК ${f(version, 30)}`, windows && f(windows, 80), admin && `права администратора: ${f(admin, 20)}`].filter(Boolean);
  const sections = [
    { keep: true, text: "📋 <b>Выжимка из отчёта</b>" },
    { keep: true, text: head.join(" · ") },
    { keep: true, text: what && `Что случилось: ${f(what, 100)}` },
    { keep: false, text: body && `«${f(body, 400)}»` },
    { keep: true, text: contact && `Ответить на: ${f(contact, 100)}` },
    { keep: true, text: parts.length ? (bad.length ? `<b>Части с проблемами:</b>\n${bad.slice(0, 5).map((l) => f(l, 200)).join("\n")}` : "Все части программы работают без ошибок.") : "" },
    { keep: false, text: logHead ? `Журнал: ${f(logHead[1], 6)} строк, с ошибками — ${errLines.length}` +
      (errLines.length ? ":\n" + errLines.slice(-3).map((l) => `<code>${f(l.trim(), 180)}</code>`).join("\n") : ".") : "" },
    { keep: true, text: `Снимков экрана: ${shots}.` },
  ].filter((s) => s.text);
  // Не влезает в сообщение — убираем целые необязательные части, а не режем разметку.
  let out = sections.map((s) => s.text).join("\n\n");
  for (const drop of sections.filter((s) => !s.keep).reverse()) {
    if (out.length <= 4000) break;
    sections.splice(sections.indexOf(drop), 1);
    out = sections.map((s) => s.text).join("\n\n");
  }
  return out.length <= 4000 ? out : "📋 <b>Выжимка из отчёта</b>\n\n" + head.join(" · ");
}

// Минимальное чтение zip: только нужные текстовые файлы (deflate или без сжатия), с жёсткими пределами
// на число записей и на распакованный размер — чужой архив не может съесть память или время.
const ZIP_MAX_ENTRIES = 200;
const ZIP_MAX_TEXT = 512 * 1024;

async function inflateLimited(data, max) {
  const reader = new Blob([data]).stream().pipeThrough(new DecompressionStream("deflate-raw")).getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > max) { await reader.cancel(); return null; }
    chunks.push(value);
  }
  const out = new Uint8Array(size);
  let p = 0;
  for (const c of chunks) { out.set(c, p); p += c.length; }
  return out;
}

async function unzipText(buf, wanted) {
  if (buf.length < 22) return null;
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) {
    if (dv.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) return null;
  const count = Math.min(dv.getUint16(eocd + 10, true), ZIP_MAX_ENTRIES);
  let p = dv.getUint32(eocd + 16, true);
  const names = [], files = {};
  const dec = new TextDecoder("utf-8");
  for (let i = 0; i < count && p + 46 <= buf.length; i++) {
    if (dv.getUint32(p, true) !== 0x02014b50) break;
    const method = dv.getUint16(p + 10, true);
    const csize = dv.getUint32(p + 20, true);
    const nlen = dv.getUint16(p + 28, true), elen = dv.getUint16(p + 30, true), clen = dv.getUint16(p + 32, true);
    const local = dv.getUint32(p + 42, true);
    const name = dec.decode(buf.subarray(p + 46, Math.min(p + 46 + nlen, buf.length)));
    names.push(name);
    p += 46 + nlen + elen + clen;
    if (!wanted.includes(name) || files[name] !== undefined || csize > ZIP_MAX_TEXT || local + 30 > buf.length) continue;
    if (dv.getUint32(local, true) !== 0x04034b50) continue;
    const start = local + 30 + dv.getUint16(local + 26, true) + dv.getUint16(local + 28, true);
    if (start + csize > buf.length) continue;
    const data = buf.subarray(start, start + csize);
    let bytes = null;
    if (method === 0) bytes = data;
    else if (method === 8) { try { bytes = await inflateLimited(data, ZIP_MAX_TEXT); } catch { bytes = null; } }
    if (!bytes) continue;
    files[name] = dec.decode(bytes).replace(/^﻿/, "");
  }
  return { names, files };
}

// ── оценка версии: обычный опрос Telegram в канале через неделю после выхода, итог разработчику через 3 дня ──
// Голоса считает сам Telegram — хранилище бота на них не тратится.

const POLL_OPTIONS = ["5 ⭐ отлично", "4 ⭐ хорошо", "3 ⭐ нормально", "2 ⭐ плохо", "1 ⭐ совсем плохо"];

async function pollCheck(env, now) {
  const kv = env.BOT_KV;
  if (!kv) return;
  let r;
  try { r = await latestRelease(env, false); } catch { return; }
  if (!r || !r.date) return;
  const released = Date.parse(r.date + "T12:00:00Z");
  if (!(now >= released + 7 * DAY_MS)) return;
  const key = `poll:${r.version}`;
  const st = await kv.get(key);
  const channel = (env.CHANNEL || DEFAULT_CHANNEL).trim();
  if (!st) {
    if (now > released + 30 * DAY_MS) return; // бот подключили намного позже выхода — не спрашиваем о старой версии
    await kv.put(key, "sending", { expirationTtl: 60 * 60 * 24 * 400 });
    const res = await tg(env, "sendPoll", {
      chat_id: channel,
      question: `Как вам Пульс ПК ${r.version}?`,
      options: POLL_OPTIONS.map((text) => ({ text })),
      is_anonymous: true,
      reply_markup: { inline_keyboard: [[{ text: "🤖 Что-то не работает — написать", url: BOT_LINK }]] },
    });
    if (res.ok) await kvPut(env, key, `done:${res.result.message_id}:${now}`, 60 * 60 * 24 * 400);
    else await kvDel(env, key);
    return;
  }
  const m = /^done:(\d+):(\d+)$/.exec(st);
  if (!m || now < Number(m[2]) + 3 * DAY_MS || !env.ADMIN_ID) return;
  if (await kv.get(`pollsum:${r.version}`)) return;
  const stop = await tg(env, "stopPoll", { chat_id: channel, message_id: Number(m[1]) });
  if (!stop.ok || !stop.result) {
    // Опрос уже закрыт или удалён — больше не пытаемся.
    if (/closed|not found|can't be stopped/i.test(stop.description || "")) await kvPut(env, `pollsum:${r.version}`, "gone", 60 * 60 * 24 * 400);
    return;
  }
  await kvPut(env, `pollsum:${r.version}`, "1", 60 * 60 * 24 * 400);
  const counts = (stop.result.options || []).map((o) => o.voter_count || 0); // порядок: 5, 4, 3, 2, 1
  const total = counts.reduce((a, b) => a + b, 0);
  const avg = total ? counts.reduce((a, c, i) => a + c * (5 - i), 0) / total : 0;
  const text = total
    ? `${esc(r.version)}: ${avg.toFixed(1).replace(".", ",")} из 5 (голосов: ${total}) — ` + counts.map((c, i) => `${5 - i}★ ${c}`).join(" · ")
    : `${esc(r.version)}: никто не проголосовал.`;
  await send(env, env.ADMIN_ID, "📊 <b>Итог оценки версии за 3 дня</b> (опрос закрыт)\n" + text);
}

// ─────────────────────────────── посты по расписанию ───────────────────────────────

// Картинки и видео постов лежат в репозитории (src/bot/media). GitHub отдаёт mp4 как «просто файл», а Telegram
// по ссылке принимает видео только с типом video/mp4 — поэтому бот раздаёт их сам, с правильным типом.
// Раздаются только файлы из этого списка.
const MEDIA = {
  "zakrep.png": "image/png", "novoe-1.5.png": "image/png", "overlay.png": "image/png",
  "tormozit.png": "image/png", "vernut.png": "image/png",
  "obzor-1.5.mp4": "video/mp4", "zaryad.mp4": "video/mp4",
};

async function media(name) {
  const type = Object.prototype.hasOwnProperty.call(MEDIA, name) ? MEDIA[name] : null;
  if (!type) return new Response("not found", { status: 404 });
  const res = await fetch(`https://raw.githubusercontent.com/${REPO}/main/src/bot/media/${name}`, { headers: UA });
  if (!res.ok) return new Response("not found", { status: 404 });
  const headers = { "content-type": type, "cache-control": "public, max-age=86400" };
  const len = res.headers.get("content-length");
  if (len) headers["content-length"] = len;
  return new Response(res.body, { headers });
}

// Пост выходит, когда пришло его время. За один запуск — не больше трёх, строго по порядку:
// если пост не вышел, следующие ждут. Отметка ставится ДО отправки, поэтому повторов не бывает.
// Кнопки под каждым постом канала: скачать и чат с ботом.
const POST_BUTTONS = {
  inline_keyboard: [[
    { text: "⬇ Скачать", url: `https://github.com/${REPO}/releases/latest` },
    { text: "🤖 Вопросы и ошибки", url: BOT_LINK },
  ]],
};

async function publishPosts(env, now) {
  const kv = env.BOT_KV;
  if (!kv) return;
  const origin = await kv.get("origin");
  if (!origin) return; // /setup ещё не открывали
  const channel = (env.CHANNEL || DEFAULT_CHANNEL).trim();
  let sent = 0;
  for (const p of POSTS) {
    if (Date.parse(p.at) > now) break;
    const state = await kv.get(`post:${p.id}`);
    if (state) {
      // Пост вышел раньше без кнопок (до их появления) — добавляем кнопки к нему.
      const m = /^done:(\d+)$/.exec(state);
      if (m) {
        const r = await tg(env, "editMessageReplyMarkup", { chat_id: channel, message_id: Number(m[1]), reply_markup: POST_BUTTONS });
        if (r.ok || /not modified/i.test(r.description || "")) await kvPut(env, `post:${p.id}`, state + ":btn");
      }
      continue;
    }
    if (sent >= 3) break;
    await kv.put(`post:${p.id}`, "sending");
    const base = { chat_id: channel, parse_mode: "HTML", reply_markup: POST_BUTTONS };
    const url = `${origin}/media/${p.media}`;
    const res = p.kind === "photo" ? await tg(env, "sendPhoto", { ...base, photo: url, caption: p.text })
      : p.kind === "video" ? await tg(env, "sendVideo", { ...base, video: url, caption: p.text, supports_streaming: true, width: 1080, height: 1920 })
      : await tg(env, "sendMessage", { ...base, text: p.text, link_preview_options: { is_disabled: !p.preview } });
    if (res.ok) {
      await kvPut(env, `post:${p.id}`, "done:" + (res.result && res.result.message_id) + ":btn");
      sent++;
      continue;
    }
    await kvDel(env, `post:${p.id}`);
    const flag = `postfail:${p.id}`;
    if (env.ADMIN_ID && !(await kvGet(env, flag))) {
      await kvPut(env, flag, "1", 60 * 60 * 24);
      await send(env, env.ADMIN_ID, `Не получилось опубликовать пост «${esc(p.title)}» в ${esc(channel)}: ${esc(res.description || "ошибка Telegram")}. Бот попробует снова через 30 минут.`);
    }
    break;
  }
}

// ─────────────────────────────── выпуски ───────────────────────────────

const releaseUrl = (v) => `https://github.com/${REPO}/releases/tag/v${v}`;
const installerUrl = (r) => `https://github.com/${REPO}/releases/download/v${r.version}/${encodeURIComponent(r.url)}`;

// Последний выпуск — из update.json, того же файла, по которому обновляется сама программа.
async function latestRelease(env, fresh) {
  if (!fresh) {
    const cached = await kvGet(env, "rel", "json");
    if (cached) return cached;
  }
  const res = await fetch(`https://github.com/${REPO}/releases/latest/download/update.json`, {
    headers: UA, redirect: "follow", signal: AbortSignal.timeout(8000),
  });
  if (!res.ok) throw new Error("update.json: HTTP " + res.status);
  const j = JSON.parse((await res.text()).replace(/^﻿/, ""));
  const r = {
    version: String(j.version || ""),
    url: String(j.url || ""),
    sha256: String(j.sha256 || "").toLowerCase(),
    date: String(j.date || ""),
    notes: Array.isArray(j.notes) ? j.notes.map(String) : [],
  };
  if (!/^\d+\.\d+\.\d+$/.test(r.version) || !/^[0-9a-f]{64}$/.test(r.sha256) || !/^[\w.\-]+\.exe$/.test(r.url)) {
    throw new Error("update.json: unexpected content");
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(r.date)) r.date = "";
  await kvPut(env, "rel", JSON.stringify(r), RELEASE_CACHE_SEC);
  return r;
}

// По расписанию: вышла новая версия — пост в канал. Первый запуск только запоминает текущую версию.
// Ошибки чтения KV здесь не глушатся: лучше пропустить проверку, чем опубликовать дважды.
async function checkRelease(env) {
  const kv = env.BOT_KV;
  if (!kv) return;
  const r = await latestRelease(env, true);
  const posted = await kv.get("posted");
  if (!posted) { await kv.put("posted", r.version); return; }
  if (cmpVersion(r.version, posted) <= 0) return;

  // Тем, кто просил сообщать о новых версиях, — рассылка в личку (по частям, см. notifyBatch). Один раз на версию.
  await kvDel(env, "hashes"); // список официальных файлов — заново, уже с новой версией
  if (!(await kv.get(`ntv:${r.version}`))) {
    await kv.put(`ntv:${r.version}`, "1", { expirationTtl: 60 * 60 * 24 * 90 });
    await kv.put("ntq", JSON.stringify({ v: r.version, cursor: null, done: false, retry: [] }));
  }

  // Отметка «публикую» ставится ДО отправки: если потом не запишется «posted», повторного поста всё равно не будет.
  const mark = `posting:${r.version}`;
  if (await kv.get(mark)) { await kv.put("posted", r.version); return; }
  await kv.put(mark, "1", { expirationTtl: 60 * 60 * 24 * 30 });

  const channel = (env.CHANNEL || DEFAULT_CHANNEL).trim();
  const res = await tg(env, "sendMessage", {
    chat_id: channel,
    text: await releasePost(r),
    parse_mode: "HTML",
    link_preview_options: { is_disabled: true },
    reply_markup: { inline_keyboard: [[{ text: "⬇ Скачать и все изменения", url: releaseUrl(r.version) }], [{ text: "🤖 Вопросы и ошибки", url: BOT_LINK }]] },
  });
  if (res.ok) { await kvPut(env, "posted", r.version); return; }

  // Не вышло (бот не админ канала и т. п.) — снимаем отметку, попробуем в следующий раз; разработчику скажем один раз на версию.
  await kvDel(env, mark);
  const flag = `postfail:${r.version}`;
  if (env.ADMIN_ID && !(await kvGet(env, flag))) {
    await kvPut(env, flag, "1", 60 * 60 * 24 * 7);
    await send(env, env.ADMIN_ID,
      `Не получилось опубликовать в ${esc(channel)} пост о версии ${esc(r.version)}: ${esc(res.description || "ошибка Telegram")}.
` +
      "Проверьте, что бот — администратор канала с правом публиковать сообщения. Бот попробует снова по расписанию.");
  }
}

async function releasePost(r) {
  let items = [];
  let fixes = 0;
  // Заголовки берутся из описания выпуска на GitHub (жирные части пунктов «Новое»), иначе — из update.json.
  try {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases/tags/v${r.version}`, {
      headers: { ...UA, accept: "application/vnd.github+json" }, signal: AbortSignal.timeout(8000),
    });
    if (res.ok) ({ items, fixes } = parseReleaseBody((await res.json()).body || ""));
  } catch (e) { console.log("release body failed", e && e.message); }
  if (!items.length) ({ items, fixes } = parseNotes(r.notes));

  let text = `<b>Вышел Пульс ПК ${esc(r.version)}</b>\n`;
  if (items.length) text += "\n<b>Новое</b>\n" + items.map((i) => "• " + esc(i)).join("\n") + "\n";
  if (fixes) text += `\nИсправлено ошибок: ${fixes}.\n`;
  text += "\nУже стоит программа? Она сама предложит обновиться, или: «Система» → «Новые версии» → «Обновить».";
  return text.length > 4000 ? text.slice(0, 3990).replace(/\n[^\n]*$/, "") + "\n…" : text;
}

function parseReleaseBody(body) {
  const ru = body.split(/\r?\n-{3,}\r?\n/)[0];
  const section = (name) => {
    const m = ru.match(new RegExp("^#{2,4}\\s*" + name + "\\s*$([\\s\\S]*?)(?=^#{1,4}\\s|$(?![\\s\\S]))", "mi"));
    return m ? m[1].split(/\r?\n/).filter((l) => /^\s*[-*]\s+/.test(l)) : [];
  };
  const items = section("Новое")
    .map((l) => {
      const bold = [...l.matchAll(/\*\*(.+?)\*\*/g)].map((x) => x[1]);
      return clean(bold.length ? bold.join(", ") : headline(l.replace(/^\s*[-*]\s+/, "")));
    })
    .filter(Boolean)
    .map(capitalize);
  return { items, fixes: section("Исправлено").length };
}

function parseNotes(notes) {
  const items = notes.filter((n) => /^Новое:/i.test(n)).map((n) => capitalize(clean(headline(n.replace(/^Новое:\s*/i, ""))))).filter(Boolean);
  return { items, fixes: notes.filter((n) => /^Исправлено:/i.test(n)).length };
}

// Запасной заголовок из длинной строки update.json: до «;» или скобки, не длиннее 90 знаков по границе слова.
function headline(s) {
  const h = s.split(/;|\s\(/)[0].trim();
  if (h.length <= 90) return h;
  const cut = h.slice(0, 90);
  return cut.slice(0, cut.lastIndexOf(" ")).replace(/[\s,—:]+$/, "") + "…";
}
const clean = (s) => s.replace(/<\/?kbd>/gi, "").replace(/\*\*|__|`/g, "").replace(/\[([^\]]+)\]\([^)]+\)/g, "$1").trim();
const capitalize = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);

function cmpVersion(a, b) {
  const pa = a.split(".").map(Number), pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  return 0;
}

// ─────────────────────────────── Telegram ───────────────────────────────

async function tg(env, method, body) {
  try {
    const res = await fetch(`https://api.telegram.org/bot${env.BOT_TOKEN}/${method}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    const j = await res.json().catch(() => ({ ok: false, description: "HTTP " + res.status }));
    if (!j.ok) console.log(method, j.description);
    return j;
  } catch (e) {
    console.log(method, e && e.message);
    return { ok: false, description: String(e && e.message || e) };
  }
}

const send = (env, chat, text, markup) =>
  tg(env, "sendMessage", { chat_id: chat, text, parse_mode: "HTML", link_preview_options: { is_disabled: true }, ...(markup ? { reply_markup: markup } : {}) });

async function edit(env, msg, text, markup) {
  const r = await tg(env, "editMessageText", {
    chat_id: msg.chat.id, message_id: msg.message_id, text, parse_mode: "HTML",
    link_preview_options: { is_disabled: true }, ...(markup ? { reply_markup: markup } : {}),
  });
  // Старое сообщение не редактируется (например, это файл) — просто пишем новое.
  if (!r.ok && !/not modified/i.test(r.description || "")) return send(env, msg.chat.id, text, markup);
  return r;
}

// ─────────────────────────────── мелочи ───────────────────────────────

function esc(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

const MONTHS_RU = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
const MONTHS_EN = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
function dateRu(d) { const [y, m, day] = d.split("-").map(Number); return `${day} ${MONTHS_RU[m - 1]} ${y}`; }
function dateEn(d) { const [y, m, day] = d.split("-").map(Number); return `${MONTHS_EN[m - 1]} ${day}, ${y}`; }

// Для проверок (test.mjs); на работу Worker'а не влияет.
export const _test = { T, POSTS, MEDIA, reportSummary, unzipText, POLL_OPTIONS, parseReleaseBody, parseNotes, releasePost, cmpVersion, webhookSecret, esc, langOf };
