# Publishing a plugin update

The custom repository feed is the root `repo.json` on `main`. Dalamud reads
its version and downloads the matching GitHub release asset. Repository
source changes alone do not publish a new plugin version.

1. Update `Version` in `Plugin/StageManager.csproj`, the README version, and
   `RELEASE-NOTES.md`. Use a new version; do not replace an existing download.
2. Build on Windows with .NET 10 and references matching the supported stable
   Dalamud version. For a Dalamud API change, update the SDK and compatibility
   checks in `tools/prepare-release.py` after reviewing the native adapter.

   ```powershell
   dotnet build Plugin/StageManager.csproj -c Release
   node --test tests/web.test.js
   dotnet run --project tests/CoreTests/CoreTests.csproj -- tests/rehearsal.stage.json --bridge
   python tools/prepare-release.py
   ```

3. Commit and push the source. Create a GitHub release tagged `v<version>`
   from that commit. Use `RELEASE-NOTES.md` as its body and upload the generated
   `StageManager-<version>-win-x64.zip` and `SHA256SUMS.txt` from `artifacts/`.
   Mark prototype releases as prereleases. They are still installable from
   this custom feed without enabling Dalamud testing builds.
4. Publish the release, download its ZIP anonymously, and check the hash,
   archive integrity, and root `StageManager.dll` / `StageManager.json` files.
5. Copy `artifacts/repo.json` to the repository root, commit, and push `main`.
   Check that the public raw feed is valid JSON and its download URLs work.
   Keep `InternalName` equal to `StageManager` so installed copies update.

Feed URL:

```text
https://raw.githubusercontent.com/phamjordan/ffxiv-stage-manager-plugin/main/repo.json
```

The package contains only Stage Manager assemblies, its dependency manifest,
plugin manifest, and README. Do not distribute Dalamud, game assemblies,
local configuration, rehearsal exports, or build reference directories.

Web changes are published separately through the existing website repository.
See `DEPLOYMENT.md` for the initial web deployment record.
