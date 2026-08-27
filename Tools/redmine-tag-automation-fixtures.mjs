#!/usr/bin/env node

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, '..');
const credentialsIndex = process.argv.indexOf('--credentials');
const outputIndex = process.argv.indexOf('--output');
const credentialsPath = path.resolve(credentialsIndex >= 0
    ? process.argv[credentialsIndex + 1]
    : path.join(repositoryRoot, 'redmine_testing.txt'));
const outputPath = path.resolve(outputIndex >= 0
    ? process.argv[outputIndex + 1]
    : path.join(repositoryRoot, '.build-tmp', 'redmine-tag-automation-fixtures.json'));

const fixtureProjects = [
    {
        identifier: 'diaryapp-tag-auto-basic',
        name: 'DiaryApp 标签自动化 - 基础',
        description: 'DiaryApp 标签自动化基础测试夹具。由 Tools/redmine-tag-automation-fixtures.mjs 维护，请勿用于真实工作。',
        issues: [
            ['TA-BASIC-01 默认开发目标', 'Feature', 'Normal', '用于验证空字段由标签规则填充 Development 和目标 Issue。'],
            ['TA-BASIC-02 默认设计目标', 'Feature', 'Normal', '用于验证空字段由标签规则填充 Design 和目标 Issue。'],
            ['TA-BASIC-03 用户预选目标', 'Support', 'High', '用于预先选择 Tracker 值，验证关闭强制修改时保留用户选择。'],
            ['TA-BASIC-04 强制覆盖目标', 'Bug', 'Urgent', '用于验证开启强制修改时覆盖已有 Activity 和 Issue。'],
        ],
    },
    {
        identifier: 'diaryapp-tag-auto-conflict',
        name: 'DiaryApp 标签自动化 - 冲突',
        description: 'DiaryApp 标签自动化规则顺序和多标签冲突测试夹具。由工具脚本维护，请勿用于真实工作。',
        issues: [
            ['TA-CONFLICT-01 第一规则目标', 'Feature', 'Normal', '同一字段多规则冲突时，配置顺序中的第一条有效规则应获胜。'],
            ['TA-CONFLICT-02 第二规则目标', 'Bug', 'High', '同一字段多规则冲突时应被报告为冲突，不覆盖第一条规则。'],
            ['TA-CONFLICT-03 先添加标签目标', 'Support', 'Normal', '用于验证多个标签按实际添加顺序应用。'],
            ['TA-CONFLICT-04 后添加标签目标', 'Feature', 'High', '用于验证后添加标签基于前一步编辑器状态处理。'],
        ],
    },
    {
        identifier: 'diaryapp-tag-auto-lifecycle',
        name: 'DiaryApp 标签自动化 - 生命周期',
        description: 'DiaryApp 标签来源、保存重载和同步生命周期测试夹具。由工具脚本维护，请勿用于真实工作。',
        issues: [
            ['TA-LIFECYCLE-01 模板应用主标签目标', 'Feature', 'Normal', '用于验证“应用模板”替换已有标签并执行主标签规则。'],
            ['TA-LIFECYCLE-02 模板应用次标签目标', 'Support', 'Normal', '用于验证带主标签和次标签的模板按保存顺序执行规则。'],
            ['TA-LIFECYCLE-03 模板更新目标', 'Feature', 'High', '用于验证“更新自模板”只在当前无标签时添加模板标签并执行规则。'],
            ['TA-LIFECYCLE-04 重复事项目标', 'Support', 'Normal', '用于验证重复事项按复制标签顺序重新执行规则。'],
            ['TA-LIFECYCLE-05 重载保持目标', 'Bug', 'High', '用于验证保存和重载不会再次执行或改写用户覆盖值。'],
            ['TA-LIFECYCLE-06 工时同步目标', 'Feature', 'High', '用于验证规则生成的 Activity 和 Issue 可用于真实工时同步。'],
        ],
    },
];

function parseCredentials(content) {
    const lines = content.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
    if (lines.length < 2 || !/^https?:\/\//i.test(lines[0]) || !lines[1])
        throw new Error('凭据文件应包含两行：Redmine 地址和 API Key');
    return { baseUrl: lines[0].replace(/\/+$/, ''), apiKey: lines[1] };
}

async function request(credentials, method, requestPath, body) {
    const response = await fetch(credentials.baseUrl + requestPath, {
        method,
        headers: {
            'X-Redmine-API-Key': credentials.apiKey,
            Accept: 'application/json',
            ...(body ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body ? JSON.stringify(body) : undefined,
    });
    const text = await response.text();
    if (!response.ok)
        throw new Error(`${method} ${requestPath} 返回 HTTP ${response.status}: ${text.slice(0, 300)}`);
    return text ? JSON.parse(text) : {};
}

async function loadAllProjects(credentials) {
    const result = await request(credentials, 'GET', '/projects.json?limit=100', null);
    return result.projects ?? [];
}

async function loadProjectIssues(credentials, projectId) {
    const issues = [];
    for (let offset = 0; ; offset += 100) {
        const query = `/issues.json?project_id=${projectId}&status_id=*&limit=100&offset=${offset}`;
        const result = await request(credentials, 'GET', query, null);
        issues.push(...(result.issues ?? []));
        if (issues.length >= (result.total_count ?? issues.length))
            return issues;
    }
}

function requireIdByName(items, name, kind) {
    const item = items.find(candidate => candidate.name === name);
    if (!item)
        throw new Error(`Redmine 缺少测试所需${kind}：${name}`);
    return item.id;
}

async function ensureProject(credentials, existingProjects, definition, trackerIds) {
    let project = existingProjects.find(item => item.identifier === definition.identifier);
    let created = false;
    if (!project) {
        const result = await request(credentials, 'POST', '/projects.json', {
            project: {
                name: definition.name,
                identifier: definition.identifier,
                description: definition.description,
                is_public: false,
                tracker_ids: trackerIds,
                enabled_module_names: ['issue_tracking', 'time_tracking'],
            },
        });
        project = result.project;
        existingProjects.push(project);
        created = true;
    }
    return { project, created };
}

async function ensureIssues(credentials, project, definitions, assignedToId, trackers, priorities) {
    const existingIssues = await loadProjectIssues(credentials, project.id);
    const results = [];
    for (const [subject, trackerName, priorityName, description] of definitions) {
        let issue = existingIssues.find(item => item.subject === subject);
        let created = false;
        if (!issue) {
            const response = await request(credentials, 'POST', '/issues.json', {
                issue: {
                    project_id: project.id,
                    tracker_id: requireIdByName(trackers, trackerName, 'Tracker'),
                    priority_id: requireIdByName(priorities, priorityName, '优先级'),
                    status_id: 1,
                    assigned_to_id: assignedToId,
                    subject,
                    description,
                },
            });
            issue = response.issue;
            existingIssues.push(issue);
            created = true;
        }
        results.push({ id: issue.id, subject: issue.subject, created });
    }
    return results;
}

const credentials = parseCredentials(await fs.readFile(credentialsPath, 'utf8'));
const [currentUser, trackerResult, priorityResult] = await Promise.all([
    request(credentials, 'GET', '/users/current.json', null),
    request(credentials, 'GET', '/trackers.json', null),
    request(credentials, 'GET', '/enumerations/issue_priorities.json', null),
]);
if (!currentUser.user?.admin)
    throw new Error('当前 Redmine API 用户不是管理员，无法保证可创建测试项目');
const trackers = trackerResult.trackers ?? [];
const priorities = priorityResult.issue_priorities ?? [];
const trackerIds = ['Bug', 'Feature', 'Support'].map(name => requireIdByName(trackers, name, 'Tracker'));

const existingProjects = await loadAllProjects(credentials);
const manifest = {
    generatedAt: new Date().toISOString(),
    serverOrigin: new URL(credentials.baseUrl).origin,
    projects: [],
};

for (const definition of fixtureProjects) {
    const projectResult = await ensureProject(credentials, existingProjects, definition, trackerIds);
    const issues = await ensureIssues(
        credentials,
        projectResult.project,
        definition.issues,
        currentUser.user.id,
        trackers,
        priorities);
    manifest.projects.push({
        id: projectResult.project.id,
        identifier: definition.identifier,
        name: definition.name,
        created: projectResult.created,
        issues,
    });
}

await fs.mkdir(path.dirname(outputPath), { recursive: true });
await fs.writeFile(outputPath, JSON.stringify(manifest, null, 2) + '\n', 'utf8');
console.log(JSON.stringify({
    status: 'ready',
    projectsCreated: manifest.projects.filter(project => project.created).length,
    issuesCreated: manifest.projects.flatMap(project => project.issues).filter(issue => issue.created).length,
    projects: manifest.projects.map(project => ({
        id: project.id,
        identifier: project.identifier,
        issues: project.issues.map(issue => ({ id: issue.id, subject: issue.subject })),
    })),
    manifestPath: outputPath,
}, null, 2));
