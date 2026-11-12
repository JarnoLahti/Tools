using System.Diagnostics;

namespace GitWorktreeTool;

public class GitProcessWrapper
{
   private readonly ProcessStartInfo _processStartInfo;

   private GitProcessWrapper()
   {
      _processStartInfo = new ProcessStartInfo()
      {
         FileName = "git",
         RedirectStandardError = true,
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
      };
   }

   public static GitProcessWrapper Instance
   {
      get
      {
         field ??= new();
         return field;
      }
   }

   public async Task<bool> IsInsideGitFolder()
   {
      _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("rev-parse");
      _processStartInfo.ArgumentList.Add("--is-inside-worktree");
      
      var p = Process.Start(_processStartInfo);
      if (p is null)
      {
        return false; 
      }
      await p.WaitForExitAsync();
      return p.ExitCode == 0;
   }

    public async Task<bool> BranchExists(string branchName)
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("show-ref");
       _processStartInfo.ArgumentList.Add("--verify");
       _processStartInfo.ArgumentList.Add("--quiet");
       _processStartInfo.ArgumentList.Add($"refs/heads/{branchName}");
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
       {
          return false; 
       }
       await p.WaitForExitAsync();
       return p.ExitCode == 0;     
    }

    public async Task<string?> GetRepoRootAsync()
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("rev-parse");
       _processStartInfo.ArgumentList.Add("--show-toplevel");
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
       {
          return null; 
       }
       var output = await p.StandardOutput.ReadToEndAsync();
       await p.WaitForExitAsync();
       return p.ExitCode == 0 ? output.Trim() : null;
    }

     public async Task<bool> AddWorktree(string branchName, string path, string sourceBranch)
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("worktree");
       _processStartInfo.ArgumentList.Add("add");
       _processStartInfo.ArgumentList.Add("-b");
       _processStartInfo.ArgumentList.Add(branchName);
       _processStartInfo.ArgumentList.Add(path);
       _processStartInfo.ArgumentList.Add(sourceBranch);
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
       {
          return false; 
       }
       await p.WaitForExitAsync();
       return p.ExitCode == 0;     
    }

    public async Task<string?> GetFileContentFromBranch(string branch, string filePath)
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("show");
       _processStartInfo.ArgumentList.Add($"{branch}:{filePath}");

       var p = Process.Start(_processStartInfo);
       if (p is null) return null;
       var output = await p.StandardOutput.ReadToEndAsync();
       await p.WaitForExitAsync();
       return p.ExitCode == 0 ? output : null;
    }

    public async Task PruneWorktree()
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("worktree");
       _processStartInfo.ArgumentList.Add("prune");
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
          return;
       
       await p.WaitForExitAsync();
    }

    public async Task<List<WorktreeEntry>> ListWorktreesAsync()
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("worktree");
       _processStartInfo.ArgumentList.Add("list");
       _processStartInfo.ArgumentList.Add("--porcelain");

       var p = Process.Start(_processStartInfo);
       if (p is null) return [];

       var output = await p.StandardOutput.ReadToEndAsync();
       await p.WaitForExitAsync();

       if (p.ExitCode != 0) return [];

       return ParseWorktreeList(output);
    }

    private static List<WorktreeEntry> ParseWorktreeList(string porcelainOutput)
    {
       var entries = new List<WorktreeEntry>();
       string? currentPath = null;
       string? currentBranch = null;

       foreach (var line in porcelainOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
       {
          var trimmed = line.Trim();

          if (trimmed.StartsWith("worktree "))
          {
             currentPath = trimmed["worktree ".Length..];
          }
          else if (trimmed.StartsWith("branch "))
          {
             var refName = trimmed["branch ".Length..];
             currentBranch = refName.StartsWith("refs/heads/") ? refName["refs/heads/".Length..] : refName;
          }

          if (currentPath is not null && currentBranch is not null)
          {
             entries.Add(new WorktreeEntry(currentPath, currentBranch));
             currentPath = null;
             currentBranch = null;
          }
       }

       return entries;
    }

    public async Task RemoveWorktree(string target)
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("worktree");
       _processStartInfo.ArgumentList.Add("remove");
       _processStartInfo.ArgumentList.Add(target);
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
          return;
       
       await p.WaitForExitAsync();
    }

    public async Task DeleteBranch(string target)
    {
       _processStartInfo.ArgumentList.Clear();
       _processStartInfo.ArgumentList.Add("branch");
       _processStartInfo.ArgumentList.Add("-D");
       _processStartInfo.ArgumentList.Add(target);
       
       var p = Process.Start(_processStartInfo);
       if (p is null)
          return;
       
       await p.WaitForExitAsync();
    }
}

public record WorktreeEntry(string Path, string BranchName);