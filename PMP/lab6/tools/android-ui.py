"""ADB UI helper for reproducible lab checks. Select controls from observed UI XML."""
from pathlib import Path
import argparse
import re
import subprocess
import time
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('action', choices=['dump', 'tap', 'edit', 'swipe', 'screenshot', 'focus'])
parser.add_argument('value', nargs='?', default='')
parser.add_argument('text', nargs='?', default='')
args = parser.parse_args()
adb = r'D:\Android\sdk-coffee\platform-tools\adb.exe'
out = Path(__file__).resolve().parent.parent / 'docs/screenshots'
out.mkdir(parents=True, exist_ok=True)

def run(*parts):
    return subprocess.check_output([adb, '-s', 'emulator-5554', *parts], timeout=30).decode('utf-8', errors='replace')

def nodes():
    for attempt in range(3):
        result = run('shell', 'uiautomator', 'dump', '--compressed', '/sdcard/lab6-ui.xml')
        if 'dumped to' in result:
            break
        time.sleep(1)
    else:
        raise SystemExit('UI root is not ready. Wait for Android/app startup and retry.')
    xml = run('shell', 'cat', '/sdcard/lab6-ui.xml')
    (out / 'current-ui.xml').write_text(xml, encoding='utf-8')
    return list(ET.fromstring(xml).iter('node'))

if args.action == 'dump':
    for node in nodes():
        label = (node.get('text', '') + ' ' + node.get('content-desc', '')).strip()
        if label:
            print(f'{label!r} | {node.get("resource-id")} | {node.get("bounds")} | {node.get("class")}')
elif args.action in ['tap', 'edit']:
    visible = nodes()
    if args.action == 'edit' and args.value.startswith('@'):
        fields = [n for n in visible if n.get('class') == 'android.widget.EditText']
        matches = fields[int(args.value[1:]):int(args.value[1:])+1]
    else:
        matches = [n for n in visible if args.value in (n.get('text', '') + ' ' + n.get('content-desc', '') + ' ' + n.get('resource-id', ''))]
        if args.action == 'edit':
            matches = [n for n in matches if n.get('class') == 'android.widget.EditText']
    if not matches:
        raise SystemExit(f'No visible control matches {args.value!r}. Dump or scroll first.')
    node = min(matches, key=lambda n: len(n.get('text', '') + n.get('content-desc', '')))
    x1, y1, x2, y2 = map(int, re.findall(r'\d+', node.get('bounds')))
    run('shell', 'input', 'tap', str((x1+x2)//2), str((y1+y2)//2))
    if args.action == 'edit':
        run('shell', 'input', 'keyevent', 'KEYCODE_MOVE_END')
        # One key event per existing character; oversized runs are slow on AVD.
        run('shell', 'input', 'keyevent', *(['KEYCODE_DEL'] * (len(node.get('text', '')) + 1)))
        run('shell', 'input', 'text', args.text.replace(' ', '%s'))
elif args.action == 'swipe':
    # Coordinates must be taken from the latest dump/screenshot.
    run('shell', 'input', 'swipe', *args.value.split(','), '350')
elif args.action == 'screenshot':
    name = Path(args.value).name
    if not name.endswith('.png'):
        raise SystemExit('Use a .png filename')
    (out / name).write_bytes(subprocess.check_output([adb, '-s', 'emulator-5554', 'exec-out', 'screencap', '-p'], timeout=30))
    if (out / 'current-ui.xml').exists():
        (out / (Path(name).stem + '-ui.xml')).write_bytes((out / 'current-ui.xml').read_bytes())
    print(out / name)
elif args.action == 'focus':
    for line in run('shell', 'dumpsys', 'activity', 'activities').splitlines():
        if 'mResumedActivity' in line or 'topResumedActivity' in line:
            print(line.strip())
