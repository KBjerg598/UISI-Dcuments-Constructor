using System.IO;
using System.Runtime.InteropServices;
using UISI_Doc_Constructor.Models;
using Word = Microsoft.Office.Interop.Word;

namespace UISI_Doc_Constructor.Services;

public sealed class WordTemplateEditorService : IDisposable
{
    private const string FieldTagPrefix = "uisi-field:";

    private Word.Application? _application;
    private Word.Document? _document;
    private string? _documentPath;
    private bool _disposed;

    public bool IsDocumentOpen(string path) =>
        _document is not null &&
        string.Equals(_documentPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    public void OpenDocument(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OfficeInteropAssemblyResolver.Initialize();

        if (!OfficeInteropAssemblyResolver.IsWordInstalled())
        {
            throw new InvalidOperationException(
                "Microsoft Word не найден. Установите настольную версию Word и повторите попытку.");
        }

        var fullPath = Path.GetFullPath(path);
        if (IsDocumentOpen(fullPath))
        {
            _application!.Visible = true;
            _document!.Activate();
            return;
        }

        CloseDocument(saveChanges: true);

        try
        {
            _application = new Word.Application
            {
                Visible = true,
                DisplayAlerts = Word.WdAlertLevel.wdAlertsAll
            };

            _document = _application.Documents.Open(
                FileName: fullPath,
                ConfirmConversions: false,
                ReadOnly: false,
                AddToRecentFiles: false,
                Revert: false,
                Visible: true);

            _documentPath = fullPath;
            _document.Activate();
        }
        catch (FileNotFoundException exception) when (
            string.Equals(
                Path.GetFileNameWithoutExtension(exception.FileName),
                "office",
                StringComparison.OrdinalIgnoreCase))
        {
            CloseDocument(saveChanges: false);
            throw new InvalidOperationException(
                "Не удалось найти компоненты Microsoft Office для взаимодействия с Word. " +
                "Проверьте установку Office и компонент «Поддержка программирования .NET».",
                exception);
        }
        catch (COMException exception)
        {
            CloseDocument(saveChanges: false);
            throw new InvalidOperationException(
                "Не удалось запустить Microsoft Word. Проверьте установку Office и повторите попытку.",
                exception);
        }
        catch
        {
            CloseDocument(saveChanges: false);
            throw;
        }
    }

    public TemplateField CreateFieldFromSelection(string displayName, string? existingTag = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_application is null || _document is null)
        {
            throw new InvalidOperationException("Сначала откройте рабочий шаблон в Microsoft Word.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Введите название поля.", nameof(displayName));
        }

        Word.Selection? selection = null;
        Word.Range? range = null;
        Word.ContentControl? contentControl = null;

        try
        {
            selection = _application.Selection;
            range = selection.Range.Duplicate;

            if (range.Start == range.End || string.IsNullOrWhiteSpace(range.Text))
            {
                throw new InvalidOperationException(
                    "В Word не выделен текст. Выделите пример значения и повторите действие.");
            }

            if (range.Text.Contains('\a') || range.Text.Contains('\r'))
            {
                throw new InvalidOperationException(
                    "Пока поле можно создать только внутри одного абзаца и одной ячейки таблицы.");
            }

            if (range.ContentControls.Count > 0)
            {
                throw new InvalidOperationException(
                    "Выделенный текст уже находится внутри поля шаблона.");
            }

            var tag = string.IsNullOrWhiteSpace(existingTag)
                ? $"{FieldTagPrefix}{Guid.NewGuid():D}"
                : existingTag;

            contentControl = _document.ContentControls.Add(
                Word.WdContentControlType.wdContentControlText,
                range);

            contentControl.Title = displayName.Trim();
            contentControl.Tag = tag;
            contentControl.Appearance = Word.WdContentControlAppearance.wdContentControlBoundingBox;
            contentControl.LockContentControl = false;
            contentControl.LockContents = false;

            _document.Save();

            return new TemplateField
            {
                Tag = tag,
                DisplayName = displayName.Trim(),
                Occurrences = 1
            };
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "Word не смог создать поле из текущего выделения. Проверьте, что документ доступен для редактирования.",
                exception);
        }
        finally
        {
            ReleaseComObject(contentControl);
            ReleaseComObject(range);
            ReleaseComObject(selection);
        }
    }

    public void SaveDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _document?.Save();
    }

    public void CloseDocument(bool saveChanges)
    {
        if (_document is not null)
        {
            try
            {
                _document.Close(
                    saveChanges
                        ? Word.WdSaveOptions.wdSaveChanges
                        : Word.WdSaveOptions.wdDoNotSaveChanges);
            }
            catch (COMException)
            {
                // Word мог быть закрыт самим пользователем.
            }
            finally
            {
                ReleaseComObject(_document);
                _document = null;
                _documentPath = null;
            }
        }

        if (_application is not null)
        {
            try
            {
                _application.Quit(Word.WdSaveOptions.wdDoNotSaveChanges);
            }
            catch (COMException)
            {
                // Экземпляр Word уже завершён.
            }
            finally
            {
                ReleaseComObject(_application);
                _application = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CloseDocument(saveChanges: true);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
