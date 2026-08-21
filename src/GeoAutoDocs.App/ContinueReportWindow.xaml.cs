using System.Diagnostics;
using System.IO;
using System.Windows;
using GeoAutoDocs.Core;
using GeoAutoDocs.Infrastructure;
using Microsoft.Win32;

namespace GeoAutoDocs.App;

public partial class ContinueReportWindow : Window
{
    private readonly Project _project;
    private readonly Report _report;
    private readonly WordReportService _service;
    private ReportContinuationAnalysis? _analysis;

    public ContinueReportWindow(Project project, Report report, WordReportService service)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 920, 720);
        _project = project;
        _report = report;
        _service = service;
    }

    public ReportContinuationResult? Result { get; private set; }

    private async void SelectSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar relatório existente",
            Filter = "Documento Word (*.docx)|*.docx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        SourcePathBox.Text = dialog.FileName;
        await AnalyzeAsync(dialog.FileName);
    }

    private async Task AnalyzeAsync(string path)
    {
        SetBusy(true, "Analisando estrutura, manifesto e conteúdo do relatório...");
        try
        {
            _analysis = await _service.AnalyzeContinuationAsync(_project, _report, path);
            ItemsList.ItemsSource = _analysis.Items;
            SummaryPanel.Visibility = Visibility.Visible;
            DocumentSummaryText.Text = $"Páginas: {(_analysis.PageCount > 0 ? _analysis.PageCount : "não disponível")}\n" +
                                       $"Locais identificados: {_analysis.LocationCount}\n" +
                                       $"Trechos identificados: {_analysis.SectionCount}\n" +
                                       $"Última figura: 4.{_analysis.LastFigureNumber}";
            ChangeSummaryText.Text = $"Já exportadas: {_analysis.ExistingCount}\n" +
                                     $"Novas: {_analysis.NewCount}\n" +
                                     $"Alteradas: {_analysis.ModifiedCount}\n" +
                                     $"Removidas: {_analysis.RemovedCount}\n" +
                                     $"Imagens ausentes: {_analysis.MissingImageCount}";
            var messages = _analysis.Errors.Select(x => "Erro: " + x)
                .Concat(_analysis.Warnings.Select(x => "Aviso: " + x));
            MessagesText.Text = string.Join("\n", messages);
            LegacyReviewCheck.Visibility = _analysis.RequiresManualReview ? Visibility.Visible : Visibility.Collapsed;
            LegacyReviewCheck.IsChecked = false;
            SelectionHint.Text = "Novas são selecionadas automaticamente; alterações exigem escolha.";
            UpdateGenerateState();
        }
        catch (Exception ex)
        {
            _analysis = null;
            ItemsList.ItemsSource = null;
            SummaryPanel.Visibility = Visibility.Collapsed;
            MessagesText.Text = "Erro: " + ex.Message;
            GenerateButton.IsEnabled = false;
        }
        finally { SetBusy(false); }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) return;
        var selected = _analysis.Items.Where(x => x.IsSelected && x.CanSelect).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Selecione ao menos uma anomalia nova ou alterada.", "GeoAutoDocs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var missing = selected.Sum(x => x.MissingImageCount);
        if (missing > 0 && MessageBox.Show(
                $"{missing} imagem(ns) selecionada(s) não foi(ram) encontrada(s). Continuar sem essas imagens?",
                "Imagens ausentes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true, "Gerando nova revisão sem alterar o arquivo original...");
        try
        {
            Result = await _service.ContinueReportAsync(_project, _report, _analysis.SourcePath,
                selected.Select(x => x.OccurrenceId), LegacyReviewCheck.IsChecked == true);
            ResultText.Text = $"✓ R{Result.Version:00} criada: {Path.GetFileName(Result.OutputPath)}\n" +
                              $"{Result.AddedOccurrences} anomalia(s) e {Result.AddedEvidences} evidência(s) adicionada(s).";
            OpenResultButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
            GenerateButton.IsEnabled = false;
            CloseButton.Content = "Concluir";
        }
        catch (Exception ex)
        {
            MessagesText.Text = "Erro: " + ex.Message;
            UpdateGenerateState();
        }
        finally { SetBusy(false); }
    }

    private void ReviewChanged(object sender, RoutedEventArgs e) => UpdateGenerateState();

    private void UpdateGenerateState() => GenerateButton.IsEnabled = _analysis is { CanGenerate: true } &&
        (!_analysis.RequiresManualReview || LegacyReviewCheck.IsChecked == true) && Result is null;

    private void SetBusy(bool busy, string? message = null)
    {
        IsEnabled = !busy;
        if (busy && message is not null) MessagesText.Text = message;
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Result?.OutputPath))
            Process.Start(new ProcessStartInfo(Result!.OutputPath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Result?.OutputPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Result!.OutputPath}\"") { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Result is not null) DialogResult = true;
        Close();
    }
}
