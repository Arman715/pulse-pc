// Бот «Пульс ПК» для Telegram — Cloudflare Worker, один файл, без сторонних библиотек.
//
// Что умеет:
//   • «Скачать» — последняя версия, ссылка на установщик и контрольная сумма (из update.json выпуска на GitHub);
//   • частые вопросы — ответы кнопками;
//   • «Сообщить об ошибке» — сообщения и файлы пересылаются разработчику, его ответ возвращается человеку;
//   • автопост в канал, когда на GitHub выходит новая версия (проверка по расписанию);
//   • посты канала по расписанию (список POSTS ниже, собирается из promo/telegram/КАНАЛ.md).
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
      "✉ написать разработчику об ошибке",
    bDownload: "⬇ Скачать",
    bFaq: "❓ Частые вопросы",
    bReport: "✉ Сообщить об ошибке",
    bChannel: "Канал",
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
      ["cancel", "Отменить сообщение"],
    ],
    description:
      "Бот программы «Пульс ПК» для игрового ПК на Windows: скачать последнюю версию, ответы на частые вопросы и сообщение об ошибке разработчику.",
    shortDescription: "Пульс ПК: скачать, частые вопросы, сообщить об ошибке.",
  },

  en: {
    welcome:
      "<b>Pulse PC</b> is a free app for gaming PCs on Windows 10 and 11. " +
      "It records every game and then explains in plain words what limited your FPS and where each stutter came from.\n\n" +
      "Here you can:\n" +
      "⬇ download the latest version\n" +
      "❓ find answers to common questions\n" +
      "✉ report a problem to the developer",
    bDownload: "⬇ Download",
    bFaq: "❓ Common questions",
    bReport: "✉ Report a problem",
    bChannel: "Channel (RU)",
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
      ["cancel", "Cancel the message"],
    ],
    description:
      "Bot of the Pulse PC app for gaming PCs on Windows: download the latest version, answers to common questions and problem reports to the developer.",
    shortDescription: "Pulse PC: download, common questions, report a problem.",
  },
};

const RU_LANGS = ["ru", "uk", "be", "kk", "uz", "ky", "tg", "hy", "az", "ka"];
const langOf = (user) => (user && user.language_code && !RU_LANGS.includes(user.language_code.slice(0, 2)) ? "en" : "ru");

// ─── ПОСТЫ КАНАЛА: генерируются из promo/telegram/КАНАЛ.md (gen_posts.py), руками не править ───
// Время — UTC (16:00 UTC = 19:00 по Москве). Уже вышедший пост повторно не публикуется (ключ post:<id> в KV).
const POSTS = [
  {"id": "p1", "at": "2026-11-23T00:00:00Z", "kind": "photo", "title": "Знакомство (закрепить)", "media": "zakrep.png", "text": "<b>Пульс ПК</b> — бесплатная программа для игрового ПК на Windows 10 и 11.\n\nПока вы играете, она записывает FPS, худший 1 % кадров, загрузку видеокарты и процессора, память и пинг. А после игры объясняет простыми словами: что ограничивало FPS, откуда каждый рывок и что с этим сделать.\n\nЧто ещё умеет:\n• оверлей поверх игры и заряд беспроводных мышей, наушников и геймпадов\n• диагностика: синие экраны, ошибки дисков, сеть и пинг\n• чертёж вашего компьютера и совет, что докупить — по вашим играм\n• у каждой настройки есть «Вернуть как было», «Безопасность Windows» программа не трогает\n\nБез рекламы, открытый код.\n\n⬇ Скачать: https://github.com/Arman715/pulse-pc/releases/latest\n▶ Обзор: https://www.youtube.com/@pulsepc-w7d\n📷 Instagram: https://www.instagram.com/pulsepc_official/\n🎵 TikTok: https://www.tiktok.com/@pulse.pc6\n🤖 Бот: @pulsepc_official_bot — скачать, частые вопросы, сообщить об ошибке"},
  {"id": "p2", "at": "2026-11-23T00:00:00Z", "kind": "photo", "title": "Что нового в 1.5", "media": "novoe-1.5.png", "text": "<b>Вышел Пульс ПК 1.5</b> — самое большое обновление.\n\n• <b>Оверлей в игре</b> — FPS, худший 1 %, видеокарта, процессор, пинг и заряд устройств. Отдельное окно: в игру ничего не встраивается. Ctrl+Shift+O — спрятать и показать.\n• <b>Заряд беспроводных устройств</b> — Logitech, HyperX, SteelSeries, Corsair, Razer, геймпады Xbox и PlayStation. Программа читает заряд сама, без фирменных программ.\n• <b>Отчёт после каждой игры</b> — время кадра, оценка плавности, причина каждого рывка и сравнение с прошлыми сессиями.\n• <b>«Подробнее и что делать»</b> у каждой ошибки из журналов Windows: какой диск, какая программа, план по шагам и кнопка «Открыть в «Просмотре событий»».\n• <b>Осторожный режим</b> для игр со строгим античитом.\n• Новые разделы «Сеть», «Ноутбук», «Почему тормозит прямо сейчас», «Здоровье ПК» и переносная версия.\n\nУже стоит программа? «Система» → «Новые версии» → «Обновить». Настройки и история сохранятся.\n\n⬇ https://github.com/Arman715/pulse-pc/releases/latest"},
  {"id": "p3", "at": "2026-11-23T00:00:00Z", "kind": "video", "title": "Обзор 1.5 в 1 минуту (видео)", "media": "obzor-1.5.mp4", "text": "Пульс ПК 1.5 за минуту: оверлей, заряд устройств и отчёт после игры.\n\nПолный обзор на 8 минут — на YouTube: https://www.youtube.com/@pulsepc-w7d"},
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
      try { await handleUpdate(env, update); } catch (e) { console.log("update failed", e && e.stack || e); }
      return new Response("ok"); // всегда 200 — иначе Telegram будет повторять одно и то же
    }

    return new Response("Pulse PC bot is running.");
  },

  async scheduled(event, env, ctx) {
    if (!env.BOT_TOKEN) return;
    try { await checkRelease(env); } catch (e) { console.log("release check failed", e && e.stack || e); }
    try { await publishPosts(env, (event && event.scheduledTime) || Date.now()); } catch (e) { console.log("posts failed", e && e.stack || e); }
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

async function handleUpdate(env, u) {
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

  if (cmd === "start" && arg === "report") return startReport(env, chat, t);
  if (cmd === "start" || cmd === "menu") { await setMode(env, chat, null); return send(env, chat, t.welcome, menu(t)); }
  if (cmd === "download") return sendDownload(env, chat, t);
  if (cmd === "help" || cmd === "faq") return send(env, chat, t.faqTitle, faqMenu(t));
  if (cmd === "report") return startReport(env, chat, t);
  if (cmd === "cancel") { await setMode(env, chat, null); return send(env, chat, t.reportCancel, menu(t)); }
  if (cmd === "id") return send(env, chat, t.id(m.from.id));

  if (!cmd && (await getMode(env, chat)) === "report") return forwardReport(env, m, t);
  return send(env, chat, t.unknown, menu(t));
}

async function handleCallback(env, q) {
  const t = T[langOf(q.from)];
  const msg = q.message;
  await tg(env, "answerCallbackQuery", { callback_query_id: q.id });
  if (!msg || !msg.chat || msg.chat.type !== "private") return;
  const chat = msg.chat.id;
  const data = q.data || "";

  if (data === "menu") return edit(env, msg, t.welcome, menu(t));
  if (data === "faq") return edit(env, msg, t.faqTitle, faqMenu(t));
  if (data.startsWith("q:")) {
    const item = t.faq.find((f) => f.k === data.slice(2));
    if (item) return edit(env, msg, item.a, { inline_keyboard: [[{ text: t.bBack, callback_data: "faq" }], [{ text: t.bReport, callback_data: "rep" }]] });
    return;
  }
  if (data === "dl") return sendDownload(env, chat, t);
  if (data === "rep") return startReport(env, chat, t);
  if (data === "cancel") { await setMode(env, chat, null); return edit(env, msg, t.reportCancel, menu(t)); }
}

function menu(t) {
  return {
    inline_keyboard: [
      [{ text: t.bDownload, callback_data: "dl" }, { text: t.bFaq, callback_data: "faq" }],
      [{ text: t.bReport, callback_data: "rep" }],
      [{ text: t.bChannel, url: LINKS.channel }, { text: t.bGithub, url: LINKS.github }],
    ],
  };
}

function faqMenu(t) {
  return { inline_keyboard: [...t.faq.map((f) => [{ text: f.q, callback_data: "q:" + f.k }]), [{ text: t.bMenu, callback_data: "menu" }]] };
}

async function sendDownload(env, chat, t) {
  let r;
  try { r = await latestRelease(env, false); } catch (e) { console.log("download info failed", e && e.message); }
  if (!r) return send(env, chat, t.downloadFail);
  return send(env, chat, t.download(r), {
    inline_keyboard: [[{ text: t.bRelease, url: releaseUrl(r.version) }], [{ text: t.bMenu, callback_data: "menu" }]],
  });
}

// ─────────────────────────────── сообщения об ошибках ───────────────────────────────

async function startReport(env, chat, t) {
  if (!env.ADMIN_ID || !env.BOT_KV) return send(env, chat, t.reportOff, menu(t));
  await setMode(env, chat, "report");
  return send(env, chat, t.reportAsk, { inline_keyboard: [[{ text: t.bCancel, callback_data: "cancel" }]] });
}

async function forwardReport(env, m, t) {
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
  if (group) return; // остальные снимки альбома: без повторных заголовков, счётчика и «Отправлено»
  await kvPut(env, countKey, String(count + 1), 60 * 60 * 48);
  return send(env, chat, t.reportSent);
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
async function publishPosts(env, now) {
  const kv = env.BOT_KV;
  if (!kv) return;
  const origin = await kv.get("origin");
  if (!origin) return; // /setup ещё не открывали
  const channel = (env.CHANNEL || DEFAULT_CHANNEL).trim();
  let sent = 0;
  for (const p of POSTS) {
    if (Date.parse(p.at) > now) break;
    if (await kv.get(`post:${p.id}`)) continue;
    if (sent >= 3) break;
    await kv.put(`post:${p.id}`, "sending");
    const base = { chat_id: channel, parse_mode: "HTML" };
    const url = `${origin}/media/${p.media}`;
    const res = p.kind === "photo" ? await tg(env, "sendPhoto", { ...base, photo: url, caption: p.text })
      : p.kind === "video" ? await tg(env, "sendVideo", { ...base, video: url, caption: p.text, supports_streaming: true, width: 1080, height: 1920 })
      : await tg(env, "sendMessage", { ...base, text: p.text, link_preview_options: { is_disabled: !p.preview } });
    if (res.ok) {
      await kvPut(env, `post:${p.id}`, "done:" + (res.result && res.result.message_id));
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
    reply_markup: { inline_keyboard: [[{ text: "⬇ Скачать и все изменения", url: releaseUrl(r.version) }]] },
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
export const _test = { T, POSTS, MEDIA, parseReleaseBody, parseNotes, releasePost, cmpVersion, webhookSecret, esc, langOf };
