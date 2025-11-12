using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Diary.BuildTasks;

public class GenInfoTask : Task
{
    [Required] public required string OutputFile { get; set; }
    [Required] public required string Project { get; set; }

    public override bool Execute()
    {
        string? rootDir = CheckOutput("git", "rev-parse --show-toplevel");
        if (rootDir == null)
        {
            Console.WriteLine($"Not a git repo? ${Environment.CurrentDirectory}");
            return false;
        }

        Console.WriteLine($"repo is {rootDir}");
        string hash = CheckOutput("git", "rev-parse HEAD")!;
        string hashShort = CheckOutput("git", "rev-parse --short HEAD")!;
        string count = CheckOutput("git", "rev-list HEAD --count --no-merges")!;
        string branch = CheckOutput("git", "rev-parse --abbrev-ref HEAD")!;
        string last = CheckOutput("git", "log -1 --pretty=format:%s")!;

        return WriteOutputFile(hash, hashShort, count, branch, last, Environment.MachineName);
    }

    private bool WriteOutputFile(string gitHash, string gitShortHash, string gitCommitCount,
        string branch, string lastMsg, string hostName)
    {
        var content =
            $$"""
              using System;

              namespace {{Project}};

              internal static class BuildInfo
              {
                  public static readonly string BuildTime = "{{DateTime.Now:yyyy-MM-dd HH:mm:ss}}";
                  public static readonly string GitHash = "{{gitHash}}";
                  public static readonly string GitHashShort = "{{gitShortHash}}";
                  public static readonly string CommitCount = "{{gitCommitCount}}";
                  public static readonly string Branch = "{{branch}}";
                  public static readonly string LastCommitMessage = "{{lastMsg}}";
                  public static readonly string HostName = "{{hostName}}";
              }

              """;

        File.WriteAllText(OutputFile, content);
        return true;
    }

    private static string? CheckOutput(string cmd, string arg)
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
            return null;

        var output = proc.StandardOutput.ReadToEnd().Trim();
        var error = proc.StandardError.ReadToEnd().Trim();

        proc.WaitForExit(5000);
        if (proc.ExitCode == 0) return output;
        Console.WriteLine($"command {cmd} exited with exit code {proc.ExitCode}, error {error}");
        return null;
    }
}