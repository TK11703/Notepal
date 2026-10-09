// Runs before first paint without exporting globals or installing event listeners.
(function () {
    let mode = 'auto';
    try {
        const stored = localStorage.getItem('notepal-theme');
        if (stored === 'light' || stored === 'dark') mode = stored;
    } catch {
        console.warn('Theme storage is unavailable; using the system preference.');
    }
    const dark = mode === 'dark' || (mode === 'auto' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
})();
