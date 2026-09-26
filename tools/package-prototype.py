#!/usr/bin/env python3
"""Package the built Windows plugin, source, and a reviewable web update."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import difflib
import hashlib
import json
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
web = Path(sys.argv[1]).resolve()
out = root / 'artifacts'
out.mkdir(exist_ok=True)
release = root / 'Plugin' / 'bin' / 'Release'
subprocess.run([sys.executable, str(root / 'tools/prepare-release.py')], check=True)
version = json.loads((release / 'StageManager.json').read_text())['AssemblyVersion'].removesuffix('.0')

web_names = ['app.js', 'stage.js', 'game-model.js', 'game-bridge.js', 'game-bridge.css']
patch = []
for name in web_names:
    before = out / 'web-before-integration' / name
    patch.extend(difflib.unified_diff(before.read_text().splitlines(True) if before.exists() else [],
                                    (web / name).read_text().splitlines(True),
                                    fromfile='a/stage-manager/' + name if before.exists() else '/dev/null',
                                    tofile='b/stage-manager/' + name))
(root / 'web-integration.patch').write_text(''.join(patch))
with ZipFile(out / f'StageManager-{version}-web-update.zip', 'w', ZIP_DEFLATED) as z:
    for name in web_names: z.write(web / name, 'stage-manager/' + name)
    z.write(root / 'web-integration.patch', 'web-integration.patch')
    z.writestr('WEB-UPDATE.md', '# Stage Manager web prototype\n\nThese five source files are already integrated in the attached web checkout. '
               'Review the included patch before applying to another checkout. Preserve any newer changes. '
               'Publish through the existing web project workflow. No database migration is needed. '
               'The plugin browser bridge needs to run on the same Windows PC as FFXIV.\n')

with ZipFile(out / f'StageManager-{version}-source.zip', 'w', ZIP_DEFLATED) as z:
    for file in sorted(root.rglob('*')):
        relative = file.relative_to(root)
        if file.is_file() and not any(part in {'artifacts', 'bin', 'obj', '__pycache__', '.git', '.vs', '.references', 'node_modules', '.DS_Store'} for part in relative.parts):
            z.write(file, 'StageManagerPlugin/' + str(relative))

lines = []
for path in sorted(out.glob(f'StageManager-{version}-*.zip')):
    with ZipFile(path) as z: assert z.testzip() is None
    lines.append(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name)
    print(path.name + f' ({path.stat().st_size:,} bytes)')
(out / 'SHA256SUMS.txt').write_text('\n'.join(lines) + '\n')
