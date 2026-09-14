using System.Windows;

namespace GeoAutoDocs.App;

public enum ReportGenerationReviewDialogMode
{
    Errors,
    Warnings,
    Incomplete
}

public enum ReportGenerationReviewChoice
{
    Cancel,
    OpenReview,
    GenerateAnyway,
    Retry
}

public partial class ReportGenerationReviewDialog : Window
{
    private readonly ReportGenerationReviewDialogMode _mode;

    public ReportGenerationReviewDialog(
        ReportGenerationReviewDialogMode mode,
        int issueCount = 0,
        int failedRuleCount = 0)
    {
        InitializeComponent();
        WindowLayout.KeepInsideWorkArea(this, 590, 360);
        _mode = mode;

        switch (mode)
        {
            case ReportGenerationReviewDialogMode.Errors:
                DialogTitleText.Text = "Não foi possível gerar o relatório";
                DialogDescriptionText.Text =
                    $"A revisão encontrou {issueCount} erro(s) que precisam ser corrigidos.";
                DialogDetailText.Text =
                    "Abra a revisão para localizar cada problema e usar “Ir até”.";
                SecondaryActionButton.Visibility = Visibility.Collapsed;
                PrimaryActionButton.Content = "Ver revisão";
                break;

            case ReportGenerationReviewDialogMode.Warnings:
                DialogTitleText.Text = "A revisão encontrou avisos";
                DialogDescriptionText.Text =
                    $"Foram encontrados {issueCount} aviso(s). Eles não impedem a geração, " +
                    "mas podem indicar informações que merecem conferência.";
                DialogDetailText.Text =
                    "Você pode revisar os avisos ou continuar esta tentativa de geração.";
                SecondaryActionButton.Content = "Revisar";
                PrimaryActionButton.Content = "Gerar mesmo assim";
                break;

            default:
                DialogTitleText.Text = "A revisão está incompleta";
                DialogDescriptionText.Text =
                    "Não foi possível concluir todas as verificações. O documento não será " +
                    "gerado até que a revisão termine com sucesso.";
                DialogDetailText.Text = failedRuleCount == 1
                    ? "Uma verificação falhou. Os demais resultados continuam disponíveis."
                    : $"{failedRuleCount} verificações falharam. Os demais resultados continuam disponíveis.";
                SecondaryActionButton.Content = "Ver revisão";
                PrimaryActionButton.Content = "Tentar novamente";
                break;
        }
    }

    public ReportGenerationReviewChoice Choice { get; private set; }

    private void SecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        Choice = _mode == ReportGenerationReviewDialogMode.Warnings
            ? ReportGenerationReviewChoice.OpenReview
            : ReportGenerationReviewChoice.OpenReview;
        DialogResult = true;
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        Choice = _mode switch
        {
            ReportGenerationReviewDialogMode.Warnings =>
                ReportGenerationReviewChoice.GenerateAnyway,
            ReportGenerationReviewDialogMode.Incomplete =>
                ReportGenerationReviewChoice.Retry,
            _ => ReportGenerationReviewChoice.OpenReview
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ReportGenerationReviewChoice.Cancel;
        DialogResult = false;
    }
}
