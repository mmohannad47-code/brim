namespace Brim.Core;

/// <summary>The page a phone opens to swap files, photos, links and text with this PC (see <see cref="PhoneLink"/>).</summary>
static class PhonePage
{
    public static string Html(string pc) => Page.Replace("{{PC}}", System.Net.WebUtility.HtmlEncode(pc));

    public const string Locked = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Brim</title><style>body{font:16px -apple-system,system-ui,sans-serif;background:#111114;color:#f2f2f5;display:grid;place-items:center;min-height:100vh;margin:0;padding:24px;box-sizing:border-box;text-align:center}p{color:#9a9aa3;max-width:320px}</style></head>
<body><div><h2>This link has expired</h2><p>On your PC, open Brim's Phone window (the Phone button on the notch's Shelf) and scan the QR code again.</p></div></body></html>
""";

    const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Brim">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="theme-color" content="#111114">
<title>Brim · {{PC}}</title>
<style>
:root{--bg:#111114;--card:#1c1c21;--line:#2b2b33;--text:#f2f2f5;--dim:#9a9aa3;--accent:#ff8fab;--onaccent:#1a0c10;--ok:#5fd38d;--bad:#ff6b6b}
@media (prefers-color-scheme:light){:root{--bg:#f2f2f6;--card:#fff;--line:#e3e3ea;--text:#16161a;--dim:#6b6b75;--accent:#e2557c;--onaccent:#fff}}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;background:var(--bg);color:var(--text);font:16px/1.4 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,system-ui,sans-serif;padding:calc(env(safe-area-inset-top) + 14px) 16px calc(env(safe-area-inset-bottom) + 24px)}
header{display:flex;align-items:center;gap:10px;margin:4px 2px 16px}
header h1{font-size:22px;margin:0;font-weight:650;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.dot{width:9px;height:9px;border-radius:50%;background:var(--ok);flex:none}
.dot.off{background:var(--bad)}
.logo{width:30px;height:30px;flex:none}
.status{display:inline-flex;align-items:center;gap:6px;background:var(--card);border:1px solid var(--line);border-radius:999px;padding:5px 10px 5px 9px;flex:none}
.dot{width:7px;height:7px}
#conn{font-size:13px;color:var(--dim)}
.card{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:16px;margin-bottom:14px}
.card h2{font-size:15px;margin:0 0 4px;font-weight:650}
.hint{font-size:13px;color:var(--dim);margin:0 0 12px}
button,.btn{font:inherit;font-size:15px;border:0;border-radius:12px;padding:11px 16px;background:var(--line);color:var(--text);cursor:pointer;text-decoration:none;display:inline-flex;align-items:center;justify-content:center;gap:6px}
button:active,.btn:active{opacity:.75}
button:disabled{opacity:.4}
.primary{background:var(--accent);color:var(--onaccent);font-weight:600}
.big{width:100%;padding:16px;font-size:17px}
.row{display:flex;gap:8px;flex-wrap:wrap}
.row>*{flex:1}
textarea{width:100%;min-height:76px;font:inherit;font-size:16px;color:var(--text);background:var(--bg);border:1px solid var(--line);border-radius:12px;padding:10px 12px;resize:vertical;margin-bottom:10px}
.item{display:flex;align-items:center;gap:12px;padding:10px 0;border-top:1px solid var(--line)}
.item:first-child{border-top:0}
.thumb{width:48px;height:48px;border-radius:10px;background:var(--line);flex:none;object-fit:cover;display:grid;place-items:center;font-size:22px}
.info{flex:1;min-width:0}
.name{font-size:15px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.sub{font-size:12.5px;color:var(--dim);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.acts{display:flex;gap:6px;flex:none}
.acts .btn,.acts button{padding:8px 11px;font-size:14px}
.bar{height:4px;border-radius:2px;background:var(--line);margin-top:6px;overflow:hidden}
.bar i{display:block;height:100%;width:0;background:var(--accent);transition:width .2s}
.empty{color:var(--dim);font-size:14px;padding:6px 0}
.post-text{font-size:15px;white-space:pre-wrap;word-break:break-word}
#toast{position:fixed;left:50%;bottom:calc(env(safe-area-inset-bottom) + 20px);transform:translateX(-50%) translateY(20px);background:var(--text);color:var(--bg);padding:10px 16px;border-radius:12px;font-size:14px;opacity:0;transition:.25s;pointer-events:none;max-width:90vw}
#toast.show{opacity:1;transform:translateX(-50%)}
#clipbox{margin-top:10px;display:none}
input[type=file]{display:none}
[hidden]{display:none!important}
#posts:not(:empty)+#items .item:first-child{border-top:1px solid var(--line)}
.head{display:flex;align-items:center;gap:8px;margin-bottom:4px}
.head h2{flex:1;margin:0}
button.small{padding:6px 12px;font-size:13.5px;border-radius:10px}
.acts button.x{padding:8px 10px;background:transparent;color:var(--dim);font-size:16px;line-height:1}
</style>
</head>
<body>
<header><img class="logo" alt="" src="data:image/png;base64,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"><h1>{{PC}}</h1><span class="status"><span class="dot" id="dot"></span><span id="conn">Connected</span></span></header>

<section class="card">
  <h2>Send to PC</h2>
  <p class="hint">Photos, videos and files land in the PC's Downloads folder and on the notch's Shelf.</p>
  <label class="btn primary big" for="pick">Choose photos or files</label>
  <input type="file" id="pick" multiple>
  <div id="ups"></div>
</section>

<section class="card">
  <h2>Links and text</h2>
  <p class="hint">Send a link or a note to the PC. "Open on PC" opens a link straight away in its browser.</p>
  <textarea id="txt" placeholder="Paste a link or type something"></textarea>
  <div class="row">
    <button id="sendTxt">Send</button>
    <button id="openTxt" class="primary" disabled>Open on PC</button>
  </div>
  <div class="row" style="margin-top:8px"><button id="getClip">Get what's copied on the PC</button></div>
  <div id="clipbox" class="item"></div>
</section>

<section class="card">
  <div class="head"><h2>From your PC</h2><button id="clearAll" class="small" hidden>Clear all</button></div>
  <p class="hint" id="fromHint">Files on the notch's Shelf, and links and text sent from the PC. ✕ clears one from this list; files stay on the PC's Shelf.</p>
  <div id="posts"></div>
  <div id="items"></div>
</section>

<div id="toast"></div>

<script>
const K = new URLSearchParams(location.search).get('k') || '';
const api = p => p + (p.includes('?') ? '&' : '?') + 'k=' + encodeURIComponent(K);
const $ = id => document.getElementById(id);
const iOS = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
if (iOS) $('fromHint').textContent += ' To keep a photo or video in Photos: tap View, then press and hold it (or use the share button).';

function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function toast(t) { const e = $('toast'); e.textContent = t; e.classList.add('show'); clearTimeout(e._t); e._t = setTimeout(() => e.classList.remove('show'), 2200); }
function size(n) { if (!n) return ''; const u = ['B', 'KB', 'MB', 'GB']; let i = 0; while (n >= 1024 && i < 3) { n /= 1024; i++; } return (i ? n.toFixed(1) : n) + ' ' + u[i]; }
function isLink(t) { return /^https?:\/\/\S+$/i.test(t.trim()); }
function copy(t) {
  // The clipboard API only works on https pages; this page is on the home network, so use the old way when needed
  if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(t).then(() => toast('Copied'), () => fallback()); return; }
  fallback();
  function fallback() {
    const a = el('textarea'); a.value = t; a.setAttribute('readonly', ''); a.style.position = 'fixed'; a.style.opacity = '0';
    document.body.appendChild(a); a.select(); a.setSelectionRange(0, t.length);
    try { document.execCommand('copy'); toast('Copied'); } catch (e) { toast('Press and hold to copy'); }
    a.remove();
  }
}
function linkBtn(href, label, cls) { const a = el('a', 'btn' + (cls ? ' ' + cls : ''), label); a.href = href; a.target = '_blank'; a.rel = 'noopener noreferrer'; return a; }

// ---------- sending files
const queue = []; let sending = false;
$('pick').addEventListener('change', e => { for (const f of e.target.files) queue.push(f); e.target.value = ''; next(); });
function next() {
  if (sending || !queue.length) return;
  sending = true;
  const f = queue.shift();
  const row = el('div', 'item'), info = el('div', 'info'), bar = el('div', 'bar'), fill = el('i'), sub = el('div', 'sub', 'Sending…');
  bar.appendChild(fill); info.append(el('div', 'name', f.name), sub, bar);
  row.append(el('div', 'thumb', '⬆'), info); $('ups').prepend(row);
  const x = new XMLHttpRequest();
  x.open('POST', api('/api/up?name=' + encodeURIComponent(f.name)));
  x.upload.onprogress = e => { if (e.lengthComputable) { fill.style.width = (e.loaded / e.total * 100) + '%'; sub.textContent = size(e.loaded) + ' of ' + size(e.total); } };
  x.onload = () => { const ok = x.status === 200; fill.style.width = '100%'; sub.textContent = ok ? 'On your PC ✓' : 'Failed (' + x.status + ')'; if (!ok) fill.style.background = 'var(--bad)'; done(); };
  x.onerror = () => { sub.textContent = "Couldn't reach the PC"; fill.style.background = 'var(--bad)'; done(); };
  x.send(f);
  function done() { sending = false; next(); }
}

// ---------- links and text
const txt = $('txt');
txt.addEventListener('input', () => { $('openTxt').disabled = !isLink(txt.value); });
async function sendText(open) {
  const t = txt.value.trim(); if (!t) return;
  try {
    const r = await fetch(api('/api/text' + (open ? '?open=1' : '')), { method: 'POST', body: t, headers: { 'Content-Type': 'text/plain;charset=utf-8' } });
    if (!r.ok) throw 0;
    toast(open ? 'Opening on your PC' : isLink(t) ? 'Link sent · copied on the PC' : 'Sent · copied on the PC');
    txt.value = ''; $('openTxt').disabled = true;
  } catch (e) { toast("Couldn't reach the PC"); }
}
$('sendTxt').onclick = () => sendText(false);
$('openTxt').onclick = () => sendText(true);
$('getClip').onclick = async () => {
  try {
    const j = await (await fetch(api('/api/clip'))).json();
    const box = $('clipbox'); box.replaceChildren(); box.style.display = 'flex';
    if (!j.text) { box.append(el('div', 'empty', 'Nothing copied on the PC right now.')); return; }
    const info = el('div', 'info'); info.append(el('div', 'post-text', j.text.length > 600 ? j.text.slice(0, 600) + '…' : j.text));
    const acts = el('div', 'acts');
    if (j.link) acts.append(linkBtn(j.text.trim(), 'Open', 'primary'));
    const c = el('button', '', 'Copy'); c.onclick = () => copy(j.text); acts.append(c);
    box.append(info, acts);
  } catch (e) { toast("Couldn't reach the PC"); }
};

// ---------- what the PC offers
let last = '';
function render(s) {
  const posts = $('posts'), items = $('items');
  posts.replaceChildren(); items.replaceChildren();
  for (const p of s.posts) {
    const row = el('div', 'item'), info = el('div', 'info'), acts = el('div', 'acts');
    if (p.link) {
      let host = p.text; try { host = new URL(p.text).hostname.replace(/^www\./, ''); } catch (e) {}
      info.append(el('div', 'name', host), el('div', 'sub', p.text));
      acts.append(linkBtn(p.text, 'Open', 'primary'));
    } else info.append(el('div', 'post-text', p.text), el('div', 'sub', 'Text · ' + p.at));
    const c = el('button', '', 'Copy'); c.onclick = () => copy(p.text); acts.append(c);
    acts.append(xBtn(row, 'post=' + p.id));
    row.append(el('div', 'thumb', p.link ? '🔗' : '✎'), info, acts);
    posts.append(row);
  }
  for (const it of s.items) {
    const row = el('div', 'item'), info = el('div', 'info'), acts = el('div', 'acts');
    let thumb;
    if (it.image) { thumb = el('img', 'thumb'); thumb.loading = 'lazy'; thumb.src = api('/t/' + it.id); thumb.alt = ''; }
    else thumb = el('div', 'thumb', it.folder ? '📁' : it.video ? '🎬' : '📄');
    info.append(el('div', 'name', it.name), el('div', 'sub', it.folder ? 'Folder · comes as a zip' : size(it.size)));
    if (it.image || it.video) acts.append(linkBtn(api('/f/' + it.id + '?view=1'), 'View'));
    const d = linkBtn(api('/f/' + it.id), 'Save', 'primary'); d.setAttribute('download', it.name); d.target = ''; acts.append(d);
    acts.append(xBtn(row, 'item=' + it.id));
    row.append(thumb, info, acts);
    items.append(row);
  }
  const none = !s.posts.length && !s.items.length;
  $('clearAll').hidden = none;
  if (none) items.append(el('div', 'empty', 'Nothing yet. On the PC, drop files on the notch\'s Shelf, or send a link from Brim\'s Phone window.'));
}
// ---------- clearing the list
async function remove(q) {
  try { const r = await fetch(api('/api/remove?' + q), { method: 'POST' }); if (!r.ok) throw 0; }
  catch (e) { toast("Couldn't reach the PC"); }
  last = ''; poll();
}
function xBtn(row, q) {
  const b = el('button', 'x', '✕'); b.setAttribute('aria-label', 'Clear from this list');
  b.onclick = () => { row.remove(); remove(q); };
  return b;
}
$('clearAll').onclick = () => { if (confirm('Clear everything from this list? Files stay on the PC\'s Shelf.')) { $('posts').replaceChildren(); $('items').replaceChildren(); remove('all=1'); } };
async function poll() {
  if (document.visibilityState !== 'visible') return;
  try {
    const r = await fetch(api('/api/state'), { cache: 'no-store' });
    if (r.status === 403) { location.reload(); return; }
    const t = await r.text();
    $('dot').classList.remove('off'); $('conn').textContent = 'Connected';
    if (t !== last) { last = t; render(JSON.parse(t)); }
  } catch (e) { $('dot').classList.add('off'); $('conn').textContent = 'PC not reachable'; }
}
poll(); setInterval(poll, 2500);
document.addEventListener('visibilitychange', poll);
</script>
</body>
</html>
""";
}
