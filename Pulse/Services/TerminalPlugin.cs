using Microsoft.SemanticKernel;
using Pulse.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Semantic Kernel plugin that lets the agent execute shell commands
/// (PowerShell, cmd, or bash) on the host machine and return their output.
///
/// Enabled only when <see cref="TerminalSettings.IsEnabled"/> is <c>true</c>.
/// Each command runs as a child process with a configurable timeout and
/// output-length cap.
/// </summary>
public sealed class TerminalPlugin(
    LlmSettingsService settingsService,
    ITenantContext tenantContext,
    ILogger<TerminalPlugin> logger)
{
    [KernelFunction("run_terminal_command")]
    [Description(
        "Executes a shell command and returns the combined stdout + stderr output. " +
        "Use for running scripts, CLI tools, build tasks, file operations, system queries, git commands, etc. " +
        "The exit code is included at the end of the output. " +
        "Prefer PowerShell for Windows tasks, bash for Unix/WSL tasks.")]
    public async Task<string> RunCommandAsync(
        [Description("The full command to execute, e.g. 'Get-Process | Select-Object -First 5' or 'ls -la'.")]
        string command,

        [Description("Shell to use: 'powershell' (default), 'cmd', or 'bash'.")]
        string shell = "powershell",

        [Description("Absolute working directory path. Leave empty to use the configured default.")]
        string workingDirectory = "",

        CancellationToken ct = default)
    {
        var ts = (await settingsService.GetAsync(tenantContext.TenantId ?? Guid.Empty)).TerminalSettings;

        if (!ts.IsEnabled)
            return "Terminal execution is disabled. Enable it in Settings → Terminal.";

        var resolvedShell = string.IsNullOrWhiteSpace(shell) ? ts.DefaultShell : shell.Trim().ToLowerInvariant();
        var resolvedDir   = string.IsNullOrWhiteSpace(workingDirectory)
            ? (string.IsNullOrWhiteSpace(ts.WorkingDirectory) ? Directory.GetCurrentDirectory() : ts.WorkingDirectory)
            : workingDirectory;

        if (!Directory.Exists(resolvedDir))
            return $"Working directory not found: {resolvedDir}";

        logger.LogInformation(
            "Terminal: shell={Shell} dir={Dir} command={Cmd}",
            resolvedShell, resolvedDir, command.Length > 120 ? command[..120] + "…" : command);

        try
        {
            return await ExecuteAsync(
                resolvedShell, command, resolvedDir,
                TimeSpan.FromSeconds(ts.TimeoutSeconds),
                ts.MaxOutputLength, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal execution failed");
            return $"Terminal error: {ex.Message}";
        }
    }

    // ── Execution engine ──────────────────────────────────────────────────────

    private static async Task<string> ExecuteAsync(
        string shell, string command, string workingDir,
        TimeSpan timeout, int maxOutput, CancellationToken ct)
    {
        var (fileName, args) = BuildShellArgs(shell, command);

        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc   = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return $"[TIMEOUT] Command exceeded {timeout.TotalSeconds}s.\n" +
                   TruncateOutput(stdout.ToString(), maxOutput / 2);
        }

        var result = BuildResult(stdout.ToString(), stderr.ToString(), proc.ExitCode, maxOutput);
        return result;
    }

    private static (string FileName, string[] Args) BuildShellArgs(string shell, string command)
    {
        return shell switch
        {
            "cmd" => ("cmd.exe", ["/c", command]),

            "bash" => ("bash", ["-c", command]),

            _ => // powershell — use EncodedCommand to avoid quoting issues
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? ("powershell.exe",
                       ["-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-EncodedCommand", EncodeCommand(command)])
                    : ("pwsh",
                       ["-NonInteractive",
                        "-EncodedCommand", EncodeCommand(command)])
        };
    }

    /// <summary>Base-64 UTF-16LE encodes a PowerShell command (avoids all quoting issues).</summary>
    private static string EncodeCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private static string BuildResult(string stdout, string stderr, int exitCode, int maxOutput)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(stdout))
            sb.Append(stdout.TrimEnd());

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("[STDERR]");
            sb.Append(stderr.TrimEnd());
        }

        if (sb.Length > 0) sb.AppendLine();
        sb.Append($"[Exit Code: {exitCode}]");

        var result = sb.ToString();
        return result.Length > maxOutput
            ? result[..maxOutput] + "\n…[output truncated]"
            : result;
    }

    private static string TruncateOutput(string text, int max) =>
        text.Length > max ? text[..max] + "\n…[truncated]" : text;
}
