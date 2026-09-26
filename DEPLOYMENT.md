# Stage Manager web deployment — 2026-09-25

- Production: https://ffxiv.jjammin.com/stage-manager/
- Repository: https://github.com/phamjordan/ffxiv
- Branch: `main`
- Commit: `d62e8d89b305716797663d76fd4011a78d18a98d`
- Cloudflare Worker: `ffxiv`
- Initial deployed version: `1b99cd4e-ccf4-44eb-8276-d31c5eb5bd01` (100% traffic at deployment)
- Previous version: `6873a687-4b76-4ab8-b9e9-5a50a19a8f6e`
- Stage Manager module version: `fe319e1d426f`

The five prototype source files and a scoped deployment builder were committed
and pushed. The builder versions nested module imports and the dynamically
loaded bridge stylesheet. Cloudflare uploaded six changed/new assets, all
under `/stage-manager/`.

The isolated release used committed source for the existing database, audio,
and stylesheet modules, preserving the live versions. Other uncommitted work
in the original checkout was left uncommitted. No database migrations or
production data edits were performed. The production domain and route stayed
the same.

Verification: browser workflow against the generated bundle passed; production
Stage Manager files matched that bundle byte-for-byte; sampled other site
pages remained identical. Native plugin runtime verification is still separate.

## Browser help update — 2026-09-26

- Web commit: `f6588a6` (only `stage-manager/game-bridge.js` source changed)
- Active Worker version: `98f85ec8-8496-453e-afcd-79ce4a06d938` (100% traffic)
- Previous live version: `49587a02-f610-4621-b502-eb701ece3759`
- Stage Manager module version: `b39beb4e07af`

The browser now describes choreography downloads as backups and directs users
to bridge playback, matching plugin 0.1.3's removal of file import. Existing
calibration export/import remains available. No database or choreography data
was changed.

Before deployment, all 11 live Stage Manager text files matched the expected
baseline, and 31 other differing checkout assets matched the isolated release
baseline. The browser regression passed. Cloudflare uploaded three changed
files: `stage-manager/game-bridge.js`, `stage-manager/app.js`, and
`stage-manager/index.html`; the latter two carry the new module version.
Live files matched the release bundle after deployment; sampled other pages
also remained identical.
