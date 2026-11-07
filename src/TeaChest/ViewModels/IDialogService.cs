namespace TeaChest.ViewModels;

public enum DialogIcon
{
    Information,
    Warning,
    Question
}

public enum ConfirmButtons
{
    OkCancel,
    YesNo
}

/// <summary>
/// The file pickers and message boxes the view models need, so their flows can run in tests
/// against a fake instead of real windows.
/// </summary>
public interface IDialogService
{
    /// <summary>The picked file, or null if the user cancelled. The file must already exist.</summary>
    string? PickOpenFile(string title, string filter);

    /// <summary>The picked file, or null if the user cancelled. Asks before overwriting.</summary>
    string? PickSaveFile(string title, string filter, string defaultExtension, string fileName, string initialDirectory);

    /// <summary>True for OK or Yes.</summary>
    bool Confirm(string message, string title, ConfirmButtons buttons, DialogIcon icon);

    void Show(string message, string title, DialogIcon icon);
}
