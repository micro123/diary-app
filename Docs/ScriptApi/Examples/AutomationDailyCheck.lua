function automation_main(context)
    -- context.automation.trigger 为 "Scheduled"（定时）或 "Startup"（启动补跑）。
    local trigger = context.automation.trigger
    diary.log.info("自动化触发：" .. trigger)

    local result = diary.workItems.query({ range = "yesterday", limit = 1 })
    if not result.succeeded then
        error(result.error.message)
    end
    if #result.items > 0 then
        diary.log.info("昨日已有记录，跳过补录")
        return
    end

    local yesterday = result.normalizedQuery.startDate
    local append = diary.logItems.create({
        date = yesterday,
        hours = 0.5,
        title = "昨日无记录自动补录",
        note = "自动化脚本补录，请修改为实际工作内容。",
        idempotencyKey = "auto-daily-check:" .. yesterday,
    })
    if not append.succeeded then
        error(append.error.message)
    end

    diary.ui.notify("自动化脚本", "昨天（" .. yesterday .. "）没有工作记录，已自动补录一条，请核对并修改。")
    return append
end
