using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;

namespace GitWorktreeTool.Test;

public static class TestSessionFixture
{
    private static IContainer Container { get; set; } = null!;
    private const string RepoPath = "/repo";

    [Before(TestSession)]
    public static async Task SetupContainer()
    {
        var solutionRoot = ResolveSolutionRoot();
        if (!Directory.Exists(Path.Combine(solutionRoot, "GitWorktreeTool")))
        {
            throw new DirectoryNotFoundException($"Solution root not found at: {solutionRoot}");
        }

        Container = new ContainerBuilder("mcr.microsoft.com/dotnet/sdk:10.0")
            .WithCommand("sleep", "infinity")
            .WithResourceMapping(DirectoryPath.Of(solutionRoot), DirectoryPath.Of("/src"))
            .Build();

        await Container.StartAsync();

        await Container.ExecAsync(["/bin/bash", "-c", "apt-get update -qq && apt-get install -y -qq git clang zlib1g-dev"]);

        var publishResult = await Container.ExecAsync(["/bin/bash", "-c", "dotnet publish /src/GitWorktreeTool/GitWorktreeTool.csproj -o /usr/local/bin"]);
        if (publishResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit code {publishResult.ExitCode}. Stdout: {publishResult.Stdout}. Stderr: {publishResult.Stderr}");
        }

        var verifyBinary = await Container.ExecAsync(["test", "-x", "/usr/local/bin/gitworktree"]);
        if (verifyBinary.ExitCode != 0)
        {
            throw new InvalidOperationException("Published binary /usr/local/bin/gitworktree not found or not executable.");
        }

        await Container.ExecAsync(["git", "config", "--global", "user.email", "test@test.com"]);
        await Container.ExecAsync(["git", "config", "--global", "user.name", "Test User"]);

        await Container.ExecAsync(["mkdir", "-p", RepoPath]);
        await ExecCommandInDir("git init -b main", RepoPath);

        await CreateFile("test_file1.txt", "test_file1");
        await CreateFile(".env", "ENV_VAR=test_value");
        await Container.ExecAsync(["mkdir", "-p", $"{RepoPath}/test"]);
        await CreateFile("test/test_file2.txt", "test_file2");
        await CreateFile("test/.env", "NESTED_ENV_VAR=test_value");
        await CreateFile(".gitignore", "*.env");
        await CreateFile(".worktreeinclude", ".env");
        await Container.ExecAsync(["/bin/bash", "-c", $"echo '*.env' >> {RepoPath}/.worktreeinclude"]);

        await ExecCommandInDir("git add .", RepoPath);
        await ExecCommandInDir("git commit -m 'initial commit'", RepoPath);
    }

    [After(TestSession)]
    public static async Task TearDownContainer()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }

    private static string ResolveSolutionRoot()
    {
        var testAssemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        return Path.GetFullPath(Path.Combine(testAssemblyDir, "../../../../../"));
    }

    public static async Task CreateFile(string relativePath, string content)
    {
        var fullPath = $"{RepoPath}/{relativePath}";
        await Container.ExecAsync(["/bin/bash", "-c", $"echo '{content}' > {fullPath}"]);
    }

    public static async Task<bool> FileExistsInContainer(string relativePath)
    {
        var fullPath = $"{RepoPath}/{relativePath}";
        var result = await Container.ExecAsync(["test", "-e", fullPath]);
        return result.ExitCode == 0;
    }

    public static async Task<bool> BranchExistsInRepo(string branchName)
    {
        var result = await Container.ExecAsync(["/bin/bash", "-c", $"cd {RepoPath} && git show-ref --verify --quiet refs/heads/{branchName}"]);
        return result.ExitCode == 0;
    }

    public static async Task DeleteInContainer(string relativePath)
    {
        var fullPath = $"{RepoPath}/{relativePath}";
        await Container.ExecAsync(["rm", "-rf", fullPath]);
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunTool(string args)
    {
        var result = await Container.ExecAsync(["/bin/bash", "-c", $"cd {RepoPath} && gitworktree {args}"]);
        return ((int)(result.ExitCode ?? -1), result.Stdout ?? string.Empty, result.Stderr ?? string.Empty);
    }

    private static async Task ExecCommandInDir(string command, string workingDir)
    {
        await Container.ExecAsync(["/bin/bash", "-c", $"cd {workingDir} && {command}"]);
    }

    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

public class InitCommandTests
{
    [Test]
    public async Task Init_NoArgs_ReturnsError()
    {
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool("init");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).IsNotEmpty();
    }

    [Test]
    public async Task Init_OneArg_ReturnsError()
    {
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool("init main");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).IsNotEmpty();
    }

    [Test]
    public async Task Init_InvalidSourceBranch_ReturnsError()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool($"init {target} nonexistent");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("Source branch");
    }

    [Test]
    public async Task Init_Valid_CreatesWorktreeAndCopiesEnv()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, _) = await TestSessionFixture.RunTool($"init {target} main");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/.env")).IsTrue();
    }

    [Test]
    public async Task Init_WithNoInclude_CreatesWorktreeWithoutEnv()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, _) = await TestSessionFixture.RunTool($"init {target} main --no-include");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/.env")).IsFalse();
    }

    [Test]
    public async Task Init_WithTypeFeature_SetsBranchNamePrefix()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, _) = await TestSessionFixture.RunTool($"init {target} main --type feat");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo($"feat/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();
    }

    [Test]
    public async Task Init_WithFullTypeName_SetsBranchNamePrefix()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, _) = await TestSessionFixture.RunTool($"init {target} main --type documentation");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo($"doc/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();
    }

    [Test]
    public async Task Init_WithShortAlias_SetsBranchNamePrefix()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, _) = await TestSessionFixture.RunTool($"init {target} main -t fix");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo($"fix/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();
    }

    [Test]
    public async Task Init_WithInvalidType_ReturnsError()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool($"init {target} main --type invalid");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("Invalid branch type");
    }
}

public class IncludeCommandTests
{
    [Test]
    public async Task Include_NoArgs_ReturnsError()
    {
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool("include");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).IsNotEmpty();
    }

    [Test]
    public async Task Include_OneArg_ReturnsError()
    {
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool("include main");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).IsNotEmpty();
    }

    [Test]
    public async Task Include_NonexistentTarget_ReturnsError()
    {
        var target = TestSessionFixture.UniqueName("target");
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool($"include {target} main");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("not exist");
    }

    [Test]
    public async Task Include_NonexistentSourceBranch_ReturnsError()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init main {target} --no-include");

        var (exitCode, _, stderr) = await TestSessionFixture.RunTool($"include {target} nonexistent");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).Contains("Source branch");
    }

    [Test]
    public async Task Include_Valid_CopiesEnvToExistingWorktree()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init {target} main --no-include");

        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/.env")).IsFalse();

        var (exitCode, _, _) = await TestSessionFixture.RunTool($"include {target} main");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/.env")).IsTrue();
    }

    [Test]
    public async Task Include_GlobPattern_CopiesNestedMatchingFiles()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init {target} main --no-include");

        var (exitCode, _, _) = await TestSessionFixture.RunTool($"include {target} main");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/.env")).IsTrue();
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}/test/.env")).IsTrue();
    }
}

public class DeleteCommandTests
{
    [Test]
    public async Task Delete_NoArgs_ReturnsError()
    {
        var (exitCode, _, stderr) = await TestSessionFixture.RunTool("delete");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(stderr).IsNotEmpty();
    }

    [Test]
    public async Task Delete_Valid_RemovesWorktreeAndBranch()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init {target} main");

        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsTrue();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsTrue();

        var (exitCode, _, _) = await TestSessionFixture.RunTool($"delete {target}");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsFalse();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();
    }

    [Test]
    public async Task Delete_NonexistentWorktree_Succeeds()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init {target} main --no-include");
        await TestSessionFixture.RunTool($"delete {target}");

        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();

        var (exitCode, _, _) = await TestSessionFixture.RunTool($"delete {target}");

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_ManuallyRemovedWorktreeFolder_PrunesAndDeletesBranch()
    {
        var target = TestSessionFixture.UniqueName("target");
        await TestSessionFixture.RunTool($"init {target} main");

        await TestSessionFixture.DeleteInContainer($".worktree/{target}");

        await Assert.That(await TestSessionFixture.FileExistsInContainer($".worktree/{target}")).IsFalse();
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsTrue();

        var (exitCode, _, _) = await TestSessionFixture.RunTool($"delete {target}");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await TestSessionFixture.BranchExistsInRepo(target)).IsFalse();
    }
}
