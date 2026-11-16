"""Local fixed-purpose Jenkins bridge. Only sanitized state and one release action.

Git/Jenkins credentials stay on the host. The demo Pod only holds a random bridge key.
"""
import importlib.machinery
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import secrets
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / '.artifacts/live-release'
SOURCE = RUNTIME / 'source'
TOKEN = (RUNTIME / 'bridge-token').read_text().strip()
API = importlib.machinery.SourceFileLoader('jenkins_api', str(ROOT / 'tools/ci/jenkins-api.py')).load_module()
JOB = 'job/burmalda-lab10/'
LOCK = threading.Lock()
COMMAND_LOCK = threading.Lock()
STATE = {'pending': False, 'command': None, 'error': None, 'commit': None}
EDITION = 'RIOT/burmalda/src/Services/Catalog/Burmalda.Catalog.Api/ReleaseEdition.cs'


def request(path, data=None):
    with LOCK:
        return API.request(path, data)


def state():
    build = json.loads(request(JOB + 'lastBuild/api/json?tree=number,building,result,timestamp,duration,actions[lastBuiltRevision[SHA1]]'))
    stages = json.loads(request(JOB + str(build['number']) + '/wfapi/describe')).get('stages', [])
    sha = next((x['lastBuiltRevision']['SHA1'] for x in build['actions'] if x.get('lastBuiltRevision')), '')
    return {'number': build['number'], 'building': build['building'], 'result': build['result'],
            'commit': sha, 'startedAtMs': build['timestamp'], 'durationMs': build['duration'],
            'checkedAtMs': int(time.time() * 1000), 'command': dict(STATE),
            'stages': [{'name': s['name'], 'status': s['status'], 'startedAtMs': s['startTimeMillis'],
                        'durationMs': s['durationMillis']} for s in stages if not s['name'].startswith('Declarative:')]}


def git(*args):
    result = subprocess.run(['git', '-C', str(SOURCE), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError('Git action failed: ' + args[0])
    return result.stdout.strip()


def release():
    try:
        if state()['building']:
            raise RuntimeError('Jenkins is already running a build')
        path = SOURCE / EDITION
        code = path.read_text(encoding='utf-8')
        match = re.search(r'public const int Number = (\d+);', code)
        if not match:
            raise RuntimeError('ReleaseEdition source is unavailable')
        if git('status', '--porcelain'):
            raise RuntimeError('Demo source worktree has unfinished changes')
        code = code[:match.start(1)] + str(int(match[1]) + 1) + code[match.end(1):]
        path.write_text(code, encoding='utf-8')
        git('add', '--', EDITION)
        git('commit', '-m', 'demo: publish catalog edition ' + str(int(match[1]) + 1))
        sha = git('rev-parse', 'HEAD')
        STATE['commit'] = sha
        git('push', 'origin', 'HEAD:refs/heads/demo/live-release')
        # The configured SCM remains the only source of Jenkinsfile and application code.
        request(JOB + 'build', {})
        STATE['command'] = 'queued'
    except Exception as error:
        STATE['error'] = str(error)
        STATE['command'] = 'failed'
    finally:
        STATE['pending'] = False
        (RUNTIME / 'last-command.json').write_text(json.dumps(STATE))
        COMMAND_LOCK.release()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send(self, code, value):
        body = json.dumps(value).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def authorized(self):
        return secrets.compare_digest(self.headers.get('X-Release-Key', ''), TOKEN)

    def do_GET(self):
        if self.path == '/health':
            self.send(200, {'service': 'burmalda-release-bridge'})
        elif not self.authorized():
            self.send(401, {'error': 'Unauthorized'})
        elif self.path == '/state':
            try:
                self.send(200, state())
            except Exception:
                self.send(503, {'error': 'Jenkins state is unavailable'})
        else:
            self.send(404, {})

    def do_POST(self):
        if not self.authorized():
            return self.send(401, {'error': 'Unauthorized'})
        if self.path != '/release':
            return self.send(404, {})
        if not COMMAND_LOCK.acquire(blocking=False):
            return self.send(409, {'error': 'A release request is already pending'})
        try:
            # Limit the command to the deliberately configured demo branch.
            definition = request(JOB + 'config.xml').decode()
            current = state()
            queued = STATE['command'] == 'queued' and STATE['commit'] != current['commit']
            if '<name>*/demo/live-release</name>' not in definition or current['building'] or queued:
                COMMAND_LOCK.release()
                return self.send(409, {'error': 'Select demo/live-release and wait for the active build'})
            STATE.update(pending=True, command='publishing', error=None, commit=None)
            threading.Thread(target=release, daemon=True).start()
            self.send(202, dict(STATE))
        except Exception:
            COMMAND_LOCK.release()
            self.send(503, {'error': 'Cannot contact Jenkins'})


if __name__ == '__main__':
    ThreadingHTTPServer.allow_reuse_address = False
    ThreadingHTTPServer(('0.0.0.0', 18090), Handler).serve_forever()
