using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Pulse.Services;

/// <summary>
/// Generates Microsoft Word (.docx) documents using DocumentFormat.OpenXml.
/// Markdown is parsed with Markdig and the AST is walked into native Word paragraphs,
/// runs, and properties — producing a document that is fully editable in Word.
/// </summary>
public sealed class WordService : IWordService
{
    // ── Colours (OpenXML hex — no # prefix) ──────────────────────────────────
    private const string ColBlue      = "0D6EFD";
    private const string ColBlueDark  = "0A4FB4";
    private const string ColBlueBg    = "E7F1FF";
    private const string ColBlueBdr   = "B6D4FE";
    private const string ColGreyBg    = "F8F9FA";
    private const string ColGreyBdr   = "DEE2E6";
    private const string ColMuted     = "6C757D";
    private const string ColCodeBg    = "F1F3F5";
    private const string ColCodeBdr   = "DEE2E6";
    private const string ColQuoteBdr  = "ADB5BD";
    private const string ColWhite     = "FFFFFF";

    // ── Twips helpers (1 twip = 1/1440 inch) ─────────────────────────────────
    // A4: 11906 × 16838 twips   Margin 2 cm ≈ 1134 twips
    private const string PageW    = "11906";
    private const string PageH    = "16838";
    private const string MarginSz = "1134";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // ── Public API ────────────────────────────────────────────────────────────

    public byte[] GenerateChatDocx(string sessionId, IReadOnlyList<DisplayMessage> messages)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            var body = InitDocument(main);

            // ── Cover block ───────────────────────────────────────────────────
            body.Append(MakePara("Pulse AI Agent", bold: true, size: 36, colour: ColBlue));
            body.Append(MakePara("Conversation Export", colour: ColMuted, size: 22));
            body.Append(MakePara(
                $"Session: {sessionId[..Math.Min(12, sessionId.Length)]}   •   " +
                $"{DateTime.UtcNow.ToLocalTime():MMMM d, yyyy  h:mm tt}   •   {messages.Count} message(s)",
                size: 18, colour: ColMuted));
            body.Append(HorizontalRule());

            // ── Messages ──────────────────────────────────────────────────────
            foreach (var msg in messages)
            {
                var isUser = msg.Role == "user";

                // Role header
                body.Append(MakePara(
                    $"{(isUser ? "You" : "Pulse AI")}   {msg.Timestamp.ToLocalTime():h:mm tt}",
                    bold: true, size: 18,
                    colour: isUser ? ColBlue : ColMuted,
                    spaceBefore: 120));

                if (isUser)
                {
                    // Plain user text in a shaded paragraph
                    body.Append(MakeShaded(msg.Content, isUser: true));
                }
                else
                {
                    // Markdown-rendered AI response
                    var mdDoc = Markdown.Parse(msg.Content, Pipeline);
                    foreach (var el in RenderBlocks(mdDoc, aiShade: true))
                        body.Append(el);
                }

                body.Append(HorizontalRule(colour: ColGreyBdr));
            }

            AddPageSetup(body);
            AddFooter(main, "Pulse — AI Agent Conversation Export");
            main.Document?.Save();
        }
        return stream.ToArray();
    }

    public byte[] GenerateReportDocx(string title, string content)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            var body = InitDocument(main);

            // ── Title block ───────────────────────────────────────────────────
            body.Append(MakeColorBar());
            body.Append(MakePara("Pulse AI Agent Report",
                bold: true, size: 22, colour: ColBlueDark, spaceBefore: 160));
            body.Append(MakePara(title, bold: true, size: 36, colour: ColBlueDark));
            body.Append(MakePara(
                $"Generated {DateTime.UtcNow.ToLocalTime():MMMM d, yyyy  h:mm tt}",
                size: 18, colour: ColMuted, spaceAfter: 240));
            body.Append(HorizontalRule());

            // ── Markdown content ──────────────────────────────────────────────
            var mdDoc = Markdown.Parse(content, Pipeline);
            foreach (var el in RenderBlocks(mdDoc))
                body.Append(el);

            AddPageSetup(body);
            AddFooter(main, "Pulse — AI Agent Report");
            main.Document?.Save();
        }
        return stream.ToArray();
    }

    // ── Document scaffolding ──────────────────────────────────────────────────

    private static Body InitDocument(MainDocumentPart main)
    {
        main.Document = new Document();
        return main.Document.AppendChild(new Body());
    }

    private static void AddPageSetup(Body body)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new SectionProperties(
                    new PageSize { Width  = UInt32Value.FromUInt32(11906),
                                   Height = UInt32Value.FromUInt32(16838) },
                    new PageMargin { Top    = 1134, Bottom = 1134,
                                     Left   = UInt32Value.FromUInt32(1134),
                                     Right  = UInt32Value.FromUInt32(1134) }))));
    }

    // ── Styles ────────────────────────────────────────────────────────────────

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            MakeStyle("Normal",   "Normal",   isDefault: true),
            MakeStyle("Heading1", "heading 1", baseStyle: "Normal",
                bold: true, size: 36, colour: ColBlueDark),
            MakeStyle("Heading2", "heading 2", baseStyle: "Normal",
                bold: true, size: 28, colour: ColBlueDark),
            MakeStyle("Heading3", "heading 3", baseStyle: "Normal",
                bold: true, size: 24, colour: ColBlueDark),
            MakeStyle("Heading4", "heading 4", baseStyle: "Normal",
                bold: true, size: 22, colour: ColMuted),
            MakeStyle("CodeBlock", "Code Block", baseStyle: "Normal",
                font: "Courier New", size: 17, shade: ColCodeBg),
            MakeStyle("ListParagraph", "List Paragraph", baseStyle: "Normal",
                indent: "360")
        );
        part.Styles.Save();
    }

    private static Style MakeStyle(
        string styleId, string name,
        bool isDefault = false,
        string? baseStyle = null,
        bool bold = false,
        int size = 20,
        string? colour = null,
        string? font = null,
        string? shade = null,
        string? indent = null)
    {
        var rpr = new StyleRunProperties();
        if (bold)            rpr.Append(new Bold());
        if (colour != null)  rpr.Append(new Color { Val = colour });
        if (font != null)    rpr.Append(new RunFonts { Ascii = font, HighAnsi = font });
        rpr.Append(new FontSize { Val = size.ToString() });

        var ppr = new StyleParagraphProperties();
        if (shade != null)
            ppr.Append(new Shading { Val = ShadingPatternValues.Clear,
                                     Fill = shade, Color = "auto" });
        if (indent != null)
            ppr.Append(new Indentation { Left = indent });

        var style = new Style
        {
            Type    = StyleValues.Paragraph,
            StyleId = styleId,
            Default = isDefault ? true : null,
        };
        style.Append(new StyleName { Val = name });
        if (baseStyle != null)
            style.Append(new BasedOn { Val = baseStyle });
        if (ppr.HasChildren) style.Append(ppr);
        style.Append(rpr);
        return style;
    }

    // ── Footer with page numbers ──────────────────────────────────────────────

    private static void AddFooter(MainDocumentPart main, string leftText)
    {
        var footerPart = main.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(
            new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Normal" },
                    new Justification { Val = JustificationValues.Right }),
                MuteRun(leftText + "   Page "),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" PAGE  \\* MERGEFORMAT ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                MuteRun("1"),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
                MuteRun(" of "),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" NUMPAGES  \\* MERGEFORMAT ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                MuteRun("1"),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End })));

        footerPart.Footer.Save();
        var footerId = main.GetIdOfPart(footerPart);

        // Attach footer to the document section
        var body = main.Document?.Body!;
        var sectPr = body.Elements<SectionProperties>().LastOrDefault()
                     ?? body.AppendChild(new SectionProperties());
        sectPr.Append(new FooterReference
            { Type = HeaderFooterValues.Default, Id = footerId });
    }

    // ── Markdown → OpenXML ────────────────────────────────────────────────────

    private static IEnumerable<OpenXmlElement> RenderBlocks(
        ContainerBlock blocks, bool aiShade = false)
    {
        foreach (var block in blocks)
        foreach (var el in RenderBlock(block, aiShade))
            yield return el;
    }

    private static IEnumerable<OpenXmlElement> RenderBlock(Block block, bool aiShade)
    {
        switch (block)
        {
            // ── Heading ───────────────────────────────────────────────────────
            case HeadingBlock h:
            {
                var styleId = h.Level switch { 1 => "Heading1", 2 => "Heading2",
                                               3 => "Heading3", _ => "Heading4" };
                var sz = h.Level switch { 1 => 36, 2 => 28, 3 => 24, _ => 22 };

                var ppr = new ParagraphProperties(
                    new ParagraphStyleId { Val = styleId },
                    new SpacingBetweenLines { Before = "120", After = "60" });
                var para = new Paragraph(ppr);
                foreach (var run in InlineRuns(h.Inline, bold: true, size: sz, colour: ColBlueDark))
                    para.Append(run);
                yield return para;
                break;
            }

            // ── Paragraph ─────────────────────────────────────────────────────
            case ParagraphBlock p:
            {
                var ppr = new ParagraphProperties(
                    new SpacingBetweenLines { After = "100" });
                if (aiShade)
                    ppr.Append(new Shading { Val = ShadingPatternValues.Clear,
                                             Fill = ColGreyBg, Color = "auto" });
                var para = new Paragraph(ppr);
                foreach (var run in InlineRuns(p.Inline))
                    para.Append(run);
                yield return para;
                break;
            }

            // ── Fenced / Indented code block ──────────────────────────────────
            case FencedCodeBlock fenced:
                yield return MakeCodeBlock(
                    string.Join("\n", fenced.Lines.Lines.Take(fenced.Lines.Count)));
                break;

            case CodeBlock code:
                yield return MakeCodeBlock(
                    string.Join("\n", code.Lines.Lines.Take(code.Lines.Count)));
                break;

            // ── Blockquote ────────────────────────────────────────────────────
            case QuoteBlock quote:
                foreach (var inner in RenderBlocks(quote))
                {
                    if (inner is Paragraph qPara)
                    {
                        var qPpr = qPara.ParagraphProperties ?? qPara.PrependChild(new ParagraphProperties());
                        qPpr.Append(new Indentation { Left = "720" });
                        qPpr.Append(new ParagraphBorders(
                            new LeftBorder { Val = BorderValues.Single,
                                             Color = ColQuoteBdr, Size = 12, Space = 12 }));
                        // Italicise all runs
                        foreach (var r in qPara.Elements<Run>())
                            (r.RunProperties ??= new RunProperties()).Append(new Italic());
                    }
                    yield return inner;
                }
                break;

            // ── List ──────────────────────────────────────────────────────────
            case ListBlock list:
            {
                var idx = 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var prefix = list.IsOrdered ? $"{idx++}." : "•";
                    // render first child paragraph with prefix, remainder indented
                    bool first = true;
                    foreach (var child in RenderBlocks(item))
                    {
                        if (first && child is Paragraph lPara)
                        {
                            first = false;
                            var lPpr = lPara.ParagraphProperties
                                       ?? lPara.PrependChild(new ParagraphProperties());
                            lPpr.Append(new Indentation { Left = "360", Hanging = "360" });
                            lPpr.Append(new SpacingBetweenLines { After = "60" });
                            lPara.PrependChild(new Run(
                                new RunProperties(new Color { Val = ColMuted }),
                                new Text(prefix + "\t") { Space = SpaceProcessingModeValues.Preserve }));
                        }
                        yield return child;
                    }
                }
                break;
            }

            // ── Thematic break ────────────────────────────────────────────────
            case ThematicBreakBlock:
                yield return HorizontalRule();
                break;

            // ── Nested container ──────────────────────────────────────────────
            case ContainerBlock nested:
                foreach (var el in RenderBlocks(nested, aiShade))
                    yield return el;
                break;
        }
    }

    // ── Inline rendering ──────────────────────────────────────────────────────

    private static IEnumerable<Run> InlineRuns(
        ContainerInline? container,
        bool bold = false, bool italic = false,
        int? size = null, string? colour = null)
    {
        if (container is null) yield break;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                {
                    var rpr = BuildRpr(bold, italic, size, colour);
                    yield return new Run(rpr,
                        new Text(lit.Content.ToString()) { Space = SpaceProcessingModeValues.Preserve });
                    break;
                }

                case EmphasisInline em:
                    foreach (var r in InlineRuns(em,
                        bold   || em.DelimiterCount >= 2,
                        italic || em.DelimiterCount == 1,
                        size, colour))
                        yield return r;
                    break;

                case CodeInline code:
                {
                    var rpr = new RunProperties(
                        new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
                        new FontSize { Val = "17" },
                        new Shading { Val = ShadingPatternValues.Clear,
                                      Fill = ColCodeBg, Color = "auto" });
                    yield return new Run(rpr,
                        new Text($"\u00A0{code.Content}\u00A0") { Space = SpaceProcessingModeValues.Preserve });
                    break;
                }

                case LineBreakInline lb when lb.IsHard:
                    yield return new Run(new Break());
                    break;

                case LinkInline link:
                {
                    // Render the link text (URL shown in parentheses when no children)
                    var rpr = BuildRpr(bold, italic, size, ColBlue, underline: true);
                    var text = link.FirstChild is LiteralInline lt
                        ? lt.Content.ToString()
                        : link.Url ?? "";
                    yield return new Run(rpr,
                        new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                    break;
                }

                case ContainerInline nested:
                    foreach (var r in InlineRuns(nested, bold, italic, size, colour))
                        yield return r;
                    break;
            }
        }
    }

    private static RunProperties BuildRpr(
        bool bold, bool italic, int? size, string? colour, bool underline = false)
    {
        var rpr = new RunProperties();
        if (bold)       rpr.Append(new Bold());
        if (italic)     rpr.Append(new Italic());
        if (underline)  rpr.Append(new Underline { Val = UnderlineValues.Single });
        if (size.HasValue) rpr.Append(new FontSize { Val = size.Value.ToString() });
        if (colour != null) rpr.Append(new Color { Val = colour });
        return rpr;
    }

    // ── Element factories ─────────────────────────────────────────────────────

    private static Paragraph MakePara(
        string text,
        bool bold = false, int size = 20, string? colour = null,
        int spaceBefore = 0, int spaceAfter = 80)
    {
        var rpr = BuildRpr(bold, false, size, colour);
        var ppr = new ParagraphProperties(
            new SpacingBetweenLines
            {
                Before = spaceBefore > 0 ? spaceBefore.ToString() : null,
                After  = spaceAfter.ToString()
            });
        return new Paragraph(ppr,
            new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph MakeShaded(string text, bool isUser)
    {
        var fill  = isUser ? ColBlueBg  : ColGreyBg;
        var bdr   = isUser ? ColBlueBdr : ColGreyBdr;
        return new Paragraph(
            new ParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = fill, Color = "auto" },
                new ParagraphBorders(
                    new TopBorder    { Val = BorderValues.Single, Color = bdr, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Color = bdr, Size = 4 },
                    new LeftBorder   { Val = BorderValues.Single, Color = bdr, Size = 4 },
                    new RightBorder  { Val = BorderValues.Single, Color = bdr, Size = 4 }),
                new Indentation { Left = "80", Right = "80" },
                new SpacingBetweenLines { After = "80" }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph MakeCodeBlock(string text)
    {
        return new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "CodeBlock" },
                new ParagraphBorders(
                    new TopBorder    { Val = BorderValues.Single, Color = ColCodeBdr, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Color = ColCodeBdr, Size = 4 },
                    new LeftBorder   { Val = BorderValues.Single, Color = ColCodeBdr, Size = 4 },
                    new RightBorder  { Val = BorderValues.Single, Color = ColCodeBdr, Size = 4 }),
                new Indentation { Left = "120", Right = "120" },
                new SpacingBetweenLines { Before = "80", After = "80" }),
            new Run(
                new RunProperties(
                    new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
                    new FontSize { Val = "17" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph HorizontalRule(string colour = ColBlueDark)
    {
        return new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(
                    new BottomBorder { Val = BorderValues.Single,
                                       Color = colour, Size = 6 }),
                new SpacingBetweenLines { Before = "80", After = "80" }));
    }

    /// <summary>A solid blue bar used as a decorative title-page header.</summary>
    private static Paragraph MakeColorBar()
    {
        return new Paragraph(
            new ParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = ColBlue, Color = "auto" },
                new SpacingBetweenLines { Before = "0", After = "0" }),
            new Run(
                new RunProperties(new Color { Val = ColWhite }, new FontSize { Val = "12" }),
                new Text("\u00A0") { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Run MuteRun(string text) =>
        new(new RunProperties(new Color { Val = ColMuted }, new FontSize { Val = "16" }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve });
}
