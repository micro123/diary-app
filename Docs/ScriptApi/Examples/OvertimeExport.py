"""把当前右键范围内带有“加班”标签的工作项导出为 XLSX。"""


def has_overtime_tag(item):
    return any(tag.get("name") == "加班" for tag in item.get("tags", []))


def editor_main(context):
    if context.getDateRange() is not None:
        items = context.items.stream()
    elif context.workItem is not None:
        date = context.workItem["date"]
        items = context.diary.workItems.stream(startDate=date, endDate=date, pageSize=500)
    else:
        raise RuntimeError("请从日期或工作项右键菜单执行此脚本。")

    rows = [
        [item["date"], item.get("comment") or "", item.get("hours", 0)]
        for item in items
        if has_overtime_tag(item)
    ]
    if not rows:
        context.diary.ui.notify("导出加班明细", "当前范围没有加班工作项。")
        return

    directory = context.diary.ui.pick_directory({"title": "选择加班明细导出目录"})
    if directory is None:
        return

    result = context.exports.export({
        "format_id": "xlsx",
        "directory_selection_id": directory["selection_id"],
        "file_name": "加班明细.xlsx",
        "format_options": {"format_id": "xlsx", "values": {"sheet_name": "加班明细"}},
        "content": {
            "kind": "table",
            "title": "加班明细",
            "columns": [
                {"name": "日期", "type": "date"},
                {"name": "工作内容", "type": "text"},
                {"name": "工时", "type": "decimal", "number_format": "0.00"},
            ],
            "rows": rows,
            "aggregates": [{"column_name": "工时", "aggregation": "sum", "label": "总工时"}],
            "style": "report",
        },
    })

    if not result["succeeded"]:
        export_error = result["error"]
        context.diary.ui.notify(
            "导出失败",
            export_error["code"] + ": " + export_error["message"] +
            "\n可重试：" + str(export_error["retryable"]),
        )
        return
    context.diary.ui.ask_to_open_exported_file(result["file_id"])
