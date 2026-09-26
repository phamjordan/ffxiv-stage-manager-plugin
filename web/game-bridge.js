import { BRIDGE_PORT, calibration, compilePackage, findVenue, fromWorld, normalizeEmote, scopeKey } from './game-model.js';

export function initGameBridge({ getContext, saveCurrentStage }) {
  const css = document.createElement('link');
  css.rel = 'stylesheet'; css.href = new URL('./game-bridge.css', import.meta.url).href;
  document.head.appendChild(css);
  const button = document.createElement('button');
  button.className = 'game-open'; button.textContent = 'In-game rehearsal';
  document.querySelector('.player-info').appendChild(button);
  const panel = document.createElement('aside');
  panel.className = 'game-panel'; panel.hidden = true;
  panel.setAttribute('aria-label', 'In-game rehearsal');
  panel.innerHTML = `
    <header><h2>In-game rehearsal</h2><button data-close aria-label="Close">×</button></header>
    <p>See your next position and emote in FFXIV. Open <b>/stage</b> in the Windows plugin.</p>
    <details open><summary>1 · Connect this PC</summary>
      <p>In the plugin, set Web origin to <code data-origin></code>, enable the bridge, then paste its pairing token below.</p>
      <label>Pairing token <input data-token type="password" autocomplete="off" spellcheck="false"></label>
      <div class="game-row"><button data-connect>Connect</button><button data-disconnect>Disconnect</button></div>
      <p class="game-status" data-status role="status">Disconnected</p>
      <p>Keep this browser open during rehearsal. Music and spoken reminders continue here.</p>
    </details>
    <details data-editor><summary>2 · Calibrate the stage</summary>
      <p>Use three widely separated spots on the same floor. Pick each spot on the map, stand there in-game, then capture it. Map X/Y are canvas coordinates.</p>
      <label>Venue name <input data-venue-name value="Main stage" maxlength="80"></label>
      <div data-anchors></div>
      <button data-save-venue>Save calibration to this song</button>
      <p data-venue-status></p>
      <div class="game-row"><button data-export-venue>Export calibration</button><label class="game-file">Import calibration<input data-import-venue type="file" accept="application/json,.json" hidden></label></div>
    </details>
    <details data-editor open><summary>3 · Actor on the current slide</summary>
      <p data-slide></p>
      <label>Cast member <select data-cast></select></label>
      <label>Height above calibrated floor (game units) <input data-height type="number" step="0.01" min="-100" max="100" value="0"></label>
      <label>Facing on map (0° up, 90° right) <input data-heading type="number" step="1" min="-360" max="360" value="0"></label>
      <label>Emote when this slide starts <input data-emote placeholder="/beesknees" maxlength="41" spellcheck="false"></label>
      <div class="game-row"><button data-save-actor>Save actor cue</button><button data-record>Record my game position</button></div>
      <p>Recording sets map position, height, and facing. Place an actor on the map first. A blank emote leaves the ghost idle.</p>
    </details>
    <details><summary>4 · Preview and share</summary>
      <p>Timed <b>/nextpos</b> lyrics reveal the next slide’s ghost and a five-second movement countdown. Its emote starts when that slide begins. Emotes in timed lyrics also work.</p>
      <p>Use <b>Character Name: /beesknees</b>, a role name, or <b>All: /beesknees</b> to target cues. Unlabelled commands apply to everyone.</p>
      <button data-export>Export plugin choreography</button>
      <p>JSON export also works without a browser connection: import it in /stage, choose your cast member, then use the plugin playback controls.</p>
      <pre data-warnings></pre>
    </details>
  `;
  document.body.appendChild(panel);
  panel.addEventListener('keydown', e => e.stopPropagation());
  const $ = key => panel.querySelector(`[data-${key}]`);
  $('origin').textContent = location.origin;
  let token = '', connected = false, sending = false, lastPackage = '', sequence = 0, sessionId = '';
  let anchors = [], pickIndex = null, lastSelection = '', lastSong = '', anchorSong = '';
  const status = text => { $('status').textContent = text; };
  const run = fn => async () => { try { await fn(); } catch (e) { status(e.message); } };
  const current = () => {
    const ctx = getContext();
    if (!ctx.song || !ctx.slides.length || !ctx.active) throw new Error('Open a song with slides first.');
    return ctx;
  };
  const edit = () => { const ctx = current(); if (!ctx.canEdit) throw new Error('Stage editing requires a director.'); return ctx; };
  const stageCopy = ctx => structuredClone(ctx.slides[ctx.slideIndex].stage_data || { cast_positions: [], shapes: [] });
  const request = async (route, body) => {
    if (!token) throw new Error('Connect using the token shown in /stage.');
    let response;
    try {
      response = await fetch(`http://127.0.0.1:${BRIDGE_PORT}/${route}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify(body), signal: AbortSignal.timeout(2500), credentials: 'omit', cache: 'no-store',
      });
    } catch { throw new Error('Cannot reach the plugin. Check /stage, the Web origin, and browser permission for local network access. JSON export remains available.'); }
    const reply = await response.json();
    if (!response.ok || !reply.ok) throw new Error(reply.message || 'The plugin rejected this request.');
    return reply;
  };
  const snapshot = async () => {
    const reply = await request('snapshot', {});
    if (!reply.snapshot) throw new Error('Log into FFXIV and wait until your character is ready.');
    return reply.snapshot;
  };

  button.onclick = () => { panel.hidden = !panel.hidden; if (!panel.hidden) refresh(true); };
  $('close').onclick = () => { panel.hidden = true; pickIndex = null; };
  $('connect').onclick = run(async () => {
    token = $('token').value.trim(); await request('snapshot', {});
    connected = true; lastPackage = ''; sequence = 0; sessionId = crypto.randomUUID(); status('Connected. Calibrate the stage to send choreography.');
  });
  $('disconnect').onclick = () => { connected = false; token = ''; $('token').value = ''; status('Disconnected. In-game markers clear after three seconds.'); };
  $('cast').onchange = () => refreshActor();

  function drawAnchors() {
    $('anchors').replaceChildren();
    for (let i = 0; i < 3; i++) {
      const row = document.createElement('div'); row.className = 'game-anchor';
      row.innerHTML = `<strong>Point ${i + 1}</strong><div class="game-row"><label>Map X<input type="number" data-u></label><label>Map Y<input type="number" data-v></label></div><div class="game-row"><button data-pick>Pick on map</button><button data-capture>Capture in-game</button></div><small data-result></small>`;
      const a = anchors[i];
      row.querySelector('[data-u]').value = a.u; row.querySelector('[data-v]').value = a.v;
      row.querySelector('[data-u]').oninput = e => { a.u = Number(e.target.value); };
      row.querySelector('[data-v]').oninput = e => { a.v = Number(e.target.value); };
      row.querySelector('[data-result]').textContent = a.world ? `XYZ ${a.world.x.toFixed(2)}, ${a.world.y.toFixed(2)}, ${a.world.z.toFixed(2)}` : 'Not captured';
      row.querySelector('[data-pick]').onclick = () => { pickIndex = i; status(`Click point ${i + 1} on the stage map.`); };
      row.querySelector('[data-capture]').onclick = run(async () => {
        const ctx = edit(), songId = ctx.song.id, p = await snapshot();
        if (current().song.id !== songId) throw new Error('Song changed; capture again.');
        a.world = p.position; a.scope = p.scope; drawAnchors(); status(`Captured point ${i + 1}.`);
      });
      $('anchors').appendChild(row);
    }
  }
  document.getElementById('stageSvg').addEventListener('pointerdown', e => {
    if (pickIndex === null) return;
    e.preventDefault(); e.stopImmediatePropagation();
    const svg = e.currentTarget, point = new DOMPoint(e.clientX, e.clientY).matrixTransform(svg.getScreenCTM().inverse());
    anchors[pickIndex].u = Math.round(point.x); anchors[pickIndex].v = Math.round(point.y);
    pickIndex = null; drawAnchors(); status('Map point set. Stand at that spot in-game and capture it.');
  }, true);
  $('save-venue').onclick = run(() => {
    const ctx = edit();
    if (anchors.some(a => !a.world || !a.scope)) throw new Error('Capture all three points.');
    if (anchors.some(a => scopeKey(a.scope) !== scopeKey(anchors[0].scope))) throw new Error('All points must be in the same venue.');
    if (anchors.some(a => Math.abs(a.world.y - anchors[0].world.y) > .25)) throw new Error('Calibrate on one level floor; use actor height offsets for raised platforms.');
    const venue = { id: crypto.randomUUID(), revision: Date.now(), name: $('venue-name').value || 'Stage', scope: anchors[0].scope, anchors: structuredClone(anchors) };
    calibration(venue);
    const data = stageCopy(ctx); data.game_venue = venue; saveCurrentStage(data);
    status('Calibration saved through the existing slide autosave. Arm this venue in /stage after checking a test marker.'); refresh(true);
  });
  $('save-actor').onclick = run(() => {
    const ctx = edit(), data = stageCopy(ctx), p = data.cast_positions.find(p => p.cast_member_id === $('cast').value);
    if (!p) throw new Error('Place this cast member on the current slide first.');
    p.game = { height: Number($('height').value), heading: Number($('heading').value), emote: normalizeEmote($('emote').value) };
    // Compile validates coordinates before saving.
    const slides = ctx.slides.map((s, i) => i === ctx.slideIndex ? { ...s, stage_data: data } : s);
    compilePackage({ ...ctx, slides }); saveCurrentStage(data); status('Actor cue saved through slide autosave.');
  });
  $('record').onclick = run(async () => {
    const ctx = edit(), songId = ctx.song.id, slideId = ctx.slides[ctx.slideIndex].id, castId = $('cast').value;
    const captured = await snapshot(), fresh = edit();
    if (fresh.song.id !== songId || fresh.slides[fresh.slideIndex].id !== slideId) throw new Error('Slide changed during capture; record again.');
    const data = stageCopy(fresh), p = data.cast_positions.find(p => p.cast_member_id === castId);
    if (!p) throw new Error('Place this cast member on the current slide first.');
    const converted = fromWorld(findVenue(fresh.slides), captured);
    p.x = converted.x; p.y = converted.y; p.game = { ...p.game, ...converted.game };
    saveCurrentStage(data); refreshActor(); status('Recorded position, height, and facing.');
  });
  $('export').onclick = run(() => {
    const ctx = current(), pkg = compilePackage(ctx); $('warnings').textContent = pkg.warnings.join('\n');
    download(`${safeName(ctx.song.title)}.stage.json`, pkg); status('Choreography exported. Import this file in /stage.');
  });
  $('export-venue').onclick = run(() => { const venue = findVenue(current().slides); calibration(venue); download('stage-calibration.json', venue); });
  $('import-venue').onchange = run(async () => {
    const ctx = edit(), songId = ctx.song.id, file = $('import-venue').files[0];
    if (!file) return; if (file.size > 100000) throw new Error('Calibration file is too large.');
    const venue = JSON.parse(await file.text()); calibration(venue);
    const fresh = edit(); if (fresh.song.id !== songId) throw new Error('Song changed; import again.');
    venue.revision = Date.now(); const data = stageCopy(fresh); data.game_venue = venue;
    saveCurrentStage(data); anchorSong = ''; refresh(true); status('Calibration imported.'); $('import-venue').value = '';
  });

  function refreshActor() {
    const ctx = getContext(), p = ctx.slides[ctx.slideIndex]?.stage_data?.cast_positions?.find(p => p.cast_member_id === $('cast').value);
    $('height').value = p?.game?.height ?? 0; $('heading').value = p?.game?.heading ?? 0; $('emote').value = p?.game?.emote ?? '';
  }
  function refresh(force = false) {
    const ctx = getContext();
    panel.querySelectorAll('[data-editor]').forEach(el => { el.hidden = !ctx.canEdit; });
    const selection = `${ctx.song?.id}:${ctx.slides[ctx.slideIndex]?.id}:${ctx.cast.map(c => c.id).join(',')}`;
    if (force || selection !== lastSelection) {
      const selected = $('cast').value; $('cast').replaceChildren(...ctx.cast.map(c => new Option(c.character_name, c.id)));
      if (ctx.cast.some(c => c.id === selected)) $('cast').value = selected;
      else if (ctx.cast.some(c => c.id === ctx.castId)) $('cast').value = ctx.castId;
      $('slide').textContent = ctx.slides[ctx.slideIndex]?.label || 'Open a song';
      lastSelection = selection; refreshActor();
    }
    const venue = findVenue(ctx.slides);
    $('venue-status').textContent = venue ? `Using ${venue.name}. Floor Y = ${venue.anchors[0].world.y.toFixed(2)}.` : 'No calibration yet.';
    if (anchorSong !== ctx.song?.id) {
      anchors = venue ? structuredClone(venue.anchors) : [{u:200,v:550},{u:1000,v:550},{u:200,v:150}];
      $('venue-name').value = venue?.name || 'Main stage'; anchorSong = ctx.song?.id; drawAnchors();
    }
  }

  async function tick() {
    if (!panel.hidden) refresh();
    if (!connected || sending) return;
    sending = true;
    try {
      const ctx = getContext();
      if (ctx.song?.id !== lastSong) { lastPackage = ''; lastSong = ctx.song?.id; }
      const active = Boolean(ctx.active && ctx.song && ctx.slides.length);
      const pkg = active ? compilePackage(ctx) : null;
      const serialized = pkg ? JSON.stringify(pkg) : '';
      // Take the audio timestamp AFTER compiling, immediately before sending.
      const playback = getContext();
      const transport = { sessionId, sequence: ++sequence, songId: ctx.song?.id || '', positionMs: playback.positionMs,
        playing: active && playback.playing, active };
      await request('state', { package: serialized !== lastPackage ? pkg : null, transport });
      lastPackage = serialized;
      $('warnings').textContent = pkg?.warnings.join('\n') || '';
      status(active ? `Connected · ${ctx.song.title} · ${playback.playing ? 'Playing' : 'Paused'}` : 'Connected · open a song');
    } catch (e) { lastPackage = ''; status(e.message); }
    finally { sending = false; }
  }
  const timer = setInterval(tick, 250);
  window.addEventListener('pagehide', () => { clearInterval(timer); connected = false; }, { once: true });
  refresh(true);
}

function safeName(name) { return (name || 'rehearsal').replace(/[^a-z0-9_-]+/gi, '-').slice(0, 80); }
function download(name, value) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }));
  const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
