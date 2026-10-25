namespace Clink.Screenshots.Editing;

/// <summary>
/// Snapshot (memento) undo: a list of immutable states and a cursor. <see cref="Push"/> after a finished gesture drops everything
/// that could be redone and the oldest states beyond <see cref="Limit"/>. Transient previews (a drag in progress) never come here:
/// they live in the session until the gesture ends.
/// </summary>
internal sealed class UndoStack<T> where T : class
{
    private readonly List<T> _states = new();
    private int _at;   // index of the current state

    public int Limit { get; }

    public UndoStack(T initial, int limit = 200)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
        _states.Add(initial);
    }

    public T Current => _states[_at];
    public bool CanUndo => _at > 0;
    public bool CanRedo => _at < _states.Count - 1;
    /// <summary>The number of states held (the current one included).</summary>
    public int Count => _states.Count;

    /// <summary>Makes <paramref name="state"/> current. A state that is the current one (same reference) is not pushed again; returns whether anything was pushed.</summary>
    public bool Push(T state)
    {
        if (ReferenceEquals(state, Current)) return false;
        if (_at < _states.Count - 1) _states.RemoveRange(_at + 1, _states.Count - _at - 1);
        _states.Add(state);
        _at = _states.Count - 1;
        if (_states.Count > Limit)
        {
            int drop = _states.Count - Limit;
            _states.RemoveRange(0, drop);
            _at -= drop;
        }
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _at--;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _at++;
        return true;
    }
}
