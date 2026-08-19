local function has_overtime_tag(item)
    for _, tag in ipairs(item.tags or {}) do
        if tag.name == "加班" then
            return true
        end
    end
    return false
end

function editor_main(context)
    local items
    if context.get_date_range() ~= nil then
        items = context.items.stream()
    elseif context.work_item ~= nil then
        local date = context.work_item.date
        items = diary.work_items.stream({ startDate = date, endDate = date, pageSize = 500 })
    else
        error("请从日期或工作项右键菜单执行此脚本。")
    end

    local rows = {}
    for item in items do
        if has_overtime_tag(item) then
            table.insert(rows, { item.date, item.comment or "", item.hours or 0 })
        end
    end
    if #rows == 0 then
        diary.ui.notify("导出加班明细", "当前范围没有加班工作项。")
        return
    end

    local directory = diary.ui.pick_directory({ title = "选择加班明细导出目录" })
    if directory == nil then
        return
    end

    local result = diary.exports.export({
        format_id = "xlsx",
        directory_selection_id = directory.selection_id,
        file_name = "加班明细.xlsx",
        format_options = { format_id = "xlsx", values = { sheet_name = "加班明细" } },
        content = {
            kind = "table",
            title = "加班明细",
            columns = {
                { name = "日期", type = "date" },
                { name = "工作内容", type = "text" },
                { name = "工时", type = "decimal", number_format = "0.00" },
            },
            rows = rows,
            aggregates = { { column_name = "工时", aggregation = "sum", label = "总工时" } },
            style = "report",
        },
    })

    if not result.succeeded then
        local export_error = result.error
        diary.ui.notify(
            "导出失败",
            export_error.code .. ": " .. export_error.message .. "\n可重试：" .. tostring(export_error.retryable))
        return
    end
    diary.ui.ask_to_open_exported_file(result.file_id)
end
