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
    isChecked,
    isEnabled,
    isVisible,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const ctrl = 2;
const alt = 1;
const shift = 8;
const stamp = Date.now().toString(36);
const workTitle = 'UI全量核心-' + stamp;
const savedQueryName = 'UI查询-' + stamp;
const renamedQueryName = savedQueryName + '-重命名';

function viewRoot(tree, typeName) {
    return tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes(typeName));
}

function textInView(tree, typeName, text, contains = false) {
    const root = viewRoot(tree, typeName);
    if (!root)
        return null;
    return [root, ...descendants(tree, root)].find(entry => isVisible(entry)
        && (contains ? textOf(entry).includes(text) : textOf(entry) === text));
}

async function clickTextInView(connection, typeName, text, contains = false) {
    const tree = await connection.getTree();
    const entry = textInView(tree, typeName, text, contains);
    assertUi(entry, '在 ' + typeName + ' 中找不到文字：' + text);
    const target = controlForText(tree, entry);
    assertUi(target, '找不到文字对应控件：' + text);
    await connection.client.send('DOM.focus', { nodeId: target.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
}

async function closeViewWithButton(connection, typeName, button) {
    if (!viewRoot(await connection.getTree(), typeName))
        return false;
    const tree = await connection.getTree();
    const entry = textInView(tree, typeName, button);
    assertUi(entry, typeName + ' 缺少按钮：' + button);
    const target = controlForText(tree, entry);
    assertUi(target, typeName + ' 的按钮不可激活：' + button);
    await connection.client.send('DOM.focus', { nodeId: target.nodeId });
    await connection.pressKey('Enter', 'Enter', 13);
    await connection.waitForTree(current => !viewRoot(current, typeName), 8000, typeName + ' 没有关闭');
    return true;
}

async function closeViewWithDialogButton(connection, typeName) {
    await delay(80);
    const tree = await connection.getTree();
    const view = viewRoot(tree, typeName);
    if (!view)
        return false;
    const dialog = ancestor(tree, view, entry => typeOf(entry).includes('DialogControl'));
    const closeButton = dialog && descendants(tree, dialog).find(entry =>
        isVisible(entry) && typeOf(entry).includes('Button') && entry.a.Name === 'PART_CloseButton');
    assertUi(closeButton, typeName + ' 缺少对话框关闭按钮');
    await connection.clickNode(closeButton);
    await connection.waitForTree(current => !viewRoot(current, typeName), 8000, typeName + ' 没有关闭');
    return true;
}

async function closeKnownDialog(connection) {
    for (const [typeName, button] of [
        ['OnboardingView', '稍后再看'],
        ['SettingsView', '关闭'],
        ['ExportTemplateManagerView', '关闭'],
        ['TrackerSettingsDialogView', '取消'],
        ['CopyDayView', '取消'],
    ]) {
        if (await closeViewWithButton(connection, typeName, button))
            return;
    }
    if (await closeViewWithDialogButton(connection, 'AboutView'))
        return;
    await connection.pressKey('Escape', 'Escape', 27);
    await delay(100);
}

async function openSettingsText(connection, text, expectedType) {
    await connection.clickByName('SettingsMenuButton');
    const menu = await connection.waitForTree(tree => {
        const entry = findByText(tree, text, item => hasAncestorType(tree, item, 'MenuItem'));
        return entry && ancestor(tree, entry, item => typeOf(item).includes('MenuItem'));
    }, 5000, '设置菜单项未出现：' + text);
    await connection.clickNode(menu.value);
    return connection.waitForTree(tree => viewRoot(tree, expectedType), 10000,
        '对话框未出现：' + expectedType);
}

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

function isCurrentDateText(value, date) {
    const parts = value.match(/\d+/g)?.map(Number);
    if (parts?.length !== 3)
        return false;
    const year = date.getFullYear();
    const month = date.getMonth() + 1;
    const day = date.getDate();
    return [
        [year, month, day],
        [month, day, year],
        [day, month, year],
    ].some(expected => expected.every((part, index) => parts[index] === part));
}

function boundsOf(entry) {
    const parts = String(entry?.a?.Bounds ?? '').split(',').map(Number);
    assertUi(parts.length === 4 && parts.every(Number.isFinite), '控件缺少有效 Bounds：' + entry?.a?.Name);
    return { x: parts[0], y: parts[1], width: parts[2], height: parts[3] };
}

await runUiSuite({ name: 'ui-core-full', scenario: 'default', timeoutMs: 10000, stopOnFailure: true }, async ({
    connection, runStep, addFinding,
}) => {
    await closeKnownDialog(connection);

    await runStep('shell.navigation-status', '主窗口、导航和状态栏', async () => {
        const tree = await connection.getTree();
        for (const text of ['日记记录', '事项查询', '统计工具'])
            assertUi(findByText(tree, text), '缺少主导航：' + text);
        assertUi(findByName(tree, 'StatusBarView'), '状态栏不可见');
        assertUi(findByName(tree, 'Version'), '版本入口不可见');
        const now = new Date();
        const statusBar = findByName(tree, 'StatusBarView');
        const statusDate = [statusBar, ...descendants(tree, statusBar)]
            .find(entry => isVisible(entry) && isCurrentDateText(textOf(entry), now));
        assertUi(statusDate, '状态栏缺少当前日期');
        await connection.clickByName('Version');
        const versionMenu = await connection.waitForTree(current => {
            const item = findByText(current, '检查更新', entry => hasAncestorType(current, entry, 'MenuItem'));
            return item && ancestor(current, item, entry => typeOf(entry).includes('MenuItem'));
        }, 5000, '版本菜单缺少检查更新入口');
        assertUi(isEnabled(versionMenu.value), '检查更新入口不可用');
        await connection.pressKey('Escape', 'Escape', 27);
        return {
            version: textOf(findByName(tree, 'Version')),
            dateText: textOf(statusDate),
            updateShortcut: textOf(versionMenu.value),
        };
    });

    await runStep('shell.application-menu', '应用菜单内容', async () => {
        await connection.clickByName('ApplicationMenuButton');
        const result = await connection.waitForTree(tree => {
            const expected = ['关于', '最大化', '最小化', '重启程序', '退出'];
            return expected.every(text => findByText(tree, text, entry => hasAncestorType(tree, entry, 'MenuItem')));
        }, 5000, '应用菜单内容不完整');
        assertUi(!findByText(result.tree, '用户手册', entry => hasAncestorType(result.tree, entry, 'MenuItem')),
            'Debug 构建不应显示发布版用户手册入口');
        await connection.pressKey('Escape', 'Escape', 27);
        return { openMs: result.elapsedMs };
    });

    await runStep('shell.navigation-collapse', '导航栏展开折叠', async () => {
        let tree = await connection.getTree();
        const toggle = findByName(tree, 'PanelOpen');
        assertUi(toggle, '找不到导航折叠按钮');
        const before = isChecked(toggle);
        await connection.clickNode(toggle);
        const changed = await connection.waitForTree(current => {
            const item = findByName(current, 'PanelOpen');
            return item && isChecked(item) !== before ? item : null;
        }, 3000, '导航栏折叠状态没有变化');
        await connection.clickNode(changed.value);
        await connection.waitForTree(current => isChecked(findByName(current, 'PanelOpen')) === before,
            3000, '导航栏没有恢复原状态');
        return { initialExpanded: before };
    });

    await runStep('shell.keyboard-navigation', 'Alt 数字主导航', async () => {
        await connection.pressKey('2', 'Digit2', 50, alt);
        const query = await connection.waitForTree(tree => viewRoot(tree, 'WorkItemQueryView'), 8000);
        await connection.pressKey('3', 'Digit3', 51, alt);
        const statistics = await connection.waitForTree(tree => viewRoot(tree, 'StatisticsView'), 8000);
        await connection.pressKey('1', 'Digit1', 49, alt);
        const diary = await connection.waitForTree(tree => viewRoot(tree, 'DiaryEditorView'), 8000);
        return { queryMs: query.elapsedMs, statisticsMs: statistics.elapsedMs, diaryMs: diary.elapsedMs };
    });

    await runStep('settings.program', '程序设置分组和日志入口', async () => {
        const opened = await connection.openSettingsMenuItem('ProgramSettingsMenuItem');
        const dialog = await connection.waitForTree(tree => viewRoot(tree, 'SettingsView'), 8000);
        const tree = dialog.tree;
        for (const group of ['视图设置', '工作设置', '数据库设置', '调查统计功能设置', '应用更新'])
            assertUi(textInView(tree, 'SettingsView', group), '程序设置缺少分组：' + group);
        for (const action of ['打开当前日志', '导出日志', '保存', '关闭'])
            assertUi(textInView(tree, 'SettingsView', action), '程序设置缺少入口：' + action);
        await closeViewWithButton(connection, 'SettingsView', '关闭');
        return { menuMs: opened, dialogMs: dialog.elapsedMs };
    });

    await runStep('settings.data-template', '数据模板空状态', async () => {
        const opened = await openSettingsText(connection, '数据模板', 'ExportTemplateManagerView');
        const tree = opened.tree;
        assertUi(textInView(tree, 'ExportTemplateManagerView', '尚未导入数据模板。'), '数据模板空状态缺失');
        assertUi(textInView(tree, 'ExportTemplateManagerView', '导入模板'), '导入模板入口缺失');
        await closeViewWithButton(connection, 'ExportTemplateManagerView', '关闭');
        return { openMs: opened.elapsedMs };
    });

    await runStep('settings.tracker-empty', 'Tracker 设置核心模式', async () => {
        const opened = await openSettingsText(connection, 'Tracker 设置', 'TrackerSettingsDialogView');
        const tree = opened.tree;
        assertUi(textInView(tree, 'TrackerSettingsDialogView', 'Tracker 配置'), 'Tracker 配置页签缺失');
        assertUi(textInView(tree, 'TrackerSettingsDialogView', '插件状态'), '插件状态页签缺失');
        assertUi(textInView(tree, 'TrackerSettingsDialogView', '保存'), 'Tracker 保存入口缺失');
        await closeViewWithButton(connection, 'TrackerSettingsDialogView', '取消');
        return { openMs: opened.elapsedMs };
    });

    await runStep('shell.about', '关于对话框', async () => {
        await connection.clickByName('ApplicationMenuButton');
        const menu = await connection.waitForTree(tree => {
            const entry = findByText(tree, '关于', item => hasAncestorType(tree, item, 'MenuItem'));
            return entry && ancestor(tree, entry, item => typeOf(item).includes('MenuItem'));
        }, 5000);
        await connection.clickNode(menu.value);
        const opened = await connection.waitForTree(tree => viewRoot(tree, 'AboutView'), 8000, '关于对话框未出现');
        assertUi(textInView(opened.tree, 'AboutView', 'Diary Tools NG'), '关于对话框应用名缺失');
        await closeViewWithDialogButton(connection, 'AboutView');
        return { openMs: opened.elapsedMs };
    });

    await runStep('diary.copy-dialog', '紧凑周历、分层右键菜单、跨月回到今天和复制整天取消', async () => {
        await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.clickByText('回到今天');
        let tree = await connection.getTree();
        const dateHeading = findByName(tree, 'DiaryDateHeading');
        const dateDescription = findByName(tree, 'DiaryDateDescription');
        const dateActions = findByName(tree, 'DiaryDateActions');
        const statusPill = findByName(tree, 'SelectedWorkStatusPill');
        const compactCalendar = findByName(tree, 'CompactCalendar');
        const compactDays = findByName(tree, 'CompactCalendarDays');
        const todayHeader = textOf(findByName(tree, 'CompactCalendarHeader'));
        assertUi(dateHeading && dateDescription && dateActions && compactCalendar && compactDays && todayHeader,
            '日记页日期头部或紧凑周历结构不完整');
        const headingBounds = boundsOf(dateHeading);
        const descriptionBounds = boundsOf(dateDescription);
        const actionBounds = boundsOf(dateActions);
        assertUi(headingBounds.y + headingBounds.height <= actionBounds.y,
            '日期标题与操作按钮发生垂直重叠');
        assertUi(descriptionBounds.height <= 24, '日期说明被挤压换行');
        assertUi(!statusPill || !isVisible(statusPill), '未选中事项时仍显示空状态胶囊');
        const compactDayButtons = descendants(tree, compactDays).filter(entry =>
            isVisible(entry) && typeOf(entry).includes('Button')
            && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
        assertUi(compactDayButtons.length === 7, '默认一周视图没有显示 7 个日期');
        const todayButton = compactDayButtons.find(entry => textOf(entry) === String(new Date().getDate())
            && String(entry.a.Class ?? '').includes('Selected'));
        assertUi(todayButton, '紧凑周历没有标记当前选中日期');

        const selectedDateTitle = textOf(findByName(tree, 'DiaryDateTitle'));
        const initialFirstDayText = textOf(compactDayButtons[0]);
        const calendarBox = await connection.client.send('DOM.getBoxModel', { nodeId: compactCalendar.nodeId });
        const calendarQuad = calendarBox.model.border;
        const calendarX = (calendarQuad[0] + calendarQuad[4]) / 2;
        const calendarY = (calendarQuad[1] + calendarQuad[5]) / 2;
        await connection.client.send('Input.dispatchMouseEvent', {
            type: 'mouseMoved', x: calendarX, y: calendarY,
        });
        await connection.client.send('Input.dispatchMouseEvent', {
            type: 'mouseWheel', x: calendarX, y: calendarY, deltaX: 0, deltaY: 120,
        });
        await connection.waitForTree(current => {
            const root = findByName(current, 'CompactCalendarDays');
            const buttons = root && descendants(current, root).filter(entry =>
                isVisible(entry) && typeOf(entry).includes('Button')
                && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
            return buttons?.length === 7 && textOf(buttons[0]) !== initialFirstDayText ? root : null;
        }, 3000, '滚轮向下没有浏览到后一周');
        tree = await connection.getTree();
        assertUi(textOf(findByName(tree, 'DiaryDateTitle')) === selectedDateTitle,
            '滚轮浏览周历时改变了当前选中日期');
        await connection.client.send('Input.dispatchMouseEvent', {
            type: 'mouseWheel', x: calendarX, y: calendarY, deltaX: 0, deltaY: -120,
        });
        await connection.waitForTree(current => {
            const root = findByName(current, 'CompactCalendarDays');
            const buttons = root && descendants(current, root).filter(entry =>
                isVisible(entry) && typeOf(entry).includes('Button')
                && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
            return buttons?.length === 7 && textOf(buttons[0]) === initialFirstDayText ? root : null;
        }, 3000, '反向滚轮没有恢复原周');

        tree = await connection.getTree();
        const currentCompactDays = findByName(tree, 'CompactCalendarDays');
        const currentDayButton = descendants(tree, currentCompactDays).find(entry =>
            isVisible(entry) && typeOf(entry).includes('Button')
            && String(entry.a.Class ?? '').includes('CompactCalendarDay')
            && String(entry.a.Class ?? '').includes('Selected'));
        assertUi(currentDayButton, '找不到可验证右键菜单的周历日期');
        await connection.client.send('DOM.focus', { nodeId: currentDayButton.nodeId });
        await connection.pressKey('F10', 'F10', 121, shift);
        await connection.waitForTree(current => ['同步本日工时', '统计本周工时'].every(text =>
            findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'))),
        3000, '紧凑周历日期右键菜单没有同时提供日和周操作');
        await connection.pressKey('Escape', 'Escape', 27);

        tree = await connection.getTree();
        const compactCalendarHeader = findByName(tree, 'CompactCalendarHeader');
        assertUi(compactCalendarHeader, '找不到可验证右键菜单的月份标题');
        await connection.client.send('DOM.focus', { nodeId: compactCalendarHeader.nodeId });
        await connection.pressKey('F10', 'F10', 121, shift);
        await connection.waitForTree(current => ['统计本月工时', '统计本季度工时', '统计此年工时'].every(text =>
            findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'))),
        3000, '月份标题右键菜单没有同时提供月、季度和年度操作');
        await connection.pressKey('Escape', 'Escape', 27);

        await connection.clickByName('CompactCalendarHeader');
        const fullCalendar = await connection.waitForTree(current => {
            const calendar = findByName(current, 'DiaryCalendar');
            return calendar && isVisible(calendar) ? calendar : null;
        }, 3000, '点击周历标题没有打开完整月历');
        const fullCalendarBounds = boundsOf(fullCalendar.value);
        const calendarItem = findByName(fullCalendar.tree, 'PART_CalendarItem');
        assertUi(calendarItem, '完整月历模板内容缺失');
        const calendarItemBounds = boundsOf(calendarItem);
        assertUi(fullCalendarBounds.width >= calendarItemBounds.width
            && fullCalendarBounds.height >= calendarItemBounds.height,
        '完整月历尺寸小于内部模板，边框会被裁切');
        await connection.pressKey('Escape', 'Escape', 27);
        await connection.pressKey('Escape', 'Escape', 27);

        for (let index = 0; index < 5; index += 1)
            await connection.clickByName('PreviousCalendarPeriodButton');
        const previousPeriod = await connection.waitForTree(current => {
            const header = findByName(current, 'CompactCalendarHeader');
            return header && textOf(header) !== todayHeader ? header : null;
        }, 3000, '向前浏览后周历标题没有跨月');
        await connection.clickByText('回到今天');
        const returnedToday = await connection.waitForTree(current => {
            const header = findByName(current, 'CompactCalendarHeader');
            return header && textOf(header) === todayHeader ? header : null;
        }, 3000, '跨月后回到今天没有恢复当前周期');

        await connection.clickByText('复制记录');
        const menu = await connection.waitForTree(current => ['复制昨天', '复制最近', '复制整天'].every(text =>
            findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'))), 5000, '复制菜单不完整');
        const wholeDayText = findByText(menu.tree, '复制整天', entry => hasAncestorType(menu.tree, entry, 'MenuItem'));
        await connection.clickNode(ancestor(menu.tree, wholeDayText, entry => typeOf(entry).includes('MenuItem')));
        const dialog = await connection.waitForTree(current => viewRoot(current, 'CopyDayView'), 8000, '复制整天对话框未出现');
        assertUi(textInView(dialog.tree, 'CopyDayView', '源日期'), '复制整天缺少源日期');
        assertUi(textInView(dialog.tree, 'CopyDayView', '目标日期：'), '复制整天缺少目标日期');
        await closeViewWithButton(connection, 'CopyDayView', '取消');
        return {
            oneWeekHeight: boundsOf(compactDays).height,
            compactDayContextMenu: true,
            compactHeaderContextMenu: true,
            fullCalendarHeight: fullCalendarBounds.height,
            calendarItemWidth: calendarItemBounds.width,
            calendarItemHeight: calendarItemBounds.height,
            wheelWeekBrowsing: true,
            previousPeriodHeader: textOf(previousPeriod.value),
            todayHeader: textOf(returnedToday.value),
            dateDescriptionHeight: descriptionBounds.height,
            emptyStatusPillHidden: !statusPill || !isVisible(statusPill),
            dialogMs: dialog.elapsedMs,
        };
    });

    await runStep('diary.shortcuts', '新建、保存、重复和删除取消快捷键', async () => {
        await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.pressKey('n', 'KeyN', 78, ctrl);
        const editor = await connection.waitForTree(tree => findByName(tree, 'WorkTitleInput'), 8000, 'Ctrl+N 未打开编辑器');
        const editorTree = editor.tree;
        const dateInput = findByName(editorTree, 'WorkDatePicker');
        const titleInput = findByName(editorTree, 'WorkTitleInput');
        const timeInput = findByName(editorTree, 'WorkTimeInput');
        assertUi(dateInput && titleInput && timeInput, '一般信息字段结构不完整');
        const [dateBox, titleBox, timeBox] = await Promise.all([
            connection.client.send('DOM.getBoxModel', { nodeId: dateInput.nodeId }),
            connection.client.send('DOM.getBoxModel', { nodeId: titleInput.nodeId }),
            connection.client.send('DOM.getBoxModel', { nodeId: timeInput.nodeId }),
        ]);
        const inputLeftEdges = [dateBox, titleBox, timeBox].map(box => box.model.border[0]);
        assertUi(Math.max(...inputLeftEdges) - Math.min(...inputLeftEdges) <= 1,
            '日期、标题和耗时输入框左边缘未对齐');
        await connection.replaceText(editor.value, workTitle);
        await connection.clickByName('UseTodayButton');
        await connection.pressKey('s', 'KeyS', 83, ctrl);
        await connection.waitForTree(tree => findByText(tree, workTitle) && findByTextContains(tree, '本地已保存'),
            10000, 'Ctrl+S 后未观察到本地保存');
        await connection.pressKey('d', 'KeyD', 68, ctrl);
        const duplicate = await connection.waitForTree(tree => {
            const input = findByName(tree, 'WorkTitleInput');
            return input && textOf(input) === workTitle ? input : null;
        }, 8000, 'Ctrl+D 未生成重复事项');
        await connection.pressKey('d', 'KeyD', 68, ctrl | shift);
        const confirm = await connection.waitForTree(tree => findByTextContains(tree, '确认删除这条工作记录'),
            8000, 'Ctrl+Shift+D 未显示删除确认');
        const noButton = findByName(confirm.tree, 'PART_NoButton');
        assertUi(noButton, '删除确认缺少否按钮');
        await connection.client.send('DOM.focus', { nodeId: noButton.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        await connection.waitForTree(tree => !findByName(tree, 'PART_NoButton'), 5000,
            '删除确认没有取消');
        return { editorMs: editor.elapsedMs, duplicateMs: duplicate.elapsedMs, confirmMs: confirm.elapsedMs };
    });

    await runStep('query.execute-saved', '查询、条件折叠和保存查询维护', async () => {
        await connection.navigate('事项查询', 'WorkItemQueryView');
        await clickTextInView(connection, 'WorkItemQueryView', '查询');
        const queried = await connection.waitForTree(tree => findByText(tree, workTitle), 10000, '查询结果未包含新建事项');
        let tree = queried.tree;
        const filter = findByName(tree, 'FilterToggle');
        assertUi(filter && isChecked(filter), '查询条件默认未展开');
        await connection.clickNode(filter);
        await connection.waitForTree(current => !isChecked(findByName(current, 'FilterToggle')), 3000,
            '查询条件未折叠');
        tree = await connection.getTree();
        await connection.clickNode(findByName(tree, 'FilterToggle'));
        await connection.waitForTree(current => isChecked(findByName(current, 'FilterToggle')), 3000,
            '查询条件未展开');
        tree = await connection.getTree();
        const nameInput = tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes('TextBox')
            && !entry.a.Name && Number(entry.a.Width) >= 300);
        assertUi(nameInput, '找不到保存查询名称输入框');
        await connection.replaceText(nameInput, savedQueryName);
        await connection.clickByText('新增');
        await connection.waitForTree(current => findByText(current, '查询已保存'), 5000, '保存查询失败');
        await connection.clickByText('今天');
        await connection.clickByText('更新');
        await connection.waitForTree(current => findByText(current, '查询条件已更新'), 5000, '更新查询失败');
        tree = await connection.getTree();
        const renamedInput = tree.entries.find(entry => isVisible(entry) && typeOf(entry).includes('TextBox')
            && !entry.a.Name && Number(entry.a.Width) >= 300);
        await connection.replaceText(renamedInput, renamedQueryName);
        await connection.clickByText('重命名');
        await connection.waitForTree(current => findByText(current, '查询已重命名') && findByText(current, renamedQueryName),
            5000, '重命名查询失败');
        await connection.clickByText('应用');
        await connection.waitForTree(current => findByTextContains(current, '已应用查询条件'), 5000,
            '应用保存查询失败');
        await connection.clickByText('删除');
        await connection.waitForTree(current => findByTextContains(current, '确认删除'), 8000, '删除查询确认未出现');
        const deleteDialog = await connection.getTree();
        const cancelDelete = findByName(deleteDialog, 'PART_NoButton');
        assertUi(cancelDelete, '删除查询确认缺少否按钮');
        await connection.client.send('DOM.focus', { nodeId: cancelDelete.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        await connection.waitForTree(current => findByText(current, '已取消删除'), 5000, '删除查询取消状态缺失');
        return { queryMs: queried.elapsedMs, savedQuery: renamedQueryName };
    });

    await runStep('query.open-result', '从查询结果打开日记事项', async () => {
        await clickTextInView(connection, 'WorkItemQueryView', '查询');
        const result = await connection.waitForTree(tree => findByText(tree, workTitle), 10000);
        const targetTitle = findByText(result.tree, workTitle);
        const resultRow = ancestor(result.tree, targetTitle, entry =>
            typeOf(entry).includes('ContentPresenter') && textOf(entry).includes('WorkItemQueryResult'));
        const openButton = resultRow && descendants(result.tree, resultRow).find(entry =>
            typeOf(entry).includes('Button') && textOf(entry) === '打开');
        assertUi(openButton, '目标查询结果缺少打开按钮');
        await connection.client.send('DOM.focus', { nodeId: openButton.nodeId });
        await connection.pressKey('Enter', 'Enter', 13);
        const opened = await connection.waitForTree(tree => viewRoot(tree, 'DiaryEditorView')
            && findByName(tree, 'WorkTitleInput') && textOf(findByName(tree, 'WorkTitleInput')) === workTitle,
        10000, '打开查询结果后没有定位到事项');
        return { openMs: opened.elapsedMs };
    });

    await runStep('statistics.tabs-refresh', '统计页签、刷新和树操作', async () => {
        const navigationMs = await connection.navigate('统计工具', 'StatisticsView');
        let tree = await connection.getTree();
        for (const text of ['统计范围', '重新统计', '工时分布', '标签明细', '自定义'])
            assertUi(findByText(tree, text), '统计页面缺少：' + text);
        const customText = findByText(tree, '自定义');
        const customTab = ancestor(tree, customText, entry => typeOf(entry).includes('TabItem'));
        assertUi(customTab, '自定义统计页签不可点击');
        await connection.clickNode(customTab);
        await connection.waitForTree(current => {
            const refresh = findByText(current, '重新统计');
            return refresh && isEnabled(controlForText(current, refresh));
        }, 5000, '自定义统计页未激活');
        const started = performance.now();
        await connection.clickByText('重新统计');
        await connection.waitForTree(current => findByTextContains(current, '统计总工时：'), 8000,
            '重新统计没有完成');
        tree = await connection.getTree();
        const chartToggle = findByName(tree, 'StatisticsChartTypeToggle');
        assertUi(chartToggle && !isChecked(chartToggle), '统计图表没有默认显示柱状图');
        await connection.clickNode(chartToggle);
        await connection.waitForTree(current => isChecked(findByName(current, 'StatisticsChartTypeToggle')),
            5000, '统计图表没有切换到饼图');
        await connection.clickByText('全部展开');
        await connection.clickByText('全部折叠');
        return { navigationMs, refreshMs: performance.now() - started };
    });

    await runStep('performance.warm', '视觉树和截图响应速度', async () => {
        const dom = [];
        for (let index = 0; index < 20; index++) {
            const started = performance.now();
            await connection.getTree();
            dom.push(performance.now() - started);
        }
        const screenshots = [];
        for (let index = 0; index < 5; index++) {
            const started = performance.now();
            await connection.client.send('Page.captureScreenshot', { format: 'png' }, 15000);
            screenshots.push(performance.now() - started);
        }
        const metrics = {
            domP50Ms: percentile(dom, 0.5),
            domP95Ms: percentile(dom, 0.95),
            screenshotP50Ms: percentile(screenshots, 0.5),
            screenshotP95Ms: percentile(screenshots, 0.95),
        };
        if (metrics.domP95Ms > 50)
            addFinding('warning', 'dom-p95-slow', '视觉树 P95 超过 50ms', metrics);
        if (metrics.screenshotP95Ms > 250)
            addFinding('warning', 'screenshot-p95-slow', '截图 P95 超过 250ms', metrics);
        return metrics;
    });
});
