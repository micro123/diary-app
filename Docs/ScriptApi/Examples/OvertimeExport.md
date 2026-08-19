# 导出当前范围的加班明细

这组三语言 Editor 脚本从日期、周、月、季度、年份或工作项右键目标取得查询范围，筛选标签名精确等于“加班”的工作项，并导出带类型、数字格式、合计标签和 report 样式的 XLSX。

- C#：[OvertimeExport.cs](OvertimeExport.cs)
- Lua：[OvertimeExport.lua](OvertimeExport.lua)
- Python：[OvertimeExport.py](OvertimeExport.py)

运行流程为：查询工作项 → 选择目录 → 使用 `sheet_name` 导出 → 检查 `ExportResult` → 询问打开文件。取消目录选择或没有匹配数据时不会生成文件。

脚本必须从有人值守的 Editor 右键入口执行。目录令牌和文件 ID 绑定当前执行且有效期为 10 分钟，不能跨执行保存复用。完整错误和格式契约见 [../Export.md](../Export.md)。
