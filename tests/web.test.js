import test from 'node:test';
import assert from 'node:assert/strict';
import { calibration, compilePackage, fromWorld, toWorld, findVenue } from '../web/game-model.js';
import { writeFileSync } from 'node:fs';

const scope = { territory: 339, world: 21, ward: 0, plot: 0, room: 0, houseId: '123', instance: 0 };
const venue = { id: 'theatre', name: 'Test theatre', revision: 1, scope, anchors: [
  { u: 100, v: 500, world: { x: 10, y: 3, z: 20 }, scope },
  { u: 1100, v: 500, world: { x: 10, y: 3, z: 40 }, scope },
  { u: 100, v: 100, world: { x: 18, y: 3, z: 20 }, scope },
] };
const context = {
  song: { id: 'song', title: 'Rehearsal fixture', duration_ms: 20000 },
  cast: [{ id: 'alice', character_name: 'Alice Actor', world: 'Ravana', role_name: 'Lead' }, { id: 'bob', character_name: 'Bob Actor', world: 'Ravana', role_name: 'Ensemble' }],
  slides: [
    { id: 'opening', label: 'Opening', timestamp_ms: 0, order_index: 0, stage_data: { game_venue: venue, cast_positions: [{ cast_member_id: 'alice', x: 100, y: 500 }, { cast_member_id: 'bob', x: 200, y: 500 }] } },
    { id: 'chorus', label: 'Chorus', timestamp_ms: 10000, order_index: 1, stage_data: { cast_positions: [{ cast_member_id: 'alice', x: 600, y: 300, game: { height: 1.25, heading: 90, emote: '/beesknees' } }, { cast_member_id: 'bob', x: 900, y: 300 }] } },
    { id: 'exit', label: 'Exit', timestamp_ms: 18000, order_index: 2, stage_data: { cast_positions: [] } },
  ],
  lyrics: [
    { id: 'move', timestamp_ms: 5000, line_text: '/nextpos' },
    { id: 'emote', timestamp_ms: 11500, line_text: 'Lead: /wave\nBob Actor: /bow' },
    { id: 'late', timestamp_ms: 19000, line_text: '/nextpos' },
  ],
};
function close(a, b) { assert.ok(Math.abs(a - b) < 1e-8, `${a} != ${b}`); }

test('three anchors preserve surveyed world positions, with game Y independent of SVG Y', () => {
  for (const a of venue.anchors) {
    const p = toWorld(venue, { x: a.u, y: a.v });
    close(p.x, a.world.x); close(p.y, a.world.y); close(p.z, a.world.z);
  }
  const p = toWorld(venue, { x: 600, y: 300, game: { height: 1.25, heading: 90 } });
  close(p.x, 14); close(p.z, 30); close(p.y, 4.25); close(p.yaw, 0);
});
test('recording world coordinates round trips height and facing through rotated and sheared stages', () => {
  for (const skew of [0, 2]) for (const heading of [-175, -90, 0, 25, 90, 179]) {
    const v = structuredClone(venue); v.anchors[2].world.z += skew;
    const p = { x: 352.8, y: 276.5, game: { height: -.7, heading } };
    const w = toWorld(v, p), back = fromWorld(v, { position: w, yaw: w.yaw, scope });
    close(back.x, p.x); close(back.y, p.y); close(back.game.height, p.game.height); close(back.game.heading, heading);
  }
});
test('rejects degenerate points, non-finite coordinates and captures from another house', () => {
  const bad = structuredClone(venue); bad.anchors[2] = structuredClone(bad.anchors[1]);
  assert.throws(() => calibration(bad), /triangle/);
  assert.throws(() => toWorld(venue, { x: NaN, y: 5 }), /number/);
  assert.throws(() => fromWorld(venue, { position: { x: 1, y: 2, z: 3 }, yaw: 0, scope: { ...scope, houseId: '456' } }), /different venue/);
});
test('compiles targeted emotes and /nextpos as distinct cue types', () => {
  const pkg = compilePackage(context);
  assert.equal(pkg.slides[1].positions[0].emote, 'beesknees');
  assert.equal(pkg.cues[0].slideId, 'chorus'); assert.equal(pkg.cues[0].moveAtMs, 10000);
  assert.deepEqual(pkg.cues[1].castIds, ['alice']); assert.deepEqual(pkg.cues[2].castIds, ['bob']);
  assert.equal(pkg.cues.length, 3); assert.match(pkg.warnings[0], /last slide/);
  writeFileSync(new URL('./rehearsal.stage.json', import.meta.url), JSON.stringify(pkg, null, 2) + '\n');
});
test('unknown speaker cues are skipped rather than sent to the entire cast', () => {
  const c = structuredClone(context); c.lyrics = [{ id: 'x', timestamp_ms: 100, line_text: 'Unknown: /wave' }];
  const pkg = compilePackage(c); assert.equal(pkg.cues.length, 0); assert.match(pkg.warnings[0], /Unknown/);
});
test('rejects duplicate actor placements and tied slide times', () => {
  const c = structuredClone(context); c.slides[0].stage_data.cast_positions.push(c.slides[0].stage_data.cast_positions[0]);
  assert.throws(() => compilePackage(c), /Duplicate actor/);
  c.slides[0].stage_data.cast_positions.pop(); c.slides[1].timestamp_ms = 0;
  assert.throws(() => compilePackage(c), /distinct timestamp/);
});
test('exports by timestamp, while retaining stable IDs and newest calibration', () => {
  const c = structuredClone(context); c.slides.reverse();
  c.slides[0].stage_data.game_venue = { ...venue, revision: 2, name: 'New stage' };
  assert.equal(findVenue(c.slides).name, 'New stage');
  assert.deepEqual(compilePackage(c).slides.map(s => s.id), ['opening', 'chorus', 'exit']);
});
