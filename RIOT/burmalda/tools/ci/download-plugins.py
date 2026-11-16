"""Download the exact Jenkins plugin closure; verify every artifact by SHA-256."""
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request

root = Path(__file__).resolve().parents[2]
target = root / '.artifacts/ci/plugins-locked'
cache = root / '.artifacts/ci/plugins'
target.mkdir(parents=True, exist_ok=True)
for name, spec in json.loads((root / 'docker/jenkins/plugins.lock.json').read_text()).items():
    path = target / (name + '.jpi')
    def valid(candidate):
        return candidate.exists() and hashlib.sha256(candidate.read_bytes()).hexdigest() == spec['sha256']
    if not valid(path):
        cached = cache / path.name
        if valid(cached):
            shutil.copyfile(cached, path)
        else:
            url = f"https://updates.jenkins.io/download/plugins/{name}/{spec['version']}/{name}.hpi"
            print('Downloading ' + name, flush=True)
            with urllib.request.urlopen(url, timeout=120) as response:
                path.write_bytes(response.read())
        if not valid(path):
            raise RuntimeError('Plugin hash mismatch: ' + name)
print('Jenkins plugins verified.')
