using DndSheet.Core.Domain;

namespace DndSheet.Core.Application;

/// <summary>
/// The open character: tracks every change to the model (whichever view made it) and whether it
/// has been saved. Views subscribe to <see cref="Changed"/> to refresh derived values.
/// </summary>
public sealed class CharacterSession : IDisposable
{
    private readonly ChangeTracker _tracker;
    private int _changeVersion;
    private int _savedVersion;

    public CharacterSession(Character character)
    {
        Character = character;
        _tracker = new ChangeTracker(character, OnChanged);
    }

    public Character Character { get; }
    public bool IsDirty => _changeVersion != _savedVersion;

    public event EventHandler? Changed;

    /// <summary>Records that the current state is persisted.</summary>
    public void MarkSaved() => _savedVersion = _changeVersion;

    private void OnChanged()
    {
        _changeVersion++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _tracker.Dispose();
}
