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
<header><svg class="logo" viewBox="0 0 64 64" aria-hidden="true"><defs><linearGradient id="lg" x1="0" x2="1"><stop offset="0" stop-color="#ff8fab"/><stop offset="1" stop-color="#ffb38a"/></linearGradient></defs><rect x="2" y="2" width="60" height="60" rx="16" fill="#16161b"/><circle cx="32" cy="34" r="17" fill="none" stroke="#fff" stroke-opacity=".22" stroke-width="7"/><path d="M17.3 26A17 17 0 0 1 46.7 26" fill="none" stroke="url(#lg)" stroke-width="7" stroke-linecap="round"/></svg><h1>{{PC}}</h1><span class="status"><span class="dot" id="dot"></span><span id="conn">Connected</span></span></header>

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
