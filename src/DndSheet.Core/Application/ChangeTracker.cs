using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using DndSheet.Core.Domain;

namespace DndSheet.Core.Application;

/// <summary>
/// Watches an entire domain object graph (nested Observables and their collections) and reports any
/// change through one callback. This is what lets every view, derived value and autosave react to an
/// edit made anywhere, without each domain class knowing about its parent.
/// </summary>
public sealed class ChangeTracker : IDisposable
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildProperties = new();

    private readonly Action _onChanged;
    private readonly HashSet<object> _attached = new(ReferenceEqualityComparer.Instance);

    public ChangeTracker(object root, Action onChanged)
    {
        _onChanged = onChanged;
        Attach(root);
    }

    private void Attach(object? node)
    {
        if (node is null || !_attached.Add(node)) return;

        if (node is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += OnCollectionChanged;
            if (node is IEnumerable items) foreach (var item in items) Attach(item);
            return;
        }
        if (node is not Observable observable) return;

        observable.PropertyChanged += OnPropertyChanged;
        foreach (var property in ChildProperties.GetOrAdd(node.GetType(), FindChildProperties))
            Attach(property.GetValue(node));
    }

    private void Detach(object? node)
    {
        if (node is null || !_attached.Remove(node)) return;

        if (node is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCollectionChanged;
            if (node is IEnumerable items) foreach (var item in items) Detach(item);
            return;
        }
        if (node is not Observable observable) return;

        observable.PropertyChanged -= OnPropertyChanged;
        foreach (var property in ChildProperties.GetOrAdd(node.GetType(), FindChildProperties))
            Detach(property.GetValue(node));
    }

    private static PropertyInfo[] FindChildProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 &&
                        (typeof(Observable).IsAssignableFrom(p.PropertyType) ||
                         typeof(INotifyCollectionChanged).IsAssignableFrom(p.PropertyType)))
            .ToArray();

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => _onChanged();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (var item in e.OldItems) Detach(item);
        if (e.NewItems is not null) foreach (var item in e.NewItems) Attach(item);
        // ponytail: Reset (from Clear()) carries no OldItems, so cleared items stay subscribed until
        // Dispose. Harmless (they can only raise a spurious change); walk the graph if it ever matters.
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is IEnumerable current)
            foreach (var item in current) Attach(item);
        _onChanged();
    }

    public void Dispose()
    {
        foreach (var node in _attached.ToList()) Detach(node);
    }
}
