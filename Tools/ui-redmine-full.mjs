#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findAllByText,
    findByName,
    findByText,
    findByTextContains,
    hasAncestorType,
    isChecked,
    isEnabled,
    isVisible,
    nameOf,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const ctrl = 2;
const stamp = Date.now().toString(36);
const createdIssueTitle = 'UI全量Redmine-' + stamp;
const createdIssueDescription = 'DiaryApp UI 自动化创建；用于测试管理、导入与工时同步。';
const automationTagName = 'UI规则-' + stamp;
const existingTagName = 'UI已有标签-' + stamp;
const taggedTemplateName = 'UI标签模板-' + stamp;
const taggedTemplateTitle = 'UI模板内容-' + stamp;
const retainedDraftTitle = 'UI模板更新保留-' + stamp;
const workTitle = 'UI工时同步-' + stamp;
let createdIssueId = null;

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function isEffectivelyEnabled(tree, entry) {
    return Boolean(entry) && !ancestor(tree, entry, current => !isEnabled(current));
}

function textWithin(tree, root, text, contains = false) {
    if (!root)
        return null;
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry)
        && (contains ? textOf(entry).includes(text) : textOf(entry) === text));
}

function allTextWithin(tree, root, text, contains = false) {
    if (!root)
        return [];
    return [root, ...descendants(tree, root)].filter(entry => isVisible(entry)
        && (contains ? textOf(entry).includes(text) : textOf(entry) === text));
}

async function activateControl(connection, control) {
    assertUi(control, '控件不存在');
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
}

async function activateTextWithin(connection, typeName, text, contains = false) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '页面或对话框不可见：' + typeName);
    const label = textWithin(tree, root, text, contains);
    assertUi(label, typeName + ' 缺少操作：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '操作不可激活：' + text);
    await activateControl(connection, control);
    return control;
}

async function openSettingsText(connection, text, expectedType) {
    let menu;
    let lastError;
    for (let attempt = 0; attempt < 3; attempt++) {
        const tree = await connection.getTree();
        const button = findByName(tree, 'SettingsMenuButton');
        assertUi(button, '设置按钮不存在');
        if (attempt === 0)
            await connection.clickNode(button);
        else
            await activateControl(connection, button);
        try {
            menu = await connection.waitForTree(current => {
                const label = findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'));
                const item = label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
                return label && item ? { label, item } : null;
            }, 1800, '设置菜单项未出现：' + text);
            break;
        }
        catch (error) {
            lastError = error;
            await connection.pressKey('Escape', 'Escape', 27);
            await delay(80);
        }
    }
    if (!menu)
        throw lastError;
    await connection.clickNode(menu.value.label);
    return connection.waitForTree(current => rootOf(current, expectedType), 10000,
        '设置对话框未出现：' + expectedType);
}

async function selectTab(connection, labelText, expectedType) {
    const tree = await connection.getTree();
    const label = findByText(tree, labelText, entry => hasAncestorType(tree, entry, 'TabItem'));
    assertUi(label, '找不到页签：' + labelText);
    const tab = ancestor(tree, label, entry => typeOf(entry).includes('TabItem'));
    await connection.clickNode(tab);
    if (expectedType)
        await connection.waitForTree(current => rootOf(current, expectedType), 8000,
            '页签内容未出现：' + labelText);
}

async function selectComboOption(connection, combo, optionText, contains = false) {
    let lastError;
    for (let attempt = 0; attempt < 2; attempt++) {
        await connection.pressKey('Escape', 'Escape', 27);
        await connection.clickNode(combo);
        try {
            for (let page = 0; page < 10; page++) {
                const tree = await connection.getTree();
                const label = tree.entries.find(entry => isVisible(entry)
                    && hasAncestorType(tree, entry, 'ComboBoxItem')
                    && (contains ? textOf(entry).includes(optionText) : textOf(entry) === optionText));
                const option = label && ancestor(tree, label, entry => typeOf(entry).includes('ComboBoxItem'));
                if (option) {
                    await connection.clickNode(option);
                    await delay(60);
                    return;
                }
                await connection.pressKey('PageDown', 'PageDown', 34);
                await delay(80);
            }
            throw new Error('下拉选项未出现：' + optionText);
        }
        catch (error) {
            lastError = error;
            await connection.pressKey('Escape', 'Escape', 27);
            await delay(100);
        }
    }
    throw lastError;
}

async function selectFirstComboOption(connection, combo, rejectedTexts) {
    await connection.pressKey('Escape', 'Escape', 27);
    await connection.clickNode(combo);
    const option = await connection.waitForTree(tree => {
        const items = tree.entries.filter(entry => isVisible(entry)
            && typeOf(entry).includes('ComboBoxItem'));
        return items.find(item => {
            const value = textOf(item);
            return value && !rejectedTexts.some(rejected => value.includes(rejected));
        });
    }, 5000, '没有可选择的下拉选项');
    const selectedText = textOf(option.value);
    await connection.clickNode(option.value);
    await delay(60);
    return selectedText;
}

async function selectMenuOption(connection, trigger, optionText, message) {
    await connection.pressKey('Escape', 'Escape', 27);
    await connection.clickNode(trigger);
    for (let attempt = 0; attempt < 2; attempt++) {
        for (let page = 0; page < 10; page++) {
            const tree = await connection.getTree();
            const label = findByText(tree, optionText,
                entry => hasAncestorType(tree, entry, 'MenuItem'));
            const item = label && ancestor(tree, label, entry => typeOf(entry).includes('MenuItem'));
            if (item) {
                await connection.clickNode(item);
                await delay(60);
                return;
            }
            await connection.pressKey('PageDown', 'PageDown', 34);
            await delay(80);
        }
        await connection.pressKey('Escape', 'Escape', 27);
        await connection.client.send('DOM.focus', { nodeId: trigger.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        await delay(100);
    }
    throw new Error(message);
}

async function waitForStableNamedCount(connection, rootType, name, timeoutMs = 3000) {
    const started = performance.now();
    let previousCount = -1;
    let stableSamples = 0;
    while (performance.now() - started < timeoutMs) {
        const tree = await connection.getTree();
        const root = rootOf(tree, rootType);
        const count = root
            ? descendants(tree, root).filter(entry => isVisible(entry) && nameOf(entry) === name).length
            : -1;
        stableSamples = count === previousCount ? stableSamples + 1 : 0;
        if (root && stableSamples >= 3)
            return { tree, root, count };
        previousCount = count;
        await delay(80);
    }
    throw new Error(`${rootType} 中的 ${name} 数量没有稳定`);
}

async function dismissStandardMessage(connection) {
    const tree = await connection.getTree();
    const buttons = tree.entries.filter(entry => isVisible(entry) && nameOf(entry) === 'PART_OKButton');
    const button = buttons.at(-1);
    assertUi(button, '标准消息缺少确定按钮');
    await activateControl(connection, button);
    await connection.waitForTree(current => !current.entries.some(entry => isVisible(entry)
        && typeOf(entry).includes('StandardMessageView')), 8000, '标准消息没有关闭');
}

function rowContaining(tree, text) {
    const label = tree.entries.find(entry => isVisible(entry) && textOf(entry).includes(text));
    return label && ancestor(tree, label, entry => typeOf(entry).includes('DataGridRow'));
}

async function clickRowButton(connection, rowText, buttonText) {
    const tree = await connection.getTree();
    const row = rowContaining(tree, rowText);
    assertUi(row, '找不到数据行：' + rowText);
    const label = descendants(tree, row).find(entry => isVisible(entry) && textOf(entry) === buttonText);
    assertUi(label, '数据行缺少操作：' + buttonText);
    const button = controlForText(tree, label);
    await activateControl(connection, button);
}

async function closeTrackerSettings(connection, button = '取消') {
    await activateTextWithin(connection, 'TrackerSettingsDialogView', button);
    await connection.waitForTree(tree => !rootOf(tree, 'TrackerSettingsDialogView'), 10000,
        'Tracker 设置没有关闭');
}

async function convergeStartupState(connection) {
    const closed = [];
    for (let attempt = 0; attempt < 8; attempt++) {
        const tree = await connection.getTree();
        if (rootOf(tree, 'TrackerSettingsDialogView')) {
            await activateTextWithin(connection, 'TrackerSettingsDialogView', '取消');
            closed.push('TrackerSettingsDialogView');
        }
        else if (rootOf(tree, 'TagEditorView')) {
            await activateControl(connection, findByName(tree, 'SaveTagSettingsButton'));
            closed.push('TagEditorView');
        }
        else if (rootOf(tree, 'StandardMessageView')) {
            const ok = tree.entries.filter(entry => isVisible(entry)
                && nameOf(entry) === 'PART_OKButton').at(-1);
            assertUi(ok, '残留标准消息缺少确定按钮');
            await activateControl(connection, ok);
            closed.push('StandardMessageView');
        }
        else if (rootOf(tree, 'OnboardingView')) {
            await activateTextWithin(connection, 'OnboardingView', '稍后再看');
            closed.push('OnboardingView');
        }
        else {
            break;
        }
        await delay(120);
    }
    return closed;
}

function textWithinNamedControl(tree, name) {
    const control = findByName(tree, name);
    if (!control)
        return '';
    return [control, ...descendants(tree, control)].map(textOf).filter(Boolean).join(' ');
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

await runUiSuite({ name: 'ui-redmine-full', scenario: 'plugins', timeoutMs: 12000, stopOnFailure: true }, async ({
    connection, runStep, addFinding,
}) => {
    assertUi(connection.state.withPlugins, 'Redmine 套件必须以 -WithPlugins 启动');
    assertUi(connection.state.seeded, 'Redmine 套件必须使用加密 seed profile');
    await convergeStartupState(connection);

    await runStep('redmine.tracker-settings', '多 Tracker 设置、Redmine 配置与插件状态', async () => {
        await openSettingsText(connection, 'Tracker 设置', 'TrackerSettingsDialogView');
        let tree = await connection.getTree();
        let dialog = rootOf(tree, 'TrackerSettingsDialogView');
        for (const text of ['Tracker 配置', '插件状态', 'Jira', 'Redmine', '取消', '保存'])
            assertUi(textWithin(tree, dialog, text, true), 'Tracker 设置缺少：' + text);
        await selectTab(connection, 'Redmine', 'RedMineConfigurationView');
        tree = await connection.getTree();
        const redmine = rootOf(tree, 'RedMineConfigurationView');
        for (const text of [
            'Redmine Tracker 实例',
            '添加实例',
            '删除实例',
            '实例配置',
            '显示名称:',
            '导航图标:',
            '启用此实例:',
            '服务地址:',
            'API Key:',
            '使用代理服务器:',
            '标签自动规则',
            '添加规则',
        ])
            assertUi(textWithin(tree, redmine, text, true), 'Redmine 配置缺少：' + text);
        const apiLabel = textWithin(tree, redmine, 'API Key:');
        const apiItem = ancestor(tree, apiLabel, entry => textOf(entry).includes('SettingText'));
        const apiInput = apiItem && descendants(tree, apiItem).find(entry => typeOf(entry).includes('TextBox'));
        assertUi(apiInput, 'API Key 缺少输入控件');
        assertUi(String(apiInput.a.Class ?? '').split(/\s+/).includes('RevealPasswordButton'),
            'API Key 未启用敏感文本编辑器');
        await activateTextWithin(connection, 'RedMineConfigurationView', '添加实例');
        await connection.waitForTree(current => findByText(current, 'Redmine 实例 2'), 5000,
            '添加第二个 Redmine 实例失败');
        tree = await connection.getTree();
        const remove = controlForText(tree, textWithin(tree, rootOf(tree, 'RedMineConfigurationView'), '删除实例'));
        assertUi(remove && isEnabled(remove), '新增实例后删除实例按钮未启用');
        await activateControl(connection, remove);
        await connection.waitForTree(current => !findByText(current, 'Redmine 实例 2'), 5000,
            '删除临时 Redmine 实例失败');
        await selectTab(connection, '插件状态', 'TrackerPluginDiagnosticsView');
        tree = await connection.getTree();
        const diagnostics = rootOf(tree, 'TrackerPluginDiagnosticsView');
        for (const text of ['Tracker 插件状态', 'Redmine 测试服', '运行中', 'jira.default', '已禁用'])
            assertUi(textWithin(tree, diagnostics, text, true), '插件状态缺少：' + text);
        await closeTrackerSettings(connection);
        return { providers: 2, temporaryInstanceRoundTrip: true, redmineState: '运行中' };
    });

    await runStep('redmine.management-shell', 'Redmine 动态导航、用户信息与管理页结构', async () => {
        const navigationMs = await connection.navigate('Redmine 测试服', 'RedMineManageView');
        const ready = await connection.waitForTree(tree => {
            const root = rootOf(tree, 'RedMineManageView');
            return root && textWithin(tree, root, '基本信息') && !textWithin(tree, root, '无法连接服务器', true);
        }, 10000, 'Redmine 管理页未连接测试服');
        let tree = ready.tree;
        const manage = rootOf(tree, 'RedMineManageView');
        for (const text of ['基本信息', '问题管理', '项目管理', '用户信息', '活动列表', '已导入的问题'])
            assertUi(textWithin(tree, manage, text, true), 'Redmine 管理页缺少：' + text);
        const userHeader = textWithin(tree, rootOf(tree, 'RedMineInfoView'), '用户信息');
        const userExpander = ancestor(tree, userHeader, entry => typeOf(entry).includes('Expander'));
        const userToggle = descendants(tree, userExpander).find(entry => nameOf(entry) === 'ExpanderHeader');
        await activateControl(connection, userToggle);
        tree = (await connection.waitForTree(current => findByText(current, '用户ID：'), 5000,
            'Redmine 用户信息没有展开')).tree;
        const info = rootOf(tree, 'RedMineInfoView');
        for (const text of ['用户ID：', '登录名：', '用户名：'])
            assertUi(textWithin(tree, info, text), '用户信息缺少：' + text);
        return { navigationMs };
    });

    await runStep('redmine.activities', '同步 Redmine 活动定义', async () => {
        let tree = await connection.getTree();
        const info = rootOf(tree, 'RedMineInfoView');
        const header = textWithin(tree, info, '活动列表');
        const expander = ancestor(tree, header, entry => typeOf(entry).includes('Expander'));
        const toggle = descendants(tree, expander).find(entry => nameOf(entry) === 'ExpanderHeader');
        if (!isChecked(toggle))
            await activateControl(connection, toggle);
        await connection.waitForTree(current => findByText(current, '同步服务器定义'), 5000,
            '活动同步入口没有出现');
        const started = performance.now();
        await activateTextWithin(connection, 'RedMineInfoView', '同步服务器定义');
        const synced = await connection.waitForTree(current => {
            const currentInfo = rootOf(current, 'RedMineInfoView');
            const currentHeader = textWithin(current, currentInfo, '活动列表');
            const currentExpander = ancestor(current, currentHeader, entry => typeOf(entry).includes('Expander'));
            const ids = descendants(current, currentExpander).filter(entry => /^#\d+$/.test(textOf(entry)));
            return ids.length > 0 ? ids : null;
        }, 10000, '活动定义没有同步到本地');
        return { syncMs: performance.now() - started, activityCount: synced.value.length };
    });

    await runStep('redmine.project-create', '项目搜索、说明查看和创建 Issue', async () => {
        await selectTab(connection, '项目管理', 'RedMineProjectView');
        let tree = await connection.getTree();
        let view = rootOf(tree, 'RedMineProjectView');
        for (const text of ['搜索', '首页', '上一页', '下一页', '尾页'])
            assertUi(textWithin(tree, view, text, true), '项目管理缺少：' + text);
        const started = performance.now();
        await activateTextWithin(connection, 'RedMineProjectView', '搜索');
        const results = await connection.waitForTree(current => {
            const currentView = rootOf(current, 'RedMineProjectView');
            return textWithin(current, currentView, '共检索到', true)
                && textWithin(current, currentView, '新建问题') ? currentView : null;
        }, 10000, '项目搜索没有返回结果');
        tree = results.tree;
        view = results.value;
        const descriptionButtons = descendants(tree, view).filter(entry => isVisible(entry)
            && typeOf(entry).includes('Button') && !textOf(entry));
        if (descriptionButtons.length > 0) {
            await activateControl(connection, descriptionButtons[0]);
            await connection.waitForTree(current => rootOf(current, 'StandardMessageView'), 5000,
                '项目说明没有打开');
            await dismissStandardMessage(connection);
        }
        await activateTextWithin(connection, 'RedMineProjectView', '新建问题');
        const dialogResult = await connection.waitForTree(current => rootOf(current, 'NewIssueView'),
            8000, '创建 Issue 对话框未出现');
        tree = dialogResult.tree;
        const newIssue = dialogResult.value;
        const inputs = descendants(tree, newIssue).filter(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox'));
        assertUi(inputs.length >= 2, '创建 Issue 对话框缺少标题或说明输入框');
        await connection.replaceText(inputs[0], createdIssueTitle);
        tree = await connection.getTree();
        const currentIssue = rootOf(tree, 'NewIssueView');
        const currentInputs = descendants(tree, currentIssue).filter(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox'));
        await connection.replaceText(currentInputs[1], createdIssueDescription);
        tree = await connection.getTree();
        const ok = tree.entries.find(entry => isVisible(entry) && nameOf(entry) === 'PART_OKButton');
        await activateControl(connection, ok);
        const created = await connection.waitForTree(current => {
            const message = rootOf(current, 'StandardMessageView');
            return message && textWithin(current, message, '新问题ID为:', true) ? message : null;
        }, 15000, 'Redmine Issue 创建结果未出现');
        const body = descendants(created.tree, created.value).map(textOf).find(text => text.includes('新问题ID为:')) ?? '';
        const match = body.match(/新问题ID为:\s*(\d+)/);
        assertUi(match, 'Issue 创建成功消息缺少远程 ID');
        createdIssueId = Number(match[1]);
        await dismissStandardMessage(connection);
        return { searchMs: results.elapsedMs, createMs: created.elapsedMs, created: true };
    });

    await runStep('redmine.issue-search-import', 'Issue 关键字/ID 搜索和即时导入', async () => {
        assertUi(createdIssueId > 0, '缺少刚创建的 Issue ID');
        await selectTab(connection, '问题管理', 'RedMineIssueManageView');
        let tree = await connection.getTree();
        let view = rootOf(tree, 'RedMineIssueManageView');
        let searchInput = descendants(tree, view).find(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox'));
        assertUi(searchInput, '问题管理缺少搜索输入框');
        await connection.replaceText(searchInput, createdIssueTitle);
        const keywordStarted = performance.now();
        await connection.pressKey('Enter', 'Enter', 13);
        await connection.waitForTree(current => textWithin(current, rootOf(current, 'RedMineIssueManageView'), createdIssueTitle, true),
            12000, '关键字搜索没有找到刚创建的 Issue');
        const keywordMs = performance.now() - keywordStarted;
        tree = await connection.getTree();
        view = rootOf(tree, 'RedMineIssueManageView');
        searchInput = descendants(tree, view).find(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox'));
        await connection.replaceText(searchInput, String(createdIssueId));
        const idStarted = performance.now();
        await connection.pressKey('Enter', 'Enter', 13, ctrl);
        await connection.waitForTree(current => textWithin(current, rootOf(current, 'RedMineIssueManageView'), createdIssueTitle, true),
            12000, '按 ID 搜索没有找到刚创建的 Issue');
        const idMs = performance.now() - idStarted;
        const importIssue = async useKeyboard => {
            const current = await connection.getTree();
            const currentView = rootOf(current, 'RedMineIssueManageView');
            const label = textWithin(current, currentView, '导入问题');
            const button = label && controlForText(current, label);
            assertUi(button, '问题管理缺少导入问题按钮');
            if (useKeyboard)
                await activateControl(connection, button);
            else
                await connection.clickNode(button);
            await delay(400);
        };
        await importIssue(false);
        await selectTab(connection, '基本信息', 'RedMineInfoView');
        let imported;
        try {
            imported = await connection.waitForTree(current =>
                textWithin(current, rootOf(current, 'RedMineInfoView'), createdIssueTitle, true),
            3000, '导入 Issue 后基本信息页没有即时刷新');
        }
        catch {
            await selectTab(connection, '问题管理', 'RedMineIssueManageView');
            await importIssue(true);
            await selectTab(connection, '基本信息', 'RedMineInfoView');
            imported = await connection.waitForTree(current =>
                textWithin(current, rootOf(current, 'RedMineInfoView'), createdIssueTitle, true),
            10000, '导入 Issue 后基本信息页没有即时刷新');
        }
        return { keywordMs, idMs, importObservedMs: imported.elapsedMs };
    });

    await runStep('redmine.issue-maintenance', '已导入 Issue 同步、启停和删除边界', async () => {
        await activateTextWithin(connection, 'RedMineInfoView', '与服务器同步');
        await delay(200);
        await activateTextWithin(connection, 'RedMineInfoView', '重新抓取');
        await connection.waitForTree(current => textWithin(current, rootOf(current, 'RedMineInfoView'), createdIssueTitle, true),
            8000, '重新抓取后导入 Issue 丢失');
        let tree = await connection.getTree();
        let row = rowContaining(tree, createdIssueTitle);
        assertUi(row, '找不到已导入 Issue 行');
        let toggleLabel = descendants(tree, row).find(entry => isVisible(entry)
            && ['关闭', '打开'].includes(textOf(entry)));
        assertUi(toggleLabel, '已导入 Issue 缺少启停操作');
        const firstState = textOf(toggleLabel);
        await activateControl(connection, controlForText(tree, toggleLabel));
        const opposite = firstState === '关闭' ? '打开' : '关闭';
        await connection.waitForTree(current => {
            const currentRow = rowContaining(current, createdIssueTitle);
            return currentRow && descendants(current, currentRow).some(entry => textOf(entry) === opposite);
        }, 8000, 'Issue 本地启停状态没有切换');
        tree = await connection.getTree();
        row = rowContaining(tree, createdIssueTitle);
        toggleLabel = descendants(tree, row).find(entry => isVisible(entry) && textOf(entry) === opposite);
        await activateControl(connection, controlForText(tree, toggleLabel));
        await connection.waitForTree(current => {
            const currentRow = rowContaining(current, createdIssueTitle);
            return currentRow && descendants(current, currentRow).some(entry => textOf(entry) === firstState);
        }, 8000, 'Issue 本地启停状态没有恢复');
        await clickRowButton(connection, createdIssueTitle, '删除');
        const toast = await connection.waitForTree(current => findByText(current, '暂时不支持删除！'),
            5000, '删除边界提示未出现');
        return { syncAndReload: true, toggleRoundTrip: true, deleteBoundaryMs: toast.elapsedMs };
    });

    await runStep('redmine.tag-create', '创建标签供 Redmine 自动化规则使用', async () => {
        await openSettingsText(connection, '标签设置', 'TagEditorView');
        let tree = await connection.getTree();
        for (const tagName of [automationTagName, existingTagName]) {
            const input = findByName(tree, 'TagNameInput');
            assertUi(input, '标签编辑器缺少名称输入框');
            await connection.replaceText(input, tagName);
            tree = await connection.getTree();
            await activateControl(connection, findByName(tree, 'AddTagButton'));
            const created = await connection.waitForTree(current => findByText(current, tagName), 8000,
                '自动化测试标签没有创建：' + tagName);
            tree = created.tree;
        }
        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'SaveTagSettingsButton'));
        await connection.waitForTree(current => !rootOf(current, 'TagEditorView'), 10000,
            '标签设置没有保存关闭');
        return { automationTagCreated: true, existingTagCreated: true };
    });

    await runStep('redmine.tag-rule', '配置 Redmine 标签自动规则', async () => {
        await openSettingsText(connection, 'Tracker 设置', 'TrackerSettingsDialogView');
        await selectTab(connection, 'Redmine', 'RedMineConfigurationView');
        const loadedRules = await connection.waitForTree(current => {
            const currentEditor = rootOf(current, 'RedMineTagRuleEditorView');
            const addLabel = textWithin(current, currentEditor, '添加规则');
            const addButton = addLabel && controlForText(current, addLabel);
            return addButton && isEnabled(addButton) ? currentEditor : null;
        }, 5000, 'Redmine 标签规则编辑器没有完成加载');
        const stableRules = await waitForStableNamedCount(
            connection, 'RedMineTagRuleEditorView', 'TagRuleCard');
        let tree = stableRules.tree;
        let editor = stableRules.root;
        const existingRuleCount = stableRules.count;
        const addRuleLabel = textWithin(tree, editor, '添加规则');
        const addRuleButton = addRuleLabel && controlForText(tree, addRuleLabel);
        assertUi(addRuleButton, 'Redmine 标签规则缺少添加按钮');
        await connection.clickNode(addRuleButton);
        try {
            await connection.waitForTree(current => current.entries.filter(entry => isVisible(entry)
                && nameOf(entry) === 'TagRuleCard').length > existingRuleCount, 400);
        }
        catch {
            await connection.client.send('DOM.focus', { nodeId: addRuleButton.nodeId });
            await connection.pressKey('Enter', 'Enter', 13);
        }
        const added = await connection.waitForTree(current => {
            const currentEditor = rootOf(current, 'RedMineTagRuleEditorView');
            const rows = currentEditor && descendants(current, currentEditor).filter(entry => isVisible(entry)
                && nameOf(entry) === 'TagRuleCard');
            return rows?.length > existingRuleCount ? rows.at(-1) : null;
        }, 8000, 'Redmine 标签规则没有添加');
        let ruleCard = added.value;
        let tagCombo = descendants(added.tree, ruleCard).find(entry => nameOf(entry) === 'TagRuleTagComboBox');
        let activityCombo = descendants(added.tree, ruleCard).find(entry => nameOf(entry) === 'TagRuleActivityComboBox');
        let issueCombo = descendants(added.tree, ruleCard).find(entry => nameOf(entry) === 'TagRuleIssueComboBox');
        assertUi(tagCombo && activityCombo && issueCombo, '新 Redmine 标签规则缺少映射下拉框');
        await selectComboOption(connection, tagCombo, automationTagName);
        const selectedRule = await connection.waitForTree(current => {
            const currentEditor = rootOf(current, 'RedMineTagRuleEditorView');
            const cards = currentEditor && descendants(current, currentEditor).filter(entry => isVisible(entry)
                && nameOf(entry) === 'TagRuleCard');
            return cards?.find(card => textWithin(current, card, automationTagName)) ?? null;
        }, 5000, '新 Redmine 标签规则没有选中测试标签');
        tree = selectedRule.tree;
        editor = rootOf(tree, 'RedMineTagRuleEditorView');
        ruleCard = selectedRule.value;
        activityCombo = descendants(tree, ruleCard).find(entry => nameOf(entry) === 'TagRuleActivityComboBox');
        const activity = await selectFirstComboOption(connection, activityCombo, ['不设置活动']);
        tree = await connection.getTree();
        editor = rootOf(tree, 'RedMineTagRuleEditorView');
        ruleCard = descendants(tree, editor).filter(entry => isVisible(entry)
            && nameOf(entry) === 'TagRuleCard')
            .find(card => textWithin(tree, card, automationTagName));
        issueCombo = descendants(tree, ruleCard).find(entry => nameOf(entry) === 'TagRuleIssueComboBox');
        await selectComboOption(connection, issueCombo, createdIssueTitle, true);
        tree = await connection.getTree();
        editor = rootOf(tree, 'RedMineTagRuleEditorView');
        ruleCard = descendants(tree, editor).filter(entry => isVisible(entry)
            && nameOf(entry) === 'TagRuleCard')
            .find(card => textWithin(tree, card, automationTagName));
        const enabled = descendants(tree, ruleCard).find(entry => typeOf(entry).includes('CheckBox')
            && textOf(entry) === '启用');
        const forceOverwrite = descendants(tree, ruleCard).find(entry => typeOf(entry).includes('CheckBox')
            && textOf(entry) === '强制修改');
        assertUi(enabled && isChecked(enabled), '新 Redmine 标签规则默认应启用');
        assertUi(forceOverwrite && isChecked(forceOverwrite), '新 Redmine 标签规则默认应强制修改');
        assertUi(textWithin(tree, ruleCard, automationTagName), '新 Redmine 标签规则没有选中测试标签');
        assertUi(textWithin(tree, ruleCard, createdIssueTitle, true), '新 Redmine 标签规则没有选中测试 Issue');
        assertUi(!textWithin(tree, ruleCard, '不设置活动'), '新 Redmine 标签规则没有选中活动');
        await closeTrackerSettings(connection, '保存');
        await connection.waitForTree(current => findByText(current, 'Redmine 测试服'), 10000,
            '保存 Tracker 规则后 Redmine 导航未恢复');
        return { configured: true, activitySelected: Boolean(activity) };
    });

    await runStep('redmine.tagged-template-config', '创建带标签的事项模板', async () => {
        await openSettingsText(connection, '模板设置', 'TemplateEditorView');
        let tree = await connection.getTree();
        const nameInput = findByName(tree, 'TemplateNameInput');
        assertUi(nameInput, '模板编辑器缺少模板名称输入框');
        await connection.replaceText(nameInput, taggedTemplateName);
        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'AddTemplateButton'));
        const added = await connection.waitForTree(current => findByText(current, taggedTemplateName), 8000,
            '带标签模板没有创建');
        tree = added.tree;
        const templateText = findByText(tree, taggedTemplateName);
        const expander = templateText && ancestor(tree, templateText,
            entry => typeOf(entry).includes('Expander'));
        assertUi(expander, '找不到新建模板的展开区域');
        const header = descendants(tree, expander).find(entry => nameOf(entry) === 'ExpanderHeader');
        assertUi(header, '找不到新建模板的展开按钮');
        await activateControl(connection, header);
        const expanded = await connection.waitForTree(current => findByName(current, 'TemplateDefaultTitleInput'),
            8000, '模板详细配置没有展开');
        tree = expanded.tree;
        await connection.replaceText(findByName(tree, 'TemplateDefaultTitleInput'), taggedTemplateTitle);
        tree = await connection.getTree();
        await connection.replaceText(findByName(tree, 'TemplateDefaultTimeInput'), '0.5');
        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'TemplateAddTagButton'));
        const tagOption = await connection.waitForTree(current => {
            const label = findByText(current, automationTagName,
                entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '模板标签候选中没有自动化标签');
        await connection.clickNode(tagOption.value);
        await connection.waitForTree(current => {
            const currentTemplate = findByText(current, taggedTemplateName);
            const currentExpander = currentTemplate && ancestor(current, currentTemplate,
                entry => typeOf(entry).includes('Expander'));
            return currentExpander && descendants(current, currentExpander)
                .some(entry => textOf(entry) === automationTagName);
        }, 5000, '模板没有添加自动化标签');
        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'SaveTemplateSettingsButton'));
        await connection.waitForTree(current => !rootOf(current, 'TemplateEditorView'), 10000,
            '模板设置没有保存关闭');
        return { template: taggedTemplateName, tag: automationTagName, defaultTime: 0.5 };
    });

    await runStep('redmine.tagged-template-update-apply', '验证带标签模板更新和应用', async () => {
        await connection.navigate('日记记录', 'DiaryEditorView');
        let tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'NewWorkItemButton'));
        await connection.waitForTree(current => findByName(current, 'WorkTitleInput'), 8000,
            '新建事项没有打开编辑器');

        tree = await connection.getTree();
        let templateAction = findByName(tree, 'TemplateActionButton');
        assertUi(templateAction, '缺少从模板更新按钮');
        await connection.clickNode(templateAction);
        const updateOption = await connection.waitForTree(current => {
            const label = findByText(current, taggedTemplateName,
                entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '从模板更新菜单没有带标签模板');
        await connection.clickNode(updateOption.value);
        const blankUpdated = await connection.waitForTree(current => {
            const editor = rootOf(current, 'WorkEditorView');
            const region = rootOf(current, 'RedMineEditorRegionView');
            return textOf(findByName(current, 'WorkTitleInput')) === taggedTemplateTitle
                && textWithinNamedControl(current, 'WorkTimeInput').includes('0.5')
                && textWithin(current, editor, automationTagName)
                && textWithin(current, region, createdIssueTitle, true);
        }, 10000, '空事项从带标签模板更新后没有填充内容或执行标签规则');
        assertUi(blankUpdated.value, '空事项模板更新结果不完整');

        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'NewWorkItemButton'));
        const retainedEditor = await connection.waitForTree(current => findByName(current, 'WorkTitleInput'),
            8000, '第二个模板测试事项没有打开');
        tree = retainedEditor.tree;
        await connection.replaceText(findByName(tree, 'WorkTitleInput'), retainedDraftTitle);
        tree = await connection.getTree();
        await connection.replaceText(findByName(tree, 'WorkTimeInput'), '0.75');
        await connection.pressKey('Enter', 'Enter', 13);
        tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'WorkAddTagButton'));
        const existingTagOption = await connection.waitForTree(current => {
            const label = findByText(current, existingTagName,
                entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '事项标签候选中没有已有内容标签');
        await connection.clickNode(existingTagOption.value);

        tree = await connection.getTree();
        templateAction = findByName(tree, 'TemplateActionButton');
        assertUi(templateAction, '缺少第二次从模板更新按钮');
        await connection.clickNode(templateAction);
        const retainedUpdateOption = await connection.waitForTree(current => {
            const label = findByText(current, taggedTemplateName,
                entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '第二次从模板更新菜单没有带标签模板');
        await connection.clickNode(retainedUpdateOption.value);
        await delay(250);
        tree = await connection.getTree();
        let editor = rootOf(tree, 'WorkEditorView');
        let region = rootOf(tree, 'RedMineEditorRegionView');
        assertUi(textOf(findByName(tree, 'WorkTitleInput')) === retainedDraftTitle,
            '从模板更新覆盖了已有标题');
        assertUi(textWithinNamedControl(tree, 'WorkTimeInput').includes('0.75'),
            '从模板更新覆盖了已有工时');
        assertUi(textWithin(tree, editor, existingTagName), '从模板更新移除了已有标签');
        assertUi(!textWithin(tree, editor, automationTagName), '从模板更新错误添加了模板标签');
        assertUi(!textWithin(tree, region, createdIssueTitle, true), '从模板更新错误执行了模板标签规则');

        const applyButton = findByName(tree, 'ApplyTemplateMenuButton');
        assertUi(applyButton, '缺少应用模板菜单按钮');
        await connection.clickNode(applyButton);
        const applyOption = await connection.waitForTree(current => {
            const label = findByText(current, taggedTemplateName,
                entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '应用模板菜单没有带标签模板');
        await connection.clickNode(applyOption.value);
        const applied = await connection.waitForTree(current => {
            const currentEditor = rootOf(current, 'WorkEditorView');
            const currentRegion = rootOf(current, 'RedMineEditorRegionView');
            return textOf(findByName(current, 'WorkTitleInput')) === taggedTemplateTitle
                && textWithinNamedControl(current, 'WorkTimeInput').includes('0.5')
                && textWithin(current, currentEditor, automationTagName)
                && !textWithin(current, currentEditor, existingTagName)
                && textWithin(current, currentRegion, createdIssueTitle, true);
        }, 10000, '应用带标签模板没有替换内容、标签或执行规则');
        editor = rootOf(applied.tree, 'WorkEditorView');
        region = rootOf(applied.tree, 'RedMineEditorRegionView');
        assertUi(editor && region, '应用模板后事项或 Redmine 编辑区域丢失');
        return {
            updateBlankAppliedDefaults: true,
            updateExistingPreserved: true,
            applyReplacedContentAndTriggeredRule: true,
        };
    });

    await runStep('redmine.work-tag-defaults', '标签自动填充 Issue 与活动并保存本地事项', async () => {
        await connection.navigate('日记记录', 'DiaryEditorView');
        let tree = await connection.getTree();
        await activateControl(connection, findByName(tree, 'NewWorkItemButton'));
        const editor = await connection.waitForTree(current => findByName(current, 'WorkTitleInput'),
            8000, '新建事项未打开编辑器');
        tree = editor.tree;
        await connection.replaceText(findByName(tree, 'WorkTitleInput'), workTitle);
        tree = await connection.getTree();
        const dateInput = tree.entries.find(entry => isVisible(entry) && nameOf(entry) === 'PART_TextBox'
            && ancestor(tree, entry, item => nameOf(item) === 'WorkDatePicker'));
        assertUi(dateInput, '工作日期输入框不存在');
        await connection.replaceText(dateInput, '2026-08-21');
        await connection.pressKey('Enter', 'Enter', 13);
        tree = await connection.getTree();
        const timeControl = findByName(tree, 'WorkTimeInput');
        const timeInput = timeControl && typeOf(timeControl).includes('TextBox')
            ? timeControl
            : descendants(tree, timeControl).find(entry => isVisible(entry)
                && typeOf(entry).includes('TextBox'));
        assertUi(timeInput, '工作耗时输入框不存在');
        await connection.replaceText(timeInput, '0.25');
        await connection.pressKey('Enter', 'Enter', 13);
        tree = await connection.getTree();
        const addTag = textWithin(tree, rootOf(tree, 'WorkEditorView'), '添加标签（常用优先）');
        const addTagButton = controlForText(tree, addTag);
        assertUi(addTagButton, '事项编辑器缺少添加标签按钮');
        await selectMenuOption(
            connection,
            addTagButton,
            automationTagName,
            '新标签没有出现在事项标签菜单');
        const defaults = await connection.waitForTree(current => {
            const region = rootOf(current, 'RedMineEditorRegionView');
            if (!region)
                return null;
            const issue = textWithin(current, region, createdIssueTitle, true);
            const combos = descendants(current, region).filter(entry => isVisible(entry)
                && typeOf(entry).endsWith('.ComboBox'));
            return issue && combos.length >= 2 ? { issue, combos } : null;
        }, 8000, '标签规则没有自动填充 Redmine Issue/活动');
        assertUi(textWithin(defaults.tree, rootOf(defaults.tree, 'WorkEditorView'), automationTagName),
            '事项没有添加自动化标签');
        await activateTextWithin(connection, 'DiaryEditorView', '保存');
        await connection.waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            return input && textOf(input) === workTitle
                && textWithinNamedControl(current, 'WorkTimeInput').includes('0.25');
        }, 10000, '本地事项保存结果不正确');
        return { issueDefaultApplied: true, activityDefaultApplied: true };
    });

    await runStep('redmine.time-sync', 'Redmine 工时同步、锁定和防重复入口', async () => {
        const started = performance.now();
        await activateTextWithin(connection, 'DiaryEditorView', '同步工时');
        const success = await connection.waitForTree(current => findByText(current, '同步成功'),
            20000, 'Redmine 工时同步没有成功');
        const elapsedMs = performance.now() - started;
        const tree = success.tree;
        const editor = rootOf(tree, 'WorkEditorView');
        const region = rootOf(tree, 'RedMineEditorRegionView');
        assertUi(editor && region, '同步后工作编辑区或 Redmine 区域丢失');
        const titleInput = findByName(tree, 'WorkTitleInput');
        assertUi(titleInput && !isEffectivelyEnabled(tree, titleInput), '成功同步后一般字段应锁定');
        const modifyButton = findByName(tree, 'ModifySubmittedWorkButton');
        assertUi(modifyButton && isEffectivelyEnabled(tree, modifyButton), '成功同步后没有可用的本地修改按钮');
        const datePicker = findByName(tree, 'WorkDatePicker');
        assertUi(datePicker && !isEffectivelyEnabled(tree, datePicker), '成功同步后日期仍可修改');
        const diary = rootOf(tree, 'DiaryEditorView');
        const uploadButtonText = textWithin(tree, diary, '同步工时');
        const uploadButton = controlForText(tree, uploadButtonText);
        assertUi(uploadButton, '同步后找不到同步工时按钮');
        assertUi(!isEffectivelyEnabled(tree, uploadButton), '同步成功后同步按钮仍然可以重复执行');
        const statusBefore = descendants(tree, editor).map(textOf).find(text =>
            text.includes('本地已保存') && text.includes('已同步'));
        assertUi(statusBefore, '同步成功后缺少已同步状态摘要');

        await activateControl(connection, modifyButton);
        const localEditing = await connection.waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            const date = findByName(current, 'WorkDatePicker');
            const tracker = rootOf(current, 'RedMineEditorRegionView');
            return input && isEffectivelyEnabled(current, input)
                && date && !isEffectivelyEnabled(current, date)
                && tracker && !isEffectivelyEnabled(current, tracker)
                ? { input }
                : null;
        }, 8000, '本地修改模式没有按边界解锁事项字段');
        const localTitle = `${workTitle}-local`;
        await connection.replaceText(localEditing.value.input, localTitle);
        await activateTextWithin(connection, 'DiaryEditorView', '保存');
        await connection.waitForTree(current => {
            const input = findByName(current, 'WorkTitleInput');
            const date = findByName(current, 'WorkDatePicker');
            return input && textOf(input) === localTitle
                && !isEffectivelyEnabled(current, input)
                && date && !isEffectivelyEnabled(current, date)
                ? input
                : null;
        }, 10000, '本地修改保存后没有恢复已提交锁定状态');

        await connection.clickNode(uploadButton);
        await connection.pressKey('u', 'KeyU', 85, ctrl);
        await delay(500);
        const after = await connection.getTree();
        const afterEditor = rootOf(after, 'WorkEditorView');
        const statusAfter = descendants(after, afterEditor).map(textOf).find(text =>
            text.includes('本地已保存') && text.includes('已同步'));
        assertUi(statusAfter === statusBefore, '按钮或 Ctrl+U 绕过了重复同步保护');
        if (elapsedMs > 3000)
            addFinding('warning', 'redmine-upload-slow', 'Redmine 工时同步超过 3 秒', { elapsedMs });
        return { elapsedMs, duplicateGuard: true, localEditGuard: true };
    });

    await runStep('redmine.remote-delete-boundary', '已同步事项删除仅影响本地的警告与取消', async () => {
        await activateTextWithin(connection, 'DiaryEditorView', '删除当前项');
        const warning = await connection.waitForTree(tree => {
            const message = tree.entries.find(entry => isVisible(entry)
                && typeOf(entry).includes('MessageBoxControl'));
            return message && textOf(message).includes('远程工时不会被删除') ? message : null;
        }, 8000, '已同步事项删除警告未出现');
        const no = warning.tree.entries.find(entry => isVisible(entry) && nameOf(entry) === 'PART_NoButton');
        assertUi(no, '删除警告缺少取消按钮');
        await activateControl(connection, no);
        await connection.waitForTree(tree => !tree.entries.some(entry => isVisible(entry)
            && typeOf(entry).includes('MessageBoxControl')), 8000, '删除警告没有关闭');
        assertUi(findByName(await connection.getTree(), 'WorkTitleInput'), '取消删除后事项丢失');
        return { cancelled: true };
    });

    await runStep('redmine.security-performance', 'Redmine 配置加密、日志脱敏和管理响应速度', async () => {
        const configDirectory = path.join(connection.state.profile, 'config');
        const configFiles = await fs.readdir(configDirectory);
        const redmineFiles = configFiles.filter(name => name.toLowerCase().includes('redmine'));
        assertUi(redmineFiles.length > 0, '隔离 profile 缺少 Redmine 配置文件');
        for (const name of redmineFiles) {
            const content = await fs.readFile(path.join(configDirectory, name));
            assertUi(content.subarray(0, 8).toString('ascii') === 'DiaryGCM',
                'Redmine 配置文件没有迁移为当前整体加密格式：' + name);
        }
        const dataDirectory = path.join(connection.state.profile, 'data');
        const logNames = (await fs.readdir(dataDirectory)).filter(name => /^Diary\.App.*\.log$/.test(name));
        const logs = (await Promise.all(logNames.map(name => fs.readFile(path.join(dataDirectory, name), 'utf8')))).join('\n');
        for (const forbidden of ['X-Redmine-API-Key', 'Authorization: Bearer', 'response body'])
            assertUi(!logs.toLowerCase().includes(forbidden.toLowerCase()), '日志包含敏感 HTTP 内容标记：' + forbidden);
        const navigationSamples = [];
        for (const [label, type] of [
            ['Redmine 测试服', 'RedMineManageView'],
            ['日记记录', 'DiaryEditorView'],
            ['Redmine 测试服', 'RedMineManageView'],
        ])
            navigationSamples.push(await connection.navigate(label, type));
        const p95 = percentile(navigationSamples, 0.95);
        if (p95 > 1500)
            addFinding('warning', 'redmine-navigation-slow', 'Redmine 页面导航 P95 超过 1.5 秒', { navigationSamples });
        return { encryptedConfig: true, logMarkersAbsent: true, navigationSamplesMs: navigationSamples, p95 };
    });
});
