using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sambient.App.ViewModels;

/// <summary>One of the 16 step buttons of the visible page.</summary>
public sealed partial class StepCellViewModel : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _inLength = true;
    [ObservableProperty] private bool _hasNotes;
    [ObservableProperty] private string _noteCountText = "";
    [ObservableProperty] private bool _hasLocks;
    [ObservableProperty] private string _probabilityText = "";
    [ObservableProperty] private bool _tieLeft;
    [ObservableProperty] private bool _tieRight;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPlayhead;

    public void Apply(StepCellInfo c)
    {
        Index = c.Index;
        Label = (c.Index + 1).ToString();
        InLength = c.InLength;
        HasNotes = c.HasNotes;
        NoteCountText = c.NoteCount > 1 ? "×" + c.NoteCount : "";
        HasLocks = c.HasLocks;
        ProbabilityText = c.ProbabilityText;
        TieLeft = c.TieFromPrevious;
        TieRight = c.TieToNext;
    }
}

/// <summary>A selectable item of a small segmented row (bank tabs, step pages).</summary>
public sealed partial class SelectorItemViewModel : ObservableObject
{
    public int Value { get; }
    public string Text { get; }
    public ICommand SelectCommand { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;

    public SelectorItemViewModel(int value, string text, Action<int> select)
    {
        Value = value; Text = text;
        SelectCommand = new RelayCommand(() => select(value));
    }
}

/// <summary>One of the 16 slot buttons of the pattern browser.</summary>
public sealed partial class PatternSlotViewModel : ObservableObject
{
    public int Slot { get; }
    public string Number => (Slot + 1).ToString();
    public ICommand ClickCommand { get; }
    [ObservableProperty] private int _bank;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = "";
    /// <summary>Position(s) in the chain, e.g. "1,3" (empty when not chained).</summary>
    [ObservableProperty] private string _chainText = "";

    public PatternSlotViewModel(int slot, Action<PatternSlotViewModel> click)
    {
        Slot = slot;
        ClickCommand = new RelayCommand(() => click(this));
    }

    partial void OnEditNameChanged(string value)
    {
        var s = PatternNames.Sanitize(value);
        if (s != value) EditName = s;
    }
}

public sealed partial class ChainItemViewModel : ObservableObject
{
    public int Bank { get; }
    public int Slot { get; }
    public int Position { get; set; }
    public string Text { get; set; }
    /// <summary>The entry PLAY SONG starts from (click an entry to select it).</summary>
    [ObservableProperty] private bool _isSelected;
    public ICommand RemoveCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }
    public ICommand SelectCommand { get; }

    public ChainItemViewModel(int bank, int slot, string text, Action<ChainItemViewModel> remove, Action<ChainItemViewModel, int> move,
        Action<ChainItemViewModel> select)
    {
        Bank = bank; Slot = slot; Text = text;
        RemoveCommand = new RelayCommand(() => remove(this));
        UpCommand = new RelayCommand(() => move(this, -1));
        DownCommand = new RelayCommand(() => move(this, 1));
        SelectCommand = new RelayCommand(() => select(this));
    }
}

/// <summary>A note of the selected step (name, velocity, tie length) with a delete button.</summary>
public sealed class StepNoteRowViewModel
{
    public int Note { get; }
    public string Text { get; }
    public ICommand DeleteCommand { get; }

    public StepNoteRowViewModel(int note, int velocity, int tie, Action<int> delete)
    {
        Note = note;
        Text = $"{NoteNames.Name(note)}  vel {velocity}" + (tie > 1 ? $"  tie {tie}" : "");
        DeleteCommand = new RelayCommand(() => delete(note));
    }
}

/// <summary>A parameter lock of the selected step with a delete button.</summary>
public sealed class StepLockRowViewModel
{
    public Sambient.Engine.ParamId Id { get; }
    public string Text { get; }
    public ICommand DeleteCommand { get; }

    public StepLockRowViewModel(Sambient.Engine.ParamId id, float value, Action<Sambient.Engine.ParamId> delete)
    {
        Id = id;
        Text = $"{ParamLabels.Of(id)} = {ParamLabels.FormatValue(value)}";
        DeleteCommand = new RelayCommand(() => delete(id));
    }
}
