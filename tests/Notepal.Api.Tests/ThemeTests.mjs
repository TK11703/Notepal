import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import test from 'node:test';
import vm from 'node:vm';

const scripts = resolve(import.meta.dirname, '..', '..', 'src', 'Notepal.Web', 'wwwroot', 'js');

function browser({ dark = false, stored = null, unavailable = false } = {}) {
    const storage = new Map(stored === null ? [] : [['notepal-theme', stored]]);
    const attributes = new Map();
    const listeners = new Set();
    const warnings = [];
    const media = {
        matches: dark,
        addEventListener: (_, listener) => listeners.add(listener),
        removeEventListener: (_, listener) => listeners.delete(listener),
    };
    const context = vm.createContext({
        window: { matchMedia: () => media },
        document: { documentElement: { setAttribute: (key, value) => attributes.set(key, value) } },
        localStorage: {
            getItem(key) {
                if (unavailable) throw new Error('Storage disabled');
                return storage.get(key) ?? null;
            },
            setItem(key, value) {
                if (unavailable) throw new Error('Storage disabled');
                storage.set(key, value);
            },
            removeItem(key) {
                if (unavailable) throw new Error('Storage disabled');
                storage.delete(key);
            },
        },
        console: { warn: message => warnings.push(message) },
    });
    return { context, storage, attributes, listeners, warnings, media };
}

async function load(name, context) {
    const module = new vm.SourceTextModule(await readFile(resolve(scripts, name), 'utf8'), { context });
    await module.link(() => { throw new Error('Unexpected module dependency'); });
    await module.evaluate();
    return module.namespace;
}

test('synchronous initializer preserves stored/system themes without globals or listeners', async () => {
    const source = await readFile(resolve(scripts, 'theme-init.js'), 'utf8');
    for (const [stored, dark, expected] of [
        ['dark', false, 'dark'],
        ['light', true, 'light'],
        [null, true, 'dark'],
        [null, false, 'light'],
        ['invalid', true, 'dark'],
    ]) {
        const state = browser({ stored, dark });
        vm.runInContext(source, state.context);
        assert.equal(state.attributes.get('data-bs-theme'), expected);
        assert.equal(state.listeners.size, 0);
        assert.deepEqual(Object.keys(state.context.window), ['matchMedia']);
    }
});

test('theme module persists choices, follows system in auto mode, and disposes listeners', async () => {
    const state = browser({ dark: true });
    const theme = await load('theme.js', state.context);
    assert.equal(theme.initialize(), 'auto');
    theme.initialize();
    assert.equal(state.listeners.size, 1);
    assert.equal(state.attributes.get('data-bs-theme'), 'dark');

    assert.equal(theme.set('light'), 'light');
    assert.equal(state.storage.get('notepal-theme'), 'light');
    state.media.matches = true;
    for (const listener of state.listeners) listener();
    assert.equal(state.attributes.get('data-bs-theme'), 'light');

    assert.equal(theme.set('dark'), 'dark');
    assert.equal(state.attributes.get('data-bs-theme'), 'dark');
    assert.equal(theme.set('auto'), 'auto');
    assert.equal(state.storage.has('notepal-theme'), false);
    state.media.matches = false;
    for (const listener of state.listeners) listener();
    assert.equal(state.attributes.get('data-bs-theme'), 'light');

    theme.dispose();
    assert.equal(state.listeners.size, 1);
    theme.dispose();
    assert.equal(state.listeners.size, 0);
    theme.dispose();
    theme.initialize();
    assert.equal(state.listeners.size, 1);
    theme.dispose();
    assert.deepEqual(Object.keys(state.context.window), ['matchMedia']);
});

test('preference module retains existing browser storage keys without globals', async () => {
    const state = browser();
    const preferences = await load('preferences.js', state.context);
    assert.equal(preferences.get('note-view'), null);
    preferences.set('note-view', 'Notes');
    assert.equal(state.storage.get('notepal-note-view'), 'Notes');
    assert.equal(preferences.get('note-view'), 'Notes');
    assert.deepEqual(Object.keys(state.context.window), ['matchMedia']);
});

test('disabled storage gives explicit warnings and still applies the selected theme', async () => {
    const state = browser({ dark: true, unavailable: true });
    vm.runInContext(await readFile(resolve(scripts, 'theme-init.js'), 'utf8'), state.context);
    assert.equal(state.attributes.get('data-bs-theme'), 'dark');
    const theme = await load('theme.js', state.context);
    assert.equal(theme.initialize(), 'auto');
    assert.equal(theme.set('light'), 'light');
    assert.equal(state.attributes.get('data-bs-theme'), 'light');
    const preferences = await load('preferences.js', state.context);
    assert.equal(preferences.get('note-view'), null);
    preferences.set('note-view', 'Original');
    assert.ok(state.warnings.length >= 5);
    theme.dispose();
    assert.equal(state.listeners.size, 0);
});
