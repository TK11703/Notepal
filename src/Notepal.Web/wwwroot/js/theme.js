const storageKey = 'notepal-theme';
const media = window.matchMedia('(prefers-color-scheme: dark)');
let subscribers = 0;

export function get() {
    try {
        const value = localStorage.getItem(storageKey);
        return value === 'light' || value === 'dark' ? value : 'auto';
    } catch {
        console.warn('Theme storage is unavailable; using the system preference.');
        return 'auto';
    }
}

function apply(mode) {
    const resolved = mode === 'auto' ? (media.matches ? 'dark' : 'light') : mode;
    document.documentElement.setAttribute('data-bs-theme', resolved);
}

export function set(mode) {
    try {
        if (mode === 'auto') {
            localStorage.removeItem(storageKey);
        } else {
            localStorage.setItem(storageKey, mode);
        }
    } catch {
        console.warn('Theme preference could not be saved; it will apply only to this page.');
    }
    apply(mode);
    return mode;
}

function systemThemeChanged() {
    if (get() === 'auto') apply('auto');
}

export function initialize() {
    if (subscribers++ === 0) media.addEventListener('change', systemThemeChanged);
    const mode = get();
    apply(mode);
    return mode;
}

export function dispose() {
    if (subscribers > 0 && --subscribers === 0) media.removeEventListener('change', systemThemeChanged);
}
