#!/usr/bin/env node

import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findByName,
    findByText,
    hasAncestorType,
    isChecked,
    isVisible,
    nameOf,
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
    await delay(80);
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
                return label && item ? label : null;
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
    await connection.clickNode(menu.value);
    return connection.waitForTree(current => rootOf(current, expectedType), 10000,
        '设置对话框未出现：' + expectedType);
}

async function selectTab(connection, labelText, expectedType) {
    const tree = await connection.getTree();
    const label = findByText(tree, labelText, entry => hasAncestorType(tree, entry, 'TabItem'));
    assertUi(label, '找不到页签：' + labelText);
    const tab = ancestor(tree, label, entry => typeOf(entry).includes('TabItem'));
    assertUi(tab, '找不到页签容器：' + labelText);
    await connection.clickNode(tab);
    if (expectedType)
        await connection.waitForTree(current => rootOf(current, expectedType), 8000,
            '页签内容未出现：' + labelText);
    await delay(80);
}

async function nodeBounds(connection, entry) {
    assertUi(entry, '待测布局节点不存在');
    const result = await connection.client.send('DOM.getBoxModel', { nodeId: entry.nodeId });
    const quad = result.model.border;
    return {
        top: quad[1],
        bottom: quad[5],
        centerY: (quad[1] + quad[5]) / 2,
    };
}

async function assertCenterAligned(connection, reference, target, label) {
    const [referenceBounds, targetBounds] = await Promise.all([
        nodeBounds(connection, reference),
        nodeBounds(connection, target),
    ]);
    const difference = Math.abs(referenceBounds.centerY - targetBounds.centerY);
    assertUi(difference <= 2, `${label} 中心线偏差 ${difference.toFixed(1)}px，超过 2px`);
    return difference;
}

async function closeTrackerSettings(connection) {
    const tree = await connection.getTree();
    const dialog = rootOf(tree, 'TrackerSettingsDialogView');
    const cancelLabel = textWithin(tree, dialog, '取消');
    const cancel = cancelLabel && controlForText(tree, cancelLabel);
    await activateControl(connection, cancel);
    await connection.waitForTree(current => !rootOf(current, 'TrackerSettingsDialogView'), 10000,
        'Tracker 设置没有关闭');
}

await runUiSuite({ name: 'ui-redmine-style', scenario: 'plugins', timeoutMs: 12000, stopOnFailure: true }, async ({
    connection, runStep,
}) => {
    assertUi(connection.state.withPlugins, 'Redmine 样式套件必须加载 Tracker 插件');
    assertUi(connection.state.seeded, 'Redmine 样式套件必须使用加密 seed profile');

    await runStep('redmine-style.configuration', '检查 Tracker 配置和敏感字段样式', async () => {
        await openSettingsText(connection, 'Tracker 设置', 'TrackerSettingsDialogView');
        await selectTab(connection, 'Redmine', 'RedMineConfigurationView');
        const tree = await connection.getTree();
        const redmine = rootOf(tree, 'RedMineConfigurationView');
        for (const text of ['Redmine Tracker 实例', '实例配置', 'API Key:', '标签自动规则'])
            assertUi(textWithin(tree, redmine, text, true), 'Redmine 配置缺少：' + text);
        const apiLabel = textWithin(tree, redmine, 'API Key:');
        const apiItem = ancestor(tree, apiLabel, entry => textOf(entry).includes('SettingText'));
        const apiInput = apiItem && descendants(tree, apiItem).find(entry => typeOf(entry).includes('TextBox'));
        assertUi(apiInput, 'API Key 缺少输入控件');
        assertUi(String(apiInput.a.Class ?? '').split(/\s+/).includes('RevealPasswordButton'),
            'API Key 未使用敏感文本编辑器');
        return { screenshot: await connection.screenshot('redmine-style-configuration.png') };
    });

    await runStep('redmine-style.plugin-status', '检查插件状态页布局', async () => {
        await selectTab(connection, '插件状态', 'TrackerPluginDiagnosticsView');
        const tree = await connection.getTree();
        const diagnostics = rootOf(tree, 'TrackerPluginDiagnosticsView');
        for (const text of ['Tracker 插件状态', '运行中'])
            assertUi(textWithin(tree, diagnostics, text, true), '插件状态缺少：' + text);
        const screenshot = await connection.screenshot('redmine-style-plugin-status.png');
        await closeTrackerSettings(connection);
        return { screenshot };
    });

    await runStep('redmine-style.information', '检查 Redmine 基本信息和展开分组', async () => {
        const navigationMs = await connection.navigate('Redmine 测试服', 'RedMineManageView');
        const ready = await connection.waitForTree(tree => {
            const root = rootOf(tree, 'RedMineManageView');
            return root && textWithin(tree, root, '基本信息')
                && rootOf(tree, 'RedMineInfoView') ? root : null;
        }, 10000, 'Redmine 管理页未连接测试服');
        let tree = ready.tree;
        const info = rootOf(tree, 'RedMineInfoView');
        const userHeader = textWithin(tree, info, '用户信息');
        const userExpander = ancestor(tree, userHeader, entry => typeOf(entry).includes('Expander'));
        const userToggle = descendants(tree, userExpander).find(entry => nameOf(entry) === 'ExpanderHeader');
        if (!isChecked(userToggle))
            await activateControl(connection, userToggle);
        tree = (await connection.waitForTree(current => findByText(current, '用户ID：'), 5000,
            'Redmine 用户信息没有展开')).tree;
        for (const text of ['用户ID：', '登录名：', '用户名：', '活动列表', '已导入的问题'])
            assertUi(textWithin(tree, rootOf(tree, 'RedMineInfoView'), text, true), '基本信息缺少：' + text);
        return { navigationMs, screenshot: await connection.screenshot('redmine-style-information.png') };
    });

    await runStep('redmine-style.issue-toolbar', '检查问题工具栏和 CheckBox 中心线', async () => {
        await selectTab(connection, '问题管理', 'RedMineIssueManageView');
        const tree = await connection.getTree();
        const view = rootOf(tree, 'RedMineIssueManageView');
        const input = descendants(tree, view).find(entry => typeOf(entry).includes('TextBox'));
        const openedLabel = textWithin(tree, view, '打开的问题');
        const mineLabel = textWithin(tree, view, '分配给我的');
        const opened = ancestor(tree, openedLabel, entry => typeOf(entry).includes('CheckBox'));
        const mine = ancestor(tree, mineLabel, entry => typeOf(entry).includes('CheckBox'));
        const openedDifference = await assertCenterAligned(connection, input, opened, '“打开的问题”CheckBox');
        const mineDifference = await assertCenterAligned(connection, input, mine, '“分配给我的”CheckBox');
        for (const text of ['关键字搜索', '首页', '上一页', '下一页', '尾页'])
            assertUi(textWithin(tree, view, text, true), '问题管理缺少：' + text);
        return {
            openedDifference,
            mineDifference,
            screenshot: await connection.screenshot('redmine-style-issues.png'),
        };
    });

    await runStep('redmine-style.project-toolbar', '检查项目工具栏和分页布局', async () => {
        await selectTab(connection, '项目管理', 'RedMineProjectView');
        const tree = await connection.getTree();
        const view = rootOf(tree, 'RedMineProjectView');
        for (const text of ['搜索', '首页', '上一页', '下一页', '尾页'])
            assertUi(textWithin(tree, view, text, true), '项目管理缺少：' + text);
        return { screenshot: await connection.screenshot('redmine-style-projects.png') };
    });
});
