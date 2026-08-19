-- Diary Script Worker Lua 引导脚本
-- 由 LuaWorker.CreateLua 以嵌入资源方式加载，在 C# 侧注册完 __diary_* 函数与上下文全局后执行。
-- 职责：沙箱限制、宿主 API 门面、分页流式查询、执行上下文装配、print 重定向。

-- 1. 沙箱：禁用库与动态加载能力
io = nil
os = nil
debug = nil
package = nil
require = nil
dofile = nil
loadfile = nil
load = nil
loadstring = nil
import = nil
luanet = nil
clr = nil

-- 2. 宿主 API 门面（底层 __diary_* 函数由 LuaWorker 以 C# 注册）
diary = {
    workItems = {
        query = function(params) return __diary_work_items_query(params) end,
    },
    logItems = {
        create = function(params) return __diary_log_items_create(params) end,
    },
    templateLogItems = {
        create = function(params) return __diary_template_log_items_create(params) end,
    },
    templates = {
        list = function() return __diary_templates_list() end,
    },
    host = {
        list = function() return __diary_host_capabilities_list() end,
    },
    trackerInstances = {
        get = function(params) return __diary_tracker_get(params) end,
        list = function() return __diary_tracker_list() end,
    },
    clipboard = {
        get = function() return __diary_clipboard_get() end,
        set = function(text) return __diary_clipboard_set(text) end,
    },
    ui = {
        notify = function(title, body) return __diary_ui_notify(title, body) end,
        confirm = function(title, body) return __diary_ui_confirm(title, body) end,
        select_option = function(request) return __diary_ui_options_select(request) end,
        pick_directory = function(options) return __diary_ui_directory_pick(options or {}) end,
        ask_to_open_exported_file = function(file_id) return __diary_ui_exported_file_open({ file_id = file_id }) end,
    },
    exports = {
        export = function(request) return __diary_exports_export(request) end,
        list_formats = function() return __diary_exports_formats_list() end,
    },
    log = {
        debug = function(message) return __diary_log_write('Debug', message) end,
        info = function(message) return __diary_log_write('Info', message) end,
        warning = function(message) return __diary_log_write('Warning', message) end,
        error = function(message) return __diary_log_write('Error', message) end,
    },
}

-- print 重定向到脚本日志 Info 级（与 C# Console.WriteLine、Python print 语义一致）；
-- 转发是尽力而为：log.write 未配置/失败时不因此让脚本失败。
print = function(...)
    local parts = {}
    for i = 1, select('#', ...) do
        parts[#parts + 1] = tostring(select(i, ...))
    end
    pcall(__diary_log_write, 'Info', table.concat(parts, '\t'))
end

-- 3. 分页流式查询（workItems.stream / items.stream）
diary.workItems.stream = function(params)
    params = params or {}
    local pageSize = params.pageSize or 500
    if pageSize < 1 or pageSize > 500 then
        error('pageSize must be between 1 and 500')
    end
    local offset = params.offset or 0
    local page = {}
    local index = 1
    local finished = false
    params.pageSize = nil
    return function()
        while true do
            if index <= #page then
                local item = page[index]
                index = index + 1
                return item
            end
            if finished then
                return nil
            end
            params.limit = pageSize
            params.offset = offset
            local result = __diary_work_items_query(params)
            if not result.succeeded then
                error(result.error.message)
            end
            page = result.items or {}
            index = 1
            offset = offset + #page
            finished = #page < pageSize
        end
    end
end

-- 4. 执行上下文装配
__diary_context = {}
__diary_context.target = __diary_context_target
__diary_context.dateRange = __diary_context_date_range
__diary_context.workItem = __diary_context_work_item
__diary_context.arguments = __diary_context_arguments or {}
__diary_context.log = diary.log
__diary_context.progress = {
    report = function(fraction, message) return __diary_progress_report(fraction, message) end,
}
__diary_context.isCancelled = function() return __diary_is_cancelled() end
__diary_context.getDateRange = function() return __diary_context_date_range end
__diary_context.items = {
    stream = function(params)
        local range = __diary_context_date_range
        if range == nil then
            error('当前目标没有日期范围')
        end
        params = params or {}
        params.startDate = range.startDate
        params.endDate = range.endDate
        return diary.workItems.stream(params)
    end,
}

__diary_context.entryKind = __diary_context_entry_kind
__diary_context.idempotencyKey = __diary_context_idempotency_key
__diary_context.preview = __diary_context_preview
__diary_context.automation = {
    trigger = __diary_context_automation_trigger,
    eventData = __diary_context_automation_event_data or {},
    idempotencyKey = __diary_context_automation_idempotency_key,
}
