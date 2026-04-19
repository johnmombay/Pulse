namespace Pulse.Services;

/// <summary>Generates Microsoft Excel (.xlsx) workbooks.</summary>
public interface IExcelService
{
    /// <summary>
    /// Exports a full chat conversation as a structured Excel workbook.
    /// Produces a single "Conversation" sheet with columns:
    /// No. | Timestamp | Role | Message.
    /// </summary>
    byte[] GenerateChatExcel(string sessionId, IReadOnlyList<DisplayMessage> messages);

    /// <summary>
    /// Converts a JSON array (e.g. from <c>db_execute_query</c>) into a formatted
    /// Excel table with auto-filter, frozen header, and auto-fit columns.
    /// </summary>
    /// <param name="title">Workbook title written to document properties.</param>
    /// <param name="sheetName">Name for the data sheet (max 31 chars).</param>
    /// <param name="jsonData">
    /// JSON array of flat objects, e.g.
    /// <c>[{"id":1,"name":"Alice"},{"id":2,"name":"Bob"}]</c>.
    /// </param>
    byte[] GenerateDataExcel(string title, string sheetName, string jsonData);

    /// <summary>
    /// Parses all Markdown pipe-tables in <paramref name="markdownContent"/> and
    /// creates one Excel sheet per table, preserving header rows and data types.
    /// </summary>
    byte[] GenerateMarkdownTablesExcel(string title, string markdownContent);
}
