function query_main(context)
    local range = context.arguments.range
    local minimumHours = tonumber(context.arguments.minimumHours)
    local includeZero = context.arguments.includeZero == "true"
    local titlePrefix = context.arguments.titlePrefix
    local count = 0
    local totalHours = 0

    for item in diary.work_items.stream({ range = range, pageSize = 500 }) do
        if item.hours >= minimumHours and (includeZero or item.hours ~= 0) then
            count = count + 1
            totalHours = totalHours + item.hours
        end
    end

    diary.log.info(string.format(
        "%s：范围 %s；事项 %d；工时 %.2f",
        titlePrefix,
        range,
        count,
        totalHours))
end
