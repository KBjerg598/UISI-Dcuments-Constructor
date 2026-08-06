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
            .ToList();
    }

    public int RenameField(string documentPath, string fieldTag, string displayName)
    {
        var normalizedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Введите название поля.", nameof(displayName));
        }

        using var document = WordprocessingDocument.Open(documentPath, true);
        var renamedCount = 0;

        foreach (var root in EnumerateRoots(document))
        {
            var matchingControls = root
                .Descendants<SdtElement>()
                .Where(control => string.Equals(GetTag(control), fieldTag, StringComparison.Ordinal))
                .ToList();

            foreach (var control in matchingControls)
            {
                var properties = control.GetFirstChild<SdtProperties>();
                if (properties is null)
                {
                    properties = new SdtProperties();
                    control.PrependChild(properties);
                }

                var alias = properties.GetFirstChild<SdtAlias>();
                if (alias is null)
                {
                    properties.AppendChild(new SdtAlias { Val = normalizedName });
                }
                else
                {
                    alias.Val = normalizedName;
                }

                renamedCount++;
            }

            if (matchingControls.Count > 0)
            {
                root.Save();
            }
        }

        if (renamedCount == 0)
        {
            throw new InvalidOperationException(
                "Поле больше не найдено в документе. Обновите список полей и повторите попытку.");
        }

        return renamedCount;
    }

    public int RemoveField(string documentPath, string fieldTag)
    {
        using var document = WordprocessingDocument.Open(documentPath, true);
        var removedCount = 0;

        foreach (var root in EnumerateRoots(document))
        {
            var matchingControls = root
                .Descendants<SdtElement>()
                .Where(control => string.Equals(GetTag(control), fieldTag, StringComparison.Ordinal))
                .OrderByDescending(control => control.Ancestors().Count())
                .ToList();

            foreach (var control in matchingControls)
            {
                UnwrapContentControl(
                    control,
                    "Одно из полей имеет повреждённую структуру и не может быть удалено.");
                removedCount++;
            }

            if (matchingControls.Count > 0)
            {
                root.Save();
            }
        }

        if (removedCount == 0)
        {
            throw new InvalidOperationException(
                "Поле больше не найдено в документе. Обновите список полей и повторите попытку.");
        }

        return removedCount;
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
            var controls = root
                .Descendants<SdtElement>()
                .Where(control =>
                {
                    var tag = GetTag(control);
                    return tag is not null && values.ContainsKey(tag);
                })
                .OrderByDescending(control => control.Ancestors().Count())
                .ToList();

            foreach (var control in controls)
            {
                var tag = GetTag(control)!;
                var value = values[tag];

                ReplaceText(control, value);
                UnwrapContentControl(
                    control,
                    "Одно из полей имеет повреждённую структуру и не может быть преобразовано в обычный текст.");
                replacementCount++;
            }

            root.Save();
        }

        return replacementCount;
    }

    private static void UnwrapContentControl(SdtElement control, string invalidStructureMessage)
    {
        var content = control.ChildElements.FirstOrDefault(
            element => string.Equals(
                element.LocalName,
                "sdtContent",
                StringComparison.Ordinal));
        var parent = control.Parent;

        if (content is null || parent is null)
        {
            throw new InvalidDataException(invalidStructureMessage);
        }

        foreach (var child in content.ChildElements.ToList())
        {
            parent.InsertBefore(child.CloneNode(true), control);
        }

        control.Remove();
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
