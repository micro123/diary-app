#!/usr/bin/env node

import fs from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import {
    ancestor,
    delay,
    descendants,
    findByName,
    findByText,
    isVisible,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const execFileAsync = promisify(execFile);
const fixedPages = [
    { label: '日记记录', viewType: 'DiaryEditorView', anchorName: 'CompactCalendar', kind: 'core' },
    { label: '事项查询', viewType: 'WorkItemQueryView', anchorName: 'FilterToggle', kind: 'core' },
    { label: '统计工具', viewType: 'StatisticsView', anchorName: 'StatisticsChartTypeToggle', kind: 'core' },
    { label: '调查工具', viewType: 'SurveyView', anchorName: 'SurveyQueryStatus', kind: 'core' },
    { label: '脚本管理', viewType: 'ScriptManagementView', kind: 'core' },
];

function argumentValue(name, fallback) {
    const index = process.argv.indexOf(name);
    return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

function integerArgument(name, fallback, minimum, maximum) {
    const value = Number(argumentValue(name, fallback));
    if (!Number.isInteger(value) || value < minimum || value > maximum)
        throw new Error(`${name} 必须是 ${minimum} 到 ${maximum} 的整数`);
    return value;
}

const hotRounds = integerArgument('--hot-rounds', 5, 1, 50);
const orderOffset = integerArgument('--order-offset', 0, 0, 1000);
const preloadWaitMs = integerArgument('--preload-wait-ms', 1800, 0, 10000);
const requireDynamicPage = process.argv.includes('--require-dynamic-page');

function percentile(values, ratio) {
    if (values.length === 0)
        return 0;
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

function summarize(samples, selector) {
    const values = samples.map(selector);
    return {
        count: values.length,
        averageMs: values.reduce((sum, value) => sum + value, 0) / values.length,
        p50Ms: percentile(values, 0.5),
        p95Ms: percentile(values, 0.95),
        p99Ms: percentile(values, 0.99),
        maxMs: Math.max(...values),
    };
}

function roundMetrics(value) {
    if (Array.isArray(value))
        return value.map(roundMetrics);
    if (!value || typeof value !== 'object')
        return typeof value === 'number' ? Math.round(value * 100) / 100 : value;
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, roundMetrics(child)]));
}

function rootOf(tree, viewType) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(viewType));
}

function pageReady(tree, page) {
    const root = rootOf(tree, page.viewType);
    if (!root)
        return null;
    if (page.anchorName) {
        const anchor = findByName(tree, page.anchorName);
        if (!anchor || !ancestor(tree, anchor, entry => entry.nodeId === root.nodeId))
            return null;
    }
    if (page.viewType === 'ScriptManagementView') {
        const loading = descendants(tree, root).some(entry => isVisible(entry)
            && ['正在加载脚本目录', '正在重新加载脚本目录'].includes(textOf(entry)));
        if (loading)
            return null;
    }
    return root;
}

function pageSignature(tree, page, root) {
    const visible = [root, ...descendants(tree, root)].filter(isVisible);
    const texts = visible.map(textOf).filter(Boolean).slice(0, 40);
    return JSON.stringify({
        type: typeOf(root),
        bounds: root.a.Bounds,
        visibleCount: visible.length,
        texts,
        ready: Boolean(pageReady(tree, page)),
    });
}

async function waitForStablePage(connection, page, timeoutMs = 12000) {
    const visible = await connection.waitForTree(tree => pageReady(tree, page), timeoutMs,
        `导航页面未就绪：${page.label}`);
    const visibleAt = performance.now();
    let previous = '';
    let stableCount = 0;
    const deadline = performance.now() + timeoutMs;
    while (performance.now() < deadline) {
        const tree = await connection.getTree();
        const root = pageReady(tree, page);
        if (!root) {
            previous = '';
            stableCount = 0;
        }
        else {
            const signature = pageSignature(tree, page, root);
            stableCount = signature === previous ? stableCount + 1 : 1;
            previous = signature;
            if (stableCount >= 3)
                return { visible, visibleAt, tree, root };
        }
        await delay(40);
    }
    throw new Error(`页面视觉树未稳定：${page.label}`);
}

function navigationLabels(tree) {
    const list = findByName(tree, 'ViewList');
    assertUi(list, '找不到左侧主导航 ViewList');
    const labels = [];
    for (const item of descendants(tree, list).filter(entry => typeOf(entry).includes('SelectionListItem'))) {
        const label = descendants(tree, item).find(entry => isVisible(entry)
            && String(entry.a.Class ?? '').includes('ViewName'));
        const value = textOf(label) || textOf(item);
        if (value && !labels.includes(value))
            labels.push(value);
    }
    return labels;
}

function resolvePages(labels) {
    const pages = [];
    for (const label of labels) {
        const fixed = fixedPages.find(page => page.label === label);
        pages.push(fixed ?? {
            label,
            viewType: 'RedMineManageView',
            kind: 'tracker',
        });
    }
    return pages;
}

function rotate(values, offset) {
    if (values.length === 0)
        return [];
    const normalized = offset % values.length;
    return [...values.slice(normalized), ...values.slice(0, normalized)];
}

function cycleDestinations(values, currentLabel, reverse) {
    const ordered = reverse ? [...values].reverse() : values;
    const currentIndex = ordered.findIndex(page => page.label === currentLabel);
    assertUi(currentIndex >= 0, `当前页面不在导航清单中：${currentLabel}`);
    const rotated = rotate(ordered, currentIndex + 1);
    return [...rotated.filter(page => page.label !== currentLabel), ordered[currentIndex]];
}

async function navigationTarget(connection, page) {
    const tree = await connection.getTree();
    const textEntry = findByText(tree, page.label, entry =>
        Boolean(ancestor(tree, entry, current => typeOf(current).includes('SelectionListItem'))));
    assertUi(textEntry, `找不到导航项：${page.label}`);
    const item = ancestor(tree, textEntry, entry => typeOf(entry).includes('SelectionListItem'));
    assertUi(item, `找不到导航容器：${page.label}`);
    const box = await connection.client.send('DOM.getBoxModel', { nodeId: item.nodeId });
    const quad = box.model.content?.length >= 8 ? box.model.content : box.model.border;
    return {
        item,
        x: (quad[0] + quad[2] + quad[4] + quad[6]) / 4,
        y: (quad[1] + quad[3] + quad[5] + quad[7]) / 4,
    };
}

async function measureNavigation(connection, from, page, phase, round) {
    const target = await navigationTarget(connection, page);
    const started = performance.now();
    await connection.client.send('Input.dispatchMouseEvent', {
        type: 'mouseMoved', x: target.x, y: target.y,
    });
    await connection.client.send('Input.dispatchMouseEvent', {
        type: 'mousePressed', x: target.x, y: target.y, button: 'left', clickCount: 1,
    });
    await connection.client.send('Input.dispatchMouseEvent', {
        type: 'mouseReleased', x: target.x, y: target.y, button: 'left', clickCount: 1,
    });
    const dispatchedAt = performance.now();
    const settled = await waitForStablePage(connection, page);
    const completed = performance.now();
    return {
        phase,
        round,
        from,
        to: page.label,
        kind: page.kind,
        dispatchMs: dispatchedAt - started,
        visibleMs: settled.visibleAt - started,
        settledMs: completed - started,
    };
}

async function linuxProcessMetrics(processId) {
    const [ioText, statusText, statText] = await Promise.all([
        fs.readFile(`/proc/${processId}/io`, 'utf8'),
        fs.readFile(`/proc/${processId}/status`, 'utf8'),
        fs.readFile(`/proc/${processId}/stat`, 'utf8'),
    ]);
    const io = Object.fromEntries(ioText.trim().split('\n').map(line => {
        const separator = line.indexOf(':');
        return [line.slice(0, separator), Number(line.slice(separator + 1).trim())];
    }));
    const rssMatch = /^VmRSS:\s+(\d+)\s+kB$/m.exec(statusText);
    const statFields = statText.slice(statText.lastIndexOf(')') + 2).trim().split(/\s+/);
    let clockTicks = 100;
    try {
        clockTicks = Number((await execFileAsync('getconf', ['CLK_TCK'])).stdout.trim()) || 100;
    }
    catch {
        // 100 是 Linux 常见值，采样仅用于进程内趋势比较。
    }
    return {
        available: true,
        platform: 'linux',
        cpuTimeMs: ((Number(statFields[11]) + Number(statFields[12])) / clockTicks) * 1000,
        workingSetBytes: Number(rssMatch?.[1] ?? 0) * 1024,
        readBytes: io.read_bytes ?? 0,
        writeBytes: io.write_bytes ?? 0,
        readOperations: io.syscr ?? 0,
        writeOperations: io.syscw ?? 0,
    };
}

async function windowsProcessMetrics(processId) {
    const executable = process.env.DIARY_PWSH || 'pwsh.exe';
    const script = [
        `$p = Get-CimInstance Win32_Process -Filter 'ProcessId = ${processId}'`,
        'if ($null -eq $p) { throw "process not found" }',
        '[pscustomobject]@{',
        ' cpuTimeMs = (([double]$p.KernelModeTime + [double]$p.UserModeTime) / 10000)',
        ' workingSetBytes = [double]$p.WorkingSetSize',
        ' readBytes = [double]$p.ReadTransferCount',
        ' writeBytes = [double]$p.WriteTransferCount',
        ' readOperations = [double]$p.ReadOperationCount',
        ' writeOperations = [double]$p.WriteOperationCount',
        '} | ConvertTo-Json -Compress',
    ].join('; ');
    const { stdout } = await execFileAsync(executable,
        ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, timeout: 10000 });
    return { available: true, platform: 'win32', ...JSON.parse(stdout.trim()) };
}

async function collectProcessMetrics(processId) {
    try {
        if (process.platform === 'linux')
            return await linuxProcessMetrics(processId);
        if (process.platform === 'win32')
            return await windowsProcessMetrics(processId);
        return { available: false, platform: process.platform, reason: 'unsupported-platform' };
    }
    catch (error) {
        return {
            available: false,
            platform: process.platform,
            reason: error instanceof Error ? error.message : String(error),
        };
    }
}

function processMetricDelta(before, after) {
    if (!before.available || !after.available)
        return { available: false, before, after };
    return {
        available: true,
        platform: after.platform,
        cpuTimeMs: after.cpuTimeMs - before.cpuTimeMs,
        workingSetDeltaBytes: after.workingSetBytes - before.workingSetBytes,
        readBytes: after.readBytes - before.readBytes,
        writeBytes: after.writeBytes - before.writeBytes,
        readOperations: after.readOperations - before.readOperations,
        writeOperations: after.writeOperations - before.writeOperations,
    };
}

function summarizeByPage(samples) {
    return Object.fromEntries([...new Set(samples.map(sample => sample.to))].map(label => {
        const pageSamples = samples.filter(sample => sample.to === label);
        return [label, {
            visible: summarize(pageSamples, sample => sample.visibleMs),
            settled: summarize(pageSamples, sample => sample.settledMs),
        }];
    }));
}

let pages = [];
let coldSamples = [];
let hotSamples = [];
let processBefore;
let processAfterCold;

await runUiSuite({
    name: 'ui-navigation-performance',
    scenario: 'navigation-performance',
    timeoutMs: 15000,
    stopOnFailure: true,
}, async ({ connection, runStep, addFinding }) => {
    await runStep('navigation-performance.startup-inventory', '启动就绪与导航页面清单', async () => {
        const startupPage = fixedPages[0];
        await waitForStablePage(connection, startupPage);
        const diaryStableAtUnixMs = Date.now();
        if (preloadWaitMs > 0)
            await delay(preloadWaitMs);
        processBefore = await collectProcessMetrics(connection.state.processId);
        const tree = await connection.getTree();
        const labels = navigationLabels(tree);
        pages = resolvePages(labels);
        for (const expected of fixedPages)
            assertUi(labels.includes(expected.label), `性能场景缺少导航页面：${expected.label}`);
        const dynamicPages = pages.filter(page => page.kind === 'tracker');
        if (requireDynamicPage)
            assertUi(dynamicPages.length > 0, '要求测试 Tracker 动态页，但当前导航中没有动态页面');
        else if (connection.state.withPlugins && dynamicPages.length === 0)
            addFinding('warning', 'navigation-no-dynamic-page', '已加载插件，但没有配置可显示管理页的 Tracker 实例');
        const processStartedAtUnixMs = Number(connection.state.processStartedAtUnixMs || 0);
        return {
            startupReadyMs: connection.state.startupReadyMs,
            startupToDiaryStableMs: processStartedAtUnixMs > 0
                ? diaryStableAtUnixMs - processStartedAtUnixMs
                : null,
            labels,
            corePageCount: pages.filter(page => page.kind === 'core').length,
            dynamicPageCount: dynamicPages.length,
            hotRounds,
            orderOffset,
            preloadWaitMs,
        };
    });

    await runStep('navigation-performance.cold', '首次访问各功能页面', async () => {
        const startupPage = pages.find(page => page.label === '日记记录');
        const unvisited = pages.filter(page => page.label !== '日记记录');
        const order = rotate(unvisited, orderOffset);
        let current = '日记记录';
        for (const page of order) {
            const sample = await measureNavigation(connection, current, page, 'cold', 0);
            coldSamples.push(sample);
            current = page.label;
            await delay(80);
        }
        if (startupPage && current !== startupPage.label) {
            coldSamples.push(await measureNavigation(
                connection, current, startupPage, 'startup-return', 0));
        }
        processAfterCold = await collectProcessMetrics(connection.state.processId);
        for (const sample of coldSamples.filter(sample => sample.phase === 'cold')) {
            if (sample.visibleMs > (sample.kind === 'tracker' ? 3000 : 2000))
                addFinding('warning', 'navigation-cold-slow', `${sample.to} 首次可见耗时偏高`, sample);
        }
        return roundMetrics({
            order: order.map(page => page.label),
            samples: coldSamples,
            byPage: summarizeByPage(coldSamples.filter(sample => sample.phase === 'cold')),
            processDelta: processMetricDelta(processBefore, processAfterCold),
        });
    });

    await runStep('navigation-performance.hot', '正序与倒序重复热切换', async () => {
        let current = '日记记录';
        for (let round = 1; round <= hotRounds; round++) {
            const order = cycleDestinations(pages, current, round % 2 === 0);
            for (const page of order) {
                hotSamples.push(await measureNavigation(connection, current, page, 'hot', round));
                current = page.label;
                await delay(60);
            }
        }
        const byPage = summarizeByPage(hotSamples);
        for (const page of pages) {
            const summary = byPage[page.label]?.visible;
            if (summary && summary.p95Ms > (page.kind === 'tracker' ? 800 : 300))
                addFinding('warning', 'navigation-hot-p95-slow', `${page.label} 热切换 P95 偏高`, summary);
        }
        return roundMetrics({
            rounds: hotRounds,
            samples: hotSamples,
            overall: {
                visible: summarize(hotSamples, sample => sample.visibleMs),
                settled: summarize(hotSamples, sample => sample.settledMs),
            },
            byPage,
        });
    });

    await runStep('navigation-performance.resources', '资源增量与最终状态', async () => {
        const processAfter = await collectProcessMetrics(connection.state.processId);
        const coldDelta = processMetricDelta(processBefore, processAfterCold);
        const hotDelta = processMetricDelta(processAfterCold, processAfter);
        if (hotDelta.available && hotDelta.workingSetDeltaBytes > 128 * 1024 * 1024)
            addFinding('warning', 'navigation-memory-growth-high', '热切换阶段工作集增长超过 128 MiB', hotDelta);
        return roundMetrics({
            processBefore,
            processAfterCold,
            processAfter,
            coldDelta,
            hotDelta,
            screenshot: await connection.screenshot('navigation-performance-final.png'),
        });
    });
});
