function query_main(context)
    local totals = {}
    for item in diary.work_items.stream({ range = "thisMonth", pageSize = 500 }) do
        local tagName = "无"
        for _, tag in ipairs(item.tags) do
            if tag.isPrimary then
                tagName = tag.name
                break
            end
        end
        totals[tagName] = (totals[tagName] or 0) + item.hours
    end

    local names = {}
    for name in pairs(totals) do
        table.insert(names, name)
    end
    table.sort(names, function(a, b) return totals[a] > totals[b] end)

    local lines = { "主标签 | 工时" }
    for _, name in ipairs(names) do
        table.insert(lines, string.format("%s | %.2f 小时", name, totals[name]))
    end

    diary.log.info(table.concat(lines, "\n"))
end
