namespace Pulse.Services;

/// <summary>Generates Microsoft Word (.docx) documents from chat histories and Markdown content.</summary>
public interface IWordService
{
    /// <summary>
    /// Renders a full chat conversation as a styled Word document and returns the raw bytes.
    /// </summary>
    byte[] GenerateChatDocx(string sessionId, IReadOnlyList<DisplayMessage> messages);

    /// <summary>
    /// Renders arbitrary Markdown <paramref name="content"/> as a titled Word report
    /// and returns the raw bytes.
    /// </summary>
    byte[] GenerateReportDocx(string title, string content);
}
