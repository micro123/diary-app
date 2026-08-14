def query_main(context):
    totals = {}
    for item in context.diary.workItems.stream(pageSize=500, range="thisMonth"):
        tag_name = "无"
        for tag in item.get("tags", []):
            if tag.get("isPrimary", False):
                tag_name = tag.get("name") or "无"
                break
        totals[tag_name] = totals.get(tag_name, 0.0) + item.get("hours", 0.0)

    lines = ["主标签 | 工时"]
    for name, hours in sorted(totals.items(), key=lambda pair: pair[1], reverse=True):
        lines.append(f"{name} | {hours:.2f} 小时")

    context.log.info("\n".join(lines))
    return None
