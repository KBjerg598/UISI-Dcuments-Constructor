using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UISI_Doc_Constructor.Models;

namespace UISI_Doc_Constructor.Services;

public sealed class OpenXmlDocumentService
{
    private const string FieldTagPrefix = "uisi-field:";

    public IReadOnlyList<TemplateField> ReadFields(string documentPath)
    {
        using var document = WordprocessingDocument.Open(documentPath, false);

        return EnumerateRoots(document)
            .SelectMany(root => root.Descendants<SdtElement>())
            .Select(ReadField)
            .Where(field => field is not null)
            .Cast<TemplateField>()
            .GroupBy(field => field.Tag, StringComparer.Ordinal)
            .Select(group => new TemplateField
            {
                Tag = group.Key,
                DisplayName = group
                    .Select(field => field.DisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? "Без названия",
                Occurrences = group.Count()
            })
            .OrderBy(field => field.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public int GenerateDocument(
        string templatePath,
        string destinationPath,
        IReadOnlyDictionary<string, string> values)
    {
        File.Copy(templatePath, destinationPath, overwrite: true);

        using var document = WordprocessingDocument.Open(destinationPath, true);
        var replacementCount = 0;

        foreach (var root in EnumerateRoots(document))
        {
            var controls = root.Descendants<SdtElement>().ToList();

            foreach (var control in controls)
            {
                var tag = GetTag(control);
                if (tag is null || !values.TryGetValue(tag, out var value))
                {
                    continue;
                }

                ReplaceText(control, value);
                replacementCount++;
            }

            root.Save();
        }

        return replacementCount;
    }

    private static TemplateField? ReadField(SdtElement control)
    {
        var tag = GetTag(control);
        if (tag is null)
        {
            return null;
        }

        var properties = control.GetFirstChild<SdtProperties>();
        var displayName = properties?
            .GetFirstChild<SdtAlias>()?
            .Val?
            .Value;

        return new TemplateField
        {
            Tag = tag,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Без названия" : displayName,
            Occurrences = 1
        };
    }

    private static string? GetTag(SdtElement control)
    {
        var tag = control
            .GetFirstChild<SdtProperties>()?
            .GetFirstChild<Tag>()?
            .Val?
            .Value;

        return tag is not null && tag.StartsWith(FieldTagPrefix, StringComparison.Ordinal)
            ? tag
            : null;
    }

    private static void ReplaceText(SdtElement control, string value)
    {
        var textElements = control.Descendants<Text>().ToList();
        if (textElements.Count == 0)
        {
            throw new InvalidDataException(
                "В шаблоне найдено поле без текстового содержимого. Пересоздайте это поле в Word.");
        }

        textElements[0].Text = value;
        textElements[0].Space = SpaceProcessingModeValues.Preserve;

        foreach (var extraText in textElements.Skip(1))
        {
            extraText.Text = string.Empty;
        }
    }

    private static IEnumerable<OpenXmlPartRootElement> EnumerateRoots(
        WordprocessingDocument document)
    {
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("В DOCX отсутствует основная часть документа.");

        if (mainPart.Document is not null)
        {
            yield return mainPart.Document;
        }

        foreach (var headerPart in mainPart.HeaderParts)
        {
            if (headerPart.Header is not null)
            {
                yield return headerPart.Header;
            }
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            if (footerPart.Footer is not null)
            {
                yield return footerPart.Footer;
            }
        }
    }
}
