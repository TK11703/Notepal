// Loaded synchronously in <head> so the correct colour scheme is applied before first paint.
(function () {
    const storageKey = 'notepal-theme';
    const media = window.matchMedia('(prefers-color-scheme: dark)');

    function stored() {
        try {
            const value = localStorage.getItem(storageKey);
            return value === 'light' || value === 'dark' ? value : 'auto';
        } catch {
            return 'auto';
        }
    }

    function apply(mode) {
        const resolved = mode === 'auto' ? (media.matches ? 'dark' : 'light') : mode;
        document.documentElement.setAttribute('data-bs-theme', resolved);
    }

    window.notepalTheme = {
        get: stored,
        set: function (mode) {
            try {
                if (mode === 'auto') {
                    localStorage.removeItem(storageKey);
                } else {
                    localStorage.setItem(storageKey, mode);
                }
            } catch { /* storage unavailable: theme still applies for this page */ }
            apply(mode);
            return stored();
        }
    };

    // Small UI preferences (e.g. the preferred note view) remembered per browser.
    window.notepalPrefs = {
        get: function (key) {
            try { return localStorage.getItem('notepal-' + key); } catch { return null; }
        },
        set: function (key, value) {
            try { localStorage.setItem('notepal-' + key, value); } catch { /* storage unavailable */ }
        }
    };

    media.addEventListener('change', function () { if (stored() === 'auto') apply('auto'); });
    apply(stored());
})();
