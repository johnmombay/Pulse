namespace Pulse.Models;

/// <summary>
/// Controls the agent's ability to search the web via DuckDuckGo.
/// No API key required — uses DuckDuckGo's free public APIs.
/// </summary>
public class WebSearchSettings
{
    public bool IsEnabled   { get; set; } = false;
    public int  MaxResults  { get; set; } = 5;
}
