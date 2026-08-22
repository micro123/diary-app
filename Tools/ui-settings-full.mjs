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
    isChecked,
    isVisible,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function textWithin(tree, root, text, contains = false) {
    if (!root)
        return null;
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry)
        && (contains ? textOf(entry).includes(text) : textOf(entry) === text));
}

async function activateControl(connection, control) {
    assertUi(control, '控件不存在');
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
}

async function activateTextWithin(connection, typeName, text) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '页面或对话框不可见：' + typeName);
    const label = textWithin(tree, root, text);
    assertUi(label, typeName + ' 缺少操作：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '操作不可激活：' + text);
    await activateControl(connection, control);
    return control;
}

async function dismissOnboarding(connection, button = '稍后再看') {
    const tree = await connection.getTree();
    if (!rootOf(tree, 'OnboardingView'))
        return false;
    await activateTextWithin(connection, 'OnboardingView', button);
    await connection.waitForTree(current => !rootOf(current, 'OnboardingView'), 8000,
        '首次使用引导没有关闭');
    return true;
}

async function openSettings(connection) {
    await connection.openSettingsMenuItem('ProgramSettingsMenuItem');
    const result = await connection.waitForTree(tree => rootOf(tree, 'SettingsView'),
        8000, '程序设置未打开');
    return result.value;
}

async function closeSettings(connection, button = '关闭') {
    await activateTextWithin(connection, 'SettingsView', button);
    await connection.waitForTree(tree => !rootOf(tree, 'SettingsView'), 8000,
        '程序设置未关闭');
}

async function expandGroup(connection, groupName, expectedText) {
    let tree = await connection.getTree();
    const settings = rootOf(tree, 'SettingsView');
    assertUi(settings, '程序设置不可见');
    const label = textWithin(tree, settings, groupName);
    assertUi(label, '设置分组不存在：' + groupName);
    const expander = ancestor(tree, label, entry => typeOf(entry).includes('Expander'));
    assertUi(expander, '设置分组缺少 Expander：' + groupName);
    const toggle = descendants(tree, expander).find(entry => entry.a.Name === 'ExpanderHeader');
    assertUi(toggle, '设置分组缺少展开控件：' + groupName);
    await connection.client.send('DOM.focus', { nodeId: toggle.nodeId });
    if (!isChecked(toggle))
        await connection.pressKey('Enter', 'Enter', 13);
    return connection.waitForTree(current => {
        const currentSettings = rootOf(current, 'SettingsView');
        return textWithin(current, currentSettings, expectedText, true);
    }, 5000, '设置分组没有展开：' + groupName);
}

function settingItem(tree, settings, labelText, modelType) {
    const label = textWithin(tree, settings, labelText);
    if (!label)
        return null;
    return ancestor(tree, label, entry => textOf(entry).includes(modelType));
}

function settingToggle(tree, settings, labelText) {
    const item = settingItem(tree, settings, labelText, 'Diary.GUIBase.ViewModels.SettingSwitch');
    return item && descendants(tree, item).find(entry => typeOf(entry).includes('ToggleSwitch'));
}

async function setDeveloperFeatures(connection, enabled) {
    await expandGroup(connection, '视图设置', '显示开发者功能:');
    let tree = await connection.getTree();
    const settings = rootOf(tree, 'SettingsView');
    const toggle = settingToggle(tree, settings, '显示开发者功能:');
    assertUi(toggle, '找不到显示开发者功能开关');
    if (isChecked(toggle) !== enabled) {
        await connection.client.send('DOM.focus', { nodeId: toggle.nodeId });
        await connection.pressKey('Space', 'Space', 32);
        await connection.waitForTree(current => {
            const currentSettings = rootOf(current, 'SettingsView');
            const currentToggle = settingToggle(current, currentSettings, '显示开发者功能:');
            return currentToggle && isChecked(currentToggle) === enabled;
        }, 3000, '显示开发者功能开关没有切换');
    }
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

await runUiSuite({ name: 'ui-settings-full', scenario: 'default', timeoutMs: 10000 }, async ({
    connection, runStep, addFinding,
}) => {
    await runStep('settings.onboarding-startup', '首次使用引导内容与安全关闭', async () => {
        const tree = await connection.getTree();
        const onboarding = rootOf(tree, 'OnboardingView');
        assertUi(onboarding, '全新 profile 未显示首次使用引导');
        for (const text of [
            '欢迎使用 DiaryApp',
            '1. 本地记录',
            '2. 远程同步',
            '3. 日常效率',
            '以后不再显示此引导',
            '稍后再看',
            '打开数据库设置',
            '开始记录',
        ])
            assertUi(textWithin(tree, onboarding, text, true), '首次使用引导缺少：' + text);
        await dismissOnboarding(connection);
        return { dismissed: true };
    });

    await runStep('settings.groups', '程序设置分组和关键字段', async () => {
        await openSettings(connection);
        const groups = [
            ['视图设置', ['界面字体:', '默认配色主题:', '始终显示托盘:', '隐藏到托盘:', '显示开发者功能:', '重新打开']],
            ['工作设置', ['默认事项名称:']],
            ['数据库设置', ['数据库驱动:', '配置', '创建备份', '选择备份', '迁移向导']],
            ['调查统计功能设置', ['启用调查功能:', '作为调查者:', '调查者 IP 地址:']],
            ['应用更新', ['自动检查更新:', '更新服务器地址:', '更新频道:', '安装包类型:', '立即检查']],
        ];
        for (const [group, expected] of groups) {
            await expandGroup(connection, group, expected[0]);
            const tree = await connection.getTree();
            const settings = rootOf(tree, 'SettingsView');
            for (const text of expected)
                assertUi(textWithin(tree, settings, text, true), group + ' 缺少：' + text);
        }
        const tree = await connection.getTree();
        const settings = rootOf(tree, 'SettingsView');
        for (const text of ['打开当前日志', '导出日志', '保存', '关闭'])
            assertUi(textWithin(tree, settings, text), '程序设置底部缺少：' + text);
        await closeSettings(connection);
        return { groups: groups.length };
    });

    await runStep('settings.discard', '关闭程序设置丢弃未保存修改', async () => {
        await openSettings(connection);
        await setDeveloperFeatures(connection, true);
        await closeSettings(connection, '关闭');
        let tree = await connection.getTree();
        assertUi(!findByText(tree, '脚本管理'), '关闭后不应应用开发者导航');
        await openSettings(connection);
        await expandGroup(connection, '视图设置', '显示开发者功能:');
        tree = await connection.getTree();
        const settings = rootOf(tree, 'SettingsView');
        const toggle = settingToggle(tree, settings, '显示开发者功能:');
        assertUi(toggle && !isChecked(toggle), '未保存的开发者功能开关没有被丢弃');
        await closeSettings(connection);
        return { discarded: true };
    });

    await runStep('settings.save-navigation', '保存开发者设置并动态重建导航', async () => {
        await openSettings(connection);
        await setDeveloperFeatures(connection, true);
        await closeSettings(connection, '保存');
        const enabled = await connection.waitForTree(tree => findByText(tree, '脚本管理'),
            8000, '保存后脚本管理导航未出现');
        await openSettings(connection);
        await setDeveloperFeatures(connection, false);
        await closeSettings(connection, '保存');
        const disabled = await connection.waitForTree(tree => !findByText(tree, '脚本管理'),
            8000, '恢复设置后脚本管理导航未移除');
        return { enableMs: enabled.elapsedMs, disableMs: disabled.elapsedMs };
    });

    await runStep('settings.onboarding-reopen', '从程序设置重新打开首次使用引导', async () => {
        await openSettings(connection);
        await expandGroup(connection, '视图设置', '重新打开');
        await activateTextWithin(connection, 'SettingsView', '重新打开');
        const result = await connection.waitForTree(tree => rootOf(tree, 'OnboardingView'),
            8000, '首次使用引导没有重新打开');
        assertUi(textWithin(result.tree, result.value, '欢迎使用 DiaryApp'), '重新打开的引导内容不完整');
        await dismissOnboarding(connection);
        assertUi(rootOf(await connection.getTree(), 'SettingsView'), '关闭引导后程序设置不应丢失');
        await closeSettings(connection);
        return { openMs: result.elapsedMs };
    });

    await runStep('settings.database-dialogs', '数据库配置和迁移向导安全取消', async () => {
        await openSettings(connection);
        await expandGroup(connection, '数据库设置', '配置');
        await activateTextWithin(connection, 'SettingsView', '配置');
        let result = await connection.waitForTree(tree => rootOf(tree, 'GenericConfigView'),
            8000, 'SQLite 数据库配置未打开');
        let tree = result.tree;
        let dialog = result.value;
        for (const text of ['数据库设置', '存储路径:', '保存', '放弃'])
            assertUi(textWithin(tree, dialog, text, true), '数据库配置缺少：' + text);
        await activateTextWithin(connection, 'GenericConfigView', '放弃');
        await connection.waitForTree(current => !rootOf(current, 'GenericConfigView'), 8000,
            '数据库配置未关闭');
        await activateTextWithin(connection, 'SettingsView', '迁移向导');
        result = await connection.waitForTree(current => rootOf(current, 'DbMigrationView'),
            8000, '数据迁移向导未打开');
        tree = result.tree;
        dialog = result.value;
        for (const text of ['数据迁移向导', '数据库类型:', '数据库文件:', '取消', '迁移'])
            assertUi(textWithin(tree, dialog, text, true), '迁移向导缺少：' + text);
        await activateTextWithin(connection, 'DbMigrationView', '取消');
        await connection.waitForTree(current => !rootOf(current, 'DbMigrationView'), 8000,
            '迁移向导未关闭');
        await closeSettings(connection);
        return { databaseConfig: true, migration: true, nativeBackupRestore: 'Manual-Native' };
    });

    await runStep('settings.log-export', '程序设置导出当前运行日志', async () => {
        await openSettings(connection);
        const temporaryDirectory = path.join(connection.state.profile, 'temp');
        const before = await fs.readdir(temporaryDirectory).catch(() => []);
        await activateTextWithin(connection, 'SettingsView', '导出日志');
        const toast = await connection.waitForTree(tree => findByTextContains(tree, '日志已导出：'),
            8000, '程序设置没有显示日志导出结果');
        const after = await fs.readdir(temporaryDirectory).catch(() => []);
        const archives = after.filter(name => name.startsWith('DiaryApp-logs-')
            && name.endsWith('.zip') && !before.includes(name));
        assertUi(archives.length > 0, '程序设置导出日志后没有生成新压缩包');
        const stat = await fs.stat(path.join(temporaryDirectory, archives.at(-1)));
        assertUi(stat.size > 0, '程序设置导出的日志压缩包为空');
        await closeSettings(connection);
        return { toastMs: toast.elapsedMs, archiveBytes: stat.size };
    });

    await runStep('settings.update-check', '应用更新配置和手动检查', async () => {
        await openSettings(connection);
        await expandGroup(connection, '应用更新', '立即检查');
        const dataDirectory = path.join(connection.state.profile, 'data');
        const logName = (await fs.readdir(dataDirectory)).find(name => /^Diary\.App.*\.log$/i.test(name));
        assertUi(logName, '找不到应用运行日志');
        const logPath = path.join(dataDirectory, logName);
        const beforeLog = await fs.readFile(logPath, 'utf8');
        await activateTextWithin(connection, 'SettingsView', '立即检查');
        const started = await connection.waitForTree(tree => findByText(tree, '正在检查更新…'),
            5000, '未显示正在检查更新状态');
        const completedStarted = performance.now();
        let resultText = '';
        let completed = false;
        while (performance.now() - completedStarted < 30000) {
            const tree = await connection.getTree();
            const visibleResult = findByTextContains(tree, '更新服务器没有当前平台和包类型的发布快照')
                ?? findByTextContains(tree, '更新服务器暂时不可用')
                ?? findByTextContains(tree, '连接更新服务器超时')
                ?? findByTextContains(tree, '检查更新失败')
                ?? findByTextContains(tree, '当前已是最新版本');
            if (visibleResult) {
                resultText = textOf(visibleResult);
                completed = true;
                break;
            }
            const currentLog = await fs.readFile(logPath, 'utf8');
            if (currentLog.slice(beforeLog.length).includes('应用更新检查完成')) {
                resultText = '运行日志确认检查完成';
                completed = true;
                break;
            }
            await delay(50);
        }
        assertUi(completed, '手动更新检查没有给出结果');
        await closeSettings(connection);
        return {
            startedMs: started.elapsedMs,
            completedMs: performance.now() - completedStarted,
            result: resultText,
        };
    });

    await runStep('settings.performance', '程序设置打开、分组展开和关闭响应速度', async () => {
        const samples = [];
        for (let index = 0; index < 3; index++) {
            const started = performance.now();
            await openSettings(connection);
            await expandGroup(connection, '视图设置', '界面字体:');
            await closeSettings(connection);
            samples.push(performance.now() - started);
        }
        const p95 = percentile(samples, 0.95);
        if (p95 > 2000)
            addFinding('warning', 'settings-open-slow', '程序设置打开/关闭 P95 超过 2 秒', { samples });
        return { samplesMs: samples, p95 };
    });
});
