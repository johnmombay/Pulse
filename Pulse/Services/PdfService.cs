using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pulse.Services;

/// <summary>
/// Generates styled A4 PDFs using QuestPDF (MIT / Community licence).
/// Markdown content is parsed with Markdig and walked into QuestPDF's fluent API.
/// </summary>
public sealed class PdfService : IPdfService
{
    // ── Colours ───────────────────────────────────────────────────────────────
    private static readonly string Blue        = "#0d6efd";
    private static readonly string LightBlue   = "#e7f1ff";
    private static readonly string BorderBlue  = "#b6d4fe";
    private static readonly string LightGrey   = "#f8f9fa";
    private static readonly string BorderGrey  = "#e9ecef";
    private static readonly string TextMuted   = "#6c757d";
    private static readonly string CodeBg      = "#f1f3f5";
    private static readonly string CodeBorder  = "#dee2e6";
    private static readonly string QuoteBorder = "#adb5bd";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // ── Public API ────────────────────────────────────────────────────────────

    public byte[] GenerateChatPdf(string sessionId, IReadOnlyList<DisplayMessage> messages)
    {
        var doc = new ChatDocument(sessionId, messages, DateTime.UtcNow, this);
        return doc.GeneratePdf();
    }

    public byte[] GenerateReportPdf(string title, string content)
    {
        var doc = new ReportDocument(title, content, DateTime.UtcNow, this);
        return doc.GeneratePdf();
    }

    // ── Chat document ─────────────────────────────────────────────────────────

    private sealed class ChatDocument(
        string sessionId,
        IReadOnlyList<DisplayMessage> messages,
        DateTime generatedAt,
        PdfService svc) : IDocument
    {
        public DocumentMetadata GetMetadata() => new()
        {
            Title       = $"Pulse Chat — {sessionId[..Math.Min(8, sessionId.Length)]}",
            Author      = "Pulse AI Agent",
            CreationDate = generatedAt
        };

        public DocumentSettings GetSettings() => DocumentSettings.Default;

        public void Compose(IDocumentContainer container)
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(2, Unit.Centimetre);
                page.MarginVertical(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Arial));

                page.Header().Element(ComposeHeader);
                page.Content().Element(ComposeContent);
                page.Footer().Element(ComposeFooter);
            });
        }

        private void ComposeHeader(IContainer c)
        {
            c.Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(inner =>
                    {
                        inner.Item().Text("Pulse AI Agent")
                             .FontSize(16).Bold().FontColor(Blue);
                        inner.Item().Text("Conversation Export")
                             .FontSize(10).FontColor(TextMuted);
                    });

                    row.ConstantItem(200).AlignRight().Column(inner =>
                    {
                        inner.Item().Text($"Session: {sessionId[..Math.Min(12, sessionId.Length)]}")
                             .FontSize(8).FontColor(TextMuted);
                        inner.Item().Text(generatedAt.ToLocalTime().ToString("MMMM d, yyyy  h:mm tt"))
                             .FontSize(8).FontColor(TextMuted);
                        inner.Item().Text($"{messages.Count} message(s)")
                             .FontSize(8).FontColor(TextMuted);
                    });
                });

                col.Item().PaddingTop(6).LineHorizontal(1).LineColor(BorderGrey);
            });
        }

        private void ComposeContent(IContainer c)
        {
            if (messages.Count == 0)
            {
                c.AlignCenter().AlignMiddle().Text("No messages in this session.")
                 .FontColor(TextMuted).Italic();
                return;
            }

            c.PaddingTop(10).Column(col =>
            {
                foreach (var msg in messages)
                {
                    col.Item().Element(item => RenderMessage(item, msg));
                    col.Item().PaddingBottom(10);
                }
            });
        }

        private void RenderMessage(IContainer c, DisplayMessage msg)
        {
            var isUser     = msg.Role == "user";
            var bgColour   = isUser ? LightBlue  : LightGrey;
            var border     = isUser ? BorderBlue : BorderGrey;
            var senderName = isUser ? "You"      : "Pulse AI";

            c.Column(col =>
            {
                // sender + timestamp
                col.Item().PaddingBottom(2).Row(row =>
                {
                    row.RelativeItem()
                       .Text(senderName)
                       .FontSize(9).Bold()
                       .FontColor(isUser ? Blue : TextMuted);

                    row.ConstantItem(70).AlignRight()
                       .Text(msg.Timestamp.ToLocalTime().ToString("h:mm tt"))
                       .FontSize(8).FontColor(QuoteBorder);
                });

                // bubble
                col.Item()
                   .Background(bgColour)
                   .Border(1).BorderColor(border)
                   .CornerRadius(4)
                   .Padding(8)
                   .Column(bubble =>
                   {
                       if (isUser)
                       {
                           bubble.Item().Text(msg.Content).FontSize(10);
                       }
                       else
                       {
                           var mdDoc = Markdown.Parse(msg.Content, Pipeline);
                           svc.RenderBlocks(bubble, mdDoc);
                       }
                   });
            });
        }

        private static void ComposeFooter(IContainer c)
        {
            c.Column(col =>
            {
                col.Item().PaddingBottom(4).LineHorizontal(1).LineColor(BorderGrey);
                col.Item().Row(row =>
                {
                    row.RelativeItem()
                       .Text("Pulse — AI Agent Conversation Export")
                       .FontSize(8).FontColor(TextMuted);

                    row.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.Span("Page ").FontSize(8).FontColor(TextMuted);
                        t.CurrentPageNumber().FontSize(8).FontColor(TextMuted);
                        t.Span(" of ").FontSize(8).FontColor(TextMuted);
                        t.TotalPages().FontSize(8).FontColor(TextMuted);
                    });
                });
            });
        }
    }

    // ── Report document ───────────────────────────────────────────────────────

    private sealed class ReportDocument(
        string title,
        string content,
        DateTime generatedAt,
        PdfService svc) : IDocument
    {
        public DocumentMetadata GetMetadata() => new()
        {
            Title        = title,
            Author       = "Pulse AI Agent",
            CreationDate = generatedAt
        };

        public DocumentSettings GetSettings() => DocumentSettings.Default;

        public void Compose(IDocumentContainer container)
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(2, Unit.Centimetre);
                page.MarginVertical(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Arial));

                page.Header().Element(ComposeHeader);
                page.Content().Element(ComposeContent);
                page.Footer().Element(ComposeFooter);
            });
        }

        private void ComposeHeader(IContainer c)
        {
            c.Column(col =>
            {
                col.Item()
                   .Background(Blue)
                   .Padding(12)
                   .Column(inner =>
                   {
                       inner.Item().Text("Pulse AI Agent Report")
                            .FontSize(11).FontColor(Colors.White).Bold();
                       inner.Item().PaddingTop(4).Text(title)
                            .FontSize(16).FontColor(Colors.White).Bold();
                       inner.Item().PaddingTop(4)
                            .Text($"Generated {generatedAt.ToLocalTime():MMMM d, yyyy  h:mm tt}")
                            .FontSize(8).FontColor("#cfe2ff");
                   });
            });
        }

        private void ComposeContent(IContainer c)
        {
            c.PaddingTop(14).Column(col =>
            {
                var mdDoc = Markdown.Parse(content, Pipeline);
                svc.RenderBlocks(col, mdDoc);
            });
        }

        private static void ComposeFooter(IContainer c)
        {
            c.Column(col =>
            {
                col.Item().PaddingBottom(4).LineHorizontal(1).LineColor(BorderGrey);
                col.Item().Row(row =>
                {
                    row.RelativeItem()
                       .Text("Pulse — AI Agent Report")
                       .FontSize(8).FontColor(TextMuted);

                    row.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.Span("Page ").FontSize(8).FontColor(TextMuted);
                        t.CurrentPageNumber().FontSize(8).FontColor(TextMuted);
                        t.Span(" of ").FontSize(8).FontColor(TextMuted);
                        t.TotalPages().FontSize(8).FontColor(TextMuted);
                    });
                });
            });
        }
    }

    // ── Markdown → QuestPDF renderer ─────────────────────────────────────────

    /// <summary>Renders a Markdig <see cref="ContainerBlock"/> into a QuestPDF column.</summary>
    internal void RenderBlocks(ColumnDescriptor col, ContainerBlock blocks)
    {
        foreach (var block in blocks)
            col.Item().Element(c => RenderBlock(c, block));
    }

    private void RenderBlock(IContainer c, Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
                var headingSize = h.Level switch { 1 => 18f, 2 => 15f, 3 => 13f, _ => 11f };
                c.PaddingBottom(4).PaddingTop(h.Level == 1 ? 8 : 4)
                 .Text(t =>
                 {
                     t.DefaultTextStyle(s => s.FontSize(headingSize).Bold());
                     RenderInlines(t, h.Inline);
                 });
                break;

            case ParagraphBlock p:
                c.PaddingBottom(4).Text(t => RenderInlines(t, p.Inline));
                break;

            case FencedCodeBlock fenced:
                var codeText = string.Join("\n", fenced.Lines.Lines
                    .Take(fenced.Lines.Count)
                    .Select(l => l.ToString()));
                c.PaddingBottom(6)
                 .Background(CodeBg)
                 .Border(1).BorderColor(CodeBorder)
                 .CornerRadius(3)
                 .Padding(8)
                 .Text(codeText)
                 .FontFamily("Courier New").FontSize(8.5f);
                break;

            case CodeBlock code:
                var indentedCode = string.Join("\n", code.Lines.Lines
                    .Take(code.Lines.Count)
                    .Select(l => l.ToString()));
                c.PaddingBottom(6)
                 .Background(CodeBg)
                 .Border(1).BorderColor(CodeBorder)
                 .CornerRadius(3)
                 .Padding(8)
                 .Text(indentedCode)
                 .FontFamily("Courier New").FontSize(8.5f);
                break;

            case QuoteBlock quote:
                c.PaddingBottom(6)
                 .Row(row =>
                 {
                     row.ConstantItem(3).Background(QuoteBorder);
                     row.RelativeItem()
                        .PaddingLeft(8)
                        .Column(col =>
                        {
                            col.Item().DefaultTextStyle(s => s.FontColor(TextMuted).Italic());
                            RenderBlocks(col, quote);
                        });
                 });
                break;

            case ListBlock list:
                c.PaddingBottom(4).Column(col =>
                {
                    var index = 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var prefix = list.IsOrdered ? $"{index++}." : "•";
                        col.Item().Row(row =>
                        {
                            row.ConstantItem(18).Text(prefix)
                               .FontColor(TextMuted).FontSize(10);
                            row.RelativeItem().Column(inner => RenderBlocks(inner, item));
                        });
                    }
                });
                break;

            case ThematicBreakBlock:
                c.PaddingVertical(6).LineHorizontal(1).LineColor(BorderGrey);
                break;

            case ContainerBlock nested:
                c.Column(col => RenderBlocks(col, nested));
                break;
        }
    }

    private static void RenderInlines(TextDescriptor t, ContainerInline? container,
        bool bold = false, bool italic = false)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    var span = t.Span(lit.Content.ToString());
                    if (bold)   span.Bold();
                    if (italic) span.Italic();
                    break;

                case EmphasisInline em:
                    var emBold   = em.DelimiterCount >= 2;
                    var emItalic = em.DelimiterCount == 1 || (em.DelimiterCount == 3);
                    RenderInlines(t, em, bold || emBold, italic || emItalic);
                    break;

                case CodeInline code:
                    t.Span($" {code.Content} ")
                     .FontFamily("Courier New")
                     .FontSize(8.5f)
                     .BackgroundColor(CodeBg);
                    break;

                case LineBreakInline lb when lb.IsHard:
                    t.Span("\n");
                    break;

                case LinkInline link:
                    // Render link text; URL shown in parentheses when no display text
                    if (link.FirstChild is LiteralInline linkText)
                        t.Span(linkText.Content.ToString()).FontColor(Blue);
                    else if (link.Url is not null)
                        t.Span(link.Url).FontColor(Blue);
                    break;

                case ContainerInline nested:
                    RenderInlines(t, nested, bold, italic);
                    break;
            }
        }
    }
}
