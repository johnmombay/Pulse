namespace Pulse.Infrastructure;

public enum DeploymentMode
{
    MultiTenant,
    SingleTenant
}

/// <summary>
/// Bound from the "Deployment" section of appsettings.json.
/// Controls whether the app runs as a multi-tenant SaaS platform or
/// an enterprise single-tenant standalone instance.
/// </summary>
public class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>
    /// "MultiTenant" (default) or "SingleTenant".
    /// </summary>
    public DeploymentMode Mode { get; set; } = DeploymentMode.MultiTenant;

    public bool IsMultiTenant  => Mode == DeploymentMode.MultiTenant;
    public bool IsSingleTenant => Mode == DeploymentMode.SingleTenant;
}
