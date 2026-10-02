using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Notepal.Api.Ocr;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WordParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;

namespace Notepal.Api.Processing;

/// <summary>Turns a stored artifact into text: images go to the OCR agent, documents are parsed locally.</summary>
public sealed class TextExtractionService(IOcrClient ocr, ILogger<TextExtractionService> logger)
{
    /// <summary>PDF pages with less extracted text than this are treated as scanned and sent to OCR.</summary>
    private const int MinimumDigitalTextLength = 20;
    private const int MaxOcrPdfPages = 50;

    public async Task<string> ExtractAsync(byte[] data, string contentType, string fileName, CancellationToken cancellationToken)
    {
        if (FileTypes.IsImage(contentType))
        {
            return await ocr.ExtractTextAsync(data, contentType, fileName, cancellationToken);
        }

        return contentType switch
        {
            FileTypes.Pdf => await ExtractPdfAsync(data, fileName, cancellationToken),
            FileTypes.Docx => ExtractDocx(data),
            _ => throw new NotSupportedException($"Unsupported content type '{contentType}'."),
        };
    }

    private async Task<string> ExtractPdfAsync(byte[] data, string fileName, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(data);
        var builder = new StringBuilder();
        var ocrPages = 0;

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).Trim();

            if (text.Length < MinimumDigitalTextLength && ocrPages < MaxOcrPdfPages)
            {
                // Scanned page: OCR the largest embedded image (usually the full-page scan).
                var image = page.GetImages().OrderByDescending(i => i.WidthInSamples * i.HeightInSamples).FirstOrDefault();
                if (image is not null)
                {
                    byte[]? bytes = null;
                    var imageType = "image/png";
                    if (image.TryGetPng(out var png))
                    {
                        bytes = png;
                    }
                    else if (image.RawBytes.Length > 3 && image.RawBytes[0] == 0xFF && image.RawBytes[1] == 0xD8)
                    {
                        bytes = image.RawBytes.ToArray();
                        imageType = "image/jpeg";
                    }

                    if (bytes is not null)
                    {
                        ocrPages++;
                        logger.LogDebug("OCR-ing scanned page {Page} of {FileName}", page.Number, fileName);
                        var ocrText = await ocr.ExtractTextAsync(bytes, imageType, fileName, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(ocrText))
                        {
                            text = ocrText.Trim();
                        }
                    }
                }
            }

            if (document.NumberOfPages > 1)
            {
                builder.AppendLine($"--- Page {page.Number} ---");
            }

            builder.AppendLine(text).AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string ExtractDocx(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var paragraph in body.Descendants<WordParagraph>())
        {
            builder.AppendLine(paragraph.InnerText);
        }

        return builder.ToString().Trim();
    }
}
