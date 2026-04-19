using Pulse.Models;

namespace Pulse.Data.Entities;

public class FlatFileSourceEntity
{
    public string          Id                 { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string          Label              { get; set; } = "";
    public FlatFileFormat  Format             { get; set; } = FlatFileFormat.Csv;
    public string          FilePath           { get; set; } = "";
    public bool            IsEnabled          { get; set; } = true;
    public bool            HasHeaders         { get; set; } = true;
    public string?         Delimiter          { get; set; }
    public string?         SheetName          { get; set; }
    public string          Encoding           { get; set; } = "UTF-8";
    public int             MaxRows            { get; set; } = 1_000;

    /// <summary>JSON-serialised <c>List&lt;string&gt;</c>.</summary>
    public string          FixedWidthColumnsJson { get; set; } = "[]";
}
