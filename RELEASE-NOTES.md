# Stage Manager 0.1.3 — rehearsal feedback and controls

- Rings now check each assigned performer: red off mark, yellow on mark with
  an emote pending, green ready, and gray when that performer is not nearby.
- An observed matching emote completes the current cue. Short emotes stay
  complete while the performer remains on the mark; new cues and replay reset it.
- Ghosts return to idle when browser playback pauses or stops, and resume
  their current action when playback resumes.
- Countdown labels show upcoming emotes at the next position and later in the
  same slide. Your selected role also has a visible countdown HUD.
- Add Return to current position (also /stage current) to cancel a preview
  until the next slide without interrupting playback.
- Organize the window into Rehearsal, Connection, and Display & sound tabs.
- Remove choreography JSON loading and independent playback controls; the
  plugin follows the browser bridge.

Update through /xlplugins, enable the bridge in the Connection tab, reconnect
using the new pairing token, and arm the venue. Existing calibration works.
Game-rendered idle transitions and live performer emote detection require
in-game confirmation on Windows; automated checks cover cue/readiness logic.
