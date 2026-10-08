#!/usr/bin/env node

import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import {
    ancestor,
    controlForText,
    delay,
    descendants,
    findByName,
    findByText,
    isChecked,
    isVisible,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const mockPort = 19321;
const mockRequests = [];

function writeSse(response, payloads) {
    response.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        Connection: 'close',
    });
    for (const payload of payloads)
        response.write('data: ' + JSON.stringify(payload) + '\n\n');
    response.end('data: [DONE]\n\n');
}

const mockServer = http.createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request)
        chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    mockRequests.push(body);
    const hasToolResult = body.messages?.at(-1)?.role === 'tool';
    if (hasToolResult) {
        writeSse(response, [{
            choices: [{
                delta: { content: '已处理用户拒绝，不执行写入。' },
                finish_reason: 'stop',
            }],
        }]);
        return;
    }
    const argumentsJson = JSON.stringify({
        date: '2026-10-08',
        title: 'AI UI 确认测试',
        hours: 1,
        priority: 0,
        tagIds: [],
        extraFields: [],
        note: '由假模型生成的待确认备注',
        idempotencyKey: 'ui-confirmation-test',
    });
    writeSse(response, [{
        choices: [{
            delta: {
                tool_calls: [{
                    index: 0,
                    id: 'call_ui_confirmation',
                    function: {
                        name: 'diary_create_work_item',
                        arguments: argumentsJson,
                    },
                }],
            },
            finish_reason: 'tool_calls',
        }],
    }]);
});

await new Promise((resolve, reject) => {
    mockServer.once('error', reject);
    mockServer.listen(mockPort, '127.0.0.1', resolve);
});

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
    assertUi(root, '页面或对话框不可见：' + typeName);
    const label = textWithin(tree, root, text);
    assertUi(label, typeName + ' 缺少操作：' + text);
    const control = controlForText(tree, label);
    assertUi(control, '操作不可激活：' + text);
    await connection.clickNode(control);
}

async function dismissOnboarding(connection) {
    const tree = await connection.getTree();
    if (!rootOf(tree, 'OnboardingView'))
        return;
    await activateTextWithin(connection, 'OnboardingView', '稍后再看');
    await connection.waitForTree(current => !rootOf(current, 'OnboardingView'), 8000,
        '首次使用引导没有关闭');
}

async function openSettings(connection) {
    await connection.openSettingsMenuItem('ProgramSettingsMenuItem');
    return connection.waitForTree(tree => rootOf(tree, 'SettingsView'), 8000,
        '程序设置未打开');
}

async function expandSettingsGroup(connection, groupName, expectedText) {
    let tree = await connection.getTree();
    const settings = rootOf(tree, 'SettingsView');
    const label = textWithin(tree, settings, groupName);
    assertUi(label, '设置分组不存在：' + groupName);
    const expander = ancestor(tree, label, entry => typeOf(entry).includes('Expander'));
    assertUi(expander, '设置分组缺少 Expander：' + groupName);
    const toggle = descendants(tree, expander).find(entry => entry.a.Name === 'ExpanderHeader');
    assertUi(toggle, '设置分组缺少展开控件：' + groupName);
    if (!isChecked(toggle))
        await connection.clickNode(toggle);
    return connection.waitForTree(current => {
        const currentSettings = rootOf(current, 'SettingsView');
        return textWithin(current, currentSettings, expectedText);
    }, 8000, '设置分组没有展开：' + groupName);
}

async function assertComboOptions(connection, comboName, expectedOptions) {
    let tree = await connection.getTree();
    let combo = findByName(tree, comboName);
    assertUi(combo, 'AI 设置缺少下拉框：' + comboName);
    await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: combo.nodeId });
    await delay(120);
    tree = await connection.getTree();
    combo = findByName(tree, comboName);
    const glyph = descendants(tree, combo).find(entry => entry.a.Name === 'DropDownGlyph');
    assertUi(glyph, comboName + ' 缺少下拉按钮');
    await connection.clickNode(glyph);
    await delay(200);
    await connection.waitForTree(current => expectedOptions.every(option => findByText(current, option)),
        3000, comboName + ' 下拉选项为空或显示不完整');
    await connection.pressKey('Escape', 'Escape', 27);
    await connection.waitForTree(current => expectedOptions.some(option => !findByText(current, option)),
        3000, comboName + ' 下拉框没有关闭');
}

try {
await runUiSuite({ name: 'ui-ai-agent', stopOnFailure: true }, async ({ connection, runStep, assertUi }) => {
    await runStep('prepare', '关闭首次使用引导', async () => {
        await dismissOnboarding(connection);
    });

    await runStep('navigation', 'AI 模块贡献导航并打开页面', async () => {
        const tree = await connection.getTree();
        const navigation = findByText(tree, 'AI 助手', entry =>
            Boolean(ancestor(tree, entry, item => typeOf(item).includes('SelectionListItem'))));
        assertUi(navigation, '启用模块后未出现 AI 助手导航');
        const elapsedMs = await connection.navigate('AI 助手', 'AiAgentPageView');
        return { elapsedMs };
    });

    await runStep('empty-state', '无连接状态和隐私边界可见', async () => {
        const tree = await connection.getTree();
        const page = rootOf(tree, 'AiAgentPageView');
        assertUi(page, 'AI 助手页面不可见');
        assertUi(textWithin(tree, page, '本地备注不会提供给模型。', true),
            '页面未说明本地备注边界');
        assertUi(textWithin(tree, page, '连接已通过 Agent 工具闭环测试。', true),
            '页面未显示 Agent 连接状态');
        assertUi(textWithin(tree, page, 'Agent 模式'), '页面未显示 Agent 模式');
        assertUi(textWithin(tree, page, '工具调用'), '页面缺少工具调用区域');
        return connection.screenshot('ai-agent-page.png');
    });

    await runStep('write-confirmation', '真实工具链显示写入确认卡片并可拒绝', async () => {
        let tree = await connection.getTree();
        const page = rootOf(tree, 'AiAgentPageView');
        const input = findByName(tree, 'AiAgentInputTextBox');
        assertUi(input, '找不到 AI 输入框');
        await connection.replaceText(input, '请创建一个测试事项');
        await connection.pressKey('Enter', 'Enter', 13);
        const pending = await connection.waitForTree(current => {
            const currentPage = rootOf(current, 'AiAgentPageView');
            const title = textWithin(current, currentPage, '确认创建事项');
            return title && Number(title.a.Width) > 0 ? title : null;
        }, 10000, '写入确认卡片未出现');
        const pendingPage = rootOf(pending.tree, 'AiAgentPageView');
        assertUi(textWithin(pending.tree, pendingPage, 'AI UI 确认测试'), '确认卡片未展示实际标题');
        assertUi(textWithin(pending.tree, pendingPage, '不会永久跳过确认。', true),
            '确认卡片未说明逐次确认边界');
        const screenshot = await connection.screenshot('ai-agent-write-confirmation.png');
        const rejectText = [pending.value, ...descendants(pending.tree, pendingPage)]
            .find(entry => textOf(entry) === '拒绝' && Number(entry.a.Width) > 0);
        const reject = rejectText && controlForText(pending.tree, rejectText);
        assertUi(reject, '确认卡片缺少拒绝按钮');
        await connection.client.send('DOM.focus', { nodeId: reject.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        await connection.waitForTree(current => {
            const currentPage = rootOf(current, 'AiAgentPageView');
            return textWithin(current, currentPage, '已完成 · 2 轮 · 1 次工具调用');
        }, 10000, '拒绝写入后 Agent 未完成工具闭环');
        assertUi(mockRequests.length === 2, '假模型请求轮次不符合工具闭环预期');
        const toolResult = mockRequests[1].messages.find(message => message.role === 'tool');
        assertUi(toolResult?.content?.includes('user_rejected'), '拒绝结果未返回模型');
        return screenshot;
    });

    await runStep('keyboard-send', 'Ctrl+Enter 发送且 Shift+Enter 保留换行', async () => {
        let tree = await connection.getTree();
        let input = findByName(tree, 'AiAgentInputTextBox');
        assertUi(input, '找不到 AI 输入框');
        await connection.replaceText(input, '第一行');
        await connection.pressKey('Enter', 'Enter', 13, 8);
        await connection.appendText(input, '第二行');
        tree = await connection.getTree();
        input = findByName(tree, 'AiAgentInputTextBox');
        assertUi(textOf(input).includes('第一行') && textOf(input).includes('第二行'),
            'Shift+Enter 没有保留多行输入');
        const requestCountBefore = mockRequests.length;
        await connection.pressKey('Enter', 'Enter', 13, 2);
        const pending = await connection.waitForTree(current => {
            const currentPage = rootOf(current, 'AiAgentPageView');
            const title = textWithin(current, currentPage, '确认创建事项');
            return title && Number(title.a.Width) > 0 ? title : null;
        }, 10000, 'Ctrl+Enter 没有发送请求');
        assertUi(mockRequests.length === requestCountBefore + 1, 'Ctrl+Enter 请求次数不正确');
        const pendingPage = rootOf(pending.tree, 'AiAgentPageView');
        const rejectText = [pending.value, ...descendants(pending.tree, pendingPage)]
            .find(entry => textOf(entry) === '拒绝' && Number(entry.a.Width) > 0);
        const reject = rejectText && controlForText(pending.tree, rejectText);
        assertUi(reject, 'Ctrl+Enter 确认卡片缺少拒绝按钮');
        await connection.clickNode(reject);
        await connection.waitForTree(current => {
            const currentPage = rootOf(current, 'AiAgentPageView');
            return !textWithin(current, currentPage, '确认创建事项');
        }, 10000, 'Ctrl+Enter 请求拒绝后确认卡片没有关闭');
    });

    await runStep('settings', 'AI 设置贡献完整可见', async () => {
        await openSettings(connection);
        const result = await expandSettingsGroup(connection, '可选模块', 'AI 助手设置');
        const tree = result.tree;
        const settings = rootOf(tree, 'SettingsView');
        for (const text of [
            'Diary 只读工具',
            '确认后创建事项',
            '网页搜索',
            '网页读取',
            'MCP 工具',
            'MCP Servers（高级 JSON 配置）',
            '网页访问策略（重启后生效）',
            'AI 默认代理（连接选择“继承”时使用）',
            '新增',
            '复制',
            '删除连接时清理未被其他连接使用的本地凭据',
            '设为默认',
            'Credential Reference',
            '无需凭据',
            '保存设置',
        ]) {
            assertUi(textWithin(tree, settings, text), 'AI 设置缺少：' + text);
        }
        assertUi(textWithin(tree, settings, '密钥正文不会显示或写入普通设置文件。', true),
            'AI 设置未说明凭据边界');
        await assertComboOptions(connection, 'AiProtocolComboBox', [
            'OpenAiChatCompletions', 'OpenAiResponses', 'AnthropicMessages',
        ]);
        await assertComboOptions(connection, 'AiAuthenticationComboBox', ['None', 'Bearer', 'Header']);
        await assertComboOptions(connection, 'AiProxyModeComboBox', [
            'Inherit', 'System', 'Direct', 'Custom',
        ]);
        const credentialReference = textWithin(tree, settings, 'Credential Reference');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: credentialReference.nodeId });
        await delay(120);
        const screenshot = await connection.screenshot('ai-agent-settings.png');
        let currentTree = await connection.getTree();
        let removeButton = findByName(currentTree, 'RemoveAiConnectionButton');
        assertUi(removeButton, 'AI 设置缺少删除连接按钮');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: removeButton.nodeId });
        await delay(120);
        currentTree = await connection.getTree();
        removeButton = findByName(currentTree, 'RemoveAiConnectionButton');
        await connection.clickNode(removeButton);
        await connection.waitForTree(current => {
            const currentSettings = rootOf(current, 'SettingsView');
            return textWithin(current, currentSettings, '保存设置成功后将清理未被其他连接使用的本地凭据。', true);
        }, 5000, '删除连接没有进入保存后清理状态');
        const emptyState = await connection.waitForTree(current => {
            const currentSettings = rootOf(current, 'SettingsView');
            const prompt = textWithin(current, currentSettings, '尚未添加模型连接');
            const addButton = findByName(current, 'AddAiConnectionEmptyButton');
            return prompt && addButton ? addButton : null;
        }, 5000, '删除最后一个连接后没有显示明确的空状态');
        await connection.clickNode(emptyState.value);
        await connection.waitForTree(current => {
            const currentSettings = rootOf(current, 'SettingsView');
            return textWithin(current, currentSettings, '连接 1');
        }, 5000, '从空状态新增连接后没有打开连接编辑器');
        currentTree = await connection.getTree();
        let reloadButton = findByName(currentTree, 'ReloadAiSettingsButton');
        assertUi(reloadButton, 'AI 设置缺少重新加载按钮');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: reloadButton.nodeId });
        await delay(120);
        currentTree = await connection.getTree();
        reloadButton = findByName(currentTree, 'ReloadAiSettingsButton');
        await connection.clickNode(reloadButton);
        await connection.waitForTree(current => {
            const currentSettings = rootOf(current, 'SettingsView');
            return textWithin(current, currentSettings, 'UI 假模型');
        }, 5000, '重新加载未恢复尚未保存的连接');
        return screenshot;
    });

    await runStep('seed-copy', '嵌套 AI 设置种子已递归复制', async () => {
        const nestedPath = path.join(connection.state.profile, 'config', 'ai-agent', 'settings.json');
        const settings = JSON.parse(await fs.readFile(nestedPath, 'utf8'));
        assertUi(settings.schemaVersion === 1, '嵌套 AI 设置种子未复制或内容错误');
        const capabilities = settings.settings.capabilities['ui-mock'];
        assertUi(capabilities.supportsStreamingTools === true, '流式工具能力种子未复制');
        assertUi(capabilities.supportsParallelTools === true, '并行工具能力种子未复制');
        assertUi(capabilities.supportsForcedToolChoice === true, '强制工具能力种子未复制');
    });
});
}
finally {
    await new Promise(resolve => mockServer.close(resolve));
}
