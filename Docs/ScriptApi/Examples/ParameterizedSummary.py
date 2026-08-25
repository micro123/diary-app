def query_main(context):
    range_name = context.arguments["range"]
    minimum_hours = float(context.arguments["minimumHours"])
    include_zero = context.arguments["includeZero"] == "true"
    title_prefix = context.arguments["titlePrefix"]
    count = 0
    total_hours = 0.0

    for item in context.diary.work_items.stream(pageSize=500, range=range_name):
        hours = item.get("hours", 0.0)
        if hours < minimum_hours or (not include_zero and hours == 0):
            continue
        count += 1
        total_hours += hours

    context.log.info(
        f"{title_prefix}：范围 {range_name}；事项 {count}；工时 {total_hours:.2f}")
    return None
