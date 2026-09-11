using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GeoAutoDocs.Core;

namespace GeoAutoDocs.App;

public partial class SectionOrderWindow : Window
{
    private const string DragDataFormat = "GeoAutoDocs.SectionOrderItem";
    private readonly ObservableCollection<SectionOrderItem> _items;
    private Point _dragStart;
    private Guid? _draggedSectionId;

    public SectionOrderWindow(IEnumerable<InspectionSection> sections)
    {
        InitializeComponent();
        _items = new ObservableCollection<SectionOrderItem>(sections
            .OrderBy(x => x.Order)
            .Select((section, index) => new SectionOrderItem
            {
                Id = section.Id,
                Name = section.Name,
                Description = BuildDescription(section),
                Position = index + 1
            }));
        SectionsOrderList.ItemsSource = _items;
    }

    public IReadOnlyList<Guid> OrderedSectionIds => _items.Select(x => x.Id).ToList();

    private void SectionDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SectionOrderItem item }) return;
        _dragStart = e.GetPosition(SectionsOrderList);
        _draggedSectionId = item.Id;
        e.Handled = true;
    }

    private void SectionsOrderList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedSectionId = null;
            return;
        }
        if (_draggedSectionId is not Guid sectionId) return;

        var position = e.GetPosition(SectionsOrderList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var data = new DataObject();
        data.SetData(DragDataFormat, sectionId);
        _draggedSectionId = null;
        DragDrop.DoDragDrop(SectionsOrderList, data, DragDropEffects.Move);
    }

    private void SectionsOrderList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragDataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SectionsOrderList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DragDataFormat) is not Guid sourceId) return;
            var source = _items.FirstOrDefault(x => x.Id == sourceId);
            if (source is null) return;

            var targetContainer = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
            var target = targetContainer?.DataContext as SectionOrderItem;
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
            SectionsOrderList.Items.Refresh();
        }
        finally
        {
            _draggedSectionId = null;
            e.Handled = true;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static string BuildDescription(InspectionSection section)
    {
        var start = section.StartPoint?.Trim();
        var end = section.EndPoint?.Trim();
        if (!string.IsNullOrWhiteSpace(start) || !string.IsNullOrWhiteSpace(end))
            return $"{start ?? "?"} → {end ?? "?"}";
        return "Pontos inicial e final não informados";
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

    private sealed class SectionOrderItem
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int Position { get; set; }
    }
}
