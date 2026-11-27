using Sambient.Seq;

namespace Sambient.App.Projects;

public enum UnsavedChoice { Save, DontSave, Cancel }

/// <summary>The dialogs the project commands need (implemented over Avalonia windows; faked in tests).</summary>
public interface IProjectDialogs
{
    /// <summary>"Save changes to X before &lt;action&gt;?"</summary>
    Task<UnsavedChoice> AskUnsavedAsync(string projectName, string action);
    Task<string?> PickOpenPathAsync(string initialDirectory);
    Task<string?> PickSavePathAsync(string initialDirectory, string suggestedFileName);
    /// <summary>"Restore unsaved changes?" after a crash.</summary>
    Task<bool> AskRestoreAsync(AutosaveInfo info);
    Task<bool> ConfirmAsync(string title, string message, string okText);
    Task ShowMessageAsync(string title, string message);
}
