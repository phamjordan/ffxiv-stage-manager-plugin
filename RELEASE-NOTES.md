# Stage Manager 0.1.2 — ghost position updates

- Correct the native transform update used when an existing ghost changes
  position or facing. The previous version wrote actor fields directly,
  which could leave the visible model standing on its first slide's mark.
- Use the game's position and rotation setters for both creation and updates.
  Updates still run independently of emote changes, including live position edits.
- Add position-transition regression coverage for browser playback, director
  view, preview cues, rewinds, and edits with an unchanged emote.
- Retains Dalamud API 15 and the existing choreography/calibration format.

Update Stage Manager through `/xlplugins`, then reconnect the website using
the new pairing token and arm the venue again. Existing calibration can be
reused. The native movement correction still needs confirmation inside FFXIV
on Windows; automated checks cannot validate the game's rendered model.

Install instructions and the feed URL are in the repository README.
