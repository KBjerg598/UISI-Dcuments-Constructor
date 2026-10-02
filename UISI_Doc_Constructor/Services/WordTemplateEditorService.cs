using System.IO;
using System.Runtime.InteropServices;
using UISI_Doc_Constructor.Models;
using Word = Microsoft.Office.Interop.Word;

namespace UISI_Doc_Constructor.Services;

public enum WordDocumentSaveResult
{
    NoOpenDocument,
    Saved,
    Disconnected
}

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

    public void ActivateDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application is null || _document is null)
        {
            throw new InvalidOperationException("Сначала откройте рабочий шаблон в Microsoft Word.");
        }

        try
        {
            _application.Visible = true;
            _document.Activate();
        }
        catch (COMException exception) when (IsDisconnectedFromWord(exception))
        {
            ResetDisconnectedSession();
            throw new InvalidOperationException(
                "Связь с Microsoft Word была потеряна. Откройте шаблон в Word ещё раз.",
                exception);
        }
    }

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
            try
            {
                _application!.Visible = true;
                _document!.Activate();
                return;
            }
            catch (COMException exception) when (IsDisconnectedFromWord(exception))
            {
                ResetDisconnectedSession();
            }
        }

        CloseDocument(saveChanges: true);

        // Repair the app's stored copy before Word encounters legacy nested plain-text
        // fields. Keep a backup; never rewrite a document that is already open in Word.
        new OpenXmlDocumentService().RepairTemplateFields(fullPath);

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

            EnsureSelectionOutsideContentControls(range);

            var tag = string.IsNullOrWhiteSpace(existingTag)
                ? $"{FieldTagPrefix}{Guid.NewGuid():D}"
                : existingTag;

            contentControl = _document.ContentControls.Add(
                Word.WdContentControlType.wdContentControlRichText,
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
        catch (COMException exception) when (IsDisconnectedFromWord(exception))
        {
            ResetDisconnectedSession();
            throw new InvalidOperationException(
                "Связь с Microsoft Word была потеряна. Откройте шаблон в Word ещё раз.",
                exception);
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

    private void EnsureSelectionOutsideContentControls(Word.Range range)
    {
        Word.ContentControls? containedControls = null;
        Word.ContentControls? documentControls = null;
        Word.ContentControl? parent = null;
        try
        {
            parent = range.ParentContentControl;
            containedControls = range.ContentControls;
            if (parent is not null || containedControls.Count > 0) ThrowOverlappingField();

            // Range.ContentControls counts contained controls, not every enclosing or
            // partially overlapping control. Compare ranges only within the same story.
            documentControls = _document!.ContentControls;
            for (var index = 1; index <= documentControls.Count; index++)
            {
                Word.ContentControl? candidate = null;
                Word.Range? candidateRange = null;
                try
                {
                    candidate = documentControls[index];
                    candidateRange = candidate.Range;
                    if (range.InStory(candidateRange) && ContentControlSelectionGuard.Overlaps(
                            range.Start, range.End, candidateRange.Start, candidateRange.End))
                        ThrowOverlappingField();
                }
                finally
                {
                    ReleaseComObject(candidateRange);
                    ReleaseComObject(candidate);
                }
            }

            // Headers, footers and text frames have their own control collections.
            // Endpoint parents catch partial overlaps not listed in Document.ContentControls.
            CheckEndpoint(range, range.Start);
            CheckEndpoint(range, range.End - 1);
        }
        finally
        {
            ReleaseComObject(parent);
            ReleaseComObject(containedControls);
            ReleaseComObject(documentControls);
        }
    }

    private static void CheckEndpoint(Word.Range range, int position)
    {
        Word.Range? endpoint = null;
        Word.ContentControl? parent = null;
        try
        {
            endpoint = range.Duplicate;
            endpoint.SetRange(position, position);
            parent = endpoint.ParentContentControl;
            if (parent is not null) ThrowOverlappingField();
        }
        finally
        {
            ReleaseComObject(parent);
            ReleaseComObject(endpoint);
        }
    }

    private static void ThrowOverlappingField() => throw new InvalidOperationException(
        "Выделение находится внутри существующего поля или пересекает его. " +
        "Выделите отдельный текст вне рамки поля. Для изменения существующего поля используйте список полей в программе.");

    public WordDocumentSaveResult SaveDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_document is null)
        {
            return WordDocumentSaveResult.NoOpenDocument;
        }

        try
        {
            _document.Save();
            return WordDocumentSaveResult.Saved;
        }
        catch (COMException exception) when (IsDisconnectedFromWord(exception))
        {
            ResetDisconnectedSession();
            return WordDocumentSaveResult.Disconnected;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "Microsoft Word не смог сохранить шаблон. Проверьте, что документ доступен для записи.",
                exception);
        }
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

    private void ResetDisconnectedSession()
    {
        ReleaseComObject(_document);
        _document = null;
        _documentPath = null;

        ReleaseComObject(_application);
        _application = null;
    }

    private static bool IsDisconnectedFromWord(COMException exception) =>
        exception.HResult is
            unchecked((int)0x800706BA) or // RPC_S_SERVER_UNAVAILABLE
            unchecked((int)0x800706BE) or // RPC_S_CALL_FAILED
            unchecked((int)0x80010108) or // RPC_E_DISCONNECTED
            unchecked((int)0x800401FD);   // CO_E_OBJNOTCONNECTED

}
