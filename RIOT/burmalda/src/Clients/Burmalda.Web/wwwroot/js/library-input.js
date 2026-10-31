// Browser input only. Scene simulation, selection and game rules live in C#.
export function attach(element) {
    const keys = new Set();
    const pressed = new Set();
    let dx = 0, dy = 0, click = false;
    let active = true;
    const recognized = ['KeyW','KeyA','KeyS','KeyD','KeyE','KeyQ','KeyF','Escape','ArrowLeft','ArrowRight','ArrowUp','ArrowDown'];
    const down = e => {
        if (document.pointerLockElement !== element) return;
        if (recognized.includes(e.code)) { if (!keys.has(e.code)) pressed.add(e.code); keys.add(e.code); e.preventDefault(); }
    };
    const up = e => keys.delete(e.code);
    const move = e => { if (document.pointerLockElement === element) { dx += e.movementX; dy += e.movementY; } };
    const press = () => { if (document.pointerLockElement === element) click = true; };
    const blur = () => { keys.clear(); pressed.clear(); dx = dy = 0; click = false; };
    const lockChanged = () => { if (document.pointerLockElement !== element) blur(); };
    window.addEventListener('keydown', down); window.addEventListener('keyup', up);
    window.addEventListener('mousemove', move); window.addEventListener('blur', blur);
    element.addEventListener('mousedown', press); document.addEventListener('pointerlockchange', lockChanged);
    return {
        lock() { if (active) element.requestPointerLock(); },
        unlock() { if (document.pointerLockElement === element) document.exitPointerLock(); blur(); },
        read() {
            const result = { keys: [...keys], pressed: [...pressed], dx, dy, click, locked: document.pointerLockElement === element };
            dx = dy = 0; click = false; pressed.clear();
            return result;
        },
        dispose() {
            active = false;
            if (document.pointerLockElement === element) document.exitPointerLock();
            window.removeEventListener('keydown', down); window.removeEventListener('keyup', up);
            window.removeEventListener('mousemove', move); window.removeEventListener('blur', blur);
            element.removeEventListener('mousedown', press); document.removeEventListener('pointerlockchange', lockChanged); blur();
        }
    };
}
