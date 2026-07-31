using System.IO;

namespace UISI_Doc_Constructor.Services;

public sealed class TemplateStorageService
{
    public TemplateStorageService()
    {
        TemplatesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UISI Doc Constructor",
            "Templates");

        Directory.CreateDirectory(TemplatesPath);
    }

    public string TemplatesPath { get; }

    public string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Выбранный документ не найден.", sourcePath);
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("На первом этапе поддерживаются только документы DOCX.");
        }

        var safeName = MakeSafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var destinationPath = Path.Combine(TemplatesPath, $"{safeName}_{uniqueSuffix}.docx");

        File.Copy(sourcePath, destinationPath, overwrite: false);
        return destinationPath;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleanedName = new string(name
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(cleanedName) ? "Шаблон" : cleanedName;
    }
}
