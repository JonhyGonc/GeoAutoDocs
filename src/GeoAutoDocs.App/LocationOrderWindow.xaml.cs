using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class LocationOrderWindow : Window
{
    private const string DragDataFormat = "GeoAutoDocs.LocationOrderItem";
    private readonly ObservableCollection<LocationOrderItem> _items;
    private Point _dragStart;
    private Guid? _draggedLocationId;

    public LocationOrderWindow(IEnumerable<Location> locations)
    {
        InitializeComponent();
        _items = new ObservableCollection<LocationOrderItem>(locations
            .OrderBy(x => x.Order)
            .Select((location, index) => new LocationOrderItem
            {
                Id = location.Id,
                Name = location.Name,
                Description = location.Sections.Count == 1
                    ? "1 trecho cadastrado"
                    : $"{location.Sections.Count} trechos cadastrados",
                Position = index + 1
            }));
        LocationsOrderList.ItemsSource = _items;
    }

    public IReadOnlyList<Guid> OrderedLocationIds => _items.Select(x => x.Id).ToList();

    private void LocationDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LocationOrderItem item }) return;
        _dragStart = e.GetPosition(LocationsOrderList);
        _draggedLocationId = item.Id;
        e.Handled = true;
    }

    private void LocationsOrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedLocationId = null;
            return;
        }
        if (_draggedLocationId is not Guid locationId) return;

        var position = e.GetPosition(LocationsOrderList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject();
        data.SetData(DragDataFormat, locationId);
        _draggedLocationId = null;
        DragDrop.DoDragDrop(LocationsOrderList, data, DragDropEffects.Move);
    }

    private void LocationsOrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragDataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void LocationsOrderList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DragDataFormat) is not Guid sourceId) return;
            var source = _items.FirstOrDefault(x => x.Id == sourceId);
            if (source is null) return;

            var targetContainer = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
            var target = targetContainer?.DataContext as LocationOrderItem;
            if (target?.Id == sourceId) return;

            _items.Remove(source);
            var targetIndex = target is null ? _items.Count : _items.IndexOf(target);
            if (targetIndex < 0) targetIndex = _items.Count;
            if (targetContainer is not null &&
                e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
                targetIndex++;
            if (targetIndex > _items.Count) targetIndex = _items.Count;
            _items.Insert(targetIndex, source);

            for (var index = 0; index < _items.Count; index++)
                _items[index].Position = index + 1;
            LocationsOrderList.Items.Refresh();
        }
        finally
        {
            _draggedLocationId = null;
            e.Handled = true;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private sealed class LocationOrderItem
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int Position { get; set; }
    }
}
