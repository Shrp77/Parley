namespace Parley.Bridge;

/// <summary>
/// The page, served from memory.
/// </summary>
/// <remarks>
/// <para>
/// One file, no build step, no package manager, no external request. A dashboard whose job is to
/// be up when the deployment is in trouble should not have a toolchain between the source and
/// what runs, and a fleet console that fetched anything from the network would fail in exactly
/// the conditions an operator most needs it.
/// </para>
/// <para>
/// It reads counts and names. It never composes a message, because there is no endpoint to
/// compose one to: identity comes from an agent's launch environment, and an operator who could
/// speak as <c>docs</c> would make every provenance claim in the protocol unfalsifiable.
/// </para>
/// </remarks>
public static class Ui
{
    /// <summary>The whole console.</summary>
    public const string Page = """
<!doctype html>
<meta charset="utf-8">
<title>Parley</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
:root{--bg:#fbfbfa;--panel:#fff;--ink:#1a1a19;--dim:#6b6b68;--line:#e4e4e1;
--live:#1a7f4b;--doubt:#b7791f;--gone:#b91c1c;--off:#6b6b68;--accent:#2563eb}
@media(prefers-color-scheme:dark){:root{--bg:#14141a;--panel:#1c1c24;--ink:#e8e8e6;
--dim:#9a9a97;--line:#2e2e39;--live:#4ade80;--doubt:#fbbf24;--gone:#f87171;--off:#8a8a93}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
font:14px/1.5 ui-sans-serif,system-ui,-apple-system,Segoe UI,sans-serif}
header{padding:14px 20px;border-bottom:1px solid var(--line);display:flex;
align-items:baseline;gap:14px}
h1{font-size:15px;margin:0;letter-spacing:.02em}
.muted{color:var(--dim);font-size:12px}
main{display:grid;grid-template-columns:minmax(0,1.35fr) minmax(0,1fr);gap:16px;padding:16px}
@media(max-width:960px){main{grid-template-columns:1fr}}
section{background:var(--panel);border:1px solid var(--line);border-radius:8px;overflow:hidden}
h2{font-size:12px;text-transform:uppercase;letter-spacing:.08em;color:var(--dim);
margin:0;padding:10px 14px;border-bottom:1px solid var(--line)}
table{width:100%;border-collapse:collapse}
td,th{padding:8px 14px;text-align:left;border-bottom:1px solid var(--line);vertical-align:top}
th{font-weight:500;color:var(--dim);font-size:12px}
tr:last-child td{border-bottom:none}
.name{font-weight:600}
.owns{color:var(--dim);font-size:12px;max-width:38ch}
.pill{font-size:11px;padding:1px 7px;border-radius:99px;border:1px solid currentColor}
.live{color:var(--live)}.doubt{color:var(--doubt)}.gone{color:var(--gone)}.off{color:var(--off)}
.count{display:inline-block;min-width:20px;text-align:center;border-radius:5px;
padding:1px 6px;font-variant-numeric:tabular-nums}
.count.hot{background:var(--gone);color:#fff}
.zero{color:var(--dim)}
button{font:inherit;font-size:12px;padding:3px 9px;border-radius:6px;cursor:pointer;
border:1px solid var(--line);background:transparent;color:var(--ink)}
button:hover{border-color:var(--accent);color:var(--accent)}
button.danger:hover{border-color:var(--gone);color:var(--gone)}
.feed{max-height:38vh;overflow:auto;font-variant-numeric:tabular-nums}
.waiting{color:var(--gone);font-weight:600}
.orph{color:var(--gone)}
#console{font:12px/1.45 ui-monospace,SFMono-Regular,Menlo,monospace;height:34vh;
overflow:auto;padding:10px 14px;white-space:pre-wrap;word-break:break-word}
#console .lifecycle{color:var(--accent)}
#console .error{color:var(--gone)}
#console .text{color:var(--ink)}
#console .tool{color:var(--doubt)}
#console .dim{color:var(--dim)}
.composer{display:flex;gap:8px;padding:10px 14px;border-top:1px solid var(--line)}
.composer input{flex:1;font:inherit;padding:6px 9px;border-radius:6px;
border:1px solid var(--line);background:var(--bg);color:var(--ink)}
.err{color:var(--gone);padding:8px 14px;font-size:12px}
</style>

<header>
  <h1>Parley</h1>
  <span class="muted" id="watching"></span>
  <span class="muted" id="pulse" style="margin-left:auto"></span>
</header>

<main>
  <div>
    <section>
      <h2>Fleet</h2>
      <table>
        <thead><tr><th>Agent</th><th>Status</th><th>Waiting</th><th>Owes</th><th></th></tr></thead>
        <tbody id="fleet"></tbody>
      </table>
    </section>

    <section style="margin-top:16px">
      <h2>Exchanges</h2>
      <div class="feed"><table><tbody id="exchanges"></tbody></table></div>
    </section>

    <section style="margin-top:16px">
      <h2>Traffic</h2>
      <div class="feed"><table><tbody id="traffic"></tbody></table></div>
    </section>
  </div>

  <div>
    <section>
      <h2 id="consoleTitle">Console</h2>
      <div id="console"><span class="dim">Start an agent to watch it work. Only agents this
bridge started can be watched: a process's output belongs to whoever spawned it, so one you
launched in your own terminal is visible through the store and no further.</span></div>
      <div class="composer">
        <input id="say" placeholder="Give the agent a turn..." disabled>
        <button id="send" disabled>Send</button>
      </div>
      <div class="err" id="err"></div>
    </section>
  </div>
</main>

<script>
const token = new URLSearchParams(location.search).get('t') || '';
const api = (path, init = {}) => fetch(path, {
  ...init, headers: { ...(init.headers || {}), 'X-Parley-Token': token,
                      ...(init.body ? { 'Content-Type': 'application/json' } : {}) } });

let selected = null;
const lines = new Map();

const el = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const shade = s => s === 'live' ? 'live'
  : s === 'not responding' ? 'doubt'
  : s === 'unreachable' || s === 'retired' ? 'gone' : 'off';

const ago = iso => {
  if (!iso) return 'never';
  const s = Math.max(0, (Date.now() - Date.parse(iso)) / 1000);
  if (s < 60) return Math.floor(s) + 's ago';
  if (s < 3600) return Math.floor(s / 60) + 'm ago';
  if (s < 86400) return Math.floor(s / 3600) + 'h ago';
  return Math.floor(s / 86400) + 'd ago';
};

const fail = m => { el('err').textContent = m || ''; };

async function act(name, verb) {
  fail('');
  const r = await api(`/api/agents/${encodeURIComponent(name)}/${verb}`, { method: 'POST' });
  if (!r.ok) { fail(await r.text()); return; }
  if (verb === 'purge') {
    const o = await r.json();
    if (o.verdict === 'Held') {
      fail('Refused: still referenced by ' +
        o.held.map(h => `${h.rows} in ${h.table}.${h.column}`).join(', '));
    }
  }
  draw();
}

function drawFleet(agents) {
  el('fleet').innerHTML = agents.map(a => `
    <tr>
      <td><div class="name">${esc(a.name)}</div>
          <div class="owns">${esc(a.owns || '')}</div></td>
      <td><span class="pill ${shade(a.status)}">${esc(a.status)}</span>
          <div class="muted">${esc(ago(a.lastSeenUtc))}</div></td>
      <td><span class="count ${a.uncollected ? 'hot' : 'zero'}">${a.uncollected}</span></td>
      <td><span class="count ${a.owed ? 'hot' : 'zero'}">${a.owed}</span></td>
      <td style="white-space:nowrap">
        ${a.hosted
          ? `<button onclick="watch('${esc(a.name)}')">Watch</button>
             <button onclick="act('${esc(a.name)}','stop')">Stop</button>`
          : `<button onclick="act('${esc(a.name)}','start')">Start</button>`}
        <button class="danger" onclick="act('${esc(a.name)}','retire')">Retire</button>
      </td>
    </tr>`).join('') || '<tr><td class="muted">No agents registered.</td></tr>';
}

function drawExchanges(rows) {
  el('exchanges').innerHTML = rows.map(x => `
    <tr>
      <td>#${x.id}</td>
      <td class="${x.state === 'ORPHANED' ? 'orph' : ''}">${esc(x.state)}</td>
      <td>${esc(x.asker)} &rarr; ${esc(x.answerer)}</td>
      <td class="muted">${esc(ago(x.openedUtc))}</td>
    </tr>`).join('') || '<tr><td class="muted">Nothing open.</td></tr>';
}

function drawTraffic(rows) {
  el('traffic').innerHTML = rows.map(m => `
    <tr>
      <td>#${m.id}</td>
      <td>${esc(m.type)}</td>
      <td>${esc(m.from)} &rarr; ${esc(m.to)}</td>
      <td class="${m.deliveredUtc ? 'muted' : 'waiting'}">
        ${m.deliveredUtc ? 'collected' : 'waiting'}</td>
      <td class="muted">${esc(ago(m.createdUtc))}</td>
    </tr>`).join('') || '<tr><td class="muted">No messages yet.</td></tr>';
}

async function draw() {
  const r = await api('/api/fleet');
  if (!r.ok) { fail('Cannot read the store.'); return; }
  const s = await r.json();
  drawFleet(s.agents); drawExchanges(s.exchanges); drawTraffic(s.traffic);
  el('pulse').textContent = 'updated ' + new Date().toLocaleTimeString();
}

function render(line) {
  const box = el('console');
  const stick = box.scrollTop + box.clientHeight >= box.scrollHeight - 30;
  const span = document.createElement('div');

  if (line.kind !== 'event') {
    span.className = line.kind;
    span.textContent = line.text;
  } else {
    let e; try { e = JSON.parse(line.text); } catch { e = null; }
    const parts = [];
    if (e && e.type === 'assistant') {
      for (const b of (e.message?.content || [])) {
        if (b.type === 'text' && b.text.trim()) parts.push(['text', b.text.trim()]);
        if (b.type === 'tool_use') parts.push(['tool', '→ ' + b.name]);
      }
    } else if (e && e.type === 'result') {
      parts.push(['dim', `turns ${e.num_turns ?? '?'}  $${(e.total_cost_usd ?? 0).toFixed(4)}`]);
    } else if (e && e.type === 'system') {
      parts.push(['dim', 'session ' + (e.session_id || '').slice(0, 8)]);
    }
    if (!parts.length) return;
    span.innerHTML = parts.map(([c, t]) => `<div class="${c}">${esc(t)}</div>`).join('');
  }

  box.appendChild(span);
  if (stick) box.scrollTop = box.scrollHeight;
}

async function watch(name) {
  selected = name;
  el('consoleTitle').textContent = 'Console — ' + name;
  el('console').innerHTML = '';
  el('say').disabled = el('send').disabled = false;
  const r = await api(`/api/agents/${encodeURIComponent(name)}/transcript`);
  if (r.ok) for (const line of await r.json()) render(line);
}

el('send').onclick = async () => {
  const text = el('say').value.trim();
  if (!text || !selected) return;
  el('say').value = '';
  const r = await api(`/api/agents/${encodeURIComponent(selected)}/send`,
    { method: 'POST', body: JSON.stringify({ text }) });
  if (!r.ok) fail(await r.text());
};
el('say').addEventListener('keydown', e => { if (e.key === 'Enter') el('send').click(); });

// Pushed to, never polling. The bridge notices commits with one pragma against a local file.
async function listen() {
  while (true) {
    try {
      const r = await api('/api/stream');
      const reader = r.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let cut;
        while ((cut = buffer.indexOf('\n\n')) >= 0) {
          const frame = buffer.slice(0, cut); buffer = buffer.slice(cut + 2);
          const kind = /event: (.*)/.exec(frame)?.[1];
          const data = JSON.parse(/data: (.*)/s.exec(frame)?.[1] || '{}');
          if (kind === 'changed') draw();
          if (kind === 'line' && data.agent === selected) render(data);
        }
      }
    } catch { /* the bridge went away; try again below */ }
    await new Promise(r => setTimeout(r, 1000));
  }
}

el('watching').textContent = 'watching the store';
draw(); listen(); setInterval(draw, 15000);
</script>
""";
}
