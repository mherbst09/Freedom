using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ViennaAtomicClock;

/// <summary>
/// Stores a rich note page as XAML text. Pictures travel as base64 in the image's Tag, because the
/// XAML writer cannot save in-memory pictures itself.
/// </summary>
internal static class NoteDocument
{
    public const double BodySize = 15;

    public static string Serialize(FlowDocument document)
    {
        var images = Images(document).ToList();
        var sources = images.Select(i => i.Source).ToList();
        try
        {
            foreach (var image in images) image.Source = null;
            return XamlWriter.Save(document);
        }
        finally
        {
            for (var i = 0; i < images.Count; i++) images[i].Source = sources[i];
        }
    }

    public static FlowDocument Deserialize(string? xaml, string fallbackText)
    {
        if (!string.IsNullOrWhiteSpace(xaml))
        {
            try
            {
                if (XamlReader.Parse(xaml) is FlowDocument document)
                {
                    foreach (var image in Images(document)) image.Source = ImageFromTag(image.Tag as string);
                    return document;
                }
            }
            catch
            {
                // A damaged page falls back to its plain text below.
            }
        }
        return FromPlain(fallbackText);
    }

    /// <summary>Older notes were plain text: every line becomes a paragraph.</summary>
    public static FlowDocument FromPlain(string text)
    {
        var document = new FlowDocument();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return document;
    }

    public static FlowDocument Clone(FlowDocument document) => Deserialize(Serialize(document), "");

    public static IEnumerable<Image> Images(FlowDocument document)
    {
        foreach (var block in AllBlocks(document.Blocks))
        {
            if (block is BlockUIContainer { Child: Image blockImage }) yield return blockImage;
            if (block is Paragraph paragraph)
                foreach (var inline in AllInlines(paragraph.Inlines))
                    if (inline is InlineUIContainer { Child: Image inlineImage }) yield return inlineImage;
        }
    }

    public static IEnumerable<Block> AllBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var children = block switch
            {
                Section section => AllBlocks(section.Blocks),
                List list => list.ListItems.SelectMany(item => AllBlocks(item.Blocks)),
                Table table => table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => AllBlocks(c.Blocks)),
                _ => Enumerable.Empty<Block>()
            };
            foreach (var child in children) yield return child;
        }
    }

    public static IEnumerable<Inline> AllInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in AllInlines(span.Inlines)) yield return child;
        }
    }

    /// <summary>Reads a picture file; very large photos are scaled down so notes stay small.</summary>
    public static Image? LoadImage(byte[] bytes)
    {
        var source = Decode(bytes);
        if (source is null) return null;
        if (source.PixelWidth > 1600 || bytes.Length > 3_000_000)
        {
            var scale = Math.Min(1, 1600.0 / source.PixelWidth);
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            bytes = stream.ToArray();
            source = Decode(bytes)!;
        }
        return Wrap(source, Convert.ToBase64String(bytes));
    }

    public static Image FromBitmap(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return LoadImage(stream.ToArray()) ?? Wrap(bitmap, Convert.ToBase64String(stream.ToArray()));
    }

    private static Image Wrap(BitmapSource source, string base64) => new()
    {
        Source = source, Tag = base64, Stretch = Stretch.Uniform,
        Width = Math.Min(520, source.PixelWidth), HorizontalAlignment = HorizontalAlignment.Left
    };

    private static BitmapSource? ImageFromTag(string? base64)
    {
        try { return base64 is null ? null : Decode(Convert.FromBase64String(base64)); }
        catch { return null; }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Readable plain text: list items get bullets or numbers, table cells are separated by tabs.</summary>
    public static string PlainText(FlowDocument document)
    {
        var text = new StringBuilder();
        WriteBlocks(document.Blocks, text, "");
        return text.ToString().TrimEnd();
    }

    private static void WriteBlocks(IEnumerable<Block> blocks, StringBuilder text, string indent)
    {
        foreach (var block in blocks)
            switch (block)
            {
                case Paragraph paragraph:
                    text.Append(indent).AppendLine(InlineText(paragraph.Inlines));
                    break;
                case List list:
                    var number = list.StartIndex;
                    foreach (var item in list.ListItems)
                    {
                        var marker = list.MarkerStyle is TextMarkerStyle.Decimal ? $"{number++}. " : "• ";
                        var inner = new StringBuilder();
                        WriteBlocks(item.Blocks, inner, "");
                        var lines = inner.ToString().TrimEnd().Split(Environment.NewLine);
                        for (var i = 0; i < lines.Length; i++)
                            text.Append(indent).Append(i == 0 ? marker : "   ").AppendLine(lines[i]);
                    }
                    break;
                case Table table:
                    foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
                        text.Append(indent).AppendLine(string.Join("\t", row.Cells.Select(c =>
                        {
                            var cell = new StringBuilder();
                            WriteBlocks(c.Blocks, cell, "");
                            return cell.ToString().Trim().Replace(Environment.NewLine, " ");
                        })));
                    break;
                case Section section:
                    WriteBlocks(section.Blocks, text, indent);
                    break;
                case BlockUIContainer:
                    text.Append(indent).AppendLine("[" + Loc.T("fmt.picture") + "]");
                    break;
            }
    }

    private static string InlineText(InlineCollection inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
            switch (inline)
            {
                case Run run: text.Append(run.Text); break;
                case LineBreak: text.Append('\n'); break;
                case Span span: text.Append(InlineText(span.Inlines)); break;
                case InlineUIContainer: text.Append('[').Append(Loc.T("fmt.picture")).Append(']'); break;
            }
        return text.ToString();
    }
}

/// <summary>Exports a note page as .txt, .docx (Office Open XML, written by hand) or .pdf (rendered pages).</summary>
internal static class NoteExport
{
    public static void Text(string path, string title, FlowDocument document) =>
        File.WriteAllText(path, (title.Length > 0 ? title + Environment.NewLine + new string('=', Math.Min(60, title.Length)) + Environment.NewLine + Environment.NewLine : "") +
                                NoteDocument.PlainText(document) + Environment.NewLine, new UTF8Encoding(true));

    /// <summary>Copy of the page for paper: black text on white, A4, with the title on top.</summary>
    public static FlowDocument PrintCopy(string title, DateTime created, FlowDocument document)
    {
        var copy = NoteDocument.Clone(document);
        copy.Foreground = Brushes.Black;
        copy.Background = Brushes.White;
        copy.FontFamily = new FontFamily("Segoe UI");
        copy.FontSize = NoteDocument.BodySize;
        copy.PageWidth = 793.7;
        copy.PageHeight = 1122.5;
        copy.PagePadding = new Thickness(72, 64, 72, 64);
        copy.ColumnWidth = double.PositiveInfinity;
        copy.TextAlignment = TextAlignment.Left;
        copy.Resources.Add(typeof(Paragraph), NotesPage.ParagraphStyle());
        copy.Resources.Add(typeof(Table), NotesPage.TableStyle());
        // White text from a dark theme would vanish on paper.
        foreach (var run in copy.Blocks.SelectMany(b => NoteDocument.AllBlocks(new[] { b })).OfType<Paragraph>().SelectMany(p => NoteDocument.AllInlines(p.Inlines)))
            if (run.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush { Color: var c } && c.R + c.G + c.B > 690)
                run.ClearValue(TextElement.ForegroundProperty);

        var heading = new Paragraph(new Run(title.Length > 0 ? title : Loc.T("notes.untitled"))) { FontSize = 28, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
        var date = new Paragraph(new Run(created.ToString("D", Loc.Culture) + "  " + created.ToString("t", Loc.Culture))) { FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 18) };
        if (copy.Blocks.FirstBlock is { } first)
        {
            copy.Blocks.InsertBefore(first, heading);
            copy.Blocks.InsertAfter(heading, date);
        }
        else
        {
            copy.Blocks.Add(heading);
            copy.Blocks.Add(date);
        }
        return copy;
    }

    // ---------- PDF ----------

    /// <summary>
    /// Writes a PDF with one sharp picture per A4 page (192 dpi). Every script and colour looks exactly
    /// like in the app, without needing a PDF library or embedded fonts.
    /// </summary>
    public static void Pdf(string path, FlowDocument printCopy)
    {
        var paginator = ((IDocumentPaginatorSource)printCopy).DocumentPaginator;
        paginator.PageSize = new Size(printCopy.PageWidth, printCopy.PageHeight);
        paginator.ComputePageCount();
        const double dpi = 192;
        var pages = new List<(int W, int H, byte[] Data)>();
        for (var i = 0; i < paginator.PageCount; i++)
        {
            var page = paginator.GetPage(i);
            var w = (int)Math.Round(printCopy.PageWidth * dpi / 96);
            var h = (int)Math.Round(printCopy.PageHeight * dpi / 96);
            var paper = new DrawingVisual();
            using (var dc = paper.RenderOpen())
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, printCopy.PageWidth, printCopy.PageHeight));
            var bitmap = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(paper);
            bitmap.Render(page.Visual);
            var bgra = new byte[w * h * 4];
            bitmap.CopyPixels(bgra, w * 4, 0);
            var rgb = new byte[w * h * 3];
            for (int s = 0, d = 0; s < bgra.Length; s += 4, d += 3)
            {
                rgb[d] = bgra[s + 2];
                rgb[d + 1] = bgra[s + 1];
                rgb[d + 2] = bgra[s];
            }
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rgb);
            pages.Add((w, h, packed.ToArray()));
        }

        using var file = File.Create(path);
        var offsets = new List<long>();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); file.Write(b); }
        void Begin() => offsets.Add(file.Position);

        Write("%PDF-1.4\n%âãÏÓ\n");
        // 1: catalog, 2: page tree, then per page: page, content, image.
        var kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));
        Begin(); Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(); Write($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");
        const string width = "595.28", height = "841.89";
        for (var i = 0; i < pages.Count; i++)
        {
            int pageId = 3 + i * 3, contentId = pageId + 1, imageId = pageId + 2;
            Begin();
            Write($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im{i} {imageId} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");
            var content = $"q {width} 0 0 {height} 0 0 cm /Im{i} Do Q\n";
            Begin();
            Write($"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
            Begin();
            Write($"{imageId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {pages[i].W} /Height {pages[i].H} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {pages[i].Data.Length} >>\nstream\n");
            file.Write(pages[i].Data);
            Write("\nendstream\nendobj\n");
        }
        var xref = file.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // ---------- Word ----------

    private sealed class DocxWriter
    {
        public readonly StringBuilder Body = new();
        public readonly List<byte[]> Media = new();
        private readonly Color _defaultText;

        public DocxWriter(Color defaultText) => _defaultText = defaultText;

        public void Blocks(IEnumerable<Block> blocks, int level)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph paragraph:
                        ParagraphXml(paragraph, level, null);
                        break;
                    case List list:
                        var number = list.StartIndex;
                        foreach (var item in list.ListItems)
                        {
                            var marker = list.MarkerStyle is TextMarkerStyle.Decimal ? $"{number++}." : "•";
                            var first = true;
                            foreach (var inner in item.Blocks)
                            {
                                if (inner is Paragraph p && first) ParagraphXml(p, level + 1, marker);
                                else Blocks(new[] { inner }, level + 1);
                                first = false;
                            }
                        }
                        break;
                    case Table table:
                        TableXml(table);
                        break;
                    case Section section:
                        Blocks(section.Blocks, level);
                        break;
                    case BlockUIContainer { Child: Image image }:
                        Body.Append("<w:p>").Append(ImageRun(image)).Append("</w:p>");
                        break;
                }
        }

        private void ParagraphXml(Paragraph paragraph, int level, string? marker)
        {
            Body.Append("<w:p><w:pPr><w:spacing w:after=\"80\"/>");
            if (level > 0) Body.Append($"<w:ind w:left=\"{level * 360}\" w:hanging=\"{(marker is null ? 0 : 280)}\"/>");
            var align = paragraph.TextAlignment switch { TextAlignment.Center => "center", TextAlignment.Right => "right", TextAlignment.Justify => "both", _ => null };
            if (align is not null) Body.Append($"<w:jc w:val=\"{align}\"/>");
            Body.Append("</w:pPr>");
            if (marker is not null) Body.Append($"<w:r><w:t xml:space=\"preserve\">{Escape(marker)} </w:t></w:r>");
            foreach (var inline in paragraph.Inlines) InlineXml(inline);
            Body.Append("</w:p>");
        }

        private void InlineXml(Inline inline)
        {
            switch (inline)
            {
                case Run run:
                    Body.Append("<w:r>").Append(RunProperties(run)).Append($"<w:t xml:space=\"preserve\">{Escape(run.Text)}</w:t></w:r>");
                    break;
                case LineBreak:
                    Body.Append("<w:r><w:br/></w:r>");
                    break;
                case Span span:
                    foreach (var child in span.Inlines) InlineXml(child);
                    break;
                case InlineUIContainer { Child: Image image }:
                    Body.Append(ImageRun(image));
                    break;
            }
        }

        private string RunProperties(Run run)
        {
            var props = new StringBuilder("<w:rPr>");
            if (run.FontWeight.ToOpenTypeWeight() >= 600) props.Append("<w:b/>");
            if (run.FontStyle == FontStyles.Italic) props.Append("<w:i/>");
            var decorations = Decorations(run);
            if (decorations.Any(d => d.Location == TextDecorationLocation.Underline)) props.Append("<w:u w:val=\"single\"/>");
            if (decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough)) props.Append("<w:strike/>");
            if (run.Foreground is SolidColorBrush { Color: var fg } && fg != _defaultText) props.Append($"<w:color w:val=\"{Hex(fg)}\"/>");
            if (Highlight(run) is { } bg) props.Append($"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{Hex(bg)}\"/>");
            props.Append($"<w:sz w:val=\"{(int)Math.Round(run.FontSize * 1.5)}\"/>");
            return props.Append("</w:rPr>").ToString();
        }

        private static IEnumerable<TextDecoration> Decorations(Inline inline)
        {
            for (DependencyObject? node = inline; node is Inline current; node = current.Parent)
                if (current.TextDecorations is { } decorations)
                    foreach (var decoration in decorations) yield return decoration;
        }

        private static Color? Highlight(Inline inline)
        {
            for (DependencyObject? node = inline; node is Inline current; node = current.Parent)
                if (current.Background is SolidColorBrush { Color: var c } && c.A > 0) return c;
            return null;
        }

        private void TableXml(Table table)
        {
            var columns = Math.Max(1, table.RowGroups.SelectMany(g => g.Rows).Select(r => r.Cells.Count).DefaultIfEmpty(1).Max());
            Body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
            foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                Body.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:color=\"A0A0A0\"/>");
            Body.Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"100\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
            for (var i = 0; i < columns; i++) Body.Append($"<w:gridCol w:w=\"{9000 / columns}\"/>");
            Body.Append("</w:tblGrid>");
            foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
            {
                Body.Append("<w:tr>");
                foreach (var cell in row.Cells)
                {
                    Body.Append("<w:tc><w:tcPr><w:tcW w:w=\"0\" w:type=\"auto\"/></w:tcPr>");
                    var before = Body.Length;
                    Blocks(cell.Blocks, 0);
                    if (Body.Length == before) Body.Append("<w:p/>");
                    Body.Append("</w:tc>");
                }
                Body.Append("</w:tr>");
            }
            Body.Append("</w:tbl><w:p/>");
        }

        private string ImageRun(Image image)
        {
            if (image.Source is not BitmapSource bitmap) return "";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            Media.Add(stream.ToArray());
            var n = Media.Count;
            var width = double.IsNaN(image.Width) ? Math.Min(520, bitmap.PixelWidth) : image.Width;
            var height = width * bitmap.PixelHeight / Math.Max(1, bitmap.PixelWidth);
            long cx = (long)(width * 9525), cy = (long)(height * 9525);
            return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                   $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{n}\" name=\"Bild {n}\"/>" +
                   "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                   "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                   $"<pic:nvPicPr><pic:cNvPr id=\"{n}\" name=\"image{n}.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                   $"<pic:blipFill><a:blip r:embed=\"rIdImg{n}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                   $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                   "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
        }

        private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static void Word(string path, FlowDocument printCopy)
    {
        var writer = new DocxWriter(Colors.Black);
        writer.Blocks(printCopy.Blocks, 0);

        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                       $"<w:document xmlns:w=\"{w}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                       "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                       "<w:body>" + writer.Body +
                       "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
                       "</w:body></w:document>";
        var styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                     $"<w:styles xmlns:w=\"{w}\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\" w:eastAsia=\"Yu Gothic\" w:cs=\"Segoe UI\"/>" +
                     "<w:sz w:val=\"22\"/><w:lang w:val=\"de-AT\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults></w:styles>";
        var relations = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                                          "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        for (var i = 1; i <= writer.Media.Count; i++)
            relations.Append($"<Relationship Id=\"rIdImg{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{i}.png\"/>");
        relations.Append("</Relationships>");

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var entry = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            entry.Write(text);
        }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                                   "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                                   "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                                   "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                           "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", document);
        Add("word/styles.xml", styles);
        Add("word/_rels/document.xml.rels", relations.ToString());
        for (var i = 0; i < writer.Media.Count; i++)
        {
            using var entry = zip.CreateEntry($"word/media/image{i + 1}.png", CompressionLevel.NoCompression).Open();
            entry.Write(writer.Media[i]);
        }
    }

    private static string Escape(string text) =>
        SecurityElement.Escape(new string(text.Select(c => c == '\t' ? ' ' : c).Where(c => c >= ' ').ToArray())) ?? "";
}
