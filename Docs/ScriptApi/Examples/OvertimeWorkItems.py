"""列出指定日期范围内带有“加班”标签的工作项。"""

TAG_NAME = "加班"


def primary_tag_name(item):
    for tag in item.get("tags", []):
        if tag.get("isPrimary", False):
            return tag.get("name") or "无"
    return "无"


def editor_main(context):
    date_range = context.get_date_range()
    if date_range is not None:
        items = context.items.stream()
    elif context.work_item is not None:
        work_item_date = context.work_item["date"]
        items = context.diary.work_items.stream(
            startDate=work_item_date,
            endDate=work_item_date,
            pageSize=500,
        )
    else:
        raise RuntimeError("请从日期或工作项右键菜单执行此脚本。")

    matched_items = []
    for item in items:
        if any(tag.get("name") == TAG_NAME for tag in item.get("tags", [])):
            matched_items.append(item)

    if not matched_items:
        message = "无"
    else:
        lines = [
            "日期 | 标题 | 主标签 | 工时",
            *[
                f"{item['date']} | "
                f"{item.get('comment') or '（无标题）'} | "
                f"{primary_tag_name(item)} | "
                f"{item.get('hours', 0)} 小时"
                for item in matched_items
            ],
        ]
        message = "\n".join(lines)

    context.diary.ui.notify("加班工作项", message)
