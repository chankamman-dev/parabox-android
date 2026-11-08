// Generates a script for Android's `uinput` tool: registers a multi-touch screen (1920x1080, direct
// input, like a real panel) and plays finger steps on it. `adb input tap` is no good for testing touch
// controls: its events come from the virtual device -1, which the Input System does not see as a
// touch screen.
//   node mktouch.js "<steps>" > out.json      then: adb push out.json /data/local/tmp/ && adb shell uinput /data/local/tmp/out.json
// steps (space separated, coordinates in screen pixels):
//   t:x,y            tap (120 ms)
//   h:x,y,ms         press and hold
//   s:x1,y1,x2,y2,ms drag from one point to another over ms
//   m:x1,y1,x2,y2,ms two fingers held together for ms (multi-touch)
//   w:ms             wait
const steps = (process.argv[2] || "").split(/\s+/).filter(Boolean);
const out = [];
const cmd = o => out.push(JSON.stringify(Object.assign({ id: 2 }, o)));
const abs = (code, max) => ({ code, info: { value: 0, minimum: 0, maximum: max, fuzz: 0, flat: 0, resolution: 0 } });
cmd({
    command: "register", name: "Parabox Test Touchscreen", vid: 0x18d1, pid: 0x4ee7, bus: "usb",
    configuration: [
        { type: 100, data: [1, 3] },            // EV_KEY, EV_ABS
        { type: 101, data: [330] },             // BTN_TOUCH
        { type: 103, data: [0, 1, 47, 53, 54, 57] }, // ABS_X, ABS_Y, MT_SLOT, MT_POSITION_X/Y, MT_TRACKING_ID
        { type: 110, data: [1] },               // INPUT_PROP_DIRECT
    ],
    abs_info: [abs(0, 1919), abs(1, 1079), abs(47, 9), abs(53, 1919), abs(54, 1079),
        { code: 57, info: { value: 0, minimum: 0, maximum: 65535, fuzz: 0, flat: 0, resolution: 0 } }],
});
cmd({ command: "delay", duration: 2500 });
let tid = 1;
const ev = e => cmd({ command: "inject", events: e.concat([0, 0, 0]) });
const wait = ms => cmd({ command: "delay", duration: Math.max(1, Math.round(ms)) });
const down = (slot, x, y) => [3, 47, slot, 3, 57, tid++, 3, 53, x, 3, 54, y, 3, 0, x, 3, 1, y];
const move = (slot, x, y) => [3, 47, slot, 3, 53, x, 3, 54, y];
const up = slot => [3, 47, slot, 3, 57, -1];
for (const s of steps) {
    const [k, a] = s.split(":");
    const n = (a || "").split(",").map(Number);
    if (k === "w") wait(n[0]);
    else if (k === "t" || k === "h") {
        ev(down(0, n[0], n[1]).concat([1, 330, 1]));
        wait(k === "t" ? 120 : n[2]);
        ev(up(0).concat([1, 330, 0]));
        wait(250);
    } else if (k === "s") {
        const [x1, y1, x2, y2, ms] = n, frames = Math.max(2, Math.round(ms / 16));
        ev(down(0, x1, y1).concat([1, 330, 1]));
        for (let i = 1; i <= frames; i++) {
            wait(ms / frames);
            ev(move(0, Math.round(x1 + (x2 - x1) * i / frames), Math.round(y1 + (y2 - y1) * i / frames)));
        }
        wait(100);
        ev(up(0).concat([1, 330, 0]));
        wait(250);
    } else if (k === "m") {
        ev(down(0, n[0], n[1]).concat(down(1, n[2], n[3])).concat([1, 330, 1]));
        wait(n[4]);
        ev(up(0).concat(up(1)).concat([1, 330, 0]));
        wait(250);
    }
}
wait(1500);
process.stdout.write(out.join("\n") + "\n");
