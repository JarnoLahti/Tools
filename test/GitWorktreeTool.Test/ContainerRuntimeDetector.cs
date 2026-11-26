using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;

namespace GitWorktreeTool.Test;

/// <summary>
/// Points Testcontainers at a working container runtime. Docker is used when its socket is
/// reachable (Testcontainers detects it by itself); otherwise Podman is detected and the
/// environment variables Testcontainers reads are set for it.
/// Must run before any Testcontainers type is used, because Testcontainers reads its settings once.
/// </summary>
internal static class ContainerRuntimeDetector
{
    private const string DockerHostVariable = "DOCKER_HOST";
    private const string DockerSocketOverrideVariable = "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE";

    /// <summary>Docker-compatible socket path inside the Podman machine VM, mounted into Ryuk.</summary>
    private const string PodmanMachineInternalSocketPath = "/var/run/docker.sock";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private sealed record PodmanSocket(string HostPath, string PathInsideRuntime);

    public static void ConfigureEnvironment()
    {
        if (OperatingSystem.IsWindows() || IsEnvironmentVariableSet(DockerHostVariable))
        {
            return;
        }

        if (GetDockerSocketCandidates().Any(IsUnixSocketReachable))
        {
            return;
        }

        var podmanSocket = FindPodmanSocket();
        if (podmanSocket is null)
        {
            return;
        }

        Environment.SetEnvironmentVariable(DockerHostVariable, $"unix://{podmanSocket.HostPath}");
        if (!IsEnvironmentVariableSet(DockerSocketOverrideVariable))
        {
            Environment.SetEnvironmentVariable(DockerSocketOverrideVariable, podmanSocket.PathInsideRuntime);
        }
    }

    private static IEnumerable<string> GetDockerSocketCandidates()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/var/run/docker.sock";
        yield return Path.Combine(homeDirectory, ".docker", "run", "docker.sock");
        yield return Path.Combine(homeDirectory, ".docker", "desktop", "docker.sock");
    }

    /// <summary>
    /// Podman machine (macOS/Windows) exposes a host socket forwarded into a VM; native Podman
    /// (Linux) exposes a local socket that is the same path for the host and the containers.
    /// </summary>
    private static PodmanSocket? FindPodmanSocket()
    {
        var machineSocketPath = RunCommand("podman", "machine", "inspect", "--format", "{{.ConnectionInfo.PodmanSocket.Path}}");
        if (machineSocketPath is not null && IsUnixSocketReachable(machineSocketPath))
        {
            return new PodmanSocket(machineSocketPath, PodmanMachineInternalSocketPath);
        }

        var localSocketPath = RunCommand("podman", "info", "--format", "{{.Host.RemoteSocket.Path}}");
        if (localSocketPath is not null && IsUnixSocketReachable(localSocketPath))
        {
            return new PodmanSocket(localSocketPath, localSocketPath);
        }

        return null;
    }

    private static bool IsUnixSocketReachable(string socketPath)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <returns>Trimmed standard output, or null when the command is missing, fails or prints nothing.</returns>
    private static string? RunCommand(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
            {
                process.Kill();
                return null;
            }

            var output = standardOutput.Result.Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool IsEnvironmentVariableSet(string variableName)
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variableName));
}
