namespace UISI_Doc_Constructor.Services;

public static class ContentControlSelectionGuard
{
    // Word character ranges are half-open: adjacent fields must remain selectable.
    public static bool Overlaps(int selectionStart, int selectionEnd, int controlStart, int controlEnd) =>
        selectionStart < selectionEnd && controlStart < controlEnd &&
        selectionStart < controlEnd && selectionEnd > controlStart;
}
