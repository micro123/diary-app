#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const execFileAsync = promisify(execFile);
const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '..');
const statePath = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'current.json');
const reportDirectory = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'reports');

function argumentValue(name, fallback = '') {
    const index = process.argv.indexOf(name);
    return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

function integerArgument(name, fallback, minimum, maximum) {
    const value = Number(argumentValue(name, fallback));
    if (!Number.isInteger(value) || value < minimum || value > maximum)
        throw new Error(`${name} 必须是 ${minimum} 到 ${maximum} 的整数`);
    return value;
}

const runs = integerArgument('--runs', 5, 1, 100);
const hotRounds = integerArgument('--hot-rounds', 5, 1, 50);
const preloadWaitMs = integerArgument('--preload-wait-ms', 1800, 0, 10000);
const port = integerArgument('--port', 9340, 1024, 65535);
const mode = argumentValue('--mode', 'core').toLowerCase();
if (!['core', 'full'].includes(mode))
    throw new Error('--mode 只能是 core 或 full');
const seedProfile = argumentValue('--seed-profile');
const display = argumentValue('--display');
const requireDynamicPage = process.argv.includes('--require-dynamic-page');
const build = process.argv.includes('--build');

function percentile(values, ratio) {
    if (values.length === 0)
        return null;
    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * ratio) - 1)];
}

function summarize(values) {
    if (values.length === 0)
        return null;
    return {
        count: values.length,
        averageMs: values.reduce((sum, value) => sum + value, 0) / values.length,
        p50Ms: percentile(values, 0.5),
        p95Ms: percentile(values, 0.95),
        p99Ms: percentile(values, 0.99),
        maxMs: Math.max(...values),
        minMs: Math.min(...values),
    };
}

function round(value) {
    return typeof value === 'number' ? Math.round(value * 100) / 100 : value;
}

function roundObject(value) {
    if (Array.isArray(value))
        return value.map(roundObject);
    if (!value || typeof value !== 'object')
        return round(value);
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, roundObject(child)]));
}

async function exists(filePath) {
    try {
        await fs.access(filePath);
        return true;
    }
    catch {
        return false;
    }
}

async function runCommand(executable, args, options = {}) {
    const result = await execFileAsync(executable, args, {
        cwd: repositoryRoot,
        env: process.env,
        windowsHide: true,
        timeout: options.timeout ?? 180000,
        maxBuffer: 20 * 1024 * 1024,
    });
    if (options.echo !== false && result.stdout.trim())
        process.stdout.write(result.stdout);
    if (result.stderr.trim())
        process.stderr.write(result.stderr);
    return result;
}

function uiTestCommand(command, extra = []) {
    if (process.platform === 'win32') {
        return {
            executable: process.env.DIARY_PWSH || 'pwsh.exe',
            args: ['-NoProfile', '-NonInteractive', '-File', path.join(scriptDirectory, 'ui-test.ps1'), command, ...extra],
        };
    }
    return {
        executable: 'bash',
        args: [path.join(scriptDirectory, 'ui-test.sh'), command, ...extra],
    };
}

async function startApplication(runIndex) {
    const extra = process.platform === 'win32'
        ? ['-NoBuild', '-Scenario', 'navigation-performance', '-Port', String(port)]
        : ['--no-build', '--scenario', 'navigation-performance', '--port', String(port)];
    if (mode === 'full')
        extra.push(process.platform === 'win32' ? '-WithPlugins' : '--with-plugins');
    if (seedProfile) {
        extra.push(process.platform === 'win32' ? '-SeedProfile' : '--seed-profile', seedProfile);
    }
    if (display && process.platform !== 'win32')
        extra.push('--display', display);
    const command = uiTestCommand('start', extra);
    process.stdout.write(`\n=== 启动第 ${runIndex + 1}/${runs} 个新进程 ===\n`);
    await runCommand(command.executable, command.args);
}

async function stopApplication() {
    const command = uiTestCommand('stop');
    try {
        await runCommand(command.executable, command.args, { echo: false, timeout: 30000 });
    }
    catch (error) {
        process.stderr.write(`停止 UI 测试进程失败：${error.message}\n`);
    }
}

async function latestSuiteReport(previous) {
    const names = (await fs.readdir(reportDirectory))
        .filter(name => name.startsWith('ui-navigation-performance-') && name.endsWith('.json'));
    const candidates = [];
    for (const name of names) {
        const filePath = path.join(reportDirectory, name);
        const stat = await fs.stat(filePath);
        if (!previous.has(filePath))
            candidates.push({ filePath, mtimeMs: stat.mtimeMs });
    }
    candidates.sort((left, right) => right.mtimeMs - left.mtimeMs);
    if (candidates.length === 0)
        throw new Error('没有找到本轮导航性能报告');
    return candidates[0].filePath;
}

function stepDetails(report, id) {
    const step = report.steps.find(item => item.id === id);
    if (!step || step.status !== 'passed')
        throw new Error(`报告缺少成功步骤：${id}`);
    return step.details;
}

function aggregateReports(reports) {
    const coldSamples = reports.flatMap(report =>
        stepDetails(report, 'navigation-performance.cold').samples.filter(sample => sample.phase === 'cold'));
    const hotSamples = reports.flatMap(report => stepDetails(report, 'navigation-performance.hot').samples);
    const startup = reports.map(report => stepDetails(report, 'navigation-performance.startup-inventory'));
    const pageLabels = [...new Set([...coldSamples, ...hotSamples].map(sample => sample.to))];
    const byPage = Object.fromEntries(pageLabels.map(label => {
        const cold = coldSamples.filter(sample => sample.to === label);
        const hot = hotSamples.filter(sample => sample.to === label);
        const coldVisible = summarize(cold.map(sample => sample.visibleMs));
        const hotVisible = summarize(hot.map(sample => sample.visibleMs));
        return [label, {
            coldVisible,
            coldSettled: summarize(cold.map(sample => sample.settledMs)),
            hotVisible,
            hotSettled: summarize(hot.map(sample => sample.settledMs)),
            firstVisitPenalty: coldVisible && hotVisible && hotVisible.p50Ms > 0
                ? coldVisible.p50Ms / hotVisible.p50Ms
                : null,
        }];
    }));
    return roundObject({
        suite: 'ui-navigation-performance-aggregate',
        status: reports.every(report => report.status === 'passed') ? 'passed' : 'failed',
        mode,
        runs: reports.length,
        hotRounds,
        preloadWaitMs,
        generatedAt: new Date().toISOString(),
        startup: {
            cdpReady: summarize(startup.map(item => item.startupReadyMs)),
            diaryStable: summarize(startup.map(item => item.startupToDiaryStableMs).filter(Number.isFinite)),
        },
        pages: byPage,
        samples: {
            cold: coldSamples,
            hot: hotSamples,
        },
        runReports: reports.map(report => report.reportPath),
        findings: reports.flatMap((report, index) => report.findings.map(finding => ({ run: index + 1, ...finding }))),
    });
}

function milliseconds(value) {
    return Number.isFinite(value) ? `${Math.round(value)} ms` : '—';
}

function markdownReport(aggregate) {
    const lines = [
        '# 主导航冷热切换性能报告',
        '',
        `- 模式：${aggregate.mode}`,
        `- 新进程次数：${aggregate.runs}`,
        `- 每进程热切换轮数：${aggregate.hotRounds}`,
        `- 日记页稳定后的预热等待：${aggregate.preloadWaitMs} ms`,
        `- CDP Ready P50/P95：${milliseconds(aggregate.startup.cdpReady?.p50Ms)} / ${milliseconds(aggregate.startup.cdpReady?.p95Ms)}`,
        `- 启动至日记页稳定 P50/P95：${milliseconds(aggregate.startup.diaryStable?.p50Ms)} / ${milliseconds(aggregate.startup.diaryStable?.p95Ms)}`,
        '',
        '| 页面 | 冷切换 P50 | 冷切换最大值 | 热切换 P50 | 热切换 P95 | 热切换 P99 | 首次访问惩罚 |',
        '| --- | ---: | ---: | ---: | ---: | ---: | ---: |',
    ];
    for (const [label, metrics] of Object.entries(aggregate.pages)) {
        lines.push(`| ${label} | ${milliseconds(metrics.coldVisible?.p50Ms)} | ${milliseconds(metrics.coldVisible?.maxMs)} | ${milliseconds(metrics.hotVisible?.p50Ms)} | ${milliseconds(metrics.hotVisible?.p95Ms)} | ${milliseconds(metrics.hotVisible?.p99Ms)} | ${Number.isFinite(metrics.firstVisitPenalty) ? `${metrics.firstVisitPenalty.toFixed(2)}x` : '—'} |`);
    }
    if (aggregate.findings.length > 0) {
        lines.push('', '## 警告', '');
        for (const finding of aggregate.findings)
            lines.push(`- 第 ${finding.run} 轮：${finding.message}`);
    }
    lines.push('');
    return lines.join('\n');
}

if (await exists(statePath))
    throw new Error('已有 UI 测试状态文件，请先停止当前 UI 测试程序');
if (build)
    await runCommand('dotnet', ['build', 'Diary.App/Diary.App.csproj', '--configuration', 'Debug']);

await fs.mkdir(reportDirectory, { recursive: true });
const reports = [];
for (let runIndex = 0; runIndex < runs; runIndex++) {
    const before = new Set((await fs.readdir(reportDirectory))
        .filter(name => name.startsWith('ui-navigation-performance-') && name.endsWith('.json'))
        .map(name => path.join(reportDirectory, name)));
    try {
        await startApplication(runIndex);
        const suiteArgs = [
            path.join(scriptDirectory, 'ui-navigation-performance.mjs'),
            '--state', statePath,
            '--hot-rounds', String(hotRounds),
            '--preload-wait-ms', String(preloadWaitMs),
            '--order-offset', String(runIndex),
        ];
        if (requireDynamicPage)
            suiteArgs.push('--require-dynamic-page');
        await runCommand(process.execPath, suiteArgs, { timeout: 300000 });
        const reportPath = await latestSuiteReport(before);
        const report = JSON.parse(await fs.readFile(reportPath, 'utf8'));
        if (report.status !== 'passed')
            throw new Error(`第 ${runIndex + 1} 轮失败：${reportPath}`);
        reports.push(report);
    }
    finally {
        await stopApplication();
    }
}

const aggregate = aggregateReports(reports);
const stamp = new Date().toISOString().replaceAll(':', '-').replaceAll('.', '-');
const jsonPath = path.join(reportDirectory, `ui-navigation-performance-aggregate-${stamp}.json`);
const markdownPath = path.join(reportDirectory, `ui-navigation-performance-aggregate-${stamp}.md`);
aggregate.reportPath = jsonPath;
aggregate.markdownPath = markdownPath;
await fs.writeFile(jsonPath, JSON.stringify(aggregate, null, 2) + '\n', 'utf8');
await fs.writeFile(markdownPath, markdownReport(aggregate), 'utf8');
process.stdout.write(JSON.stringify({
    status: aggregate.status,
    runs: aggregate.runs,
    mode: aggregate.mode,
    reportPath: jsonPath,
    markdownPath,
}, null, 2) + '\n');
