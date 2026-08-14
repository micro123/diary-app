def automation_main(context):
    # context.automation["trigger"] 为 "Scheduled"（定时）或 "Startup"（启动补跑）。
    trigger = context.automation["trigger"]
    context.log.info(f"自动化触发：{trigger}")

    result = context.diary.workItems.query(limit=1, range="yesterday")
    if not result["succeeded"]:
        raise RuntimeError(result["error"]["message"])
    if result["items"]:
        context.log.info("昨日已有记录，跳过补录")
        return None

    yesterday = result["normalizedQuery"]["startDate"]
    append = context.diary.logItems.create({
        "date": yesterday,
        "hours": 0.5,
        "title": "昨日无记录自动补录",
        "note": "自动化脚本补录，请修改为实际工作内容。",
        "idempotencyKey": f"auto-daily-check:{yesterday}",
    })
    if not append["succeeded"]:
        raise RuntimeError(append["error"]["message"])

    context.diary.ui.notify(
        "自动化脚本",
        f"昨天（{yesterday}）没有工作记录，已自动补录一条，请核对并修改。",
    )
    return None
