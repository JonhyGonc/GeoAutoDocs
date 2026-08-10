using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GeoAutoDocs.Core;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace GeoAutoDocs.Infrastructure;

public sealed class WordReportService
{
    private const long EmusPerInch = 914400;
    private readonly AppDataPaths _paths;

    public WordReportService(AppDataPaths paths)
    {
        _paths = paths;
        DefaultTemplatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "MODELO DE RELATORIO.docx");
    }

    public string DefaultTemplatePath { get; }

    public bool ApplyStandardTemplate(Project project)
    {
        if (!File.Exists(DefaultTemplatePath)) return false;
        var changed = false;
        foreach (var report in project.Reports.Where(x =>
                     ShouldUseStandardTemplate(x.TemplatePath) &&
                     !PathsEqual(x.TemplatePath, DefaultTemplatePath)))
        {
            report.TemplatePath = DefaultTemplatePath;
            changed = true;
        }
        return changed;
    }

    public Task<string> GenerateAsync(Project project, Report report, bool includeSection4TechnicalProof = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (report.ProjectId != project.Id) throw new InvalidOperationException("O relatório não pertence ao projeto selecionado.");
        var templatePath = ResolveTemplatePath(report.TemplatePath);
        report.TemplatePath = templatePath;

        var diameters = FormatDiameters(report);
        var outputDirectory = Path.Combine(project.ProjectPath ?? Path.Combine(_paths.ProjectsDirectory, project.Id.ToString("N")), "reports");
        Directory.CreateDirectory(outputDirectory);
        var baseName = $"Relatorio_{SanitizeFileName(project.Name)}_{SanitizeFileName(report.Name)}";
        var outputPath = Path.Combine(outputDirectory, baseName + ".docx");
        if (File.Exists(outputPath))
            outputPath = Path.Combine(outputDirectory, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");

        if (Path.GetFullPath(templatePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O relatório de saída não pode sobrescrever o template.");

        File.Copy(templatePath, outputPath, false);
        using (var document = WordprocessingDocument.Open(outputPath, true))
        {
            var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("Template DOCX sem documento principal.");
            var body = mainPart.Document.Body ?? throw new InvalidDataException("Template DOCX sem corpo.");

            FillControlledText(body, report, diameters);
            ReplaceLocationImage(mainPart, report.LocationImagePath);
            InsertClientLogo(mainPart, body, report.LogoPath);
            ApplyEquipmentSelection(mainPart, body, report.Equipments);
            ApplyOptionalModules(body, report);
            InsertDynamicResults(mainPart, body, project, report);
            MarkFieldsForUpdate(mainPart);
            mainPart.Document.Save();
        }

        using (var validation = WordprocessingDocument.Open(outputPath, false))
        {
            if (validation.MainDocumentPart?.Document.Body is null)
                throw new InvalidDataException("O DOCX gerado não pôde ser reaberto corretamente.");
        }

        report.GeneratedReportPath = outputPath;
        return Task.FromResult(outputPath);
    }

    private string ResolveTemplatePath(string? configuredPath)
    {
        if (ShouldUseStandardTemplate(configuredPath) && File.Exists(DefaultTemplatePath)) return DefaultTemplatePath;
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
        if (File.Exists(DefaultTemplatePath)) return DefaultTemplatePath;
        RequireFile(configuredPath, "Template Word");
        return configuredPath!;
    }

    private static bool ShouldUseStandardTemplate(string? path) =>
        string.IsNullOrWhiteSpace(path) ||
        Path.GetFileName(path).Equals("MODELO DE RELATORIO.docx", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("modelo folha.docx", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void FillControlledText(Body body, Report report, string diameters)
    {
        ReplaceExact(body, "CLIENTE: XXX", $"CLIENTE: {report.Client}");
        ReplaceExact(body, "ÁREA: XXX", $"ÁREA: {report.Area}");
        ReplaceExact(body, "GF-RL-02-XXX-XXX.26-001", report.ReportCode);
        ReplaceExact(body, "CONTRATO: XXX", $"CONTRATO: {report.Contract}");
        ReplaceExact(body, "PEDIDO DE COMPRA: XXX", $"PEDIDO DE COMPRA: {report.PurchaseOrder}");
        ReplaceExact(body, "XX/XX/XX", report.ReportDate?.ToString("dd/MM/yy") ?? "");

        ReplaceInAnchoredParagraph(body, "O presente trabalho constitui",
            "XXX (Igual a localização do cabeçalho)", report.LocationDescription);
        ReplaceInAnchoredParagraph(body, "Sendo assim, os serviços realizados",
            "aproximadamente XXX metros", $"aproximadamente {FormatMeters(report.TotalInvestigatedMeters)} metros");

        var diameterParagraph = FindParagraph(body, "Para a visualização interna das redes");
        if (diameterParagraph is not null)
        {
            var text = ParagraphText(diameterParagraph);
            var start = text.IndexOf("medidas:", StringComparison.OrdinalIgnoreCase);
            var end = text.IndexOf("(Figuras", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start)
            {
                var valueStart = start + "medidas:".Length;
                ReplaceTextPreservingRuns(diameterParagraph, text[valueStart..end], $" {diameters} ");
            }
        }
    }

    private static void ApplyEquipmentSelection(MainDocumentPart mainPart, Body body, EquipmentSelection selected)
    {
        if (!selected.HasFlag(EquipmentSelection.Endoscope))
            RemoveBlock(body, "O endoscópio industrial", "Seu uso é recomendando");
        if (!selected.HasFlag(EquipmentSelection.Bug))
            RemoveBlock(body, "Para diâmetros de até 400 mm", "Figura 2.2");
        if (!selected.HasFlag(EquipmentSelection.Hunter))
            RemoveBlock(body, "Para diâmetros maiores, de 400 mm", "Figura 2.3");

        var spider = selected.HasFlag(EquipmentSelection.Spider);
        var tracker = selected.HasFlag(EquipmentSelection.Tracker);
        if (!spider && !tracker)
        {
            RemoveBlock(body, "Para diâmetros maiores de 600 mm", "Figura 2.5 e 2.6");
        }
        else if (spider && !tracker)
        {
            var paragraph = FindParagraph(body, "Para diâmetros maiores de 600 mm");
            if (paragraph is not null)
                SetParagraphText(paragraph, "Para diâmetros maiores de 600 mm em diante foi utilizado o robô SPIDER (Figura 2.4).");
            RemoveImageParagraph(mainPart, body, "media/image6.jpeg");
            RemoveParagraph(body, "Figura 2.5 e 2.6");
        }
        else if (!spider && tracker)
        {
            var paragraph = FindParagraph(body, "Para diâmetros maiores de 600 mm");
            if (paragraph is not null)
                SetParagraphText(paragraph, "Para diâmetros maiores de 600 mm em diante foi utilizado o robô TRACKER (Figuras 2.5 e 2.6).");
            RemoveImageParagraph(mainPart, body, "media/image5.jpeg");
            RemoveParagraph(body, "Figura 2.4");
        }
    }

    private static void ApplyOptionalModules(Body body, Report report)
    {
        if (!report.IncludeInspectionOpening)
            RemoveBlock(body, "Além disso, foram realizadas intervenções", "Figuras 3.5 a 3.8");
        if (!report.IncludeGpr)
            RemoveBlock(body, "Em complemento à investigação", "Figuras 3.9 a 3.14");
    }

    private static void InsertDynamicResults(MainDocumentPart mainPart, Body body, Project project, Report report)
    {
        var first = FindParagraph(body, "RUA RENATO CAETANO DA SILVA FILHO");
        var last = FindParagraph(body, "Figura 4.5");
        var sectionTemplate = FindParagraph(body, "TRECHO 01");
        var occurrenceTemplate = FindParagraph(body, "- Juntas de ligação");
        var captionTemplate = FindParagraph(body, "Figura 4.1");
        var bodyTemplate = FindParagraph(body, "A seguir, serão apresentados");
        var imageTemplate = FindImageParagraph(mainPart, body, "media/image23.png");
        if (first is null || last is null || sectionTemplate is null || occurrenceTemplate is null ||
            captionTemplate is null || bodyTemplate is null || imageTemplate is null)
            throw new InvalidDataException("O bloco dinâmico da seção de Resultados não foi encontrado no template.");

        var newElements = new List<OpenXmlElement>();
        var figureNumber = 1;
        foreach (var inspection in report.Sections)
        {
            var locationName = project.Locations
                .FirstOrDefault(x => x.Sections.Any(y => y.Id == inspection.Id))?.Name;
            if (string.IsNullOrWhiteSpace(locationName)) locationName = report.LocationDescription;

            var location = (Paragraph)first.CloneNode(true);
            SetParagraphText(location, string.IsNullOrWhiteSpace(locationName) ? inspection.Name.ToUpperInvariant() : locationName.Trim().ToUpperInvariant());
            newElements.Add(location);

            var section = (Paragraph)sectionTemplate.CloneNode(true);
            SetParagraphText(section, FormatSectionTitle(inspection));
            newElements.Add(section);

            var imageElements = new List<OpenXmlElement>();
            foreach (var occurrenceData in inspection.Occurrences.OrderBy(x => x.Order))
            {
                var description = FormatOccurrenceForReport(occurrenceData);
                var evidences = occurrenceData.Evidence.OrderBy(x => x.Order)
                    .Where(x => IsSupportedImageFile(x.FilePath)).ToList();
                var firstFigure = figureNumber;
                var lastFigure = figureNumber + evidences.Count - 1;
                var reference = evidences.Count switch
                {
                    0 => "",
                    1 => $" (Figura 4.{firstFigure})",
                    _ => $" (Figuras 4.{firstFigure} a 4.{lastFigure})"
                };

                var occurrence = (Paragraph)occurrenceTemplate.CloneNode(true);
                SetParagraphText(occurrence, $"- {description.TrimEnd('.', ';')}{reference}.");
                newElements.Add(occurrence);

                foreach (var evidence in evidences)
                {
                    imageElements.Add(CloneImageParagraph(mainPart, imageTemplate, evidence.FilePath));
                    var caption = (Paragraph)captionTemplate.CloneNode(true);
                    var captionText = string.IsNullOrWhiteSpace(evidence.Caption) ? description : evidence.Caption.Trim();
                    SetParagraphText(caption, $"Figura 4.{figureNumber} – {captionText.TrimEnd('.', ';')}.");
                    imageElements.Add(caption);
                    figureNumber++;
                }
            }
            newElements.AddRange(imageElements);
        }

        foreach (var custom in report.CustomSections.OrderBy(x => x.Order))
        {
            if (!string.IsNullOrWhiteSpace(custom.Title))
            {
                var heading = (Paragraph)first.CloneNode(true);
                SetParagraphText(heading, custom.Title.Trim().ToUpperInvariant());
                newElements.Add(heading);
            }
            if (!string.IsNullOrWhiteSpace(custom.Text))
            {
                var paragraph = (Paragraph)bodyTemplate.CloneNode(true);
                SetParagraphText(paragraph, custom.Text.Trim());
                newElements.Add(paragraph);
            }
            foreach (var image in custom.Images.OrderBy(x => x.Order).Where(x => IsSupportedImageFile(x.FilePath)))
            {
                newElements.Add(CloneImageParagraph(mainPart, imageTemplate, image.FilePath));
                var caption = (Paragraph)captionTemplate.CloneNode(true);
                SetParagraphText(caption, $"Figura 4.{figureNumber} – {custom.Title ?? "Imagem complementar"}.");
                newElements.Add(caption);
                figureNumber++;
            }
        }

        foreach (var element in newElements) body.InsertBefore(element, first);
        RemoveInclusive(first, last);
    }

    private static string FormatSectionTitle(InspectionSection section)
    {
        var title = section.Name.Trim();
        if (section.Diameter is not null && !title.Contains("mm", StringComparison.OrdinalIgnoreCase))
            title += $" – REDE {FormatMeters(section.Diameter)} mm";
        if (!string.IsNullOrWhiteSpace(section.Reference))
            title += $" ({section.Reference.Trim()})";
        return title;
    }

    private static string FormatOccurrenceForReport(Occurrence occurrence)
    {
        if (!string.IsNullOrWhiteSpace(occurrence.CustomGeneratedText))
            return occurrence.CustomGeneratedText.Trim();

        var description = occurrence.Anomalies.Count == 0
            ? "Ocorrência registrada"
            : string.Join(", ", occurrence.Anomalies.OrderBy(x => x.Order).Select(x => x.Name));
        description += occurrence.LocationMode switch
        {
            OccurrenceLocationMode.SpecificMeterage when occurrence.Meterage is not null =>
                $" a {FormatMeters(occurrence.Meterage)} metros do início da filmagem",
            OccurrenceLocationMode.Interval when occurrence.Meterage is not null && occurrence.EndMeterage is not null =>
                $" entre {FormatMeters(occurrence.Meterage)} e {FormatMeters(occurrence.EndMeterage)} metros do início da filmagem",
            OccurrenceLocationMode.EntireSection => " em todo trecho filmado",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(occurrence.Position)) description += $" ({occurrence.Position.Trim()})";
        if (!string.IsNullOrWhiteSpace(occurrence.Notes)) description += $", {occurrence.Notes.Trim()}";
        return description;
    }

    private static bool IsSupportedImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceLocationImage(MainDocumentPart mainPart, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        RequireFile(filePath, "Imagem de localização");
        var paragraph = FindImageParagraph(mainPart, mainPart.Document.Body!, "media/image1.jpg");
        if (paragraph is null) throw new InvalidDataException("O slot da imagem de localização não foi encontrado no template.");
        var blip = paragraph.Descendants<A.Blip>().First();
        var part = mainPart.AddImagePart(GetImagePartType(filePath));
        using (var stream = File.OpenRead(filePath)) part.FeedData(stream);
        blip.Embed = mainPart.GetIdOfPart(part);
        ResizeDrawingToFit(paragraph, filePath);
    }

    private static void InsertClientLogo(MainDocumentPart mainPart, Body body, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        RequireFile(filePath, "Logo do cliente");
        var table = body.Elements<Table>().FirstOrDefault();
        if (table is null) return;
        var cell = table.Elements<TableRow>().First().Elements<TableCell>().First();
        var paragraph = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
        var imagePart = mainPart.AddImagePart(GetImagePartType(filePath));
        using (var stream = File.OpenRead(filePath)) imagePart.FeedData(stream);
        var relationshipId = mainPart.GetIdOfPart(imagePart);
        var (width, height) = FitImage(filePath, (long)(1.55 * EmusPerInch), (long)(0.65 * EmusPerInch));
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.Justification = new Justification { Val = JustificationValues.Center };
        paragraph.AppendChild(new Run(CreateDrawing(relationshipId, Path.GetFileName(filePath), width, height, NextDrawingId(mainPart))));
    }

    private static Paragraph CloneImageParagraph(MainDocumentPart mainPart, Paragraph template, string filePath)
    {
        var clone = (Paragraph)template.CloneNode(true);
        var imagePart = mainPart.AddImagePart(GetImagePartType(filePath));
        using (var stream = File.OpenRead(filePath)) imagePart.FeedData(stream);
        clone.Descendants<A.Blip>().First().Embed = mainPart.GetIdOfPart(imagePart);
        ResizeDrawingToFit(clone, filePath);
        return clone;
    }

    private static void ResizeDrawingToFit(Paragraph paragraph, string filePath)
    {
        var extent = paragraph.Descendants<DW.Extent>().FirstOrDefault();
        if (extent?.Cx?.Value is not long maxWidth || extent.Cy?.Value is not long maxHeight) return;
        var (width, height) = FitImage(filePath, maxWidth, maxHeight);
        extent.Cx = width; extent.Cy = height;
        foreach (var drawingExtent in paragraph.Descendants<A.Extents>())
        {
            drawingExtent.Cx = width;
            drawingExtent.Cy = height;
        }
    }

    private static Drawing CreateDrawing(string relationshipId, string name, long width, long height, uint id)
    {
        var inline = new DW.Inline(
            new DW.Extent { Cx = width, Cy = height },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = name },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = width, Cy = height }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };
        return new Drawing(inline);
    }

    private static void ReplaceExact(Body body, string original, string replacement)
    {
        var paragraph = body.Descendants<Paragraph>().FirstOrDefault(x => ParagraphText(x).Trim() == original);
        if (paragraph is not null) ReplaceTextPreservingRuns(paragraph, original, replacement);
    }

    private static void ReplaceInAnchoredParagraph(Body body, string prefix, string original, string replacement)
    {
        var paragraph = FindParagraph(body, prefix);
        if (paragraph is not null) ReplaceTextPreservingRuns(paragraph, original, replacement);
    }

    private static bool ReplaceTextPreservingRuns(Paragraph paragraph, string original, string replacement)
    {
        if (original.Length == 0) return false;
        var nodes = paragraph.Descendants<Text>().ToList();
        var fullText = string.Concat(nodes.Select(x => x.Text));
        var start = fullText.IndexOf(original, StringComparison.Ordinal);
        if (start < 0) return false;
        var end = start + original.Length;

        var cursor = 0;
        var startIndex = -1;
        var endIndex = -1;
        var startOffset = 0;
        var endOffset = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var length = nodes[i].Text?.Length ?? 0;
            if (startIndex < 0 && start < cursor + length)
            {
                startIndex = i;
                startOffset = start - cursor;
            }
            if (endIndex < 0 && end <= cursor + length)
            {
                endIndex = i;
                endOffset = end - cursor;
                break;
            }
            cursor += length;
        }
        if (startIndex < 0 || endIndex < 0) return false;

        var startText = nodes[startIndex].Text ?? "";
        var endText = nodes[endIndex].Text ?? "";
        if (startIndex == endIndex)
        {
            nodes[startIndex].Text = startText[..startOffset] + replacement + startText[endOffset..];
            nodes[startIndex].Space = SpaceProcessingModeValues.Preserve;
            return true;
        }

        nodes[startIndex].Text = startText[..startOffset] + replacement;
        nodes[startIndex].Space = SpaceProcessingModeValues.Preserve;
        for (var i = startIndex + 1; i < endIndex; i++) nodes[i].Text = "";
        nodes[endIndex].Text = endText[endOffset..];
        nodes[endIndex].Space = SpaceProcessingModeValues.Preserve;
        return true;
    }

    private static Paragraph? FindParagraph(Body body, string startsWith) =>
        body.Descendants<Paragraph>().FirstOrDefault(x => ParagraphText(x).TrimStart().StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));

    private static string ParagraphText(Paragraph paragraph) => string.Concat(paragraph.Descendants<Text>().Select(x => x.Text));

    private static void SetParagraphText(Paragraph paragraph, string text)
    {
        var firstRun = paragraph.Elements<Run>().FirstOrDefault();
        var runProperties = firstRun?.RunProperties?.CloneNode(true) as RunProperties;
        foreach (var run in paragraph.Elements<Run>().ToList()) run.Remove();
        var newRun = runProperties is null ? new Run() : new Run(runProperties);
        newRun.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(newRun);
    }

    private static void RemoveBlock(Body body, string startPrefix, string endPrefix)
    {
        var start = FindParagraph(body, startPrefix);
        var end = FindParagraph(body, endPrefix);
        if (start is not null && end is not null) RemoveInclusive(start, end);
    }

    private static void RemoveInclusive(OpenXmlElement start, OpenXmlElement end)
    {
        var current = start;
        while (current is not null)
        {
            var next = current.NextSibling();
            var isEnd = ReferenceEquals(current, end);
            current.Remove();
            if (isEnd) break;
            current = next;
        }
    }

    private static void RemoveParagraph(Body body, string prefix) => FindParagraph(body, prefix)?.Remove();

    private static Paragraph? FindImageParagraph(MainDocumentPart mainPart, Body body, string targetSuffix) =>
        body.Descendants<Paragraph>().FirstOrDefault(p => p.Descendants<A.Blip>().Any(blip =>
            blip.Embed?.Value is string id && mainPart.GetPartById(id).Uri.ToString().EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase)));

    private static void RemoveImageParagraph(MainDocumentPart mainPart, Body body, string targetSuffix) =>
        FindImageParagraph(mainPart, body, targetSuffix)?.Remove();

    private static void MarkFieldsForUpdate(MainDocumentPart mainPart)
    {
        var settingsPart = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var update = settingsPart.Settings.GetFirstChild<UpdateFieldsOnOpen>();
        if (update is null) settingsPart.Settings.AppendChild(new UpdateFieldsOnOpen { Val = true });
        else update.Val = true;
        settingsPart.Settings.Save();
    }

    private static string FormatDiameters(Report report)
    {
        var selected = new List<int>();
        var options = new[]
        {
            (StandardDiameterSelection.Mm300, 300), (StandardDiameterSelection.Mm400, 400),
            (StandardDiameterSelection.Mm500, 500), (StandardDiameterSelection.Mm600, 600),
            (StandardDiameterSelection.Mm800, 800), (StandardDiameterSelection.Mm1000, 1000),
            (StandardDiameterSelection.Mm1500, 1500)
        };
        selected.AddRange(options.Where(x => report.Diameters.HasFlag(x.Item1)).Select(x => x.Item2));
        selected.AddRange(Regex.Matches(report.CustomDiameters ?? "", @"\d+")
            .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)));
        var values = selected.Where(x => x > 0).Distinct().Order().Select(x => $"{x} mm").ToList();
        if (values.Count == 0) throw new InvalidOperationException("Selecione ao menos um diâmetro.");
        return values.Count switch
        {
            1 => values[0],
            2 => $"{values[0]} e {values[1]}",
            _ => string.Join(", ", values.Take(values.Count - 1)) + " e " + values[^1]
        };
    }

    private static string FormatMeters(decimal? value) =>
        value?.ToString("0.###", CultureInfo.GetCultureInfo("pt-BR")) ?? "0";

    private static string SanitizeFileName(string name)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var result = Regex.Replace(name.Trim(), $"[{Regex.Escape(invalid)}]", "_");
        return string.IsNullOrWhiteSpace(result) ? "Projeto" : result;
    }

    private static void RequireFile(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{label} não encontrado: {path ?? "não informado"}.", path);
    }

    private static PartTypeInfo GetImagePartType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImagePartType.Png,
        ".jpg" or ".jpeg" => ImagePartType.Jpeg,
        _ => throw new NotSupportedException("Use imagens PNG, JPG ou JPEG.")
    };

    private static uint NextDrawingId(MainDocumentPart mainPart) =>
        mainPart.Document.Descendants<DW.DocProperties>().Select(x => x.Id?.Value ?? 0U).DefaultIfEmpty().Max() + 1;

    private static (long Width, long Height) FitImage(string path, long maxWidth, long maxHeight)
    {
        var (pixelWidth, pixelHeight) = ReadImageDimensions(path);
        var scale = Math.Min((double)maxWidth / pixelWidth, (double)maxHeight / pixelHeight);
        return ((long)(pixelWidth * scale), (long)(pixelHeight * scale));
    }

    private static (int Width, int Height) ReadImageDimensions(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length >= 24 && data.AsSpan(1, 3).SequenceEqual("PNG"u8))
            return (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));

        if (data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var index = 2;
            while (index + 9 < data.Length)
            {
                if (data[index] != 0xFF) { index++; continue; }
                var marker = data[index + 1];
                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                    return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index + 7, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index + 5, 2)));
                if (index + 4 >= data.Length) break;
                index += 2 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index + 2, 2));
            }
        }

        throw new InvalidDataException($"Não foi possível ler as dimensões da imagem: {path}");
    }
}
