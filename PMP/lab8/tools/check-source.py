"""Verify that the earlier coursework source trees match the initial snapshot."""
from pathlib import Path
import hashlib
import json

project = Path(__file__).resolve().parents[1]
base = project.parent
baseline = json.loads((project / 'docs/source-baseline.json').read_text(encoding='utf-8'))
ignored = {'build', '.dart_tool', '.idea', '.vscode', '.gradle', '.kotlin', '.cxx'}
actual = {}
for name in ['lab4-5', 'lab6', 'lab7']:
    for path in (base / name).rglob('*'):
        if path.is_file() and not ignored.intersection(path.relative_to(base / name).parts) and path.name != 'local.properties':
            actual[path.relative_to(base).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
changed = sorted(name for name in baseline.keys() | actual.keys() if baseline.get(name) != actual.get(name))
if changed:
    raise SystemExit('Source mismatch:\n' + '\n'.join(changed))
print(f'PASS: {len(baseline)} files in lab4-5, lab6 and lab7 unchanged; no source files added or removed.')
