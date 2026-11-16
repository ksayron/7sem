"""Jenkins stage commands. No external Python packages; all state is per workspace."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
os.chdir(ROOT)
RUNTIME = ROOT / '.artifacts/ci'
RESULTS = RUNTIME / 'results'
RESULTS.mkdir(parents=True, exist_ok=True)
SERVICES = ['identity', 'catalog', 'lending', 'economy', 'reputation', 'notifications', 'gateway', 'web']
KUBE = ['kubectl', '--context', 'docker-desktop', '-n', 'burmalda']
UNIT = ['Domain', 'LibraryGame', 'Raft', 'DemoControl']


def run(args, *, capture=False, env=None, timeout=900, check=True):
    result = subprocess.run([str(a) for a in args], text=True, env=env,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if capture else None, timeout=timeout)
    if check and result.returncode:
        if capture:
            print(result.stdout, result.stderr, file=sys.stderr)
        raise RuntimeError(f'{args[0]} {args[1]} failed ({result.returncode})')
    return result.stdout.strip() if capture else result.returncode


def save(name, data):
    (RESULTS / name).write_text(json.dumps(data, indent=2) + '\n')


def read(name):
    return json.loads((RESULTS / name).read_text())


def kube(*args, **kwargs):
    return run(KUBE + list(args), **kwargs)


def snapshot(name):
    # Resource specs may contain configuration; retain only operational metadata.
    data = json.loads(kube('get', 'deployments,statefulsets,pods', '-o', 'json', capture=True))
    save(name, [{'kind': x['kind'], 'name': x['metadata']['name'], 'uid': x['metadata']['uid'],
                 'status': x.get('status'),
                 'containers': x['spec'].get('containers', x['spec'].get('template', {}).get('spec', {}).get('containers', [])) and
                 [{'name': c['name'], 'image': c['image']} for c in x['spec'].get('containers', x['spec'].get('template', {}).get('spec', {}).get('containers', []))]}
                for x in data['items']])


def preflight():
    sha = run(['git', 'rev-parse', 'HEAD'], capture=True)
    number = os.environ['BUILD_NUMBER']
    if not number.isdigit():
        raise RuntimeError('BUILD_NUMBER must be numeric')
    save('release.json', {'commit': sha, 'build': number, 'tag': f'ci-{number}-{sha[:12]}'})
    for args in [['dotnet', '--version'], ['git', '--version'], ['docker', 'version'], ['node', '--version'], ['kubectl', 'version', '--client']]:
        run(args)
    kube('get', 'deployment/catalog', 'service/web')
    for permission in [('patch', 'deployments'), ('create', 'pods', '--subresource=portforward')]:
        if kube('auth', 'can-i', *permission, capture=True) != 'yes':
            raise RuntimeError('Missing Kubernetes permission')
    snapshot('before.json')


def restore():
    run(['dotnet', 'restore', 'Burmalda.slnx', '-m:1'])


def build():
    run(['dotnet', 'build', 'Burmalda.slnx', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false'])


def reports(trx):
    root = ET.parse(trx).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows = root.findall('.//t:UnitTestResult', ns)
    if not rows:
        raise RuntimeError('No test results in ' + str(trx))
    suite = ET.Element('testsuite', name=trx.stem, tests=str(len(rows)), failures='0', skipped='0')
    failures = skipped = 0
    for row in rows:
        case = ET.SubElement(suite, 'testcase', name=row.attrib['testName'], classname=trx.stem)
        outcome = row.attrib.get('outcome')
        if outcome == 'NotExecuted':
            skipped += 1
            ET.SubElement(case, 'skipped')
        elif outcome != 'Passed':
            failures += 1
            detail = row.find('t:Output/t:ErrorInfo', ns)
            ET.SubElement(case, 'failure', message=outcome or 'Unknown').text = ''.join(detail.itertext()) if detail is not None else outcome
    suite.set('failures', str(failures))
    suite.set('skipped', str(skipped))
    ET.ElementTree(suite).write(trx.with_suffix('.junit.xml'), encoding='utf-8', xml_declaration=True)
    if skipped:
        raise RuntimeError(f'{trx.name}: {skipped} unexpected skipped tests')


def test_project(project, name, env=None, test_filter=None):
    trx = RESULTS / (name + '.trx')
    command = ['dotnet', 'test', project, '-c', 'Release', '--no-build', '--no-restore', '-m:1',
               '--logger', 'trx;LogFileName=' + trx.name, '--results-directory', str(RESULTS)]
    if test_filter:
        command += ['--filter', test_filter]
    code = run(command, env=env, check=False)
    if not trx.exists():
        raise RuntimeError('Test runner produced no report: ' + name)
    reports(trx)
    if code:
        raise RuntimeError('Tests failed: ' + name)


def dependencies():
    name = 'burmalda-ci-test-' + read('release.json')['build']
    state = {'network': name, 'postgres': name + '-pg', 'rabbit': name + '-mq', 'password': secrets.token_hex(24)}
    # This private state is never archived.
    (RUNTIME / 'dependencies.json').write_text(json.dumps(state))
    run(['docker', 'network', 'create', name])
    run(['docker', 'network', 'connect', name, os.environ['CI_AGENT_CONTAINER']])
    env = dict(os.environ, POSTGRES_PASSWORD=state['password'], RABBITMQ_DEFAULT_USER='ci', RABBITMQ_DEFAULT_PASS=state['password'])
    run(['docker', 'run', '-d', '--name', state['postgres'], '--network', name, '--network-alias', 'ci-postgres',
         '--memory', '512m', '-e', 'POSTGRES_PASSWORD', 'postgres:17-alpine'], env=env)
    run(['docker', 'run', '-d', '--name', state['rabbit'], '--network', name, '--network-alias', 'ci-rabbit',
         '--memory', '512m', '-e', 'RABBITMQ_DEFAULT_USER', '-e', 'RABBITMQ_DEFAULT_PASS', 'rabbitmq:4-management-alpine'], env=env)
    for container, command in [(state['postgres'], ['pg_isready', '-U', 'postgres']), (state['rabbit'], ['rabbitmq-diagnostics', '-q', 'ping'])]:
        for attempt in range(60):
            result = subprocess.run(['docker', 'exec', container] + command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if result.returncode == 0:
                break
            time.sleep(2)
        else:
            raise RuntimeError('Test dependency did not become ready: ' + container)
    return test_env()


def test_env():
    state = json.loads((RUNTIME / 'dependencies.json').read_text())
    return dict(os.environ, BURMALDA_TEST_POSTGRES=f"Host=ci-postgres;Database=postgres;Username=postgres;Password={state['password']}",
                BURMALDA_TEST_RABBIT_HOST='ci-rabbit', BURMALDA_TEST_RABBIT_USER='ci',
                BURMALDA_TEST_RABBIT_PASSWORD=state['password'], BURMALDA_TEST_RABBIT_PORT='5672',
                BURMALDA_LOAD_REPORT=str(RESULTS / 'load.json'))


def test():
    for name in UNIT:
        test_project(f'tests/Burmalda.{name}.Tests', name)
    test_project('tests/Burmalda.Catalog.IntegrationTests', 'Integration', dependencies(), 'Category!=Load')


def load():
    test_project('tests/Burmalda.Catalog.IntegrationTests', 'Load', test_env(), 'Category=Load')


def package():
    release = read('release.json')
    images = {}
    for service in SERVICES:
        area = 'Clients' if service == 'web' else 'Gateway' if service == 'gateway' else 'Services'
        projects = list((ROOT / 'src' / area).rglob(f'Burmalda.{service.capitalize()}.Api.csproj')) if area == 'Services' else list((ROOT / 'src' / area).rglob(f'Burmalda.{service.capitalize()}.csproj'))
        if len(projects) != 1:
            raise RuntimeError('Ambiguous project for ' + service)
        project = projects[0]
        image = f"burmalda-{service}:{release['tag']}"
        args = ['docker', 'build', '-f', 'docker/Dockerfile', '--target', 'web' if service == 'web' else 'runtime', '-t', image,
                '--label', 'org.opencontainers.image.revision=' + release['commit'],
                '--label', 'org.opencontainers.image.version=' + release['tag'],
                '--build-arg', 'PROJECT_PATH=' + project.relative_to(ROOT).as_posix(),
                '--build-arg', 'ASSEMBLY_NAME=' + project.stem]
        print('Packaging ' + image, flush=True)
        run(args + ['.'], timeout=1500)
        info = json.loads(run(['docker', 'image', 'inspect', image], capture=True))[0]
        images[service] = {'image': image, 'id': info['Id'], 'labels': info['Config']['Labels']}
    save('images.json', images)


def deploy():
    images, release = read('images.json'), read('release.json')
    previous = {'changed': [], 'deployments': {}}
    for service in SERVICES:
        obj = json.loads(kube('get', 'deployment/' + service, '-o', 'json', capture=True))
        previous['deployments'][service] = obj['spec']
    (RUNTIME / 'previous.json').write_text(json.dumps(previous))
    for service in SERVICES:
        previous['changed'].append(service)
        (RUNTIME / 'previous.json').write_text(json.dumps(previous))
        # One atomic template update prevents separate annotation/image rollouts.
        patch = {'spec': {'replicas': 1, 'template': {'metadata': {'annotations': {
            'burmalda.local/commit': release['commit'], 'burmalda.local/build': release['build']}},
            'spec': {'containers': [{'name': service, 'image': images[service]['image']}]}}}}
        patch_file = RUNTIME / 'patch.json'
        patch_file.write_text(json.dumps(patch))
        kube('patch', 'deployment/' + service, '--type', 'strategic', '--patch-file', patch_file)
        kube('rollout', 'status', 'deployment/' + service, '--timeout=300s', timeout=330)
    snapshot('deployed.json')


def verify():
    images = read('images.json')
    for service in SERVICES:
        obj = json.loads(kube('get', 'deployment/' + service, '-o', 'json', capture=True))
        if obj['spec']['template']['spec']['containers'][0]['image'] != images[service]['image']:
            raise RuntimeError('Wrong deployed image: ' + service)
        pods = json.loads(kube('get', 'pods', '-l', 'app=' + service, '-o', 'json', capture=True))['items']
        active = [p for p in pods if not p['metadata'].get('deletionTimestamp')]
        if not active or any(p['spec']['containers'][0]['image'] != images[service]['image'] or
                             not p.get('status', {}).get('containerStatuses') or
                             not all(c.get('ready') and c.get('imageID') for c in p['status']['containerStatuses']) for p in active):
            raise RuntimeError('New image is not Ready: ' + service)
    log = (RESULTS / 'port-forward.log').open('w')
    proc = subprocess.Popen(KUBE + ['port-forward', 'service/web', '15300:8080', '--address', '127.0.0.1'], stdout=log, stderr=log)
    try:
        import urllib.request
        for attempt in range(60):
            if proc.poll() is not None:
                raise RuntimeError('Service port-forward exited')
            try:
                urllib.request.urlopen('http://127.0.0.1:15300/', timeout=2).close()
                break
            except OSError:
                time.sleep(1)
        else:
            raise RuntimeError('Service endpoint is unavailable')
        env = dict(os.environ, BURMALDA_WEB_URL='http://127.0.0.1:15300', BURMALDA_EXPECTED_MODE='kubernetes',
                   BURMALDA_RESULT_DIR=str(RESULTS / 'acceptance'), BURMALDA_SESSION_FILE=str(RUNTIME / 'session.json'))
        run(['node', 'tools/verify-core.cjs'], env=env, timeout=300)
        snapshot('verified.json')
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
        log.close()


def rollback():
    path = RUNTIME / 'previous.json'
    if not path.exists():
        print('No deployment changes; previous release remains unchanged.')
        return
    try:
        snapshot('failed.json')
        events = json.loads(kube('get', 'events', '-o', 'json', capture=True))
        save('failure-events.json', events)
    except Exception as error:
        print('Diagnostic collection failed: ' + str(error), file=sys.stderr)
    previous = json.loads(path.read_text())
    errors = []
    for service in reversed(previous['changed']):
        spec = previous['deployments'][service]
        annotations = spec['template']['metadata'].setdefault('annotations', {})
        for key in ['burmalda.local/commit', 'burmalda.local/build']:
            annotations.setdefault(key, None)
        patch = RUNTIME / 'rollback-patch.json'
        patch.write_text(json.dumps({'spec': {'replicas': spec['replicas'], 'template': spec['template']}}))
        try:
            kube('patch', 'deployment/' + service, '--type', 'merge', '--patch-file', patch)
            kube('rollout', 'status', 'deployment/' + service, '--timeout=300s', timeout=330)
        except Exception as error:
            errors.append(f'{service}: {error}')
    save('rollback.json', {'restored': previous['changed'], 'errors': errors})
    if errors:
        raise RuntimeError('Rollback incomplete: ' + '; '.join(errors))


def cleanup():
    state_file = RUNTIME / 'dependencies.json'
    errors = []
    if state_file.exists():
        state = json.loads(state_file.read_text())
        if not all(state[key].startswith('burmalda-ci-test-') for key in ['network', 'postgres', 'rabbit']):
            raise RuntimeError('Unexpected cleanup target')
        for args in [['docker', 'rm', '-fv', state['postgres'], state['rabbit']],
                     ['docker', 'network', 'disconnect', state['network'], os.environ['CI_AGENT_CONTAINER']],
                     ['docker', 'network', 'rm', state['network']]]:
            if run(args, check=False):
                errors.append(' '.join(args[:3]))
        state_file.unlink()
    save('cleanup.json', {'errors': errors})
    if errors:
        raise RuntimeError('CI test cleanup incomplete')


if __name__ == '__main__':
    try:
        actions = {f.__name__: f for f in [preflight, restore, build, test, load, package, deploy, verify, rollback, cleanup]}
        actions[sys.argv[1]]()
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
