using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GeoAutoDocs.App;

public enum PendingCaptureAction
{
    None,
    Register,
    Navigate,
    Delete
}

public sealed class PendingCaptureListItem(
    Guid id,
    string? thumbnailPath,
    string timestamp,
    string? videoName,
    bool canRegister,
    bool canDelete) : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public Guid Id { get; } = id;
    public string? ThumbnailPath { get; } = thumbnailPath;
    public string Timestamp { get; } = timestamp;
    public string VideoName { get; } = string.IsNullOrWhiteSpace(videoName) ? "Vídeo de origem" : videoName;
    public bool CanRegister { get; } = canRegister;
    public bool CanDelete { get; } = canDelete;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class PendingCapturesWindow : Window
{
    private readonly Func<string?, CancellationToken, Task<BitmapImage?>> _thumbnailLoader;
    private readonly CancellationTokenSource _closing = new();

    public PendingCaptureAction SelectedAction { get; private set; }
    public Guid? SelectedCaptureId { get; private set; }

    public PendingCapturesWindow(
        IReadOnlyList<PendingCaptureListItem> items,
        Func<string?, CancellationToken, Task<BitmapImage?>> thumbnailLoader,
        Guid? highlightedCaptureId = null)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 1040, 700);
        _thumbnailLoader = thumbnailLoader;
        CapturesList.ItemsSource = items;
        SummaryText.Text = items.Count switch
        {
            0 => "Nenhuma captura aguardando revisão",
            1 => "1 captura aguardando revisão",
            _ => $"{items.Count} capturas aguardando revisão"
        };
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (highlightedCaptureId is Guid captureId &&
            items.FirstOrDefault(x => x.Id == captureId) is { } highlighted)
        {
            CapturesList.SelectedItem = highlighted;
            Loaded += (_, _) =>
            {
                CapturesList.UpdateLayout();
                CapturesList.ScrollIntoView(highlighted);
                CapturesList.Focus();
            };
        }
    }

    private void Complete(PendingCaptureAction action, object? tag)
    {
        if (tag is not Guid id) return;
        SelectedAction = action;
        SelectedCaptureId = id;
        DialogResult = true;
    }

    private void Register_Click(object sender, RoutedEventArgs e) =>
        Complete(PendingCaptureAction.Register, ((Button)sender).Tag);

    private void Navigate_Click(object sender, RoutedEventArgs e) =>
        Complete(PendingCaptureAction.Navigate, ((Button)sender).Tag);

    private void Delete_Click(object sender, RoutedEventArgs e) =>
        Complete(PendingCaptureAction.Delete, ((Button)sender).Tag);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { DataContext: PendingCaptureListItem item } image ||
            item.Thumbnail is not null) return;
        try
        {
            var thumbnail = await _thumbnailLoader(item.ThumbnailPath, _closing.Token);
            if (thumbnail is not null && !_closing.IsCancellationRequested &&
                ReferenceEquals(image.DataContext, item))
                item.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
