using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using UISI_Doc_Constructor.Models;
using UISI_Doc_Constructor.Services;

internal static class Program
{
    private const string SourceTag = "uisi-field:source";
    private const string TargetTag = "uisi-field:target";
    private static readonly OpenXmlDocumentService Service = new();

    [STAThread]
    private static void Main(string[] args)
    {
        var testRoot = Path.Combine(AppContext.BaseDirectory, "DocumentRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        TestSelectionRanges();
        TestLegacyFields(testRoot);
        TestAuthorDocumentWithoutFrames(testRoot);
        TestAmbiguousFields(testRoot);
        TestLegacyTranslationPackage(testRoot);
        EditableTranslationTests.Run(testRoot);
        TranslationLayoutTests.Run(testRoot);
        var templateIndex = Array.IndexOf(args, "--template");
        if (templateIndex >= 0) TestExistingTemplate(args[templateIndex + 1], testRoot);
        Console.WriteLine("All document regression checks passed: duplicate nesting, formatting, identifiers, backups, headers/footers, partial translation, atomic output, and overlapping selection ranges.");
    }

    private static void TestSelectionRanges()
    {
        foreach (var (start, end) in new[] { (11, 19), (10, 20), (5, 25), (5, 15), (15, 25) })
            Assert(ContentControlSelectionGuard.Overlaps(start, end, 10, 20), "contained, enclosing or partial overlap is rejected");
        foreach (var (start, end) in new[] { (0, 10), (20, 30), (3, 7), (25, 28), (15, 15) })
            Assert(!ContentControlSelectionGuard.Overlaps(start, end, 10, 20), "adjacent, separate or empty selection does not overlap");
    }

    private static void TestLegacyFields(string root)
    {
        var source = Path.Combine(root, "legacy.docx");
        using (var document = WordprocessingDocument.Create(source, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(Field(SourceTag, 1, new Run(new RunProperties(new Bold()), new Text("Український оригінал")))),
                new Paragraph(Field(TargetTag, 2,
                    Field(TargetTag, 3, Field(TargetTag, 4,
                        new Run(new RunProperties(new Italic()), new Text("English placeholder")))),
                    new Run(new Text(" tail")))),
                new Paragraph(Field(TargetTag, 5, new Run(new Text("Another occurrence"))))));
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(Field(TargetTag, 6,
                Field(TargetTag, 7, new Run(new RunProperties(new Bold()), new Text("Header"))))));
            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new Footer(new Paragraph(Field(SourceTag, 8, new Run(new Text("Footer")))));
            main.Document.Body!.AppendChild(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) }));
        }
        var sourceHash = Hash(source);
        Assert(Service.ReadFields(source).Single(field => field.Tag == TargetTag).Occurrences == 3,
            "nested duplicates are not counted as separate occurrences");
        var author = Path.Combine(root, "author.docx");
        Assert(Service.GenerateDocument(source, author, new Dictionary<string, string> { [SourceTag] = "Заповнено" }) == 2,
            "author values are filled in body and footer");
        using (var document = WordprocessingDocument.Open(author, false))
        {
            var remaining = Roots(document).SelectMany(part => part.Descendants<SdtElement>()).ToList();
            Assert(remaining.Count == 3 && remaining.All(control => TagOf(control) == TargetTag), "only English fields remain");
            Assert(remaining.All(control => !control.Descendants<SdtElement>().Any()), "duplicate English nesting removed");
            Assert(remaining.All(control => control.GetFirstChild<SdtProperties>()!.GetFirstChild<SdtContentText>() is null),
                "remaining legacy fields are rich text");
            Assert(document.MainDocumentPart!.Document!.InnerText.Contains("English placeholder tail"), "unfilled English text preserved");
            Assert(document.MainDocumentPart!.Document!.Descendants<Italic>().Any(), "unfilled English formatting preserved");
            Assert(!new OpenXmlValidator().Validate(document).Any(), "partial document passes Open XML validation");
        }
        var final = Path.Combine(root, "final.docx");
        Assert(Service.GenerateDocument(author, final, new Dictionary<string, string> { [TargetTag] = "Translated" }) == 3,
            "translator fills every independent English occurrence");
        using (var document = WordprocessingDocument.Open(final, false))
        {
            Assert(Roots(document).All(part => !part.Descendants<SdtElement>().Any()), "final document has no UISI frames");
            Assert(document.MainDocumentPart!.Document!.Descendants<Italic>().Any(), "final English text retains formatting");
            Assert(!new OpenXmlValidator().Validate(document).Any(), "final document passes Open XML validation");
        }
        Assert(Hash(source) == sourceHash, "generation does not modify the template");

        var repaired = Path.Combine(root, "stored.docx");
        File.Copy(source, repaired);
        var before = Describe(repaired);
        Assert(Service.RepairTemplateFields(repaired) > 0, "legacy template repair reports changes");
        var after = Describe(repaired);
        Assert(before.Text == after.Text && before.RunProperties == after.RunProperties, "repair preserves text and formatting exactly");
        Assert(Service.ReadFields(repaired).Single(field => field.Tag == TargetTag).Occurrences == 3, "stable field identifiers survive repair");
        var backup = Directory.GetFiles(root, "stored.docx.fields-backup-*.bak").Single();
        Assert(Hash(backup) == sourceHash, "backup is byte-for-byte original");
        var repairedHash = Hash(repaired);
        Assert(Service.RepairTemplateFields(repaired) == 0 && Hash(repaired) == repairedHash, "repair is idempotent");
        Assert(Directory.GetFiles(root, "stored.docx.fields-backup-*.bak").Length == 1, "no redundant backup when nothing changes");

        var imported = new TemplateStorageService(Path.Combine(root, "ImportedLibrary")).Import(source, "Legacy test");
        Assert(imported.Name == "Legacy test" && Hash(source) == sourceHash, "import preserves original source");
        var directOutput = Path.Combine(root, "direct-final.docx");
        Assert(Service.GenerateDocument(source, directOutput, new Dictionary<string, string>
            { [SourceTag] = "Source", [TargetTag] = "Target" }) == 5, "all values replace each physical occurrence once");
        Assert(!Directory.GetFiles(root, "*.tmp").Any(), "temporary files are cleaned up");
    }

    private static void TestAuthorDocumentWithoutFrames(string root)
    {
        const string sameValueTag = "uisi-field:same-value-number";
        const string unrelatedTag = "external-form:keep";
        var template = Path.Combine(root, "author-export-template.docx");
        File.Copy(Path.Combine(root, "legacy.docx"), template);
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var body = document.MainDocumentPart!.Document!.Body!;
            var section = body.GetFirstChild<SectionProperties>()!;
            body.InsertBefore(new Table(new TableProperties(), new TableGrid(new GridColumn(), new GridColumn()),
                new TableRow(
                    new TableCell(new Paragraph(Field(SourceTag, 30, new Run(new Text("Український текст"))))),
                    new TableCell(new Paragraph(Field(TargetTag, 31,
                        new Run(new RunProperties(new Italic()), new Text("Existing English value"))))))), section);
            body.InsertBefore(new Paragraph(Field(sameValueTag, 32, new Run(new Text("Contract number")))), section);
            body.InsertBefore(new Paragraph(Field(unrelatedTag, 33, new Run(new Text("Independent Word form")))), section);
        }
        var originalHash = Hash(template);
        var output = Path.Combine(root, "author-export-without-frames.docx");
        var replacements = Service.GenerateDocument(template, output, new Dictionary<string, string>
        {
            [SourceTag] = "Заповнено автором",
            [sameValueTag] = "26-156"
        }, flattenUnfilledFields: true);
        Assert(replacements == 4, "author export counts filled occurrences, not removed English frames");
        using (var document = WordprocessingDocument.Open(output, false))
        {
            var roots = Roots(document).ToList();
            var controls = roots.SelectMany(part => part.Descendants<SdtElement>()).ToList();
            Assert(controls.All(control => TagOf(control)?.StartsWith("uisi-field:", StringComparison.Ordinal) != true),
                "ordinary DOCX export removes both Ukrainian and unfilled English field frames");
            Assert(controls.Count == 1 && TagOf(controls[0]) == unrelatedTag, "unrelated Word forms are preserved");
            var main = document.MainDocumentPart!;
            Assert(main.Document!.InnerText.Contains("English placeholder tail") &&
                main.Document.InnerText.Contains("Another occurrence") &&
                main.Document.InnerText.Contains("Existing English value"), "English text remains unchanged when removing its frames");
            Assert(main.Document.InnerText.Contains("26-156"), "same-value English fields keep their filled value");
            Assert(main.Document.Descendants<Italic>().Count() == 2, "English character formatting is preserved");
            Assert(main.HeaderParts.Single().Header!.InnerText == "Header", "English header text remains after frame removal");
            Assert(main.FooterParts.Single().Footer!.InnerText == "Заповнено автором", "Ukrainian footer is filled without a frame");
            Assert(!new OpenXmlValidator().Validate(document).Any(), "ordinary bilingual DOCX export is valid Open XML");
        }
        Assert(Hash(template) == originalHash, "ordinary export leaves the template and its field anchors untouched");
        Assert(Service.ReadFields(template).Single(field => field.Tag == TargetTag).Occurrences == 4,
            "all English template fields remain available for future translation packages");
        Console.WriteLine("Ordinary DOCX export: both languages have no UISI frames; English text, formatting, tables, headers/footers and the template are preserved.");
    }

    private static void TestAmbiguousFields(string root)
    {
        var source = Path.Combine(root, "ambiguous.docx");
        using (var document = WordprocessingDocument.Create(source, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(Field(SourceTag, 10,
                Field(TargetTag, 11, new Run(new Text("Do not lose this value")))))));
        }
        var before = Hash(source);
        ExpectRejected(() => Service.RepairTemplateFields(source));
        Assert(Hash(source) == before, "ambiguous template is unchanged");
        var destination = Path.Combine(root, "existing-output.docx");
        File.Copy(source, destination);
        var destinationHash = Hash(destination);
        ExpectRejected(() => Service.GenerateDocument(source, destination, new Dictionary<string, string> { [SourceTag] = "Other" }));
        Assert(Hash(destination) == destinationHash, "failed generation preserves existing output");
        Assert(!Directory.GetFiles(root, "ambiguous.docx.fields-backup-*.bak").Any(), "rejected repair creates no backup");
        Assert(!Directory.GetFiles(root, "*.tmp").Any(), "failed operations leave no temporary files");
        try { Service.GenerateDocument(source, source, new Dictionary<string, string>()); throw new Exception("Same path was accepted."); }
        catch (InvalidOperationException) { }
        Assert(Hash(source) == before, "template cannot be overwritten by generated output");
    }

    private static void TestExistingTemplate(string source, string root)
    {
        var originalHash = Hash(source);
        var repaired = Path.Combine(root, "existing-template-copy.docx");
        File.Copy(source, repaired);
        var before = Describe(repaired);
        var changes = Service.RepairTemplateFields(repaired);
        var after = Describe(repaired);
        Assert(before.Text == after.Text && before.RunProperties == after.RunProperties, "existing template text and formatting preserved");
        var fields = Service.ReadFields(repaired);
        Assert(fields.Count > 0, "existing template still contains fields");
        using (var document = WordprocessingDocument.Open(repaired, false))
            Assert(Roots(document).SelectMany(part => part.Descendants<SdtElement>()).All(control =>
                !control.Ancestors<SdtElement>().Any(ancestor => TagOf(ancestor) == TagOf(control) && TagOf(control) is not null)),
                "existing template has no redundant nested identifiers after repair");
        var result = Path.Combine(root, "existing-template-result.docx");
        Service.GenerateDocument(repaired, result, fields.ToDictionary(field => field.Tag, _ => "Regression check value"));
        using (var document = WordprocessingDocument.Open(result, false))
            Assert(Roots(document).SelectMany(part => part.Descendants<SdtElement>()).All(control =>
                TagOf(control)?.StartsWith("uisi-field:", StringComparison.Ordinal) != true), "existing template output has no UISI controls");
        Assert(Hash(source) == originalHash, "personal template was only read, never modified");
        Console.WriteLine($"Existing template copy verified: {fields.Count} stable fields, {changes} structural repairs, original unchanged.");
    }

    private static void TestLegacyTranslationPackage(string root)
    {
        var packageService = new TranslationPackageService(sessionRoot: Path.Combine(root, "Sessions"));
        var package = Path.Combine(root, "new.uisi-translation");
        packageService.CreatePackage(new DocumentTemplate
        {
            Id = Guid.NewGuid(), Name = "Synthetic bilingual test", HasTranslation = true,
            TranslationFields =
            [
                new() { Tag = SourceTag, Role = TranslationFieldRole.Source },
                new() { Tag = TargetTag, Role = TranslationFieldRole.Target, SourceFieldTag = SourceTag }
            ]
        }, Path.Combine(root, "legacy.docx"),
        [
            new() { Tag = SourceTag, DisplayName = "Source", Value = "Автор" },
            new() { Tag = TargetTag, DisplayName = "Target" }
        ], package);
        var workingPath = Path.Combine(root, "legacy-working.docx");
        TranslationPackageManifest manifest;
        using (var archive = ZipFile.OpenRead(package))
        {
            using var stream = archive.GetEntry("manifest.json")!.Open();
            manifest = JsonSerializer.Deserialize<TranslationPackageManifest>(stream)!;
            archive.GetEntry("working.docx")!.ExtractToFile(workingPath);
        }
        // Old packages flattened their Ukrainian fields before being handed to the translator.
        var flattenedPath = Path.Combine(root, "legacy-flattened.docx");
        Service.GenerateDocument(workingPath, flattenedPath, new Dictionary<string, string> { [SourceTag] = "Автор" });
        File.Move(flattenedPath, workingPath, overwrite: true);
        manifest.FormatVersion = 1;
        using (var document = WordprocessingDocument.Open(workingPath, true))
        {
            var control = document.MainDocumentPart!.Document!.Descendants<SdtRun>().First(control => TagOf(control) == TargetTag);
            var properties = control.GetFirstChild<SdtProperties>()!;
            properties.ReplaceChild(new SdtContentText(), properties.GetFirstChild<SdtContentRichText>()!);
            var duplicate = (SdtRun)control.CloneNode(true);
            duplicate.GetFirstChild<SdtProperties>()!.GetFirstChild<SdtId>()!.Val = 999;
            control.SdtContentRun!.RemoveAllChildren();
            control.SdtContentRun.AppendChild(duplicate);
        }
        manifest.WorkingDocumentSha256 = Hash(workingPath);
        var legacyPackage = Path.Combine(root, "old.uisi-translation");
        using (var archive = ZipFile.Open(legacyPackage, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, manifest);
            archive.CreateEntryFromFile(workingPath, "working.docx");
        }
        var packageHash = Hash(legacyPackage);
        using var session = packageService.OpenPackage(legacyPackage);
        using (var document = WordprocessingDocument.Open(session.WorkingDocumentPath, false))
            Assert(Roots(document).SelectMany(part => part.Descendants<SdtElement>()).All(control =>
                !control.Descendants<SdtElement>().Any()), "legacy translation package repaired after checksum validation");
        var values = packageService.CreateTargetItems(session);
        Assert(values.All(value => value.IsSourceReadOnly && !value.CanEditSource), "old package sources remain read-only");
        foreach (var field in values) field.Value = "Translation";
        var saved = Path.Combine(root, "saved.uisi-translation");
        packageService.SavePackage(session, values, saved);
        using var reopened = packageService.OpenPackage(saved);
        packageService.GenerateFinalDocument(reopened, packageService.CreateTargetItems(reopened), Path.Combine(root, "translated.docx"));
        var legacyFinalHash = Hash(Path.Combine(root, "translated.docx"));
        values[0].SourceValue = "Unsupported correction";
        try
        {
            packageService.GenerateFinalDocument(session, values, Path.Combine(root, "translated.docx"));
            throw new Exception("Old package source correction was silently discarded.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("старом пакете")) { }
        Assert(Hash(Path.Combine(root, "translated.docx")) == legacyFinalHash, "unsupported correction preserves the existing final DOCX");
        Assert(Hash(legacyPackage) == packageHash, "original translation package unchanged");
    }

    private static SdtRun Field(string tag, int id, params OpenXmlElement[] contents) => new(
        new SdtProperties(new SdtAlias { Val = tag }, new SdtId { Val = id }, new Tag { Val = tag }, new SdtContentText()),
        new SdtContentRun(contents));

    private static string? TagOf(SdtElement control) => control.GetFirstChild<SdtProperties>()?.GetFirstChild<Tag>()?.Val?.Value;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static IEnumerable<OpenXmlPartRootElement> Roots(WordprocessingDocument document)
    {
        var main = document.MainDocumentPart!;
        yield return main.Document!;
        foreach (var part in main.HeaderParts) yield return part.Header!;
        foreach (var part in main.FooterParts) yield return part.Footer!;
    }
    private static (string Text, string RunProperties) Describe(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var roots = Roots(document).ToList();
        return (string.Join("\n", roots.Select(part => part.InnerText)),
            string.Join("\n", roots.SelectMany(part => part.Descendants<RunProperties>()).Select(properties => properties.OuterXml)));
    }
    private static void ExpectRejected(Action action)
    {
        try { action(); throw new Exception("Ambiguous nested identifiers were accepted."); }
        catch (InvalidDataException) { }
    }
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Document regression failed: " + description);
    }
}
