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
    hasAncestorType,
    isChecked,
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

function parameterizedCSharpSource(item) {
    const className = item.id.split('-')
        .map(part => part.charAt(0).toUpperCase() + part.slice(1))
        .join('');
    return `#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;

namespace Diary.UserScripts;

public sealed class ${className} : ApplicationScriptV2
{
    public override string Id => "${item.id}";
    public override string Name => "${item.name}";
    public override IReadOnlyList<ScriptParameterDefinition> Parameters =>
    [
        new("range", "统计范围", ScriptParameterType.Choice, Required: true,
            DefaultValue: "week", Choices: [new("week", "本周"), new("month", "本月")]),
        new("minimumHours", "最低工时", ScriptParameterType.Number, DefaultValue: "0",
            Constraints: new(Minimum: "0", Maximum: "24", Step: "0.5", Unit: "小时")),
        new("titlePrefix", "标题前缀", ScriptParameterType.String, DefaultValue: "工时汇总",
            Constraints: new(MaxLength: 20,
                Suggestions: [new("工时汇总", "工时汇总"), new("团队周报", "团队周报")])),
        new("includeZero", "包含零工时", ScriptParameterType.Boolean, DefaultValue: "false"),
    ];

    public override ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ScriptExecutionResult.Succeeded());
}
`;
}

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function within(tree, root, predicate) {
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry) && predicate(entry));
}

function textWithin(tree, root, text, contains = false) {
    return within(tree, root, entry => contains ? textOf(entry).includes(text) : textOf(entry) === text);
}

function isEffectivelyVisible(tree, entry) {
    return !ancestor(tree, entry, current => !isVisible(current));
}

function aiPreviewText(tree, root) {
    const preview = descendants(tree, root, entry => isEffectivelyVisible(tree, entry)
        && typeOf(entry).includes('TextBox') && textOf(entry).includes('diary.ai_context'))[0];
    return textOf(preview);
}

function boundsOf(entry) {
    const parts = String(entry?.a?.Bounds ?? '').split(',').map(Number);
    assertUi(parts.length === 4 && parts.every(Number.isFinite), 'AI 预览框缺少有效 Bounds');
    return { x: parts[0], y: parts[1], width: parts[2], height: parts[3] };
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

async function openProgramSettings(connection) {
    let lastError;
    const started = performance.now();
    for (let attempt = 0; attempt < 3; attempt++) {
        try {
            const tree = await connection.getTree();
            const settingsButton = findByName(tree, 'SettingsMenuButton');
            assertUi(settingsButton, '找不到设置菜单按钮');
            await connection.clickNode(settingsButton);
            const menuItem = await connection.waitForTree(current => findByName(current, 'ProgramSettingsMenuItem'),
                1800, '程序设置菜单项未出现');
            await activate(connection, menuItem.value);
            await connection.waitForTree(current => rootOf(current, 'SettingsView'), 3000, '程序设置未打开');
            return performance.now() - started;
        }
        catch (error) {
            lastError = error;
            await connection.pressKey('Escape', 'Escape', 27);
            await delay(100);
        }
    }
    throw lastError;
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
    assertUi(textWithin(tree, root, 'V2（推荐）'), '新建脚本向导缺少 V2 推荐说明');
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
    let manualScreenshot;
    let dialogBounds;
    if (item.language === 'C#') {
        let manualInputs = descendants(tree, root, entry => isVisible(entry)
            && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
        await connection.replaceText(manualInputs[0], '每日摘要');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptCreationView');
        manualInputs = descendants(tree, root, entry => isVisible(entry)
            && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
        await connection.replaceText(manualInputs[1], 'daily-summary');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptCreationView');
        const versionText = textWithin(tree, root, 'V2（推荐）');
        const versionCombo = versionText && ancestor(tree, versionText,
            entry => typeOf(entry).includes('ComboBox'));
        assertUi(versionCombo, '找不到脚本 API 版本下拉框');
        await connection.clickNode(versionCombo);
        const expanded = await connection.waitForTree(current =>
            findByText(current, 'V1（兼容）')
            && findByTextContains(current, '适合兼容旧脚本'),
        3000, '脚本 API 版本选项没有展开或缺少说明');
        tree = expanded.tree;
        assertUi(findByTextContains(tree, '执行前校验'), 'V2 选项缺少参数契约说明');
        manualScreenshot = await connection.screenshot('manual-script-creation-api-version.png');
        dialogBounds = boundsOf(root);
        await connection.pressKey('Escape', 'Escape', 27);
        await connection.waitForTree(current => !findByText(current, 'V1（兼容）'),
            3000, '脚本 API 版本下拉框没有关闭');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptCreationView');
        manualInputs = descendants(tree, root, entry => isVisible(entry)
            && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
        await connection.replaceText(manualInputs[0], item.name);
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptCreationView');
        manualInputs = descendants(tree, root, entry => isVisible(entry)
            && typeOf(entry).includes('TextBox') && Number(entry.a.Width) > 100);
        await connection.replaceText(manualInputs[1], item.id);
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptCreationView');
    }
    const createText = textWithin(tree, root, '创建');
    const createButton = createText && controlForText(tree, createText);
    assertUi(createButton && isEnabled(createButton), '创建按钮未启用：' + item.language);
    await activate(connection, createButton);
    const created = await connection.waitForTree(current => !rootOf(current, 'ScriptCreationView')
        && findByText(current, item.name), 30000, '脚本创建或检查未完成：' + item.name);
    return {
        dialogMs: opened.elapsedMs,
        createMs: created.elapsedMs,
        ...(manualScreenshot ? { manualScreenshot, dialogBounds } : {}),
    };
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
            '目录诊断', '执行历史', '运行日志', 'AI 上下文'])
            assertUi(textWithin(tree, root, text), '脚本工作台缺少：' + text);
        assertUi(!textWithin(tree, root, 'API Reference'), '脚本管理页不应显示 API Reference 页签');
        assertUi(textWithin(tree, root, '尚未发现脚本'), '全新 profile 的脚本空状态缺失');
        return { navigationMs };
    });

    await runStep('scripts.ai-context', 'AI 上下文授权、预览、快照与手册截图', async () => {
        let tree = await connection.getTree();
        let root = rootOf(tree, 'ScriptManagementView');
        let pageShown = false;
        for (let attempt = 0; attempt < 3 && !pageShown; attempt++) {
            tree = await connection.getTree();
            root = rootOf(tree, 'ScriptManagementView');
            const tabText = textWithin(tree, root, 'AI 上下文');
            const tab = tabText && ancestor(tree, tabText, entry => typeOf(entry).includes('TabItem'));
            assertUi(tab, 'AI 上下文页签缺失');
            await connection.clickNode(tab);
            try {
                await connection.waitForTree(current => findByText(current, '选择允许外部 AI 看到的本地信息'),
                    1800, 'AI 上下文页面未显示');
                pageShown = true;
            }
            catch (error) {
                if (attempt === 2)
                    throw error;
                await delay(80);
            }
        }

        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        const workItemsText = textWithin(tree, root,
            '显式包含事项标题、备注和附加字段值（不可信数据）');
        let workItemsCheckBox = workItemsText && controlForText(tree, workItemsText);
        assertUi(workItemsCheckBox && !isChecked(workItemsCheckBox), '事项内容必须默认关闭');
        const defaultStartText = textWithin(tree, root, '开始');
        assertUi(defaultStartText && !isEffectivelyVisible(tree, defaultStartText),
            '默认状态不应显示事项日期范围');
        const previewBefore = findByName(tree, 'AiContextPreviewTextBox');
        const previewBoundsBefore = boundsOf(previewBefore);
        assertUi(previewBoundsBefore.height >= 200, 'AI 预览框没有填满剩余区域');

        await activateText(connection, 'ScriptManagementView', '生成预览');
        await connection.waitForTree(current => findByTextContains(current, '预览已生成：标签 1，字段 1'),
            8000, '默认 AI 上下文预览未生成');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        let previewText = aiPreviewText(tree, root);
        const previewBoundsAfter = boundsOf(findByName(tree, 'AiContextPreviewTextBox'));
        assertUi(Math.abs(previewBoundsAfter.height - previewBoundsBefore.height) <= 1,
            '生成预览后文本框高度发生跳变');
        assertUi(previewText.includes('diary.ai_context'), '预览缺少 schema 标识');
        assertUi(previewText.includes('AI\\u4E0A\\u4E0B\\u6587\\u793A\\u4F8B\\u9879\\u76EE'),
            '预览缺少示例标签');
        assertUi(previewText.includes('"work_item_count": 0'), '默认预览不应包含事项');
        const defaultScreenshot = await connection.screenshot('manual-ai-context-default.png');

        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        const refreshedWorkItemsText = textWithin(tree, root,
            '显式包含事项标题、备注和附加字段值（不可信数据）');
        workItemsCheckBox = refreshedWorkItemsText && controlForText(tree, refreshedWorkItemsText);
        await connection.clickNode(workItemsCheckBox);
        await connection.waitForTree(current => {
            const startText = findByText(current, '开始');
            const endText = findByText(current, '结束');
            return startText && endText && isEffectivelyVisible(current, startText)
                && isEffectivelyVisible(current, endText);
        }, 5000, '启用事项后日期范围未显示');
        await activateText(connection, 'ScriptManagementView', '生成预览');
        await connection.waitForTree(current => findByTextContains(current, '事项 1'),
            8000, '显式事项预览未生成');
        tree = await connection.getTree();
        root = rootOf(tree, 'ScriptManagementView');
        previewText = aiPreviewText(tree, root);
        assertUi(previewText.includes('\\u6574\\u7406 AI \\u811A\\u672C\\u4E0A\\u4E0B\\u6587'),
            '事项预览缺少示例标题');
        assertUi(previewText.includes('untrusted_user_content'), '事项预览缺少不可信数据标记');
        const workItemsScreenshot = await connection.screenshot('manual-ai-context-work-items.png');

        await activateText(connection, 'ScriptManagementView', '刷新 MCP 快照');
        await connection.waitForTree(current => findByTextContains(current, 'MCP 快照已刷新'),
            8000, 'MCP 快照刷新未完成');
        const snapshotPath = path.join(connection.state.profile, 'config', 'ai-context', 'mcp-snapshot.json');
        const snapshot = JSON.parse(await fs.readFile(snapshotPath, 'utf8'));
        assertUi(snapshot.schema_id === 'diary.ai_context' && snapshot.schema_version === 1,
            'MCP 快照 schema 无效');
        assertUi(snapshot.disclosure.work_items === true && snapshot.work_items.length === 1,
            'MCP 快照事项范围不符合显式授权');
        assertUi(!JSON.stringify(snapshot.tags).includes('metadata'), 'MCP 快照不得包含标签 metadata');
        return {
            defaultScreenshot,
            workItemsScreenshot,
            snapshotPath,
            workItemCount: snapshot.work_items.length,
        };
    });

    await runStep('settings.mcp-setup', '程序设置生成 AI 可读 MCP 配置', async () => {
        const openedMs = await openProgramSettings(connection);
        let tree = await connection.getTree();
        let root = rootOf(tree, 'SettingsView');
        const mcpHeader = textWithin(tree, root, 'AI 与 MCP');
        const mcpHeaderButton = mcpHeader && ancestor(tree, mcpHeader,
            entry => typeOf(entry).includes('ToggleButton'));
        assertUi(mcpHeaderButton, 'AI 与 MCP 设置分组缺少展开按钮');
        await activate(connection, mcpHeaderButton);
        await connection.waitForTree(current => findByText(current, '打开 AI 上下文'),
            5000, 'AI 与 MCP 设置分组未展开');
        tree = await connection.getTree();
        root = rootOf(tree, 'SettingsView');
        for (const text of ['AI 与 MCP', '打开 AI 上下文', '复制 AI 说明',
            '复制 MCP JSON', '打开使用文档'])
            assertUi(textWithin(tree, root, text), 'AI 与 MCP 设置缺少：' + text);
        const settingsBounds = boundsOf(root);
        for (let index = 0; index < 5; index++) {
            await connection.client.send('Input.dispatchMouseEvent', {
                type: 'mouseWheel',
                x: settingsBounds.x + settingsBounds.width / 2,
                y: settingsBounds.y + Math.min(settingsBounds.height - 80, 560),
                deltaX: 0,
                deltaY: 420,
            });
            await delay(30);
        }
        tree = await connection.getTree();
        root = rootOf(tree, 'SettingsView');
        const scriptGroupText = textWithin(tree, root, '脚本');
        assertUi(scriptGroupText, '程序设置缺少脚本分组');
        const scriptGroup = ancestor(tree, scriptGroupText, entry => typeOf(entry).includes('Expander'));
        assertUi(scriptGroup, '脚本设置分组不可展开');
        const scriptGroupHeader = descendants(tree, scriptGroup)
            .find(entry => nameOf(entry) === 'ExpanderHeader');
        assertUi(scriptGroupHeader, '脚本设置分组缺少可聚焦标题');
        await activate(connection, scriptGroupHeader);
        await connection.waitForTree(current => {
            const currentRoot = rootOf(current, 'SettingsView');
            return currentRoot && textWithin(current, currentRoot, '清除参数历史');
        }, 3000, '程序设置缺少清除参数历史入口');
        assertUi(textWithin(tree, root, '已生成 ·', true), '设置页未识别已生成的 MCP 快照');
        const guideText = textWithin(tree, root, '打开使用文档');
        const guideButton = guideText && controlForText(tree, guideText);
        assertUi(guideButton, 'AI 与 MCP 设置缺少使用文档按钮');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', {
            nodeId: guideButton.nodeId,
        }).catch(() => {});
        await delay(100);
        const settingsScreenshot = await connection.screenshot('manual-mcp-settings.png');

        let copyText = textWithin(tree, root, '复制 AI 说明');
        let copyButton = copyText && controlForText(tree, copyText);
        assertUi(copyButton && isEnabled(copyButton), 'AI 配置说明复制按钮未启用');
        await activate(connection, copyButton);
        await connection.waitForTree(current => findByText(current, '给 AI 的 MCP 配置说明已复制'),
            5000, 'AI 配置说明未复制');

        tree = await connection.getTree();
        root = rootOf(tree, 'SettingsView');
        copyText = textWithin(tree, root, '复制 MCP JSON');
        copyButton = copyText && controlForText(tree, copyText);
        assertUi(copyButton && isEnabled(copyButton), '通用 MCP JSON 复制按钮未启用');
        await activate(connection, copyButton);
        await connection.waitForTree(current => findByText(current, '通用 MCP JSON 已复制'),
            5000, '通用 MCP JSON 未复制');

        await activateText(connection, 'SettingsView', '打开 AI 上下文');
        await connection.waitForTree(current => !rootOf(current, 'SettingsView')
            && rootOf(current, 'ScriptManagementView')
            && findByText(current, '选择允许外部 AI 看到的本地信息'),
        8000, '未从程序设置打开 AI 上下文');
        return { openedMs, settingsScreenshot, copiedAiInstructions: true, copiedGenericJson: true };
    });

    for (const item of scripts) {
        await runStep('scripts.create-' + item.language.toLowerCase().replace('#', 'sharp'),
            '创建 ' + item.language + ' 脚本', async () => createScript(connection, item));
    }

    await fs.writeFile(
        path.join(connection.state.profile, 'config', 'scripts', 'application', scripts[0].id + '.cs'),
        parameterizedCSharpSource(scripts[0]),
        'utf8');

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
        tree = await connection.getTree();
        for (const item of scripts) {
            const row = scriptRow(tree, item.name);
            assertUi(row && descendants(tree, row).some(entry => textOf(entry) === 'V2'),
                '脚本列表缺少 V2 标识：' + item.name);
            const versionBadge = descendants(tree, row)
                .find(entry => nameOf(entry) === 'ScriptApiVersionBadge');
            assertUi(versionBadge && Number(versionBadge.a.Width) >= 30 && Number(versionBadge.a.Height) >= 18,
                '脚本 API 版本标识缺少圆角矩形背景容器：' + item.name);
        }
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
        for (const text of ['统计范围 *', '最低工时', '标题前缀', '包含零工时', '小时'])
            assertUi(textWithin(dialog.tree, dialog.value, text), 'V2 类型化参数表单缺少：' + text);
        for (const text of ['range · Choice', 'minimumHours · Number', 'titlePrefix · String', 'includeZero · Boolean'])
            assertUi(textWithin(dialog.tree, dialog.value, text, true), 'V2 参数键和类型提示缺失：' + text);
        assertUi(!textWithin(dialog.tree, dialog.value, '脚本默认', true), 'V2 参数辅助说明不应显示值来源');
        const parameterScreenshot = await connection.screenshot('manual-script-v2-parameters.png');
        const previewText = textWithin(dialog.tree, dialog.value, '预览执行');
        const preview = previewText && controlForText(dialog.tree, previewText);
        assertUi(preview, '预览执行选项缺失');
        await connection.clickNode(preview);
        await activateText(connection, 'ScriptRunDialogView', '运行');
        const completed = await connection.waitForTree(tree => findByTextContains(tree, item.name + ' 执行成功'),
            30000, '脚本预览执行失败');
        return {
            dialogMs: dialog.elapsedMs,
            executeMs: completed.elapsedMs,
            parameterScreenshot,
            dialogBounds: boundsOf(dialog.value),
        };
    });

    await runStep('scripts.history-logs', '执行历史与运行日志', async () => {
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
        assertUi(textWithin(tree, root, '复制全部'), '运行日志缺少复制全部入口');
        const runLogTextBox = findByName(tree, 'ScriptRunLogTextBox');
        assertUi(runLogTextBox && typeOf(runLogTextBox).includes('TextBox'),
            '运行日志没有使用可选择复制的只读文本框');
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
        return { historyVisible: true, logsVisible: true };
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
