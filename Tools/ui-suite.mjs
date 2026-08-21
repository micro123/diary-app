#!/usr/bin/env node

import process from 'node:process';
import { connectUiTest, writeSuiteReport } from './ui-cdp.mjs';

export function assertUi(condition, message) {
    if (!condition)
        throw new Error(message);
}

function safeFilePart(value) {
    return value.replace(/[^a-zA-Z0-9_-]+/g, '-').replace(/^-+|-+$/g, '');
}

export async function runUiSuite(options, defineSteps) {
    const connection = await connectUiTest({ timeoutMs: options.timeoutMs ?? 8000 });
    const startedAt = new Date();
    const steps = [];
    const findings = [];
    let stepIndex = 0;

    const runStep = async (id, title, action) => {
        const started = performance.now();
        const record = { id, title, status: 'running' };
        steps.push(record);
        try {
            const details = await action(connection);
            record.status = 'passed';
            record.durationMs = performance.now() - started;
            if (details !== undefined)
                record.details = details;
            console.log('PASS ' + id + ' ' + Math.round(record.durationMs) + 'ms');
            return details;
        }
        catch (error) {
            record.status = 'failed';
            record.durationMs = performance.now() - started;
            record.error = error instanceof Error ? error.message : String(error);
            try {
                record.screenshot = await connection.screenshot(
                    options.name + '-' + String(++stepIndex).padStart(2, '0') + '-' + safeFilePart(id) + '.png');
            }
            catch (screenshotError) {
                record.screenshotError = screenshotError instanceof Error ? screenshotError.message : String(screenshotError);
            }
            console.error('FAIL ' + id + ' ' + record.error);
            if (options.stopOnFailure)
                throw error;
            return undefined;
        }
    };

    const addFinding = (severity, code, message, details) => {
        findings.push({ severity, code, message, ...(details === undefined ? {} : { details }) });
    };

    let definitionError;
    try {
        if (options.scenario)
            assertUi(connection.state.scenario === options.scenario,
                '场景不匹配：期望 ' + options.scenario + '，实际 ' + connection.state.scenario);
        await defineSteps({ connection, runStep, addFinding, assertUi });
    }
    catch (error) {
        definitionError = error instanceof Error ? error.message : String(error);
    }
    finally {
        connection.close();
    }

    const failed = steps.filter(step => step.status === 'failed');
    const completedAt = new Date();
    const report = await writeSuiteReport(options.name, {
        status: failed.length === 0 && !definitionError ? 'passed' : 'failed',
        scenario: connection.state.scenario,
        startedAt: startedAt.toISOString(),
        completedAt: completedAt.toISOString(),
        durationMs: completedAt.getTime() - startedAt.getTime(),
        processId: connection.state.processId,
        profile: connection.state.profile,
        startupReadyMs: connection.state.startupReadyMs,
        summary: {
            total: steps.length,
            passed: steps.length - failed.length,
            failed: failed.length,
        },
        steps,
        findings,
        ...(definitionError ? { definitionError } : {}),
    });
    console.log(JSON.stringify(report, null, 2));
    if (report.status !== 'passed')
        process.exitCode = 1;
    return report;
}
