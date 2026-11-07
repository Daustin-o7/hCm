using UglyToad.PdfPig;

namespace Hospital_CRM.Tests;

internal static class PdfTestReader
{
    public static string ReadText(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseActualText = true });
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }
}
