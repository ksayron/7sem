"""Optional local build cache for networks that reset long downloads.

Upstream TLS remains verified. APT signatures and kubectl checksums are still
checked by the image build. Only the two public distribution hosts are allowed.
"""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import threading
import time
import urllib.error
import urllib.request
import urllib.parse

CACHE = Path(__file__).resolve().parents[2] / '.artifacts/ci/downloads'
CACHE.mkdir(parents=True, exist_ok=True)
POOL = ThreadPoolExecutor(max_workers=64)
CONNECTIONS = threading.local()
LOCKS = {}
GUARD = threading.Lock()
CHUNK = 16 * 1024


def fetch(url, start, end, size):
    part = CACHE / (hashlib.sha256(url.encode()).hexdigest() + f'.{start}-{end}')
    if part.exists() and part.stat().st_size == end - start + 1:
        return part.read_bytes()
    for attempt in range(5):
        try:
            headers = {'Range': f'bytes={start}-{end}', 'User-Agent': 'Burmalda-LR10'}
            parsed = urllib.parse.urlsplit(url)
            if parsed.hostname == 'deb.debian.org' and not urllib.request.getproxies():
                if not getattr(CONNECTIONS, 'debian', None):
                    CONNECTIONS.debian = http.client.HTTPSConnection('deb.debian.org', timeout=40)
                CONNECTIONS.debian.request('GET', parsed.path + ('?' + parsed.query if parsed.query else ''), headers=headers)
                response = CONNECTIONS.debian.getresponse()
            else:
                response = urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=40)
            with response:
                expected = end - start + 1
                if response.status != 206 and size != expected:
                    raise RuntimeError('Upstream does not support byte ranges')
                if response.status == 206 and response.headers.get('Content-Range') != f'bytes {start}-{end}/{size}':
                    raise RuntimeError('Unexpected Content-Range')
                data = response.read(expected)
                if len(data) == expected:
                    part.write_bytes(data)
                    return data
                raise RuntimeError('Unexpected ranged response')
        except Exception:
            if getattr(CONNECTIONS, 'debian', None):
                CONNECTIONS.debian.close()
                CONNECTIONS.debian = None
            if attempt == 4:
                raise
            time.sleep(attempt + 1)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/health':
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain; charset=utf-8')
            self.end_headers()
            self.wfile.write(b'burmalda-ci-download-cache')
            return
        parts = self.path.lstrip('/').split('/', 1)
        if len(parts) != 2 or parts[0] not in {'deb.debian.org', 'dl.k8s.io'} or '..' in parts[1]:
            self.send_error(403)
            return
        url = 'https://' + '/'.join(parts)
        key = hashlib.sha256(url.encode()).hexdigest()
        path = CACHE / key
        with GUARD:
            lock = LOCKS.setdefault(key, threading.Lock())
        try:
            with lock:
                if not path.exists():
                    with urllib.request.urlopen(urllib.request.Request(url, method='HEAD'), timeout=30) as response:
                        size = int(response.headers['Content-Length'])
                    print(f'Download {url} ({size} bytes)', flush=True)
                    chunks = [POOL.submit(fetch, url, start, min(start + CHUNK, size) - 1, size) for start in range(0, size, CHUNK)]
                    data = b''.join(future.result() for future in chunks)
                    path.write_bytes(data)
            self.send_response(200)
            self.send_header('Content-Length', str(path.stat().st_size))
            self.end_headers()
            self.wfile.write(path.read_bytes())
        except urllib.error.HTTPError as error:
            self.send_error(error.code)
        except Exception as error:
            print(f'Failed {url}: {error}', flush=True)
            self.send_error(502)


if __name__ == '__main__':
    ThreadingHTTPServer.allow_reuse_address = False
    print('Build download cache listening on port 18089', flush=True)
    ThreadingHTTPServer(('0.0.0.0', 18089), Handler).serve_forever()
