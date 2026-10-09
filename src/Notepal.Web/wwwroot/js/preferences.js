export function get(key) {
    try {
        return localStorage.getItem('notepal-' + key);
    } catch {
        console.warn('Preference storage is unavailable; using the default.');
        return null;
    }
}

export function set(key, value) {
    try {
        localStorage.setItem('notepal-' + key, value);
    } catch {
        console.warn('Preference could not be saved; it will apply only to this page.');
    }
}
