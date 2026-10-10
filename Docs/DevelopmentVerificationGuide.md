# 本地开发与验证避坑指南

本文记录在 DiaryApp 本地开发和验证中已经实际遇到、容易重复发生的问题。遇到构建或测试异常时，先检查这里，再判断是否属于代码缺陷。

## 1. 不要并行构建共享依赖图的项目

不要在同一个工作区同时启动多个 `dotnet build` 或 `dotnet test`，尤其不要并行构建 `Diary.App`、`Diary.AppTests` 和 `Diary.DbTests`。这些项目共享大量依赖项目及同一套 `bin/obj` 输出目录，MSBuild 进程可能同时写入 `.dll`、`.pdb` 或 `.deps.json`，出现：

- `The process cannot access the file ... because it is being used by another process`；
- `GenerateDepsFile` 写入失败；
- Avalonia `AVLN9999` PDB 文件占用。

这种错误首先按输出文件争用处理，不应直接判断为代码编译失败。停止并行任务后串行重试；不需要删除整个工作区，也不要使用破坏性 Git 命令。

推荐顺序：

```bash
dotnet build Diary.App/Diary.App.csproj -c Release --no-restore
dotnet test --project Diary.AppTests/Diary.AppTests.csproj -c Release --no-restore
dotnet test --project Diary.DbTests/Diary.DbTests.csproj -c Release --no-restore
```

## 2. .NET 10 测试项目参数

当前 SDK 的 Microsoft Testing Platform 命令行要求用 `--project` 或 `--solution` 指定目标。不要使用旧式位置参数：

```bash
# 错误
dotnet test Diary.AppTests/Diary.AppTests.csproj

# 正确
dotnet test --project Diary.AppTests/Diary.AppTests.csproj
dotnet test --solution DiaryApp.sln
```

定向测试示例：

```bash
dotnet test --project Diary.AppTests/Diary.AppTests.csproj \
  -c Release --no-restore \
  --filter FullyQualifiedName~DiaryEditorCalendarTests
```

## 3. Debug 与 Release 的条件依赖不同

`Diary.App` 的 CDP UI 自动化依赖只在 Debug 配置引用。NuGet restore 结果受 `Configuration` 条件影响；如果最近只执行过 Release restore，随后直接 `dotnet build` 默认构建 Debug，可能出现：

```text
Avalonia.Diagnostics.Cdp 命名空间不存在
```

这通常表示 Debug 条件包没有进入当前 `project.assets.json`，不代表业务代码或 XAML 有问题。需要 Debug/CDP 时先执行：

```bash
dotnet restore Diary.App/Diary.App.csproj -p:Configuration=Debug
dotnet build Diary.App/Diary.App.csproj -c Debug --no-restore
```

只验证正式应用路径时使用 Release：

```bash
dotnet restore Diary.App/Diary.App.csproj -p:Configuration=Release
dotnet build Diary.App/Diary.App.csproj -c Release --no-restore
```

不要通过删除 `#if DEBUG`、移除 CDP 引用或修改业务代码来掩盖配置不匹配。

## 4. 验证结果的报告口径

- 文件锁失败：说明是并行输出争用，并在串行重试后报告真实结果。
- Debug 条件依赖未还原：说明 Debug 构建未完成及原因；可以另外报告 Release 验证，但不能称“全部构建通过”。
- 缺少 Docker、PostgreSQL、Python、Quarto 或 UI 显示环境：明确列出未执行项，不把 Inconclusive 或跳过项计为通过。
- 修改 Avalonia XAML 时，至少完成一次 `Diary.App` Release 构建，以覆盖 XAML 编译和编译绑定检查。

## 5. CDP 操作 Avalonia ComboBox

CDP 中仅聚焦 `ComboBox` 后发送 `Home/End`，在 Avalonia 下可能只移动焦点或预选而不提交绑定值。需要验证设置持久化时，应按真实用户路径先点击展开下拉框，再点击目标 `ComboBoxItem`，或在展开状态发送 `Home/End` 后按 `Enter` 提交；最后从 `ComboBox` 的后代内容读取选中文本并执行“保存”。`ComboBox` 根节点自身的 `text` 属性可能为空，直接读取根节点会产生误报。
