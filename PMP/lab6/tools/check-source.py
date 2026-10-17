"""Verify that the original lab4-5 files are unchanged, excluding build caches."""
from pathlib import Path
import hashlib
import json

project = Path(__file__).resolve().parent.parent
source = project.parent / 'lab4-5'
baseline = json.loads((project / 'docs/lab4-5-baseline.json').read_text(encoding='utf-8'))
ignored = {'.dart_tool', 'build', '.gradle', '.cxx', '.kotlin'}
current = {
    p.relative_to(source).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
    for p in source.rglob('*')
    if p.is_file() and not any(part in ignored for part in p.relative_to(source).parts)
}
changed = sorted(key for key in baseline.keys() | current.keys() if baseline.get(key) != current.get(key))
if changed:
    raise SystemExit('Original lab changed: ' + ', '.join(changed))
print(f'PASS: all {len(baseline)} original files unchanged; no source files added or removed.')
