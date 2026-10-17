// SkiaSharp renders a stable-anchor atlas once; the browser clock replays it locally.
const players = new WeakMap();

export async function attach(root, atlas) {
    dispose(root);
    const controller = new AbortController();
    const state = { controller, request: 0, playing: true, frame: 0, previous: null, remainder: 0, delay: 150 };
    players.set(root, state);
    const image = new Image();
    image.src = atlas.source;
    try { await image.decode(); }
    catch (error) { if (players.get(root) === state) dispose(root); throw error; }
    if (players.get(root) !== state || !root.isConnected) { controller.abort(); return; }
    const canvas = root.querySelector("canvas");
    const context = canvas.getContext("2d");
    if (!context) { dispose(root); throw new Error("Canvas 2D unavailable."); }
    canvas.width = atlas.frameWidth;
    canvas.height = atlas.frameHeight;
    const control = name => root.querySelector('[data-action="' + name + '"]');
    state.delay = Math.max(25, Math.min(2000, Number(control("delay").value) || 150));
    const label = root.querySelector("[data-frame-label]");
    const listen = (target, event, action) => target.addEventListener(event, action, { signal: controller.signal });
    const draw = () => {
        context.clearRect(0, 0, canvas.width, canvas.height);
        context.imageSmoothingEnabled = false;
        context.drawImage(image, state.frame % atlas.columns * atlas.frameWidth,
            Math.floor(state.frame / atlas.columns) * atlas.frameHeight,
            atlas.frameWidth, atlas.frameHeight, 0, 0, canvas.width, canvas.height);
        root.dataset.frame = String(state.frame);
        control("seek").value = state.frame;
        label.textContent = "Frame " + (state.frame + 1) + " / " + atlas.frameCount;
        control("play").textContent = state.playing ? "Pause" : "Play";
    };
    const schedule = () => {
        if (state.playing && !document.hidden && !state.request) state.request = requestAnimationFrame(tick);
    };
    const tick = time => {
        state.request = 0;
        if (!root.isConnected) { dispose(root); return; }
        if (!state.playing || document.hidden) return;
        if (state.previous !== null) state.remainder += Math.max(0, time - state.previous);
        state.previous = time;
        const steps = Math.floor(state.remainder / state.delay);
        if (steps > 0) {
            state.remainder %= state.delay;
            if (control("loop").checked) state.frame = (state.frame + steps) % atlas.frameCount;
            else {
                state.frame = Math.min(atlas.frameCount - 1, state.frame + steps);
                if (state.frame === atlas.frameCount - 1) state.playing = false;
            }
            draw();
        }
        schedule();
    };
    const pause = () => {
        state.playing = false; state.previous = null; state.remainder = 0;
        cancelAnimationFrame(state.request); state.request = 0;
    };
    listen(control("play"), "click", () => {
        if (state.playing) pause();
        else { state.playing = true; state.previous = null; schedule(); }
        draw();
    });
    listen(control("reset"), "click", () => { pause(); state.frame = 0; draw(); });
    for (const [name, offset] of [["previous", -1], ["next", 1]])
        listen(control(name), "click", () => {
            pause(); state.frame = (state.frame + offset + atlas.frameCount) % atlas.frameCount; draw();
        });
    listen(control("seek"), "input", event => {
        pause(); state.frame = Math.max(0, Math.min(atlas.frameCount - 1, Number(event.target.value))); draw();
    });
    listen(control("delay"), "change", event => {
        state.delay = Math.max(25, Math.min(2000, Number(event.target.value) || 150));
        event.target.value = state.delay; state.remainder = 0; state.previous = null;
    });
    const zoom = () => {
        const factor = Number(control("zoom").value);
        canvas.style.width = (atlas.frameWidth * factor) + "px";
        canvas.style.height = (atlas.frameHeight * factor) + "px";
    };
    listen(control("zoom"), "change", zoom);
    listen(document, "visibilitychange", () => {
        state.previous = null;
        if (document.hidden) { cancelAnimationFrame(state.request); state.request = 0; }
        else schedule();
    });
    root.dataset.state = "ready";
    zoom(); draw(); schedule();
}

export function dispose(root) {
    const state = players.get(root);
    if (!state) return;
    cancelAnimationFrame(state.request);
    state.controller.abort();
    players.delete(root);
}
