using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HydroPilotWeb.Services.Vision;

/// <summary>
/// Servicio de evaluación visual y fenológica para lechugas hidropónicas (Plan 19 + Gemini).
/// Ejecuta el pipeline de 6 etapas:
/// 1. Compuerta GDD (ventana fenológica Baby Leaf)
/// 2. Compuerta de Confiabilidad de Imagen (nitidez Laplaciana, exposición, saturación, cobertura ExG)
/// 3. Extracción Morfológica foliar (área cm2, longitud de hoja cm, roseta, solidez, SizeRatio)
/// 4. Vigor y Tasa de Crecimiento (GrowthRate)
/// 5. Inferencia Multi-Head ONNX (Nutrición NPK + Sanidad/Plagas + Morfología auxiliar)
/// 6. Baby Leaf Score (BLS 0-100) y clasificación agronómica (Apta, Candidata, No Apta)
/// </summary>
public class BabyLeafPipelineService : IDisposable
{
    private readonly InferenceSession? _onnxSession;
    private readonly ILogger<BabyLeafPipelineService> _logger;

    public static readonly string[] NpkClasses = ["FN", "-N", "-P", "-K"];
    public static readonly string[] HealthClasses = ["Healthy", "DownyMildew", "PowderyMildew", "Septoria", "Aphids"];

    public static readonly Dictionary<string, string> NpkDescriptions = new()
    {
        ["FN"] = "Sana / Nutrición Completa",
        ["-N"] = "Deficiencia de Nitrógeno (Clorosis foliar generalizada)",
        ["-P"] = "Deficiencia de Fósforo (Retraso en crecimiento y tonos oscuros)",
        ["-K"] = "Deficiencia de Potasio (Clorosis marginal y necrosis en bordes)"
    };

    public static readonly Dictionary<string, string> HealthDescriptions = new()
    {
        ["Healthy"] = "Sana / Sin patógenos ni plagas visibles",
        ["DownyMildew"] = "Mildiu Velloso (Bremia lactucae - manchas cloróticas/algodonosas)",
        ["PowderyMildew"] = "Oídio / Mildiu Polvoriento (Polvillo blanco superficial)",
        ["Septoria"] = "Septoriosis (Septoria lactucae - manchas necróticas circulares)",
        ["Aphids"] = "Pulgones / Áfidos (Colonias de insectos chupadores)"
    };

    public const double DefaultOptimalLeafLengthCm = 10.0;
    public const double DefaultOptimalLeafAreaCm2 = 65.0;
    public const double DefaultReferenceGrowthRate = 0.45;

    public BabyLeafPipelineService(
        IWebHostEnvironment env,
        ILogger<BabyLeafPipelineService> logger)
    {
        _logger = logger;

        var modelPath = Path.Combine(env.ContentRootPath, "MLModels", "lettuce_babyleaf_multihead.onnx");
        if (!File.Exists(modelPath))
        {
            modelPath = Path.Combine(AppContext.BaseDirectory, "MLModels", "lettuce_babyleaf_multihead.onnx");
        }

        if (File.Exists(modelPath))
        {
            try
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                _onnxSession = new InferenceSession(modelPath, options);
                _logger.LogInformation("Modelo Multi-Head ONNX cargado exitosamente desde {Path}", modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al inicializar sesión ONNX en {Path}", modelPath);
            }
        }
        else
        {
            _logger.LogWarning("Archivo de modelo Multi-Head ONNX no encontrado en {Path}", modelPath);
        }
    }

    /// <summary>
    /// Constructor alternativo para pruebas unitarias donde se especifica la ruta explícita del modelo.
    /// </summary>
    public BabyLeafPipelineService(string onnxModelPath, ILogger<BabyLeafPipelineService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BabyLeafPipelineService>.Instance;
        if (File.Exists(onnxModelPath))
        {
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _onnxSession = new InferenceSession(onnxModelPath, options);
        }
    }

    public bool IsModelLoaded => _onnxSession is not null;

    /// <summary>
    /// Ejecuta el árbol de decisión completo para una imagen y contexto fenológico dado.
    /// </summary>
    public async Task<BabyLeafEvaluationResult> EvaluatePlantAsync(
        Stream imageStream,
        double accumulatedGdd,
        double gddMin,
        double gddMax,
        double cmPerPixel = 0.045,
        double? previousAreaCm2 = null,
        double? previousGdd = null,
        CancellationToken ct = default)
    {
        // 1. Compuerta Fenológica (¿En ventana temporal Baby Leaf?)
        if (accumulatedGdd < gddMin || accumulatedGdd > gddMax)
        {
            string windowMsg = accumulatedGdd < gddMin
                ? $"GDD insuficiente ({accumulatedGdd:0.#} < {gddMin:0.#}): Plántula aún en desarrollo vegetativo temprano."
                : $"GDD excedido ({accumulatedGdd:0.#} > {gddMax:0.#}): La planta superó la ventana óptima de Baby Leaf.";

            return new BabyLeafEvaluationResult(
                Status: BabyLeafStageStatus.NoBabyLeaf,
                BabyLeafScore: 0.0,
                AccumulatedGdd: accumulatedGdd,
                IsInTemporalWindow: false,
                ImageQuality: null,
                Morphology: null,
                GrowthRateCm2PerGdd: null,
                GrowthFactor: null,
                VisualState: null,
                Recommendation: windowMsg,
                EvaluatedAtUtc: DateTime.UtcNow);
        }

        // 2 & 3. Compuerta de Confiabilidad de Imagen y Extracción Morfológica
        var analysis = await BabyLeafImageAnalyzer.AnalyzeAsync(
            imageStream,
            cmPerPixel: cmPerPixel,
            ct: ct);

        if (!analysis.Quality.IsReliable)
        {
            return new BabyLeafEvaluationResult(
                Status: BabyLeafStageStatus.SinDecision,
                BabyLeafScore: 0.0,
                AccumulatedGdd: accumulatedGdd,
                IsInTemporalWindow: true,
                ImageQuality: analysis.Quality,
                Morphology: analysis.Morphology,
                GrowthRateCm2PerGdd: null,
                GrowthFactor: null,
                VisualState: null,
                Recommendation: $"Captura descartada: {analysis.Quality.RejectionReason ?? "Calidad de imagen insuficiente"}.",
                EvaluatedAtUtc: DateTime.UtcNow);
        }

        // 4. GrowthRate y Factor de Vigor
        var (growthRate, growthFactor) = ComputeGrowthRate(
            analysis.Morphology.LeafAreaCm2,
            previousAreaCm2,
            accumulatedGdd,
            previousGdd);

        // 5. Inferencia Multi-Head ONNX (Nutrición NPK + Sanidad + Morfología auxiliar)
        var visualState = RunMultiHeadInference(analysis.NormalizedTensor, out float[] nnMorphPreds);

        // Si la morfología basada en ExG no detectó tamaño suficiente, usar la cabeza de regresión de la red
        double effectiveLeafLength = analysis.Morphology.MaxLeafLengthCm > 0.5
            ? analysis.Morphology.MaxLeafLengthCm
            : Math.Max(1.0, (double)nnMorphPreds[0]);

        double effectiveLeafArea = analysis.Morphology.LeafAreaCm2 > 1.0
            ? analysis.Morphology.LeafAreaCm2
            : Math.Max(2.0, (double)nnMorphPreds[1]);

        var effectiveMorphology = analysis.Morphology with
        {
            MaxLeafLengthCm = Math.Round(effectiveLeafLength, 2),
            LeafAreaCm2 = Math.Round(effectiveLeafArea, 2),
            SizeRatio = Math.Round((0.65 * (effectiveLeafLength / DefaultOptimalLeafLengthCm)) + (0.35 * (effectiveLeafArea / DefaultOptimalLeafAreaCm2)), 3)
        };

        // 6. Cálculo del Baby Leaf Score (BLS 0-100)
        double lengthScore = Math.Exp(-Math.Pow(effectiveLeafLength - DefaultOptimalLeafLengthCm, 2) / (2.0 * Math.Pow(2.2, 2)));
        double areaScore = Math.Exp(-Math.Pow(effectiveLeafArea - DefaultOptimalLeafAreaCm2, 2) / (2.0 * Math.Pow(20.0, 2)));
        double morphScore = (0.65 * lengthScore) + (0.35 * areaScore);

        double visualMultiplier = visualState.CombinedVisualAptitude;
        double bls = 100.0 * ((0.75 * morphScore) + (0.25 * growthFactor)) * visualMultiplier;
        bls = Math.Round(Math.Clamp(bls, 0.0, 100.0), 2);

        // Clasificación final y recomendación agronómica
        BabyLeafStageStatus status;
        string recommendation;

        if (bls >= 78.0 && visualMultiplier >= 0.75)
        {
            status = BabyLeafStageStatus.Apta;
            recommendation = "Apta para cosecha Baby Leaf: calibre foliar, vigor y sanidad óptimos.";
        }
        else if (bls >= 50.0 && visualMultiplier >= 0.60)
        {
            status = BabyLeafStageStatus.Candidata;
            recommendation = "Planta candidata: en ventana térmica y buen estado sanitario. Mantener 24-48h para alcanzar calibre comercial.";
        }
        else
        {
            status = BabyLeafStageStatus.NoApta;
            string reasonDetail;
            if (visualState.TopHealthClass != "Healthy")
            {
                reasonDetail = $"Alerta fitosanitaria: detectado {HealthDescriptions.GetValueOrDefault(visualState.TopHealthClass, visualState.TopHealthClass)}";
            }
            else if (visualState.TopNpkClass != "FN")
            {
                reasonDetail = $"Alerta nutricional: detectada {NpkDescriptions.GetValueOrDefault(visualState.TopNpkClass, visualState.TopNpkClass)}";
            }
            else
            {
                reasonDetail = "Calibre foliar o vigor de crecimiento insuficiente";
            }
            recommendation = $"No apta (Score={bls:0.#}): {reasonDetail}.";
        }

        return new BabyLeafEvaluationResult(
            Status: status,
            BabyLeafScore: bls,
            AccumulatedGdd: accumulatedGdd,
            IsInTemporalWindow: true,
            ImageQuality: analysis.Quality,
            Morphology: effectiveMorphology,
            GrowthRateCm2PerGdd: growthRate,
            GrowthFactor: growthFactor,
            VisualState: visualState,
            Recommendation: recommendation,
            EvaluatedAtUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Tasa de crecimiento incremental en función de los GDD acumulados.
    /// </summary>
    public static (double GrowthRate, double GrowthFactor) ComputeGrowthRate(
        double currentAreaCm2,
        double? previousAreaCm2,
        double currentGdd,
        double? previousGdd)
    {
        if (!previousAreaCm2.HasValue || !previousGdd.HasValue || currentGdd <= previousGdd.Value)
        {
            return (0.0, 0.85); // Factor neutro-alto al no existir captura previa
        }

        double deltaArea = Math.Max(0.0, currentAreaCm2 - previousAreaCm2.Value);
        double deltaGdd = Math.Max(1.0, currentGdd - previousGdd.Value);
        double rate = deltaArea / deltaGdd;
        double factor = Math.Clamp(rate / DefaultReferenceGrowthRate, 0.20, 1.0);

        return (Math.Round(rate, 4), Math.Round(factor, 4));
    }

    /// <summary>
    /// Ejecuta la inferencia Multi-Head en ONNX Runtime.
    /// </summary>
    public VisualHealthPrediction RunMultiHeadInference(DenseTensor<float> inputTensor, out float[] nnMorphMetrics)
    {
        if (_onnxSession is null)
        {
            nnMorphMetrics = [(float)DefaultOptimalLeafLengthCm, (float)DefaultOptimalLeafAreaCm2];
            var fallbackNpk = NpkClasses.ToDictionary(c => c, c => c == "FN" ? 1.0f : 0.0f);
            var fallbackHealth = HealthClasses.ToDictionary(c => c, c => c == "Healthy" ? 1.0f : 0.0f);
            return new VisualHealthPrediction("FN", 1.0f, fallbackNpk, "Healthy", 1.0f, fallbackHealth);
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor)
        };

        using var results = _onnxSession.Run(inputs);

        float[] npkLogits = results.First(r => r.Name == "npk_logits").AsEnumerable<float>().ToArray();
        float[] healthLogits = results.First(r => r.Name == "health_logits").AsEnumerable<float>().ToArray();
        nnMorphMetrics = results.FirstOrDefault(r => r.Name == "morph_metrics")?.AsEnumerable<float>().ToArray()
                         ?? [8.0f, 45.0f];

        float[] npkProbs = Softmax(npkLogits);
        float[] healthProbs = Softmax(healthLogits);

        var npkDict = new Dictionary<string, float>();
        int bestNpkIdx = 0;
        for (int i = 0; i < NpkClasses.Length && i < npkProbs.Length; i++)
        {
            npkDict[NpkClasses[i]] = (float)Math.Round(npkProbs[i], 4);
            if (npkProbs[i] > npkProbs[bestNpkIdx]) bestNpkIdx = i;
        }

        var healthDict = new Dictionary<string, float>();
        int bestHealthIdx = 0;
        for (int i = 0; i < HealthClasses.Length && i < healthProbs.Length; i++)
        {
            healthDict[HealthClasses[i]] = (float)Math.Round(healthProbs[i], 4);
            if (healthProbs[i] > healthProbs[bestHealthIdx]) bestHealthIdx = i;
        }

        return new VisualHealthPrediction(
            TopNpkClass: NpkClasses[bestNpkIdx],
            ProbFn: npkDict.GetValueOrDefault("FN", 0f),
            NpkProbabilities: npkDict,
            TopHealthClass: HealthClasses[bestHealthIdx],
            ProbHealthy: healthDict.GetValueOrDefault("Healthy", 0f),
            HealthProbabilities: healthDict);
    }

    /// <summary>
    /// Inferencia simplificada para compatibilidad con código existente que consume ClassificationResult.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(Stream imageStream, CancellationToken ct = default)
    {
        var analysis = await BabyLeafImageAnalyzer.AnalyzeAsync(imageStream, ct: ct);
        var visual = RunMultiHeadInference(analysis.NormalizedTensor, out _);

        var topClass = visual.TopNpkClass;
        var confidence = visual.NpkProbabilities.GetValueOrDefault(topClass, 0f);
        var desc = NpkDescriptions.GetValueOrDefault(topClass, "Estado visual");

        return new ClassificationResult(
            topClass,
            desc,
            confidence,
            visual.NpkProbabilities,
            "BabyLeafMultiHead-2.0");
    }

    private static float[] Softmax(float[] logits)
    {
        float maxLogit = logits.Max();
        float[] exps = logits.Select(x => MathF.Exp(x - maxLogit)).ToArray();
        float sumExps = exps.Sum();
        return exps.Select(x => x / (sumExps > 0 ? sumExps : 1.0f)).ToArray();
    }

    public void Dispose()
    {
        _onnxSession?.Dispose();
    }
}
