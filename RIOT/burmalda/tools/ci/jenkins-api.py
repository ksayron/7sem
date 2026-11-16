"""Local authenticated inspection/export of Jenkins evidence. Never prints credentials."""
import base64
import http.cookiejar
import json
from pathlib import Path
import sys
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:8081/'
PASSWORD = (ROOT / '.artifacts/ci/secrets/admin-password').read_text().strip()
CLIENT = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
CLIENT.addheaders = [('Authorization', 'Basic ' + base64.b64encode(('admin:' + PASSWORD).encode()).decode())]


def request(path, data=None):
    headers = {}
    if data is not None:
        crumb = json.loads(request('crumbIssuer/api/json'))
        headers[crumb['crumbRequestField']] = crumb['crumb']
        data = urllib.parse.urlencode(data).encode()
    with CLIENT.open(urllib.request.Request(BASE + path, data=data, headers=headers), timeout=30) as response:
        return response.read()


if __name__ == '__main__':
    action = sys.argv[1]
    job = 'job/burmalda-lab10/'
    if action == 'status':
        data = json.loads(request(job + 'api/json?tree=builds[number,result,building,duration],lastBuild[number],nextBuildNumber'))
        print(json.dumps(data))
    elif action == 'console':
        print(request(job + sys.argv[2] + '/consoleText').decode(errors='replace')[-16000:])
    elif action == 'stages':
        print(request(job + sys.argv[2] + '/wfapi/describe').decode())
    elif action == 'run':
        request(job + 'build', {})
        print('Build queued.')
    elif action == 'export':
        number = sys.argv[2]
        output = ROOT / '.artifacts/ci/export' / number
        output.mkdir(parents=True, exist_ok=True)
        data = json.loads(request(job + number + '/api/json'))
        (output / 'build.json').write_text(json.dumps(data, indent=2))
        (output / 'stages.json').write_bytes(request(job + number + '/wfapi/describe'))
        (output / 'console.txt').write_bytes(request(job + number + '/consoleText'))
        for artifact in data['artifacts']:
            path = artifact['relativePath']
            target = output / 'artifacts' / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(request(job + number + '/artifact/' + urllib.parse.quote(path)))
        print('Exported build ' + number)
