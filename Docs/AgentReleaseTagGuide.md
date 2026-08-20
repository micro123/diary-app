# Agent 发布新 Tag 操作指南

最后更新：2026-08-17

本文面向自动化 Agent，说明如何在 DiaryApp 仓库中安全地准备、创建并推送发布 Tag。目标是让 Tag、应用显示版本、CHANGELOG、GitHub Actions 和 GitHub Release 保持一致，并避免误发、移动已有 Tag 或发布未验证提交。

## 1. 适用范围与权限边界

本指南适用于：

- 准备正式版本 Tag，例如 `v1.0.0-r438`；
- 准备内部验证 Tag，例如 `v1.0.0-alpha2`；
- 推送 Tag 并跟踪 `.github/workflows/release-on-tags.yml`；
- 验收 GitHub Release 和 Windows/Linux 发布包。

Agent 必须遵守以下边界：

1. **“准备发布”不等于“立即推送 Tag”**。只有用户明确要求创建或发布 Tag 时，才能执行 `git tag` 和 `git push origin <tag>`。
2. 推送 `v*` Tag 会触发具有 `contents: write` 权限的发布工作流，属于外部可见操作；执行前必须再次确认目标 Tag 和目标提交。
3. 不得在工作区有未提交修改时创建发布 Tag。
4. 不得静默移动、覆盖或复用已经存在的本地或远程 Tag。
5. 不得使用 `git push --tags`，避免把本地的测试 Tag、历史 Tag 或 `manual-*` Tag 一并推送。
6. 删除远程 Tag、删除 Release、强制更新 Tag 等破坏性操作，必须取得用户明确授权。

## 2. 当前发布机制

### 2.1 触发条件

`.github/workflows/release-on-tags.yml` 在以下条件触发：

```yaml
on:
  push:
    tags:
      - 'v*'
```

因此：

- `v1.0.0-r438` 会触发正式 Tag 工作流；
- `v1.0.0-alpha2` 会触发正式 Tag 工作流；
- `manual-*` 不由该工作流处理，它属于手动构建工作流的临时 Tag 命名空间。

### 2.2 工作流阶段

发布工作流按顺序执行：

1. **Verify**：Ubuntu 和 Windows 分别执行 Release 构建与全量测试；
2. **Publish**：生成 `win-x64` 与 `linux-x64` 自包含发布目录；
3. **Package**：检查应用、脚本 Worker、DiagnosticsClient 依赖和 Jira/RedMine 插件程序集是否齐全；
4. **Create Release**：下载两个平台的压缩包，从 `Docs/CHANGELOG.md` 提取发布说明并创建 GitHub Release。

任一 Verify 或 Publish 阶段失败，`create-release` 都不会执行。

### 2.3 发布产物

成功后应存在两个附件：

```text
DiaryAppNG-<TAG>-win-x64.zip
DiaryAppNG-<TAG>-linux-x64.zip
```

例如：

```text
DiaryAppNG-v1.0.0-r438-win-x64.zip
DiaryAppNG-v1.0.0-r438-linux-x64.zip
```

## 3. 版本号与 Tag 规则

### 3.1 应用显示版本

应用版本由以下两部分组成：

```text
<DataVersion.VersionString>-r<Git CommitCount>
```

当前数据版本定义在：

```text
Diary.Core/DataVersion.cs
```

构建脚本通过以下命令计算提交计数：

```bash
git rev-list --count HEAD
```

因此，正式发布 Tag 中的 `rN` 必须与 **Tag 指向提交的提交总数**一致。

### 3.2 正式发布 Tag

格式：

```text
v<DataVersion>-r<CommitCount>
```

例如 Tag 指向提交的计数为 `438`：

```text
v1.0.0-r438
```

对应 CHANGELOG 标题必须去掉开头的 `v`：

```markdown
## 1.0.0-r438 (正式版)
```

### 3.3 内部验证 Tag

内部验证版本可以不带 `rN`：

```text
v1.0.0-alpha2
v1.0.0-beta1
v1.0.0-rc1
```

对应 CHANGELOG 标题示例：

```markdown
## 1.0.0-alpha2 (内部验证版)
```

不要使用 `manual-*` 作为人工发布 Tag；该前缀由 `.github/workflows/manual-build.yml` 管理。

### 3.4 当前 prerelease 判定注意事项

当前工作流使用：

```yaml
prerelease: ${{ contains(github.ref_name, '-') }}
```

这意味着只要 Tag 中包含连字符，就会被 GitHub 标记为 prerelease。按当前命名规则：

- `v1.0.0-alpha2` 会标记为 prerelease；
- **`v1.0.0-r438` 同样会标记为 prerelease**。

如果用户要求创建 GitHub 的稳定正式 Release，Agent 必须先指出这一现状，并询问是否先修改工作流的 prerelease 判定；不得假设 `v1.0.0-rN` 会自动成为稳定 Release。

## 4. 发布前置条件

创建 Tag 前必须全部满足：

- 当前分支为 `main`；
- 工作区和暂存区为空；
- `HEAD` 是用户确认要发布的提交；
- 本地 `main` 不落后于 `origin/main`；
- 目标提交已推送到 `origin/main`；
- CHANGELOG 存在与目标 Tag 精确匹配的章节；
- 最新 CI 已通过，或已按等价命令完成本地验证；
- 目标 Tag 在本地和远程均不存在；
- 用户已明确授权推送该 Tag。

建议先执行只读检查：

```bash
git status --short
git branch --show-current
git remote get-url origin
git fetch origin --tags --prune
git rev-list --left-right --count origin/main...HEAD
git log -1 --decorate --oneline
git rev-list --count HEAD
```

预期：

- `git status --short` 没有输出；
- 当前分支为 `main`；
- `origin/main...HEAD` 的左侧计数为 `0`；
- 如果右侧计数不为 `0`，必须先推送提交，再创建和推送 Tag。

若本地分支落后、出现分叉或工作区包含用户尚未处理的修改，Agent 应停止发布流程并报告状态，不得自行执行破坏性 reset、强制推送或未经授权的 rebase。

## 5. 准备 CHANGELOG

### 5.1 标题格式

发布工作流按以下模式查找章节：

```text
## <Tag 去掉 v 后的版本号> <其他说明>
```

标题在版本号后必须保留一个空格和说明文字。例如：

```markdown
## 1.0.0-r438 (正式版)

日期：2026-08-17
```

不要只写：

```markdown
## 1.0.0-r438
```

因为当前提取脚本要求版本号后还有空格。

### 5.2 提交计数尚未确定时

发布准备提交本身会增加一次提交计数。推荐流程：

1. 先完成代码、测试和文档修改；
2. 提交发布准备提交；
3. 执行 `git rev-list --count HEAD` 得到最终 `N`；
4. 检查 CHANGELOG 标题是否为 `1.0.0-rN`；
5. 如果不一致，修改 CHANGELOG 后使用 `git commit --amend`；
6. amend 不会增加提交数量，再次确认计数和标题一致。

发布准备提交示例：

```bash
git commit -m "docs: 准备 v1.0.0-r438 发布说明" \
  -m "整理本次版本变更、已知限制和验证结果。"
```

提交消息必须遵循仓库 `AGENTS.md`：使用 Conventional Commits 前缀、中文正文和真实段落，不使用字面量 `\n`。

### 5.3 本地模拟 Release Notes 提取

设置目标 Tag 后，可以在本地运行与工作流等价的提取检查：

```bash
TAG="v1.0.0-r438"
VERSION="${TAG#v}"
BASE_VERSION=$(printf '%s\n' "$VERSION" | sed -E 's/-[a-zA-Z][a-zA-Z0-9.]*$//')
RELEASE_BODY=$(mktemp)

for candidate in "$VERSION" "$BASE_VERSION"; do
  awk -v version="$candidate" '
    $0 ~ "^## " version " " { found=1; print; next }
    found && $0 ~ /^## / { exit }
    found { print }
  ' Docs/CHANGELOG.md > "$RELEASE_BODY"
  [ -s "$RELEASE_BODY" ] && break
done

cat "$RELEASE_BODY"
test -s "$RELEASE_BODY"
rm -f "$RELEASE_BODY"
```

必须确认输出的是目标版本章节，而不是旧版本章节或自动回退文案。

## 6. 发布前验证

### 6.1 基础检查

```bash
git diff --check
```

如果修改了 C# 文件，在提交前按 `AGENTS.md` 对本次修改文件执行最小范围的 `dotnet format`；发布前还应运行 CI 等价的格式门禁：

```bash
dotnet restore DiaryApp.sln
dotnet format DiaryApp.sln --no-restore --verify-no-changes
```

### 6.2 Release 构建与测试

工作流使用：

```bash
dotnet build DiaryApp.sln --configuration Release --no-restore
dotnet test DiaryApp.sln \
  --configuration Release \
  --no-build \
  --no-restore \
  --verbosity minimal
```

Linux CI 还设置：

```bash
DIARY_REQUIRE_POSTGRES_TESTS=1
DIARY_REQUIRE_PYTHON_TESTS=1
```

本地缺少 PostgreSQL/Docker 或 Python 3.10 时，不得把跳过测试描述为“全部验证通过”。此时至少应确认目标提交在 `main` 的 GitHub Actions Windows/Ubuntu 矩阵已经成功。

### 6.3 在 Linux 本地生成带 Python 的 Windows 包

当前开发机可使用仓库脚本交叉发布 `win-x64` 自包含应用，并附带与 tag 发布工作流一致的 Python 3.13.15 embeddable runtime：

```bash
./Tools/package-win-x64-with-python.sh
```

默认产物写入 `artifacts/packages/`，包名包含 Git 提交数、短哈希和 `local` 标识；工作区不干净时还会增加 `dirty` 标识。也可以传入只包含字母、数字、点、下划线和连字符的版本标签：

```bash
./Tools/package-win-x64-with-python.sh v1.0.0-test1
```

脚本会执行以下检查：

1. 还原并交叉发布 `win-x64` 自包含应用；
2. 检查应用、脚本 Worker、DiagnosticsClient 和 Tracker 插件程序集；
3. 从 python.org 下载固定版本的 embeddable ZIP 并校验 SHA-256；本地脚本会复用 `artifacts/cache/python/` 中的有效缓存，Tag CI 会复用 GitHub Actions 缓存；
4. 将运行时解压到发布目录的 `python/` 子目录；
5. 确认目标 RID 的 NNG 托管与原生运行文件已位于发布根目录，保留 `runtimes/win-x64/` 和 RID 无关的 `runtimes/any/`，只移除其他平台的运行时目录；
6. 检查最终 ZIP 完整性、`python/python.exe` 等关键条目，要求目标 RID 与 `any` 运行时目录存在，并拒绝其他 RID 条目。

Linux 主机不能直接执行 Windows 的 `python.exe` 或桌面应用，因此该脚本只完成内容与压缩包校验。交付前仍应在 Windows x64 环境启动应用并执行至少一个 Python 脚本 Smoke Test。

如果局域网已经部署 FileCodeBox，也可以在打包完成后自动上传并输出取件码：

```bash
./Tools/package-win-x64-with-python.sh --upload-filecodebox v1.0.0-test1
```

该选项上传到 `http://192.168.1.40:12345/`，有效期固定为 3 小时；不指定该选项时不会访问 FileCodeBox。上传失败不会删除已经生成的本地 ZIP，脚本会以失败状态退出并打印本地文件路径。

### 6.4 工作流 YAML 修改检查

如果本次发布同时修改了 `.github/workflows/*.yml`，按仓库规则执行：

```bash
python3 - <<'PY'
from pathlib import Path
import yaml

for path in sorted(Path('.github/workflows').glob('*.yml')):
    with path.open(encoding='utf-8') as stream:
        yaml.safe_load(stream)
    print(f'YAML OK: {path}')
PY

git diff --check
```

当前环境不使用 `actionlint`。

## 7. 创建 Tag

### 7.1 计算并核对正式版本

```bash
DATA_VERSION="1.0.0" # 必须与 Diary.Core/DataVersion.cs 一致
COMMIT_COUNT=$(git rev-list --count HEAD)
TAG="v${DATA_VERSION}-r${COMMIT_COUNT}"
printf 'Target tag: %s\n' "$TAG"
```

再次核对 CHANGELOG：

```bash
grep -F "## ${TAG#v} " Docs/CHANGELOG.md
```

### 7.2 检查 Tag 是否已存在

```bash
if git show-ref --tags --verify --quiet "refs/tags/$TAG"; then
  echo "Local tag already exists: $TAG" >&2
  exit 1
fi

if git ls-remote --exit-code --tags origin "refs/tags/$TAG" >/dev/null 2>&1; then
  echo "Remote tag already exists: $TAG" >&2
  exit 1
fi
```

### 7.3 创建 annotated Tag

推荐创建 annotated Tag：

```bash
git tag -a "$TAG" -m "发布 $TAG"
```

确认 Tag 指向当前 `HEAD`：

```bash
test "$(git rev-parse "$TAG^{commit}")" = "$(git rev-parse HEAD)"
git show --stat --oneline "$TAG"
```

如果此时发现错误且 Tag 尚未推送，可以删除本地 Tag 后重新准备：

```bash
git tag -d "$TAG"
```

## 8. 推送发布

确保提交先存在于远程 `main`：

```bash
git push origin main
```

然后只推送目标 Tag：

```bash
git push origin "$TAG"
```

禁止使用：

```bash
git push --tags
```

Tag 推送成功后，Agent 应报告“Tag 已推送，发布工作流已触发”，不能在工作流完成前声称 Release 已发布成功。

## 9. 跟踪 GitHub Actions

如果环境已安装并登录 GitHub CLI：

```bash
gh run list --workflow release-on-tags.yml --limit 10
```

找到对应 Tag 的 run ID 后：

```bash
gh run watch <RUN_ID> --exit-status
```

也可以查看失败日志：

```bash
gh run view <RUN_ID> --log-failed
```

发布完成后检查 Release：

```bash
gh release view "$TAG" \
  --json tagName,name,isDraft,isPrerelease,publishedAt,url,assets
```

验收条件：

- Tag 名和 Release 名均为目标 `$TAG`；
- Release 不是 draft；
- prerelease 状态符合当前工作流实际规则；
- 发布说明来自目标 CHANGELOG 章节；
- 同时存在 `win-x64` 和 `linux-x64` ZIP；
- 两个附件文件大小均大于 0。

## 10. 失败处理

### 10.1 工作流失败但尚未创建 Release

推荐做法：

1. 保留失败 Tag 作为不可变历史；
2. 在 `main` 上提交修复；
3. 重新运行完整验证；
4. 使用新提交计数创建新 Tag，例如从 `v1.0.0-r438` 前进到 `v1.0.0-r439`。

不要把旧 Tag 强制移动到新提交。

### 10.2 Tag 推错提交

Agent 必须停止并报告：

- 错误 Tag；
- 当前指向的提交；
- 正确目标提交；
- Release 是否已经创建。

只有用户明确授权后，才能删除远程 Tag 或 Release。可能使用的命令包括：

```bash
gh release delete "$TAG" --yes

git push origin ":refs/tags/$TAG"
git tag -d "$TAG"
```

这些命令具有破坏性，不得作为自动恢复步骤执行。

### 10.3 Release Notes 未匹配

如果 Release 已创建但 body 使用了自动回退文案，不要静默重打同名 Tag。先修复 CHANGELOG 或提取规则，再由用户决定：

- 手工更新当前 Release body；或
- 创建新的修订 Tag。

## 11. Agent 最终交付模板

### 11.1 仅准备、未推送

```text
已完成 <TAG> 发布准备：
- Tag 目标提交：<SHA>
- 提交计数：<N>
- CHANGELOG 章节：已匹配
- 本地验证：<结果>
- Tag：尚未创建/尚未推送

等待你确认后再推送发布 Tag。
```

### 11.2 已推送、CI 运行中

```text
已推送 <TAG>，GitHub Actions 发布工作流已触发。
当前状态：验证/构建/创建 Release 进行中。
在工作流完成前不视为发布成功。
```

### 11.3 发布成功

```text
<TAG> 已发布：
- Windows: DiaryAppNG-<TAG>-win-x64.zip
- Linux: DiaryAppNG-<TAG>-linux-x64.zip
- Verify: Windows/Ubuntu 均通过
- Release Notes: 已从 Docs/CHANGELOG.md 匹配
- Release: <URL>
```

## 12. 最终检查清单

发布前：

- [ ] 用户明确授权发布目标 Tag；
- [ ] 当前分支为 `main`；
- [ ] 工作区干净；
- [ ] `main` 不落后于 `origin/main`；
- [ ] 目标提交已推送；
- [ ] `DataVersion`、提交计数、Tag 和 CHANGELOG 一致；
- [ ] CHANGELOG 提取结果正确；
- [ ] 格式、构建和测试门禁已通过；
- [ ] 本地和远程不存在同名 Tag；
- [ ] 已告知用户当前 prerelease 判定行为。

发布后：

- [ ] Tag 工作流成功；
- [ ] GitHub Release 已创建且非 draft；
- [ ] Release body 为目标版本章节；
- [ ] Windows/Linux 两个附件均存在；
- [ ] 最终回复明确区分“Tag 已推送”和“Release 已成功”。
