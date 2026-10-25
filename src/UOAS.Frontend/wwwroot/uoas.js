window.uoas = {
    measure(element) { const r = element.getBoundingClientRect(); return [element.clientWidth, element.clientHeight, r.left + element.clientLeft, r.top + element.clientTop]; },
    capture(element, id) { element.setPointerCapture(id); },
    release(element, id) { if (element.hasPointerCapture(id)) element.releasePointerCapture(id); },
    download(name, base64, type = 'image/png') {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], {type}));
        const link = document.createElement('a'); link.href = url; link.download = name; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
