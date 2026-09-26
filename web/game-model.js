// Pure, versioned web → plugin contract. SVG y is depth; game y is height.
export const SCHEMA_VERSION = 1;
export const BRIDGE_PORT = 17845;

function finite(value, label, min = -10000, max = 10000) {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max)
    throw new Error(`${label} must be a number between ${min} and ${max}.`);
  return value;
}

export function calibration(venue) {
  if (!venue || ![3, 4].includes(venue.anchors?.length) || !venue.scope || !venue.id)
    throw new Error('Record all four stage corners first.');
  const [a, b, c] = venue.anchors;
  for (const p of venue.anchors) {
    finite(p.u, 'Map X'); finite(p.v, 'Map Y');
    for (const key of ['x', 'y', 'z']) finite(p.world?.[key], `Game ${key.toUpperCase()}`);
  }
  if (venue.anchors.length === 4) return fourCornerCalibration(venue);
  // Preserve the original transform for saved three-point calibrations.
  const du1 = b.u - a.u, dv1 = b.v - a.v, du2 = c.u - a.u, dv2 = c.v - a.v;
  const det = du1 * dv2 - du2 * dv1;
  if (Math.abs(det) < 1 || Math.abs(det) / (Math.hypot(du1, dv1) * Math.hypot(du2, dv2)) < .05)
    throw new Error('Map calibration points must form a wide triangle.');
  const dx1 = b.world.x - a.world.x, dz1 = b.world.z - a.world.z;
  const dx2 = c.world.x - a.world.x, dz2 = c.world.z - a.world.z;
  const worldDet = dx1 * dz2 - dx2 * dz1;
  if (Math.abs(worldDet) < .01 || Math.abs(worldDet) / (Math.hypot(dx1, dz1) * Math.hypot(dx2, dz2)) < .05)
    throw new Error('Game calibration points must form a wide triangle.');
  // Three surveyed points define an affine map, including scale and rotation.
  const xu = (dx1 * dv2 - dx2 * dv1) / det, xv = (dx2 * du1 - dx1 * du2) / det;
  const zu = (dz1 * dv2 - dz2 * dv1) / det, zv = (dz2 * du1 - dz1 * du2) / det;
  return { a, xu, xv, zu, zv, det: xu * zv - xv * zu };
}

function checkCorners(points, label, minimumArea) {
  let winding = 0;
  for (let i = 0; i < 4; i++) {
    const a = points[i], b = points[(i + 1) % 4], c = points[(i + 2) % 4];
    const dx = b[0] - a[0], dy = b[1] - a[1], ex = c[0] - b[0], ey = c[1] - b[1];
    const cross = dx * ey - dy * ex;
    if (Math.abs(cross) < minimumArea || Math.abs(cross) / (Math.hypot(dx, dy) * Math.hypot(ex, ey)) < .05 ||
        (winding && Math.sign(cross) !== winding))
      throw new Error(`${label} corners must go around the stage in order, without crossing or overlapping.`);
    winding = Math.sign(cross);
  }
}

function fourCornerCalibration(venue) {
  const points = venue.anchors;
  checkCorners(points.map(p => [p.u, p.v]), 'Map', 1);
  checkCorners(points.map(p => [p.world.x, p.world.z]), 'Game', .01);
  if (points.some(p => p.scope && scopeKey(p.scope) !== scopeKey(venue.scope)))
    throw new Error('All four corners must be captured in the same venue.');
  if (points.some(p => Math.abs(p.world.y - points[0].world.y) > .25))
    throw new Error('Capture all four corners on one level floor; use actor height offsets for platforms.');
  // Fit one affine transform to all four observations. Centering avoids losing
  // precision at large world coordinates and keeps the stage's scale uniform.
  const mean = fn => points.reduce((sum, p) => sum + fn(p), 0) / 4;
  const a = { u: mean(p => p.u), v: mean(p => p.v), world: { x: mean(p => p.world.x), y: points[0].world.y, z: mean(p => p.world.z) } };
  let uu = 0, uv = 0, vv = 0, ux = 0, vx = 0, uz = 0, vz = 0;
  for (const p of points) {
    const u = p.u - a.u, v = p.v - a.v, x = p.world.x - a.world.x, z = p.world.z - a.world.z;
    uu += u * u; uv += u * v; vv += v * v;
    ux += u * x; vx += v * x; uz += u * z; vz += v * z;
  }
  const normalDet = uu * vv - uv * uv;
  if (normalDet <= 0 || normalDet / (uu + vv) ** 2 < 1e-10) throw new Error('Spread the map corners farther apart.');
  const xu = (ux * vv - vx * uv) / normalDet, xv = (vx * uu - ux * uv) / normalDet;
  const zu = (uz * vv - vz * uv) / normalDet, zv = (vz * uu - uz * uv) / normalDet;
  const det = xu * zv - xv * zu;
  if (!Number.isFinite(det) || Math.abs(det) < 1e-12) throw new Error('Game corners do not define a usable stage.');
  const errors = points.map(p => Math.hypot(a.world.x + xu * (p.u - a.u) + xv * (p.v - a.v) - p.world.x,
    a.world.z + zu * (p.u - a.u) + zv * (p.v - a.v) - p.world.z));
  const maxError = Math.max(...errors);
  if (maxError > .35) throw new Error(`The four corners do not line up (${maxError.toFixed(2)} units off). Check corresponding map/game corners and capture them again.`);
  return { a, xu, xv, zu, zv, det, maxError };
}

export function toWorld(venue, position) {
  const m = calibration(venue), du = finite(position.x, 'Map X') - m.a.u, dv = finite(position.y, 'Map Y') - m.a.v;
  const heading = finite(position.game?.heading ?? 0, 'Facing', -360, 360) * Math.PI / 180;
  const su = Math.sin(heading), sv = -Math.cos(heading);
  return {
    x: m.a.world.x + m.xu * du + m.xv * dv,
    y: m.a.world.y + finite(position.game?.height ?? 0, 'Height offset', -100, 100),
    z: m.a.world.z + m.zu * du + m.zv * dv,
    yaw: Math.atan2(m.xu * su + m.xv * sv, m.zu * su + m.zv * sv),
  };
}

export function fromWorld(venue, snapshot) {
  const m = calibration(venue), w = snapshot.position;
  if (scopeKey(venue.scope) !== scopeKey(snapshot.scope)) throw new Error('You are in a different venue.');
  const dx = finite(w?.x, 'Game X') - m.a.world.x, dz = finite(w?.z, 'Game Z') - m.a.world.z;
  const yaw = finite(snapshot.yaw, 'Yaw', -Math.PI * 2, Math.PI * 2);
  const uDir = (m.zv * Math.sin(yaw) - m.xv * Math.cos(yaw)) / m.det;
  const vDir = (-m.zu * Math.sin(yaw) + m.xu * Math.cos(yaw)) / m.det;
  return {
    x: m.a.u + (m.zv * dx - m.xv * dz) / m.det,
    y: m.a.v + (-m.zu * dx + m.xu * dz) / m.det,
    game: { height: finite(w.y, 'Game Y') - m.a.world.y, heading: Math.atan2(uDir, -vDir) * 180 / Math.PI },
  };
}

export function scopeKey(scope) {
  return [scope?.territory, scope?.world, scope?.ward, scope?.plot, scope?.room, scope?.houseId, scope?.instance].join('|');
}

export function findVenue(slides) {
  return slides.map(s => s.stage_data?.game_venue).filter(Boolean)
    .sort((a, b) => (b.revision || 0) - (a.revision || 0))[0] ?? null;
}

// Commands are animation reminders, never executable player chat commands.
export function compilePackage(context, venue = findVenue(context.slides)) {
  calibration(venue);
  if (!context.song?.id || !context.slides?.length) throw new Error('Open a song with at least one slide.');
  const warnings = [], cast = context.cast.map(c => ({ id: c.id, name: c.character_name, world: c.world || '', role: c.role_name || '' }));
  const slides = [...context.slides].sort((a, b) => a.timestamp_ms - b.timestamp_ms || a.order_index - b.order_index).map(s => {
    const seen = new Set();
    const positions = (s.stage_data?.cast_positions || []).map(p => {
      if (seen.has(p.cast_member_id)) throw new Error(`Duplicate actor in ${s.label || s.id}; keep one position per actor.`);
      seen.add(p.cast_member_id);
      if (!cast.some(c => c.id === p.cast_member_id)) throw new Error('A slide contains a deleted cast member.');
      return { castId: p.cast_member_id, ...toWorld(venue, p), emote: normalizeEmote(p.game?.emote || '') };
    });
    return { id: s.id, label: s.label || 'Slide', atMs: finite(s.timestamp_ms, 'Slide time', 0, 86400000), positions };
  });
  for (let i = 1; i < slides.length; i++) {
    if (slides[i].atMs === slides[i - 1].atMs) throw new Error('Give each slide a distinct timestamp before connecting.');
  }
  const cues = [];
  for (const lyric of context.lyrics || []) {
    const atMs = finite(lyric.timestamp_ms, 'Cue time', 0, 86400000);
    for (const [lineIndex, line] of lyric.line_text.split('\n').entries()) {
      const commands = line.match(/\/[a-zA-Z]\w*/g) || [];
      if (!commands.length) continue;
      const speaker = line.match(/^\s*([^:]+):/u)?.[1].trim();
      const matched = speaker && !/^(all|everyone|cast)$/i.test(speaker)
        ? cast.filter(c => c.name.toLowerCase() === speaker.toLowerCase() || (c.role && c.role.toLowerCase() === speaker.toLowerCase())) : [];
      if (speaker && !/^(all|everyone|cast)$/i.test(speaker) && !matched.length) {
        warnings.push(`Skipped unknown speaker "${speaker}" at ${atMs} ms.`); continue;
      }
      for (const [commandIndex, command] of commands.entries()) {
        const next = command.toLowerCase() === '/nextpos';
        const target = next ? slides.find(s => s.atMs > atMs) : null;
        if (next && !target) { warnings.push('Skipped /nextpos after the last slide.'); continue; }
        if (next && target.atMs < atMs + 5000) warnings.push(`The /nextpos at ${atMs} ms is less than five seconds before its slide; move the cue earlier to match the browser countdown.`);
        cues.push({ id: `${lyric.id}:${lineIndex}:${commandIndex}`, atMs, kind: next ? 'preview' : 'emote',
          castIds: matched.map(c => c.id), slideId: target?.id || '', emote: next ? '' : normalizeEmote(command), moveAtMs: next ? atMs + 5000 : atMs });
      }
    }
  }
  return { schemaVersion: SCHEMA_VERSION, songId: context.song.id, title: context.song.title,
    durationMs: Math.max(context.song.duration_ms || 0, ...slides.map(s => s.atMs), ...cues.map(c => c.atMs)),
    venue: { id: venue.id, name: venue.name || 'Stage', scope: venue.scope,
      boundary: venue.anchors.length === 4 ? venue.anchors.map(p => ({ x: p.world.x, y: venue.anchors[0].world.y, z: p.world.z })) : [] },
    cast, slides, cues: cues.sort((a,b) => a.atMs - b.atMs), warnings };
}

export function normalizeEmote(value) {
  const command = value.trim().replace(/^\//, '').toLowerCase();
  if (command && !/^[a-z][a-z0-9_]{0,39}$/.test(command)) throw new Error('Use one emote command, for example /beesknees.');
  if (command === 'nextpos') throw new Error('Put /nextpos in the timed lyrics, not in the emote field.');
  return command;
}
