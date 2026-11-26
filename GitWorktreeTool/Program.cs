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
            PrintError($"Target worktree directory does not exist: {targetPath}");
            return ExitCodeError;
        }
        PrintInfo("Target worktree directory exists", debugEnabled);

        var repoRelativeFiles = new Lazy<List<string>>(() => EnumerateRepoFiles(repoRoot)
            .Select(file => ToRepoRelativePath(repoRoot, file))
            .ToList());

        await foreach (var entry in File.ReadLinesAsync(worktreeIncludePath).Select(line => line.Trim()))
        {
            if (string.IsNullOrEmpty(entry) || entry.StartsWith('#'))
            {
                continue;
            }

            PrintInfo($"Processing entry: '{entry}'", debugEnabled);

            if (IsLiteralAnchoredPath(entry))
            {
                CopyLiteralEntry(entry.Trim('/'), repoRoot, targetPath, gitignoreEntries, debugEnabled);
            }
            else
            {
                CopyMatchingEntries(entry, repoRelativeFiles.Value, repoRoot, targetPath, gitignoreEntries, debugEnabled);
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

    private static void CopyLiteralEntry(string entry, string repoRoot, string targetPath, List<string> gitignoreEntries, bool debugEnabled)
    {
        if (!IsGitignored(entry, gitignoreEntries))
        {
            PrintInfo($"Entry '{entry}' is not gitignored, skipping", debugEnabled);
            return;
        }
        PrintInfo($"Entry '{entry}' matched gitignore pattern", debugEnabled);

        var sourcePath = Path.Combine(repoRoot, entry);
        if (File.Exists(sourcePath))
        {
            PrintInfo($"Copying file: '{sourcePath}' -> '{targetPath}/{entry}'", debugEnabled);
            CopyFilePreservingRelativePath(repoRoot, targetPath, entry);
            PrintInfo($"File copied successfully: {entry}", debugEnabled);
        }
        else if (Directory.Exists(sourcePath))
        {
            PrintInfo($"Copying directory: '{sourcePath}' -> '{targetPath}/{entry}'", debugEnabled);
            CopyDirectory(sourcePath, Path.Combine(targetPath, entry));
            PrintInfo($"Directory copied successfully: {entry}", debugEnabled);
        }
        else
        {
            PrintWarning($"Entry '{entry}' not found on disk at '{sourcePath}'. Skipping");
        }
    }

    private static void CopyMatchingEntries(string entry, List<string> repoRelativeFiles, string repoRoot, string targetPath, List<string> gitignoreEntries, bool debugEnabled)
    {
        var copiedFileCount = 0;
        foreach (var relativePath in repoRelativeFiles)
        {
            if (!MatchesPathPattern(relativePath, entry))
            {
                continue;
            }

            if (!IsGitignored(relativePath, gitignoreEntries))
            {
                PrintInfo($"Match '{relativePath}' is not gitignored, skipping", debugEnabled);
                continue;
            }

            CopyFilePreservingRelativePath(repoRoot, targetPath, relativePath);
            PrintInfo($"Copied match: '{relativePath}'", debugEnabled);
            copiedFileCount++;
        }

        if (copiedFileCount == 0)
        {
            PrintWarning($"Entry '{entry}' matched no gitignored files. Skipping");
        }
    }

    private static void CopyFilePreservingRelativePath(string sourceRoot, string targetRoot, string relativePath)
    {
        var platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.Combine(targetRoot, platformRelativePath);
        var destinationDir = Path.GetDirectoryName(destination);
        if (destinationDir is not null)
        {
            Directory.CreateDirectory(destinationDir);
        }

        File.Copy(Path.Combine(sourceRoot, platformRelativePath), destination, overwrite: true);
    }

    private static string ToRepoRelativePath(string repoRoot, string file)
        => Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static List<string> ParseGitignoreEntries(string gitignoreContent)
    {
        return gitignoreContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.TrimEnd('/'))
            .ToList();
    }

    private static bool IsGitignored(string relativePath, List<string> gitignoreEntries)
        => gitignoreEntries.Any(pattern => MatchesPathPattern(relativePath, pattern));

    private static bool ContainsWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    /// <summary>
    /// Following gitignore semantics, a pattern containing a slash (other than a trailing one)
    /// is relative to the repository root; otherwise it matches at any directory depth.
    /// </summary>
    private static bool IsAnchoredPattern(string pattern) => pattern.TrimEnd('/').Contains('/');

    private static bool IsLiteralAnchoredPath(string entry) => IsAnchoredPattern(entry) && !ContainsWildcard(entry);

    /// <summary>
    /// Checks a repository-relative path against a gitignore-style pattern. A match on any
    /// leading directory also matches everything beneath it.
    /// </summary>
    private static bool MatchesPathPattern(string relativePath, string pattern)
    {
        var pathSegments = relativePath.Split('/');

        if (!IsAnchoredPattern(pattern))
        {
            var segmentPattern = pattern.TrimEnd('/');
            return pathSegments.Any(segment => MatchesExactlyOrByGlob(segment, segmentPattern));
        }

        var anchoredPattern = pattern.Trim('/');
        return Enumerable.Range(1, pathSegments.Length)
            .Select(depth => string.Join('/', pathSegments, 0, depth))
            .Any(pathPrefix => MatchesExactlyOrByGlob(pathPrefix, anchoredPattern));
    }

    private static bool MatchesExactlyOrByGlob(string text, string pattern)
        => ContainsWildcard(pattern) ? MatchesGlob(text, pattern) : text == pattern;

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
