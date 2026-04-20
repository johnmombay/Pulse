namespace Pulse.Data.Entities;

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
