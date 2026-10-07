'use strict';
// Puulaakiliiga – league manager. Data lives in the server's SQLite database (see /api).
const EMPTY = { users: [], teams: [], players: [], coaches: [], contacts: [], games: [], penalties: [] };
let db = structuredClone(EMPTY);
let page = 'standings';
const $ = s => document.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let me = null;
const canEdit = () => me && me.role !== 'Viewer';
const isAdmin = () => me?.role === 'Admin';
async function api(method, url, body) {
  const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) });
  if (r.status === 401 && !url.startsWith('/api/auth/')) { me = null; showAuth(); throw new Error('Please sign in'); }
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || (r.status === 403 ? 'You do not have permission' : `Request failed (${r.status})`));
  return r.status === 204 ? null : r.json();
}
async function refresh() {
  try {
    db = { ...EMPTY, ...await api('GET', '/api/data') };
    db.users = isAdmin() ? await api('GET', '/api/users') : [];
    render();
  } catch (e) { toast(e.message); }
}
const guard = fn => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message); } };
const by = (list, id) => db[list].find(x => x.id === +id);
const teamName = id => by('teams', id)?.name ?? '—';
const playerName = id => by('players', id)?.name ?? '—';
const opts = (list, label) => db[list].map(x => [x.id, label(x)]);

// ---- Entity definitions: drive the tables and forms -------------------------
const teamOpts = () => opts('teams', t => t.name);
const E = {
  teams: {
    title: 'Teams', one: 'team',
    fields: [
      { k: 'name', label: 'Team name', req: true },
      { k: 'coachId', label: 'Coach', type: 'select', opts: () => [['', '— none —'], ...opts('coaches', c => c.name)] },
      { k: 'contactId', label: 'Contact person', type: 'select', opts: () => [['', '— none —'], ...opts('contacts', c => c.name)] },
    ],
    cols: [['Team', t => `<b>${esc(t.name)}</b>`], ['Coach', t => esc(by('coaches', t.coachId)?.name ?? '—')],
      ['Contact', t => esc(by('contacts', t.contactId)?.name ?? '—')], ['Players', t => db.players.filter(p => p.teamId === t.id).length, 'num']],
  },
  players: {
    title: 'Players', one: 'player',
    fields: [
      { k: 'name', label: 'Name', req: true },
      { k: 'teamId', label: 'Team', type: 'select', opts: teamOpts, req: true },
      { k: 'number', label: 'Jersey number', type: 'number', min: 0, max: 999 },
      { k: 'position', label: 'Position' },
    ],
    cols: [['#', p => p.number ?? '', 'num'], ['Name', p => `<b>${esc(p.name)}</b>`], ['Team', p => esc(teamName(p.teamId))],
      ['Position', p => esc(p.position)], ['Penalties', p => db.penalties.filter(x => x.playerId === p.id).length, 'num']],
  },
  coaches: {
    title: 'Coaches', one: 'coach',
    fields: [{ k: 'name', label: 'Name', req: true }, { k: 'teamId', label: 'Team', type: 'select', opts: () => [['', '— none —'], ...teamOpts()] }],
    cols: [['Name', c => `<b>${esc(c.name)}</b>`], ['Team', c => esc(teamName(c.teamId))]],
  },
  contacts: {
    title: 'Contacts', one: 'contact',
    fields: [{ k: 'name', label: 'Name', req: true }, { k: 'phone', label: 'Phone', type: 'tel' }, { k: 'email', label: 'Email', type: 'email' }],
    cols: [['Name', c => `<b>${esc(c.name)}</b>`], ['Phone', c => esc(c.phone)], ['Email', c => c.email ? `<a href="mailto:${esc(c.email)}">${esc(c.email)}</a>` : '']],
  },
  games: {
    title: 'Games', one: 'game',
    fields: [
      { k: 'date', label: 'Date & time', type: 'datetime-local', req: true },
      { k: 'homeId', label: 'Home team', type: 'select', opts: teamOpts, req: true },
      { k: 'awayId', label: 'Visiting team', type: 'select', opts: teamOpts, req: true },
      { k: 'homeScore', label: 'Home score (leave empty if not played)', type: 'number', min: 0 },
      { k: 'awayScore', label: 'Visitor score', type: 'number', min: 0 },
      { k: 'field', label: 'Field' },
      { k: 'notes', label: 'Other notes', type: 'textarea' },
    ],
    validate: g => g.homeId === g.awayId ? 'A team cannot play itself.' : (g.homeScore == null) !== (g.awayScore == null) ? 'Enter both scores or neither.' : '',
    sort: (a, b) => (a.date || '').localeCompare(b.date || ''),
    cols: [['Date', g => esc((g.date || '').replace('T', ' '))], ['Match', g => `${esc(teamName(g.homeId))} – ${esc(teamName(g.awayId))}`],
      ['Result', g => g.homeScore == null ? '<span class="pill">upcoming</span>' : `<span class="score">${g.homeScore} – ${g.awayScore}</span>`],
      ['Field', g => esc(g.field)], ['Penalties', g => db.penalties.filter(p => p.gameId === g.id).length, 'num']],
  },
  penalties: {
    title: 'Penalties', one: 'penalty',
    fields: [
      { k: 'playerId', label: 'Player', type: 'select', opts: () => opts('players', p => `${p.name} (${teamName(p.teamId)})`), req: true },
      { k: 'gameId', label: 'Game', type: 'select', opts: () => [['', '— none —'], ...opts('games', g => `${(g.date || '').slice(0, 10)} ${teamName(g.homeId)} – ${teamName(g.awayId)}`)] },
      { k: 'minutes', label: 'Minutes', type: 'number', min: 0 },
      { k: 'reason', label: 'Reason', req: true },
    ],
    cols: [['Player', p => `<b>${esc(playerName(p.playerId))}</b>`], ['Team', p => esc(teamName(by('players', p.playerId)?.teamId))],
      ['Game', p => { const g = by('games', p.gameId); return g ? esc(`${teamName(g.homeId)} – ${teamName(g.awayId)}`) : '—'; }],
      ['Min', p => p.minutes ?? '', 'num'], ['Reason', p => esc(p.reason)]],
  },
};
E.users = {
  title: 'Users', one: 'user',
  fields: [
    { k: 'username', label: 'Username', req: true },
    { k: 'role', label: 'Role (Admin: everything · Manager: edit league data · Viewer: read-only)', type: 'select', opts: () => ['Viewer', 'Manager', 'Admin'].map(r => [r, r]), req: true },
    { k: 'password', label: 'Password (min 8 characters; leave empty to keep the current one)', type: 'password' },
  ],
  sort: (a, b) => a.username.localeCompare(b.username),
  cols: [['Username', u => `<b>${esc(u.username)}</b>${u.username === me?.username ? ' <span class="pill">you</span>' : ''}`], ['Role', u => `<span class="pill">${esc(u.role)}</span>`]],
};
const NAV_ALL = ['standings', 'teams', 'players', 'coaches', 'contacts', 'games', 'penalties', 'users'];
const nav = () => NAV_ALL.filter(n => n !== 'users' || isAdmin());
const LABEL = { users: 'Users',  standings: 'Standings', ...Object.fromEntries(Object.entries(E).map(([k, v]) => [k, v.title])) };

// ---- Standings ---------------------------------------------------------------
function standings() {
  const rows = new Map(db.teams.map(t => [t.id, { t, gp: 0, w: 0, d: 0, l: 0, gf: 0, ga: 0, pts: 0, pim: 0 }]));
  for (const g of db.games) {
    if (g.homeScore == null || !rows.has(g.homeId) || !rows.has(g.awayId)) continue;
    const h = rows.get(g.homeId), a = rows.get(g.awayId);
    h.gp++; a.gp++; h.gf += g.homeScore; h.ga += g.awayScore; a.gf += g.awayScore; a.ga += g.homeScore;
    if (g.homeScore > g.awayScore) { h.w++; a.l++; h.pts += 3; }
    else if (g.homeScore < g.awayScore) { a.w++; h.l++; a.pts += 3; }
    else { h.d++; a.d++; h.pts++; a.pts++; }
  }
  for (const p of db.penalties) { const r = rows.get(by('players', p.playerId)?.teamId); if (r) r.pim += p.minutes || 0; }
  return [...rows.values()].sort((a, b) => b.pts - a.pts || (b.gf - b.ga) - (a.gf - a.ga) || b.gf - a.gf || a.t.name.localeCompare(b.t.name));
}

function renderStandings() {
  const s = standings(), played = db.games.filter(g => g.homeScore != null);
  const goals = played.reduce((n, g) => n + g.homeScore + g.awayScore, 0);
  const stat = (n, l) => `<div class="stat"><b>${n}</b><span>${l}</span></div>`;
  const upcoming = db.games.filter(g => g.homeScore == null).sort(E.games.sort).slice(0, 5);
  const bad = db.players.map(p => [p, db.penalties.filter(x => x.playerId === p.id).reduce((n, x) => n + (x.minutes || 0), 0)]).filter(x => x[1]).sort((a, b) => b[1] - a[1]).slice(0, 5);
  return `<div class="stats">${stat(db.teams.length, 'Teams')}${stat(db.players.length, 'Players')}${stat(played.length, 'Games played')}${stat(played.length ? (goals / played.length).toFixed(1) : '–', 'Goals / game')}</div>
  <h2>League table</h2>${table(['#', 'Team', 'GP', 'W', 'D', 'L', 'GF', 'GA', 'GD', 'PIM', 'Pts'].map((h, i) => [h, null, i > 1 ? 'num' : '']),
    s.map((r, i) => [i + 1, `<b>${esc(r.t.name)}</b>`, r.gp, r.w, r.d, r.l, r.gf, r.ga, r.gf - r.ga, r.pim, `<b>${r.pts}</b>`], ),
    'Add teams and games to see the table. Win = 3 pts, draw = 1.', 2)}
  <h2>Upcoming games</h2>${table([['Date'], ['Match'], ['Field']], upcoming.map(g => [esc(g.date.replace('T', ' ')), `${esc(teamName(g.homeId))} – ${esc(teamName(g.awayId))}`, esc(g.field)]), 'No upcoming games.')}
  <h2>Penalty leaders</h2>${table([['Player'], ['Team'], ['Minutes', null, 'num']], bad.map(([p, m]) => [esc(p.name), esc(teamName(p.teamId)), m]), 'No penalties recorded.')}`;
}

function table(heads, rows, empty, numFrom = -1) {
  if (!rows.length) return `<div class="card empty">${empty}</div>`;
  return `<div class="card"><table><thead><tr>${heads.map(h => `<th class="${h[2] || ''}">${h[0]}</th>`).join('')}</tr></thead><tbody>${
    rows.map(r => `<tr>${r.map((c, i) => `<td class="${(heads[i][2] === 'num' || (numFrom >= 0 && i >= numFrom)) ? 'num' : ''}">${c}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
}

// ---- Entity list view ----------------------------------------------------------
function renderList(name) {
  const e = E[name], q = $('#search').value.trim().toLowerCase();
  let items = [...db[name]];
  if (e.sort) items.sort(e.sort); else items.sort((a, b) => String(a.name ?? '').localeCompare(String(b.name ?? '')));
  if (q) items = items.filter(it => e.cols.map(c => c[1](it)).join(' ').replace(/<[^>]+>/g, '').toLowerCase().includes(q));
  const heads = [...e.cols.map(c => [c[0], null, c[2]]), ...(canEdit() ? [['', null, '']] : [])];
  const rows = items.map(it => [...e.cols.map(c => c[1](it)), ...(canEdit() ? [`<button class="sm" data-edit="${it.id}">Edit</button> <button class="sm danger" data-del="${it.id}">Delete</button>`] : [])]);
  return table(heads, rows, `No ${e.title.toLowerCase()} yet.${canEdit() ? ' Click “+ Add” to create the first one.' : ''}`)
    .replace(/<td class="(num)?">(<button class="sm" data-edit)/g, '<td class="row-actions">$2');
}

// ---- Form dialog ---------------------------------------------------------------
function openForm(name, item) {
  const e = E[name], dlg = $('#dlg'), f = $('#form');
  if (e.fields.some(x => x.type === 'select' && x.req && !x.opts().length)) return toast('Create the required related items first (e.g. a team).');
  f.innerHTML = `<h3>${item ? 'Edit' : 'Add'} ${e.one}</h3>${e.fields.map(x => {
    const v = item?.[x.k] ?? '', a = `name="${x.k}" ${x.req ? 'required' : ''}`;
    const input = x.type === 'select' ? `<select ${a}>${x.opts().map(([id, l]) => `<option value="${id}" ${String(id) === String(v) ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select>`
      : x.type === 'textarea' ? `<textarea ${a} rows="3">${esc(v)}</textarea>`
      : `<input ${a} type="${x.type || 'text'}" value="${esc(v)}" ${x.min != null ? `min="${x.min}"` : ''} ${x.max != null ? `max="${x.max}"` : ''}>`;
    return `<label>${x.label}${x.req ? ' *' : ''}${input}</label>`;
  }).join('')}<div class="btns"><button type="button" id="cancel">Cancel</button><button class="primary">Save</button></div>`;
  $('#cancel').onclick = () => dlg.close();
  f.onsubmit = guard(async ev => {
    ev.preventDefault();
    const rec = { ...(item || {}) };
    for (const x of e.fields) {
      const raw = new FormData(f).get(x.k);
      rec[x.k] = x.type === 'number' ? (raw === '' ? null : +raw) : x.type === 'select' ? (raw === '' ? null : /^\d+$/.test(raw) ? +raw : raw) : x.type === 'password' ? String(raw) : String(raw).trim();
    }
    const err = e.validate?.(rec); if (err) return toast(err);
    if (item) await api('PUT', `/api/${name}/${item.id}`, rec); else await api('POST', `/api/${name}`, rec);
    dlg.close(); await refresh(); toast('Saved');
  });
  dlg.showModal();
}

const remove = guard(async (name, id) => {
  const it = by(name, id);
  if (!confirm(`Delete ${it.name || it.username || E[name].one}?`)) return;
  await api('DELETE', `/api/${name}/${id}`);
  await refresh(); toast('Deleted');
});

// ---- Shell -----------------------------------------------------------------------
function render() {
  $('#nav').innerHTML = nav().map(n => `<a data-p="${n}" class="${n === page ? 'on' : ''}">${LABEL[n]}${n !== 'standings' ? `<span>${db[n].length}</span>` : ''}</a>`).join('');
  $('#title').textContent = LABEL[page];
  const list = page !== 'standings';
  $('#search').hidden = !list;
  $('#addBtn').hidden = !list || !canEdit();
  $('#importLabel').hidden = $('#demoBtn').hidden = !isAdmin();
  $('#whoami').textContent = me ? `${me.username} · ${me.role}` : '';
  $('#addBtn').textContent = list ? `+ Add ${E[page].one}` : '';
  $('#view').innerHTML = list ? renderList(page) : renderStandings();
}
function toast(m) { const t = $('#toast'); t.textContent = m; t.classList.add('show'); clearTimeout(toast.t); toast.t = setTimeout(() => t.classList.remove('show'), 2200); }

$('#nav').onclick = e => { const a = e.target.closest('a'); if (a) { page = a.dataset.p; $('#search').value = ''; location.hash = page; render(); } };
$('#addBtn').onclick = () => openForm(page);
$('#search').oninput = render;
$('#view').onclick = e => {
  const b = e.target.closest('button'); if (!b) return;
  if (b.dataset.edit) openForm(page, by(page, b.dataset.edit));
  if (b.dataset.del) remove(page, b.dataset.del);
};
$('#exportBtn').onclick = () => {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify(db, null, 2)], { type: 'application/json' }));
  a.download = 'puulaakiliiga.json'; a.click(); URL.revokeObjectURL(a.href);
};
$('#importFile').onchange = async e => {
  try { await api('POST', '/api/import', JSON.parse(await e.target.files[0].text())); await refresh(); toast('Imported'); } catch { toast('Invalid file'); }
  e.target.value = '';
};
$('#demoBtn').onclick = guard(async () => {
  if (db.teams.length && !confirm('Replace current data with demo data?')) return;
  db = structuredClone(EMPTY);
  let seq = 0;
  const add = (list, o) => { const x = { ...o, id: ++seq }; db[list].push(x); return x.id; };
  const names = ['Kuusi Kings', 'Mänty Bears', 'Koivu Wolves', 'Tammi Hawks'], teams = [];
  names.forEach((n, i) => {
    const c = add('contacts', { name: ['Aino Virta', 'Eero Salo', 'Liisa Mäki', 'Jussi Niemi'][i], phone: `+358 40 123 45${i}0`, email: `contact${i}@example.com` });
    const co = add('coaches', { name: ['Matti Kallio', 'Pekka Lahti', 'Sari Koski', 'Olli Rinne'][i] });
    const t = add('teams', { name: n, coachId: co, contactId: c }); teams.push(t);
    by('coaches', co).teamId = t;
    ['Goalie', 'Defender', 'Forward', 'Forward', 'Defender'].forEach((pos, j) => add('players', { name: `Player ${i + 1}.${j + 1}`, teamId: t, number: j * 7 + 1, position: pos }));
  });
  const d = n => new Date(Date.now() + n * 864e5).toISOString().slice(0, 11) + '18:00';
  [[0, 1, 3, 2, -14], [2, 3, 1, 1, -12], [0, 2, 4, 0, -7], [1, 3, 2, 5, -5], [3, 0, 2, 2, -2]].forEach(([h, a, hs, as, day], i) => {
    const g = add('games', { date: d(day), homeId: teams[h], awayId: teams[a], homeScore: hs, awayScore: as, field: `Field ${i % 2 + 1}`, notes: '' });
    if (i % 2 === 0) add('penalties', { playerId: db.players.find(p => p.teamId === teams[a]).id, gameId: g, minutes: 2, reason: 'Tripping' });
  });
  add('games', { date: d(3), homeId: teams[1], awayId: teams[2], homeScore: null, awayScore: null, field: 'Field 1', notes: '' });
  await api('POST', '/api/import', db);
  await refresh(); toast('Demo data loaded');
});

// ---- Sign in / first-run setup -----------------------------------------------------
let setupMode = false;
function showAuth() {
  $('#auth').hidden = false;
  $('#authTitle').textContent = setupMode ? 'Welcome! Create the admin account' : 'Sign in';
  $('#authSub').textContent = setupMode ? 'This is the first run. The admin can add other users later.' : 'Puulaakiliiga league manager';
  $('#authBtn').textContent = setupMode ? 'Create admin & sign in' : 'Sign in';
  $('#authPass').autocomplete = setupMode ? 'new-password' : 'current-password';
  $('#authErr').textContent = ''; $('#authPass').value = '';
  $('#authUser').focus();
}
$('#authForm').onsubmit = async ev => {
  ev.preventDefault();
  try {
    me = await api('POST', setupMode ? '/api/auth/setup' : '/api/auth/login', { username: $('#authUser').value, password: $('#authPass').value });
    setupMode = false; $('#auth').hidden = true; $('#authPass').value = '';
    if (!nav().includes(page)) page = 'standings';
    await refresh();
  } catch (e) { $('#authErr').textContent = e.message; }
};
$('#pwBtn').onclick = () => {
  const f = $('#form'), dlg = $('#dlg');
  f.innerHTML = `<h3>Change password</h3>
    <label>Current password<input name="cur" type="password" required autocomplete="current-password"></label>
    <label>New password (min 8 characters)<input name="new" type="password" required minlength="8" autocomplete="new-password"></label>
    <div class="btns"><button type="button" id="cancel">Cancel</button><button class="primary">Change</button></div>`;
  $('#cancel').onclick = () => dlg.close();
  f.onsubmit = guard(async ev => {
    ev.preventDefault();
    const d = new FormData(f);
    await api('POST', '/api/auth/password', { current: d.get('cur'), new: d.get('new') });
    dlg.close(); toast('Password changed');
  });
  dlg.showModal();
};
$('#logoutBtn').onclick = async () => { try { await api('POST', '/api/auth/logout'); } catch {} me = null; db = structuredClone(EMPTY); render(); showAuth(); };

(async function boot() {
  const r = await fetch('/api/auth/me');
  if (r.ok) {
    const j = await r.json();
    if (j.needsSetup) { setupMode = true; return showAuth(); }
    me = j; $('#auth').hidden = true;
    const h = location.hash.slice(1); if (nav().includes(h)) page = h;
    await refresh();
  } else showAuth();
})();
