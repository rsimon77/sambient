using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Sambient.App.Projects;
using Sambient.Seq;

namespace Sambient.App.Views;

/// <summary>Project dialogs over Avalonia windows and the platform file pickers (native open / save panels on macOS).</summary>
public sealed class AvaloniaProjectDialogs : IProjectDialogs
{
    private readonly Window _owner;

    public AvaloniaProjectDialogs(Window owner) { _owner = owner; }

    private static readonly FilePickerFileType ProjectType = new("Sambient project") { Patterns = ["*" + ProjectFile.Extension] };

    public async Task<UnsavedChoice> AskUnsavedAsync(string projectName, string action)
        => await ChoiceDialog.Unsaved(projectName, action).ShowAsync(_owner) switch
        {
            0 => UnsavedChoice.Save,
            1 => UnsavedChoice.DontSave,
            _ => UnsavedChoice.Cancel,
        };

    public async Task<bool> AskRestoreAsync(AutosaveInfo info) => await ChoiceDialog.Restore(info.ProjectName, info.SavedUtc).ShowAsync(_owner) == 0;

    public async Task<bool> ConfirmAsync(string title, string message, string okText) => await ChoiceDialog.Confirm(title, message, okText).ShowAsync(_owner) == 0;

    public async Task ShowMessageAsync(string title, string message) => await ChoiceDialog.Message(title, message).ShowAsync(_owner);

    private async Task<IStorageFolder?> Folder(string dir)
    {
        try { Directory.CreateDirectory(dir); return await _owner.StorageProvider.TryGetFolderFromPathAsync(dir); }
        catch (Exception) { return null; }
    }

    public async Task<string?> PickOpenPathAsync(string initialDirectory)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Sambient project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectType],
            SuggestedStartLocation = await Folder(initialDirectory),
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSavePathAsync(string initialDirectory, string suggestedFileName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Sambient project",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = ProjectFile.Extension.TrimStart('.'),
            FileTypeChoices = [ProjectType],
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await Folder(initialDirectory),
        });
        return file?.TryGetLocalPath();
    }
}
