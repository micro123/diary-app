#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import {
    ancestor,
    descendants,
    findByName,
    findByText,
    isVisible,
    localDateText,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const scenarioConfigurations = {
    'date-cold-performance': {
        titlePrefix: 'CDP冷日期性能',
        itemCount: 12,
        dateOffset: -1,
        hotRounds: 8,
        burstRounds: 3,
    },
    'date-performance': {
        titlePrefix: 'CDP日期性能',
        itemCount: 48,
        dateOffset: 0,
        hotRounds: 3,
        burstRounds: 2,
    },
};

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    if (sorted.length === 0)
        return 0;
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

function summarize(samples) {
    return {
        switches: samples.length,
        p50Ms: percentile(samples, 0.5),
        p95Ms: percentile(samples, 0.95),
        p99Ms: percentile(samples, 0.99),
        maxMs: Math.max(...samples),
        averageMs: samples.reduce((sum, value) => sum + value, 0) / samples.length,
    };
}

function addDays(value, days) {
    const result = new Date(value.getFullYear(), value.getMonth(), value.getDate());
    result.setDate(result.getDate() + days);
    return result;
}

async function collectDatabaseFiles(root) {
    const files = [];
    const visit = async directory => {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                await visit(fullPath);
                continue;
            }
            if (!entry.isFile() || !/\.sqlite3(?:-(?:wal|shm|journal))?$/.test(entry.name))
                continue;
            const stat = await fs.stat(fullPath);
            files.push({
                path: path.relative(root, fullPath),
                size: stat.size,
                mtimeMs: stat.mtimeMs,
            });
        }
    };
    await visit(root);
    return files.sort((left, right) => left.path.localeCompare(right.path));
}

function compareDatabaseFiles(before, after) {
    const relevant = file => !file.path.endsWith('-shm');
    const beforeMap = new Map(before.filter(relevant).map(file => [file.path, file]));
    const afterMap = new Map(after.filter(relevant).map(file => [file.path, file]));
    const changes = [];
    for (const filePath of new Set([...beforeMap.keys(), ...afterMap.keys()])) {
        const previous = beforeMap.get(filePath);
        const current = afterMap.get(filePath);
        if (!previous || !current || previous.size !== current.size || previous.mtimeMs !== current.mtimeMs)
            changes.push({ path: filePath, previous, current });
    }
    return changes;
}

await runUiSuite({
    name: 'ui-work-item-performance',
    timeoutMs: 15000,
    stopOnFailure: true,
}, async ({ connection, runStep, addFinding }) => {
    let titles = [];
    let workEditorNodeId;
    let databaseBefore;
    const configuration = scenarioConfigurations[connection.state.scenario];
    assertUi(configuration,
        `事项切换性能不支持场景：${connection.state.scenario}`);
    const { titlePrefix, itemCount, dateOffset, hotRounds, burstRounds } = configuration;
    const priorityOrderedIndexes = Array.from({ length: itemCount }, (_, index) => index)
        .sort((left, right) => (left % 4) - (right % 4) || left - right);

    const waitForTitle = title => connection.waitForTree(tree => {
        const input = findByName(tree, 'WorkTitleInput');
        return input && textOf(input) === title ? input : null;
    }, 5000, '事项切换后标题未更新：' + title);

    const focusSelectedItem = async title => {
        const tree = await connection.getTree();
        const list = findByName(tree, 'DailyItemList');
        assertUi(list, '找不到 DailyItemList');
        const label = descendants(tree, list).find(entry => isVisible(entry) && textOf(entry) === title);
        const item = ancestor(tree, label, entry => typeOf(entry).includes('ListBoxItem'));
        assertUi(item, '找不到事项列表项：' + title);
        await connection.clickNode(item);
        await waitForTitle(title);
        await connection.client.send('DOM.focus', { nodeId: item.nodeId });
    };

    const switchItem = async (key, code, virtualKeyCode, expectedTitle) => {
        const started = performance.now();
        await connection.pressKey(key, code, virtualKeyCode);
        await waitForTitle(expectedTitle);
        return performance.now() - started;
    };

    const moveToFirstItem = async () => {
        await connection.pressKey('Home', 'Home', 36);
        await waitForTitle(titles[0]);
    };

    await runStep('work-item-performance.dataset', '确认富事项数据和单实例编辑器', async () => {
        const navigationMs = await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.clickByText('回到今天');
        const today = new Date();
        const targetDate = addDays(today, dateOffset);
        const calendar = await connection.waitForTree(
            tree => findByName(tree, 'CompactCalendarDays'),
            8000,
            '找不到紧凑日期控件');
        await connection.client.send('DOM.focus', { nodeId: calendar.value.nodeId });
        if (dateOffset !== 0)
            await connection.pressKey('ArrowLeft', 'ArrowLeft', 37);
        await connection.waitForTree(
            tree => findByText(tree, `${titlePrefix} ${localDateText(targetDate)} #00`),
            15000,
            '没有加载事项切换性能数据');

        const tree = await connection.getTree();
        const list = findByName(tree, 'DailyItemList');
        assertUi(list, '找不到 DailyItemList');
        const datePrefix = `${titlePrefix} ${localDateText(targetDate)} #`;
        titles = priorityOrderedIndexes.map(index => `${datePrefix}${String(index).padStart(2, '0')}`);
        const visibleTitles = descendants(tree, list)
            .filter(entry => isVisible(entry) && textOf(entry).startsWith(datePrefix))
            .filter(entry => ancestor(tree, entry, candidate => typeOf(candidate).includes('ListBoxItem')))
            .map(textOf);
        assertUi(visibleTitles.includes(titles[0]), '当前虚拟化区域没有首个性能事项');

        const workEditor = tree.entries.find(entry => isVisible(entry)
            && typeOf(entry).includes('WorkEditorView'));
        assertUi(workEditor, '找不到 WorkEditorView');
        const trackerVisible = Boolean(findByName(tree, 'TrackerAssociationCard'));
        if (connection.state.withPlugins)
            assertUi(trackerVisible, '加载插件后没有显示 Tracker 编辑区域');
        workEditorNodeId = workEditor.nodeId;
        await focusSelectedItem(titles[0]);
        databaseBefore = await collectDatabaseFiles(connection.state.profile);
        return {
            navigationMs,
            date: localDateText(targetDate),
            itemCount: titles.length,
            order: titles,
            initiallyRealizedItems: visibleTitles.length,
            workEditorNodeId,
            trackerVisible,
            databaseBefore,
        };
    });

    await runStep('work-item-performance.first-pass', '首次逐项切换并记录每项接管成本', async () => {
        const samples = [];
        for (let index = 1; index < titles.length; index++)
            samples.push(await switchItem('ArrowDown', 'ArrowDown', 40, titles[index]));
        const metrics = summarize(samples);
        if (metrics.p95Ms > 150)
            addFinding('warning', 'work-item-first-pass-slow', '事项首次逐项切换 P95 超过 150 毫秒', metrics);
        return metrics;
    });

    await runStep('work-item-performance.hot-roundtrip', '多轮往返切换事项', async () => {
        await moveToFirstItem();
        const samples = [];
        for (let round = 0; round < hotRounds; round++) {
            for (let index = 1; index < titles.length; index++)
                samples.push(await switchItem('ArrowDown', 'ArrowDown', 40, titles[index]));
            for (let index = titles.length - 2; index >= 0; index--)
                samples.push(await switchItem('ArrowUp', 'ArrowUp', 38, titles[index]));
        }
        const metrics = summarize(samples);
        if (metrics.p95Ms > 100)
            addFinding('warning', 'work-item-hot-switch-slow', '事项热切换 P95 超过 100 毫秒', metrics);
        return { rounds: hotRounds, ...metrics };
    });

    await runStep('work-item-performance.burst', '快速连续切换并等待最终事项稳定', async () => {
        await moveToFirstItem();
        const passes = [];
        for (let round = 0; round < burstRounds; round++) {
            let started = performance.now();
            for (let index = 1; index < titles.length; index++)
                await connection.pressKey('ArrowDown', 'ArrowDown', 40);
            const dispatchedForwardMs = performance.now() - started;
            const settledForward = await waitForTitle(titles.at(-1));

            started = performance.now();
            for (let index = titles.length - 2; index >= 0; index--)
                await connection.pressKey('ArrowUp', 'ArrowUp', 38);
            const dispatchedBackwardMs = performance.now() - started;
            const settledBackward = await waitForTitle(titles[0]);
            passes.push({
                round: round + 1,
                dispatchedForwardMs,
                settleForwardMs: settledForward.elapsedMs,
                dispatchedBackwardMs,
                settleBackwardMs: settledBackward.elapsedMs,
            });
        }
        return { rounds: burstRounds, switches: burstRounds * (titles.length - 1) * 2, passes };
    });

    await runStep('work-item-performance.integrity', '确认编辑器实例稳定且切换未写入数据库', async () => {
        const tree = await connection.getTree();
        const workEditor = tree.entries.find(entry => isVisible(entry)
            && typeOf(entry).includes('WorkEditorView'));
        assertUi(workEditor?.nodeId === workEditorNodeId,
            `WorkEditorView 实例发生变化：${workEditorNodeId} -> ${workEditor?.nodeId ?? 'missing'}`);
        const databaseAfter = await collectDatabaseFiles(connection.state.profile);
        const databaseChanges = compareDatabaseFiles(databaseBefore, databaseAfter);
        assertUi(databaseChanges.length === 0,
            '纯事项切换修改了 SQLite 文件：' + JSON.stringify(databaseChanges));
        return { workEditorNodeId, databaseChanges, databaseAfter };
    });

    await runStep('work-item-performance.screenshot', '保存事项切换性能场景截图', async () =>
        connection.screenshot('work-item-performance-final.png'));
});
