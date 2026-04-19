namespace Pulse.Models;

/// <summary>
/// Controls the agent's ability to execute shell commands on the host machine.
/// Disabled by default — must be explicitly enabled in Settings.
/// </summary>
public class TerminalSettings
{
    public bool   IsEnabled       { get; set; } = false;
    /// <summary>powershell | cmd | bash</summary>
    public string DefaultShell    { get; set; } = "powershell";
    /// <summary>Absolute path; empty = ContentRootPath.</summary>
    public string WorkingDirectory { get; set; } = "";
    public int    TimeoutSeconds   { get; set; } = 30;
    public int    MaxOutputLength  { get; set; } = 8000;
}
