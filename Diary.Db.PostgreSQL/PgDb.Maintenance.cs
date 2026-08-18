using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using Diary.Database;
using Npgsql;

namespace Diary.Db.PostgreSQL;

public sealed partial class PgDb : IDbMaintenanceProvider, IDbPostRestoreValidator
{
    private static readonly TimeSpan ToolVersionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArchiveValidationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BackupRestoreTimeout = TimeSpan.FromHours(1);
    private static readonly string[] RedMineTables =
    [
        "redmine_projects",
        "redmine_activities",
        "redmine_issues",
        "redmine_time_entries",
    ];
    private static readonly string[] JiraTables =
    [
        "jira_projects",
        "jira_issues",
        "jira_work_entries",
    ];
    private static readonly string[] KnownDiaryTables =
        CoreSchemaContract.Current.Tables
            .Select(table => table.Name)
            .Concat(["plugin_data_versions"])
            .Concat(RedMineTables)
            .Concat(JiraTables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public DbMaintenanceSupport GetMaintenanceSupport()
    {
        var tools = GetToolAvailability();
        return tools.Supported
            ? new DbMaintenanceSupport(
                DbMaintenanceCapabilities.Backup | DbMaintenanceCapabilities.Restore)
            : new DbMaintenanceSupport(DbMaintenanceCapabilities.None, tools.UnavailableReason);
    }

    public DbBackupResult CreateBackup(string destinationPath)
    {
        var tools = GetToolAvailability();
        if (!tools.Supported)
            return new DbBackupResult(false, null, tools.UnavailableReason);
        if (string.IsNullOrWhiteSpace(destinationPath))
            return new DbBackupResult(false, null, "未指定备份文件路径。");

        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.Database))
            return new DbBackupResult(false, null, "PostgreSQL 数据库名称未配置。");

        var finalPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrWhiteSpace(directory))
            return new DbBackupResult(false, null, "无法确定备份文件所在目录。");

        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var version = PgToolProcess.Run(
                tools.PgDumpPath!,
                ["--version"],
                config,
                ToolVersionTimeout);
            if (!version.Success)
                return new DbBackupResult(false, null, $"pg_dump 工具不可用：{version.ErrorMessage}");
            if (!TryGetToolMajorVersion(version, out var toolMajor))
                return new DbBackupResult(false, null, "无法识别 pg_dump 的版本。");

            var serverMajor = GetServerMajorVersion(config, config.Database);
            if (serverMajor != toolMajor)
            {
                return new DbBackupResult(
                    false,
                    null,
                    $"pg_dump 主版本 {toolMajor} 与 PostgreSQL 服务端主版本 {serverMajor} 不匹配。");
            }

            var arguments = new List<string>
            {
                "--format", "custom",
                "--file", temporaryPath,
                "--no-password",
            };
            AddConnectionArguments(arguments, config);
            var result = PgToolProcess.Run(
                tools.PgDumpPath!,
                arguments,
                config,
                BackupRestoreTimeout);
            if (!result.Success)
                return new DbBackupResult(false, null, $"PostgreSQL 备份创建失败：{result.ErrorMessage}");

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                return new DbBackupResult(false, null, "pg_dump 未生成有效备份文件。");

            File.Move(temporaryPath, finalPath, true);
            return new DbBackupResult(true, finalPath, null);
        }
        catch (Exception exception)
        {
            return new DbBackupResult(false, null, $"PostgreSQL 备份创建失败：{exception.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public DbBackupValidationResult ValidateBackup(string backupPath, uint expectedVersion)
    {
        _ = expectedVersion;
        var tools = GetToolAvailability();
        if (!tools.Supported)
            return InvalidBackup(tools.UnavailableReason);
        if (string.IsNullOrWhiteSpace(backupPath))
            return InvalidBackup("未指定备份文件路径。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(backupPath);
        }
        catch (Exception exception)
        {
            return InvalidBackup($"备份文件路径无效：{exception.Message}");
        }

        if (!File.Exists(fullPath))
            return InvalidBackup("备份文件不存在。");
        if (new FileInfo(fullPath).Length == 0)
            return InvalidBackup("备份文件为空。");

        try
        {
            var config = GetConfig();
            var version = PgToolProcess.Run(
                tools.PgRestorePath!,
                ["--version"],
                config,
                ToolVersionTimeout);
            if (!version.Success)
                return InvalidBackup($"pg_restore 工具不可用：{version.ErrorMessage}");

            var result = PgToolProcess.Run(
                tools.PgRestorePath!,
                ["--list", "--no-password", fullPath],
                config,
                ArchiveValidationTimeout);
            if (!result.Success)
                return InvalidBackup($"PostgreSQL 备份文件校验失败：{result.ErrorMessage}");
            if (!result.StandardOutput.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
                return InvalidBackup("PostgreSQL 备份文件中没有可还原的表定义。");

            // custom archive 的数据版本只能在目标库中通过应用的兼容性检查确认，
            // 这里不为校验而创建临时数据库，也不探查归档之外的权限信息。
            return new DbBackupValidationResult(
                true,
                "PostgreSQL",
                0,
                null,
                null);
        }
        catch (Exception exception)
        {
            return InvalidBackup($"PostgreSQL 备份校验失败：{exception.Message}");
        }
    }

    public DbRestoreResult RestoreBackup(string backupPath, uint expectedVersion)
    {
        var tools = GetToolAvailability();
        if (!tools.Supported)
            return FailedRestore(tools.UnavailableReason);
        if (_connection is not null)
            return FailedRestore("执行 PostgreSQL 还原前必须关闭当前数据库连接。");

        var validation = ValidateBackup(backupPath, expectedVersion);
        if (!validation.Success)
            return FailedRestore(validation.Error);

        var config = GetConfig();
        var sourceDatabase = config.Database.Trim();
        if (string.IsNullOrWhiteSpace(sourceDatabase))
            return FailedRestore("PostgreSQL 当前数据库名称未配置。");

        var requestedTarget = config.RestoreTargetDatabase.Trim();
        if (string.Equals(sourceDatabase, requestedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return FailedRestore(
                "PostgreSQL 还原目标不能与当前数据库相同，否则可能覆盖当前数据。");
        }

        string targetDatabase;
        if (string.IsNullOrWhiteSpace(requestedTarget))
        {
            targetDatabase = CreateAutomaticTargetDatabaseName();
        }
        else
        {
            if (!IsValidDatabaseName(requestedTarget))
                return FailedRestore("PostgreSQL 还原目标数据库名称无效或超过 63 字节。");
            targetDatabase = requestedTarget;
        }

        PgRestorePreflight preflight;
        try
        {
            preflight = InspectRestoreTarget(config, targetDatabase);
        }
        catch (Exception exception)
        {
            return FailedRestore($"PostgreSQL 还原权限预检失败：{exception.Message}");
        }

        if (!preflight.HasRole)
            return FailedRestore("无法读取当前 PostgreSQL 用户的必要权限信息。");
        if (!TryGetToolMajorVersion(
                PgToolProcess.Run(
                    tools.PgRestorePath!,
                    ["--version"],
                    config,
                    ToolVersionTimeout),
                out var toolMajor))
        {
            return FailedRestore("无法识别 pg_restore 的版本。");
        }
        if (toolMajor != preflight.ServerMajorVersion)
        {
            return FailedRestore(
                $"pg_restore 主版本 {toolMajor} 与 PostgreSQL 服务端主版本 {preflight.ServerMajorVersion} 不匹配。");
        }
        if (preflight.DatabaseExists
            && (!preflight.HasPublicUsage || !preflight.HasPublicCreate))
        {
            return FailedRestore(
                "当前用户缺少目标库 public schema 的 USAGE 或 CREATE 权限，无法执行还原。");
        }
        if (preflight.ExistingDiaryTables.Count > 0)
        {
            return FailedRestore(
                $"目标数据库已包含 DiaryApp 表（{string.Join(", ", preflight.ExistingDiaryTables)}），为避免覆盖数据，已拒绝还原。");
        }

        var createdDatabase = false;
        try
        {
            if (!preflight.DatabaseExists)
            {
                if (!preflight.RoleCanCreateDatabase)
                {
                    return FailedRestore(
                        "目标数据库不存在，且当前用户没有 CREATEDB 权限；请配置已有空数据库作为还原目标。");
                }

                CreateDatabase(config, targetDatabase);
                createdDatabase = true;
            }

            var arguments = new List<string>
            {
                "--exit-on-error",
                "--single-transaction",
                "--no-owner",
                "--no-password",
                "--dbname", targetDatabase,
            };
            AddConnectionArguments(arguments, config, includeDatabase: false);
            arguments.Add(Path.GetFullPath(backupPath));
            var result = PgToolProcess.Run(
                tools.PgRestorePath!,
                arguments,
                config,
                BackupRestoreTimeout);
            if (!result.Success)
            {
                if (createdDatabase)
                    TryDropDatabase(config, targetDatabase);
                return FailedRestore($"PostgreSQL 还原失败：{result.ErrorMessage}");
            }

            config.Database = targetDatabase;
            return new DbRestoreResult(
                true,
                targetDatabase,
                createdDatabase ? CreatedDatabaseRecoveryMarker : null,
                !createdDatabase,
                null,
                sourceDatabase);
        }
        catch (Exception exception)
        {
            if (createdDatabase)
                TryDropDatabase(config, targetDatabase);
            return FailedRestore($"PostgreSQL 还原失败：{exception.Message}");
        }
    }

    public bool RollbackRestore(DbRestoreResult restore, out string? error)
    {
        error = null;
        if (!restore.Success)
            return true;

        var config = GetConfig();
        var previousDatabase = restore.PreviousDatabase;
        if (!string.IsNullOrWhiteSpace(previousDatabase))
            config.Database = previousDatabase;

        if (string.IsNullOrWhiteSpace(restore.RestoredPath))
            return true;

        try
        {
            if (restore.TargetPreviouslyExisted)
            {
                ClearKnownDiaryTables(config, restore.RestoredPath);
            }
            else if (string.Equals(restore.RecoveryPath, CreatedDatabaseRecoveryMarker, StringComparison.Ordinal))
            {
                TryDropDatabase(config, restore.RestoredPath);
            }
            return true;
        }
        catch (Exception exception)
        {
            error = $"删除 PostgreSQL 自动创建的还原目标失败：{exception.Message}";
            return false;
        }
    }

    public DbPostRestoreValidationResult ValidateRestoredDatabase()
    {
        if (_dataSource is null)
            return new DbPostRestoreValidationResult(false, "PostgreSQL 还原目标尚未连接。");

        try
        {
            var knownObjects = Query(
                    $"""
                    SELECT c.relname
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public'
                      AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                      AND c.relname IN ({string.Join(", ", KnownDiaryTables.Select(QuoteLiteral))});
                    """,
                    reader => reader.GetString(0))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var redMineValidation = ValidateTrackerTableGroup(
                "RedMine",
                RedMineTables,
                knownObjects);
            if (!redMineValidation.Success)
                return redMineValidation;

            var jiraValidation = ValidateTrackerTableGroup(
                "Jira",
                JiraTables,
                knownObjects);
            if (!jiraValidation.Success)
                return jiraValidation;

            return new DbPostRestoreValidationResult(true);
        }
        catch (Exception exception)
        {
            return new DbPostRestoreValidationResult(
                false,
                $"PostgreSQL Tracker 表复检失败：{exception.Message}");
        }
    }

    private const string CreatedDatabaseRecoveryMarker = "postgresql-created-database";

    private Config GetConfig() => (Config)Factory.GetConfig();

    private static DbPostRestoreValidationResult ValidateTrackerTableGroup(
        string trackerName,
        IReadOnlyCollection<string> expectedTables,
        IReadOnlySet<string> existingTables)
    {
        var present = expectedTables.Where(existingTables.Contains).ToArray();
        if (present.Length == 0)
            return new DbPostRestoreValidationResult(true);
        if (present.Length != expectedTables.Count)
        {
            var missing = expectedTables.Where(table => !existingTables.Contains(table));
            return new DbPostRestoreValidationResult(
                false,
                $"PostgreSQL 备份中的 {trackerName} 表不完整，缺少：{string.Join(", ", missing)}。");
        }
        if (!existingTables.Contains("plugin_data_versions"))
        {
            return new DbPostRestoreValidationResult(
                false,
                $"PostgreSQL 备份包含 {trackerName} 表，但缺少 plugin_data_versions。");
        }

        return new DbPostRestoreValidationResult(true);
    }

    private PgRestorePreflight InspectRestoreTarget(Config config, string targetDatabase)
    {
        using var admin = OpenConnection(config, GetMaintenanceDatabase(config));
        var role = QueryRole(admin);
        var databaseExists = DatabaseExists(admin, targetDatabase);
        if (!databaseExists)
        {
            return new PgRestorePreflight(
                false,
                role.HasRole,
                role.CanCreateDatabase,
                role.IsSuperuser,
                role.ServerVersionNum / 10000,
                true,
                true,
                []);
        }

        using var target = OpenConnection(config, targetDatabase);
        var targetState = QueryTargetState(target, config.User);
        return new PgRestorePreflight(
            true,
            role.HasRole && targetState.CurrentUserMatches,
            role.CanCreateDatabase,
            role.IsSuperuser,
            role.ServerVersionNum / 10000,
            targetState.HasPublicUsage,
            targetState.HasPublicCreate,
            targetState.ExistingDiaryTables);
    }

    private static PgRoleState QueryRole(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand(
            """
            SELECT current_user,
                   current_database(),
                   current_setting('server_version_num')::integer,
                   r.rolsuper,
                   r.rolcreatedb
            FROM pg_roles r
            WHERE r.rolname = current_user;
            """,
            connection);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PgRoleState(
                true,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4))
            : new PgRoleState(false, "", "", 0, false, false);
    }

    private static PgTargetState QueryTargetState(
        NpgsqlConnection connection,
        string configuredUser)
    {
        using var command = new NpgsqlCommand(
            $"""
            SELECT current_user = $1,
                   has_schema_privilege(current_user, 'public', 'USAGE'),
                   has_schema_privilege(current_user, 'public', 'CREATE');
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND c.relname IN ({string.Join(", ", KnownDiaryTables.Select(QuoteLiteral))});
            """,
            connection);
        command.Parameters.AddWithValue(configuredUser);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new PgTargetState(false, false, false, []);

        var currentUserMatches = reader.GetBoolean(0);
        var hasUsage = reader.GetBoolean(1);
        var hasCreate = reader.GetBoolean(2);
        reader.NextResult();
        var existingTables = new List<string>();
        while (reader.Read())
            existingTables.Add(reader.GetString(0));
        return new PgTargetState(currentUserMatches, hasUsage, hasCreate, existingTables);
    }

    private static bool DatabaseExists(NpgsqlConnection connection, string database)
    {
        using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1);",
            connection);
        command.Parameters.AddWithValue(database);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    private static void CreateDatabase(Config config, string database)
    {
        using var admin = OpenConnection(config, GetMaintenanceDatabase(config));
        using var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(database)};", admin);
        command.ExecuteNonQuery();
    }

    private static void TryDropDatabase(Config config, string database)
    {
        using var admin = OpenConnection(config, GetMaintenanceDatabase(config));
        using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(database)};",
            admin);
        command.ExecuteNonQuery();
    }

    private static void ClearKnownDiaryTables(Config config, string database)
    {
        using var connection = OpenConnection(config, database);
        var statements = string.Join(
            Environment.NewLine,
            KnownDiaryTables.Select(table =>
                $"DROP TABLE IF EXISTS {QuoteIdentifier(table)} CASCADE;"));
        using var command = new NpgsqlCommand(statements, connection);
        command.ExecuteNonQuery();
    }

    private static NpgsqlConnection OpenConnection(Config config, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = database,
            Username = config.User,
            Password = config.Password,
            ApplicationName = "DiaryApp maintenance",
            CommandTimeout = 10,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string GetMaintenanceDatabase(Config config)
        => string.Equals(config.Database, "postgres", StringComparison.OrdinalIgnoreCase)
            ? "template1"
            : "postgres";

    private static string CreateAutomaticTargetDatabaseName()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        return $"diaryapp_restore_{DateTime.UtcNow:yyyyMMddHHmmss}_{suffix}";
    }

    private static bool IsValidDatabaseName(string database)
        => !database.Contains('\0')
           && Encoding.UTF8.GetByteCount(database) <= 63;

    private static int GetServerMajorVersion(Config config, string database)
    {
        using var connection = OpenConnection(config, database);
        using var command = new NpgsqlCommand(
            "SELECT current_setting('server_version_num')::integer;",
            connection);
        var versionNum = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return versionNum / 10000;
    }

    private static bool TryGetToolMajorVersion(
        PgToolProcessResult result,
        out int majorVersion)
    {
        majorVersion = 0;
        if (!result.Success)
            return false;
        var match = Regex.Match(
            result.StandardOutput,
            @"PostgreSQL\)\s+(?<major>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(
            match.Groups["major"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out majorVersion);
    }

    private static void AddConnectionArguments(
        ICollection<string> arguments,
        Config config,
        bool includeDatabase = true)
    {
        if (!string.IsNullOrWhiteSpace(config.Host))
        {
            arguments.Add("--host");
            arguments.Add(config.Host);
        }

        if (config.Port > 0)
        {
            arguments.Add("--port");
            arguments.Add(config.Port.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(config.User))
        {
            arguments.Add("--username");
            arguments.Add(config.User);
        }

        if (includeDatabase)
        {
            arguments.Add("--dbname");
            arguments.Add(config.Database);
        }
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static DbBackupValidationResult InvalidBackup(string? error)
        => new(false, "PostgreSQL", 0, null, error);

    private static DbRestoreResult FailedRestore(string? error)
        => new(false, null, null, false, error);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record PgRoleState(
        bool HasRole,
        string CurrentUser,
        string CurrentDatabase,
        int ServerVersionNum,
        bool IsSuperuser,
        bool CanCreateDatabase);

    private sealed record PgTargetState(
        bool CurrentUserMatches,
        bool HasPublicUsage,
        bool HasPublicCreate,
        IReadOnlyList<string> ExistingDiaryTables);

    private sealed record PgRestorePreflight(
        bool DatabaseExists,
        bool HasRole,
        bool RoleCanCreateDatabase,
        bool IsSuperuser,
        int ServerMajorVersion,
        bool HasPublicUsage,
        bool HasPublicCreate,
        IReadOnlyList<string> ExistingDiaryTables);
}

internal sealed record PgToolProcessResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string ErrorMessage
        => string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput.Trim()
            : StandardError.Trim();
}

internal static class PgToolProcess
{
    public static PgToolProcessResult Run(
        string executable,
        IEnumerable<string> arguments,
        Config config,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PGPASSWORD"] = config.Password;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new PgToolProcessResult(false, -1, string.Empty, "无法启动 PostgreSQL 工具进程。");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var effectiveTimeout = timeout ?? TimeSpan.FromHours(1);
            var timeoutMilliseconds = effectiveTimeout.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : Math.Max(1, checked((int)effectiveTimeout.TotalMilliseconds));
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                catch
                {
                }

                Task.WaitAll(outputTask, errorTask);
                return new PgToolProcessResult(
                    false,
                    -1,
                    Sanitize(outputTask.GetAwaiter().GetResult(), config.Password),
                    $"PostgreSQL 工具执行超时（{effectiveTimeout}）。");
            }

            Task.WaitAll(outputTask, errorTask);
            var output = Sanitize(outputTask.GetAwaiter().GetResult(), config.Password);
            var error = Sanitize(errorTask.GetAwaiter().GetResult(), config.Password);
            return new PgToolProcessResult(process.ExitCode == 0, process.ExitCode, output, error);
        }
        catch (Exception exception)
        {
            return new PgToolProcessResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string Sanitize(string value, string password)
        => string.IsNullOrEmpty(password)
            ? value
            : value.Replace(password, "******", StringComparison.Ordinal);
}
