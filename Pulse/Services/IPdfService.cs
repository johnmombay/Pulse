using Pulse.Services;

namespace Pulse.Services;

/// <summary>Generates PDF documents from chat histories and Markdown content.</summary>
public interface IPdfService
{
    /// <summary>
    /// Renders a full chat conversation (all display messages for <paramref name="sessionId"/>)
    /// as a styled A4 PDF and returns the raw bytes.
    /// </summary>
    byte[] GenerateChatPdf(string sessionId, IReadOnlyList<DisplayMessage> messages);

    /// <summary>
    /// Renders arbitrary Markdown <paramref name="content"/> as a titled A4 PDF report
    /// and returns the raw bytes.
    /// </summary>
    byte[] GenerateReportPdf(string title, string content);
}
