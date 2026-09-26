# Stage Manager 0.1.4 — stage boundaries and direction guidance

- Calibrate with all four stage corners: front-left, front-right, back-right,
  back-left, following the map with the audience at the bottom.
- See a numbered stage outline on the web map and a cyan boundary in-game.
- Follow a line from your moving character to your own displayed mark, with
  an arrow, remaining horizontal distance, and height difference when needed.
- The line is blue for the current mark and gold for the next-position preview.
  /nextpos and Preview next position switch it to the next mark; Return to
  current position switches it back. It disappears when you reach your mark.
- Toggle the stage boundary and direction line in Display & sound. Both start
  enabled and work with ghost models disabled.
- Existing three-point calibrations retain their mapping. Capture and save
  four corners to enable the boundary. All four captures contribute to the new
  mapping; crossed corners and mismatched captures are rejected.

Update through /xlplugins, refresh the Stage Manager website, reconnect the
bridge with its new pairing token, and arm the venue. Capture all four corners
and save the calibration to use the outline. The website URL stays the same.

The release compiles against stable Dalamud API 15. Calibration, protocol, and
browser checks pass; the new in-game overlays need visual confirmation in
FFXIV on Windows. Guidance is a straight line, not obstacle-aware pathfinding.
