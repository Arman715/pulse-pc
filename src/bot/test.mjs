// Проверка бота без Telegram: запросы к api.telegram.org перехватываются, GitHub — настоящий.
// Запуск: node test.mjs
import worker, { _test } from "./worker.js";

let failed = 0;
const ok = (cond, name) => { console.log((cond ? "  ok   " : "  FAIL ") + name); if (!cond) failed++; };

// ── заглушки ──
class KV {
  constructor() { this.m = new Map(); }
  async get(k, type) { const v = this.m.get(k); if (v == null) return null; return type === "json" ? JSON.parse(v) : v; }
  async put(k, v, opts) { this.m.set(k, v); this.ttl = { ...(this.ttl || {}), [k]: opts && opts.expirationTtl }; }
  async delete(k) { this.m.delete(k); }
}
const realFetch = globalThis.fetch;
let calls = [];
let tgFail = {};          // метод → описание ошибки
let msgId = 1000;
let githubOverride = null; // (url) => Response | null
let memberStatus = "member"; // что Telegram ответит на getChatMember
globalThis.fetch = async (url, init) => {
  url = String(url);
  if (url.startsWith("https://api.telegram.org/")) {
    const method = url.split("/").pop();
    const body = JSON.parse(init.body);
    calls.push({ method, body });
    if (tgFail[method]) return Response.json({ ok: false, description: tgFail[method] });
    if (method === "getChatMember") return Response.json({ ok: true, result: { status: memberStatus, user: { id: body.user_id } } });
    if (method === "sendMessage" || method === "copyMessage") { calls[calls.length - 1].id = ++msgId; return Response.json({ ok: true, result: { message_id: msgId } }); }
    return Response.json({ ok: true, result: true });
  }
  if (githubOverride) { const r = githubOverride(url); if (r) return r; }
  return realFetch(url, init);
};

const TOKEN = "123456:TEST-token";
const ADMIN = 777;
const env = (extra = {}) => ({ BOT_TOKEN: TOKEN, ADMIN_ID: String(ADMIN), BOT_KV: new KV(), ...extra });
const secret = await _test.webhookSecret({ BOT_TOKEN: TOKEN });
const post = (e, update, sec = secret) =>
  worker.fetch(new Request("https://bot.example.workers.dev/tg", {
    method: "POST", headers: { "x-telegram-bot-api-secret-token": sec, "content-type": "application/json" }, body: JSON.stringify(update),
  }), e, {});
let uid = 1;
const user = (lang = "ru", id = 42) => ({ id, is_bot: false, first_name: "Иван", username: "ivan", language_code: lang });
const msg = (text, from = user(), extra = {}) => ({ update_id: uid++, message: { message_id: uid, chat: { id: from.id, type: "private" }, from, text, ...extra } });
const cb = (data, from = user()) => ({ update_id: uid++, callback_query: { id: "q" + uid, from, data, message: { message_id: 5, chat: { id: from.id, type: "private" } } } });
const sent = (m) => calls.filter((c) => c.method === m);
const last = () => calls[calls.length - 1];

// Только разрешённая Telegram HTML-разметка и закрытые теги.
function validHtml(s) {
  const tags = [...s.matchAll(/<\/?([a-z]+)[^>]*>/g)];
  const allowed = new Set(["b", "i", "a", "code", "pre", "u", "s"]);
  const stack = [];
  for (const t of tags) {
    if (!allowed.has(t[1])) return false;
    if (t[0].startsWith("</")) { if (stack.pop() !== t[1]) return false; } else stack.push(t[1]);
  }
  return stack.length === 0 && !/&(?!amp;|lt;|gt;|quot;)/.test(s);
}

// ── 1. вебхук ──
console.log("Вебхук и настройка");
{
  const e = env();
  let r = await post(e, msg("/start"), "wrong");
  ok(r.status === 403, "чужой запрос без секрета отклоняется (403)");
  calls = [];
  r = await worker.fetch(new Request("https://bot.example.workers.dev/setup"), e, {});
  const txt = await r.text();
  const hook = sent("setWebhook")[0];
  ok(hook && hook.body.url === "https://bot.example.workers.dev/tg" && hook.body.secret_token === secret, "/setup ставит вебхук на свой адрес с секретом");
  ok(/^[0-9a-f]{48}$/.test(secret), "секрет — 48 hex-символов (допустим для Telegram)");
  ok(sent("setMyCommands").length === 11 && sent("setMyDescription").length === 11, "команды и описание: по умолчанию + 10 русскоязычных кодов");
  ok(sent("setMyCommands").find((c) => !c.body.language_code).body.commands[0].description === "Menu", "по умолчанию — английский (как и ответы бота)");
  ok(sent("setMyCommands").find((c) => c.body.language_code === "ru").body.commands[0].description === "Меню", "для ru — русский");
  ok(!hook.body.drop_pending_updates && hook.body.max_connections === 1, "очередь сообщений не сбрасывается, обработка по одному");
  const again = await worker.fetch(new Request("https://bot.example.workers.dev/setup"), e, {});
  ok(again.status === 429, "повторный /setup в течение минуты ничего не делает");
  ok(!txt.includes(TOKEN) && !txt.includes(secret), "ответ /setup не показывает токен и секрет");
  ok(sent("setMyDescription").every((c) => c.body.description.length <= 512) && sent("setMyShortDescription").every((c) => c.body.short_description.length <= 120), "описания в пределах Telegram (512/120)");
}

// ── 2. меню, скачать, вопросы ──
console.log("Меню, скачать, частые вопросы");
{
  const e = env();
  calls = [];
  await post(e, msg("/start"));
  ok(last().method === "sendMessage" && last().body.reply_markup.inline_keyboard.length === 3, "/start — приветствие с меню");
  calls = [];
  await post(e, cb("dl"));
  const dl = sent("sendMessage")[0];
  const rel = await e.BOT_KV.get("rel", "json");
  console.log("        версия с GitHub:", rel && rel.version, rel && rel.date);
  ok(rel && /^\d+\.\d+\.\d+$/.test(rel.version), "версия получена из настоящего update.json");
  ok(dl && dl.body.text.includes(rel.sha256) && dl.body.text.includes(`/releases/download/v${rel.version}/PulsePC-Setup-${rel.version}.exe`), "ссылка на установщик и SHA-256 в сообщении");
  ok(dl && validHtml(dl.body.text), "разметка «Скачать» корректна");
  const head = await realFetch(`https://github.com/Arman715/pulse-pc/releases/download/v${rel.version}/PulsePC-Setup-${rel.version}.exe`, { method: "HEAD", redirect: "follow" });
  ok(head.ok, `ссылка на установщик открывается (HTTP ${head.status})`);
  ok(sent("answerCallbackQuery").length === 1, "нажатие кнопки подтверждается");

  for (const lang of ["ru", "en"]) {
    const t = _test.T[lang];
    ok(t.faq.length === 8 && t.faq.every((f) => f.a.length < 4096 && validHtml(f.a)), `${lang}: 8 ответов, короче 4096, разметка корректна`);
    ok(t.faq.every((f) => f.q.length <= 64), `${lang}: подписи кнопок не длиннее 64`);
    ok([t.welcome, t.faqTitle, t.reportAsk, t.reportSent, t.download({ version: "1.5.0", url: "PulsePC-Setup-1.5.0.exe", sha256: "a".repeat(64), date: "2026-11-22" })].every(validHtml), `${lang}: разметка остальных текстов корректна`);
  }
  ok(_test.T.ru.faq.map((f) => f.k).join() === _test.T.en.faq.map((f) => f.k).join(), "вопросы ru и en совпадают");

  calls = [];
  await post(e, cb("q:ovl"));
  ok(last().method === "editMessageText" && last().body.text.includes("Ctrl+Shift+O"), "ответ на вопрос открывается в том же сообщении");
  calls = [];
  await post(e, cb("faq", user("en")));
  ok(last().body.text.includes("Common questions"), "английский интерфейс для language_code=en");
  calls = [];
  await post(e, cb("faq", user("uk")));
  ok(last().body.text.includes("Частые вопросы"), "украинский язык — русский интерфейс");
  calls = [];
  await post(e, msg("привет"));
  ok(last().body.text.includes("Сообщить об ошибке"), "свободный текст вне режима сообщения — подсказка, а не пересылка");
  ok(sent("copyMessage").length === 0, "ничего не переслано разработчику");
  calls = [];
  await post(e, { update_id: uid++, message: { message_id: 9, chat: { id: -100, type: "group" }, from: user(), text: "/start" } });
  ok(calls.length === 0, "сообщения в группах игнорируются");
  calls = [];
  await post(e, msg("/id"));
  ok(last().body.text.includes("42"), "/id показывает id");
}

// ── 3. сообщения об ошибках ──
console.log("Сообщения об ошибках");
{
  const e = env();
  calls = [];
  await post(e, cb("rep"));
  ok(last().body.text.includes("PulsePC-report-"), "«Сообщить об ошибке» объясняет, как приложить отчёт");
  calls = [];
  await post(e, msg("Программа падает при запуске оверлея"));
  const head = sent("sendMessage").find((c) => c.body.chat_id === String(ADMIN));
  const copy = sent("copyMessage")[0];
  ok(head && head.body.text.includes("@ivan") && head.body.text.includes("id 42"), "разработчику — кто пишет");
  ok(copy && copy.body.chat_id === String(ADMIN) && copy.body.from_chat_id === 42, "само сообщение скопировано разработчику");
  ok(sent("sendMessage").some((c) => c.body.chat_id === 42 && c.body.text.includes("Отправлено")), "человеку — «Отправлено»");
  // досылка файла
  calls = [];
  await post(e, { update_id: uid++, message: { message_id: 77, chat: { id: 42, type: "private" }, from: user(), document: { file_id: "F", file_name: "PulsePC-report-20261123-120000.zip" } } });
  ok(sent("copyMessage").length === 1, "файл отчёта тоже пересылается");
  const copiedId = sent("copyMessage")[0].id; // id копии у разработчика
  // ответ разработчика
  calls = [];
  const admin = { id: ADMIN, is_bot: false, first_name: "Dev", language_code: "ru" };
  await post(e, { update_id: uid++, message: { message_id: 500, chat: { id: ADMIN, type: "private" }, from: admin, text: "Исправлено в 1.5.1", reply_to_message: { message_id: copiedId } } });
  const toUser = sent("copyMessage")[0];
  ok(toUser && toUser.body.chat_id === 42 && toUser.body.message_id === 500, "ответ разработчика (reply) уходит человеку");
  ok(sent("sendMessage").some((c) => c.body.chat_id === 42 && c.body.text.includes("Ответ разработчика")), "человеку видно, что это ответ разработчика");
  ok(sent("sendMessage").some((c) => c.body.chat_id === ADMIN && c.body.text.includes("Отправлено")), "разработчику — подтверждение");
  calls = [];
  await post(e, { update_id: uid++, message: { message_id: 501, chat: { id: ADMIN, type: "private" }, from: admin, text: "?", reply_to_message: { message_id: 1 } } });
  ok(last().body.text.includes("Не знаю, кому ответить"), "reply не на пересланное — подсказка, никуда не уходит");
  // альбом из трёх снимков
  const eA = env();
  await post(eA, msg("/report", user("ru", 60)));
  calls = [];
  for (let i = 0; i < 3; i++) await post(eA, { update_id: uid++, message: { message_id: 300 + i, media_group_id: "G1", chat: { id: 60, type: "private" }, from: user("ru", 60), photo: [{ file_id: "P" + i }] } });
  ok(sent("copyMessage").length === 3, "альбом: все три снимка пересланы");
  ok(sent("sendMessage").filter((c) => c.body.chat_id === String(ADMIN)).length === 1, "альбом: один заголовок разработчику");
  ok(sent("sendMessage").filter((c) => c.body.chat_id === 60).length === 1, "альбом: одно «Отправлено» человеку");
  ok((await eA.BOT_KV.get(`c:60:${new Date().toISOString().slice(0, 10)}`)) === "1", "альбом считается одним сообщением");
  // сообщение, которое нельзя скопировать
  calls = [];
  tgFail = { copyMessage: "Bad Request: message can't be copied" };
  await post(eA, msg("опрос", user("ru", 60)));
  tgFail = {};
  ok(last().body.text.includes("переслать не даёт") && (await eA.BOT_KV.get(`c:60:${new Date().toISOString().slice(0, 10)}`)) === "1", "не скопировалось — честно сказано, счётчик не тронут");
  // KV недоступен (лимит исчерпан): бот всё равно отвечает
  const broken = { get: async () => { throw new Error("KV GET failed: 429"); }, put: async () => { throw new Error("KV PUT failed: 429"); }, delete: async () => { throw new Error("KV DELETE failed: 429"); } };
  const eB = env({ BOT_KV: broken });
  calls = [];
  await post(eB, msg("/start"));
  ok(last() && last().method === "sendMessage" && last().body.reply_markup, "KV сломан — /start всё равно отвечает меню");
  calls = [];
  await post(eB, cb("dl"));
  ok(sent("sendMessage").some((c) => c.body.text.includes("SHA-256")), "KV сломан — «Скачать» всё равно работает");
  // /start не тратит удаления KV, если удалять нечего
  const eD = env(); let dels = 0; const kvD = eD.BOT_KV; const origDel = kvD.delete.bind(kvD); kvD.delete = async (k) => { dels++; return origDel(k); };
  await post(eD, msg("/start")); await post(eD, msg("/start"));
  ok(dels === 0, "/start без открытого сообщения не удаляет ничего в KV");
  // отмена
  calls = [];
  await post(e, msg("/cancel"));
  await post(e, msg("ещё текст"));
  ok(sent("copyMessage").length === 0, "после /cancel ничего не пересылается");
  // лимит
  const e2 = env();
  await post(e2, msg("/report", user("ru", 55)));
  calls = [];
  for (let i = 0; i < 22; i++) await post(e2, msg("спам " + i, user("ru", 55)));
  ok(sent("copyMessage").length === 20 && last().body.text.includes("много"), "не больше 20 сообщений в сутки от одного человека");
  // не настроено
  const e3 = env({ ADMIN_ID: "" });
  calls = [];
  await post(e3, cb("rep"));
  ok(last().body.text.includes("не настроен"), "без ADMIN_ID бот честно говорит, что приём не настроен");
  // режим «сообщение» живёт час
  ok(e.BOT_KV.ttl && e.BOT_KV.ttl["s:42"] === 3600, "режим сообщения сохраняется на час");
  ok(e.BOT_KV.ttl && Object.entries(e.BOT_KV.ttl).some(([k, v]) => k.startsWith("r:") && v === 2592000), "связь «кому ответить» хранится 30 дней");
}

// ── 4. автопост о выпуске ──
console.log("Автопост о новой версии");
{
  const e = env({ CHANNEL: "@pulsepc_official" });
  calls = [];
  await worker.scheduled({}, e, {});
  const cur = await e.BOT_KV.get("posted");
  ok(cur && sent("sendMessage").length === 0, `первый запуск запоминает ${cur} и ничего не публикует`);
  calls = [];
  await worker.scheduled({}, e, {});
  ok(sent("sendMessage").length === 0, "та же версия — повторного поста нет");

  // настоящий текст выпуска 1.5.0 как пост
  const real = await _test.releasePost({ version: cur, notes: [] });
  console.log("\n" + real.split("\n").map((l) => "        | " + l).join("\n") + "\n");
  ok(real.length < 4096 && validHtml(real), `пост из настоящего выпуска: ${real.length} знаков, разметка корректна`);
  ok(/Оверлей поверх игры/.test(real) && /Исправлено ошибок: \d+/.test(real), "в посте — заголовки новинок и число исправлений");

  // новая версия: подменяем update.json на 9.9.9
  const relNow = await e.BOT_KV.get("rel", "json");
  githubOverride = (url) => url.endsWith("/update.json") ? Response.json({ ...relNow, url: "PulsePC-Setup-9.9.9.exe", version: "9.9.9" })
    : url.includes("/releases/tags/v9.9.9") ? new Response("not found", { status: 404 }) : null;
  calls = [];
  tgFail = { sendMessage: "Bad Request: chat not found" };
  await worker.scheduled({}, e, {});
  ok((await e.BOT_KV.get("posted")) === cur, "пост не вышел — версия не отмечена, будет повтор");
  ok(sent("sendMessage").some((c) => c.body.chat_id === String(ADMIN)), "разработчику сказано, что бот не смог опубликовать");
  calls = [];
  await worker.scheduled({}, e, {});
  ok(sent("sendMessage").filter((c) => c.body.chat_id === String(ADMIN)).length === 0, "о той же ошибке второй раз не пишет");
  tgFail = {};
  calls = [];
  await worker.scheduled({}, e, {});
  const p = sent("sendMessage").find((c) => c.body.chat_id === "@pulsepc_official");
  ok(p && p.body.text.includes("Вышел Пульс ПК 9.9.9") && validHtml(p.body.text), "новая версия — пост в канал (запасной текст из update.json)");
  ok(p && p.body.reply_markup.inline_keyboard[0][0].url.endsWith("/releases/tag/v9.9.9"), "кнопка ведёт на страницу выпуска");
  ok((await e.BOT_KV.get("posted")) === "9.9.9", "версия отмечена опубликованной");
  console.log("\n" + p.body.text.split("\n").map((l) => "        | " + l).join("\n") + "\n");
  githubOverride = null;

  // защита от повторного поста: пост ушёл, а «posted» не записался
  const eP = env({ CHANNEL: "@pulsepc_official" });
  await eP.BOT_KV.put("posted", "1.0.0");
  const origPut = eP.BOT_KV.put.bind(eP.BOT_KV);
  eP.BOT_KV.put = async (k, v, o) => { if (k === "posted") throw new Error("KV PUT failed: 429"); return origPut(k, v, o); };
  calls = [];
  await worker.scheduled({}, eP, {});
  await worker.scheduled({}, eP, {});
  await worker.scheduled({}, eP, {});
  ok(sent("sendMessage").filter((c) => c.body.chat_id === "@pulsepc_official").length === 1, "KV не записал «posted» — пост всё равно один, без повторов");

  ok(_test.cmpVersion("1.10.0", "1.9.9") > 0 && _test.cmpVersion("1.5.0", "1.5.0") === 0 && _test.cmpVersion("1.4.3", "1.5.0") < 0, "сравнение версий");
}

// ── 4б. мягкая подписка на канал ──
console.log("Предложение подписаться на канал");
{
  const e = env();
  memberStatus = "left";
  calls = [];
  await post(e, msg("/start", user("ru", 900)));
  let w = sent("sendMessage").find((c) => c.body.chat_id === 900);
  ok(w.body.reply_markup.inline_keyboard[0][0].text.includes("Подписаться") && w.body.text.includes("@pulsepc_official"), "неподписанному — первая кнопка «Подписаться на канал»");
  ok(w.body.reply_markup.inline_keyboard.flat().some((b) => b.callback_data === "dl") && w.body.reply_markup.inline_keyboard.flat().some((b) => b.callback_data === "rep"), "ничего не закрыто: «Скачать» и «Сообщить об ошибке» на месте");
  calls = [];
  await post(e, cb("dl", user("ru", 900)));
  ok(sent("sendMessage").length === 2 && sent("sendMessage")[1].body.text.includes("Подпишитесь"), "после «Скачать» — одно напоминание о канале");
  calls = [];
  await post(e, cb("dl", user("ru", 900)));
  ok(sent("sendMessage").length === 1, "второй раз не напоминает");
  // после отправленного сообщения об ошибке — другой человек
  await post(e, msg("/report", user("ru", 901)));
  calls = [];
  await post(e, msg("не работает", user("ru", 901)));
  const toUser = sent("sendMessage").filter((c) => c.body.chat_id === 901).map((c) => c.body.text);
  ok(toUser.length === 2 && toUser[0].includes("Отправлено") && toUser[1].includes("Подпишитесь"), "после «Отправлено» — напоминание о канале");
  memberStatus = "member";
  calls = [];
  await post(e, msg("/start", user("ru", 902)));
  w = sent("sendMessage").find((c) => c.body.chat_id === 902);
  ok(!w.body.text.includes("Подпишитесь") && !w.body.reply_markup.inline_keyboard[0][0].text.includes("Подписаться"), "подписчику — без напоминаний");
  calls = [];
  await post(e, cb("dl", user("ru", 902)));
  ok(sent("sendMessage").length === 1, "подписчику после «Скачать» — без напоминания");
  tgFail = { getChatMember: "Bad Request: member list is inaccessible" };
  calls = [];
  await post(e, msg("/start", user("ru", 903)));
  ok(!sent("sendMessage")[0].body.text.includes("Подпишитесь"), "Telegram не ответил о подписке — не напоминаем");
  tgFail = {};
  memberStatus = "left";
  calls = [];
  await post(e, msg("/start", user("en", 904)));
  ok(sent("sendMessage")[0].body.reply_markup.inline_keyboard[0][0].text.includes("Join"), "по-английски — «Join the channel»");
  memberStatus = "member";
}

// ── 5. посты по расписанию ──
console.log("Посты канала по расписанию");
{
  const { POSTS, MEDIA } = _test;
  const fs = await import("node:fs");
  const strip = (t) => t.replace(/<[^>]+>/g, "").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
  ok(POSTS.length === 9, "9 постов в расписании");
  ok(POSTS.every((p, i) => i === 0 || Date.parse(p.at) >= Date.parse(POSTS[i - 1].at)), "посты идут по времени");
  ok(POSTS.every((p) => !p.media || (MEDIA[p.media] && fs.existsSync(new URL("./media/" + p.media, import.meta.url)))), "у каждого поста есть свой файл в media/");
  ok(POSTS.every((p) => validHtml(p.text)), "разметка всех постов корректна");
  ok(POSTS.every((p) => (p.kind === "text" ? strip(p.text).length <= 4096 : strip(p.text).length <= 1024)), "подписи ≤ 1024, тексты ≤ 4096 знаков");
  ok(POSTS.filter((p) => p.media).every((p) => fs.statSync(new URL("./media/" + p.media, import.meta.url)).size <= (p.kind === "photo" ? 5e6 : 20e6)), "файлы в пределах Telegram по ссылке (фото ≤5 МБ, видео ≤20 МБ)");

  const e = env({ CHANNEL: "@pulsepc_official" });
  const day1 = Date.parse("2026-11-22T22:30:00Z");
  const toChannel = () => calls.filter((c) => c.body.chat_id === "@pulsepc_official" && /^send(Photo|Video|Message)$/.test(c.method));
  calls = [];
  await worker.scheduled({ scheduledTime: day1 }, e, {});
  ok(toChannel().length === 0, "без /setup (нет адреса) посты не выходят");
  await e.BOT_KV.put("origin", "https://bot.example.workers.dev");
  calls = [];
  await worker.scheduled({ scheduledTime: day1 }, e, {});
  const out = toChannel();
  ok(out.map((c) => c.method).join() === "sendPhoto,sendPhoto,sendVideo", "день 1: знакомство, «что нового», видео — по порядку");
  ok(out[0].body.photo === "https://bot.example.workers.dev/media/zakrep.png" && out[2].body.video.endsWith("/media/obzor-1.5.mp4"), "картинки и видео — по ссылке бота");
  ok(out[0].body.caption.includes("@pulsepc_official_bot") && out[0].body.parse_mode === "HTML", "в знакомстве есть ссылка на бота");
  calls = [];
  await worker.scheduled({ scheduledTime: day1 + 30 * 60e3 }, e, {});
  ok(toChannel().length === 0, "повторный запуск — без повторов");
  calls = [];
  await worker.scheduled({ scheduledTime: Date.parse("2026-11-25T16:00:00Z") }, e, {});
  ok(toChannel().map((c) => c.method).join() === "sendPhoto", "25 ноября в 19:00 МСК — один пост «Почему тормозит»");
  // сбой на посте 5: отметка снимается, разработчику — один раз, пост 6 ждёт
  tgFail = { sendPhoto: "Bad Request: wrong type of the web page content" };
  calls = [];
  await worker.scheduled({ scheduledTime: Date.parse("2026-12-01T16:00:00Z") }, e, {});
  ok(!(await e.BOT_KV.get("post:p5")) && !(await e.BOT_KV.get("post:p6")), "пост не вышел — не отмечен, следующий ждёт");
  ok(sent("sendMessage").some((c) => c.body.chat_id === String(ADMIN) && c.body.text.includes("Оверлей")), "разработчику сказано, какой пост не вышел");
  calls = [];
  await worker.scheduled({ scheduledTime: Date.parse("2026-12-01T16:30:00Z") }, e, {});
  ok(!sent("sendMessage").some((c) => c.body.chat_id === String(ADMIN)), "о том же сбое второй раз не пишет");
  tgFail = {};
  calls = [];
  await worker.scheduled({ scheduledTime: Date.parse("2026-12-01T17:00:00Z") }, e, {});
  ok(toChannel().map((c) => c.method).join() === "sendPhoto,sendVideo", "после сбоя: «Оверлей», затем видео про заряд");
  calls = [];
  await worker.scheduled({ scheduledTime: Date.parse("2026-12-31T00:00:00Z") }, e, {});
  const rest = toChannel();
  ok(rest.map((c) => c.method).join() === "sendPhoto,sendMessage,sendMessage", "остальные три поста выходят по порядку");
  ok(rest[1].body.link_preview_options.is_disabled === false && rest[2].body.link_preview_options.is_disabled === true, "у поста про YouTube — превью ссылки, у остальных нет");

  ok(out.every((c) => c.body.reply_markup.inline_keyboard[0].map((b) => b.url).join() === "https://github.com/Arman715/pulse-pc/releases/latest,https://t.me/pulsepc_official_bot"), "под каждым постом — кнопки «Скачать» и «Вопросы и ошибки»");

  // посты, вышедшие до появления кнопок, получают кнопки
  const eR = env({ CHANNEL: "@pulsepc_official" });
  await eR.BOT_KV.put("origin", "https://bot.example.workers.dev");
  await eR.BOT_KV.put("post:p1", "done:55"); await eR.BOT_KV.put("post:p2", "done:56"); await eR.BOT_KV.put("post:p3", "done:57");
  calls = [];
  await worker.scheduled({ scheduledTime: day1 }, eR, {});
  ok(sent("editMessageReplyMarkup").map((c) => c.body.message_id).join() === "55,56,57" && toChannel().length === 0, "старым постам добавлены кнопки, новые посты не повторяются");
  ok((await eR.BOT_KV.get("post:p1")) === "done:55:btn", "отмечено, что кнопки уже есть");
  calls = [];
  await worker.scheduled({ scheduledTime: day1 + 30 * 60e3 }, eR, {});
  ok(sent("editMessageReplyMarkup").length === 0, "второй раз кнопки не добавляются");

  // раздача файлов
  githubOverride = (url) => url.includes("/src/bot/media/zaryad.mp4") ? new Response("VIDEO", { headers: { "content-type": "application/octet-stream" } }) : null;
  let r = await worker.fetch(new Request("https://bot.example.workers.dev/media/zaryad.mp4"), env(), {});
  ok(r.status === 200 && r.headers.get("content-type") === "video/mp4" && (await r.text()) === "VIDEO", "видео отдаётся с типом video/mp4");
  r = await worker.fetch(new Request("https://bot.example.workers.dev/media/..%2Fworker.js"), env(), {});
  ok(r.status === 404, "чужие файлы не раздаются");
  r = await worker.fetch(new Request("https://bot.example.workers.dev/media/README.md"), env(), {});
  ok(r.status === 404, "файлы не из списка не раздаются");
  githubOverride = null;
}

console.log(failed ? `\nПРОВАЛЕНО: ${failed}` : "\nВсе проверки прошли.");
process.exit(failed ? 1 : 0);
