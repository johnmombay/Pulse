namespace Pulse.Data.Entities;

public class McpServerEntity : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string  Id            { get; set; } = Guid.NewGuid().ToString("N");
    public string  Name          { get; set; } = "";
    public string  TransportType { get; set; } = "http";
    public string? Url           { get; set; }
    public string? Command       { get; set; }
    public string? Arguments     { get; set; }
    public bool    IsEnabled     { get; set; } = true;
}
