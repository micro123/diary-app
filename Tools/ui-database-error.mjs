#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findByName,
    findByText,
    findByTextContains,
    isVisible,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function visibleRoots(tree, typeName) {
    return tree.entries.filter(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function textWithin(tree, root, text, contains = false) {
    if (!root)
        return null;
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry)
        && (contains ? textOf(entry).includes(text) : textOf(entry) === text));
}

async function activateTextWithin(connection, typeName, text, contains = false) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '页面不可见：' + typeName);
    const label = textWithin(tree, root, text, contains);
    assertUi(label, '页面缺少操作：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '操作不可激活：' + text);
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
}

async function closeStartupSettings(connection) {
    const tree = await connection.getTree();
    const settings = rootOf(tree, 'SettingsView');
    if (!settings)
        return false;
    const closeText = textWithin(tree, settings, '关闭');
    assertUi(closeText, '启动时设置对话框缺少关闭按钮');
    const closeButton = controlForText(tree, closeText);
    assertUi(closeButton, '启动时设置对话框无法关闭');
    await connection.client.send('DOM.focus', { nodeId: closeButton.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
    await connection.waitForTree(current => !rootOf(current, 'SettingsView'), 10000,
        '启动时设置对话框没有关闭');
    return true;
}

async function dismissMessages(connection) {
    let count = 0;
    for (let index = 0; index < 8; index++) {
        const tree = await connection.getTree();
        const buttons = tree.entries.filter(entry => isVisible(entry)
            && entry.a.Name === 'PART_OKButton');
        const button = buttons.at(-1);
        if (!button)
            break;
        await connection.client.send('DOM.focus', { nodeId: button.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        count++;
        await delay(80);
    }
    return count;
}

async function waitForMessage(connection, title, bodyText) {
    return connection.waitForTree(tree => {
        for (const message of visibleRoots(tree, 'StandardMessageView').reverse()) {
            const dialog = ancestor(tree, message, entry => typeOf(entry).includes('DialogControl'));
            const scope = dialog ?? message;
            const titleEntry = textWithin(tree, scope, title, true);
            const bodyEntry = textWithin(tree, scope, bodyText, true);
            if (titleEntry && bodyEntry)
                return { message, dialog, titleEntry, bodyEntry };
        }
        return null;
    }, 10000, '通知未出现：' + title);
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

await runUiSuite({ name: 'ui-database-error', scenario: 'database-error', timeoutMs: 10000 }, async ({
    connection, runStep, addFinding,
}) => {
    await closeStartupSettings(connection);
    await dismissMessages(connection);

    await runStep('database.diary-empty-state', '日记页数据库异常空状态与恢复入口', async () => {
        const navigationMs = await connection.navigate('日记记录', 'DiaryEditorView');
        const tree = await connection.getTree();
        const root = rootOf(tree, 'DiaryEditorView');
        for (const text of [
            '数据库连接不可用',
            '本地记录不会因为连接失败被删除。恢复连接后即可继续查看和编辑。',
            '重试连接',
            '打开数据库设置',
            '导出诊断日志',
        ])
            assertUi(textWithin(tree, root, text, true), '日记异常状态缺少：' + text);
        const status = textWithin(tree, root, 'Diary.UiTest.MissingDatabase', true);
        assertUi(status, '日记异常状态未显示具体驱动错误');
        return { navigationMs, status: textOf(status) };
    });

    await runStep('database.retry', '重试连接保留数据并给出可恢复操作', async () => {
        await activateTextWithin(connection, 'DiaryEditorView', '重试连接');
        const result = await waitForMessage(connection, '数据库仍不可用', '本地记录不会因连接失败被删除');
        const tree = result.tree;
        const message = result.value.dialog ?? result.value.message;
        assertUi(textWithin(tree, message, '可恢复操作：重试连接、打开数据库设置、导出诊断日志。', true),
            '重试失败通知缺少可恢复操作说明');
        await dismissMessages(connection);
        return { notificationMs: result.elapsedMs };
    });

    await runStep('database.settings-unavailable-driver', '无效数据库驱动的设置入口安全失败', async () => {
        await activateTextWithin(connection, 'DiaryEditorView', '打开数据库设置');
        const result = await connection.waitForTree(tree => {
            for (const message of visibleRoots(tree, 'StandardMessageView').reverse()) {
                const dialog = ancestor(tree, message, entry => typeOf(entry).includes('DialogControl'));
                const scope = dialog ?? message;
                const body = [scope, ...descendants(tree, scope)].find(entry => isVisible(entry)
                    && (textOf(entry).includes('数据库驱动') || textOf(entry).includes('不支持')));
                if (body)
                    return { message, dialog, body };
            }
            return null;
        }, 10000, '无效数据库驱动没有给出错误通知');
        assertUi(!rootOf(result.tree, 'GenericConfigView'), '无效驱动不应进入可保存的数据库配置界面');
        const message = textOf(result.value.body);
        await dismissMessages(connection);
        return { notificationMs: result.elapsedMs, message };
    });

    await runStep('database.export-diagnostics', '数据库异常时导出诊断日志', async () => {
        const temporaryDirectory = path.join(connection.state.profile, 'temp');
        const before = await fs.readdir(temporaryDirectory).catch(() => []);
        await activateTextWithin(connection, 'DiaryEditorView', '导出诊断日志');
        const result = await connection.waitForTree(tree => {
            for (const message of visibleRoots(tree, 'StandardMessageView').reverse()) {
                const dialog = ancestor(tree, message, entry => typeOf(entry).includes('DialogControl'));
                const scope = dialog ?? message;
                const resultText = textWithin(tree, scope, '诊断日志已导出', true)
                    ?? textWithin(tree, scope, '暂无诊断日志', true);
                if (resultText)
                    return resultText;
            }
            return null;
        }, 10000, '诊断日志导出结果通知未出现');
        const exported = await fs.readdir(temporaryDirectory).catch(() => []);
        const newArchives = exported.filter(name => name.startsWith('DiaryApp-logs-')
            && name.endsWith('.zip') && !before.includes(name));
        assertUi(newArchives.length > 0, '通知显示后未找到新导出的诊断日志压缩包');
        const archivePath = path.join(temporaryDirectory, newArchives.at(-1));
        const stat = await fs.stat(archivePath);
        assertUi(stat.size > 0, '导出的诊断日志压缩包为空');
        await dismissMessages(connection);
        return { notificationMs: result.elapsedMs, archiveBytes: stat.size, archiveCount: newArchives.length };
    });

    await runStep('database.query-preserves-results', '查询页在数据库异常时保留上次结果', async () => {
        const navigationMs = await connection.navigate('事项查询', 'WorkItemQueryView');
        const tree = await connection.getTree();
        const root = rootOf(tree, 'WorkItemQueryView');
        const queryText = textWithin(tree, root, '查询');
        assertUi(queryText, '查询页缺少查询按钮');
        const queryButton = controlForText(tree, queryText);
        assertUi(queryButton, '查询按钮不可激活');
        await connection.client.send('DOM.focus', { nodeId: queryButton.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        const result = await connection.waitForTree(current => {
            const currentRoot = rootOf(current, 'WorkItemQueryView');
            return textWithin(current, currentRoot, '数据库不可用，已保留上次查询结果', true);
        }, 10000, '数据库异常查询未显示保留结果提示');
        return { navigationMs, feedbackMs: result.elapsedMs };
    });

    await runStep('database.statistics-empty-state', '统计页数据库异常空状态与恢复入口', async () => {
        const navigationMs = await connection.navigate('统计工具', 'StatisticsView');
        const tree = await connection.getTree();
        const root = rootOf(tree, 'StatisticsView');
        for (const text of [
            '数据库连接不可用',
            '本地记录不会因为连接失败被删除。恢复连接后即可继续查看统计。',
            '重试连接',
            '打开数据库设置',
            '导出诊断日志',
        ])
            assertUi(textWithin(tree, root, text, true), '统计异常状态缺少：' + text);
        assertUi(!descendants(tree, root).some(entry => isVisible(entry) && typeOf(entry).includes('TreeDataGrid')),
            '数据库异常时统计数据表不应可见');
        return { navigationMs };
    });

    await runStep('database.statistics-retry', '统计页重试失败通知', async () => {
        await activateTextWithin(connection, 'StatisticsView', '重试连接');
        const result = await waitForMessage(connection, '数据库仍不可用', '本地记录不会因连接失败被删除');
        await dismissMessages(connection);
        return { notificationMs: result.elapsedMs };
    });

    await runStep('database.performance', '异常状态导航与视觉树响应速度', async () => {
        const samples = [];
        for (const [label, viewType] of [
            ['日记记录', 'DiaryEditorView'],
            ['事项查询', 'WorkItemQueryView'],
            ['统计工具', 'StatisticsView'],
            ['日记记录', 'DiaryEditorView'],
            ['统计工具', 'StatisticsView'],
        ])
            samples.push(await connection.navigate(label, viewType));
        const treeSamples = [];
        for (let index = 0; index < 5; index++) {
            const started = performance.now();
            await connection.getTree();
            treeSamples.push(performance.now() - started);
        }
        const navigationP95 = percentile(samples, 0.95);
        const treeP95 = percentile(treeSamples, 0.95);
        if (navigationP95 > 1000)
            addFinding('warning', 'database-error-navigation-slow', '数据库异常状态导航 P95 超过 1 秒', { samples });
        if (treeP95 > 250)
            addFinding('warning', 'database-error-tree-slow', '数据库异常状态视觉树读取 P95 超过 250 毫秒', { treeSamples });
        return { navigationSamplesMs: samples, navigationP95, treeSamplesMs: treeSamples, treeP95 };
    });
});
