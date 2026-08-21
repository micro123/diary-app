#!/usr/bin/env node

import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findByName,
    findByText,
    findByTextContains,
    hasAncestorType,
    isEnabled,
    isVisible,
    nameOf,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const stamp = Date.now().toString(36);
const scripts = [
    { language: 'C#', name: 'UI CSharp ' + stamp, id: 'ui-csharp-' + stamp },
    { language: 'Lua', name: 'UI Lua ' + stamp, id: 'ui-lua-' + stamp },
    { language: 'Python', name: 'UI Python ' + stamp, id: 'ui-python-' + stamp },
];

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function within(tree, root, predicate) {
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry) && predicate(entry));
}

function textWithin(tree, root, text, contains = false) {
    return within(tree, root, entry => contains ? textOf(entry).includes(text) : textOf(entry) === text);
}

async function activate(connection, entry) {
    assertUi(entry, '不能激活空控件');
    await connection.client.send('DOM.focus', { nodeId: entry.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
}

async function activateText(connection, typeName, text) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '页面不存在：' + typeName);
    const label = textWithin(tree, root, text);
    assertUi(label, typeName + ' 缺少文字：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '找不到可激活控件：' + text);
    await activate(connection, control);
}

async function selectComboValue(connection, dialogType, currentText, value) {
    if (currentText === value)
        return;
    const tree = await connection.getTree();
    const root = rootOf(tree, dialogType);
    const selected = textWithin(tree, root, currentText);
    const combo = selected && ancestor(tree, selected, entry => typeOf(entry).includes('ComboBox'));
    assertUi(combo, '找不到下拉框当前值：' + currentText);
    const values = ['C#', 'Lua', 'Python'];
    const currentIndex = values.indexOf(currentText);
    const targetIndex = values.indexOf(value);
    assertUi(currentIndex >= 0 && targetIndex >= 0, '不支持的下拉值：' + value);
    await connection.client.send('DOM.focus', { nodeId: combo.nodeId });
    const key = targetIndex > currentIndex ? 'ArrowDown' : 'ArrowUp';
    const code = key;
    const virtualKeyCode = key === 'ArrowDown' ? 40 : 38;
    for (let index = 0; index < Math.abs(targetIndex - currentIndex); index++)
        await connection.pressKey(key, code, virtualKeyCode);
    await connection.pressKey('Enter', 'Enter', 13);
    await connection.waitForTree(current => {
        const currentRoot = rootOf(current, dialogType);
        return currentRoot && textWithin(current, currentRoot, value);
    }, 5000, '下拉值没有切换：' + value);
}

async function createScript(connection, item) {
    await activateText(connection, 'ScriptManagementView', '新建脚本');
    const opened = await connection.waitForTree(tree => rootOf(tree, 'ScriptCreationView'), 8000,
        '新建脚本对话框未出现');
    let tree = opened.tree;
    let root = opened.value;
    const inputs = descendants(tree, root, entry => isVisible(entry)
        && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
    assertUi(inputs.length >= 3, '新建脚本输入框不完整');
    await connection.replaceText(inputs[0], item.name);
    tree = await connection.getTree();
    root = rootOf(tree, 'ScriptCreationView');
    const refreshedInputs = descendants(tree, root, entry => isVisible(entry)
        && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
    await connection.replaceText(refreshedInputs[1], item.id);
    await selectComboValue(connection, 'ScriptCreationView', 'C#', item.language);
    tree = await connection.getTree();
    root = rootOf(tree, 'ScriptCreationView');
    const createText = textWithin(tree, root, '创建');
    const createButton = createText && controlForText(tree, createText);
    assertUi(createButton && isEnabled(createButton), '创建按钮未启用：' + item.language);
    await activate(connection, createButton);
    const created = await connection.waitForTree(current => !rootOf(current, 'ScriptCreationView')
        && findByText(current, item.name), 30000, '脚本创建或检查未完成：' + item.name);
    return { dialogMs: opened.elapsedMs, createMs: created.elapsedMs };
}

function scriptRow(tree, name) {
    const text = findByText(tree, name);
    return text && ancestor(tree, text, entry => typeOf(entry).includes('ListBoxItem'));
}

async function selectScript(connection, name) {
    const result = await connection.waitForTree(tree => scriptRow(tree, name), 10000, '脚本行不存在：' + name);
    await connection.clickNode(result.value);
    await connection.waitForTree(tree => {
        const row = scriptRow(tree, name);
        return row && row.a.IsSelected === 'true';
    }, 5000, '脚本没有选中：' + name);
}

async function clickScriptRowButton(connection, name, buttonText) {
    const tree = await connection.getTree();
    const row = scriptRow(tree, name);
    assertUi(row, '脚本行不存在：' + name);
    const button = descendants(tree, row).find(entry => typeOf(entry).includes('Button') && textOf(entry) === buttonText);
    assertUi(button, '脚本行缺少按钮：' + buttonText);
    await activate(connection, button);
}

await runUiSuite({ name: 'ui-extended-full', scenario: 'extended', timeoutMs: 12000 }, async ({
    connection, runStep, addFinding,
}) => {
    await connection.pressKey('Escape', 'Escape', 27);
    await delay(100);

    await runStep('scripts.shell', '脚本导航与工作台结构', async () => {
        const navigationMs = await connection.navigate('脚本管理', 'ScriptManagementView');
        const tree = await connection.getTree();
        const root = rootOf(tree, 'ScriptManagementView');
        for (const text of ['脚本工作台', '新建脚本', '导出共享包', '重新加载', '概览', '诊断详情',
            '目录诊断', '执行历史', '运行日志', 'API Reference'])
            assertUi(textWithin(tree, root, text), '脚本工作台缺少：' + text);
        assertUi(textWithin(tree, root, '尚未发现脚本'), '全新 profile 的脚本空状态缺失');
        return { navigationMs };
    });

    for (const item of scripts) {
        await runStep('scripts.create-' + item.language.toLowerCase().replace('#', 'sharp'),
            '创建 ' + item.language + ' 脚本', async () => createScript(connection, item));
    }

    await runStep('scripts.filter-reload', '脚本搜索、筛选和重新加载', async () => {
        let tree = await connection.getTree();
        const root = rootOf(tree, 'ScriptManagementView');
        const search = descendants(tree, root).find(entry => typeOf(entry).includes('TextBox')
            && within(tree, entry, child => nameOf(child) === 'PART_Watermark'
                && textOf(child) === '搜索脚本名称或 ID'));
        assertUi(search, '脚本搜索框缺失');
        await connection.replaceText(search, scripts[1].id);
        await connection.waitForTree(current => scriptRow(current, scripts[1].name)
            && !scriptRow(current, scripts[0].name), 5000, '脚本搜索筛选失败');
        tree = await connection.getTree();
        const currentRoot = rootOf(tree, 'ScriptManagementView');
        const currentSearch = descendants(tree, currentRoot).find(entry => typeOf(entry).includes('TextBox')
            && Number(entry.a.Width) > 400);
        await connection.replaceText(currentSearch, '');
        await connection.waitForTree(current => scripts.every(item => scriptRow(current, item.name)), 5000,
            '清空搜索后脚本没有恢复');
        await activateText(connection, 'ScriptManagementView', '重新加载');
        const reloaded = await connection.waitForTree(current => findByTextContains(current, '已加载 3 个脚本'),
            30000, '脚本目录重新加载失败');
        return { reloadMs: reloaded.elapsedMs };
    });

    await runStep('scripts.preview-run', 'C# 脚本预览执行', async () => {
        const item = scripts[0];
        await selectScript(connection, item.name);
        await activateText(connection, 'ScriptManagementView', '运行');
        const dialog = await connection.waitForTree(tree => rootOf(tree, 'ScriptRunDialogView'), 8000,
            '脚本运行对话框未出现');
        const previewText = textWithin(dialog.tree, dialog.value, '预览执行（宿主强制阻止持久写入和真实文件导出）');
        const preview = previewText && controlForText(dialog.tree, previewText);
        assertUi(preview, '预览执行选项缺失');
        await connection.clickNode(preview);
        await activateText(connection, 'ScriptRunDialogView', '运行');
        const completed = await connection.waitForTree(tree => findByTextContains(tree, item.name + ' 执行成功'),
            30000, '脚本预览执行失败');
        return { dialogMs: dialog.elapsedMs, executeMs: completed.elapsedMs };
    });

    await runStep('scripts.history-logs-api', '执行历史、运行日志与 API Reference', async () => {
        let tree = await connection.getTree();
        let root = rootOf(tree, 'ScriptManagementView');
        let tabText = textWithin(tree, root, '执行历史');
        await connection.clickNode(ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem')));
        await connection.waitForTree(current => {
            const text = findByText(current, scripts[0].name);
            return text && hasAncestorType(current, text, 'ScriptHistoryListItem') ? text : findByText(current, '成功');
        }, 5000, '执行历史未显示');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        assertUi(textWithin(tree, root, scripts[0].name), '执行历史缺少脚本名称');
        assertUi(textWithin(tree, root, '成功'), '执行历史缺少成功状态');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        tabText = textWithin(tree, root, '运行日志');
        await connection.clickNode(ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem')));
        await delay(80);
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        assertUi(textWithin(tree, root, '清空日志'), '运行日志缺少清理入口');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        tabText = textWithin(tree, root, 'API Reference');
        await connection.clickNode(ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem')));
        await delay(80);
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        assertUi(textWithin(tree, root, '打开完整文档'), 'API Reference 缺少完整文档入口');
        assertUi(findByTextContains(tree, 'work_items') || findByTextContains(tree, 'API'), 'API Reference 内容为空');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        tabText = textWithin(tree, root, '诊断详情');
        await connection.clickNode(ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem')));
        await delay(80);
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        tabText = textWithin(tree, root, '目录诊断');
        await connection.clickNode(ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem')));
        await delay(80);
        return { historyVisible: true, apiVisible: true };
    });

    await runStep('scripts.delete-confirm', '脚本删除取消与确认', async () => {
        const item = scripts[2];
        await clickScriptRowButton(connection, item.name, '删除');
        let confirm = await connection.waitForTree(tree => findByName(tree, 'PART_NoButton'), 8000,
            '删除脚本确认未出现');
        await connection.client.send('DOM.focus', { nodeId: confirm.value.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        await connection.waitForTree(tree => findByText(tree, '已取消删除脚本') && scriptRow(tree, item.name),
            5000, '取消删除脚本失败');
        await clickScriptRowButton(connection, item.name, '删除');
        confirm = await connection.waitForTree(tree => findByName(tree, 'PART_YesButton'), 8000,
            '第二次删除确认未出现');
        await connection.client.send('DOM.focus', { nodeId: confirm.value.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        const deleted = await connection.waitForTree(tree => !scriptRow(tree, item.name)
            && findByTextContains(tree, '脚本已删除'), 30000, '确认删除脚本失败');
        return { deleteMs: deleted.elapsedMs };
    });

    await runStep('scripts.performance', '脚本页刷新响应速度', async () => {
        const samples = [];
        for (let index = 0; index < 5; index++) {
            const started = performance.now();
            await activateText(connection, 'ScriptManagementView', '重新加载');
            await connection.waitForTree(tree => findByTextContains(tree, '已加载 2 个脚本'), 30000);
            samples.push(performance.now() - started);
        }
        const maxMs = Math.max(...samples);
        if (maxMs > 2000)
            addFinding('warning', 'script-reload-slow', '脚本目录刷新超过 2 秒', { samples });
        return { samples, maxMs };
    });
});
