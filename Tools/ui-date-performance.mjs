#!/usr/bin/env node

import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import {
    ancestor,
    delay,
    descendants,
    findByName,
    findByText,
    localDateText,
    textOf,
    typeOf,
} from './ui-cdp.mjs';
import { assertUi, runUiSuite } from './ui-suite.mjs';

const execFileAsync = promisify(execFile);
const titlePrefix = 'CDP日期性能';
const dayCount = 540;
const itemsPerDay = 48;
const serialSwitchesPerDirection = 60;
const burstSwitchesPerDirection = 24;
const postgresEnvironment = {
    host: process.env.DIARY_UI_TEST_PG_HOST?.trim(),
    port: process.env.DIARY_UI_TEST_PG_PORT?.trim() || '5432',
    database: process.env.DIARY_UI_TEST_PG_DATABASE?.trim(),
    user: process.env.DIARY_UI_TEST_PG_USER?.trim(),
    password: process.env.DIARY_UI_TEST_PG_PASSWORD ?? '',
};
const databaseMode = postgresEnvironment.host ? 'postgresql' : 'sqlite';
const redmineMode = Boolean(process.env.DIARY_UI_TEST_REDMINE_URL?.trim());

function percentile(values, ratio) {
    const sorted = [...values].sort((left, right) => left - right);
    if (sorted.length === 0)
        return 0;
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

function addDays(value, days) {
    const result = new Date(value.getFullYear(), value.getMonth(), value.getDate());
    result.setDate(result.getDate() + days);
    return result;
}

function markerTitle(date) {
    return titlePrefix + ' ' + localDateText(date) + ' #00';
}

async function collectDatabaseFiles(root) {
    const result = [];
    const visit = async directory => {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                await visit(fullPath);
                continue;
            }
            if (!entry.isFile() || !/\.sqlite3(?:-(?:wal|shm|journal))?$/.test(entry.name))
                continue;
            const stat = await fs.stat(fullPath);
            result.push({
                path: path.relative(root, fullPath),
                size: stat.size,
                mtimeMs: stat.mtimeMs,
            });
        }
    };
    await visit(root);
    return result.sort((left, right) => left.path.localeCompare(right.path));
}

function compareDatabaseFiles(before, after) {
    const relevant = file => !file.path.endsWith('-shm');
    const beforeMap = new Map(before.filter(relevant).map(file => [file.path, file]));
    const afterMap = new Map(after.filter(relevant).map(file => [file.path, file]));
    const paths = new Set([...beforeMap.keys(), ...afterMap.keys()]);
    const changes = [];
    for (const filePath of [...paths].sort()) {
        const previous = beforeMap.get(filePath);
        const current = afterMap.get(filePath);
        if (!previous || !current) {
            changes.push({ path: filePath, kind: previous ? 'removed' : 'added', previous, current });
            continue;
        }
        if (previous.size !== current.size || previous.mtimeMs !== current.mtimeMs) {
            changes.push({
                path: filePath,
                kind: 'modified',
                sizeDelta: current.size - previous.size,
                previousMtimeMs: previous.mtimeMs,
                currentMtimeMs: current.mtimeMs,
            });
        }
    }
    return changes;
}

async function collectPostgreSqlState(withPlugins) {
    for (const [name, value] of Object.entries(postgresEnvironment)) {
        if (name !== 'password')
            assertUi(value, 'PostgreSQL 日期性能测试缺少连接参数：' + name);
    }
    const trackerDatasetField = withPlugins
        ? redmineMode
            ? `, 'trackerBindingCount', (SELECT COUNT(*) FROM redmine_time_entries r JOIN perf_items p ON p.id = r.work_id WHERE r.instance_id = 'redmine.cdp-performance')`
            : `, 'trackerBindingCount', (SELECT COUNT(*) FROM jira_work_entries r JOIN perf_items p ON p.id = r.work_id WHERE r.instance_id = 'jira.cdp-performance')`
        : '';
    const query = `
        WITH perf_items AS (
            SELECT id, comment, hours
            FROM work_items
            WHERE comment LIKE '${titlePrefix} %'
        ), dataset AS (
            SELECT COUNT(*) AS item_count,
                   COALESCE(SUM(id), 0) AS id_sum,
                   COALESCE(SUM(LENGTH(comment)), 0) AS comment_length_sum,
                   COALESCE(SUM(hours), 0) AS hours_sum,
                   (SELECT COUNT(*) FROM work_notes n JOIN perf_items p ON p.id = n.id) AS note_count,
                   (SELECT COUNT(*) FROM work_item_tags t JOIN perf_items p ON p.id = t.work_id) AS tag_count,
                   (SELECT COUNT(*) FROM work_item_extra_field_values e JOIN perf_items p ON p.id = e.work_id) AS extra_field_count
            FROM perf_items
        )
        SELECT json_build_object(
            'provider', 'PostgreSQL',
            'dataset', json_build_object(
                'itemCount', dataset.item_count,
                'idSum', dataset.id_sum,
                'commentLengthSum', dataset.comment_length_sum,
                'hoursSum', dataset.hours_sum,
                'noteCount', dataset.note_count,
                'tagCount', dataset.tag_count,
                'extraFieldCount', dataset.extra_field_count
                ${trackerDatasetField}),
            'writeCounters', json_build_object(
                'tuplesInserted', stats.tup_inserted,
                'tuplesUpdated', stats.tup_updated,
                'tuplesDeleted', stats.tup_deleted,
                'tempBytes', stats.temp_bytes))::text
        FROM dataset
        JOIN pg_stat_database stats ON stats.datname = current_database();`;
    const { stdout } = await execFileAsync('psql', [
        '-X', '-qAt', '-v', 'ON_ERROR_STOP=1',
        '-h', postgresEnvironment.host,
        '-p', postgresEnvironment.port,
        '-U', postgresEnvironment.user,
        '-d', postgresEnvironment.database,
        '-c', query,
    ], {
        env: { ...process.env, PGPASSWORD: postgresEnvironment.password },
        timeout: 15000,
        maxBuffer: 1024 * 1024,
    });
    return JSON.parse(stdout.trim());
}

async function collectDatabaseState(profile, withPlugins = false) {
    if (databaseMode === 'postgresql')
        return collectPostgreSqlState(withPlugins);
    return {
        provider: 'SQLite',
        files: await collectDatabaseFiles(profile),
    };
}

function compareDatabaseState(before, after) {
    if (databaseMode === 'sqlite')
        return compareDatabaseFiles(before.files, after.files);
    const changes = [];
    if (JSON.stringify(before.dataset) !== JSON.stringify(after.dataset))
        changes.push({ kind: 'dataset-changed', previous: before.dataset, current: after.dataset });
    for (const key of ['tuplesInserted', 'tuplesUpdated', 'tuplesDeleted']) {
        const delta = Number(after.writeCounters[key]) - Number(before.writeCounters[key]);
        if (delta !== 0)
            changes.push({ kind: 'postgres-write-counter', counter: key, delta });
    }
    return changes;
}

async function linuxProcessMetrics(processId) {
    const [ioText, statusText, statText] = await Promise.all([
        fs.readFile('/proc/' + processId + '/io', 'utf8'),
        fs.readFile('/proc/' + processId + '/status', 'utf8'),
        fs.readFile('/proc/' + processId + '/stat', 'utf8'),
    ]);
    const io = Object.fromEntries(ioText.trim().split('\n').map(line => {
        const separator = line.indexOf(':');
        return [line.slice(0, separator), Number(line.slice(separator + 1).trim())];
    }));
    const rssMatch = /^VmRSS:\s+(\d+)\s+kB$/m.exec(statusText);
    const statFields = statText.slice(statText.lastIndexOf(')') + 2).trim().split(/\s+/);
    let clockTicks = 100;
    try {
        clockTicks = Number((await execFileAsync('getconf', ['CLK_TCK'])).stdout.trim()) || 100;
    }
    catch {
        // 100 是 Linux 常见 CLK_TCK；仅用于测试报告中的趋势值。
    }
    return {
        available: true,
        platform: 'linux',
        cpuTimeMs: ((Number(statFields[11]) + Number(statFields[12])) / clockTicks) * 1000,
        workingSetBytes: Number(rssMatch?.[1] ?? 0) * 1024,
        readBytes: io.read_bytes ?? 0,
        writeBytes: io.write_bytes ?? 0,
        readOperations: io.syscr ?? 0,
        writeOperations: io.syscw ?? 0,
    };
}

async function windowsProcessMetrics(processId) {
    const executable = process.env.ComSpec?.toLowerCase().includes('cmd.exe') ? 'powershell.exe' : 'pwsh.exe';
    const script = [
        "$p = Get-CimInstance Win32_Process -Filter 'ProcessId = " + processId + "'",
        'if ($null -eq $p) { throw "process not found" }',
        '[pscustomobject]@{',
        ' cpuTimeMs = (([double]$p.KernelModeTime + [double]$p.UserModeTime) / 10000)',
        ' workingSetBytes = [double]$p.WorkingSetSize',
        ' readBytes = [double]$p.ReadTransferCount',
        ' writeBytes = [double]$p.WriteTransferCount',
        ' readOperations = [double]$p.ReadOperationCount',
        ' writeOperations = [double]$p.WriteOperationCount',
        '} | ConvertTo-Json -Compress',
    ].join('; ');
    const { stdout } = await execFileAsync(executable, ['-NoProfile', '-NonInteractive', '-Command', script], {
        windowsHide: true,
        timeout: 10000,
    });
    return { available: true, platform: 'win32', ...JSON.parse(stdout.trim()) };
}

async function collectProcessMetrics(processId) {
    try {
        if (process.platform === 'linux')
            return await linuxProcessMetrics(processId);
        if (process.platform === 'win32')
            return await windowsProcessMetrics(processId);
        return { available: false, platform: process.platform, reason: 'unsupported-platform' };
    }
    catch (error) {
        return {
            available: false,
            platform: process.platform,
            reason: error instanceof Error ? error.message : String(error),
        };
    }
}

function processMetricDelta(before, after) {
    if (!before.available || !after.available)
        return { available: false, before, after };
    return {
        available: true,
        platform: before.platform,
        cpuTimeMs: after.cpuTimeMs - before.cpuTimeMs,
        workingSetBytes: after.workingSetBytes,
        workingSetDeltaBytes: after.workingSetBytes - before.workingSetBytes,
        readBytes: after.readBytes - before.readBytes,
        writeBytes: after.writeBytes - before.writeBytes,
        readOperations: after.readOperations - before.readOperations,
        writeOperations: after.writeOperations - before.writeOperations,
    };
}

await runUiSuite({
    name: 'ui-date-performance',
    scenario: 'date-performance',
    timeoutMs: 15000,
    stopOnFailure: true,
}, async ({ connection, runStep, addFinding }) => {
    let databaseBefore;
    let processBefore;
    let measurementStarted;
    let serialMetrics;
    let burstMetrics;

    const focusCalendar = async date => {
        const tree = await connection.getTree();
        const calendar = findByName(tree, 'CompactCalendarDays');
        assertUi(calendar, '找不到日期性能测试所需的 CompactCalendarDays');
        const dayText = String(date.getDate());
        const selectedDayText = descendants(tree, calendar).find(entry =>
            textOf(entry) === dayText
            && String(entry.a.Class ?? '').includes('Selected'));
        const dayButton = ancestor(tree, selectedDayText, entry => typeOf(entry).includes('Button'));
        assertUi(dayButton, '找不到当前日期按钮：' + localDateText(date));
        await connection.client.send('DOM.focus', { nodeId: calendar.nodeId });
    };

    const waitForDate = async date => connection.waitForTree(
        tree => findByText(tree, markerTitle(date)),
        15000,
        '日期切换后未加载性能样本：' + localDateText(date));

    const switchDate = async (key, code, virtualKeyCode, expectedDate) => {
        const started = performance.now();
        await connection.pressKey(key, code, virtualKeyCode);
        await waitForDate(expectedDate);
        return performance.now() - started;
    };

    await runStep('date-performance.dataset', '确认大量日期性能数据和日历入口', async () => {
        const navigationMs = await connection.navigate('日记记录', 'DiaryEditorView');
        await connection.clickByText('回到今天');
        const today = new Date();
        const ready = await waitForDate(today);
        await focusCalendar(today);
        const databaseState = await collectDatabaseState(
            connection.state.profile,
            connection.state.withPlugins);
        if (databaseMode === 'sqlite')
            assertUi(databaseState.files.some(file => file.path.endsWith('.sqlite3')), '隔离 profile 中未找到 SQLite 数据库');
        else
        {
            assertUi(Number(databaseState.dataset.itemCount) === dayCount * itemsPerDay,
                'PostgreSQL 性能数据数量不正确：' + JSON.stringify(databaseState.dataset));
            assertUi(Number(databaseState.dataset.noteCount) === dayCount * itemsPerDay / 4,
                'PostgreSQL 性能备注数量不正确：' + JSON.stringify(databaseState.dataset));
            assertUi(Number(databaseState.dataset.tagCount) === dayCount * itemsPerDay * 13 / 10,
                'PostgreSQL 性能标签关系数量不正确：' + JSON.stringify(databaseState.dataset));
            assertUi(Number(databaseState.dataset.extraFieldCount) === dayCount * itemsPerDay / 3,
                'PostgreSQL 性能附加字段数量不正确：' + JSON.stringify(databaseState.dataset));
        }
        if (databaseMode === 'postgresql' && connection.state.withPlugins)
            assertUi(Number(databaseState.dataset.trackerBindingCount) === dayCount * itemsPerDay / 5,
                'PostgreSQL Tracker 性能数据数量不正确：' + JSON.stringify(databaseState.dataset));
        return {
            navigationMs,
            readyMs: ready.elapsedMs,
            databaseMode,
            trackerMode: connection.state.withPlugins ? (redmineMode ? 'redmine' : 'jira') : 'none',
            dataset: {
                days: dayCount,
                itemsPerDay,
                totalItems: dayCount * itemsPerDay,
                notes: dayCount * itemsPerDay / 4,
                tagRelations: dayCount * itemsPerDay * 13 / 10,
                extraFieldValues: dayCount * itemsPerDay / 3,
                trackerBindings: connection.state.withPlugins ? dayCount * itemsPerDay / 5 : 0,
            },
            databaseState,
        };
    });

    await runStep('date-performance.warmup', '预热相邻日期加载', async () => {
        const samples = [];
        let current = new Date();
        for (let index = 0; index < 4; index++) {
            current = addDays(current, 1);
            samples.push(await switchDate('ArrowRight', 'ArrowRight', 39, current));
            current = addDays(current, -1);
            samples.push(await switchDate('ArrowLeft', 'ArrowLeft', 37, current));
        }
        await delay(250);
        databaseBefore = await collectDatabaseState(
            connection.state.profile,
            connection.state.withPlugins);
        processBefore = await collectProcessMetrics(connection.state.processId);
        measurementStarted = performance.now();
        return { samplesMs: samples, processBefore, databaseBefore };
    });

    await runStep('date-performance.serial-switching', '连续逐次切换 120 次日期', async () => {
        const samples = [];
        const slowest = [];
        let current = new Date();
        for (const direction of [
            { key: 'ArrowRight', code: 'ArrowRight', virtualKeyCode: 39, delta: 1 },
            { key: 'ArrowLeft', code: 'ArrowLeft', virtualKeyCode: 37, delta: -1 },
        ]) {
            for (let index = 0; index < serialSwitchesPerDirection; index++) {
                current = addDays(current, direction.delta);
                const elapsedMs = await switchDate(
                    direction.key, direction.code, direction.virtualKeyCode, current);
                samples.push(elapsedMs);
                slowest.push({ date: localDateText(current), elapsedMs });
            }
        }
        slowest.sort((left, right) => right.elapsedMs - left.elapsedMs);
        serialMetrics = {
            switches: samples.length,
            p50Ms: percentile(samples, 0.5),
            p95Ms: percentile(samples, 0.95),
            p99Ms: percentile(samples, 0.99),
            maxMs: Math.max(...samples),
            averageMs: samples.reduce((sum, value) => sum + value, 0) / samples.length,
            slowest: slowest.slice(0, 10),
            samplesMs: samples,
        };
        if (serialMetrics.p95Ms > 300)
            addFinding('warning', 'date-switch-p95-slow', '逐次日期切换 P95 超过 300 毫秒', serialMetrics);
        if (serialMetrics.maxMs > 1500)
            addFinding('warning', 'date-switch-max-slow', '单次日期切换最大耗时超过 1.5 秒', serialMetrics);
        return serialMetrics;
    });

    await runStep('date-performance.burst-switching', '模拟按住方向键的高速日期切换', async () => {
        let current = new Date();
        await focusCalendar(current);
        const runBurst = async (key, code, virtualKeyCode, delta) => {
            const started = performance.now();
            for (let index = 0; index < burstSwitchesPerDirection; index++) {
                current = addDays(current, delta);
                await connection.pressKey(key, code, virtualKeyCode);
            }
            const dispatchedMs = performance.now() - started;
            const settled = await waitForDate(current);
            const totalMs = performance.now() - started;
            return {
                switches: burstSwitchesPerDirection,
                dispatchedMs,
                settleMs: settled.elapsedMs,
                totalMs,
                switchesPerSecond: burstSwitchesPerDirection / (totalMs / 1000),
                finalDate: localDateText(current),
            };
        };

        const forward = await runBurst('ArrowRight', 'ArrowRight', 39, 1);
        const backward = await runBurst('ArrowLeft', 'ArrowLeft', 37, -1);
        burstMetrics = { forward, backward };
        if (Math.max(forward.totalMs, backward.totalMs) > 8000)
            addFinding('warning', 'date-burst-slow', '24 次高速日期切换超过 8 秒', burstMetrics);
        return burstMetrics;
    });

    await runStep('date-performance.io-integrity', '确认只浏览日期没有修改数据库', async () => {
        await delay(databaseMode === 'postgresql' ? 1500 : 500);
        const databaseAfter = await collectDatabaseState(
            connection.state.profile,
            connection.state.withPlugins);
        const databaseChanges = compareDatabaseState(databaseBefore, databaseAfter);
        const processAfter = await collectProcessMetrics(connection.state.processId);
        const processDelta = processMetricDelta(processBefore, processAfter);
        const wallTimeMs = performance.now() - measurementStarted;
        if (processDelta.available)
            processDelta.averageCpuCores = processDelta.cpuTimeMs / wallTimeMs;
        assertUi(databaseChanges.length === 0,
            '日期切换期间数据库发生写入或测试数据变化：' + JSON.stringify(databaseChanges));
        if (processDelta.available && processDelta.writeBytes > 1024 * 1024)
            addFinding('warning', 'date-switch-process-write-high', '日期切换期间进程写入超过 1 MiB', processDelta);
        if (processDelta.available && processDelta.workingSetDeltaBytes > 256 * 1024 * 1024)
            addFinding('warning', 'date-switch-memory-growth-high', '日期切换期间进程工作集增长超过 256 MiB', processDelta);
        return {
            databaseBefore,
            databaseAfter,
            databaseChanges,
            databaseMode,
            processBefore,
            processAfter,
            processDelta,
            wallTimeMs,
            serialMetrics,
            burstMetrics,
        };
    });

    await runStep('date-performance.screenshot', '保存日期性能场景截图', async () =>
        connection.screenshot('date-performance-final.png'));
});
