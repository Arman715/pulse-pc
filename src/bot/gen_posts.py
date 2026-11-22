import io, re, json, sys
root = sys.argv[1]
md = io.open(root + '/promo/telegram/КАНАЛ.md', encoding='utf-8').read()
blocks = re.findall(r"^### Пост (\d+) · ([^\n]+)\n```\n(.*?)```", md, re.S | re.M)
assert len(blocks) == 9, len(blocks)
def html(t):
    t = t.rstrip('\n').replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
    return re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', t)
plan = {
 1: ('photo', 'zakrep.png',  '2026-11-22T21:00:00Z'),
 2: ('photo', 'novoe-1.5.png','2026-11-22T21:00:00Z'),
 3: ('video', 'obzor-1.5.mp4','2026-11-22T21:00:00Z'),
 4: ('photo', 'tormozit.png', '2026-11-25T16:00:00Z'),
 5: ('photo', 'overlay.png',  '2026-11-27T16:00:00Z'),
 6: ('video', 'zaryad.mp4',   '2026-11-30T16:00:00Z'),
 7: ('photo', 'vernut.png',   '2026-12-02T16:00:00Z'),
 8: ('text',  None,           '2026-12-04T16:00:00Z'),
 9: ('text',  None,           '2026-12-07T16:00:00Z'),
}
out = []
for n, title, body in blocks:
    kind, media, at = plan[int(n)]
    item = {'id': 'p' + n, 'at': at, 'kind': kind, 'title': title.strip()}
    if media: item['media'] = media
    if int(n) == 8: item['preview'] = True
    item['text'] = html(body)
    out.append('  ' + json.dumps(item, ensure_ascii=False) + ',')
js = ('// ─── ПОСТЫ КАНАЛА: генерируются из promo/telegram/КАНАЛ.md (gen_posts.py), руками не править ───\n'
      '// Время — UTC (16:00 UTC = 19:00 по Москве). Уже вышедший пост повторно не публикуется (ключ post:<id> в KV).\n'
      'const POSTS = [\n' + '\n'.join(out) + '\n];\n// ─── /ПОСТЫ ───\n')
p = root + '/bot/worker.js'
s = io.open(p, encoding='utf-8').read()
if '// ─── ПОСТЫ КАНАЛА' in s:
    s = re.sub(r"// ─── ПОСТЫ КАНАЛА.*?// ─── /ПОСТЫ ───\n", lambda m: js, s, flags=re.S)
else:
    anchor = '// ─────────────────────────────── вход ───────────────────────────────'
    assert s.count(anchor) == 1
    s = s.replace(anchor, js + '\n' + anchor)
io.open(p, 'w', encoding='utf-8').write(s)
for line in out: print(len(json.loads(line.strip().rstrip(','))['text']), line[:90])
