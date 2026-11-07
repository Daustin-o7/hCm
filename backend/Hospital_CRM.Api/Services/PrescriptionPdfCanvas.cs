using System.Globalization;
using System.Text;
using QRCoder;
using SkiaSharp;
using Topten.RichTextKit;

namespace Hospital_CRM.Api.Services;

/// <summary>English/Latin and Hindi/Devanagari prescription layout with embedded fonts.</summary>
internal sealed class PrescriptionPdfCanvas : IDisposable
{
    private const float PageHeight = 841.89f;
    private static readonly BundledFontMapper Fonts = new();
    private readonly MemoryStream _output = new();
    private readonly SKDocument _document;
    private SKCanvas _canvas;
    public int PageCount { get; private set; } = 1;

    public PrescriptionPdfCanvas()
    {
        _document = SKDocument.CreatePdf(_output);
        _canvas = _document.BeginPage(595.28f, PageHeight);
    }

    public void NewPage()
    {
        _document.EndPage();
        _canvas = _document.BeginPage(595.28f, PageHeight);
        PageCount++;
    }

    public TextBlock Layout(string text, float size = 9, bool bold = false, float? width = 515)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        // Throw on malformed UTF-16 before EnumerateRunes can replace it with U+FFFD.
        _ = new UTF8Encoding(false, true).GetByteCount(text);
        var block = new TextBlock { FontMapper = Fonts, MaxWidth = width, Alignment = TextAlignment.Left };
        using var latin = new SKFont(Fonts.Faces[bold ? 2 : 0]);
        using var devanagari = new SKFont(Fonts.Faces[1]);
        var pending = new StringBuilder();
        var current = bold ? 2 : 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var whitespace = rune.Value is '\r' or '\n' or '\t';
            var joiner = rune.Value is 0x200c or 0x200d;
            if (Rune.IsControl(rune) && !whitespace
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format && !joiner)
                throw new EncoderFallbackException("Prescription contains an unsupported control character.");
            // First-clinic scope: Latin letters/accents, shared punctuation/currency, and Devanagari.
            var latinOrCommon = rune.Value is <= 0x024f or >= 0x0300 and <= 0x036f
                or >= 0x1e00 and <= 0x1eff or >= 0x2000 and <= 0x206f or >= 0x20a0 and <= 0x20cf;
            var selected = joiner ? current : whitespace || latinOrCommon && latin.GetGlyph(rune.Value) != 0 ? (bold ? 2 : 0)
                : rune.Value is >= 0x0900 and <= 0x097f && devanagari.GetGlyph(rune.Value) != 0 ? 1 : -1;
            if (selected < 0)
                throw new EncoderFallbackException($"Prescription English/Hindi fonts do not support U+{rune.Value:X4}.");
            if (selected != current && pending.Length > 0)
            {
                block.AddText(pending.ToString(), new Style { FontFamily = current.ToString(CultureInfo.InvariantCulture), FontSize = size });
                pending.Clear();
            }
            current = selected;
            pending.Append(rune);
        }
        if (pending.Length > 0)
            block.AddText(pending.ToString(), new Style { FontFamily = current.ToString(CultureInfo.InvariantCulture), FontSize = size });
        block.Layout();
        // Never accept an OS fallback font or a missing shaped glyph.
        if (block.FontRuns.Any(run => !Fonts.Faces.Contains(run.Typeface)
            || run.RunKind != FontRunKind.TrailingWhitespace && run.Glyphs.Any(glyph => glyph == 0)))
            throw new EncoderFallbackException("Prescription text could not be shaped with the bundled fonts.");
        return block;
    }

    public void DrawTextLine(TextLine line, float x, float top, SKColor? color = null)
    {
        foreach (var run in line.Runs)
        {
            if (run.Glyphs.Length == 0 || run.RunKind == FontRunKind.TrailingWhitespace) continue;
            var text = Encoding.UTF8.GetBytes(string.Concat(run.CodePoints.Select(char.ConvertFromUtf32)));
            using var font = new SKFont(run.Typeface, run.Style.FontSize);
            using var builder = new SKTextBlobBuilder();
            var buffer = builder.AllocateRawPositionedTextRun(font, run.Glyphs.Length, text.Length);
            run.Glyphs.AsSpan().CopyTo(buffer.Glyphs);
            run.GlyphPositions.AsSpan().CopyTo(buffer.Positions);
            text.CopyTo(buffer.Text);
            // One ActualText span per run preserves the original Hindi text behind shaped ligatures.
            buffer.Clusters.Clear();
            using var blob = builder.Build();
            using var paint = new SKPaint { Color = color ?? SKColors.Black, IsAntialias = true };
            _canvas.DrawText(blob, x, PageHeight - top - line.YCoord, paint);
        }
    }

    public void DrawText(string text, float x, float y, float size = 10, bool bold = false)
    {
        var block = Layout(text, size, bold, width: null);
        try
        {
            foreach (var line in block.Lines) DrawTextLine(line, x, y + line.BaseLine - line.YCoord);
        }
        finally { block.Clear(); }
    }

    public void DrawRightAlignedText(string text, float right, float y, float size)
    {
        var block = Layout(text, size, width: null);
        try
        {
            foreach (var line in block.Lines) DrawTextLine(line, right - block.MeasuredWidth, y + line.BaseLine);
        }
        finally { block.Clear(); }
    }

    public void DrawWatermark(string text)
    {
        var block = Layout(text, 42, true, width: null);
        try
        {
            _canvas.Save();
            _canvas.Translate(120, PageHeight - 400);
            _canvas.RotateDegrees(-45);
            foreach (var line in block.Lines) DrawTextLine(line, 0, PageHeight + line.BaseLine, new SKColor(230, 230, 230));
            _canvas.Restore();
        }
        finally { block.Clear(); }
    }

    public void DrawLine(float x1, float y1, float x2, float y2, float r, float g, float b)
    {
        using var paint = new SKPaint { Color = new SKColor((byte)(255 * r), (byte)(255 * g), (byte)(255 * b)), StrokeWidth = 1 };
        _canvas.DrawLine(x1, PageHeight - y1, x2, PageHeight - y2, paint);
    }

    public void DrawQrCode(string text, float x, float y, float size)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var paint = new SKPaint { Color = SKColors.White };
        _canvas.DrawRect(x - 3, PageHeight - y - size - 3, size + 6, size + 6, paint);
        paint.Color = SKColors.Black;
        var unit = size / data.ModuleMatrix.Count;
        for (var row = 0; row < data.ModuleMatrix.Count; row++)
            for (var col = 0; col < data.ModuleMatrix.Count; col++)
                if (data.ModuleMatrix[row][col])
                    _canvas.DrawRect(x + col * unit, PageHeight - y - size + row * unit, unit, unit, paint);
    }

    public byte[] ToPdfBytes()
    {
        _document.EndPage();
        _document.Close();
        return _output.ToArray();
    }

    public void Dispose()
    {
        _document.Dispose();
        _output.Dispose();
    }

    private sealed class BundledFontMapper : FontMapper
    {
        public SKTypeface[] Faces { get; } = new[] { "NotoSans-Regular", "NotoSansDevanagari-Regular", "NotoSans-Bold" }
            .Select(name =>
            {
                using var stream = typeof(PrescriptionPdfCanvas).Assembly.GetManifestResourceStream($"Hospital_CRM.Api.Assets.Fonts.{name}.ttf")
                    ?? throw new InvalidOperationException($"Bundled prescription font {name} is missing.");
                return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"Bundled prescription font {name} is invalid.");
            }).ToArray();

        public override SKTypeface TypefaceFromStyle(IStyle style, bool ignoreFontVariants) => Faces[int.Parse(style.FontFamily, CultureInfo.InvariantCulture)];
    }
}
