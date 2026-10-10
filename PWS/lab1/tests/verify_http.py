"""Real HTTP integration tests, no third-party Python dependencies."""
import base64
import argparse
import concurrent.futures
import datetime
import http.cookiejar
import json
import pathlib
import sys
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
CHECKS = []
TRACE = []


class Client:
    def __init__(self, port):
        self.url = f"http://localhost:{port}"
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, method="GET", suffix="", body=None, raw=None, content_type="application/json", expected=200):
        data = json.dumps(body).encode() if body is not None else raw
        headers = {"Content-Type": content_type} if data is not None else {}
        request = urllib.request.Request(self.url + "/stack" + suffix, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status = response.status
            result = json.load(response)
            instance = response.headers.get("X-Instance")
            policy = response.headers.get("X-Gateway-Policy")
            assert status == expected, (method, status, expected, result)
            if status == 200:
                assert result["RESULT"] == result["baseValue"] + (result["topStackValue"] or 0)
                assert result["stackSize"] == len(result["stack"])
                assert result["topStackValue"] == (result["stack"][-1] if result["stack"] else None)
                assert instance == result["instance"]
                assert response.headers.get("Cache-Control") == "no-store"
            TRACE.append({"port": int(self.url.rsplit(":", 1)[1]), "method": method, "query": suffix,
                          "status": status, "instance": instance, "policy": policy, "response": result})
            return result, instance, policy


def check(name, function):
    try:
        function()
        CHECKS.append({"name": name, "passed": True})
        print(f"[ok] {name}")
    except Exception as error:
        CHECKS.append({"name": name, "passed": False, "error": repr(error)})
        print(f"[error] {name}: {error}")


def operations(port):
    client = Client(port)
    assert client.request()[0]["RESULT"] == 0
    assert client.request("POST", body={"RESULT": 10})[0]["RESULT"] == 10
    assert client.request("PUT", "?ADD=5")[0]["RESULT"] == 15
    assert client.request("PUT", "?ADD=-3")[0]["RESULT"] == 7
    for _ in range(3):
        assert client.request()[0]["RESULT"] == 7  # GET must not accumulate.
    assert client.request("DELETE")[0]["RESULT"] == 15
    assert client.request("DELETE")[0]["RESULT"] == 10
    client.request("DELETE", expected=409)
    assert client.request()[0]["RESULT"] == 10
    # Setting base with an existing stack preserves that stack.
    client.request("PUT", "?ADD=9")
    assert client.request("POST", body={"RESULT": 2})[0]["RESULT"] == 11
    assert Client(port).request()[0]["RESULT"] == 0


def validation(port):
    client = Client(port)
    client.request("POST", body={"RESULT": 12})
    for suffix in ("", "?ADD=text", "?ADD=1.5", "?ADD=2147483648", "?ADD=1&ADD=2"):
        client.request("PUT", suffix, expected=400)
    for body in ({}, {"RESULT": "2"}, {"RESULT": 1.2}, {"RESULT": None}, {"RESULT": True},
                 {"RESULT": 2147483648}, [1]):
        client.request("POST", body=body, expected=400)
    client.request("POST", raw=b"{", expected=400)
    client.request("POST", raw=b'{"RESULT":1,"RESULT":2}', expected=400)
    client.request("POST", raw=b'{"RESULT":2}', content_type="text/plain", expected=415)
    client.request("POST", raw=b" " * 5000, expected=413)
    assert client.request()[0]["RESULT"] == 12
    client.request("POST", body={"RESULT": 2147483647})
    assert client.request("PUT", "?ADD=2147483647")[0]["RESULT"] == 4294967294
    client.request("POST", body={"RESULT": -2147483648})
    assert client.request("PUT", "?ADD=-2147483648")[0]["RESULT"] == -4294967296


def capacity(port):
    client = Client(port)
    for i in range(64):
        client.request("PUT", f"?ADD={i}")
    client.request("PUT", "?ADD=65", expected=409)
    assert client.request()[0]["stackSize"] == 64
    assert client.request("DELETE")[0]["stackSize"] == 63


def sticky():
    seen = set()
    for _ in range(8):
        client = Client(8101)
        _, instance, policy = client.request("POST", body={"RESULT": 100})
        seen.add(instance)
        assert policy == "sticky"
        for value in (4, 8, 12):
            state, current, _ = client.request("PUT", f"?ADD={value}")
            assert current == instance and state["RESULT"] == 100 + value
        for _ in range(5):
            assert client.request()[1] == instance
        cookie_names = {cookie.name for cookie in client.cookies}
        assert {"WS01.Session", "WS01.Affinity"} <= cookie_names
    assert seen == {"WS01-01-A", "WS01-01-B"}, seen


def round_robin():
    client = Client(8102)
    sequence = []
    for method, suffix, body, result in (
        ("POST", "", {"RESULT": 20}, 20),
        ("PUT", "?ADD=3", None, 23),
        ("PUT", "?ADD=8", None, 28),
        ("GET", "", None, 28),
        ("DELETE", "", None, 23),
        ("GET", "", None, 23),
        ("DELETE", "", None, 20),
        ("GET", "", None, 20),
    ):
        state, instance, policy = client.request(method, suffix, body)
        assert state["RESULT"] == result and policy == "round-robin"
        sequence.append(instance)
    assert set(sequence) == {"WS01-02-A", "WS01-02-B"}
    assert all(a != b for a, b in zip(sequence, sequence[1:])), sequence
    assert {cookie.name for cookie in client.cookies} == {"WS02.State"}


def cookie_portability():
    client = Client(8121)
    client.request("POST", body={"RESULT": 40})
    client.request("PUT", "?ADD=2")
    cookie = next(cookie for cookie in client.cookies if cookie.name == "WS02.State")
    payload = cookie.value.split(".")[0]
    decoded = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    assert decoded == {"BaseValue": 40, "Stack": [2]}, decoded
    assert len(cookie.value) < 3000
    assert any(name.lower() == "httponly" for name in cookie._rest), cookie._rest
    client.url = "http://localhost:8122"
    state, instance, _ = client.request()
    assert state["RESULT"] == 42 and instance == "WS01-02-B"
    state, _, _ = client.request("PUT", "?ADD=7")
    assert state["RESULT"] == 47
    client.url = "http://localhost:8121"
    assert client.request()[0]["RESULT"] == 47
    cookie = next(cookie for cookie in client.cookies if cookie.name == "WS02.State")
    # Change the JSON payload while keeping the original signature.
    payload, signature = cookie.value.split(".")
    payload = base64.urlsafe_b64encode(b'{"BaseValue":999,"Stack":[]}').decode().rstrip("=")
    cookie.value = payload + "." + signature
    client.request(expected=400)


def server_concurrency():
    client = Client(8111)
    client.request()
    def push(value):
        return client.request("PUT", f"?ADD={value}")[0]
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(push, range(16)))
    state = client.request()[0]
    assert state["stackSize"] == 16 and set(state["stack"]) == set(range(16)), state


def routing_and_ui():
    for port in (8101, 8102, 8111, 8112, 8121, 8122):
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(f"http://localhost:{port}/", timeout=10) as response:
            html = response.read().decode()
            assert response.status == 200 and "Лабораторная 1" in html and "__MODE__" not in html
        for method, path, status in (("GET", "/missing", 404), ("PATCH", "/stack", 405)):
            request = urllib.request.Request(f"http://localhost:{port}{path}", method=method)
            try:
                response = opener.open(request, timeout=10)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                assert response.status == status, (port, method, response.status)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument('--deployment', choices=['docker', 'local'], default='docker')
    deployment = parser.parse_args().deployment
    for port in (8111, 8112, 8121, 8122, 8101, 8102):
        check(f"CRUD, LIFO, repeated GET, client isolation :{port}", lambda port=port: operations(port))
    for port in (8111, 8121):
        check(f"Input validation and integer boundaries :{port}", lambda port=port: validation(port))
        check(f"Capacity and failed mutation atomicity :{port}", lambda port=port: capacity(port))
    check("Sticky Session: same replica per client, both replicas used", sticky)
    check("Round Robin: alternating replicas preserve state", round_robin)
    check("Client cookie: encoded state, cross-replica use, tamper rejection", cookie_portability)
    check("Server session: concurrent PUSH without lost updates", server_concurrency)
    check("Demo pages and HTTP routing", routing_and_ui)
    passed = all(item["passed"] for item in CHECKS)
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    stamp = datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=3))).isoformat()
    evidence = {"timestamp": stamp, "timezone": "Europe/Minsk", "deployment": deployment, "passed": passed, "checks": CHECKS, "requests": TRACE}
    (output / "verification.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# Проверка PWS, лабораторная 1", "", f"Время: {stamp}", "",
             f"Развёртывание: {deployment}.", "",
             f"Проверки: {sum(item['passed'] for item in CHECKS)}/{len(CHECKS)}. HTTP-запросов к /stack: {len(TRACE)}.", "",
             "| Проверка | Результат |", "|---|---|"]
    lines += [f"| {item['name']} | {'PASS' if item['passed'] else 'FAIL: ' + item['error']} |" for item in CHECKS]
    target = 'контейнеры Docker' if deployment == 'docker' else 'локальные процессы ASP.NET Core на Windows'
    lines += ["", f"Полный журнал HTTP: `verification.json`. Запросы направлены в реально запущенные {target}."]
    (output / "verification.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"[info] {sum(item['passed'] for item in CHECKS)}/{len(CHECKS)} checks passed; {len(TRACE)} requests.")
    sys.exit(0 if passed else 1)
