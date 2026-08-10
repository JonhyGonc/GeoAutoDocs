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

public sealed class WordReportService(AppDataPaths paths)
{
    private const long EmusPerInch = 914400;

    public Task<string> GenerateAsync(Project project, bool includeSection4TechnicalProof = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = project.ReportConfiguration;
        RequireFile(report.TemplatePath, "Template Word");

        var diameters = FormatDiameters(report);
        var outputDirectory = Path.Combine(project.ProjectPath ?? Path.Combine(paths.ProjectsDirectory, project.Id.ToString("N")), "reports");
        Directory.CreateDirectory(outputDirectory);
        var baseName = $"Relatorio_{SanitizeFileName(project.Name)}";
        var outputPath = Path.Combine(outputDirectory, baseName + ".docx");
        if (File.Exists(outputPath))
            outputPath = Path.Combine(outputDirectory, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");

        if (Path.GetFullPath(report.TemplatePath!).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O relatório de saída não pode sobrescrever o template.");

        File.Copy(report.TemplatePath!, outputPath, false);
        using (var document = WordprocessingDocument.Open(outputPath, true))
        {
            var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("Template DOCX sem documento principal.");
            var body = mainPart.Document.Body ?? throw new InvalidDataException("Template DOCX sem corpo.");

            FillControlledText(body, report, diameters);
            ReplaceLocationImage(mainPart, report.LocationImagePath);
            InsertClientLogo(mainPart, body, report.LogoPath);
            ApplyEquipmentSelection(mainPart, body, report.Equipments);
            ApplyOptionalModules(body, report);
            InsertCustomSections(mainPart, body, project.CustomSections);
            if (includeSection4TechnicalProof) ApplySection4TechnicalProof(mainPart, body);
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

    private static void FillControlledText(Body body, ReportConfiguration report, string diameters)
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
                var replacement = text[..(start + "medidas:".Length)] + " " + diameters + " " + text[end..];
                SetParagraphText(diameterParagraph, replacement);
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

    private static void ApplyOptionalModules(Body body, ReportConfiguration report)
    {
        if (!report.IncludeInspectionOpening)
            RemoveBlock(body, "Além disso, foram realizadas intervenções", "Figuras 3.5 a 3.8");
        if (!report.IncludeGpr)
            RemoveBlock(body, "Em complemento à investigação", "Figuras 3.9 a 3.14");
    }

    private static void InsertCustomSections(MainDocumentPart mainPart, Body body, IEnumerable<CustomSection> sections)
    {
        var resultHeading = FindParagraph(body, "Resultados e Conclusões");
        var customHeadingTemplate = FindParagraph(body, "RUA RENATO CAETANO DA SILVA FILHO");
        var imageTemplate = FindImageParagraph(mainPart, body, "media/image23.png");
        var captionTemplate = FindParagraph(body, "Figura 4.1");
        var bodyTemplate = FindParagraph(body, "Os serviços prestados foram realizados");
        if (resultHeading is null || bodyTemplate is null) return;

        foreach (var section in sections.OrderBy(x => x.Order))
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                var heading = (Paragraph)(customHeadingTemplate ?? bodyTemplate).CloneNode(true);
                SetParagraphText(heading, section.Title.Trim());
                body.InsertBefore(heading, resultHeading);
            }

            if (!string.IsNullOrWhiteSpace(section.Text))
            {
                var paragraph = (Paragraph)bodyTemplate.CloneNode(true);
                SetParagraphText(paragraph, section.Text.Trim());
                body.InsertBefore(paragraph, resultHeading);
            }

            foreach (var image in section.Images.OrderBy(x => x.Order))
            {
                RequireFile(image.FilePath, "Imagem da etapa personalizada");
                if (imageTemplate is null) continue;
                var imageParagraph = CloneImageParagraph(mainPart, imageTemplate, image.FilePath);
                body.InsertBefore(imageParagraph, resultHeading);
                if (captionTemplate is not null)
                {
                    var caption = (Paragraph)captionTemplate.CloneNode(true);
                    SetParagraphText(caption, $"Imagem complementar – {section.Title ?? "Etapa personalizada"}.");
                    body.InsertBefore(caption, resultHeading);
                }
            }
        }
    }

    private static void ApplySection4TechnicalProof(MainDocumentPart mainPart, Body body)
    {
        var first = FindParagraph(body, "RUA RENATO CAETANO DA SILVA FILHO");
        var last = FindParagraph(body, "Figura 4.5");
        var sectionTemplate = FindParagraph(body, "TRECHO 01");
        var occurrenceTemplate = FindParagraph(body, "- Juntas de ligação");
        var captionTemplate = FindParagraph(body, "Figura 4.1");
        var imageParagraphs = new[]
        {
            FindImageParagraph(mainPart, body, "media/image23.png"),
            FindImageParagraph(mainPart, body, "media/image24.png"),
            FindImageParagraph(mainPart, body, "media/image25.png")
        };
        if (first is null || last is null || sectionTemplate is null || occurrenceTemplate is null || captionTemplate is null) return;

        var newElements = new List<OpenXmlElement>();
        var location = (Paragraph)first.CloneNode(true);
        SetParagraphText(location, "RUA TESTE");
        newElements.Add(location);
        var section = (Paragraph)sectionTemplate.CloneNode(true);
        SetParagraphText(section, "TRECHO 01 – REDE 600 mm");
        newElements.Add(section);

        for (var i = 0; i < 3; i++)
        {
            var number = i + 1;
            var occurrence = (Paragraph)occurrenceTemplate.CloneNode(true);
            SetParagraphText(occurrence,
                $"- Ocorrência técnica de teste {number} a {number * 8} metros do início da filmagem (Figura 4.{number}).");
            newElements.Add(occurrence);
        }

        for (var i = 0; i < 3; i++)
        {
            if (imageParagraphs[i] is not null) newElements.Add((Paragraph)imageParagraphs[i]!.CloneNode(true));
            var caption = (Paragraph)captionTemplate.CloneNode(true);
            SetParagraphText(caption, $"Figura 4.{i + 1} – Evidência da ocorrência técnica de teste {i + 1}.");
            newElements.Add(caption);
        }

        foreach (var element in newElements) body.InsertBefore(element, first);
        RemoveInclusive(first, last);
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
        var table = body.Elements<Table>().FirstOrDefault()
            ?? throw new InvalidDataException("Tabela de capa não encontrada no template.");
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
        if (paragraph is not null) SetParagraphText(paragraph, replacement);
    }

    private static void ReplaceInAnchoredParagraph(Body body, string prefix, string original, string replacement)
    {
        var paragraph = FindParagraph(body, prefix);
        if (paragraph is not null) SetParagraphText(paragraph, ParagraphText(paragraph).Replace(original, replacement));
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

    private static string FormatDiameters(ReportConfiguration report)
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
