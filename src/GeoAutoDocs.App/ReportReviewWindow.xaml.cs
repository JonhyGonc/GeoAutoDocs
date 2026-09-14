using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using GeoAutoDocs.Core.Review;

namespace GeoAutoDocs.App;

public sealed class ReviewIssueNavigationEventArgs(ReviewIssue issue) : EventArgs
{
    public ReviewIssue Issue { get; } = issue;
}

public partial class ReportReviewWindow : Window
{
    private ReviewResult? _result;
    private ReportReviewContext? _context;
    private ReviewIssueFilter _filter = ReviewIssueFilter.All;

    public ReportReviewWindow()
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 920, 720);
    }

    public event EventHandler? ReviewRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<ReviewIssueNavigationEventArgs>? NavigateRequested;

    public void ShowSnapshot(
        ReportReviewSnapshot snapshot,
        ReportReviewContext context,
        bool isStale)
    {
        _result = snapshot.Result;
        _context = context;
        ErrorCountText.Text = snapshot.Result.ErrorCount.ToString(CultureInfo.CurrentCulture);
        WarningCountText.Text = snapshot.Result.WarningCount.ToString(CultureInfo.CurrentCulture);
        InfoCountText.Text = snapshot.Result.InfoCount.ToString(CultureInfo.CurrentCulture);
        ReviewStatusText.Text =
            $"Revisão concluída às {snapshot.CompletedAt.ToLocalTime():HH:mm}";
        IncompleteBanner.Visibility = snapshot.Result.IsComplete
            ? Visibility.Collapsed
            : Visibility.Visible;
        SetStale(isStale);
        ApplyFilter();
    }

    public void ShowNoReview(string message)
    {
        _result = null;
        _context = null;
        ErrorCountText.Text = "0";
        WarningCountText.Text = "0";
        InfoCountText.Text = "0";
        ReviewStatusText.Text = message;
        IncompleteBanner.Visibility = Visibility.Collapsed;
        StaleBanner.Visibility = Visibility.Collapsed;
        IssuesList.ItemsSource = null;
        EmptyState.Visibility = Visibility.Visible;
        EmptyStateTitle.Text = "Revisão ainda não executada";
        EmptyStateDescription.Text = "Clique em “Executar revisão” para analisar o relatório atual.";
        VisibleCountText.Text = "";
    }

    public void SetRunning(bool running)
    {
        ReviewProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunReviewButton.IsEnabled = !running;
        CancelReviewButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ReviewStatusText.Text = running ? "Revisando relatório..." : ReviewStatusText.Text;
        foreach (var button in FilterButtons()) button.IsEnabled = !running;
    }

    public void SetStale(bool isStale) =>
        StaleBanner.Visibility = isStale ? Visibility.Visible : Visibility.Collapsed;

    public void SelectFilter(ReviewIssueFilter filter)
    {
        _filter = filter;
        foreach (var button in FilterButtons())
            button.IsChecked = string.Equals(
                button.Tag as string,
                filter.ToString(),
                StringComparison.Ordinal);
        ApplyFilter();
    }

    public void ShowCancelled()
    {
        ReviewStatusText.Text = "Revisão cancelada. O resultado anterior foi preservado.";
    }

    public void ShowExecutionError()
    {
        ReviewStatusText.Text = "Não foi possível executar a revisão.";
        IncompleteBanner.Visibility = Visibility.Visible;
    }

    private void ApplyFilter()
    {
        if (_result is null || _context is null)
        {
            IssuesList.ItemsSource = null;
            return;
        }

        var filtered = ReviewIssueFiltering.Apply(_result.Issues, _filter)
            .Select(issue => ReviewIssueListItem.Create(issue, _context))
            .ToList();
        IssuesList.ItemsSource = filtered;
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_result.Issues.Count == 0 && _result.IsComplete)
        {
            EmptyStateTitle.Text = "Revisão concluída";
            EmptyStateDescription.Text = "Nenhum problema foi encontrado.";
        }
        else if (_result.Issues.Count == 0)
        {
            EmptyStateTitle.Text = "Revisão incompleta";
            EmptyStateDescription.Text = "As verificações concluídas não encontraram problemas.";
        }
        else if (filtered.Count == 0)
        {
            EmptyStateTitle.Text = "Nenhum resultado neste filtro";
            EmptyStateDescription.Text = "Escolha outro filtro para visualizar as demais verificações.";
        }

        VisibleCountText.Text = filtered.Count == 1
            ? "1 resultado exibido"
            : $"{filtered.Count} resultados exibidos";
    }

    private void RunReview_Click(object sender, RoutedEventArgs e) =>
        ReviewRequested?.Invoke(this, EventArgs.Empty);

    private void CancelReview_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReviewIssue issue })
            NavigateRequested?.Invoke(this, new ReviewIssueNavigationEventArgs(issue));
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value } ||
            !Enum.TryParse(value, out ReviewIssueFilter filter))
            return;

        SelectFilter(filter);
    }

    private IEnumerable<ToggleButton> FilterButtons()
    {
        yield return AllFilterButton;
        yield return ErrorsFilterButton;
        yield return WarningsFilterButton;
        yield return InformationFilterButton;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ReviewIssueListItem(
        ReviewIssue Issue,
        ReviewSeverity Severity,
        string SeverityLabel,
        string Title,
        string Description,
        string LocationText,
        Visibility LocationVisibility,
        string SectionText,
        Visibility SectionVisibility,
        string MeterageText,
        Visibility MeterageVisibility)
    {
        public static ReviewIssueListItem Create(
            ReviewIssue issue,
            ReportReviewContext context)
        {
            var section = issue.SectionId is Guid sectionId
                ? context.SelectedSections.FirstOrDefault(x => x.Section.Id == sectionId)
                : null;
            var location = section?.Location ?? (issue.LocationId is Guid locationId
                ? context.SelectedSections.FirstOrDefault(x => x.Location.Id == locationId)?.Location
                : null);
            var meterage = issue.Meterage is decimal value
                ? $"Metragem: {value:0.##} m"
                : string.Empty;

            return new ReviewIssueListItem(
                issue,
                issue.Severity,
                issue.Severity switch
                {
                    ReviewSeverity.Error => "Erro",
                    ReviewSeverity.Warning => "Aviso",
                    _ => "Informação"
                },
                issue.Title,
                issue.Description,
                location is null ? string.Empty : $"Local: {DisplayName(location.Name)}",
                location is null ? Visibility.Collapsed : Visibility.Visible,
                section is null ? string.Empty : $"Trecho: {DisplayName(section.Section.Name)}",
                section is null ? Visibility.Collapsed : Visibility.Visible,
                meterage,
                meterage.Length == 0 ? Visibility.Collapsed : Visibility.Visible);
        }

        private static string DisplayName(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(sem nome)" : value.Trim();
    }
}
