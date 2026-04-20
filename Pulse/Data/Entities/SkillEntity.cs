namespace Pulse.Data.Entities;

public class SkillEntity : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Id           { get; set; } = Guid.NewGuid().ToString("N");
    public string Name         { get; set; } = "";
    public string Icon         { get; set; } = "⚡";
    public string Description  { get; set; } = "";
    public string Instructions { get; set; } = "";
    public bool   IsActive     { get; set; } = true;
}
