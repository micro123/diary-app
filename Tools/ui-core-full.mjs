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

function compactCalendarDayText(tree, button) {
    const label = descendants(tree, button).find(entry =>
        String(entry.a.Class ?? '').includes('CompactCalendarDayText'));
    return textOf(label);
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

function formatCompactCalendarHeader(date) {
    const yearStart = Date.UTC(date.getFullYear(), 0, 1);
    const dateValue = Date.UTC(date.getFullYear(), date.getMonth(), date.getDate());
    const dayOfYear = Math.floor((dateValue - yearStart) / 86400000) + 1;
    const mondayOffset = (new Date(yearStart).getUTCDay() + 6) % 7;
    const week = Math.floor((dayOfYear - 1 + mondayOffset) / 7) + 1;
    return `${date.getFullYear()}年${date.getMonth() + 1}月 第${week}周`;
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
        assertUi(todayHeader === formatCompactCalendarHeader(new Date()), '周历标题没有显示当前年月和年度周次');
        const headingBounds = boundsOf(dateHeading);
        const descriptionBounds = boundsOf(dateDescription);
        const actionBounds = boundsOf(dateActions);
        const todayActionButton = controlForText(tree, findByText(tree, '回到今天'), ['Button']);
        const copyActionButton = controlForText(tree, findByText(tree, '复制记录'), ['Button']);
        const todayActionBounds = boundsOf(todayActionButton);
        const copyActionBounds = boundsOf(copyActionButton);
        assertUi(headingBounds.y + headingBounds.height <= actionBounds.y,
            '日期标题与操作按钮发生垂直重叠');
        assertUi(Math.abs(todayActionBounds.x - actionBounds.x) <= 1
            && Math.abs(copyActionBounds.x + copyActionBounds.width
                - actionBounds.x - actionBounds.width) <= 1,
            '回到今天和复制记录没有分别对齐操作区两侧');
        assertUi(descriptionBounds.height <= 24, '日期说明被挤压换行');
        assertUi(!statusPill || !isVisible(statusPill), '未选中事项时仍显示空状态胶囊');
        const compactDayButtons = descendants(tree, compactDays).filter(entry =>
            isVisible(entry) && typeOf(entry).includes('Button')
            && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
        assertUi(compactDayButtons.length === 7, '默认一周视图没有显示 7 个日期');
        const todayButton = compactDayButtons.find(entry => String(entry.a.Class ?? '').includes('Today')
            && String(entry.a.Class ?? '').includes('Selected'));
        assertUi(todayButton, '回到今天后没有同时标记今天和当前选中日期');

        const selectedDateTitle = textOf(findByName(tree, 'DiaryDateTitle'));
        const initialFirstDayText = compactCalendarDayText(tree, compactDayButtons[0]);
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
            return buttons?.length === 7
                && compactCalendarDayText(current, buttons[0]) !== initialFirstDayText ? root : null;
        }, 3000, '滚轮向下没有浏览到后一周');
        tree = await connection.getTree();
        assertUi(textOf(findByName(tree, 'DiaryDateTitle')) === selectedDateTitle,
            '滚轮浏览周历时改变了当前选中日期');
        const wheelWeekHeader = textOf(findByName(tree, 'CompactCalendarHeader'));
        assertUi(wheelWeekHeader !== todayHeader && /^\d{4}年\d{1,2}月 第\d{1,2}周$/.test(wheelWeekHeader),
            '滚轮浏览后一周时周历标题没有同步周次');
        await connection.client.send('Input.dispatchMouseEvent', {
            type: 'mouseWheel', x: calendarX, y: calendarY, deltaX: 0, deltaY: -120,
        });
        await connection.waitForTree(current => {
            const root = findByName(current, 'CompactCalendarDays');
            const header = findByName(current, 'CompactCalendarHeader');
            const buttons = root && descendants(current, root).filter(entry =>
                isVisible(entry) && typeOf(entry).includes('Button')
                && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
            return buttons?.length === 7
                && compactCalendarDayText(current, buttons[0]) === initialFirstDayText
                && textOf(header) === todayHeader ? root : null;
        }, 3000, '反向滚轮没有恢复原周');
        await delay(200);

        tree = await connection.getTree();
        const currentCompactDays = findByName(tree, 'CompactCalendarDays');
        const currentDayButtons = descendants(tree, currentCompactDays).filter(entry =>
            isVisible(entry) && typeOf(entry).includes('Button')
            && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
        const currentTodayButton = currentDayButtons.find(entry => String(entry.a.Class ?? '').includes('Today')
            && String(entry.a.Class ?? '').includes('Selected'));
        const contextTargetButton = currentDayButtons.find(entry => !String(entry.a.Class ?? '').includes('Selected'));
        assertUi(currentTodayButton && contextTargetButton, '找不到可验证今天与非选中日期的周历按钮');
        const contextTargetText = compactCalendarDayText(tree, contextTargetButton);
        const targetBox = await connection.client.send('DOM.getBoxModel', { nodeId: contextTargetButton.nodeId });
        const targetQuad = targetBox.model.border;
        const targetX = (targetQuad[0] + targetQuad[4]) / 2;
        const targetY = (targetQuad[1] + targetQuad[5]) / 2;
        const openDayMenu = async timeoutMs => {
            await connection.client.send('Input.dispatchMouseEvent', {
                type: 'mouseMoved', x: targetX, y: targetY,
            });
            await delay(200);
            await connection.client.send('Input.dispatchMouseEvent', {
                type: 'mousePressed', x: targetX, y: targetY, button: 'right', buttons: 2, clickCount: 1,
            });
            await connection.client.send('Input.dispatchMouseEvent', {
                type: 'mouseReleased', x: targetX, y: targetY, button: 'right', buttons: 0, clickCount: 1,
            });
            return connection.waitForTree(current => ['同步本日工时', '统计本周工时'].every(text =>
                findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'))),
            timeoutMs, '右键非选中日期后没有同时选中日期并打开日/周菜单');
        };
        let dayMenu;
        try {
            dayMenu = await openDayMenu(1500);
        }
        catch {
            await connection.pressKey('Escape', 'Escape', 27);
            await delay(300);
            dayMenu = await openDayMenu(5000);
        }
        const updatedCompactDays = findByName(dayMenu.tree, 'CompactCalendarDays');
        const updatedDayButtons = descendants(dayMenu.tree, updatedCompactDays).filter(entry =>
            isVisible(entry) && typeOf(entry).includes('Button')
            && String(entry.a.Class ?? '').includes('CompactCalendarDay'));
        const selectedTargetButton = updatedDayButtons.find(entry =>
            compactCalendarDayText(dayMenu.tree, entry) === contextTargetText
            && String(entry.a.Class ?? '').includes('Selected'));
        const updatedTodayButton = updatedDayButtons.find(entry => String(entry.a.Class ?? '').includes('Today'));
        assertUi(selectedTargetButton, '右键非选中日期后目标日期没有变为当前选中');
        assertUi(updatedTodayButton && !String(updatedTodayButton.a.Class ?? '').includes('Selected'),
            '选择其他日期后今天标记与当前选中状态没有分离');
        await connection.pressKey('Escape', 'Escape', 27);

        tree = await connection.getTree();
        const compactCalendarHeader = findByName(tree, 'CompactCalendarHeader');
        assertUi(compactCalendarHeader, '找不到可验证右键菜单的月份标题');
        await connection.client.send('DOM.focus', { nodeId: compactCalendarHeader.nodeId });
        await connection.pressKey('F10', 'F10', 121, shift);
        const periodMenu = await connection.waitForTree(current => [
            '统计本月工时', '统计本季度工时', '统计此年工时',
            '脚本（本月）', '脚本（本季度）', '脚本（本年度）',
        ].every(text => findByText(current, text, entry => hasAncestorType(current, entry, 'MenuItem'))),
        3000, '月份标题右键菜单没有同时提供月、季度和年度操作');
        const statisticsYear = findByText(periodMenu.tree, '统计此年工时',
            entry => hasAncestorType(periodMenu.tree, entry, 'MenuItem'));
        const monthScripts = findByText(periodMenu.tree, '脚本（本月）',
            entry => hasAncestorType(periodMenu.tree, entry, 'MenuItem'));
        const statisticsBounds = boundsOf(statisticsYear);
        const monthScriptsBounds = boundsOf(monthScripts);
        const scriptGroupGap = monthScriptsBounds.y - statisticsBounds.y - statisticsBounds.height;
        assertUi(scriptGroupGap >= 4, '脚本菜单组前缺少分隔符形成的分组间距');
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

        const fullCalendarHeader = findByName(fullCalendar.tree, 'PART_HeaderButton');
        const fullCalendarHeaderText = textOf(fullCalendarHeader);
        assertUi(fullCalendarHeader && /^\d{4}年\d{1,2}月$/.test(fullCalendarHeaderText),
            '完整月历缺少可识别的月份标题');
        await connection.clickNode(fullCalendarHeader);
        await connection.waitForTree(current => {
            const calendar = findByName(current, 'DiaryCalendar');
            const monthButtons = calendar && descendants(current, calendar).filter(entry =>
                isVisible(entry) && typeOf(entry).includes('CalendarButton')
                && boundsOf(entry).width > 0);
            return monthButtons?.length === 12 ? calendar : null;
        }, 3000, '完整月历没有进入年份选择视图');
        await connection.pressKey('Escape', 'Escape', 27);
        await connection.waitForTree(current => !findByName(current, 'DiaryCalendar'),
            3000, '完整月历 Flyout 没有关闭');

        await connection.clickByName('CompactCalendarHeader');
        const reopenedCalendar = await connection.waitForTree(current => {
            const calendar = findByName(current, 'DiaryCalendar');
            if (!calendar || !isVisible(calendar))
                return null;
            const entries = descendants(current, calendar);
            const weekdayCount = entries.filter(entry => typeOf(entry).includes('TextBlock')
                && textOf(entry).startsWith('周') && boundsOf(entry).width > 0).length;
            const visibleDayCount = entries.filter(entry => typeOf(entry).includes('CalendarDayButton')
                && boundsOf(entry).width > 0).length;
            return textOf(findByName(current, 'PART_HeaderButton')) === fullCalendarHeaderText
                && weekdayCount === 7 && visibleDayCount >= 35 ? calendar : null;
        }, 3000, '完整月历重新展开后没有恢复月视图');

        const visibleCalendarDays = descendants(reopenedCalendar.tree, reopenedCalendar.value)
            .filter(entry => isVisible(entry) && typeOf(entry).includes('CalendarDayButton')
                && boundsOf(entry).width > 0)
            .sort((left, right) => boundsOf(left).y - boundsOf(right).y
                || boundsOf(left).x - boundsOf(right).x);
        const currentMonthStart = visibleCalendarDays.findIndex(entry => textOf(entry) === '1');
        const [, calendarYearText, calendarMonthText] = fullCalendarHeaderText.match(/^(\d{4})年(\d{1,2})月$/);
        const calendarYear = Number(calendarYearText);
        const calendarMonth = Number(calendarMonthText);
        const currentMonthDays = new Date(calendarYear, calendarMonth, 0).getDate();
        const nextMonthDayThree = visibleCalendarDays[currentMonthStart + currentMonthDays + 2];
        const expectedSelectedDate = new Date(calendarYear, calendarMonth, 3);
        assertUi(currentMonthStart >= 0 && textOf(nextMonthDayThree) === '3',
            '完整月历缺少可验证的相邻月份 3 日');
        await connection.clickNode(nextMonthDayThree);
        const selectedAdjacentDate = await connection.waitForTree(current => {
            const title = findByName(current, 'DiaryDateTitle');
            return !findByName(current, 'DiaryCalendar')
                && title && isCurrentDateText(textOf(title), expectedSelectedDate) ? title : null;
        }, 3000, '点击相邻月份日期后没有选中该日期并关闭 Flyout');
        const expectedCalendarHeader = formatCompactCalendarHeader(expectedSelectedDate);
        assertUi(textOf(findByName(selectedAdjacentDate.tree, 'CompactCalendarHeader')) === expectedCalendarHeader,
            '点击相邻月份日期后周历标题没有切换到目标月份');

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
            rightClickSelectsDate: true,
            todayAndSelectedStates: true,
            compactHeaderContextMenu: true,
            fullCalendarHeight: fullCalendarBounds.height,
            calendarItemWidth: calendarItemBounds.width,
            calendarItemHeight: calendarItemBounds.height,
            fullCalendarResetsToMonth: true,
            adjacentMonthSelectionClosesFlyout: true,
            adjacentMonthSelectedDate: textOf(selectedAdjacentDate.value),
            wheelWeekBrowsing: true,
            wheelWeekHeader,
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
        const noteInput = findByName(editorTree, 'WorkNoteInput');
        const noteCard = findByName(editorTree, 'WorkNoteCard');
        assertUi(dateInput && titleInput && timeInput && noteInput && noteCard, '事项编辑字段结构不完整');
        assertUi(findByText(editorTree, '仅本地'), '备注区缺少直接显示的本地保存状态');
        assertUi(!findByText(editorTree, '补充只在本机保存的上下文信息'),
            '备注区仍依赖信息图标 Tooltip 显示本地保存说明');
        assertUi(!findByName(editorTree, 'TrackerAssociationCard'),
            '没有启用 Tracker 时仍显示 Tracker 关联卡片');
        const noteBounds = boundsOf(noteInput);
        const [noteBox, noteCardBox] = await Promise.all([
            connection.client.send('DOM.getBoxModel', { nodeId: noteInput.nodeId }),
            connection.client.send('DOM.getBoxModel', { nodeId: noteCard.nodeId }),
        ]);
        const quadBottom = quad => Math.max(...quad.filter((_, index) => index % 2 === 1));
        const noteBottomGap = quadBottom(noteCardBox.model.border) - quadBottom(noteBox.model.border);
        assertUi(noteBounds.height >= 180 && noteBottomGap <= 18,
            `备注编辑框没有填满卡片剩余高度：height=${noteBounds.height}, bottomGap=${noteBottomGap}`);
        const generalTitle = findByText(editorTree, '一般信息');
        const generalDescription = findByText(editorTree, '日期、内容、耗时与优先级');
        const generalTitleBounds = boundsOf(generalTitle);
        const generalDescriptionBounds = boundsOf(generalDescription);
        assertUi(generalTitle && generalDescription
            && generalTitleBounds.x + generalTitleBounds.width <= generalDescriptionBounds.x
            && Math.abs(generalTitleBounds.y + generalTitleBounds.height
                - generalDescriptionBounds.y - generalDescriptionBounds.height) <= 2,
        '一般信息标题与说明没有左右排列并保持底部对齐');
        const draftStatusPill = findByName(editorTree, 'SelectedWorkStatusPill');
        assertUi(draftStatusPill && isVisible(draftStatusPill)
            && String(draftStatusPill.a.Class ?? '').includes('StatusWarning')
            && descendants(editorTree, draftStatusPill).some(entry => textOf(entry).includes('未保存 · 待保存')),
        '新事项状态胶囊没有使用未保存警告配色');
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
        const saved = await connection.waitForTree(tree => findByText(tree, workTitle) && findByTextContains(tree, '本地已保存'),
            10000, 'Ctrl+S 后未观察到本地保存');
        const savedStatusPill = findByName(saved.tree, 'SelectedWorkStatusPill');
        assertUi(savedStatusPill
            && !['StatusWarning', 'StatusInfo', 'StatusSuccess', 'StatusError', 'StatusUncertain']
                .some(className => String(savedStatusPill.a.Class ?? '').includes(className)),
        '未配置 Tracker 的已保存事项不应使用同步语义色');
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
        return {
            editorMs: editor.elapsedMs,
            duplicateMs: duplicate.elapsedMs,
            confirmMs: confirm.elapsedMs,
            sectionHeadingInline: true,
            noteEditorHeight: noteBounds.height,
            noteEditorBottomGap: noteBottomGap,
            trackerAssociationHidden: true,
            draftStatusTone: 'warning',
            savedStatusTone: 'neutral',
        };
    });

    await runStep('query.execute-saved', '查询、条件折叠和保存查询维护', async () => {
        await connection.navigate('事项查询', 'WorkItemQueryView');
        await clickTextInView(connection, 'WorkItemQueryView', '查询');
        const queried = await connection.waitForTree(tree => findByText(tree, workTitle), 10000, '查询结果未包含新建事项');
        let tree = queried.tree;
        const filter = findByName(tree, 'FilterToggle');
        assertUi(filter && isChecked(filter), '查询条件默认未展开');
        const setFilterExpanded = async (expanded, message) => {
            let lastError;
            for (let attempt = 0; attempt < 3; attempt += 1) {
                const current = await connection.getTree();
                const toggle = findByName(current, 'FilterToggle');
                assertUi(toggle, '查询条件开关不存在');
                if (Boolean(isChecked(toggle)) === expanded)
                    return;
                if (attempt === 0)
                    await connection.clickNode(toggle);
                else {
                    await connection.client.send('DOM.focus', { nodeId: toggle.nodeId });
                    await connection.pressKey(' ', 'Space', 32);
                }
                try {
                    await connection.waitForTree(next => Boolean(isChecked(findByName(next, 'FilterToggle'))) === expanded,
                        1500, message);
                    return;
                }
                catch (error) {
                    lastError = error;
                    await delay(120);
                }
            }
            throw lastError;
        };
        await setFilterExpanded(false, '查询条件未折叠');
        await setFilterExpanded(true, '查询条件未展开');
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
        try {
            await connection.waitForTree(current => isChecked(findByName(current, 'StatisticsChartTypeToggle')),
                1500, '统计图表没有切换到饼图');
        }
        catch {
            const current = await connection.getTree();
            const toggle = findByName(current, 'StatisticsChartTypeToggle');
            await connection.client.send('DOM.focus', { nodeId: toggle.nodeId });
            await connection.pressKey(' ', 'Space', 32);
            await connection.waitForTree(next => isChecked(findByName(next, 'StatisticsChartTypeToggle')),
                5000, '统计图表没有切换到饼图');
        }
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
