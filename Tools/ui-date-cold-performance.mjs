#!/usr/bin/env node

import {
    ancestor,
    delay,
    descendants,
    findByName,
    findByText,
    localDateText,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const titlePrefix = 'CDP冷日期性能';

function addDays(value, days) {
    const result = new Date(value.getFullYear(), value.getMonth(), value.getDate());
    result.setDate(result.getDate() + days);
    return result;
}

function markerTitle(date) {
    return `${titlePrefix} ${localDateText(date)} #00`;
}

await runUiSuite({
    name: 'ui-date-cold-performance',
    scenario: 'date-cold-performance',
    timeoutMs: 15000,
    stopOnFailure: true,
}, async ({ connection, runStep, addFinding }) => {
    const today = new Date();
    const previousDay = addDays(today, -1);
    let coldSwitchMs;
    let warmSwitchMs;

    const waitForEmptyDate = async () => connection.waitForTree(
        tree => findByText(tree, '当前未选中任何事项'),
        15000,
        '空日期没有显示无事项状态');

    const waitForPreviousDay = async () => connection.waitForTree(
        tree => findByText(tree, markerTitle(previousDay)),
        15000,
        `没有加载上一日性能事项：${localDateText(previousDay)}`);

    const focusSelectedDay = async date => {
        const tree = await connection.getTree();
        const calendar = findByName(tree, 'CompactCalendarDays');
        assertUi(calendar, '找不到 CompactCalendarDays');
        const dayText = String(date.getDate());
        const selectedText = descendants(tree, calendar).find(entry =>
            textOf(entry) === dayText
            && String(entry.a.Class ?? '').includes('Selected'));
        const button = ancestor(tree, selectedText, entry => typeOf(entry).includes('Button'));
        assertUi(button, `找不到选中日期按钮：${localDateText(date)}`);
        await connection.client.send('DOM.focus', { nodeId: calendar.nodeId });
    };

    await runStep('date-cold-performance.empty-start', '确认启动日期为空并等待单实例编辑器稳定', async () => {
        const navigationMs = await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.clickByText('回到今天');
        const empty = await waitForEmptyDate();
        await focusSelectedDay(today);
        await delay(750);
        return {
            navigationMs,
            emptyReadyMs: empty.elapsedMs,
            emptyDate: localDateText(today),
            populatedDate: localDateText(previousDay),
        };
    });

    await runStep('date-cold-performance.first-populated-date', '首次从空日期切换到有事项日期', async () => {
        const started = performance.now();
        await connection.pressKey('ArrowLeft', 'ArrowLeft', 37);
        await waitForPreviousDay();
        coldSwitchMs = performance.now() - started;
        if (coldSwitchMs > 300)
            addFinding('warning', 'empty-to-populated-cold-slow', '空日期首次进入有事项日期超过 300 毫秒', { coldSwitchMs });
        return { coldSwitchMs, itemCount: 12 };
    });

    await runStep('date-cold-performance.warm-repeat', '返回空日期后重复进入同一有事项日期', async () => {
        await connection.pressKey('ArrowRight', 'ArrowRight', 39);
        await waitForEmptyDate();
        await focusSelectedDay(today);
        const started = performance.now();
        await connection.pressKey('ArrowLeft', 'ArrowLeft', 37);
        await waitForPreviousDay();
        warmSwitchMs = performance.now() - started;
        return {
            coldSwitchMs,
            warmSwitchMs,
            coldToWarmRatio: warmSwitchMs > 0 ? coldSwitchMs / warmSwitchMs : null,
        };
    });

    await runStep('date-cold-performance.screenshot', '保存冷日期切换结果截图', async () =>
        connection.screenshot('date-cold-performance-final.png'));
});
