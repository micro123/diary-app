using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Diary.BuildTasks;

public class GenInfoTask : Task
{
    [Required] public required string OutputDir { get; set; }
    [Required] public required string FileName { get; set; }
    [Required] public required string Project { get; set; }

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, "Generating VersionInfo for {0}.", Project);
        var rootDir = CheckOutput("git", "rev-parse --show-toplevel");
        if (rootDir == null)
        {
            Console.WriteLine($"Not a git repo? ${Environment.CurrentDirectory}");
            return false;
        }

        var hash = CheckOutput("git", "rev-parse HEAD")!;
        var hashShort = CheckOutput("git", "rev-parse --short HEAD")!;
        var count = CheckOutput("git", "rev-list HEAD --count --no-merges")!;
        var branch = CheckOutput("git", "rev-parse --abbrev-ref HEAD")!;
        var last = CheckOutput("git", "log -1 --pretty=format:%s")!;
        var date = CheckOutput("git", "log -1 --pretty=format:%cd --date=format:%y%m%d")!;
        var clean = CheckOutput("git", "status --untracked-files=no --porcelain")!;

        if (!string.IsNullOrEmpty(clean))
        {
            Log.LogMessage(MessageImportance.High, "repo was not clean!!");
            hash = $"{hash}-dirty";
            hashShort = $"{hashShort}-dirty";
        }

        var result = WriteOutputFile(hash, hashShort, count, branch, last, date, Environment.MachineName);
        Log.LogMessage(MessageImportance.High, "Generating VersionInfo for {0} Done.", Project);
        return result;
    }

    private bool WriteOutputFile(string gitHash, string gitShortHash, string gitCommitCount,
        string branch, string lastMsg, string commitDate, string hostName)
    {
        var content =
            $$"""
              using System;

              namespace {{Project}};

              internal static class VersionInfo
              {
                  public static readonly string BuildTime = "{{DateTime.Now:yyyy-MM-dd HH:mm:ss}}";
                  public static readonly string GitVersionFull = "{{gitHash}}";
                  public static readonly string GitVersionShort = "{{gitShortHash}}";
                  public static readonly string CommitCount = "{{gitCommitCount}}";
                  public static readonly string Branch = "{{branch}}";
                  public static readonly string LastCommitMessage = "{{lastMsg}}";
                  public static readonly string LastCommitDate = "{{commitDate}}";
                  public static readonly string HostName = "{{hostName}}";
              }

              """;

        if (!Directory.Exists(OutputDir))
            Directory.CreateDirectory(OutputDir);
        File.WriteAllText(Path.Combine(OutputDir, FileName), content);
        return true;
    }

    private string? CheckOutput(string cmd, string arg)
    {
        var psi = new ProcessStartInfo()
        {
            FileName = cmd,
            Arguments = arg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            Log.LogError("command {0} could not be found", cmd);
            return null;
        }

        var output = proc.StandardOutput.ReadToEnd().Trim();
        var error = proc.StandardError.ReadToEnd().Trim();

        proc.WaitForExit(5000);
        if (proc.ExitCode == 0)
        {
            Log.LogMessage(MessageImportance.High, "execute {0} {1} => {2}", cmd, arg, output);
            return output;
        }
        Log.LogError("command {0} exited with exit code {1}, error {2}", cmd, proc.ExitCode, error);
        return null;
    }
}