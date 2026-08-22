#!/usr/bin/env node

import {
    ancestor,
    connectUiTest,
    controlForText,
    delay,
    descendants,
    findByName,
    findByText,
    isEnabled,
    isVisible,
    textOf,
    typeOf,
    writeSuiteReport,
} from './ui-cdp.mjs';

const startedAt = new Date();
const steps = [];
const fail = (message) => { throw new Error(message); };
const assert = (condition, message) => { if (!condition) fail(message); };

async function step(id, title, action) {
    const started = performance.now();
    const record = { id, title, status: 'running' };
    steps.push(record);
    try {
        record.details = await action();
        record.status = 'passed';
        record.durationMs = performance.now() - started;
        console.log('PASS ' + id + ' ' + Math.round(record.durationMs) + 'ms');
    }
    catch (error) {
        record.status = 'failed';
        record.durationMs = performance.now() - started;
        record.error = error instanceof Error ? error.message : String(error);
        console.error('FAIL ' + id + ' ' + record.error);
    }
}

const main = await connectUiTest({ targetTitle: 'Diary Tools NG', timeoutMs: 10000 });
let editor;
try {
    await step('editor.open', '从脚本管理打开独立编辑器', async () => {
        await main.navigate('脚本管理', 'ScriptManagementView');
        let tree = await main.getTree();
        const scriptName = tree.entries.find(entry => isVisible(entry) && textOf(entry).startsWith('UI CSharp '));
        assert(scriptName, '找不到自动化创建的 C# 脚本');
        const row = ancestor(tree, scriptName, entry => typeOf(entry).includes('ListBoxItem'));
        await main.clickNode(row);
        tree = await main.getTree();
        const overviewText = findByText(tree, '概览');
        await main.clickNode(ancestor(tree, overviewText, entry => typeOf(entry).includes('TabItem')));
        await delay(100);
        tree = await main.getTree();
        const openSource = tree.entries.find(entry => isVisible(entry)
            && typeOf(entry).includes('Button') && textOf(entry) === '打开源码');
        assert(openSource && isEnabled(openSource), '打开源码按钮不可用');
        const before = await fetch('http://127.0.0.1:' + main.state.port + '/json').then(response => response.json());
        if (!before.some(target => target.title.includes('脚本编辑器')))
            await main.clickNode(openSource);
        const started = performance.now();
        let targets;
        while (performance.now() - started < 10000) {
            targets = await fetch('http://127.0.0.1:' + main.state.port + '/json').then(response => response.json());
            if (targets.some(target => target.title.includes('脚本编辑器')))
                break;
            await delay(50);
        }
        const target = targets?.find(item => item.title.includes('脚本编辑器'));
        assert(target, '脚本编辑器 CDP target 未出现');
        return { title: target.title, targetCount: targets.length };
    });

    editor = await connectUiTest({ targetTitleIncludes: '脚本编辑器', timeoutMs: 10000 });

    await step('editor.structure', '编辑器命令与代码区', async () => {
        const tree = await editor.getTree();
        assert(tree.entries.some(entry => typeOf(entry).includes('ScriptEditorWindow')), '脚本编辑器窗口根节点缺失');
        for (const text of ['API Reference', '编译检查', '保存', '另存为', '放弃修改', '关闭'])
            assert(findByText(tree, text), '脚本编辑器缺少：' + text);
        assert(findByName(tree, 'Editor'), '代码编辑器控件缺失');
        assert(findByName(tree, 'CheckButton') && findByName(tree, 'SaveButton')
            && findByName(tree, 'SaveAsButton') && findByName(tree, 'DiscardButton'), '稳定命名命令缺失');
        return { title: editor.target.title };
    });

    await step('editor.compile-check', 'C# 编译检查', async () => {
        const started = performance.now();
        let activation;
        let lastError;
        for (let attempt = 0; attempt < 3; attempt++) {
            const tree = await editor.getTree();
            const button = findByName(tree, 'CheckButton');
            assert(button && isEnabled(button), '编译检查按钮不可用');
            if (attempt === 0)
                await editor.clickNode(button);
            else {
                await editor.client.send('DOM.focus', { nodeId: button.nodeId });
                await editor.pressKey('Enter', 'Enter', 13);
            }
            try {
                activation = await editor.waitForTree(current => {
                    const checkButton = findByName(current, 'CheckButton');
                    const passed = findByText(current, '编译检查通过');
                    const failed = findByText(current, '编译检查失败')
                        ?? findByText(current, '编译检查失败，请查看诊断');
                    return passed ?? failed ?? (checkButton && !isEnabled(checkButton) ? checkButton : null);
                }, 1800, '未观察到编译检查启动');
                break;
            }
            catch (error) {
                lastError = error;
                await delay(80);
            }
        }
        if (!activation)
            throw lastError;
        const result = await editor.waitForTree(current => {
            const passed = findByText(current, '编译检查通过');
            const failed = findByText(current, '编译检查失败')
                ?? findByText(current, '编译检查失败，请查看诊断');
            return passed ? { passed: true } : failed ? { passed: false, status: textOf(failed) } : null;
        }, 30000, '编译检查没有完成');
        assert(result.value.passed, result.value.status ?? '编译检查失败');
        return {
            compileMs: performance.now() - started,
            activationMs: activation.elapsedMs,
            observedMs: result.elapsedMs,
        };
    });

    await step('editor.close', '安全关闭编辑器窗口', async () => {
        const tree = await editor.getTree();
        const closeText = findByText(tree, '关闭');
        const closeButton = controlForText(tree, closeText);
        assert(closeButton, '编辑器关闭按钮缺失');
        await editor.client.send('DOM.focus', { nodeId: closeButton.nodeId });
        await editor.pressKey('Enter', 'Enter', 13);
        const started = performance.now();
        while (performance.now() - started < 10000) {
            const targets = await fetch('http://127.0.0.1:' + main.state.port + '/json').then(response => response.json());
            if (!targets.some(target => target.title.includes('脚本编辑器')))
                return { closeMs: performance.now() - started };
            await delay(50);
        }
        fail('脚本编辑器没有关闭');
    });
}
finally {
    editor?.close();
    main.close();
}

const completedAt = new Date();
const failed = steps.filter(item => item.status === 'failed');
const report = await writeSuiteReport('ui-script-editor', {
    status: failed.length === 0 ? 'passed' : 'failed',
    scenario: 'extended',
    startedAt: startedAt.toISOString(),
    completedAt: completedAt.toISOString(),
    durationMs: completedAt.getTime() - startedAt.getTime(),
    summary: { total: steps.length, passed: steps.length - failed.length, failed: failed.length },
    steps,
});
console.log(JSON.stringify(report, null, 2));
if (failed.length)
    process.exitCode = 1;
