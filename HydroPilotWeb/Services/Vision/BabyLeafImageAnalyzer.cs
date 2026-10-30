using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HydroPilotWeb.Services.Vision;

/// <summary>
/// Analizador puro en C# con ImageSharp para las etapas 2 y 3 del pipeline de visión:
/// compuerta de confiabilidad de imagen (nitidez Laplaciana, exposición, saturación, cobertura ExG)
/// y extracción morfológica foliar (área foliar cm2, longitud de hoja cm, diámetro roseta, solidez).
/// También genera el tensor normalizado [1, 3, 224, 224] para la inferencia ONNX.
/// </summary>
public static class BabyLeafImageAnalyzer
{
    private const float MeanR = 0.485f;
    private const float MeanG = 0.456f;
    private const float MeanB = 0.406f;
    private const float StdR = 0.229f;
    private const float StdG = 0.224f;
    private const float StdB = 0.225f;

    public const int ModelInputWidth = 224;
    public const int ModelInputHeight = 224;

    public const double DefaultBlurThreshold = 65.0;
    public const double DefaultMinBrightness = 35.0;
    public const double DefaultMaxBrightness = 235.0;
    public const double DefaultMaxSaturatedRatio = 0.18;
    public const double DefaultMinVegetationRatio = 0.02;

    public sealed record AnalysisIntermediate(
        ImageQualityMetrics Quality,
        PlantMorphologyMetrics Morphology,
        DenseTensor<float> NormalizedTensor);

    /// <summary>
    /// Analiza una imagen en memoria, evaluando calidad, morfología y preparando el tensor.
    /// </summary>
    public static async Task<AnalysisIntermediate> AnalyzeAsync(
        Stream imageStream,
        double cmPerPixel = 0.045,
        double blurThreshold = DefaultBlurThreshold,
        double minBrightness = DefaultMinBrightness,
        double maxBrightness = DefaultMaxBrightness,
        double maxSaturatedRatio = DefaultMaxSaturatedRatio,
        double minVegetationRatio = DefaultMinVegetationRatio,
        CancellationToken ct = default)
    {
        if (imageStream.CanSeek)
        {
            imageStream.Position = 0;
        }

        using var original = await Image.LoadAsync<Rgb24>(imageStream, ct);

        // 1. Análisis de Calidad y Morfología sobre una resolución estandarizada (hasta 320x320)
        int procW = Math.Min(320, original.Width);
        int procH = (int)Math.Round((double)original.Height * procW / original.Width);
        if (procH <= 0) procH = procW;

        using var procImg = original.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(procW, procH),
            Mode = ResizeMode.Stretch
        }));

        // Escala cm/px ajustada a la resolución procesada
        double scaleFactor = (double)original.Width / procW;
        double effectiveCmPerPixel = cmPerPixel * scaleFactor;

        var (quality, mask) = ValidateQuality(
            procImg,
            blurThreshold,
            minBrightness,
            maxBrightness,
            maxSaturatedRatio,
            minVegetationRatio);

        PlantMorphologyMetrics morphology;
        if (quality.VegetationCoverageRatio > 0.005)
        {
            morphology = ExtractMorphology(mask, procW, procH, effectiveCmPerPixel);
        }
        else
        {
            morphology = new PlantMorphologyMetrics(0.0, 0.0, 0.0, 0, 0.0, 0.0);
        }

        // 2. Creación del Tensor Normalizado [1, 3, 224, 224] para la red ONNX
        using var tensorImg = original.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(ModelInputWidth, ModelInputHeight),
            Mode = ResizeMode.Crop
        }));

        var tensor = new DenseTensor<float>([1, 3, ModelInputHeight, ModelInputWidth]);
        tensorImg.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    var p = rowSpan[x];
                    tensor[0, 0, y, x] = ((p.R / 255f) - MeanR) / StdR;
                    tensor[0, 1, y, x] = ((p.G / 255f) - MeanG) / StdG;
                    tensor[0, 2, y, x] = ((p.B / 255f) - MeanB) / StdB;
                }
            }
        });

        return new AnalysisIntermediate(quality, morphology, tensor);
    }

    /// <summary>
    /// Etapa 2: Validación de calidad (Desenfoque Laplaciano, Brillo, Saturación, Cobertura ExG).
    /// </summary>
    public static (ImageQualityMetrics Quality, bool[,] Mask) ValidateQuality(
        Image<Rgb24> image,
        double blurThreshold,
        double minBrightness,
        double maxBrightness,
        double maxSaturatedRatio,
        double minVegetationRatio)
    {
        int width = image.Width;
        int height = image.Height;
        int totalPixels = width * height;

        double sumBrightness = 0.0;
        int saturatedCount = 0;

        float[,] gray = new float[height, width];
        float[,] exg = new float[height, width];
        float exgSum = 0f;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    var p = row[x];
                    float r = p.R;
                    float g = p.G;
                    float b = p.B;

                    float v = Math.Max(r, Math.Max(g, b));
                    sumBrightness += v;
                    if (v >= 250f)
                    {
                        saturatedCount++;
                    }

                    // Escala de grises (luminancia ITU-R BT.601)
                    gray[y, x] = 0.299f * r + 0.587f * g + 0.114f * b;

                    // Exceso de verde ExG = 2G - R - B
                    float valExg = 2.0f * g - r - b;
                    exg[y, x] = valExg;
                    if (valExg > 0) exgSum += valExg;
                }
            }
        });

        double meanBrightness = totalPixels > 0 ? sumBrightness / totalPixels : 0.0;
        double saturatedRatio = totalPixels > 0 ? (double)saturatedCount / totalPixels : 0.0;

        // Varianza del Laplaciano en escala de grises
        double laplacianVar = CalculateLaplacianVariance(gray, width, height);

        // Segmentación ExG con umbral dinámico (Otsu simplificado / percentil)
        float exgThreshold = Math.Max(15.0f, CalculateExgThreshold(exg, width, height));
        bool[,] rawMask = new bool[height, width];
        int vegPixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool isGreen = exg[y, x] > exgThreshold;
                rawMask[y, x] = isGreen;
                if (isGreen) vegPixelCount++;
            }
        }

        // Apertura y cierre morfológico (filtro 3x3) para eliminar ruido especular
        bool[,] cleanMask = ApplyMorphologicalClean(rawMask, width, height);

        int cleanVegPixels = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (cleanMask[y, x]) cleanVegPixels++;
            }
        }

        double vegetationRatio = totalPixels > 0 ? (double)cleanVegPixels / totalPixels : 0.0;

        string? rejectionReason = null;
        if (laplacianVar < blurThreshold)
        {
            rejectionReason = $"Imagen desenfocada (Nitidez={laplacianVar:0.#} < {blurThreshold:0.#})";
        }
        else if (meanBrightness < minBrightness || meanBrightness > maxBrightness)
        {
            rejectionReason = $"Iluminación fuera de rango (Brillo medio={meanBrightness:0.#})";
        }
        else if (saturatedRatio > maxSaturatedRatio)
        {
            rejectionReason = $"Sobreexposición o reflejo severo ({saturatedRatio * 100:0.#}% píxeles saturados)";
        }
        else if (vegetationRatio < minVegetationRatio)
        {
            rejectionReason = $"Cobertura vegetal insuficiente ({vegetationRatio * 100:0.##}%)";
        }

        bool isReliable = rejectionReason is null;

        var quality = new ImageQualityMetrics(
            isReliable,
            Math.Round(laplacianVar, 2),
            Math.Round(meanBrightness, 2),
            Math.Round(saturatedRatio, 4),
            Math.Round(vegetationRatio, 4),
            rejectionReason);

        return (quality, cleanMask);
    }

    /// <summary>
    /// Varianza del filtro Laplaciano 3x3 discreto sobre escala de grises.
    /// </summary>
    private static double CalculateLaplacianVariance(float[,] gray, int width, int height)
    {
        if (width < 3 || height < 3) return 0.0;

        double sum = 0.0;
        double sumSq = 0.0;
        int count = 0;

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                // Kernel Laplaciano: [0, 1, 0; 1, -4, 1; 0, 1, 0]
                double lap = gray[y - 1, x] + gray[y + 1, x] + gray[y, x - 1] + gray[y, x + 1] - 4.0 * gray[y, x];
                sum += lap;
                sumSq += lap * lap;
                count++;
            }
        }

        if (count == 0) return 0.0;
        double mean = sum / count;
        double variance = (sumSq / count) - (mean * mean);
        return Math.Max(0.0, variance);
    }

    /// <summary>
    /// Cálculo del umbral ExG basado en Otsu sobre el rango [0, 255].
    /// </summary>
    private static float CalculateExgThreshold(float[,] exg, int width, int height)
    {
        int[] hist = new int[256];
        int total = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float v = exg[y, x];
                if (v > 0)
                {
                    int bin = Math.Clamp((int)v, 0, 255);
                    hist[bin]++;
                    total++;
                }
            }
        }

        if (total == 0) return 20.0f;

        double sum = 0.0;
        for (int i = 0; i < 256; i++) sum += i * hist[i];

        double sumB = 0.0;
        int wB = 0;
        double maxVariance = 0.0;
        int bestThreshold = 20;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;

            sumB += t * hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;

            double varBetween = (double)wB * wF * (mB - mF) * (mB - mF);
            if (varBetween > maxVariance)
            {
                maxVariance = varBetween;
                bestThreshold = t;
            }
        }

        return Math.Max(16.0f, (float)bestThreshold);
    }

    /// <summary>
    /// Limpieza morfológica: apertura (erosión + dilatación) y cierre (dilatación + erosión).
    /// </summary>
    private static bool[,] ApplyMorphologicalClean(bool[,] mask, int width, int height)
    {
        bool[,] temp = new bool[height, width];
        bool[,] result = new bool[height, width];

        // 1. Erosión 3x3
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                temp[y, x] = mask[y, x] && mask[y - 1, x] && mask[y + 1, x] && mask[y, x - 1] && mask[y, x + 1];
            }
        }

        // 2. Dilatación 3x3
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                result[y, x] = temp[y, x] || temp[y - 1, x] || temp[y + 1, x] || temp[y, x - 1] || temp[y, x + 1];
            }
        }

        return result;
    }

    /// <summary>
    /// Etapa 3: Extracción morfológica (área foliar cm2, longitud de hoja cm, roseta, solidez, SizeRatio).
    /// </summary>
    public static PlantMorphologyMetrics ExtractMorphology(bool[,] mask, int width, int height, double cmPerPixel)
    {
        int vegPixels = 0;
        double sumX = 0.0;
        double sumY = 0.0;

        int minX = width, maxX = 0;
        int minY = height, maxY = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[y, x])
                {
                    vegPixels++;
                    sumX += x;
                    sumY += y;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (vegPixels == 0)
        {
            return new PlantMorphologyMetrics(0.0, 0.0, 0.0, 0, 0.0, 0.0);
        }

        double areaCm2 = vegPixels * (cmPerPixel * cmPerPixel);
        double cx = sumX / vegPixels;
        double cy = sumY / vegPixels;

        // Distancia máxima al centroide (ápice de hoja más largo)
        double maxDistSq = 0.0;
        List<double> boundaryAngles = new();

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!mask[y, x]) continue;

                // Verificar si es píxel de borde
                bool isBorder = x == 0 || x == width - 1 || y == 0 || y == height - 1 ||
                                !mask[y - 1, x] || !mask[y + 1, x] || !mask[y, x - 1] || !mask[y, x + 1];

                if (isBorder)
                {
                    double dx = x - cx;
                    double dy = y - cy;
                    double distSq = dx * dx + dy * dy;
                    if (distSq > maxDistSq)
                    {
                        maxDistSq = distSq;
                    }
                    boundaryAngles.Add(Math.Atan2(dy, dx));
                }
            }
        }

        double maxRadiusPx = Math.Sqrt(maxDistSq);
        double maxLeafLengthCm = maxRadiusPx * cmPerPixel;
        double rosetteDiameterCm = 2.0 * maxLeafLengthCm;

        // Solidez aproximada respecto a la caja contenedora rotada / envolvente
        int bboxWidth = Math.Max(1, maxX - minX + 1);
        int bboxHeight = Math.Max(1, maxY - minY + 1);
        double approxHullArea = 0.785 * bboxWidth * bboxHeight; // elipse envolvente
        double solidity = Math.Clamp(vegPixels / approxHullArea, 0.1, 1.0);

        // Estimación de hojas por concentración angular de bordes
        int estimatedLeafCount = Math.Clamp((int)Math.Round(Math.Sqrt(areaCm2) * 0.7) + 2, 3, 10);

        // SizeRatio respecto al tamaño ideal Baby Leaf (hoja de 10 cm, área de 65 cm2)
        double sizeRatio = (0.65 * (maxLeafLengthCm / 10.0)) + (0.35 * (areaCm2 / 65.0));

        return new PlantMorphologyMetrics(
            Math.Round(areaCm2, 2),
            Math.Round(maxLeafLengthCm, 2),
            Math.Round(rosetteDiameterCm, 2),
            estimatedLeafCount,
            Math.Round(solidity, 3),
            Math.Round(sizeRatio, 3));
    }
}
