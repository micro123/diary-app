#!/usr/bin/env node

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
    localDateText,
    nameOf,
    textOf,
    textWithinNamedControl,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const ctrl = 2;
const stamp = Date.now().toString(36);
const tagName = 'UI附加字段-' + stamp;
const workTitle = 'UI附加字段事项-' + stamp;
const readonlyTitle = 'UI只读附加字段事项';
const today = localDateText();
const fields = [
    { type: 'Text', label: '单行文本', key: 'ui.' + stamp + '.text', description: '单行文本说明' },
    { type: 'MultilineText', label: '多行文本', key: 'ui.' + stamp + '.multiline', description: '多行文本说明' },
    { type: 'Integer', label: '整数', key: 'ui.' + stamp + '.integer', description: '整数说明' },
    { type: 'Decimal', label: '小数', key: 'ui.' + stamp + '.decimal', description: '小数说明' },
    { type: 'Boolean', label: '布尔值', key: 'ui.' + stamp + '.boolean', description: '布尔值说明' },
    { type: 'Date', label: '日期', key: 'ui.' + stamp + '.date', description: '日期说明' },
    { type: 'Time', label: '时间', key: 'ui.' + stamp + '.time', description: '时间说明' },
    { type: 'DateTime', label: '日期时间', key: 'ui.' + stamp + '.datetime', description: '日期时间说明' },
    { type: 'Choice', label: '单选项', key: 'ui.' + stamp + '.choice', description: '单选项说明', options: '开发\n测试\n完成' },
];

function rootOf(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function named(tree, name) {
    const matches = tree.entries.filter(entry => isVisible(entry) && nameOf(entry) === name);
    return matches.find(entry => Number(entry.a.Width) > 0 && Number(entry.a.Height) > 0) ?? matches[0];
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

async function activateControl(connection, control) {
    assertUi(control, '控件不存在');
    await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: control.nodeId }).catch(() => {});
    await connection.client.send('DOM.focus', { nodeId: control.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
    await delay(50);
}

async function nodeBounds(connection, entry) {
    assertUi(entry, '待测布局节点不存在');
    const result = await connection.client.send('DOM.getBoxModel', { nodeId: entry.nodeId });
    const quad = result.model.border;
    return {
        left: quad[0],
        top: quad[1],
        right: quad[4],
        bottom: quad[5],
    };
}

function boundsOverlap(first, second) {
    return first.left < second.right && first.right > second.left
        && first.top < second.bottom && first.bottom > second.top;
}

async function assertNoOverlap(connection, first, second, message) {
    const [firstBounds, secondBounds] = await Promise.all([
        nodeBounds(connection, first),
        nodeBounds(connection, second),
    ]);
    assertUi(!boundsOverlap(firstBounds, secondBounds), message);
}

async function openSettingsText(connection, text, expectedType) {
    let menu;
    let lastError;
    for (let attempt = 0; attempt < 3; attempt++) {
        const tree = await connection.getTree();
        const button = named(tree, 'SettingsMenuButton');
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
    return connection.waitForTree(tree => rootOf(tree, expectedType), 10000,
        '对话框未出现：' + expectedType);
}

async function selectComboOption(connection, combo, optionText) {
    await activateControl(connection, combo);
    const option = await connection.waitForTree(tree => {
        const label = tree.entries.find(entry => isVisible(entry)
            && hasAncestorType(tree, entry, 'ComboBoxItem') && textOf(entry) === optionText);
        return label && ancestor(tree, label, entry => typeOf(entry).includes('ComboBoxItem'));
    }, 5000, '下拉选项未出现：' + optionText);
    await connection.clickNode(option.value);
    await delay(80);
}

function itemRow(tree, labelText, buttonName) {
    const label = findByText(tree, labelText);
    let current = label;
    while (current) {
        if (descendants(tree, current).some(entry => nameOf(entry) === buttonName))
            return current;
        current = tree.byId.get(current.parentId);
    }
    return null;
}

function descendantByName(tree, root, name) {
    return descendants(tree, root).find(entry => isVisible(entry) && nameOf(entry) === name);
}

function textWithinControl(tree, name) {
    const control = named(tree, name);
    return control ? [control, ...descendants(tree, control)].map(textOf).filter(Boolean).join(' ') : '';
}

function editableText(tree, namedControl) {
    const control = named(tree, namedControl);
    if (!control)
        return null;
    if (typeOf(control).includes('TextBox'))
        return control;
    return descendants(tree, control).find(entry => isVisible(entry) && typeOf(entry).includes('TextBox'));
}

async function selectTagInEditor(connection) {
    const findTag = tree => {
        const labels = tree.entries.filter(entry => isVisible(entry) && textOf(entry) === tagName
            && hasAncestorType(tree, entry, 'ListBoxItem'));
        const label = labels.find(entry => Number(entry.a.Width) > 0 && Number(entry.a.Height) > 0) ?? labels[0];
        const item = label && ancestor(tree, label, entry => typeOf(entry).includes('ListBoxItem'));
        return label && item ? { label, item } : null;
    };
    let lastError;
    for (let attempt = 0; attempt < 3; attempt++) {
        const candidate = await connection.waitForTree(findTag, 8000, '标签列表中找不到：' + tagName);
        if (attempt === 0)
            await connection.clickNode(candidate.value.label);
        else if (attempt === 1)
            await connection.clickNode(candidate.value.item);
        else {
            await connection.client.send('DOM.focus', { nodeId: candidate.value.item.nodeId });
            await connection.pressKey('Space', 'Space', 32);
        }
        try {
            await connection.waitForTree(tree => {
                const current = findTag(tree);
                return current && (current.item.a.IsSelected ?? '').toLowerCase() === 'true';
            }, 1800, '标签未真正选中：' + tagName);
            return;
        }
        catch (error) {
            lastError = error;
            await delay(80);
        }
    }
    throw lastError;
}

async function openExtraFieldsTab(connection) {
    const tree = await connection.getTree();
    const tab = named(tree, 'TagExtraFieldsTab');
    assertUi(tab, '标签编辑器缺少附加字段页签');
    await connection.clickNode(tab);
    await delay(80);
    await connection.waitForTree(current => named(current, 'AddExtraFieldButton'), 5000,
        '附加字段页签未打开');
}

async function addField(connection, field, index) {
    let tree = await connection.getTree();
    await activateControl(connection, named(tree, 'AddExtraFieldButton'));
    await connection.waitForTree(current => named(current, 'TagExtraFieldEditorRoot'), 8000,
        '新增字段对话框未出现');
    tree = await connection.getTree();
    await connection.replaceText(named(tree, 'ExtraFieldLabelInput'), field.label);
    tree = await connection.getTree();
    await connection.replaceText(named(tree, 'ExtraFieldKeyInput'), index === 0 ? 'invalid key' : field.key);
    tree = await connection.getTree();
    if (field.type !== 'Text')
        await selectComboOption(connection, named(tree, 'ExtraFieldTypeInput'), field.type);
    tree = await connection.getTree();
    await connection.replaceText(named(tree, 'ExtraFieldDescriptionInput'), field.description);
    tree = await connection.getTree();
    const sortInput = editableText(tree, 'ExtraFieldSortOrderInput');
    if (sortInput)
        await connection.replaceText(sortInput, String(index));
    if (field.options) {
        await connection.waitForTree(current => named(current, 'ExtraFieldOptionsInput'), 3000,
            'Choice 字段没有显示选项编辑器');
        tree = await connection.getTree();
        await connection.replaceText(named(tree, 'ExtraFieldOptionsInput'), field.options);
    }
    if (index === 0) {
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveExtraFieldButton'));
        await connection.waitForTree(current => findByTextContains(current, '字段标识无效'), 5000,
            '非法 FieldKey 未显示校验错误');
        tree = await connection.getTree();
        await connection.replaceText(named(tree, 'ExtraFieldKeyInput'), field.key);
    }
    tree = await connection.getTree();
    await activateControl(connection, named(tree, 'SaveExtraFieldButton'));
    await connection.waitForTree(current => !named(current, 'TagExtraFieldEditorRoot'), 8000,
        '字段编辑器未关闭：' + field.label);
    await connection.waitForTree(current => findByText(current, field.label), 5000,
        '字段未加入列表：' + field.label);
}

async function selectWork(connection, title) {
    let lastError;
    for (let attempt = 0; attempt < 3; attempt++) {
        const found = await connection.waitForTree(tree => {
            const dailyList = named(tree, 'DailyItemList');
            const label = dailyList && descendants(tree, dailyList).find(entry => isVisible(entry)
                && textOf(entry) === title && hasAncestorType(tree, entry, 'ListBoxItem'));
            const item = label && ancestor(tree, label, entry => typeOf(entry).includes('ListBoxItem'));
            return label && item ? { label, item } : null;
        }, 8000, '当天事项列表找不到：' + title);
        await connection.clickNode(found.value.item);
        try {
            return await connection.waitForTree(tree => {
                const input = named(tree, 'WorkTitleInput');
                return input && textOf(input) === title ? input : null;
            }, 1200, '事项未选中：' + title);
        }
        catch (error) {
            lastError = error;
            await delay(80);
        }
    }
    throw lastError;
}

async function setTimePicker(connection, pickerName, hour, minute) {
    let tree = await connection.getTree();
    const picker = named(tree, pickerName);
    assertUi(picker, '找不到时间选择器：' + pickerName);
    const flyoutButton = descendants(tree, picker).find(entry => isVisible(entry)
        && nameOf(entry) === 'PART_FlyoutButton' && Number(entry.a.Width) > 0);
    assertUi(flyoutButton, '时间选择器缺少弹层按钮：' + pickerName);
    await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: picker.nodeId }).catch(() => {});
    await connection.clickNode(flyoutButton);
    const opened = await connection.waitForTree(current => {
        const presenter = rootOf(current, 'TimePickerPresenter');
        const hourSelector = named(current, 'PART_HourSelector');
        const minuteSelector = named(current, 'PART_MinuteSelector');
        return presenter && hourSelector && minuteSelector ? { hourSelector, minuteSelector } : null;
    }, 5000, '时间选择器弹层未出现：' + pickerName);
    for (const [selector, value, modulus] of [
        [opened.value.hourSelector, hour, 24],
        [opened.value.minuteSelector, minute, 60],
    ]) {
        tree = await connection.getTree();
        const liveSelector = named(tree, nameOf(selector));
        const selected = descendants(tree, liveSelector).find(entry => typeOf(entry).includes('ListBoxItem')
            && (entry.a.IsSelected ?? '').toLowerCase() === 'true');
        assertUi(selected, '时间选择器没有当前选中项：' + nameOf(selector));
        const current = Number(textOf(selected));
        const down = (value - current + modulus) % modulus;
        const up = (current - value + modulus) % modulus;
        await connection.client.send('DOM.focus', { nodeId: liveSelector.nodeId });
        const key = up < down ? ['ArrowUp', 'ArrowUp', 38] : ['ArrowDown', 'ArrowDown', 40];
        const steps = Math.min(up, down);
        for (let index = 0; index < steps; index++)
            await connection.pressKey(key[0], key[1], key[2]);
        await connection.waitForTree(currentTree => {
            const currentSelector = named(currentTree, nameOf(selector));
            return descendants(currentTree, currentSelector).some(entry => typeOf(entry).includes('ListBoxItem')
                && (entry.a.IsSelected ?? '').toLowerCase() === 'true' && Number(textOf(entry)) === value);
        }, 3000, '时间选择器未切换到目标值：' + value);
    }
    tree = await connection.getTree();
    const accept = named(tree, 'PART_AcceptButton');
    assertUi(accept, '时间选择器缺少确定按钮');
    await connection.clickNode(accept);
    await connection.waitForTree(current => !rootOf(current, 'TimePickerPresenter'), 5000,
        '时间选择器弹层未关闭');
}

function assertEditorValue(tree, name, expected, message) {
    const input = named(tree, name);
    assertUi(input && textOf(input) === expected, message + '，实际：' + (input ? textOf(input) : '<missing>'));
}

await runUiSuite({ name: 'ui-extra-fields-full', scenario: 'extra-fields', timeoutMs: 12000, stopOnFailure: true }, async ({
    connection, runStep,
}) => {
    await runStep('extra-fields.definition-create', '创建 9 类字段并校验非法 FieldKey', async () => {
        await openSettingsText(connection, '标签设置', 'TagEditorView');
        let tree = await connection.getTree();
        await connection.replaceText(named(tree, 'TagNameInput'), tagName);
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'AddTagButton'));
        await selectTagInEditor(connection);
        await openExtraFieldsTab(connection);
        for (let index = 0; index < fields.length; index++)
            await addField(connection, fields[index], index);
        tree = await connection.getTree();
        for (const field of fields)
            assertUi(findByText(tree, field.label), '字段列表缺少：' + field.label);
        const tagLabel = findByText(tree, tagName, entry => hasAncestorType(tree, entry, 'ListBoxItem'));
        const tagRow = tagLabel && ancestor(tree, tagLabel, entry => typeOf(entry).includes('ListBoxItem'));
        const fieldCount = descendants(tree, tagRow).find(entry => textOf(entry).includes('个字段'));
        const deleteButton = descendants(tree, tagRow).find(entry => isVisible(entry)
            && typeOf(entry).includes('Button'));
        await assertNoOverlap(connection, tagLabel, fieldCount, '标签名称与字段数量发生重叠');
        await assertNoOverlap(connection, tagLabel, deleteButton, '标签名称与删除按钮发生重叠');
        await assertNoOverlap(connection, fieldCount, deleteButton, '字段数量与删除按钮发生重叠');
        return { tagName, fieldCount: fields.length, invalidKeyRejected: true, listLayoutNoOverlap: true };
    });

    await runStep('extra-fields.definition-persist', '保存、重开并验证字段定义不可变项', async () => {
        await delay(250);
        let tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveTagSettingsButton'));
        await connection.waitForTree(current => !rootOf(current, 'TagEditorView'), 10000,
            '标签设置未保存关闭');
        await delay(200);
        await openSettingsText(connection, '标签设置', 'TagEditorView');
        await selectTagInEditor(connection);
        await openExtraFieldsTab(connection);
        tree = await connection.getTree();
        for (const field of fields)
            assertUi(findByText(tree, field.label), '重开后字段丢失：' + field.label);
        const row = itemRow(tree, fields[0].label, 'EditExtraFieldButton');
        const editButton = descendantByName(tree, row, 'EditExtraFieldButton');
        await activateControl(connection, editButton);
        const editor = await connection.waitForTree(current => named(current, 'TagExtraFieldEditorRoot'), 8000,
            '编辑字段对话框未出现');
        const keyInput = named(editor.tree, 'ExtraFieldKeyInput');
        const typeInput = named(editor.tree, 'ExtraFieldTypeInput');
        assertUi(!isEffectivelyEnabled(editor.tree, keyInput), '已有字段 FieldKey 仍可编辑');
        assertUi(!isEffectivelyEnabled(editor.tree, typeInput), '已有字段类型仍可编辑');
        await connection.replaceText(named(editor.tree, 'ExtraFieldDescriptionInput'), '单行文本说明-已修改');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveExtraFieldButton'));
        await connection.waitForTree(current => !named(current, 'TagExtraFieldEditorRoot'), 8000,
            '字段编辑器未关闭');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveTagSettingsButton'));
        await connection.waitForTree(current => !rootOf(current, 'TagEditorView'), 10000,
            '字段定义未持久化');
        return { reopened: true, immutableKeyAndType: true, descriptionUpdated: true };
    });

    await runStep('extra-fields.editor-types', '事项中呈现 9 类类型化编辑器', async () => {
        await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.clickByName('NewWorkItemButton');
        let opened = await connection.waitForTree(tree => {
            const input = named(tree, 'WorkTitleInput');
            return input && textOf(input) === '' && isEffectivelyEnabled(tree, input) ? input : null;
        }, 8000, '新建事项未打开');
        await connection.replaceText(opened.value, workTitle);
        await connection.clickByName('UseTodayButton');
        let tree = await connection.getTree();
        const timeInput = editableText(tree, 'WorkTimeInput');
        assertUi(timeInput, '事项耗时输入框不存在');
        await connection.replaceText(timeInput, '0.5');
        tree = await connection.getTree();
        const editorRoot = rootOf(tree, 'WorkEditorView');
        const addTagText = textWithin(tree, editorRoot, '添加标签（常用优先）');
        await activateControl(connection, controlForText(tree, addTagText));
        const tagOption = await connection.waitForTree(current => {
            const label = findByText(current, tagName, entry => hasAncestorType(current, entry, 'MenuItem'));
            return label && ancestor(current, label, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '事项标签菜单缺少测试标签');
        await connection.clickNode(tagOption.value);
        const available = await connection.waitForTree(current => named(current, 'ExtraFieldsButton'), 8000,
            '添加标签后附加信息入口未出现');
        await activateControl(connection, available.value);
        const dialog = await connection.waitForTree(current => named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '附加信息编辑器未出现');
        const expectedNames = ['WorkExtraTextInput', 'WorkExtraMultilineInput', 'WorkExtraIntegerInput',
            'WorkExtraDecimalInput', 'WorkExtraBooleanInput', 'WorkExtraDateInput', 'WorkExtraTimeInput',
            'WorkExtraDateTimeEditor', 'WorkExtraChoiceInput'];
        for (const name of expectedNames)
            assertUi(named(dialog.tree, name), '缺少类型化编辑器：' + name);
        let layoutTree = dialog.tree;
        const dateTimeEditor = named(layoutTree, 'WorkExtraDateTimeEditor');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: dateTimeEditor.nodeId });
        await delay(80);
        layoutTree = await connection.getTree();
        const dateTimeDateInput = named(layoutTree, 'WorkExtraDateTimeDateInput');
        const dateTimeTimeInput = named(layoutTree, 'WorkExtraDateTimeTimeInput');
        const dateTimeClearButton = named(layoutTree, 'ClearWorkExtraDateTimeButton');
        await assertNoOverlap(connection, dateTimeDateInput, dateTimeTimeInput, '日期与时间编辑器发生重叠');
        await assertNoOverlap(connection, dateTimeTimeInput, dateTimeClearButton, '时间编辑器与清空按钮发生重叠');
        return { editorCount: expectedNames.length, dateTimeLayoutNoOverlap: true };
    });

    await runStep('extra-fields.values-edit', '编辑、清空并保存 9 类字段值', async () => {
        let tree = await connection.getTree();
        await connection.replaceText(named(tree, 'WorkExtraTextInput'), '单行值');
        tree = await connection.getTree();
        await connection.replaceText(named(tree, 'WorkExtraMultilineInput'), '第一行\n第二行');
        tree = await connection.getTree();
        await connection.replaceText(editableText(tree, 'WorkExtraIntegerInput'), '42');
        tree = await connection.getTree();
        await connection.replaceText(editableText(tree, 'WorkExtraDecimalInput'), '12.5');
        tree = await connection.getTree();
        await connection.clickNode(named(tree, 'WorkExtraBooleanInput'));
        await connection.waitForTree(current => textOf(named(current, 'WorkExtraBooleanInput')) === '否', 3000,
            '布尔值未从未设置切换为否');
        tree = await connection.getTree();
        await connection.clickNode(named(tree, 'WorkExtraBooleanInput'));
        await connection.waitForTree(current => isChecked(named(current, 'WorkExtraBooleanInput')), 3000,
            '布尔值未从否切换为是');
        tree = await connection.getTree();
        await connection.replaceText(editableText(tree, 'WorkExtraDateInput'), today);
        await connection.pressKey('Tab', 'Tab', 9);
        await setTimePicker(connection, 'WorkExtraTimeInput', 9, 30);
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'ClearWorkExtraTimeButton'));
        await setTimePicker(connection, 'WorkExtraTimeInput', 9, 30);
        tree = await connection.getTree();
        await connection.replaceText(editableText(tree, 'WorkExtraDateTimeDateInput'), today);
        await connection.pressKey('Tab', 'Tab', 9);
        await setTimePicker(connection, 'WorkExtraDateTimeTimeInput', 14, 5);
        tree = await connection.getTree();
        await selectComboOption(connection, named(tree, 'WorkExtraChoiceInput'), '测试');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'ClearWorkExtraChoiceButton'));
        await connection.waitForTree(current => !textWithinControl(current, 'WorkExtraChoiceInput').includes('测试'),
            3000, '单选值未清空');
        tree = await connection.getTree();
        await selectComboOption(connection, named(tree, 'WorkExtraChoiceInput'), '测试');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveWorkExtraFieldsButton'));
        await connection.waitForTree(current => !named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '附加字段对话框未保存关闭');
        await connection.pressKey('s', 'KeyS', 83, ctrl);
        await connection.waitForTree(current => findByText(current, workTitle)
            && findByTextContains(current, '本地已保存'), 10000, '事项未保存');
        return { choiceClearedAndReset: true, timeClearedAndReset: true };
    });

    await runStep('extra-fields.values-persist', '切换事项并验证全部字段持久化', async () => {
        await selectWork(connection, readonlyTitle);
        await selectWork(connection, workTitle);
        let tree = await connection.getTree();
        await activateControl(connection, named(tree, 'ExtraFieldsButton'));
        const dialog = await connection.waitForTree(current => named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '重开附加信息失败');
        tree = dialog.tree;
        assertEditorValue(tree, 'WorkExtraTextInput', '单行值', '单行文本未持久化');
        assertEditorValue(tree, 'WorkExtraMultilineInput', '第一行\n第二行', '多行文本未持久化');
        assertUi(textWithinControl(tree, 'WorkExtraIntegerInput').includes('42'), '整数未持久化');
        assertUi(textWithinControl(tree, 'WorkExtraDecimalInput').includes('12.5'), '小数未持久化');
        assertUi(isChecked(named(tree, 'WorkExtraBooleanInput')), '布尔值未持久化');
        assertUi(textWithinControl(tree, 'WorkExtraDateInput').includes(today.slice(0, 4)), '日期未持久化');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: named(tree, 'WorkExtraTimeEditor').nodeId });
        await delay(100);
        tree = await connection.getTree();
        assertUi(textWithinControl(tree, 'WorkExtraTimeInput').includes('9')
            && textWithinControl(tree, 'WorkExtraTimeInput').includes('30'), '时间未持久化');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: named(tree, 'WorkExtraDateTimeEditor').nodeId });
        await delay(100);
        tree = await connection.getTree();
        assertUi(textWithinControl(tree, 'WorkExtraDateTimeDateInput').includes(today.slice(0, 4)), '日期时间的日期未持久化');
        assertUi(textWithinControl(tree, 'WorkExtraDateTimeTimeInput').includes('14')
            && textWithinControl(tree, 'WorkExtraDateTimeTimeInput').includes('05'), '日期时间的时间未持久化');
        await connection.client.send('DOM.scrollIntoViewIfNeeded', { nodeId: named(tree, 'WorkExtraChoiceEditor').nodeId });
        await delay(100);
        tree = await connection.getTree();
        assertUi(textWithinControl(tree, 'WorkExtraChoiceInput').includes('测试'), '单选值未持久化');
        return { allTypesPersisted: true };
    });

    await runStep('extra-fields.definition-disable', '停用已有历史值的字段定义', async () => {
        let tree = await connection.getTree();
        await activateControl(connection, named(tree, 'CancelWorkExtraFieldsButton'));
        await connection.waitForTree(current => !named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '附加字段对话框未关闭');
        await openSettingsText(connection, '标签设置', 'TagEditorView');
        await selectTagInEditor(connection);
        await openExtraFieldsTab(connection);
        tree = await connection.getTree();
        const row = itemRow(tree, fields[0].label, 'TagExtraFieldEnabledToggle');
        const toggle = descendantByName(tree, row, 'TagExtraFieldEnabledToggle');
        assertUi(toggle && isChecked(toggle), '待停用字段当前不是启用状态');
        await activateControl(connection, toggle);
        await connection.waitForTree(current => {
            const currentRow = itemRow(current, fields[0].label, 'TagExtraFieldEnabledToggle');
            const currentToggle = descendantByName(current, currentRow, 'TagExtraFieldEnabledToggle');
            return currentToggle && !isChecked(currentToggle);
        }, 3000, '字段未切换为停用');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'SaveTagSettingsButton'));
        await connection.waitForTree(current => !rootOf(current, 'TagEditorView'), 10000,
            '停用状态未保存');
        return { disabledField: fields[0].key };
    });

    await runStep('extra-fields.disabled-history', '停用字段保留历史值并按字段只读', async () => {
        await selectWork(connection, readonlyTitle);
        await selectWork(connection, workTitle);
        let tree = await connection.getTree();
        await activateControl(connection, named(tree, 'ExtraFieldsButton'));
        const dialog = await connection.waitForTree(current => named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '停用字段后附加信息入口丢失');
        tree = dialog.tree;
        assertUi(findByText(tree, fields[0].label), '停用字段历史值未显示');
        assertUi(findByText(tree, '已停用，仅供查看'), '停用字段缺少只读提示');
        const input = named(tree, 'WorkExtraTextInput');
        assertUi(textOf(input) === '单行值', '停用字段历史值丢失');
        await connection.replaceText(input, '不应写入');
        tree = await connection.getTree();
        assertUi(textOf(named(tree, 'WorkExtraTextInput')) === '单行值', '停用字段仍可编辑');
        assertUi(named(tree, 'SaveWorkExtraFieldsButton'), '其他启用字段应仍可保存');
        tree = await connection.getTree();
        await activateControl(connection, named(tree, 'CancelWorkExtraFieldsButton'));
        await connection.waitForTree(current => !named(current, 'WorkItemExtraFieldsRoot'), 8000,
            '附加字段对话框未关闭');
        return { historyPreserved: true, fieldReadOnly: true };
    });

    await runStep('extra-fields.readonly-import', '迁移只读事项不显示附加信息入口', async () => {
        await selectWork(connection, readonlyTitle);
        const tree = await connection.getTree();
        const entry = named(tree, 'ExtraFieldsButton');
        assertUi(!entry, '迁移只读事项错误显示附加信息入口');
        const trackerEditor = named(tree, 'TrackerEditorContentHost');
        if (trackerEditor)
            assertUi(!isEffectivelyEnabled(tree, trackerEditor), '只读事项的 Tracker 区域仍可编辑');
        return {
            extraFieldsEntryHidden: true,
            trackerReadOnly: trackerEditor ? true : 'no-tracker-loaded',
        };
    });
});
