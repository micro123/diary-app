# 解决方案项目架构

## 目录结构

- App：主程序和程序工具放这下面
  - Diary.App：主程序，程序主要逻辑都在这，可能需要优化/重构
  - Diary.MigrationTool：数据库迁移工具，从老的`Diary Tool C++`迁移
  - Diary.Survey：调查功能的基础实现
- Core：核心数据结构定义
  - Diary.Core：定义主要数据结构和程序配置，也带有一些数据工具
  - Diary.Database：数据库的接口定义，所有数据库实现都是实现此接口
  - Diary.ScriptBase：脚本接口定义，已经脚本管理器实现
- Integrations：整合的各种工时提交系统，后续需要优化为可选件，因为当前组件`RedMine`可能会被弃用
  - Diary.RedMine：`RedMine`整合，支持提交工时和创建问题
- Scripting：各种脚本支持的实现
  - Diary.Script.CSharp：`C#`脚本支持
  - Diary.Script.Lua：`Lua`脚本支持
  - Diary.Script.Python：`Python`脚本支持，只支持`python3`
- Test：各种单元测试放这里
- Tools：编程工具和一些代码工具
  - Diary.Utils：程序工具，包括时间、文件、和一些属性定义
  - Diary.VersionGenerator：给`Diary.Core`用的，用来生成一些`const`变量，主要是学习用


## 主程序代码实现

程序使用`Avalonia UI`作为`UI`库以实现跨平台（主要是为了支持`Linux`平台），
使用`MVVM`架构实现数据和界面分离。各个文件夹的作用如下：

- Assets：
- Converters：
- Dialogs：
- Messages：程序中解耦使用的消息类型定义。
- Models：一些子页面的`ViewModel`,如统计、调查，也有一些通用的模型。
- Pages：`RedMine`的一些页面在这里，`ViewModel`也在这里
- Resources：额外的资源和样式文件
- Scripts：构建用到的脚本
- Utils：一些程序内使用工具
- ViewModels：主要视图模型都包含在这里，即`ViewModel`层
- Views：主要视图都包含在这里，即`View`层
- 其他文件：包含程序入口，版本信息，程序集信息等代码。
