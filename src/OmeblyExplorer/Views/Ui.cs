// Small helpers for the XAML templates.
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OmeblyExplorer.Views;

public static class Ui
{
    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool on) => on ? Visibility.Collapsed : Visibility.Visible;
    public static double HiddenOpacity(bool hidden) => hidden ? 0.55 : 1;
    public static string GallerySub(string type, string size) => string.IsNullOrEmpty(size) ? type : $"{type} · {size}";

    public static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255, i = 0;
        if (hex.Length == 8) { a = Convert.ToByte(hex[..2], 16); i = 2; }
        return new SolidColorBrush(ColorHelper.FromArgb(a, Convert.ToByte(hex.Substring(i, 2), 16), Convert.ToByte(hex.Substring(i + 2, 2), 16), Convert.ToByte(hex.Substring(i + 4, 2), 16)));
    }
}

/// <summary>
/// A list that can take 100,000 items at once with a single "reset" instead of 100,000 change events, so huge
/// folders appear instantly. Single item changes (a file added or removed) still animate normally.
/// </summary>
public sealed class BulkList<T> : IList<T>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private List<T> _items = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReplaceAll(List<T> items) { _items = items; Reset(); }
    public void AddRange(IReadOnlyCollection<T> items) { if (items.Count == 0) return; _items.AddRange(items); Reset(); }
    private void Reset()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
    public List<T> Snapshot() => _items;

    public T this[int index] { get => _items[index]; set { var old = _items[index]; _items[index] = value; CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, value, old, index)); } }
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public void Add(T item) => Insert(_items.Count, item);
    public void Insert(int index, T item) { _items.Insert(index, item); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index)); }
    public bool Remove(T item) { var i = _items.IndexOf(item); if (i < 0) return false; RemoveAt(i); return true; }
    public void RemoveAt(int index) { var item = _items[index]; _items.RemoveAt(index); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index)); }
    public void Clear() { _items = new(); Reset(); }
    public bool Contains(T item) => _items.Contains(item);
    public int IndexOf(T item) => _items.IndexOf(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    object? IList.this[int index] { get => _items[index]; set => this[index] = (T)value!; }
    bool IList.IsFixedSize => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) { Add((T)value!); return _items.Count - 1; }
    bool IList.Contains(object? value) => value is T t && Contains(t);
    int IList.IndexOf(object? value) => value is T t ? IndexOf(t) : -1;
    void IList.Insert(int index, object? value) => Insert(index, (T)value!);
    void IList.Remove(object? value) { if (value is T t) Remove(t); }
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
