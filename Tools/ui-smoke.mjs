#!/usr/bin/env node

import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import zlib from 'node:zlib';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '..');
const stateArgumentIndex = process.argv.indexOf('--state');
const statePath = stateArgumentIndex >= 0
    ? path.resolve(process.argv[stateArgumentIndex + 1])
    : path.join(repositoryRoot, '.build-tmp', 'ui-test', 'current.json');
const timeoutMs = 5000;

function paethPredictor(left, up, upperLeft) {
    const prediction = left + up - upperLeft;
    const leftDistance = Math.abs(prediction - left);
    const upDistance = Math.abs(prediction - up);
    const upperLeftDistance = Math.abs(prediction - upperLeft);
    if (leftDistance <= upDistance && leftDistance <= upperLeftDistance)
        return left;
    return upDistance <= upperLeftDistance ? up : upperLeft;
}

function pngContentLuminance(buffer) {
    const signature = '89504e470d0a1a0a';
    if (buffer.subarray(0, 8).toString('hex') !== signature)
        throw new Error('截图不是有效的 PNG');

    let width = 0;
    let height = 0;
    let bitDepth = 0;
    let colorType = 0;
    const imageData = [];
    for (let offset = 8; offset < buffer.length;) {
        const length = buffer.readUInt32BE(offset);
        const type = buffer.subarray(offset + 4, offset + 8).toString('ascii');
        const data = buffer.subarray(offset + 8, offset + 8 + length);
        if (type === 'IHDR') {
            width = data.readUInt32BE(0);
            height = data.readUInt32BE(4);
            bitDepth = data[8];
            colorType = data[9];
        } else if (type === 'IDAT') {
            imageData.push(data);
        } else if (type === 'IEND') {
            break;
        }
        offset += length + 12;
    }

    const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
    if (!width || !height || bitDepth !== 8 || channels === 0)
        throw new Error(`不支持的 PNG 格式：${width}x${height}, bitDepth=${bitDepth}, colorType=${colorType}`);

    const pixels = zlib.inflateSync(Buffer.concat(imageData));
    const stride = width * channels;
    let sourceOffset = 0;
    let previous = new Uint8Array(stride);
    let current = new Uint8Array(stride);
    const minX = Math.floor(width * 0.15);
    const maxX = Math.ceil(width * 0.85);
    const minY = Math.floor(height * 0.15);
    const maxY = Math.ceil(height * 0.85);
    let luminance = 0;
    let samples = 0;

    for (let y = 0; y < height; y++) {
        const filter = pixels[sourceOffset++];
        current.fill(0);
        for (let index = 0; index < stride; index++) {
            const raw = pixels[sourceOffset++];
            const left = index >= channels ? current[index - channels] : 0;
            const up = previous[index];
            const upperLeft = index >= channels ? previous[index - channels] : 0;
            const value = filter === 0 ? raw
                : filter === 1 ? raw + left
                    : filter === 2 ? raw + up
                        : filter === 3 ? raw + Math.floor((left + up) / 2)
                            : filter === 4 ? raw + paethPredictor(left, up, upperLeft)
                                : Number.NaN;
            if (Number.isNaN(value))
                throw new Error('不支持的 PNG 行过滤器：' + filter);
            current[index] = value & 0xff;
        }

        if (y >= minY && y < maxY && (y - minY) % 8 === 0) {
            for (let x = minX; x < maxX; x += 8) {
                const index = x * channels;
                if (channels === 4 && current[index + 3] < 128)
                    continue;
                luminance += current[index] * 0.2126 + current[index + 1] * 0.7152 + current[index + 2] * 0.0722;
                samples++;
            }
        }

        [previous, current] = [current, previous];
    }

    if (samples === 0)
        throw new Error('截图主内容区域没有可用像素');
    return luminance / samples;
}

class CdpClient {
    constructor(url) {
        this.url = url;
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
            const timer = setTimeout(() => reject(new Error('CDP WebSocket 连接超时')), timeoutMs);
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

    send(method, params = {}, commandTimeoutMs = timeoutMs) {
        if (!this.socket)
            throw new Error('CDP WebSocket 尚未连接');
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pending.delete(id);
                reject(new Error(method + ' 超时'));
            }, commandTimeoutMs);
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

function buildTree(documentResult) {
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
    return { root: documentResult.root, entries, byId };
}

function textOf(entry) {
    return entry?.a?.text ?? entry?.a?.Text ?? '';
}

function typeOf(entry) {
    return entry?.a?.type ?? entry?.a?.Type ?? '';
}

function nameOf(entry) {
    return entry?.a?.name ?? entry?.a?.Name ?? '';
}

function isVisible(entry) {
    return entry?.a?.IsVisible !== 'false';
}

function findByName(tree, name) {
    return tree.entries.find(entry => nameOf(entry) === name && isVisible(entry));
}

function findByText(tree, text, predicate = () => true) {
    return tree.entries.find(entry => textOf(entry) === text && isVisible(entry) && predicate(entry));
}

function ancestor(tree, entry, predicate) {
    let current = entry;
    while (current) {
        if (predicate(current))
            return current;
        current = tree.byId.get(current.parentId);
    }
    return null;
}

function hasAncestorType(tree, entry, typeName) {
    return Boolean(ancestor(tree, entry, current => typeOf(current).includes(typeName)));
}

function hasAncestorName(tree, entry, name) {
    return Boolean(ancestor(tree, entry, current => nameOf(current) === name));
}

function textWithinNamedControl(tree, name) {
    const control = findByName(tree, name);
    if (!control)
        return '';
    const values = tree.entries
        .filter(entry => entry.nodeId === control.nodeId
            || ancestor(tree, entry, current => current.nodeId === control.nodeId))
        .map(textOf)
        .filter(Boolean);
    return values.join(' ');
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(sorted.length * ratio) - 1))];
}

function summarize(values) {
    return {
        count: values.length,
        minMs: Math.min(...values),
        p50Ms: percentile(values, 0.5),
        p95Ms: percentile(values, 0.95),
        maxMs: Math.max(...values),
        averageMs: values.reduce((sum, value) => sum + value, 0) / values.length,
    };
}

function localDateText(date = new Date()) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return year + '-' + month + '-' + day;
}

async function readPersistedTemplate(state, title, tagName) {
    const { DatabaseSync } = await import('node:sqlite');
    const database = new DatabaseSync(path.join(state.profile, 'data', 'db.sqlite3'), { readOnly: true });
    try {
        return database.prepare(`
            SELECT work.id, work.hours,
                   EXISTS (
                       SELECT 1
                       FROM work_item_tags link
                       JOIN work_tags tag ON tag.id = link.tag_id
                       WHERE link.work_id = work.id AND tag.tag_name = ?
                   ) AS hasTag
            FROM work_items work
            WHERE work.comment = ?
            ORDER BY work.id DESC
            LIMIT 1
        `).get(tagName, title);
    }
    finally {
        database.close();
    }
}

async function main() {
    const state = JSON.parse(await fs.readFile(statePath, 'utf8'));
    const targets = await fetch('http://127.0.0.1:' + state.port + '/json').then(response => {
        if (!response.ok)
            throw new Error('读取 CDP target 失败：HTTP ' + response.status);
        return response.json();
    });
    if (!targets.length)
        throw new Error('没有可用的 CDP target');

    const client = new CdpClient(targets[0].webSocketDebuggerUrl);
    await client.connect();
    const startedAt = new Date().toISOString();
    const findings = [];
    const functional = {};

    const getTree = async () => buildTree(await client.send('DOM.getDocument', { depth: -1, pierce: true }, 10000));
    const waitForTree = async (predicate, waitTimeoutMs = timeoutMs) => {
        const started = performance.now();
        while (performance.now() - started < waitTimeoutMs) {
            const tree = await getTree();
            const value = predicate(tree);
            if (value)
                return { elapsedMs: performance.now() - started, tree, value };
            await new Promise(resolve => setTimeout(resolve, 15));
        }
        throw new Error('等待 UI 状态超时');
    };
    const clickNode = async entry => {
        const box = await client.send('DOM.getBoxModel', { nodeId: entry.nodeId });
        const quad = box.model.content?.length >= 8 ? box.model.content : box.model.border;
        const x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4;
        const y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4;
        await client.send('DOM.focus', { nodeId: entry.nodeId });
        await client.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
        await client.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
        await client.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
    };
    const activateNode = async entry => {
        if (!entry)
            throw new Error('待激活控件不存在');
        await client.send('DOM.scrollIntoViewIfNeeded', { nodeId: entry.nodeId }).catch(() => {});
        await client.send('DOM.focus', { nodeId: entry.nodeId });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13,
        });
    };    const replaceText = async (entry, text) => {
        await clickNode(entry);
        await client.send('DOM.focus', { nodeId: entry.nodeId });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8,
        });
        await client.send('Input.insertText', { text });
    };
    const openSettingsMenuItem = async name => {
        const tree = await getTree();
        const settingsButton = findByName(tree, 'SettingsMenuButton');
        if (!settingsButton)
            throw new Error('找不到 SettingsMenuButton');
        await clickNode(settingsButton);
        const item = await waitForTree(current => findByName(current, name));
        await clickNode(item.value);
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
            && typeOf(entry).includes(viewType)));
        return performance.now() - started;
    };
    const screenshot = async fileName => {
        const started = performance.now();
        const result = await client.send('Page.captureScreenshot', { format: 'png' }, 10000);
        const buffer = Buffer.from(result.data, 'base64');
        const outputPath = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'screenshots', fileName);
        await fs.mkdir(path.dirname(outputPath), { recursive: true });
        await fs.writeFile(outputPath, buffer);
        return {
            elapsedMs: performance.now() - started,
            bytes: buffer.length,
            sha256: crypto.createHash('sha256').update(buffer).digest('hex'),
            contentLuminance: pngContentLuminance(buffer),
            path: outputPath,
        };
    };

    try {
        let tree = await getTree();
        const onboarding = findByText(tree, '开始记录', entry => typeOf(entry).endsWith('Button'));
        if (onboarding) {
            const started = performance.now();
            await clickNode(onboarding);
            await waitForTree(current => !findByText(current, '开始记录'));
            functional.onboardingDismissMs = performance.now() - started;
        }

        functional.navigation = {
            queryMs: await navigate('事项查询', 'WorkItemQueryView'),
            statisticsMs: await navigate('统计工具', 'StatisticsView'),
            diaryMs: await navigate('日记记录', 'DiaryEditorView'),
        };

        tree = await getTree();
        const settingsButton = findByName(tree, 'SettingsMenuButton');
        if (!settingsButton)
            throw new Error('找不到 SettingsMenuButton');
        let started = performance.now();
        await clickNode(settingsButton);
        const menuResult = await waitForTree(current => findByName(current, 'ProgramSettingsMenuItem'));
        functional.settingsMenuOpenMs = performance.now() - started;
        started = performance.now();
        await clickNode(menuResult.value);
        await waitForTree(current => current.entries.find(entry => typeOf(entry).includes('SettingsView')));
        functional.settingsDialogOpenMs = performance.now() - started;
        functional.programSettingsScreenshot = await screenshot('manual-program-settings.png');
        tree = await getTree();
        const closeButton = tree.entries.find(entry => typeOf(entry).endsWith('Button')
            && textOf(entry) === '关闭' && hasAncestorType(tree, entry, 'SettingsView'));
        if (!closeButton)
            throw new Error('找不到设置对话框关闭按钮');
        started = performance.now();
        await clickNode(closeButton);
        await waitForTree(current => !current.entries.some(entry => typeOf(entry).includes('SettingsView')));
        functional.settingsDialogCloseMs = performance.now() - started;

        const tagName = 'UI自动化模板标签';
        const templateName = 'UI自动化事项模板';
        const templateTitle = 'UI自动化模板生成事项';
        functional.tagsAndTemplates = {};

        started = performance.now();
        await openSettingsMenuItem('TagSettingsMenuItem');
        const tagEditor = await waitForTree(current => current.entries.find(
            entry => typeOf(entry).includes('TagEditorView')));
        tree = tagEditor.tree;
        const tagNameInput = findByName(tree, 'TagNameInput');
        if (!tagNameInput)
            throw new Error('找不到标签名称输入框');
        await replaceText(tagNameInput, tagName);
        await waitForTree(current => textOf(findByName(current, 'TagNameInput')) === tagName);
        tree = await getTree();
        await activateNode(findByName(tree, 'AddTagButton'));
        await waitForTree(current => findByText(current, tagName,
            entry => hasAncestorName(current, entry, 'TagList')));
        await new Promise(resolve => setTimeout(resolve, 3000));
        functional.tagsAndTemplates.tagSettingsScreenshot = await screenshot('manual-tag-settings.png');
        tree = await getTree();
        const automationTab = findByName(tree, 'TagAutomationTab');
        if (!automationTab)
            throw new Error('找不到标签自动化页签');
        await clickNode(automationTab);
        await waitForTree(current => findByText(current, 'Tracker 自动化操作'));
        functional.tagsAndTemplates.tagCreateMs = performance.now() - started;
        functional.tagsAndTemplates.automationTabOpened = true;
        tree = await getTree();
        await activateNode(findByName(tree, 'SaveTagSettingsButton'));
        await waitForTree(current => !current.entries.some(entry => typeOf(entry).includes('TagEditorView')));

        started = performance.now();
        await openSettingsMenuItem('TemplateSettingsMenuItem');
        const templateEditor = await waitForTree(current => current.entries.find(
            entry => typeOf(entry).includes('TemplateEditorView')));
        tree = templateEditor.tree;
        const templateNameInput = findByName(tree, 'TemplateNameInput');
        if (!templateNameInput)
            throw new Error('找不到模板名称输入框');
        await replaceText(templateNameInput, templateName);
        await waitForTree(current => textOf(findByName(current, 'TemplateNameInput')) === templateName);
        tree = await getTree();
        await activateNode(findByName(tree, 'AddTemplateButton'));
        await waitForTree(current => findByName(current, 'TemplateItemExpander'));
        await new Promise(resolve => setTimeout(resolve, 120));
        const templateItemTree = await getTree();
        const templateItem = findByName(templateItemTree, 'TemplateItemExpander');
        const templateHeader = templateItemTree.entries.find(entry => nameOf(entry) === 'ExpanderHeader'
            && ancestor(templateItemTree, entry, current => current.nodeId === templateItem.nodeId));
        if (!templateHeader)
            throw new Error('找不到模板展开按钮');
        await clickNode(templateHeader);
        const expandedTemplate = await waitForTree(current => findByName(current, 'TemplateDefaultTitleInput'));
        tree = expandedTemplate.tree;
        await replaceText(findByName(tree, 'TemplateDefaultTitleInput'), templateTitle);
        await waitForTree(current => textOf(findByName(current, 'TemplateDefaultTitleInput')) === templateTitle);
        tree = await getTree();
        await replaceText(findByName(tree, 'TemplateDefaultTimeInput'), '1.5');
        await waitForTree(current => textWithinNamedControl(current, 'TemplateDefaultTimeInput').includes('1.5'));
        tree = await getTree();
        await activateNode(findByName(tree, 'TemplateAddTagButton'));
        const templateTagMenu = await waitForTree(current => findByText(current, tagName,
            entry => hasAncestorType(current, entry, 'MenuItem')));
        const templateTagMenuItem = ancestor(templateTagMenu.tree, templateTagMenu.value,
            entry => typeOf(entry).includes('MenuItem'));
        if (!templateTagMenuItem)
            throw new Error('找不到模板标签菜单项');
        await clickNode(templateTagMenuItem);
        await waitForTree(current => findByText(current, tagName,
            entry => hasAncestorName(current, entry, 'TemplateItemExpander')));
        await new Promise(resolve => setTimeout(resolve, 3000));
        functional.tagsAndTemplates.templateSettingsScreenshot = await screenshot('manual-template-settings.png');
        tree = await getTree();
        await activateNode(findByName(tree, 'SaveTemplateSettingsButton'));
        await waitForTree(current => !current.entries.some(entry => typeOf(entry).includes('TemplateEditorView')));
        functional.tagsAndTemplates.templateConfigureMs = performance.now() - started;

        const themeBefore = await screenshot('smoke-theme-before.png');
        tree = await getTree();
        const themeButton = findByName(tree, 'ThemeToggleButton');
        if (!themeButton)
            throw new Error('找不到主题切换按钮');
        started = performance.now();
        await clickNode(themeButton);
        let themeAfter;
        let luminanceDelta = 0;
        while (performance.now() - started < timeoutMs) {
            await new Promise(resolve => setTimeout(resolve, 20));
            themeAfter = await screenshot('smoke-theme-after.png');
            luminanceDelta = Math.abs(themeAfter.contentLuminance - themeBefore.contentLuminance);
            if (luminanceDelta >= 40)
                break;
        }
        if (!themeAfter || luminanceDelta < 40)
            throw new Error(`主题切换未改变主内容配色：亮度差 ${luminanceDelta.toFixed(2)}`);
        functional.themeSwitchMs = performance.now() - started;
        functional.themeSwitch = {
            beforeLuminance: themeBefore.contentLuminance,
            afterLuminance: themeAfter.contentLuminance,
            luminanceDelta,
        };

        tree = await getTree();
        const newButton = findByName(tree, 'NewWorkItemButton');
        if (!newButton)
            throw new Error('找不到 NewWorkItemButton');
        started = performance.now();
        await clickNode(newButton);
        const editorResult = await waitForTree(current => findByName(current, 'WorkTitleInput'));
        functional.newWorkItemOpenMs = performance.now() - started;
        tree = editorResult.tree;
        const dateInputBefore = tree.entries.find(entry => nameOf(entry) === 'PART_TextBox'
            && hasAncestorType(tree, entry, 'CalendarDatePicker') && isVisible(entry));
        functional.newWorkItemInitialDate = textOf(dateInputBefore);
        const titleInput = findByName(tree, 'WorkTitleInput');
        started = performance.now();
        await clickNode(titleInput);
        await client.send('DOM.focus', { nodeId: titleInput.nodeId });
        await waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            return input?.a?.IsFocused === 'true' ? input : null;
        });
        functional.titleFocusMs = performance.now() - started;
        const sampleTitle = 'UI自动化响应测试';
        started = performance.now();
        await client.send('Input.insertText', { text: sampleTitle });
        await waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            return textOf(input) === sampleTitle ? input : null;
        });
        functional.titleInputMs = performance.now() - started;

        tree = await getTree();
        const timeInput = findByName(tree, 'WorkTimeInput');
        if (!timeInput)
            throw new Error('找不到统一耗时输入框 WorkTimeInput');
        if (findByName(tree, 'WorkTimeExpressionInput') || findByText(tree, '时间输入：') || findByText(tree, '应用'))
            throw new Error('耗时仍使用两个输入框或保留了旧应用入口');
        started = performance.now();
        await replaceText(timeInput, '1h30m');
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13,
        });
        await waitForTree(current => {
            const input = findByName(current, 'WorkTimeInput');
            return input && textOf(input) === '1.5';
        });
        functional.unifiedTimeInputEnterMs = performance.now() - started;
        functional.unifiedTimeInputEnterApplied = true;

        tree = await getTree();
        const resetTimeInput = findByName(tree, 'WorkTimeInput');
        await replaceText(resetTimeInput, '2h');
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27,
        });
        await client.send('Input.dispatchKeyEvent', {
            type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27,
        });
        await waitForTree(current => textOf(findByName(current, 'WorkTimeInput')) === '1.5');
        functional.unifiedTimeInputEscapeRestored = true;

        tree = await getTree();
        await replaceText(findByName(tree, 'WorkTimeInput'), '30m');
        tree = await getTree();
        const todayButton = findByName(tree, 'UseTodayButton');
        if (!todayButton)
            throw new Error('找不到 UseTodayButton');
        started = performance.now();
        await clickNode(todayButton);
        const expectedToday = localDateText();
        await waitForTree(current => {
            const dateApplied = current.entries.find(entry => nameOf(entry) === 'PART_TextBox'
                && textOf(entry) === expectedToday && hasAncestorType(current, entry, 'CalendarDatePicker'));
            const timeApplied = textOf(findByName(current, 'WorkTimeInput')) === '0.5';
            return dateApplied && timeApplied;
        });
        functional.useTodayMs = performance.now() - started;
        functional.today = expectedToday;
        functional.unifiedTimeInputLostFocusApplied = true;
        if (functional.newWorkItemInitialDate && functional.newWorkItemInitialDate !== expectedToday) {
            findings.push({
                severity: 'warning',
                code: 'new-item-date-not-today',
                message: '新建事项默认日期不是系统当天：' + functional.newWorkItemInitialDate + '，使用今天后为 ' + expectedToday,
            });
        }

        await screenshot('smoke-new-item.png');
        functional.navigation.queryWithDraftMs = await navigate('事项查询', 'WorkItemQueryView');
        functional.navigation.diaryAfterDraftMs = await navigate('日记记录', 'DiaryEditorView');
        tree = await getTree();
        functional.draftRetainedAfterNavigation = Boolean(tree.entries.find(entry => textOf(entry) === sampleTitle));
        functional.localSaveObserved = Boolean(tree.entries.find(entry => textOf(entry).includes('本地已保存')));
        if (!functional.draftRetainedAfterNavigation || !functional.localSaveObserved)
            throw new Error('新建事项在主导航切换后未保留，或没有观察到本地保存状态');

        tree = await getTree();
        const replacementButton = findByName(tree, 'NewWorkItemButton');
        await activateNode(replacementButton);
        const replacementEditor = await waitForTree(current => findByName(current, 'WorkTitleInput'));
        const replacementTitle = 'UI自动化新建覆盖测试';
        await replaceText(replacementEditor.value, replacementTitle);
        await waitForTree(current => textOf(findByName(current, 'WorkTitleInput')) === replacementTitle);
        tree = await getTree();
        started = performance.now();
        await activateNode(findByName(tree, 'NewWorkItemButton'));
        const replacementResult = await waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            const savedTitle = current.entries.find(entry => textOf(entry) === replacementTitle);
            return input && textOf(input) === '' && savedTitle ? savedTitle : null;
        });
        functional.newEditNewPreservedMs = performance.now() - started;
        functional.newEditNewPreserved = Boolean(replacementResult.value);
        if (!functional.newEditNewPreserved)
            throw new Error('新建、修改后再次新建时，第一条日志没有保留');

        const templateReplacementDraft = 'UI自动化模板替换前草稿';
        tree = replacementResult.tree;
        await replaceText(findByName(tree, 'WorkTitleInput'), templateReplacementDraft);
        await waitForTree(current => textOf(findByName(current, 'WorkTitleInput')) === templateReplacementDraft);
        tree = await getTree();
        started = performance.now();
        await activateNode(findByName(tree, 'NewFromTemplateButton'));
        const templateMenu = await waitForTree(current => findByText(current, templateName,
            entry => hasAncestorType(current, entry, 'MenuItem')));
        const templateMenuItem = ancestor(templateMenu.tree, templateMenu.value,
            entry => typeOf(entry).includes('MenuItem'));
        if (!templateMenuItem)
            throw new Error('找不到从模板新建菜单项');
        await clickNode(templateMenuItem);
        const templateApplied = await waitForTree(current => {
            const title = findByName(current, 'WorkTitleInput');
            const previousDraft = findByText(current, templateReplacementDraft);
            const tag = findByText(current, tagName,
                entry => hasAncestorType(current, entry, 'WorkEditorView'));
            const timeText = textWithinNamedControl(current, 'WorkTimeInput');
            return title && textOf(title) === templateTitle && previousDraft && tag
                && timeText.includes('1.5') ? { previousDraft, tag, timeText } : null;
        });
        functional.tagsAndTemplates.templateApplyMs = performance.now() - started;
        functional.tagsAndTemplates.previousDraftPreserved = Boolean(templateApplied.value.previousDraft);
        functional.tagsAndTemplates.titleApplied = true;
        functional.tagsAndTemplates.timeApplied = 1.5;
        functional.tagsAndTemplates.tagApplied = true;
        const saveTree = await getTree();
        const saveText = findByText(saveTree, '保存', entry => typeOf(entry).includes('Button'));
        const saveButton = saveText && ancestor(saveTree, saveText,
            entry => typeOf(entry).includes('Button'));
        if (!saveButton)
            throw new Error('找不到模板事项保存按钮');
        await clickNode(saveButton);
        await waitForTree(current => current.entries.find(entry => isVisible(entry)
            && textOf(entry).includes('本地已保存')
            && hasAncestorType(current, entry, 'DiaryEditorView')), 8000);
        await screenshot('smoke-template-applied.png');
        functional.navigation.queryAfterTemplateMs = await navigate('事项查询', 'WorkItemQueryView');
        functional.navigation.diaryAfterTemplateMs = await navigate('日记记录', 'DiaryEditorView');
        tree = await getTree();
        const persistedTemplate = await readPersistedTemplate(state, templateTitle, tagName);
        functional.tagsAndTemplates.visibleAfterNavigation = Boolean(findByText(tree, templateTitle));
        functional.tagsAndTemplates.persistedTitle = Boolean(persistedTemplate);
        functional.tagsAndTemplates.persistedTag = persistedTemplate?.hasTag === 1;
        functional.tagsAndTemplates.persistedLocalSave = Boolean(persistedTemplate);
        functional.tagsAndTemplates.persistedHours = persistedTemplate?.hours;
        functional.tagsAndTemplates.persisted = functional.tagsAndTemplates.persistedTitle
            && functional.tagsAndTemplates.persistedTag
            && functional.tagsAndTemplates.persistedLocalSave
            && functional.tagsAndTemplates.persistedHours === 1.5;
        if (!functional.tagsAndTemplates.persisted)
            throw new Error('模板生成事项在导航后没有完整持久化');

        for (let index = 0; index < 3; index++)
            await client.send('DOM.getDocument', { depth: -1, pierce: true }, 10000);
        const documentTimes = [];
        for (let index = 0; index < 30; index++) {
            started = performance.now();
            await client.send('DOM.getDocument', { depth: -1, pierce: true }, 10000);
            documentTimes.push(performance.now() - started);
        }
        const shallowDocument = await client.send('DOM.getDocument', { depth: 1, pierce: true }, 10000);
        for (let index = 0; index < 5; index++)
            await client.send('DOM.querySelector', { nodeId: shallowDocument.root.nodeId, selector: '#ViewList' });
        const queryTimes = [];
        for (let index = 0; index < 100; index++) {
            started = performance.now();
            await client.send('DOM.querySelector', { nodeId: shallowDocument.root.nodeId, selector: '#ViewList' });
            queryTimes.push(performance.now() - started);
        }
        for (let index = 0; index < 2; index++)
            await client.send('Page.captureScreenshot', { format: 'png' }, 10000);
        const screenshotTimes = [];
        for (let index = 0; index < 10; index++) {
            started = performance.now();
            await client.send('Page.captureScreenshot', { format: 'png' }, 10000);
            screenshotTimes.push(performance.now() - started);
        }

        const finalScreenshot = await screenshot('smoke-final.png');
        const report = {
            status: 'passed',
            startedAt,
            completedAt: new Date().toISOString(),
            processId: state.processId,
            port: state.port,
            profile: state.profile,
            startupReadyMs: state.startupReadyMs,
            targetTitle: targets[0].title,
            functional,
            performance: {
                domGetDocument: summarize(documentTimes),
                querySelector: summarize(queryTimes),
                screenshot: summarize(screenshotTimes),
            },
            findings,
            finalScreenshot,
        };
        const reportDirectory = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'reports');
        await fs.mkdir(reportDirectory, { recursive: true });
        const reportPath = path.join(reportDirectory, 'ui-smoke-'
            + new Date().toISOString().replaceAll(':', '-').replaceAll('.', '-') + '.json');
        await fs.writeFile(reportPath, JSON.stringify(report, null, 2) + '\n', 'utf8');
        console.log(JSON.stringify({ ...report, reportPath }, null, 2));
    }
    finally {
        client.close();
    }
}

main().catch(error => {
    console.error(error.stack || error.message || String(error));
    process.exitCode = 1;
});
