using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class OccurrenceWindow : Window
{
    private readonly Occurrence _draft;
    private readonly bool _readOnly;
    private readonly ObservableCollection<AnomalySelectionItem> _anomalyItems;
    public ObservableCollection<EvidenceSelectionItem> EvidenceItems { get; } = [];
    public Occurrence? SavedDraft { get; private set; }

    public OccurrenceWindow(Project project, InspectionSection section,
        IReadOnlyList<AnomalyDefinition> catalog, OccurrenceService service,
        Occurrence? source = null, Capture? initialCapture = null, bool readOnly = false)
    {
        InitializeComponent();
        DataContext = this;
        _readOnly = readOnly;
        _draft = service.CreateDraft(section, source);
        WindowTitle.Text = readOnly ? "CONSULTAR OCORRÊNCIA" : source is null ? "NOVA OCORRÊNCIA" : "EDITAR OCORRÊNCIA";
        SectionText.Text = section.Diameter is null ? section.Name : $"{section.Name} — REDE {section.Diameter:N0} mm";

        var definitions = catalog.Concat(_draft.Anomalies.Select(x => x.AnomalyType))
            .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Order).ToList();
        _anomalyItems = new ObservableCollection<AnomalySelectionItem>(definitions.Select(definition =>
        {
            var selected = _draft.Anomalies.FirstOrDefault(x => x.AnomalyTypeId == definition.Id);
            return new AnomalySelectionItem(definition, selected);
        }));
        AnomaliesList.ItemsSource = _anomalyItems;

        foreach (var evidence in _draft.Evidence.OrderBy(x => x.Order))
            EvidenceItems.Add(new EvidenceSelectionItem(evidence));
        if (initialCapture is not null && EvidenceItems.All(x => x.Evidence.CaptureId != initialCapture.Id))
            EvidenceItems.Add(new EvidenceSelectionItem(OccurrenceService.FromCapture(initialCapture)));

        var usedCaptureIds = project.Locations.SelectMany(x => x.Sections).SelectMany(x => x.Occurrences)
            .Where(x => x.Id != _draft.Id).SelectMany(x => x.Evidence).Where(x => x.CaptureId is not null)
            .Select(x => x.CaptureId!.Value).ToHashSet();
        PendingCaptureCombo.ItemsSource = project.Captures
            .Where(x => x.SectionId == section.Id && !usedCaptureIds.Contains(x.Id))
            .OrderBy(x => x.VideoTimestamp).Select(x => new CaptureChoice(x)).ToList();
        PendingCaptureCombo.SelectedIndex = PendingCaptureCombo.Items.Count > 0 ? 0 : -1;

        LoadFields();
        FormPanel.IsEnabled = !readOnly;
        SaveButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = readOnly ? "Fechar" : "Cancelar";
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
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyFormToDraft();
            OccurrenceRules.Validate(_draft);
            _draft.GeneratedDescription = OccurrenceRules.GenerateDescription(_draft);
            SavedDraft = _draft;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ocorrência", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PreviewDescription_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyFormToDraft();
            OccurrenceRules.Validate(_draft);
            GeneratedDescriptionBox.Text = OccurrenceRules.GenerateDescription(_draft);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Descrição padronizada", MessageBoxButton.OK, MessageBoxImage.Information);
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
            Position = x.Position, CustomText = x.CustomText, Order = index
        }).ToList();
        _draft.Evidence = EvidenceItems.Select((x, index) =>
        {
            x.Evidence.OccurrenceId = _draft.Id;
            x.Evidence.Order = index;
            return x.Evidence;
        }).ToList();
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
    }

    private void AnomalySelection_Changed(object sender, RoutedEventArgs e) => UpdateSelectedAnomalies();

    private void UpdateSelectedAnomalies()
    {
        if (_anomalyItems is null) return;
        var selected = _anomalyItems.Where(x => x.IsSelected).Select(x => x.Definition.Name).ToList();
        SelectedAnomaliesText.Text = selected.Count == 0 ? "Nenhuma selecionada" : "Selecionadas: " + string.Join("  •  ", selected);
    }

    private void AddPendingCapture_Click(object sender, RoutedEventArgs e)
    {
        if (PendingCaptureCombo.SelectedItem is not CaptureChoice choice) return;
        if (EvidenceItems.Any(x => x.Evidence.CaptureId == choice.Capture.Id))
        {
            MessageBox.Show("Esta captura já foi adicionada.", "Evidências", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        EvidenceItems.Add(new EvidenceSelectionItem(OccurrenceService.FromCapture(choice.Capture)));
    }

    private void AddExternalImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Adicionar evidência externa", Filter = "Imagens (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true, Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
            EvidenceItems.Add(new EvidenceSelectionItem(OccurrenceService.FromExternalImage(file)));
    }

    private void RemoveEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Guid id && EvidenceItems.FirstOrDefault(x => x.Id == id) is { } item)
            EvidenceItems.Remove(item);
    }

    private void MoveEvidenceUp_Click(object sender, RoutedEventArgs e) => MoveEvidence(sender, -1);
    private void MoveEvidenceDown_Click(object sender, RoutedEventArgs e) => MoveEvidence(sender, 1);

    private void MoveEvidence(object sender, int direction)
    {
        if (((Button)sender).Tag is not Guid id) return;
        var index = EvidenceItems.ToList().FindIndex(x => x.Id == id);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < EvidenceItems.Count) EvidenceItems.Move(index, target);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private static decimal ParseRequired(string text, string field)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out value) ||
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return value;
        throw new InvalidOperationException($"Informe um valor válido para {field}.");
    }

    private static string FormatDecimal(decimal? value) => value?.ToString("N2", CultureInfo.GetCultureInfo("pt-BR")) ?? "";

    internal sealed class CaptureChoice
    {
        public Capture Capture { get; }
        public string Label => $"{Capture.VideoTimestamp.ToString(@"hh\:mm\:ss\.fff")} — {Path.GetFileName(Capture.VideoFilePath)}";
        public CaptureChoice(Capture capture) => Capture = capture;
    }
}

public sealed class AnomalySelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public AnomalyDefinition Definition { get; }
    public IReadOnlyList<PositionChoice> Positions { get; } = PositionChoice.All;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public AnomalyPosition Position { get; set; }
    public string? CustomText { get; set; }

    public AnomalySelectionItem(AnomalyDefinition definition, OccurrenceAnomaly? selected)
    {
        Definition = definition; IsSelected = selected is not null;
        Position = selected?.Position ?? AnomalyPosition.NotReported; CustomText = selected?.CustomText;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PositionChoice(AnomalyPosition Value, string Label)
{
    public static IReadOnlyList<PositionChoice> All { get; } =
    [
        new(AnomalyPosition.NotReported, "Não informado"), new(AnomalyPosition.UpperGenerator, "Geratriz superior"),
        new(AnomalyPosition.LowerGenerator, "Geratriz inferior"), new(AnomalyPosition.LeftSide, "Lateral esquerda"),
        new(AnomalyPosition.RightSide, "Lateral direita"), new(AnomalyPosition.Circumferential, "Circunferencial"),
        new(AnomalyPosition.NotApplicable, "Não aplicável")
    ];
}

public sealed class EvidenceSelectionItem
{
    public Evidence Evidence { get; }
    public Guid Id => Evidence.Id;
    public BitmapImage? Thumbnail { get; }
    public string Label => Evidence.SourceType == EvidenceSourceType.VideoCapture
        ? $"Vídeo — {Evidence.VideoTimestamp?.ToString(@"hh\:mm\:ss\.fff")}" : Path.GetFileName(Evidence.FilePath);

    public EvidenceSelectionItem(Evidence evidence)
    {
        Evidence = evidence;
        Thumbnail = LoadThumbnail(evidence.FilePath);
    }

    private static BitmapImage? LoadThumbnail(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 220; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
