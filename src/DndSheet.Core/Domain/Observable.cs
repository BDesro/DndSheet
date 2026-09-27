using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DndSheet.Core.Domain;

/// <summary>
/// Change-notifying base for domain objects. The domain model is the single source of truth and
/// every view binds to it directly, so the model itself raises change notifications rather than
/// being mirrored into per-view copies.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>
    /// Clamps and stores. Always raises when the input was out of range so a bound editor that
    /// shows the rejected text gets refreshed back to the stored value.
    /// </summary>
    protected void SetClamped(ref int field, int value, int min, int max, [CallerMemberName] string? name = null)
    {
        var clamped = Math.Clamp(value, min, max);
        if (!Set(ref field, clamped, name) && clamped != value) Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
