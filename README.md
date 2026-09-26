# FFXIV Stage Manager — first prototype

Version **0.1.3**, built for **Windows x64, .NET 10, Dalamud API 15**.
Created on 2026-09-25. The plugin compiles and its calibration, cue engine,
browser integration, and loopback protocol have been tested. Native ghost
behavior and emote detection still need verification in FFXIV on Windows.

## What is implemented

- A performer sees their own local ghost and position marker. A director can
  enable a view of up to 32 cast members.
- Three captured game locations calibrate the existing flat stage editor.
  Each actor position also stores a height offset, facing, and optional emote.
- “Record my game position” moves the selected actor's map token to the
  performer's actual game X/Z and records height and facing.
- A timed `/nextpos` lyric previews the next slide's position. The ghost stays
  idle during the preview and starts the next action when that slide begins.
- Timed lyric emotes can address a character name, role name, or everyone.
- Each floor ring checks its assigned performer: red off mark, yellow on
  mark awaiting an emote, green ready, and gray when the performer is missing.
  The mark tolerance is 0.5 horizontal units and 0.35 vertical units.
- Upcoming emotes have countdowns above their world markers and in a separate
  HUD for your selected role, including cues later in the same slide.
- Paused/stopped browser playback leaves ghosts standing idle. Resuming
  restarts the appropriate emote. Countdown timers hold while paused.
- The browser supplies music timing over a paired local connection. The plugin
  can play a Windows cue chime; the existing browser supplies spoken prompts.
- Choreography and playback sync through the browser bridge. There is no
  choreography file import or independent playback control in the plugin.

The plugin does not move or make the real player perform an emote. Performers
follow the ghost themselves. Each participant needs the plugin to see their
local rehearsal visuals.

## Windows setup

1. Open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
   Add this URL, enable its checkbox, and save:

   ```text
   https://raw.githubusercontent.com/phamjordan/ffxiv-stage-manager-plugin/main/repo.json
   ```

2. Open `/xlplugins`, select **All Plugins**, search **Stage Manager**, and
   install it. Developer mode is not required. If you previously loaded the
   prototype through **Dev Plugin Locations**, remove that entry first to
   avoid loading two copies. This release requires Dalamud API 15.
3. Open `/stage`. Ghost models start disabled; floor markers work first.
4. Use the updated Stage Manager web app on **this same Windows PC**. The
   source changes are installed in your attached web project and deployed at
   https://ffxiv.jjammin.com/stage-manager/. See `DEPLOYMENT.md` for the
   deployed commit and version.
5. Open a song, then **In-game rehearsal**. In the plugin's **Connection**
   tab, copy the displayed web origin into **Web origin**. It is scheme + host + port,
   such as `https://example.com`, with no path or trailing slash.
6. Click **Enable local bridge**, then **Copy pairing token**. Paste the token
   into the browser panel and connect. The token is held only in browser
   memory and changes whenever the plugin bridge restarts.
7. Allow the browser's local-network permission if requested. If the browser
   cannot connect, check its site permissions and confirm the origin/token.

Each cast member pairs their own browser and game client. Your existing
Supabase rehearsal room continues synchronizing their browser audio with
the conductor. The plugin needs no Supabase password or service key.

Downloads and release notes are also available on
[GitHub Releases](https://github.com/phamjordan/ffxiv-stage-manager-plugin/releases).
Future versions published to this feed appear in Dalamud's plugin updater.

## Calibrate a venue once

1. Open a song containing at least one slide, with director editing rights.
2. Choose three widely separated, recognizable points on the **same level
   floor**, forming a triangle. For example: downstage left, downstage right,
   and upstage left. Avoid three points along a straight line.
3. For each point, click **Pick on map**, click its location on your diagram,
   stand in the corresponding place in FFXIV, and click **Capture in-game**.
4. Click **Save calibration to this song**. It uses the existing slide
   `stage_data` storage and autosave; no database migration is required.
5. Select your cast member in `/stage` and click **Arm this venue**. First
   compare a marker with a known floor spot. Then enable ghost models.
6. Export the calibration and import it into other songs staged in the
   same venue. Recalibrate after changing the stage layout or map artwork.

The transform maps the editor's SVG X/Y into game X/Z. Game **Y is height**.
The first calibration point defines floor height. A platform 1.2 game units
above it uses height `1.2`; a lower level uses a negative value. The prototype
does not infer furniture heights from the flat drawing.

Venue matching includes territory, current world, instance, and raw housing
ward/plot/room/indoor-house ID. Those housing values are internal identifiers,
not the human-facing ward and plot numbers. Leaving the venue disarms it.

## Author and rehearse

1. Place each performer once on each slide where they should appear.
2. In **Actor on the current slide**, select the cast member and set height,
   facing, and an optional command such as `/beesknees`. Facing is clockwise
   on the map: 0° toward the top, 90° to the right.
3. Alternatively, stand on the desired in-game mark and use **Record my game
   position**. This records your position for the cast member selected in
   the panel; it is useful for a director walking the blocking.
4. Put `/nextpos` in a lyric line **five seconds before** the next slide.
   The target ghost appears at the cue, then its emote starts at the slide
   boundary. `/nextpos` previews a position; it does not advance the song.
5. For a later action inside a slide, use a timed lyric such as:

   ```text
   Alice Actor: /beesknees
   Ensemble: /wave
   All: /bow
   ```

   Character and role matching ignores letter case. A role can match several
   cast members. Unlabelled commands address everyone. Unknown named speakers
   are skipped and reported in export warnings. Multiple commands for one
   actor at the same instant resolve to the last one.
6. Play the song in the web app. Keep that browser active during rehearsal.
   The plugin follows local audio position, pause, and seek. Missing updates
   for three seconds hides the rehearsal instead of leaving stale marks.

Plugin controls: `/stage` toggles the window, `/stage nextpos` manually
previews the next mark, `/stage current` returns to the current mark,
and `/stage stop` disarms and removes rehearsal
visuals. A slide without an actor's position means that actor is offstage.

## Readiness, countdowns, and window layout

- **Rehearsal:** choose your role, enable director view, arm/hide the venue,
  preview the next position, return to the current position, and see readiness.
- **Connection:** set the browser origin, enable the bridge, and copy its token.
- **Display & sound:** ghost visibility/opacity, your countdown HUD, and chime.

Return to current position cancels both manual and automatic `/nextpos`
preview until the current slide ends. It does not seek or restart the music.
You can preview again immediately using the adjacent button.

Your selected role uses your local player. Other cast rings use the real nearby
player matching that cast member's name and home world. Standing at someone
else's mark cannot complete their cue. Missing players have gray markers.

An emote counts when its ID is observed on the assigned performer after the
cue is due and while on the correct mark. A short emote remains complete for
that cue. Leaving the mark, the next cue, changing positions, pausing/resuming,
or replaying resets completion. A preview with an upcoming emote stays yellow
until that cue is due; a mark without an emote requirement is green on arrival.
Commands the game cannot resolve stay pending instead of being marked complete.

The countdown names the next emote and slide, including a timed emote later
in the current slide. It uses the browser's song clock and holds while paused.
World labels remain visible with the plugin window closed. The optional HUD
at the top of the screen shows your selected cast member's upcoming/due emote.

## Prototype limits and first in-game checks

- A compiled native actor adapter is not proof of game-patch compatibility.
  Test one ghost in your rehearsal venue before a full-cast run. Verify
  position, facing, outfit, `/beesknees`, pause/seek, and removal on unload
  and zoning. Native interop can crash the game if the patch's structures
  or functions differ.
- The director's view copies actors currently loaded nearby. Missing actors
  have labelled markers only. Appearance snapshots are not uploaded or
  shared. Penumbra/Glamourer-specific appearance cloning is not implemented.
- Emotes use the game's primary emote action timeline. Props, special
  interactions, multi-part emotes, and exact animation phase after a seek
  are not guaranteed. Seeking reconstructs the applicable action and can
  restart its animation; it does not scrub the animation to music time.
- Floor rings and text are projected overlays, so they can show through
  scenery. The ghost itself is a game-rendered actor. Custom static VFX and
  pathfinding are not included. Distance is straight-line horizontal distance.
- The plugin only renders within 100 world units of the local player.
  Rendering is suppressed during zoning, cutscenes, combat, PvP, and GPose.
- Browser updates run every 250 ms. Full-cast timing still inherits your
  app's existing two-second correction broadcasts and 500 ms drift threshold.
  This is rehearsal timing, not an audio-synchronized animation system.
- Browser background throttling or a suspended tab can hide the markers.
  Browser spoken cues retain the existing app's behavior; the new per-actor
  filtering is applied to plugin ghost cues.
- Changes to slide additions/deletions and timing are still shared using the
  existing app behavior. Reopen the song on followers after structural edits;
  live stage-position and lyric updates use the existing broadcasts.

## Source layout

| File or folder | Responsibility |
| --- | --- |
| `Core/Models.cs` | Versioned JSON contract and validation |
| `Core/RehearsalEngine.cs` | Playback clock, preview/action selection, seek and reconnect behavior |
| `Core/ReadinessTracker.cs` | Per-performer position and emote completion |
| `Core/LoopbackBridge.cs` | Bounded loopback HTTP, exact origin, pairing token |
| `Plugin/Plugin.cs` | Dalamud services, venue capture, UI, markers, cue chime |
| `Plugin/NativeGhosts.cs` | Main-thread native actor ownership and emote timelines |
| `web/game-model.js` | Forward/inverse calibration and choreography compiler |
| `web/game-bridge.js` | Browser pairing, calibration and actor editing controls |
| `tools/integrate-web.py` | Checked integration into the existing Stage Manager source |
| `tests/` | Pure model, cross-language fixture, protocol and browser checks |

The files in `web/` are the canonical copies of the three added web modules.
`tools/integrate-web.py <stage-manager-directory>` synchronizes them. The
integration also updates `app.js` and adds `StageRenderer.commitStageData`.
Original integration files are backed up under
`artifacts/web-before-integration/`. Other pre-existing web changes were
preserved.

## Build and test

Install the .NET 10 SDK. On Windows, the Dalamud SDK normally resolves the
development references from `%APPDATA%\XIVLauncher\addon\Hooks\dev`. Override
`DALAMUD_HOME` if your matching API 15 references are elsewhere.

```powershell
dotnet build Plugin/StageManager.csproj -c Release
node --test tests/web.test.js
dotnet run --project tests/CoreTests/CoreTests.csproj -- tests/rehearsal.stage.json --bridge
```

The JavaScript tests write the shared JSON fixture consumed by C# tests.
Browser verification requires Playwright and Chrome; the command is at the
top of `tests/browser.test.mjs`. It uses a fake local game endpoint and the
real stage renderer, with external network requests blocked.

Build outputs live under `Plugin/bin/Release/`. The release zip contains
the plugin and its core assembly, not Dalamud or game binaries.
See [RELEASING.md](RELEASING.md) for packaging and repository feed updates.

## Research basis

The project uses the official [Dalamud plugin SDK](https://github.com/goatcorp/Dalamud.NET.Sdk)
and the public client-structure declarations for
[ClientObjectManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Object/ClientObjectManager.cs).
The optional local emote actor in
[Orange Guidance Tomestone](https://git.sharlayan.cloud/anna/OrangeGuidanceTomestone)
demonstrated the feasibility of this mechanism.
This prototype has its own implementation and does not copy that plugin's
assets, signatures, resource replacement hooks, or server.
