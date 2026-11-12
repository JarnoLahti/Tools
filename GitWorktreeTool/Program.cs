using System.CommandLine;
using Spectre.Console;

namespace GitWorktreeTool;

internal static class Program
{
    private const int ExitCodeSuccess = 0;
    private const int ExitCodeError = 1;
    private const string WorktreeFolder = ".worktree";
    private static readonly Option<bool> DebugOption = new("--debug")
    {
        Description = "Enable detailed debug output",
        Recursive = true
    };
    
    private static readonly Argument<string> SourceBranch = new("source branch")
    {
        Description = "name of the source branch which new worktree will be based on.",
    };
    
    private static readonly Argument<string> TargetBranch = new("target branch")
    {
        Description = "name of the target branch which will also be name of the worktree."
    };

    private static readonly IAnsiConsole ErrorConsole = AnsiConsole.Create(
        new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

    private static int Main(string[] args)
    {
        RootCommand cmd = new("simple worktree initializer command");
        cmd.Options.Add(DebugOption);
        cmd.Subcommands.Add(BuildInitCommand());
        cmd.Subcommands.Add(BuildIncludeCommand());
        cmd.Subcommands.Add(BuildDeleteCommand());
        if (args.Length == 0)
        {
            cmd.Parse("-h").Invoke();
            return ExitCodeError;
        }
        var parseResult = cmd.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                PrintError(error.Message);
            }
            return ExitCodeError;
        }
        return parseResult.Invoke();
    }

    private static Command BuildDeleteCommand()
    {
        Command command = new("delete", "deletes worktree folder and branch locally");
        command.Arguments.Add(TargetBranch); 
        command.SetAction(HandleCommand);
        return command;
        
        Task<int> HandleCommand(ParseResult result)
        {
            var target = result.GetValue(TargetBranch)  ?? string.Empty;
            var isDebug = result.GetValue(DebugOption);
            return DeleteWorktree(target,  isDebug);
        }
    }

    private static async Task<int> DeleteWorktree(string target,  bool isDebug)
    {
        var worktrees = await GitProcessWrapper.Instance.ListWorktreesAsync();
        PrintInfo($"Found {worktrees.Count} worktree entries", isDebug);

        var entry = worktrees.FirstOrDefault(wt =>
            wt.BranchName == target ||
            wt.BranchName.EndsWith($"/{target}") ||
            wt.Path.EndsWith($"/{target}"));

        if (entry is null)
        {
            PrintWarning($"No worktree found matching '{target}'.");
            await GitProcessWrapper.Instance.PruneWorktree();
            
            if (!await GitProcessWrapper.Instance.BranchExists(target))
            {
                PrintWarning($"branch '{target}' does not exist.");
                return ExitCodeSuccess;
            }
            
            PrintInfo($"deleting branch {target}", isDebug); 
            await GitProcessWrapper.Instance.DeleteBranch(target);
            return ExitCodeSuccess;
        }

        PrintInfo($"Matched worktree: path='{entry.Path}', branch='{entry.BranchName}'", isDebug);

        if (Directory.Exists(entry.Path))
        {
            PrintInfo($"removing worktree '{entry.Path}'", isDebug);
            await GitProcessWrapper.Instance.RemoveWorktree(entry.Path);
        }
        else
        {
            PrintWarning($"Worktree path '{entry.Path}' does not exist. Pruning..."); 
            await GitProcessWrapper.Instance.PruneWorktree();
        }

        if (!await GitProcessWrapper.Instance.BranchExists(entry.BranchName))
        {
            PrintWarning($"branch '{entry.BranchName}' does not exist.");
            return ExitCodeSuccess;
        }

        PrintInfo($"deleting branch {entry.BranchName}", isDebug); 
        await GitProcessWrapper.Instance.DeleteBranch(entry.BranchName);
        
        return ExitCodeSuccess;
    }

    private static Command BuildInitCommand()
    {
        Command command = new("init","Worktree initializer command");
        var noIncludeOption = new Option<bool>("--no-include");
        noIncludeOption.Description = "Skip copying .worktreeinclude files to the new worktree.";
        var branchTypeOption = new Option<string>("--type", "-t")
        {
            Description = "Branch type prefix. Allowed: feature (feat), fix, chore, documentation (doc). Sets branch name to [type]/[name].",
            Arity = ArgumentArity.ZeroOrOne
        };
        command.Arguments.Add(TargetBranch);
        command.Arguments.Add(SourceBranch);
        command.Options.Add(noIncludeOption);
        command.Options.Add(branchTypeOption);
        command.SetAction(HandleCommand); 
        return command;

        Task<int> HandleCommand(ParseResult result)
        {
            var  target = result.GetValue(TargetBranch)  ?? string.Empty;
            var  source = result.GetValue(SourceBranch) ?? string.Empty;
            var  noInclude = result.GetValue(noIncludeOption);
            var debugEnabled = result.GetValue(DebugOption);
            var branchType = result.GetValue(branchTypeOption);
            return InitWorktree(target, source, noInclude, debugEnabled, branchType);
        }
    }

    private static Command BuildIncludeCommand()
    {
        Command command = new("include","copies files and folders stated in .worktreeinclude from source to target. Only copying items which are part of .gitignore hence would be ignored by git worktree creation");
        command.Arguments.Add(TargetBranch);
        command.Arguments.Add(SourceBranch);
        command.SetAction(HandleCommand); 
        return command;

        async Task<int> HandleCommand(ParseResult result)
        {
            var target = result.GetValue(TargetBranch) ?? string.Empty;
            var source = result.GetValue(SourceBranch) ?? string.Empty;
            var debugEnabled = result.GetValue(DebugOption);
            return await IncludeWorktree(target, source, validateGit: true, debugEnabled: debugEnabled);
        }
    }

    private static async Task<int> IncludeWorktree(string target, string source, bool validateGit = true, bool debugEnabled = false)
    {
        PrintInfo($"IncludeWorktree started: source='{source}', target='{target}', validateGit={validateGit}", debugEnabled);

        if (validateGit)
        {
            PrintInfo("Validating git environment...", debugEnabled);
            if (!await GitProcessWrapper.Instance.IsInsideGitFolder())
            {
                PrintError($"Not a git folder: {Directory.GetCurrentDirectory()}");
                return ExitCodeError;
            }
            PrintInfo("Inside git folder: confirmed", debugEnabled);

            if (!await GitProcessWrapper.Instance.BranchExists(source))
            {
                PrintError($"Source branch '{source}' does not exist.");
                return ExitCodeError;
            }
            PrintInfo($"Source branch '{source}' exists", debugEnabled);
        }

        PrintInfo("Determining git repository root...", debugEnabled);
        var repoRoot = await GitProcessWrapper.Instance.GetRepoRootAsync();
        if (repoRoot is null)
        {
            PrintError("Failed to determine git repository root.");
            return ExitCodeError;
        }
        PrintInfo($"Repository root: {repoRoot}", debugEnabled);

        var worktreeIncludePath = Path.Combine(repoRoot, ".worktreeinclude");
        PrintInfo($"Looking for .worktreeinclude at: {worktreeIncludePath}", debugEnabled);
        if (!File.Exists(worktreeIncludePath))
        {
            PrintInfo(".worktreeinclude not found, nothing to include", debugEnabled);
            return ExitCodeSuccess;
        }
        PrintInfo(".worktreeinclude found", debugEnabled);

        PrintInfo($"Reading .gitignore from source branch '{source}'...", debugEnabled);
        var gitignoreContent = await GitProcessWrapper.Instance.GetFileContentFromBranch(source, ".gitignore");
        if (gitignoreContent is null)
        {
            PrintInfo($".gitignore not found on source branch '{source}', nothing is ignored", debugEnabled);
            return ExitCodeSuccess;
        }
        PrintInfo(".gitignore read successfully", debugEnabled);

        var gitignoreEntries = ParseGitignoreEntries(gitignoreContent);
        PrintInfo($"Parsed {gitignoreEntries.Count} gitignore entries", debugEnabled);

        var worktreeDir = Path.Combine(repoRoot, WorktreeFolder);
        var targetPath = Path.Combine(worktreeDir, target);
        PrintInfo($"Target worktree path: {targetPath}", debugEnabled);
        if (!Directory.Exists(targetPath))
        {
            PrintWarning($"Target worktree directory does not exist: {targetPath}. skipping..");
        }
        else
        {
            PrintInfo("Target worktree directory exists", debugEnabled);
        }

        await foreach (var entry in File.ReadLinesAsync(worktreeIncludePath).Select(line => line.Trim()))
        {
            if (string.IsNullOrEmpty(entry) || entry.StartsWith('#'))
            {
                continue;
            }

            PrintInfo($"Processing entry: '{entry}'", debugEnabled);

            if (entry.Contains('*') || entry.Contains('?'))
            {
                var pattern = entry.EndsWith('/') ? entry + "*" : entry;
                var copied = 0;

                foreach (var file in EnumerateRepoFiles(repoRoot))
                {
                    var relativePath = Path.GetRelativePath(repoRoot, file)
                        .Replace(Path.DirectorySeparatorChar, '/');

                    if (!MatchesGlob(relativePath, pattern))
                    {
                        continue;
                    }

                    if (!IsGitignored(relativePath, gitignoreEntries))
                    {
                        PrintInfo($"Glob match '{relativePath}' is not gitignored, skipping", debugEnabled);
                        continue;
                    }

                    var destination = Path.Combine(targetPath,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var destinationDir = Path.GetDirectoryName(destination);
                    if (destinationDir is not null)
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    File.Copy(file, destination, overwrite: true);
                    PrintInfo($"Copied glob match: '{relativePath}'", debugEnabled);
                    copied++;
                }

                if (copied == 0)
                {
                    PrintWarning($"Glob '{entry}' matched no gitignored files. Skipping");
                }

                continue;
            }

            if (!IsGitignored(entry, gitignoreEntries))
            {
                PrintInfo($"Entry '{entry}' is not gitignored, skipping", debugEnabled);
                continue;
            }
            PrintInfo($"Entry '{entry}' matched gitignore pattern", debugEnabled);

            var sourcePath = Path.Combine(repoRoot, entry);
            if (File.Exists(sourcePath))
            {
                PrintInfo($"Copying file: '{sourcePath}' -> '{targetPath}/{entry}'", debugEnabled);
                var targetFilePath = Path.Combine(targetPath, entry);
                var targetDir = Path.GetDirectoryName(targetFilePath);
                if (targetDir is not null && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    PrintInfo($"Created directory: {targetDir}", debugEnabled);
                }
                File.Copy(sourcePath, targetFilePath, overwrite: true);
                PrintInfo($"File copied successfully: {entry}", debugEnabled);
            }
            else if (Directory.Exists(sourcePath))
            {
                PrintInfo($"Copying directory: '{sourcePath}' -> '{targetPath}/{entry}'", debugEnabled);
                var destDir = Path.Combine(targetPath, entry);
                CopyDirectory(sourcePath, destDir);
                PrintInfo($"Directory copied successfully: {entry}", debugEnabled);
            }
            else
            {
                PrintWarning($"Entry '{entry}' not found on disk at '{sourcePath}'. Skipping");
            }
        }

        PrintInfo("IncludeWorktree completed successfully", debugEnabled);
        return ExitCodeSuccess;
    }

    private static IEnumerable<string> EnumerateRepoFiles(string directory)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = true,
        };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            if (Path.GetFileName(file) == ".git")
            {
                continue;
            }

            yield return file;
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(directory, "*", options))
        {
            var name = Path.GetFileName(subDirectory);
            if (name is ".git" or WorktreeFolder)
            {
                continue;
            }

            if ((File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            foreach (var file in EnumerateRepoFiles(subDirectory))
            {
                yield return file;
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }

    private static List<string> ParseGitignoreEntries(string gitignoreContent)
    {
        return gitignoreContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.TrimEnd('/'))
            .ToList();
    }

    private static bool IsGitignored(string entry, List<string> gitignoreEntries)
    {
        foreach (var pattern in gitignoreEntries)
        {
            if (entry == pattern)
            {
                return true;
            }

            if (entry.StartsWith(pattern + "/"))
            {
                return true;
            }

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (MatchesGlob(entry, pattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesGlob(string text, string pattern)
    {
        var patternSpan = pattern.AsSpan();
        var textSpan = text.AsSpan();
        var patternIdx = 0;
        var textIdx = 0;
        var starIdx = -1;
        var matchIdx = 0;

        while (textIdx < textSpan.Length)
        {
            if (patternIdx < patternSpan.Length && patternSpan[patternIdx] == '*')
            {
                starIdx = patternIdx;
                matchIdx = textIdx;
                patternIdx++;
            }
            else if (patternIdx < patternSpan.Length && (patternSpan[patternIdx] == '?' || patternSpan[patternIdx] == textSpan[textIdx]))
            {
                patternIdx++;
                textIdx++;
            }
            else if (starIdx != -1)
            {
                patternIdx = starIdx + 1;
                matchIdx++;
                textIdx = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (patternIdx < patternSpan.Length && patternSpan[patternIdx] == '*')
        {
            patternIdx++;
        }

        return patternIdx == patternSpan.Length;
    }

    private static async Task<int> InitWorktree(string target, string source, bool noInclude = false, bool debugEnabled = false, string? branchType = null)
    {
        var branchName = NormalizeBranchType(branchType) switch
        {
            not null => $"{NormalizeBranchType(branchType)}/{target}",
            null when !string.IsNullOrWhiteSpace(branchType) => null,
            null => target
        };

        if (branchName is null)
        {
            PrintError($"Invalid branch type '{branchType}'. Allowed: feature (feat), fix, chore, documentation (doc).");
            return ExitCodeError;
        }

        PrintInfo($"InitWorktree started: source='{source}', target='{target}', branchName='{branchName}', noInclude={noInclude}", debugEnabled);

        PrintInfo("Checking if inside git folder...", debugEnabled);
        if (!await GitProcessWrapper.Instance.IsInsideGitFolder())
        {
            PrintError($"Not a git folder: {Directory.GetCurrentDirectory()}"); 
            return ExitCodeError;
        }
        PrintInfo("Inside git folder: confirmed", debugEnabled);

        PrintInfo($"Checking source branch '{source}'...", debugEnabled);
        if (!await GitProcessWrapper.Instance.BranchExists(source))
        {
            PrintError($"Source branch '{source}' does not exist.");
            return ExitCodeError;
        }
        PrintInfo($"Source branch '{source}' exists", debugEnabled);

        PrintInfo($"Checking target branch '{branchName}'...", debugEnabled);
        if (await GitProcessWrapper.Instance.BranchExists(branchName))
        {
            PrintError($"Target branch '{branchName}' already exists.");
            return ExitCodeError;
        }
        PrintInfo($"Target branch '{branchName}' does not exist yet", debugEnabled);

        PrintInfo("Determining git repository root...", debugEnabled);
        var repoRoot = await GitProcessWrapper.Instance.GetRepoRootAsync();
        if (repoRoot is null)
        {
            PrintError("Failed to determine git repository root.");
            return ExitCodeError;
        }
        PrintInfo($"Repository root: {repoRoot}", debugEnabled);

        var worktreeDir = Path.Combine(repoRoot, WorktreeFolder);
        if (!Directory.Exists(worktreeDir))
        {
            PrintInfo($"Creating worktree folder: {worktreeDir}", debugEnabled);
            Directory.CreateDirectory(worktreeDir);
        }
        PrintInfo($"Worktree folder exists: {worktreeDir}", debugEnabled);

        PrintInfo("Ensuring .worktree folder is gitignored...", debugEnabled);
        await EnsureGitignoreEntryAsync(repoRoot);
        PrintInfo(".worktree gitignore entry ensured", debugEnabled);

        var targetPath = Path.Combine(worktreeDir, target);
        PrintInfo($"Checking target worktree path: {targetPath}", debugEnabled);
        if (Directory.Exists(targetPath))
        {
            PrintError($"Target directory '{targetPath}' already exists.");
            return ExitCodeError;
        }

        PrintInfo($"Creating worktree: branch='{branchName}', path='{targetPath}', source='{source}'", debugEnabled);
        if (!await GitProcessWrapper.Instance.AddWorktree(branchName, targetPath, source))
        {
            PrintError($"Failed to create worktree for '{branchName}'.");
            return ExitCodeError;
        }
        PrintInfo("Worktree created successfully", debugEnabled);

        AnsiConsole.MarkupLine($"Worktree for '{branchName}' created at '{targetPath}' (based on '{source}').");

        if (!noInclude)
        {
            PrintInfo("Running include step...", debugEnabled);
            return await IncludeWorktree(target, source, validateGit: false, debugEnabled);
        }

        PrintInfo("Include step skipped, --no-include flag set",  debugEnabled);
        return ExitCodeSuccess;
    }

    private static async Task EnsureGitignoreEntryAsync(string repoRoot)
    {
        var gitignorePath = Path.Combine(repoRoot, ".gitignore");

        if (File.Exists(gitignorePath))
        {
            if (await File.ReadLinesAsync(gitignorePath).AnyAsync(line => line.Trim() is WorktreeFolder))
            {
                return;
            }
            await File.AppendAllTextAsync(gitignorePath, Environment.NewLine + WorktreeFolder + Environment.NewLine);
        }
        else
        {
            await File.WriteAllTextAsync(gitignorePath, WorktreeFolder + Environment.NewLine);
        }
    }
    
    private static string? NormalizeBranchType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "feature" or "feat" => "feat",
            "fix" => "fix",
            "chore" => "chore",
            "documentation" or "doc" => "doc",
            _ => null
        };
    }

    private static void PrintInfo(string message, bool debugEnabled = false)
    {
        if (!debugEnabled) return;
        AnsiConsole.MarkupLine($"{Markup.Escape("[")}[blue]INFO[/]{Markup.Escape("]")}: {message}");
    }

    private static void PrintError(string message)
    {
        ErrorConsole.MarkupLine($"{Markup.Escape("[")}[red]ERROR[/]{Markup.Escape("]")}: {message}");
    }
    
    private static void PrintWarning(string message)
    {
        AnsiConsole.MarkupLine($"{Markup.Escape("[")}[yellow]WARNING[/]{Markup.Escape("]")}: {message}");
    }
}
