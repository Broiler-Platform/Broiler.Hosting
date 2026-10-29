// Host protocol 1. Logical coordinates are CSS pixels; DPR only sizes the backing canvas.
export const hostProtocolVersion = 1;

export function createCanvasHost({ canvas, input, container, dispatch, render, consumedChords = [] }) {
    const cleanups = [], pointers = new Set(), keys = new Map();
    let disposed = false, composing = false, echo = null, echoTimer = 0, frame = 0;
    const on = (target, type, handler, options) => {
        target.addEventListener(type, handler, options);
        cleanups.push(() => target.removeEventListener(type, handler, options));
    };
    const invalidate = () => {
        if (disposed || frame) return;
        frame = requestAnimationFrame(() => { frame = 0; if (!disposed) render(); });
    };
    const point = event => {
        const rect = canvas.getBoundingClientRect();
        return { x: (event.clientX - rect.left) * container.clientWidth / Math.max(1, rect.width),
            y: (event.clientY - rect.top) * container.clientHeight / Math.max(1, rect.height) };
    };
    const send = (kind, value) => { if (!disposed) { dispatch[kind]?.(value); invalidate(); } };
    const cancelPointer = id => {
        if (!pointers.delete(id)) return;
        send('pointerCancel', { id });
        if (canvas.hasPointerCapture?.(id)) canvas.releasePointerCapture(id);
    };
    on(canvas, 'pointerdown', event => {
        pointers.add(event.pointerId); canvas.setPointerCapture?.(event.pointerId);
        canvas.focus({ preventScroll: true });
        send('pointer', { ...point(event), id: event.pointerId, kind: 'down', buttons: event.buttons, button: event.button, time: event.timeStamp });
    });
    for (const kind of ['move', 'up']) on(canvas, `pointer${kind}`, event => {
        send('pointer', { ...point(event), id: event.pointerId, kind, buttons: event.buttons, button: event.button, time: event.timeStamp });
        if (kind === 'up') { pointers.delete(event.pointerId); if (canvas.hasPointerCapture?.(event.pointerId)) canvas.releasePointerCapture(event.pointerId); }
    });
    for (const type of ['pointercancel', 'lostpointercapture']) on(canvas, type, event => cancelPointer(event.pointerId));
    on(canvas, 'wheel', event => {
        event.preventDefault();
        send('wheel', { ...point(event), deltaX: event.deltaX, deltaY: event.deltaY, deltaMode: event.deltaMode, time: event.timeStamp });
    }, { passive: false });
    const key = (event, down) => {
        if (composing || event.isComposing || event.key === 'Process') return;
        const letter = event.key.toLowerCase();
        const chord = (event.ctrlKey || event.metaKey) && !['c', 'x', 'v'].includes(letter) && consumedChords.includes(letter);
        if (chord || (event.key === ' ' && event.target === canvas) || ['Tab', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Backspace', 'Delete', 'Enter', 'Escape'].includes(event.key)) event.preventDefault();
        const value = { key: event.key, code: event.code, down, repeat: event.repeat,
            shift: event.shiftKey, ctrl: event.ctrlKey, alt: event.altKey, meta: event.metaKey, time: event.timeStamp };
        if (down) keys.set(event.code, value); else keys.delete(event.code);
        send('key', value);
    };
    for (const target of [canvas, input]) {
        on(target, 'keydown', event => key(event, true));
        on(target, 'keyup', event => key(event, false));
    }
    on(input, 'beforeinput', event => {
        if (composing || event.isComposing || event.inputType.includes('Composition')) return;
        if (event.inputType === 'insertText' && event.data) {
            event.preventDefault();
            if (echo !== null && event.data === echo) echo = null;
            else send('text', { text: event.data, time: event.timeStamp });
        } else if (event.inputType.startsWith('delete')) event.preventDefault();
    });
    on(input, 'input', event => {
        if (composing || event.isComposing) return;
        if (echo !== null && event.data === echo) echo = null;
        else if (event.data && !event.inputType.includes('Composition')) send('text', { text: event.data, time: event.timeStamp });
        input.value = '';
    });
    const composition = (phase, event) => send('composition', { phase, text: event.data || '', start: input.selectionStart || 0, time: event.timeStamp });
    on(input, 'compositionstart', event => { composing = true; echo = null; composition('start', event); });
    on(input, 'compositionupdate', event => composition('update', event));
    on(input, 'compositionend', event => {
        composing = false; echo = event.data || ''; composition('commit', event); input.value = '';
        clearTimeout(echoTimer); echoTimer = setTimeout(() => { echo = null; }, 0);
    });
    const cancelComposition = event => { if (composing) { composing = false; composition('cancel', event); input.value = ''; } };
    on(input, 'blur', cancelComposition);
    for (const kind of ['copy', 'cut', 'paste']) on(input, kind, event => {
        if (!event.isTrusted || !event.clipboardData) return;
        const result = dispatch.clipboard?.({ kind, text: kind === 'paste' ? event.clipboardData.getData('text/plain') : null });
        if (!result?.handled) return;
        if (kind !== 'paste') event.clipboardData.setData('text/plain', result.text ?? '');
        event.preventDefault(); input.value = ''; invalidate();
    });
    const releaseInput = () => {
        for (const id of [...pointers]) cancelPointer(id);
        for (const value of keys.values()) send('key', { ...value, down: false, repeat: false });
        keys.clear();
        cancelComposition({ data: '', timeStamp: performance.now() });
    };
    on(window, 'blur', releaseInput);
    const resize = () => { send('resize', { width: Math.max(1, container.clientWidth), height: Math.max(1, container.clientHeight), dpr: window.devicePixelRatio || 1 }); };
    const observer = new ResizeObserver(resize); observer.observe(container);
    on(window, 'resize', resize);
    let dprQuery;
    const dprChanged = () => {
        dprQuery?.removeEventListener('change', dprChanged);
        dprQuery = matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
        dprQuery.addEventListener('change', dprChanged); resize();
    };
    dprChanged();
    return {
        invalidate,
        publishCaret({ active, x = 0, y = 0, width = 1, height = 8 }) {
            if (disposed) return;
            if (!active) { if (document.activeElement === input) canvas.focus({ preventScroll: true }); return; }
            Object.assign(input.style, { left: `${Math.max(0, x)}px`, top: `${Math.max(0, y)}px`, width: `${Math.max(1, width)}px`, height: `${Math.max(8, height)}px` });
            if (document.activeElement === canvas) input.focus({ preventScroll: true });
        },
        dispose() {
            if (disposed) return;
            releaseInput();
            disposed = true; cancelAnimationFrame(frame); clearTimeout(echoTimer);
            observer.disconnect(); dprQuery?.removeEventListener('change', dprChanged);
            for (const cleanup of cleanups) cleanup();
            keys.clear(); input.value = '';
        },
    };
}

export function createDocumentPort() {
    let busy = false;
    const name = value => {
        const leaf = value.replaceAll('\\', '/').split('/').at(-1);
        if (!leaf || leaf === '.' || leaf === '..' || /[\x00-\x1f\x7f]/.test(leaf)) throw new Error('Invalid document filename.');
        return leaf;
    };
    return {
        async open({ accept = '', maximumBytes = 16 * 1024 * 1024 } = {}, signal) {
            if (signal?.aborted) return { status: 'Cancelled' };
            if (busy) return { status: 'Failed', error: 'A file picker is already open.' };
            if (!Number.isSafeInteger(maximumBytes) || maximumBytes <= 0) return { status: 'Failed', error: 'Invalid document size limit.' };
            busy = true;
            return new Promise(resolve => {
                const picker = document.createElement('input'); picker.type = 'file'; picker.accept = accept; picker.hidden = true;
                let finished = false;
                const finish = result => {
                    if (finished) return; finished = true; busy = false;
                    signal?.removeEventListener('abort', abort); picker.remove(); resolve(result);
                };
                const abort = () => finish({ status: 'Cancelled' });
                picker.addEventListener('cancel', abort);
                signal?.addEventListener('abort', abort, { once: true });
                picker.addEventListener('change', async () => {
                    try {
                        const file = picker.files?.[0];
                        if (!file) { abort(); return; }
                        if (file.size > maximumBytes) throw new Error('The selected document exceeds the size limit.');
                        const bytes = new Uint8Array(await file.arrayBuffer());
                        if (signal?.aborted) { abort(); return; }
                        if (bytes.length > maximumBytes) throw new Error('The selected document exceeds the size limit.');
                        finish({ status: 'Opened', document: { name: name(file.name), contentType: file.type || 'application/octet-stream', bytes } });
                    } catch (error) { finish({ status: 'Failed', error: String(error) }); }
                });
                document.body.append(picker);
                try { picker.click(); } catch (error) { finish({ status: 'Failed', error: String(error) }); }
            });
        },
        async export(document, signal) {
            await new Promise(resolve => setTimeout(resolve, 0));
            if (signal?.aborted) return { status: 'Cancelled' };
            let url;
            try {
                const filename = name(document.name);
                const blob = new Blob([document.bytes], { type: document.contentType || 'application/octet-stream' });
                url = URL.createObjectURL(blob);
                const link = window.document.createElement('a'); link.download = filename; link.href = url;
                window.document.body.append(link);
                try { link.click(); } finally { link.remove(); }
                // Let the browser consume the URL; this confirms initiation, never durable storage.
                setTimeout(() => URL.revokeObjectURL(url), 60000);
                return { status: 'DownloadInitiated' };
            } catch (error) { if (url) URL.revokeObjectURL(url); return { status: 'Failed', error: String(error) }; }
        },
    };
}
