namespace PcCleaner.Core;

/// <summary>Checked rows of a list, kept by a stable key so the checks survive list rebuilds and refreshes.</summary>
/// <remarks>For UI-thread view models; not thread-safe.</remarks>
public sealed class CheckedSet<TKey> : ObservableObject where TKey : notnull
{
    private readonly HashSet<TKey> _keys = new();

    public int Count => _keys.Count;

    /// <summary>Snapshot of the checked keys, so a caller may change the set while walking it.</summary>
    public IReadOnlyCollection<TKey> Keys => _keys.ToArray();

    /// <summary>Raised after every real change (a no-op call raises nothing).</summary>
    public event EventHandler? Changed;

    public bool IsChecked(TKey key) => _keys.Contains(key);

    public void Set(TKey key, bool isChecked)
    {
        if (isChecked ? _keys.Add(key) : _keys.Remove(key))
            RaiseChanged();
    }

    public void Clear()
    {
        if (_keys.Count == 0)
            return;
        _keys.Clear();
        RaiseChanged();
    }

    /// <summary>Header checkbox: true = all of visibleKeys checked, false = none (or nothing visible), null = some.</summary>
    public bool? StateOf(IEnumerable<TKey> visibleKeys)
    {
        var anyChecked = false;
        var anyUnchecked = false;
        foreach (var key in visibleKeys)
        {
            if (_keys.Contains(key))
                anyChecked = true;
            else
                anyUnchecked = true;
            if (anyChecked && anyUnchecked)
                return null;
        }
        return anyChecked;
    }

    /// <summary>Header checkbox click: checks or unchecks the visible keys only, as one change.</summary>
    public void SetAll(IEnumerable<TKey> visibleKeys, bool isChecked)
    {
        var changed = false;
        foreach (var key in visibleKeys)
            changed |= isChecked ? _keys.Add(key) : _keys.Remove(key);
        if (changed)
            RaiseChanged();
    }

    /// <summary>Forgets keys that are no longer in the list (process exited, entry deleted).</summary>
    public void KeepOnly(IEnumerable<TKey> existingKeys)
    {
        if (_keys.Count == 0)
            return;
        var existing = existingKeys.ToHashSet();
        if (_keys.RemoveWhere(k => !existing.Contains(k)) > 0)
            RaiseChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(Count));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
