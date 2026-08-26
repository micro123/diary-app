#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { captureUiScreenshot } from './ui-screenshot.mjs';

export const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(scriptDirectory, '..');
export const defaultStatePath = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'current.json');
export const defaultTimeoutMs = 7000;

export class CdpClient {
    constructor(url, timeoutMs = defaultTimeoutMs) {
        this.url = url;
        this.timeoutMs = timeoutMs;
        this.nextId = 1;
        this.pending = new Map();
        this.socket = null;
    }

    async connect() {
        this.socket = new WebSocket(this.url);
        this.socket.addEventListener('message', async event => {
            const raw = typeof event.data === 'string' ? event.data : await event.data.text();
            const message = JSON.parse(raw);
            if (!message.id || !this.pending.has(message.id))
                return;
            const pending = this.pending.get(message.id);
            this.pending.delete(message.id);
            clearTimeout(pending.timer);
            if (message.error)
                pending.reject(new Error(message.error.message || JSON.stringify(message.error)));
            else
                pending.resolve(message.result);
        });
        await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error('CDP WebSocket 连接超时')), this.timeoutMs);
            this.socket.addEventListener('open', () => {
                clearTimeout(timer);
                resolve();
            }, { once: true });
            this.socket.addEventListener('error', () => {
                clearTimeout(timer);
                reject(new Error('CDP WebSocket 连接失败'));
            }, { once: true });
        });
    }

    send(method, params = {}, timeoutMs = this.timeoutMs) {
        if (!this.socket)
            throw new Error('CDP WebSocket 尚未连接');
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(method + ' 超时'));
            }, timeoutMs);
            this.pending.set(id, { resolve, reject, timer });
            this.socket.send(JSON.stringify({ id, method, params }));
        });
    }

    close() {
        this.socket?.close();
    }
}

function attributes(values = []) {
    const result = {};
    for (let index = 0; index + 1 < values.length; index += 2)
        result[values[index]] = values[index + 1];
    return result;
}

export function buildTree(documentResult) {
    const entries = [];
    const byId = new Map();
    const visit = (node, parentId = null) => {
        const entry = { ...node, a: attributes(node.attributes), parentId };
        entries.push(entry);
        byId.set(entry.nodeId, entry);
        for (const child of node.children || [])
            visit(child, entry.nodeId);
    };
    visit(documentResult.root);
    for (const entry of entries) {
        const parent = byId.get(entry.parentId);
        if (parent?.a?.IsVisible === 'false')
            entry.a.IsVisible = 'false';
        if (parent?.a?.IsEnabled === 'false')
            entry.a.IsEnabled = 'false';
    }
    return { root: documentResult.root, entries, byId };
}

export const textOf = entry => entry?.a?.text ?? entry?.a?.Text ?? '';
export const typeOf = entry => entry?.a?.type ?? entry?.a?.Type ?? '';
export const nameOf = entry => entry?.a?.name ?? entry?.a?.Name ?? '';
export const isVisible = entry => entry?.a?.IsVisible !== 'false';
export const isEnabled = entry => entry?.a?.IsEnabled !== 'false';
export const isChecked = entry => (entry?.a?.IsChecked ?? '').toLowerCase() === 'true';

export function findByName(tree, name, predicate = () => true) {
    return tree.entries.find(entry => nameOf(entry) === name && isVisible(entry) && predicate(entry));
}

export function findAllByText(tree, text, predicate = () => true) {
    return tree.entries.filter(entry => textOf(entry) === text && isVisible(entry) && predicate(entry));
}

export function findByText(tree, text, predicate = () => true) {
    return findAllByText(tree, text, predicate)[0];
}

export function findByTextContains(tree, text, predicate = () => true) {
    return tree.entries.find(entry => textOf(entry).includes(text) && isVisible(entry) && predicate(entry));
}

export function ancestor(tree, entry, predicate) {
    let current = entry;
    while (current) {
        if (predicate(current))
            return current;
        current = tree.byId.get(current.parentId);
    }
    return null;
}

export function descendants(tree, entry, predicate = () => true) {
    if (!entry)
        return [];
    return tree.entries.filter(candidate => candidate.nodeId !== entry.nodeId
        && ancestor(tree, candidate, current => current.nodeId === entry.nodeId)
        && predicate(candidate));
}

export function hasAncestorType(tree, entry, typeName) {
    return Boolean(ancestor(tree, entry, current => typeOf(current).includes(typeName)));
}

export function hasAncestorName(tree, entry, name) {
    return Boolean(ancestor(tree, entry, current => nameOf(current) === name));
}

export function controlForText(tree, entry, typeNames = ['Button', 'MenuItem', 'TabItem', 'CheckBox', 'ToggleSwitch', 'SelectionListItem']) {
    return ancestor(tree, entry, current => typeNames.some(typeName => typeOf(current).includes(typeName)));
}

export function textWithinNamedControl(tree, name) {
    const control = findByName(tree, name);
    if (!control)
        return '';
    return [control, ...descendants(tree, control)].map(textOf).filter(Boolean).join(' ');
}

export function localDateText(date = new Date()) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return year + '-' + month + '-' + day;
}

export const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

export async function connectUiTest(options = {}) {
    const stateIndex = process.argv.indexOf('--state');
    const statePath = options.statePath
        ?? (stateIndex >= 0 ? path.resolve(process.argv[stateIndex + 1]) : defaultStatePath);
    const state = JSON.parse(await fs.readFile(statePath, 'utf8'));
    const targets = await fetch('http://127.0.0.1:' + state.port + '/json').then(response => {
        if (!response.ok)
            throw new Error('读取 CDP target 失败：HTTP ' + response.status);
        return response.json();
    });
    if (!targets.length)
        throw new Error('没有可用的 CDP target');
    const target = options.targetTitle
        ? targets.find(item => item.title === options.targetTitle)
        : options.targetTitleIncludes
            ? targets.find(item => item.title.includes(options.targetTitleIncludes))
            : targets.find(item => item.title === 'Diary Tools NG') ?? targets[0];
    if (!target)
        throw new Error('找不到匹配的 CDP target');

    const client = new CdpClient(target.webSocketDebuggerUrl, options.timeoutMs ?? defaultTimeoutMs);
    await client.connect();
    const getTree = async () => buildTree(await client.send('DOM.getDocument', { depth: -1, pierce: true }, 12000));
    const waitForTree = async (predicate, timeoutMs = options.timeoutMs ?? defaultTimeoutMs, message = '等待 UI 状态超时') => {
        const started = performance.now();
        while (performance.now() - started < timeoutMs) {
            const tree = await getTree();
            const value = predicate(tree);
            if (value)
                return { elapsedMs: performance.now() - started, tree, value };
            await delay(20);
        }
        throw new Error(message);
    };
    const boxCenter = async entry => {
        if (!entry)
            throw new Error('不能点击空 UI 节点');
        const box = await client.send('DOM.getBoxModel', { nodeId: entry.nodeId });
        const quad = box.model.content?.length >= 8 ? box.model.content : box.model.border;
        return {
            x: (quad[0] + quad[2] + quad[4] + quad[6]) / 4,
            y: (quad[1] + quad[3] + quad[5] + quad[7]) / 4,
        };
    };
    const clickNode = async entry => {
        const { x, y } = await boxCenter(entry);
        await client.send('DOM.focus', { nodeId: entry.nodeId });
        await client.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        await client.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
        await client.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
    };
    const pressKey = async (key, code, virtualKeyCode, modifiers = 0) => {
        const parameters = { key, code, windowsVirtualKeyCode: virtualKeyCode, modifiers };
        await client.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...parameters });
        if (key.length === 1 && (modifiers & 7) === 0)
            await client.send('Input.dispatchKeyEvent', { type: 'char', ...parameters, text: key });
        await client.send('Input.dispatchKeyEvent', { type: 'keyUp', ...parameters });
    };
    const focusNode = async entry => {
        await clickNode(entry);
        await client.send('DOM.focus', { nodeId: entry.nodeId });
    };
    const replaceText = async (entry, text) => {
        await focusNode(entry);
        await pressKey('a', 'KeyA', 65, 2);
        await pressKey('Backspace', 'Backspace', 8);
        if (text)
            await client.send('Input.insertText', { text });
    };
    const appendText = async (entry, text) => {
        await focusNode(entry);
        await client.send('Input.insertText', { text });
    };
    const clickByName = async name => {
        const tree = await getTree();
        const entry = findByName(tree, name);
        if (!entry)
            throw new Error('找不到控件：' + name);
        await clickNode(entry);
        return entry;
    };
    const clickByText = async (text, options = {}) => {
        const tree = await getTree();
        const textEntry = options.contains
            ? findByTextContains(tree, text, options.predicate)
            : findByText(tree, text, options.predicate);
        if (!textEntry)
            throw new Error('找不到文字：' + text);
        const target = options.direct ? textEntry : controlForText(tree, textEntry, options.typeNames);
        if (!target)
            throw new Error('找不到文字对应控件：' + text);
        await clickNode(target);
        return target;
    };
    const navigate = async (label, viewType) => {
        const tree = await getTree();
        const textEntry = findByText(tree, label);
        if (!textEntry)
            throw new Error('找不到导航项：' + label);
        const item = ancestor(tree, textEntry, entry => typeOf(entry).includes('SelectionListItem'));
        if (!item)
            throw new Error('找不到导航容器：' + label);
        const started = performance.now();
        await clickNode(item);
        await waitForTree(current => current.entries.find(entry => isVisible(entry)
            && typeOf(entry).includes(viewType)), 12000, '导航页面未出现：' + label);
        return performance.now() - started;
    };
    const openSettingsMenuItem = async name => {
        let lastError;
        for (let attempt = 0; attempt < 3; attempt++) {
            const tree = await getTree();
            const button = findByName(tree, 'SettingsMenuButton');
            if (!button)
                throw new Error('找不到 SettingsMenuButton');
            if (attempt === 0)
                await clickNode(button);
            else {
                await client.send('DOM.focus', { nodeId: button.nodeId });
                await pressKey('Enter', 'Enter', 13);
            }
            try {
                const result = await waitForTree(current => findByName(current, name), 1800,
                    '设置菜单项未出现：' + name);
                await clickNode(result.value);
                return result.elapsedMs;
            }
            catch (error) {
                lastError = error;
                await pressKey('Escape', 'Escape', 27);
                await delay(80);
            }
        }
        throw lastError;
    };
    const screenshot = fileName => captureUiScreenshot({
        client,
        repositoryRoot,
        fileName,
        processId: state.processId,
    });
    return {
        statePath, state, target, client, getTree, waitForTree, clickNode, clickByName, clickByText,
        focusNode, replaceText, appendText, pressKey, navigate, openSettingsMenuItem, screenshot,
        close: () => client.close(),
    };
}

export async function writeSuiteReport(suite, report) {
    const reportDirectory = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'reports');
    await fs.mkdir(reportDirectory, { recursive: true });
    const stamp = new Date().toISOString().replaceAll(':', '-').replaceAll('.', '-');
    const reportPath = path.join(reportDirectory, suite + '-' + stamp + '.json');
    const payload = { suite, ...report, reportPath };
    await fs.writeFile(reportPath, JSON.stringify(payload, null, 2), 'utf8');
    return payload;
}
