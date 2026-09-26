# Stage Manager web deployment — 2026-09-25

- Production: https://ffxiv.jjammin.com/stage-manager/
- Repository: https://github.com/phamjordan/ffxiv
- Branch: `main`
- Commit: `d62e8d89b305716797663d76fd4011a78d18a98d`
- Cloudflare Worker: `ffxiv`
- Active version: `1b99cd4e-ccf4-44eb-8276-d31c5eb5bd01` (100% traffic)
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
