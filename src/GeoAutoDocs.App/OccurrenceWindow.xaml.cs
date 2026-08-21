using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly AnomalyDefinition _customAnomalyDefinition;
    private readonly IReadOnlyList<OccurrenceAnomaly> _preservedLegacyAnomalies;
    private readonly string _originalGeneratedDescription;
    private bool _loadingFields = true;
    public ObservableCollection<EvidenceSelectionItem> EvidenceItems { get; } = [];
    public ObservableCollection<MultiPointMeterageItem> MultiPointItems { get; } = [];
    public Occurrence? SavedDraft { get; private set; }
    public bool ResumeVideoAfterSave { get; private set; }
    public Guid? RequestedVideoReferenceId { get; private set; }
    public TimeSpan? RequestedVideoTimestamp { get; private set; }
    public bool IsEmbedded { get; set; }
    public bool? EmbeddedResult { get; private set; }
    public event EventHandler? EmbeddedCompleted;
    public Capture? AutomaticCapture { get; set; }
    private Func<Capture, TimeSpan, Task<Capture?>>? _recaptureAsync;
    public Func<Capture, TimeSpan, Task<Capture?>>? RecaptureAsync
    {
        get => _recaptureAsync;
        set { _recaptureAsync = value; if (IsInitialized) RefreshEvidencePresentation(); }
    }

    public OccurrenceWindow(Project project, InspectionSection section,
        IReadOnlyList<AnomalyDefinition> catalog, OccurrenceService service,
        Occurrence? source = null, Capture? initialCapture = null, bool readOnly = false, bool duplicate = false)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 820, 880);
        DataContext = this;
        _project = project;
        _section = section;
        _readOnly = readOnly;
        _draft = service.CreateDraft(section, source, duplicate);
        _originalGeneratedDescription = duplicate ? string.Empty : source?.GeneratedDescription ?? string.Empty;
        WindowTitle.Text = readOnly ? "←  Consultar anomalia" : duplicate ? "←  Duplicar anomalia" :
            source is null ? "←  Cadastrar anomalia" : "←  Editar anomalia";
        var location = project.Locations.FirstOrDefault(x => x.Sections.Any(y => y.Id == section.Id));
        var route = string.Join(" → ", new[] { section.StartPoint, section.EndPoint }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var routeAlreadyNamed = !string.IsNullOrWhiteSpace(section.StartPoint) && !string.IsNullOrWhiteSpace(section.EndPoint) &&
                                section.Name.Contains(section.StartPoint, StringComparison.CurrentCultureIgnoreCase) &&
                                section.Name.Contains(section.EndPoint, StringComparison.CurrentCultureIgnoreCase);
        var routeText = route.Length == 0 || routeAlreadyNamed ? string.Empty : $" — {route}";
        var diameter = section.Diameter is null ? "Rede: não informada" : $"Rede: {section.Diameter:N0} mm";
        SectionText.Text = $"{location?.Name ?? "LOCAL NÃO INFORMADO"}  •  {section.Name}{routeText}  •  {diameter}";

        var allDefinitions = catalog.Concat(_draft.Anomalies.Select(x => x.AnomalyType))
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Order).ToList();
        _customAnomalyDefinition = allDefinitions.FirstOrDefault(x => x.Id == AnomalyCatalog.OtherId)
            ?? AnomalyCatalog.Defaults.Single(x => x.Id == AnomalyCatalog.OtherId);
        var definitions = allDefinitions.Where(x => x.Id != AnomalyCatalog.OtherId).ToList();
        _preservedLegacyAnomalies = _draft.Anomalies
            .Where(x => definitions.All(y => y.Id != x.AnomalyTypeId) && x.AnomalyTypeId != AnomalyCatalog.OtherId)
            .OrderBy(x => x.Order).ToList();
        _anomalyItems = new ObservableCollection<AnomalySelectionItem>(definitions.Select(definition =>
        {
            var selected = _draft.Anomalies.FirstOrDefault(x => x.AnomalyTypeId == definition.Id);
            return new AnomalySelectionItem(definition, selected);
        }));
        FrequentAnomaliesList.ItemsSource = _anomalyItems;
        AdditionalAnomaliesList.ItemsSource = _anomalyItems;
        QuickAnomalyPositionsList.ItemsSource = _anomalyItems;
        AdvancedAnomaliesList.ItemsSource = _anomalyItems;
        foreach (var item in _anomalyItems)
            item.PropertyChanged += (_, _) => RefreshLivePreview();

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
        SaveAndNextButton.Visibility = Visibility.Collapsed;
        LeavePendingButton.Visibility = !readOnly && initialCapture is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = readOnly ? "Fechar" : "Cancelar";
        FooterHintText.Text = initialCapture is null ? "Detalhes opcionais." : "Captura anexada.";
        Loaded += (_, _) =>
        {
            PointMeasurementBox.Focus();
            PointMeasurementBox.SelectAll();
        };
    }

    private void LoadFields()
    {
        try
        {
            PointRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.Point;
            RangeRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.Range;
            MultiPointRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.MultiPoint;
            FromRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.From;
            EntireSectionRadio.IsChecked = _draft.LocationMode == OccurrenceLocationMode.EntireSection;
            PointMeasurementBox.Text = FormatDecimal(_draft.Meterage);
            RangeStartBox.Text = FormatDecimal(_draft.Meterage);
            RangeEndBox.Text = FormatDecimal(_draft.EndMeterage);
            FromMeasurementBox.Text = FormatDecimal(_draft.Meterage);
            MultiPointItems.Clear();
            foreach (var point in _draft.MeteragePoints.OrderBy(x => x.Meterage))
                MultiPointItems.Add(new MultiPointMeterageItem(point.Id, point.Meterage));
            ReferencePointBox.Text = _draft.ReferencePoint ?? "";
            NotesBox.Text = _draft.Notes ?? "";
            GeneratedDescriptionBox.Text = _draft.GeneratedDescription;
            ManualDescriptionBox.Text = _draft.ManualDescriptionOverride ?? "";
            InterruptedRecordingCheck.IsChecked = _draft.InterruptedRecording;
            SevereAnomalyCheck.IsChecked = _draft.IsSevere;
            CustomAnomalyBox.Text = _draft.Anomalies.FirstOrDefault(x => x.AnomalyTypeId == AnomalyCatalog.OtherId)?.CustomText ?? "";
            UpdateLocationPanels();
            UpdateSelectedAnomalies();
            RefreshEvidencePresentation();
            UpdateDraftStatus();
        }
        finally { _loadingFields = false; }
        RefreshLivePreview();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => CompleteSave(false);
    private void SaveAndNext_Click(object sender, RoutedEventArgs e) => CompleteSave(true);

    private void CompleteSave(bool resumeVideo)
    {
        try
        {
            ApplyFormToDraft();
            OccurrenceRules.Validate(_draft);
            var generated = OccurrenceRules.GenerateDescription(_draft);
            if (string.IsNullOrWhiteSpace(_originalGeneratedDescription) || generated == _originalGeneratedDescription ||
                MessageBox.Show("Os dados técnicos alteraram a descrição. Deseja regenerá-la?", "Descrição técnica",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _draft.GeneratedDescription = generated;
            else _draft.GeneratedDescription = _originalGeneratedDescription;
            SavedDraft = _draft;
            ResumeVideoAfterSave = resumeVideo;
            CompleteEditor(true);
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
            : MultiPointRadio.IsChecked == true ? OccurrenceLocationMode.MultiPoint
            : FromRadio.IsChecked == true ? OccurrenceLocationMode.From
            : EntireSectionRadio.IsChecked == true ? OccurrenceLocationMode.EntireSection
            : OccurrenceLocationMode.NotApplicable;
        _draft.Meterage = _draft.LocationMode switch
        {
            OccurrenceLocationMode.Point => ParseRequired(PointMeasurementBox.Text, "metragem"),
            OccurrenceLocationMode.Range => ParseRequired(RangeStartBox.Text, "início do intervalo"),
            OccurrenceLocationMode.MultiPoint => MultiPointItems.FirstOrDefault()?.Value,
            OccurrenceLocationMode.From => ParseRequired(FromMeasurementBox.Text, "metragem inicial"),
            _ => null
        };
        _draft.EndMeterage = _draft.LocationMode == OccurrenceLocationMode.Range
            ? ParseRequired(RangeEndBox.Text, "fim do intervalo") : null;
        _draft.MeteragePoints = _draft.LocationMode == OccurrenceLocationMode.MultiPoint
            ? MultiPointItems.Select((x, index) => new OccurrenceMeteragePoint
            {
                Id = x.Id, OccurrenceId = _draft.Id, Meterage = x.Value, Order = index
            }).ToList()
            : [];
        _draft.ReferencePoint = ReferencePointBox.Text;
        _draft.Notes = NotesBox.Text;
        _draft.ManualDescriptionOverride = ManualDescriptionBox.Text;
        _draft.InterruptedRecording = InterruptedRecordingCheck.IsChecked == true;
        _draft.IsSevere = SevereAnomalyCheck.IsChecked == true;
        var anomalies = _anomalyItems.Where(x => x.IsSelected).Select((x, index) => new OccurrenceAnomaly
        {
            OccurrenceId = _draft.Id, AnomalyTypeId = x.Definition.Id, AnomalyType = x.Definition,
            AnomalyName = x.Definition.Name,
            Position = x.Position, Orientation = x.Orientation,
            ApproximateExtension = ParseOptional(x.ExtensionText, $"extensão de {x.Definition.Name}"),
            Dimension = x.Dimension, Condition = x.Condition,
            InfiltrationCondition = x.InfiltrationCondition,
            EstimatedPercentage = ParseOptional(x.PercentageText, $"percentual de {x.Definition.Name}"),
            Notes = x.Notes, CustomText = x.CustomText, Order = index
        }).ToList();
        foreach (var legacy in _preservedLegacyAnomalies)
        {
            legacy.OccurrenceId = _draft.Id;
            legacy.Order = anomalies.Count;
            anomalies.Add(legacy);
        }
        if (!string.IsNullOrWhiteSpace(CustomAnomalyBox.Text))
        {
            anomalies.Add(new OccurrenceAnomaly
            {
                OccurrenceId = _draft.Id,
                AnomalyTypeId = _customAnomalyDefinition.Id,
                AnomalyType = _customAnomalyDefinition,
                AnomalyName = _customAnomalyDefinition.Name,
                CustomText = CustomAnomalyBox.Text.Trim(),
                Position = AnomalyPosition.NotApplicable,
                Order = anomalies.Count
            });
        }
        _draft.Anomalies = anomalies;
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
        MultiPointPanel.Visibility = MultiPointRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FromPanel.Visibility = FromRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDraftStatus();
    }

    private void DraftFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (sender == CustomAnomalyBox)
        {
            UpdateSelectedAnomalies();
            return;
        }
        UpdateDraftStatus();
        RefreshLivePreview();
    }

    private void PointMeasurementBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PointMeasurementBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void MultiPointInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddMultiPoint();
        e.Handled = true;
    }

    private void AddMultiPoint_Click(object sender, RoutedEventArgs e) => AddMultiPoint();

    private void AddMultiPoint()
    {
        if (!MeasurementText.TryParse(MultiPointInputBox.Text, out var value) || value < 0)
        {
            MessageBox.Show("Informe uma metragem válida e não negativa.", "Múltiplos pontos",
                MessageBoxButton.OK, MessageBoxImage.Information);
            MultiPointInputBox.Focus();
            return;
        }
        if (MultiPointItems.Any(x => x.Value == value))
        {
            MessageBox.Show("Essa metragem já foi adicionada.", "Múltiplos pontos",
                MessageBoxButton.OK, MessageBoxImage.Information);
            MultiPointInputBox.Focus();
            return;
        }
        var values = MultiPointItems.Append(new MultiPointMeterageItem(Guid.NewGuid(), value))
            .OrderBy(x => x.Value).ToList();
        MultiPointItems.Clear();
        foreach (var item in values) MultiPointItems.Add(item);
        MultiPointInputBox.Clear();
        MultiPointInputBox.Focus();
        UpdateDraftStatus();
        RefreshLivePreview();
    }

    private void RemoveMultiPoint_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id && MultiPointItems.FirstOrDefault(x => x.Id == id) is { } item)
            MultiPointItems.Remove(item);
        UpdateDraftStatus();
        RefreshLivePreview();
    }

    private void AnomalySelection_Changed(object sender, RoutedEventArgs e) => UpdateSelectedAnomalies();

    private void UpdateSelectedAnomalies()
    {
        if (_anomalyItems is null) return;
        var selected = _anomalyItems.Where(x => x.IsSelected).Select(x => x.Definition.Name).ToList();
        if (!string.IsNullOrWhiteSpace(CustomAnomalyBox?.Text)) selected.Add(CustomAnomalyBox.Text.Trim());
        SelectedAnomaliesText.Text = selected.Count == 0 ? "Nenhuma selecionada" :
            $"{selected.Count} selecionada(s): " + string.Join("  •  ", selected);
        CustomAnomalyFieldsList?.Items.Refresh();
        QuickAnomalyPositionsList?.Items.Refresh();
        AdvancedAnomaliesList?.Items.Refresh();
        UpdateDraftStatus();
        RefreshLivePreview();
    }

    private void AnomalyPosition_Changed(object sender, SelectionChangedEventArgs e) => RefreshLivePreview();

    private void LivePreview_Changed(object sender, RoutedEventArgs e) => RefreshLivePreview();

    private void RefreshLivePreview()
    {
        if (_loadingFields || ReportPreviewText is null || _anomalyItems is null) return;
        if (PrimaryMeterageText is not null)
            PrimaryMeterageText.Text = PointRadio?.IsChecked == true
                ? ParseOptionalSilently(PointMeasurementBox?.Text) is decimal point ? $"{MeasurementText.Format(point)} m" : "Metragem não informada"
                : RangeRadio?.IsChecked == true ? "Intervalo"
                : MultiPointRadio?.IsChecked == true ? (MultiPointItems.Count == 0 ? "Múltiplos pontos" : string.Join(" • ", MultiPointItems.Select(x => $"{MeasurementText.Format(x.Value)} m")))
                : FromRadio?.IsChecked == true ? ParseOptionalSilently(FromMeasurementBox?.Text) is decimal start ? $"A partir de {MeasurementText.Format(start)} m" : "Metragem não informada"
                : "Todo o trecho";
        try
        {
            ApplyFormToDraft();
            ReportPreviewText.Text = _draft.Anomalies.Count == 0
                ? "Selecione ao menos uma anomalia para visualizar a descrição."
                : OccurrenceRules.GenerateDescription(_draft);
        }
        catch
        {
            ReportPreviewText.Text = "Informe uma metragem válida para visualizar a descrição.";
        }
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
        if (dialog.ShowDialog(IsEmbedded ? Application.Current.MainWindow : this) != true) return;
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
        CompleteEditor(null);
    }

    private void RefreshEvidencePresentation()
    {
        if (EvidenceItems.Count > 0 && EvidenceItems.All(x => !x.Evidence.IsPrimary))
            EvidenceItems[0].Evidence.IsPrimary = true;
        for (var index = 0; index < EvidenceItems.Count; index++) EvidenceItems[index].RefreshOrder(index);
        EvidenceList?.Items.Refresh();
        if (PrimaryImage is not null)
        {
            var primary = EvidenceItems.FirstOrDefault(x => x.Evidence.IsPrimary) ?? EvidenceItems.FirstOrDefault();
            PrimaryImage.Source = primary?.OriginalImage;
            PrimaryImageEmptyState.Visibility = primary?.OriginalImage is null ? Visibility.Visible : Visibility.Collapsed;
            ViewOriginalButton.IsEnabled = primary?.OriginalImage is not null;
            PrimaryTimestampText.Text = primary?.Evidence.VideoTimestamp is TimeSpan timestamp
                ? timestamp.ToString(@"hh\:mm\:ss\.fff") : "Sem timestamp";
            PrimaryVideoText.Text = primary?.VideoName ?? "Origem não informada";
            AdditionalEvidenceList.ItemsSource = EvidenceItems.Where(x => x != primary).ToList();
            RecaptureButton.IsEnabled = !_readOnly && primary?.Evidence.CaptureId is not null && RecaptureAsync is not null;
        }
        UpdateDraftStatus();
    }

    private void ViewOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryImage.Source is not BitmapImage original) return;
        var image = new Image
        {
            Source = original,
            Stretch = System.Windows.Media.Stretch.None,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image,
            System.Windows.Media.BitmapScalingMode.HighQuality);
        var viewer = new Window
        {
            Title = "Imagem original — 100%",
            Owner = Window.GetWindow(PrimaryImage),
            Width = 1200,
            Height = 800,
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush"),
            Content = new ScrollViewer
            {
                Content = image,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(12)
            }
        };
        WindowLayout.KeepInsideWorkArea(viewer, 1200, 800);
        viewer.ShowDialog();
    }

    private async void RecaptureFrame_Click(object sender, RoutedEventArgs e) => await RecaptureAtOffsetAsync(TimeSpan.Zero);
    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) => await RecaptureAtOffsetAsync(TimeSpan.FromSeconds(-1));
    private async void NextFrame_Click(object sender, RoutedEventArgs e) => await RecaptureAtOffsetAsync(TimeSpan.FromSeconds(1));

    private async Task RecaptureAtOffsetAsync(TimeSpan offset)
    {
        var primary = EvidenceItems.FirstOrDefault(x => x.Evidence.IsPrimary) ?? EvidenceItems.FirstOrDefault();
        if (primary?.Evidence.CaptureId is not Guid captureId || RecaptureAsync is null) return;
        var current = _project.Captures.FirstOrDefault(x => x.Id == captureId);
        if (current is null) return;
        var timestamp = (primary.Evidence.VideoTimestamp ?? TimeSpan.Zero) + offset;
        if (timestamp < TimeSpan.Zero) timestamp = TimeSpan.Zero;
        RecaptureButton.IsEnabled = false;
        FrameHelpText.Text = "Extraindo o frame diretamente do vídeo...";
        try
        {
            var replacement = await RecaptureAsync(current, timestamp);
            if (replacement is null) return;
            var index = EvidenceItems.IndexOf(primary);
            EvidenceItems.Remove(primary);
            var replacementItem = new EvidenceSelectionItem(OccurrenceService.FromCapture(replacement), ResolveVideoName);
            replacementItem.Evidence.IsPrimary = true;
            EvidenceItems.Insert(Math.Max(0, index), replacementItem);
            if (AutomaticCapture?.Id == current.Id) AutomaticCapture = replacement;
            _draft.VideoReferenceId = replacement.VideoReferenceId;
            _draft.VideoTimestamp = replacement.VideoTimestamp;
            FrameHelpText.Text = "Frame atualizado no novo timestamp.";
            RefreshEvidencePresentation();
        }
        catch (Exception ex)
        {
            FrameHelpText.Text = "Não foi possível refazer a captura.";
            MessageBox.Show(ex.Message, "Refazer captura", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RecaptureButton.IsEnabled = !_readOnly && RecaptureAsync is not null; }
    }

    private void UpdateDraftStatus()
    {
        if (DraftStatusText is null) return;
        var pending = new List<string>();
        if ((_anomalyItems is null || _anomalyItems.All(x => !x.IsSelected)) &&
            string.IsNullOrWhiteSpace(CustomAnomalyBox?.Text)) pending.Add("sem anomalia");
        if (PointRadio?.IsChecked == true && ParseOptionalSilently(PointMeasurementBox?.Text) is null)
            pending.Add("sem metragem");
        if (RangeRadio?.IsChecked == true &&
            (ParseOptionalSilently(RangeStartBox?.Text) is null || ParseOptionalSilently(RangeEndBox?.Text) is null))
            pending.Add("intervalo incompleto");
        if (MultiPointRadio?.IsChecked == true && MultiPointItems.Count < 2)
            pending.Add("adicione dois pontos");
        if (FromRadio?.IsChecked == true && ParseOptionalSilently(FromMeasurementBox?.Text) is null)
            pending.Add("sem metragem inicial");
        if (EvidenceItems.Count == 0) pending.Add("sem evidência");
        DraftStatusText.Text = pending.Count == 0 ? "Completa" : "Incompleta • " + string.Join(" • ", pending);
        DraftStatusText.Foreground = (System.Windows.Media.Brush)FindResource(pending.Count == 0 ? "SuccessBrush" : "WarningBrush");
    }

    private static decimal? ParseOptionalSilently(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return MeasurementText.TryParse(text, out var value) ? value : null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CompleteEditor(null);

    private void HeaderBack_Click(object sender, MouseButtonEventArgs e) => CompleteEditor(null);

    private void LeavePending_Click(object sender, RoutedEventArgs e) => CompleteEditor(false);

    public void CancelEmbedded() => CompleteEditor(null);

    private void CompleteEditor(bool? result)
    {
        if (!IsEmbedded)
        {
            if (result is null) Close();
            else DialogResult = result;
            return;
        }
        EmbeddedResult = result;
        EmbeddedCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static decimal ParseRequired(string text, string field) => ParseOptional(text, field) ??
        throw new InvalidOperationException($"Informe um valor válido para {field}.");

    private static decimal? ParseOptional(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (MeasurementText.TryParse(text, out var value)) return value;
        throw new InvalidOperationException($"Informe um valor válido para {field}.");
    }

    internal static string FormatDecimal(decimal? value) => MeasurementText.Format(value);

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
    private AnomalyPosition _position;
    private string? _customText;
    public AnomalyDefinition Definition { get; }
    public IReadOnlyList<PositionChoice> Positions { get; } = PositionChoice.All;
    public IReadOnlyList<OrientationChoice> Orientations { get; } = OrientationChoice.All;
    public IReadOnlyList<ConditionChoice> Conditions { get; } = ConditionChoice.All;
    public IReadOnlyList<InfiltrationChoice> InfiltrationConditions { get; } = InfiltrationChoice.All;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(); } }
    public bool IsFrequent => true;
    public bool ShowInAdditionalList { get => _showInAdditionalList; set { _showInAdditionalList = value; OnPropertyChanged(); } }
    public AnomalyPosition Position { get => _position; set { if (_position == value) return; _position = value; OnPropertyChanged(); } }
    public AnomalyOrientation Orientation { get; set; }
    public string ExtensionText { get; set; } = "";
    public string? Dimension { get; set; }
    public AnomalyCondition Condition { get; set; }
    public InfiltrationCondition InfiltrationCondition { get; set; }
    public string PercentageText { get; set; } = "";
    public string? Notes { get; set; }
    public string? CustomText { get => _customText; set { if (_customText == value) return; _customText = value; OnPropertyChanged(); } }

    public AnomalySelectionItem(AnomalyDefinition definition, OccurrenceAnomaly? selected)
    {
        Definition = definition; IsSelected = selected is not null;
        ShowInAdditionalList = !IsFrequent;
        Position = selected?.Position ?? AnomalyPosition.NotApplicable;
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

public sealed record MultiPointMeterageItem(Guid Id, decimal Value)
{
    public string Label => $"{MeasurementText.Format(Value)} m";
}

public sealed record PositionChoice(AnomalyPosition Value, string Label)
{
    private static readonly HashSet<AnomalyPosition> OperationalPositions =
    [
        AnomalyPosition.NotApplicable, AnomalyPosition.UpperGenerator, AnomalyPosition.LowerGenerator,
        AnomalyPosition.LeftSide, AnomalyPosition.RightSide, AnomalyPosition.Circumferential
    ];
    public static IReadOnlyList<PositionChoice> All { get; } = OccurrenceRules.SelectablePositions
        .Where(OperationalPositions.Contains)
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
    private BitmapImage? _originalImage;
    private bool _originalImageLoaded;
    public Evidence Evidence { get; }
    public Guid Id => Evidence.Id;
    public BitmapImage? Thumbnail { get; }
    public BitmapImage? OriginalImage
    {
        get
        {
            if (_originalImageLoaded) return _originalImage;
            _originalImageLoaded = true;
            return _originalImage = LoadOriginal(Evidence.FilePath);
        }
    }
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

    private static BitmapImage? LoadOriginal(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
