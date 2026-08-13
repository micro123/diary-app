local TAG_NAME = "加班"

local function has_overtime_tag(item)
    for _, tag in ipairs(item.tags or {}) do
        if tag.name == TAG_NAME then
            return true
        end
    end
    return false
end

local function primary_tag_name(item)
    for _, tag in ipairs(item.tags or {}) do
        if tag.isPrimary then
            return tag.name
        end
    end
    return "无"
end

function editor_main(context)
    local items
    local date_range = context.getDateRange()
    if date_range ~= nil then
        items = context.items.stream()
    elseif context.workItem ~= nil then
        local work_item_date = context.workItem.date
        items = diary.workItems.stream({
            startDate = work_item_date,
            endDate = work_item_date,
            pageSize = 500
        })
    else
        error("请从日期或工作项右键菜单执行此脚本。")
    end

    local lines = {}
    for item in items do
        if has_overtime_tag(item) then
            table.insert(lines, string.format(
                "%s | %s | %s | %s 小时",
                item.date,
                item.comment or "（无标题）",
                primary_tag_name(item),
                item.hours or 0
            ))
        end
    end

    local message
    if #lines == 0 then
        message = "无"
    else
        table.insert(lines, 1, "日期 | 标题 | 主标签 | 工时")
        message = table.concat(lines, "\n")
    end

    diary.ui.notify("加班工作项", message)
end
