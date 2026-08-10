# 解决方案项目架构

当前实现的插件、数据库扩展、Redmine 实例和启动迁移链路见 [`CurrentArchitecture.md`](CurrentArchitecture.md)。
本文保留为项目目录和主程序结构的概览；插件目标架构与分阶段改造计划见 [`TrackerPluginArchitecture.md`](TrackerPluginArchitecture.md)。

## 目录结构

- App：主程序和程序工具放这下面
  - Diary.App：主程序，程序主要逻辑都在这，可能需要优化/重构
  - Diary.MigrationTool：从老的 Diary Tool C++ 导入统计数据；不迁移 Tracker 信息，导入工作项持久化为只读
- Diary.Survey：调查功能的基础实现；每个用户可配置为调查者或受访者，调查者监听固定端口 9721 并展示调查页，受访者填写调查者 IP 后仅响应调查
- Core：核心数据结构定义
  - Diary.Core：定义主要数据结构和程序配置，也带有一些数据工具
  - Diary.Database：数据库的接口定义，所有数据库实现都是实现此接口
  - Diary.ScriptBase：版本化脚本契约、诊断和执行模型
  - Diary.ScriptHost：脚本查询、日志、Tracker 目录和系统交互宿主 API
  - Diary.Script.Runtime：脚本目录项、引擎注册、构建和执行运行时
- Plugin：插件稳定契约和插件 UI 扩展
  - Diary.PluginBase：插件 manifest、生命周期、实例和迁移契约
  - Diary.PluginUI：配置、管理页、编辑器和模板贡献契约
- Database：核心数据库 provider 和 Redmine 数据库扩展
  - Diary.Db.SQLite：SQLite 核心数据库实现
  - Diary.Db.PostgreSQL：PostgreSQL 核心数据库实现
  - Diary.RedMine.SQLite：SQLite Redmine 扩展
  - Diary.RedMine.PostgreSQL：PostgreSQL Redmine 扩展
- Integrations：整合的各种工时提交系统，后续需要优化为可选件，因为当前组件`RedMine`可能会被弃用
  - Diary.RedMine：`RedMine`整合，支持提交工时和创建问题
  - Diary.RedMine.UI：Redmine 设置、管理页和编辑器 UI 扩展
- Scripting：各种脚本支持的实现
  - Diary.Script.CSharp：`C#`脚本支持
  - Diary.Script.Lua：`Lua`脚本支持（受限独立 worker）
  - Diary.Script.Python：`Python 3`脚本支持（独立解释器 worker）
  - Diary.Script.Worker：C#/Lua Worker 适配器、协议入口和受限执行进程

脚本系统的目标架构、运行时边界、宿主 API、Tracker API 和分阶段实现计划见
[`ScriptSystemDesign.md`](ScriptSystemDesign.md)。当前已经完成 C#、Lua 和 Python 脚本目录扫描、构建、
脚本管理页、构建/执行抽象、独立 Worker 路由和 HostCall 宿主转发；Lua/Python 的跨平台运行时打包矩阵、
更细粒度资源限制和更完整的 Tracker 脚本 API 仍在后续计划中，详见
[`ScriptSystemDesign.md`](ScriptSystemDesign.md) 和 [`ScriptWorkerDesign.md`](ScriptWorkerDesign.md)。

标签与 Tracker 默认字段的关联规则设计见
[`TagAutomationDesign.md`](TagAutomationDesign.md)。规则属于 Tracker 实例配置，
标签添加和应用模板添加均按实际顺序触发，但用户后续可以覆盖默认字段。

自定义事项查询的模型、标签匹配语义、数据库实现和页面能力见
[`WorkItemQueryDesign.md`](WorkItemQueryDesign.md)。当前 SQLite/PostgreSQL、查询页面和统计复用已经落地。
- Test：各种单元测试放这里
- Tools：编程工具和一些代码工具
  - Diary.Utils：程序工具，包括时间、文件、和一些属性定义
  - Diary.VersionGenerator：给`Diary.Core`用的，用来生成一些`const`变量，主要是学习用


## 主程序代码实现

程序使用`Avalonia UI`作为`UI`库以实现跨平台（主要是为了支持`Linux`平台），
使用`MVVM`架构实现数据和界面分离。各个文件夹的作用如下：

- Assets：资源文件，包含字体、图标等
- Converters：`XAML`中用到的数值转换工具
- Dialogs：一些弹出式对话框放在这里，同时对应的`ViewModel`也在这
- Messages：程序中解耦使用的消息类型定义。
- Models：一些子页面的`ViewModel`,如统计、调查，也有一些通用的模型。
- Pages：主程序核心页面；Redmine 页面位于 `Diary.RedMine.UI`
- Resources：额外的资源和样式文件
- Scripts：构建用到的脚本
- Utils：一些程序内使用工具
- ViewModels：主要视图模型都包含在这里，即`ViewModel`层
- Views：主要视图都包含在这里，即`View`层
- 其他文件：包含程序入口，版本信息，程序集信息等代码。
