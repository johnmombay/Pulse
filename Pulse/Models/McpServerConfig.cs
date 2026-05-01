namespace Pulse.Models;

public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>"http" (SSE / Streamable HTTP) or "stdio"</summary>
    public string TransportType { get; set; } = "http";

    /// <summary>Required when TransportType is "http".</summary>
    public string? Url { get; set; }

    /// <summary>Required when TransportType is "stdio".</summary>
    public string? Command { get; set; }

    /// <summary>Space-separated arguments passed to the stdio process.</summary>
    public string? Arguments { get; set; }

    /// <summary>Optional Bearer token sent as Authorization header (HTTP transport only).</summary>
    public string? ApiKey { get; set; }

    public bool IsEnabled { get; set; } = true;
}
