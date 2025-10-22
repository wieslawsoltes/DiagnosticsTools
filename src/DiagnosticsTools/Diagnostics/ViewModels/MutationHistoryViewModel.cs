using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Diagnostics.PropertyEditing;

namespace Avalonia.Diagnostics.ViewModels;

public sealed class MutationHistoryViewModel : ViewModelBase
{
    private readonly Func<MutationHistoryEntryViewModel, Task> _undoCallback;
    private readonly Func<MutationHistoryEntryViewModel, Task> _redoCallback;
    private readonly DelegateCommand _undoToCommand;
    private readonly DelegateCommand _redoToCommand;
    private bool _hasHistory;
    private bool _hasUndoHistory;
    private bool _hasRedoHistory;

    public MutationHistoryViewModel(
        Func<MutationHistoryEntryViewModel, Task> undoCallback,
        Func<MutationHistoryEntryViewModel, Task> redoCallback)
    {
        _undoCallback = undoCallback ?? throw new ArgumentNullException(nameof(undoCallback));
        _redoCallback = redoCallback ?? throw new ArgumentNullException(nameof(redoCallback));
        UndoHistory = new ObservableCollection<MutationHistoryEntryViewModel>();
        RedoHistory = new ObservableCollection<MutationHistoryEntryViewModel>();
        _undoToCommand = new DelegateCommand(UndoToAsync, parameter => parameter is MutationHistoryEntryViewModel entry && entry.Kind == MutationHistoryEntryKind.Undo);
        _redoToCommand = new DelegateCommand(RedoToAsync, parameter => parameter is MutationHistoryEntryViewModel entry && entry.Kind == MutationHistoryEntryKind.Redo);
    }

    public ObservableCollection<MutationHistoryEntryViewModel> UndoHistory { get; }

    public ObservableCollection<MutationHistoryEntryViewModel> RedoHistory { get; }

    public bool HasHistory
    {
        get => _hasHistory;
        private set => RaiseAndSetIfChanged(ref _hasHistory, value);
    }

    public bool HasUndoHistory
    {
        get => _hasUndoHistory;
        private set => RaiseAndSetIfChanged(ref _hasUndoHistory, value);
    }

    public bool HasRedoHistory
    {
        get => _hasRedoHistory;
        private set => RaiseAndSetIfChanged(ref _hasRedoHistory, value);
    }

    public ICommand UndoToCommand => _undoToCommand;

    public ICommand RedoToCommand => _redoToCommand;

    public void Refresh(IReadOnlyList<MutationEntry> undoEntries, IReadOnlyList<MutationEntry> redoEntries)
    {
        UpdateCollection(UndoHistory, undoEntries, MutationHistoryEntryKind.Undo);
        UpdateCollection(RedoHistory, redoEntries, MutationHistoryEntryKind.Redo);
        HasUndoHistory = UndoHistory.Count > 0;
        HasRedoHistory = RedoHistory.Count > 0;
        HasHistory = HasUndoHistory || HasRedoHistory;
    }

    private async Task UndoToAsync(object? parameter)
    {
        if (parameter is not MutationHistoryEntryViewModel entry || entry.Kind != MutationHistoryEntryKind.Undo)
        {
            return;
        }

        await _undoCallback(entry).ConfigureAwait(false);
    }

    private async Task RedoToAsync(object? parameter)
    {
        if (parameter is not MutationHistoryEntryViewModel entry || entry.Kind != MutationHistoryEntryKind.Redo)
        {
            return;
        }

        await _redoCallback(entry).ConfigureAwait(false);
    }

    private static void UpdateCollection(
        ObservableCollection<MutationHistoryEntryViewModel> collection,
        IReadOnlyList<MutationEntry> entries,
        MutationHistoryEntryKind kind)
    {
        collection.Clear();

        if (entries is { Count: > 0 })
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                collection.Add(CreateEntryViewModel(index, kind, entry));
            }
        }
    }

    private static MutationHistoryEntryViewModel CreateEntryViewModel(int index, MutationHistoryEntryKind kind, in MutationEntry entry)
    {
        var steps = index + 1;
        var documentsSummary = MutationHistoryFormatter.BuildDocumentsSummary(entry);
        var gesture = string.IsNullOrWhiteSpace(entry.Gesture) ? "Change" : entry.Gesture!;
        var timestamp = entry.Timestamp == default
            ? string.Empty
            : entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        return new MutationHistoryEntryViewModel(kind, steps, entry, gesture, timestamp, documentsSummary);
    }
}

public sealed class MutationHistoryEntryViewModel
{
    public MutationHistoryEntryViewModel(
        MutationHistoryEntryKind kind,
        int steps,
        in MutationEntry entry,
        string gesture,
        string timestamp,
        string documentsSummary)
    {
        Kind = kind;
        Steps = steps;
        Entry = entry;
        Gesture = gesture;
        Timestamp = timestamp;
        DocumentsSummary = documentsSummary;
    }

    public MutationHistoryEntryKind Kind { get; }

    public int Steps { get; }

    public MutationEntry Entry { get; }

    public string Gesture { get; }

    public string Timestamp { get; }

    public string DocumentsSummary { get; }

    public string Description => $"{Gesture}: {DocumentsSummary}";
}

public enum MutationHistoryEntryKind
{
    Undo,
    Redo
}
