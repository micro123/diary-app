#!/usr/bin/env node

import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findByText,
    findByTextContains,
    hasAncestorType,
    isChecked,
    isEnabled,
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

async function activateTextWithin(connection, typeName, text) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '页面不可见：' + typeName);
    const label = textWithin(tree, root, text);
    assertUi(label, '页面缺少操作：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '操作不可激活：' + text);
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
    return control;
}

async function closeDialogByText(connection, typeName, text) {
    const tree = await connection.getTree();
    const root = rootOf(tree, typeName);
    assertUi(root, '对话框不可见：' + typeName);
    const label = textWithin(tree, root, text);
    assertUi(label, '对话框缺少关闭入口：' + text);
    const control = controlForText(tree, label);
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
    await connection.waitForTree(current => !rootOf(current, typeName), 8000, typeName + ' 没有关闭');
}

async function selectComboOption(connection, combo, optionText) {
    await connection.clickNode(combo);
    const option = await connection.waitForTree(tree => {
        const label = findByText(tree, optionText, entry => hasAncestorType(tree, entry, 'ComboBoxItem'));
        return label && ancestor(tree, label, entry => typeOf(entry).includes('ComboBoxItem'));
    }, 5000, '下拉选项未出现：' + optionText);
    await connection.clickNode(option.value);
    await connection.waitForTree(tree => findByText(tree, optionText, entry => !hasAncestorType(tree, entry, 'ComboBoxItem')),
        5000, '下拉选项未生效：' + optionText);
}

function comboBySelectedText(tree, root, selectedText) {
    return descendants(tree, root).find(entry => isVisible(entry) && typeOf(entry).endsWith('.ComboBox')
        && descendants(tree, entry).some(child => isVisible(child) && textOf(child) === selectedText));
}

async function expandExtendedConditions(connection) {
    const tree = await connection.getTree();
    const root = rootOf(tree, 'SurveyView');
    const header = textWithin(tree, root, '扩展查询条件（仅新版节点）');
    assertUi(header, '扩展查询条件入口不可见');
    const expander = ancestor(tree, header, entry => typeOf(entry).includes('Expander'));
    assertUi(expander, '扩展查询条件缺少 Expander');
    const toggle = descendants(tree, expander).find(entry => isVisible(entry)
        && entry.a.Name === 'ExpanderHeader');
    assertUi(toggle, '扩展查询条件缺少展开按钮');
    await connection.clickNode(toggle);
    await connection.waitForTree(current => {
        const currentRoot = rootOf(current, 'SurveyView');
        return textWithin(current, currentRoot, '关键词') && textWithin(current, currentRoot, '分组维度');
    }, 5000, '扩展查询条件没有展开');
}

async function waitForSurveyComplete(connection, expectedFragment, timeoutMs = 8000) {
    return connection.waitForTree(tree => {
        const root = rootOf(tree, 'SurveyView');
        return textWithin(tree, root, expectedFragment, true);
    }, timeoutMs, '调查没有完成：' + expectedFragment);
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

await runUiSuite({ name: 'ui-survey-full', scenario: 'survey', timeoutMs: 10000 }, async ({
    connection, runStep, addFinding,
}) => {
    await runStep('survey.shell', '调查导航与页面结构', async () => {
        const navigationMs = await connection.navigate('调查工具', 'SurveyView');
        const tree = await connection.getTree();
        const root = rootOf(tree, 'SurveyView');
        for (const text of [
            '团队调查',
            '查询配置',
            '兼容查询（v1，支持旧版和新版）',
            '使用 9721，只按日期查询，兼容旧版和新版节点。',
            '探测节点',
            '查看详情',
            '占比计算基准',
            '重新计算',
            '发起调查',
            '调查结果',
            '打开使用指南',
        ])
            assertUi(textWithin(tree, root, text, true), '调查页缺少：' + text);
        const details = controlForText(tree, textWithin(tree, root, '查看详情'));
        assertUi(details && !isEnabled(details), '探测前查看详情应禁用');
        return { navigationMs };
    });

    await runStep('survey.compatible-query', '兼容查询与本机节点结果', async () => {
        const started = performance.now();
        await activateTextWithin(connection, 'SurveyView', '发起调查');
        await connection.waitForTree(tree => {
            const root = rootOf(tree, 'SurveyView');
            return textWithin(tree, root, '正在调查中…');
        }, 3000, '兼容查询未进入调查中状态');
        const completed = await waitForSurveyComplete(connection, '调查结束：已收到 1 个节点结果');
        const elapsedMs = performance.now() - started;
        const tree = completed.tree;
        const root = rootOf(tree, 'SurveyView');
        for (const text of ['日期：', '总耗时：', '汇总方式：', '按标签分组'])
            assertUi(textWithin(tree, root, text, true), '兼容查询结果缺少：' + text);
        assertUi(!textWithin(tree, root, '节点错误：', true), '兼容查询不应出现节点错误');
        if (elapsedMs > 5000)
            addFinding('warning', 'survey-compatible-slow', '兼容调查超过 5 秒', { elapsedMs });
        return { elapsedMs, completionObservedMs: completed.elapsedMs };
    });

    await runStep('survey.capability-discovery', '本机新版节点能力探测', async () => {
        const started = performance.now();
        await activateTextWithin(connection, 'SurveyView', '探测节点');
        const result = await connection.waitForTree(tree => {
            const root = rootOf(tree, 'SurveyView');
            return textWithin(tree, root, '已发现 1 个新版节点', true);
        }, 5000, '未发现 localhost 新版节点');
        const tree = result.tree;
        const root = rootOf(tree, 'SurveyView');
        const details = controlForText(tree, textWithin(tree, root, '查看详情'));
        assertUi(details && isEnabled(details), '探测后查看详情未启用');
        return { elapsedMs: performance.now() - started };
    });

    await runStep('survey.capability-dialog', '新版节点能力详情', async () => {
        await activateTextWithin(connection, 'SurveyView', '查看详情');
        const result = await connection.waitForTree(tree => rootOf(tree, 'SurveyCapabilitiesView'),
            5000, '新版节点能力对话框未出现');
        const tree = result.tree;
        const root = result.value;
        for (const text of [
            '新版节点能力',
            '新版协议节点',
            '支持明细',
            '查询能力',
            '能力发现、扩展统计',
            '分组能力',
            '标签、日期、优先级',
            '关闭',
        ])
            assertUi(textWithin(tree, root, text, true), '能力详情缺少：' + text);
        const nodeName = descendants(tree, root).find(entry => isVisible(entry)
            && textOf(entry).includes('@') && typeOf(entry).includes('TextBlock'));
        assertUi(nodeName, '能力详情缺少节点名称');
        await closeDialogByText(connection, 'SurveyCapabilitiesView', '关闭');
        return { nodeName: textOf(nodeName) };
    });

    await runStep('survey.extended-controls', '扩展查询筛选、分组与明细控件', async () => {
        let tree = await connection.getTree();
        let root = rootOf(tree, 'SurveyView');
        const mode = comboBySelectedText(tree, root, '兼容查询（v1，支持旧版和新版）');
        assertUi(mode, '找不到查询模式下拉框');
        await selectComboOption(connection, mode, '扩展查询（v2，仅新版）');
        await connection.waitForTree(current => {
            const currentRoot = rootOf(current, 'SurveyView');
            return textWithin(current, currentRoot, '使用 9722，可设置筛选、分组和明细，只返回新版节点。', true);
        }, 5000, '扩展查询模式没有生效');
        await expandExtendedConditions(connection);
        tree = await connection.getTree();
        root = rootOf(tree, 'SurveyView');
        for (const text of [
            '关键词',
            '标签名',
            '标签模式',
            '优先级',
            '分组维度',
            '返回结果明细',
            '仅在扩展查询中生效，明细最多返回 500 条。',
        ])
            assertUi(textWithin(tree, root, text, true), '扩展查询条件缺少：' + text);
        const detailsText = textWithin(tree, root, '返回结果明细');
        const detailsCheck = ancestor(tree, detailsText, entry => typeOf(entry).includes('CheckBox'));
        assertUi(detailsCheck && !isChecked(detailsCheck), '明细开关初始状态不正确');
        await connection.client.send('DOM.focus', { nodeId: detailsCheck.nodeId });
        await connection.pressKey(' ', 'Space', 32);
        await connection.waitForTree(current => {
            const currentRoot = rootOf(current, 'SurveyView');
            const currentText = textWithin(current, currentRoot, '返回结果明细');
            const currentCheck = ancestor(current, currentText, entry => typeOf(entry).includes('CheckBox'));
            return currentCheck && isChecked(currentCheck);
        }, 3000, '明细开关没有启用');
        return { includeDetails: true };
    });

    await runStep('survey.extended-grouping', '扩展查询标签、日期与优先级分组', async () => {
        const samples = [];
        for (const option of ['标签', '日期', '优先级']) {
            let tree = await connection.getTree();
            let root = rootOf(tree, 'SurveyView');
            const currentOptions = ['标签', '日期', '优先级'];
            const selected = currentOptions.find(text => comboBySelectedText(tree, root, text));
            const groupCombo = selected ? comboBySelectedText(tree, root, selected) : null;
            assertUi(groupCombo, '找不到分组维度下拉框');
            if (selected !== option)
                await selectComboOption(connection, groupCombo, option);
            const started = performance.now();
            await activateTextWithin(connection, 'SurveyView', '发起调查');
            const completed = await waitForSurveyComplete(connection, '调查结束：已收到 1 个节点结果');
            const elapsedMs = performance.now() - started;
            tree = completed.tree;
            root = rootOf(tree, 'SurveyView');
            assertUi(textWithin(tree, root, '按' + option + '分组', true), option + '分组结果未显示');
            samples.push({ option, elapsedMs });
        }
        return { samples };
    });

    await runStep('survey.validation-error', '扩展查询无效日期范围错误状态', async () => {
        let tree = await connection.getTree();
        const root = rootOf(tree, 'SurveyView');
        const dateInputs = descendants(tree, root).filter(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox')
            && ancestor(tree, entry, item => typeOf(item).includes('CalendarDatePicker')));
        assertUi(dateInputs.length >= 2, '调查页日期输入框不足两个');
        await connection.replaceText(dateInputs[0], '2026-08-21');
        await connection.pressKey('Enter', 'Enter', 13);
        tree = await connection.getTree();
        const currentRoot = rootOf(tree, 'SurveyView');
        const currentDateInputs = descendants(tree, currentRoot).filter(entry => isVisible(entry)
            && typeOf(entry).includes('TextBox')
            && ancestor(tree, entry, item => typeOf(item).includes('CalendarDatePicker')));
        await connection.replaceText(currentDateInputs[1], '2026-08-20');
        await connection.pressKey('Enter', 'Enter', 13);
        await activateTextWithin(connection, 'SurveyView', '发起调查');
        const errorResult = await waitForSurveyComplete(connection,
            '正在调查：已收到 0 个节点结果；节点错误：开始日期不能晚于结束日期');
        const completed = await waitForSurveyComplete(connection,
            '调查结束：已收到 0 个节点结果；节点错误：开始日期不能晚于结束日期');
        return { errorObservedMs: errorResult.elapsedMs, completionObservedMs: completed.elapsedMs };
    });

    await runStep('survey.performance', '调查页视觉树与交互响应速度', async () => {
        const treeSamples = [];
        for (let index = 0; index < 8; index++) {
            const started = performance.now();
            await connection.getTree();
            treeSamples.push(performance.now() - started);
        }
        const actionSamples = [];
        for (let index = 0; index < 5; index++) {
            const started = performance.now();
            await activateTextWithin(connection, 'SurveyView', '重新计算');
            actionSamples.push(performance.now() - started);
        }
        const treeP95 = percentile(treeSamples, 0.95);
        const actionP95 = percentile(actionSamples, 0.95);
        if (treeP95 > 250)
            addFinding('warning', 'survey-tree-slow', '调查页视觉树读取 P95 超过 250 毫秒', { treeSamples });
        if (actionP95 > 500)
            addFinding('warning', 'survey-recalc-slow', '调查重新计算 P95 超过 500 毫秒', { actionSamples });
        return { treeSamplesMs: treeSamples, treeP95, actionSamplesMs: actionSamples, actionP95 };
    });
});
