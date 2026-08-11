using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class OccurrenceWindow : Window
{
    private readonly Project _project;
    private readonly InspectionSection _section;
    private readonly Occurrence _draft;
    private readonly bool _readOnly;
    private readonly ObservableCollection<AnomalySelectionItem> _anomalyItems;
    public ObservableCollection<EvidenceSelectionItem> EvidenceItems { get; } = [];
    public Occurrence? SavedDraft { get; private set; }
    public bool ResumeVideoAfterSave { get; private set; }
    public Guid? RequestedVideoReferenceId { get; private set; }
    public TimeSpan? RequestedVideoTimestamp { get; private set; }

    public OccurrenceWindow(Project project, InspectionSection section,
        IReadOnlyList<AnomalyDefinition> catalog, OccurrenceService service,
        Occurrence? source = null, Capture? initialCapture = null, bool readOnly = false, bool duplicate = false)
    {
        InitializeComponent();
        DataContext = this;
        _project = project;
        _section = section;
        _readOnly = readOnly;
        _draft = service.CreateDraft(section, source, duplicate);
        WindowTitle.Text = readOnly ? "Consultar anomalia" : duplicate ? "Duplicar anomalia" :
            source is null ? "Nova anomalia" : "Editar anomalia";
        var location = project.Locations.FirstOrDefault(x => x.Sections.Any(y => y.Id == section.Id));
        var route = string.Join(" → ", new[] { section.StartPoint, section.EndPoint }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var routeAlreadyNamed = !string.IsNullOrWhiteSpace(section.StartPoint) && !string.IsNullOrWhiteSpace(section.EndPoint) &&
                                section.Name.Contains(section.StartPoint, StringComparison.CurrentCultureIgnoreCase) &&
                                section.Name.Contains(section.EndPoint, StringComparison.CurrentCultureIgnoreCase);
        var routeText = route.Length == 0 || routeAlreadyNamed ? string.Empty : $" — {route}";
        var diameter = section.Diameter is null ? "Rede: não informada" : $"Rede: {section.Diameter:N0} mm";
        SectionText.Text = $"{location?.Name ?? "LOCAL NÃO INFORMADO"}  •  {section.Name}{routeText}  •  {diameter}";

        var definitions = catalog.Concat(_draft.Anomalies.Select(x => x.AnomalyType))
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Order).ToList();
        _anomalyItems = new ObservableCollection<AnomalySelectionItem>(definitions.Select(definition =>
        {
            var selected = _draft.Anomalies.FirstOrDefault(x => x.AnomalyTypeId == definition.Id);
            return new AnomalySelectionItem(definition, selected);
        }));
        FrequentAnomaliesList.ItemsSource = _anomalyItems;
        AdditionalAnomaliesList.ItemsSource = _anomalyItems;
        CustomAnomalyFieldsList.ItemsSource = _anomalyItems;
        AdvancedAnomaliesList.ItemsSource = _anomalyItems;

        foreach (var evidence in _draft.Evidence.OrderBy(x => x.Order)) AddEvidenceItem(evidence);
        if (initialCapture is not null && EvidenceItems.All(x => x.Evidence.CaptureId != initialCapture.Id))
        {
            AddEvidenceItem(OccurrenceService.FromCapture(initialCapture));
            _draft.VideoReferenceId = initialCapture.VideoReferenceId;
            _draft.VideoTimestamp = initialCapture.VideoTimestamp;
        }

        var usedCaptureIds = project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
            .Where(x => x.Id != _draft.Id).SelectMany(x => x.Evidence).Where(x => x.CaptureId is not null)
            .Select(x => x.CaptureId!.Value).ToHashSet();
        PendingCaptureCombo.ItemsSource = project.Captures
            .Where(x => x.SectionId == section.Id && !usedCaptureIds.Contains(x.Id) &&
                        EvidenceItems.All(e => e.Evidence.CaptureId != x.Id))
            .OrderBy(x => x.VideoTimestamp).Select(x => new CaptureChoice(x)).ToList();
        PendingCaptureCombo.SelectedIndex = PendingCaptureCombo.Items.Count > 0 ? 0 : -1;

        LoadFields();
        FormPanel.IsEnabled = !readOnly;
        SaveButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        SaveAndNextButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        LeavePendingButton.Visibility = !readOnly && initialCapture is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = readOnly ? "Fechar" : "Cancelar";
        FooterHintText.Text = initialCapture is null ? "Detalhes opcionais." : "Captura anexada.";
        Loaded += (_, _) =>
        {
            if (Owner is not null)
            {
                var availableHeight = Math.Max(MinHeight, Owner.ActualHeight - 28);
                Height = Math.Min(800, availableHeight);
                Left = Math.Max(SystemParameters.WorkArea.Left, Owner.Left + Owner.ActualWidth - Width - 14);
                Top = Math.Max(SystemParameters.WorkArea.Top, Owner.Top + 14);
            }
            PointMeasurementBox.Focus();
            PointMeasurementBox.SelectAll();
        };
    }

    private void LoadFields()
    {
        PointRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.Point;
        RangeRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.Range;
        EntireSectionRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.EntireSection;
        PointMeasurementBox.Text = FormatDecimal(_draft.Meterage);
        RangeStartBox.Text = FormatDecimal(_draft.Meterage);
        RangeEndBox.Text = FormatDecimal(_draft.EndMeterage);
        ReferencePointBox.Text = _draft.ReferencePoint ?? "";
        NotesBox.Text = _draft.Notes ?? "";
        GeneratedDescriptionBox.Text = _draft.GeneratedDescription;
        ManualDescriptionBox.Text = _draft.ManualDescriptionOverride ?? "";
        UpdateLocationPanels();
        UpdateSelectedAnomalies();
        RefreshEvidencePresentation();
        UpdateDraftStatus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => CompleteSave(false);
    private void SaveAndNext_Click(object sender, RoutedEventArgs e) => CompleteSave(true);

    private void CompleteSave(bool resumeVideo)
    {
        try
        {
            ApplyFormToDraft();
            OccurrenceRules.Validate(_draft);
            _draft.GeneratedDescription = OccurrenceRules.GenerateDescription(_draft);
            SavedDraft = _draft;
            ResumeVideoAfterSave = resumeVideo;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Anomalia", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PreviewDescription_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyFormToDraft();
            OccurrenceRules.Validate(_draft);
            GeneratedDescriptionBox.Text = OccurrenceRules.GenerateDescription(_draft);
            _draft.GeneratedDescription = GeneratedDescriptionBox.Text;
            UpdateDraftStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Descrição no relatório", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ApplyFormToDraft()
    {
        _draft.LocationMode = PointRadio.IsChecked == true ? OccurrenceLocationMode.Point
            : RangeRadio.IsChecked == true ? OccurrenceLocationMode.Range
            : EntireSectionRadio.IsChecked == true ? OccurrenceLocationMode.EntireSection
            : OccurrenceLocationMode.NotApplicable;
        _draft.Meterage = _draft.LocationMode switch
        {
            OccurrenceLocationMode.Point => ParseRequired(PointMeasurementBox.Text, "metragem"),
            OccurrenceLocationMode.Range => ParseRequired(RangeStartBox.Text, "início do intervalo"),
            _ => null
        };
        _draft.EndMeterage = _draft.LocationMode == OccurrenceLocationMode.Range
            ? ParseRequired(RangeEndBox.Text, "fim do intervalo") : null;
        _draft.ReferencePoint = ReferencePointBox.Text;
        _draft.Notes = NotesBox.Text;
        _draft.ManualDescriptionOverride = ManualDescriptionBox.Text;
        _draft.Anomalies = _anomalyItems.Where(x => x.IsSelected).Select((x, index) => new OccurrenceAnomaly
        {
            OccurrenceId = _draft.Id, AnomalyTypeId = x.Definition.Id, AnomalyType = x.Definition,
            Position = x.Position, Orientation = x.Orientation,
            ApproximateExtension = ParseOptional(x.ExtensionText, $"extensão de {x.Definition.Name}"),
            Dimension = x.Dimension, Condition = x.Condition,
            InfiltrationCondition = x.InfiltrationCondition,
            EstimatedPercentage = ParseOptional(x.PercentageText, $"percentual de {x.Definition.Name}"),
            Notes = x.Notes, CustomText = x.CustomText, Order = index
        }).ToList();
        _draft.Evidence = EvidenceItems.Select((x, index) =>
        {
            x.Evidence.OccurrenceId = _draft.Id;
            x.Evidence.Order = index;
            return x.Evidence;
        }).ToList();
        var firstVideoEvidence = _draft.Evidence.FirstOrDefault(x => x.VideoReferenceId is not null && x.VideoTimestamp is not null);
        if (firstVideoEvidence is not null)
        {
            _draft.VideoReferenceId = firstVideoEvidence.VideoReferenceId;
            _draft.VideoTimestamp = firstVideoEvidence.VideoTimestamp;
        }
    }

    private void LocationMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateLocationPanels();
    }

    private void UpdateLocationPanels()
    {
        PointPanel.Visibility = PointRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RangePanel.Visibility = RangeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDraftStatus();
    }

    private void DraftFieldChanged(object sender, TextChangedEventArgs e) => UpdateDraftStatus();

    private void PointMeasurementBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PointMeasurementBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void AnomalySelection_Changed(object sender, RoutedEventArgs e) => UpdateSelectedAnomalies();

    private void UpdateSelectedAnomalies()
    {
        if (_anomalyItems is null) return;
        var selected = _anomalyItems.Where(x => x.IsSelected).Select(x => x.Definition.Name).ToList();
        SelectedAnomaliesText.Text = selected.Count == 0 ? "Nenhuma selecionada" :
            $"{selected.Count} selecionada(s): " + string.Join("  •  ", selected);
        CustomAnomalyFieldsList?.Items.Refresh();
        AdvancedAnomaliesList?.Items.Refresh();
        UpdateDraftStatus();
    }

    private void ToggleMoreAnomalies_Click(object sender, RoutedEventArgs e)
    {
        AdditionalAnomaliesPanel.Visibility = AdditionalAnomaliesPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        MoreAnomaliesButton.Content = AdditionalAnomaliesPanel.Visibility == Visibility.Visible ? "− Menos" : "+ Mais";
        if (AdditionalAnomaliesPanel.Visibility == Visibility.Visible) AnomalySearchBox.Focus();
    }

    private void AnomalySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_anomalyItems is null) return;
        var query = AnomalySearchBox.Text.Trim();
        foreach (var item in _anomalyItems)
            item.ShowInAdditionalList = !item.IsFrequent &&
                (query.Length == 0 || item.Definition.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ToggleImageMenu_Click(object sender, RoutedEventArgs e) =>
        AddImageMenuPanel.Visibility = AddImageMenuPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void AddPendingCapture_Click(object sender, RoutedEventArgs e)
    {
        if (PendingCaptureCombo.SelectedItem is not CaptureChoice choice) return;
        if (EvidenceItems.Any(x => x.Evidence.CaptureId == choice.Capture.Id))
        {
            MessageBox.Show("Esta captura já foi adicionada.", "Imagens da anomalia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddEvidenceItem(OccurrenceService.FromCapture(choice.Capture));
        RefreshEvidencePresentation();
        AddImageMenuPanel.Visibility = Visibility.Collapsed;
    }

    private void AddExternalImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Adicionar imagem da anomalia", Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true, Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddEvidenceItem(OccurrenceService.FromExternalImage(file));
        RefreshEvidencePresentation();
        AddImageMenuPanel.Visibility = Visibility.Collapsed;
    }

    private void AddEvidenceItem(Evidence evidence) =>
        EvidenceItems.Add(new EvidenceSelectionItem(evidence, ResolveVideoName));

    private string? ResolveVideoName(Guid id) => _project.Locations.SelectMany(x => x.Sections)
        .SelectMany(x => x.VideoReferences).FirstOrDefault(x => x.Id == id) is { } video
        ? (string.IsNullOrWhiteSpace(video.DisplayName) ? Path.GetFileName(video.FilePath) : video.DisplayName) : null;

    private void RemoveEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id && EvidenceItems.FirstOrDefault(x => x.Id == id) is { } item)
        {
            EvidenceItems.Remove(item);
            RefreshEvidencePresentation();
        }
    }

    private void MoveEvidenceUp_Click(object sender, RoutedEventArgs e) => MoveEvidence(sender, -1);
    private void MoveEvidenceDown_Click(object sender, RoutedEventArgs e) => MoveEvidence(sender, 1);

    private void MoveEvidence(object sender, int direction)
    {
        if (((Button)sender).Tag is not Guid id) return;
        var index = EvidenceItems.ToList().FindIndex(x => x.Id == id);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < EvidenceItems.Count)
        {
            EvidenceItems.Move(index, target);
            RefreshEvidencePresentation();
        }
    }

    private void PrimaryEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Guid id, IsChecked: true }) return;
        foreach (var item in EvidenceItems) item.Evidence.IsPrimary = item.Id == id;
        EvidenceList.Items.Refresh();
    }

    private void GoToEvidenceVideo_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Guid id || EvidenceItems.FirstOrDefault(x => x.Id == id) is not { } item ||
            item.Evidence.VideoReferenceId is not Guid videoId || item.Evidence.VideoTimestamp is null) return;
        RequestedVideoReferenceId = videoId;
        RequestedVideoTimestamp = item.Evidence.VideoTimestamp;
        Close();
    }

    private void RefreshEvidencePresentation()
    {
        for (var index = 0; index < EvidenceItems.Count; index++) EvidenceItems[index].RefreshOrder(index);
        EvidenceList?.Items.Refresh();
        UpdateDraftStatus();
    }

    private void UpdateDraftStatus()
    {
        if (DraftStatusText is null) return;
        var pending = new List<string>();
        if (_anomalyItems is null || _anomalyItems.All(x => !x.IsSelected)) pending.Add("sem anomalia");
        if (PointRadio?.IsChecked == true && ParseOptionalSilently(PointMeasurementBox?.Text) is null)
            pending.Add("sem metragem");
        if (RangeRadio?.IsChecked == true &&
            (ParseOptionalSilently(RangeStartBox?.Text) is null || ParseOptionalSilently(RangeEndBox?.Text) is null))
            pending.Add("intervalo incompleto");
        if (EvidenceItems.Count == 0) pending.Add("sem evidência");
        DraftStatusText.Text = pending.Count == 0 ? "Completa" : "Incompleta • " + string.Join(" • ", pending);
        DraftStatusText.Foreground = (System.Windows.Media.Brush)FindResource(pending.Count == 0 ? "SuccessBrush" : "WarningBrush");
    }

    private static decimal? ParseOptionalSilently(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void LeavePending_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static decimal ParseRequired(string text, string field) => ParseOptional(text, field) ??
        throw new InvalidOperationException($"Informe um valor válido para {field}.");

    private static decimal? ParseOptional(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return value;
        throw new InvalidOperationException($"Informe um valor válido para {field}.");
    }

    internal static string FormatDecimal(decimal? value) =>
        value?.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")) ?? "";

    internal sealed class CaptureChoice
    {
        public Capture Capture { get; }
        public string Label => $"{Capture.VideoTimestamp:hh\\:mm\\:ss\\.fff} — {Path.GetFileName(Capture.VideoFilePath)}";
        public CaptureChoice(Capture capture) => Capture = capture;
    }
}

public sealed class AnomalySelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _showInAdditionalList;
    private static readonly HashSet<string> FrequentNames = new(StringComparer.CurrentCultureIgnoreCase)
    {
        "Fissura", "Trinca", "Infiltração", "Obstrução", "Assoreamento", "Contribuição"
    };
    public AnomalyDefinition Definition { get; }
    public IReadOnlyList<PositionChoice> Positions { get; } = PositionChoice.All;
    public IReadOnlyList<OrientationChoice> Orientations { get; } = OrientationChoice.All;
    public IReadOnlyList<ConditionChoice> Conditions { get; } = ConditionChoice.All;
    public IReadOnlyList<InfiltrationChoice> InfiltrationConditions { get; } = InfiltrationChoice.All;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(); } }
    public bool IsFrequent => FrequentNames.Contains(Definition.Name);
    public bool ShowInAdditionalList { get => _showInAdditionalList; set { _showInAdditionalList = value; OnPropertyChanged(); } }
    public AnomalyPosition Position { get; set; }
    public AnomalyOrientation Orientation { get; set; }
    public string ExtensionText { get; set; } = "";
    public string? Dimension { get; set; }
    public AnomalyCondition Condition { get; set; }
    public InfiltrationCondition InfiltrationCondition { get; set; }
    public string PercentageText { get; set; } = "";
    public string? Notes { get; set; }
    public string? CustomText { get; set; }

    public AnomalySelectionItem(AnomalyDefinition definition, OccurrenceAnomaly? selected)
    {
        Definition = definition; IsSelected = selected is not null;
        ShowInAdditionalList = !IsFrequent;
        Position = selected?.Position ?? AnomalyPosition.NotReported;
        Orientation = selected?.Orientation ?? AnomalyOrientation.NotReported;
        ExtensionText = OccurrenceWindow.FormatDecimal(selected?.ApproximateExtension);
        Dimension = selected?.Dimension;
        Condition = selected?.Condition ?? AnomalyCondition.NotClassified;
        InfiltrationCondition = selected?.InfiltrationCondition ?? InfiltrationCondition.NotReported;
        PercentageText = OccurrenceWindow.FormatDecimal(selected?.EstimatedPercentage);
        Notes = selected?.Notes; CustomText = selected?.CustomText;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PositionChoice(AnomalyPosition Value, string Label)
{
    public static IReadOnlyList<PositionChoice> All { get; } = Enum.GetValues<AnomalyPosition>()
        .OrderBy(x => x == AnomalyPosition.NotReported ? 0 : x == AnomalyPosition.NotApplicable ? 99 : (int)x)
        .Select(x => new PositionChoice(x, OccurrenceRules.FormatPosition(x))).ToList();
}

public sealed record OrientationChoice(AnomalyOrientation Value, string Label)
{
    public static IReadOnlyList<OrientationChoice> All { get; } = Enum.GetValues<AnomalyOrientation>()
        .Select(x => new OrientationChoice(x, OccurrenceRules.FormatOrientation(x))).ToList();
}

public sealed record ConditionChoice(AnomalyCondition Value, string Label)
{
    public static IReadOnlyList<ConditionChoice> All { get; } = Enum.GetValues<AnomalyCondition>()
        .Select(x => new ConditionChoice(x, OccurrenceRules.FormatCondition(x))).ToList();
}

public sealed record InfiltrationChoice(InfiltrationCondition Value, string Label)
{
    public static IReadOnlyList<InfiltrationChoice> All { get; } = Enum.GetValues<InfiltrationCondition>()
        .Select(x => new InfiltrationChoice(x, OccurrenceRules.FormatInfiltration(x))).ToList();
}

public sealed class EvidenceSelectionItem : INotifyPropertyChanged
{
    public Evidence Evidence { get; }
    public Guid Id => Evidence.Id;
    public BitmapImage? Thumbnail { get; }
    public string Label => Evidence.SourceType == EvidenceSourceType.VideoCapture ? "Captura do vídeo" : Path.GetFileName(Evidence.FilePath);
    public string VideoName { get; }
    public string TimestampText => Evidence.VideoTimestamp is null ? "Sem timestamp" : $"Timestamp: {Evidence.VideoTimestamp:hh\\:mm\\:ss\\.fff}";
    public string OrderText { get; private set; } = "";
    public bool HasVideo => Evidence.VideoReferenceId is not null && Evidence.VideoTimestamp is not null;

    public EvidenceSelectionItem(Evidence evidence, Func<Guid, string?> videoNameResolver)
    {
        Evidence = evidence;
        VideoName = evidence.VideoReferenceId is Guid id ? $"Vídeo: {videoNameResolver(id) ?? "não localizado"}" : "Origem: imagem externa";
        Thumbnail = LoadThumbnail(evidence.FilePath);
    }

    public void RefreshOrder(int index)
    {
        Evidence.Order = index;
        OrderText = $"Ordem no relatório: {index + 1}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static BitmapImage? LoadThumbnail(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 320; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
