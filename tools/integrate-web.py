#!/usr/bin/env python3
"""Install the prototype's web modules and narrowly integrate with the existing app.

Run against a checkout, never a deployed server. Existing non-plugin edits are
preserved; unexpected integration points abort before any files are written.
"""
import pathlib
import shutil
import sys

root = pathlib.Path(__file__).resolve().parents[1]
target = pathlib.Path(sys.argv[1]).resolve()
app_path, stage_path = target / 'app.js', target / 'stage.js'
app, stage = app_path.read_text(), stage_path.read_text()

def replace_once(text, old, new):
    if text.count(old) != 1:
        raise SystemExit('Integration point changed; no files written: ' + old[:80])
    return text.replace(old, new, 1)

if "from './game-bridge.js'" not in app:
    app = replace_once(app, "import { StageRenderer } from './stage.js';", "import { StageRenderer } from './stage.js';\nimport { initGameBridge } from './game-bridge.js';\nimport { findVenue } from './game-model.js';")
    app = replace_once(app, 'let rehearsalSync = null;', 'let rehearsalSync = null;\nlet flushStageSaves = async () => {};')
    app = replace_once(app, '  initSync();\n  bindGlobalEvents();', '''  initSync();
  initGameBridge({
    getContext: () => ({
      song: state.currentSong, cast: state.cast, slides: state.slides, lyrics: state.lyrics,
      slideIndex: state.currentSlideIdx, castId: state.savedCastId, canEdit: isAdmin(),
      active: document.getElementById('view-rehearsal').classList.contains('active') && state.slides.every(s => s.song_id === state.currentSong?.id),
      positionMs: Math.max(0, (audioPlayer?.audio.currentTime || 0) * 1000),
      playing: Boolean(audioPlayer?.loaded && !audioPlayer.audio.paused && !audioPlayer.audio.ended && audioPlayer.audio.readyState >= 3),
    }),
    saveCurrentStage: data => {
      if (!isAdmin()) throw new Error('Stage editing requires a director.');
      stageRenderer.commitStageData(data);
    },
  });
  bindGlobalEvents();''')
    app = replace_once(app, '''  // Debounced save: coalesce rapid stage changes into a single DB write
  let _savePending = null;
  let _saveTimer = null;

  function debouncedSave(slideId, stageData, thumbIdx) {
    _savePending = { slideId, stageData, thumbIdx };
    if (_saveTimer) clearTimeout(_saveTimer);
    _saveTimer = setTimeout(() => {
      const { slideId: sid, stageData: sd, thumbIdx: ti } = _savePending;
      _savePending = null;
      _saveTimer = null;
      // Fire-and-forget: don't block the UI on the network round-trip
      updateSlide(sid, { stage_data: sd }).catch(e => console.warn('Failed to save slide:', e));
      const thumbContainer = document.getElementById(`slideThumb${ti}`);
      if (thumbContainer) stageRenderer.renderThumbnail(sd, state.cast, thumbContainer);
    }, 400);
  }''', '''  // Keep each slide's pending save, including calibration, across navigation.
  // Serialize writes per slide so a slower old save cannot overwrite a new one.
  const pending = new Map();
  const saving = new Map();
  function persistPending(slideId) {
    const entry = pending.get(slideId);
    if (!entry) return saving.get(slideId) || Promise.resolve();
    clearTimeout(entry.timer);
    pending.delete(slideId);
    const operation = (saving.get(slideId) || Promise.resolve()).catch(() => {})
      .then(() => updateSlide(slideId, { stage_data: entry.stageData }));
    saving.set(slideId, operation);
    operation.finally(() => { if (saving.get(slideId) === operation) saving.delete(slideId); }).catch(() => {});
    return operation;
  }
  flushStageSaves = async () => {
    const ids = new Set([...pending.keys(), ...saving.keys()]);
    await Promise.all([...ids].map(persistPending));
  };
  function debouncedSave(slideId, stageData, thumbIdx) {
    clearTimeout(pending.get(slideId)?.timer);
    const timer = setTimeout(() => {
      persistPending(slideId).catch(e => console.warn('Failed to save slide:', e));
      const index = state.slides.findIndex(s => s.id === slideId);
      const thumbContainer = document.getElementById(`slideThumb${index}`);
      if (thumbContainer) stageRenderer.renderThumbnail(stageData, state.cast, thumbContainer);
    }, 400);
    pending.set(slideId, { stageData, timer });
  }''')
    app = replace_once(app, '''      try {
        await deleteSlide(slide.id);
        state.slides.splice(idx, 1);''', '''      try {
        await flushStageSaves();
        // Preserve the song's shared calibration if its carrying slide is deleted.
        const venue = findVenue(state.slides);
        const remaining = state.slides.find(s => s.id !== slide.id);
        if (venue && remaining) {
          const data = { ...remaining.stage_data, game_venue: venue };
          await updateSlide(remaining.id, { stage_data: data });
          remaining.stage_data = data;
          if (rehearsalSync?.connected) rehearsalSync.broadcastStageUpdate(state.slides.indexOf(remaining), data);
        }
        await deleteSlide(slide.id);
        state.slides.splice(idx, 1);''')
    app = replace_once(app, '''  audioPlayer.onSlideChange((idx) => {
    if (idx >= 0) {
      goToSlide(idx);''', '''  audioPlayer.onSlideChange((idx) => {
    // AudioPlayer sorts by time; the editor stores slides by order_index.
    const displayIndex = idx >= 0 ? state.slides.findIndex(s => s.id === audioPlayer.slides[idx]?.id) : -1;
    if (displayIndex >= 0) {
      goToSlide(displayIndex);''')
    app = replace_once(app, '''    // Conductor broadcasts slide changes from audio sync
    if (rehearsalSync?.connected && rehearsalSync.isConductor) {
      rehearsalSync.broadcastSlide(idx);''', '''    // Conductor broadcasts slide changes from audio sync
    if (rehearsalSync?.connected && rehearsalSync.isConductor) {
      rehearsalSync.broadcastSlide(displayIndex);''')
    stage = replace_once(stage, '  onUpdate(fn) { this.onChange = fn; }', '''  onUpdate(fn) { this.onChange = fn; }

  /** Apply edits from the in-game rehearsal panel using the existing autosave. */
  commitStageData(stageData) {
    if (!this.editable) throw new Error('The stage is read-only.');
    this.load(stageData, this.castMembers);
    this._emitChange();
  }''')

# Do not send a newly selected song with the previous song's slides while loading.
app = app.replace("active: document.getElementById('view-rehearsal').classList.contains('active'),",
                  "active: document.getElementById('view-rehearsal').classList.contains('active') && state.slides.every(s => s.song_id === state.currentSong?.id),")

# Stage every transformation before writing. Keep a local pre-integration copy.
backup = root / 'artifacts' / 'web-before-integration'
backup.mkdir(parents=True, exist_ok=True)
for path in [app_path, stage_path]:
    if not (backup / path.name).exists(): shutil.copy2(path, backup / path.name)
app_path.write_text(app)
stage_path.write_text(stage)
for name in ['game-model.js', 'game-bridge.js', 'game-bridge.css']:
    shutil.copy2(root / 'web' / name, target / name)
print('Integrated Stage Manager web prototype into ' + str(target))
