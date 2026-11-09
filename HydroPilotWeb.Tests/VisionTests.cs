using System.Text;
using HydroPilotWeb.Controllers;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services;
using HydroPilotWeb.Services.Notifications;
using HydroPilotWeb.Services.Storage;
using HydroPilotWeb.Services.Vision;
using HydroPilotWeb.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HydroPilotWeb.Tests;

[Collection("sqlserver")]
public class VisionTests
{
    private readonly SqlServerTestFixture _fixture;

    public VisionTests(SqlServerTestFixture fixture)
    {
        _fixture = fixture;
    }

    private static string FindWebProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "HydroPilotWeb");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "HydroPilotWeb.csproj")))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return "/home/nix/utn/proyecto/HydroPilot/HydroPilotWeb";
    }

    private class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "HydroPilotWeb";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static byte[] GenerateSyntheticLettuceImage(int width = 320, int height = 320, bool sharpEdges = true)
    {
        using var img = new Image<Rgb24>(width, height);
        // Fondo oscuro de sustrato
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                img[x, y] = new Rgb24(25, 25, 25);
            }
        }

        // Roseta verde en el centro
        int cx = width / 2;
        int cy = height / 2;
        int radius = width / 3;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dist <= radius)
                {
                    byte noise = sharpEdges ? (byte)(((x * 7 + y * 13) % 40) - 20) : (byte)0;
                    byte g = (byte)Math.Clamp(165 + noise, 120, 220);
                    byte r = (byte)Math.Clamp(40 + noise / 2, 20, 80);
                    byte b = (byte)Math.Clamp(35 + noise / 2, 15, 75);
                    img[x, y] = new Rgb24(r, g, b);
                }
            }
        }

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task LocalFileSystemStorage_SavesRetrievesAndDeletesFile()
    {
        var tempWebRoot = Path.Combine(Path.GetTempPath(), "hydropilot-test-webroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWebRoot);

        try
        {
            var env = new TestWebHostEnvironment
            {
                WebRootPath = tempWebRoot,
                ContentRootPath = tempWebRoot
            };

            var storage = new LocalFileSystemPlantImageStorageService(env, NullLogger<LocalFileSystemPlantImageStorageService>.Instance);
            var testData = Encoding.UTF8.GetBytes("fake image data for storage test");
            using var ms = new MemoryStream(testData);

            var path = await storage.SaveImageAsync(ms, "sample_planta.png", "image/png");
            Assert.False(string.IsNullOrWhiteSpace(path));

            var publicUrl = storage.GetPublicUrl(path);
            Assert.StartsWith("/uploads/plant-images/", publicUrl);

            using var readStream = await storage.GetImageStreamAsync(path);
            Assert.NotNull(readStream);
            using var memoryCheck = new MemoryStream();
            await readStream.CopyToAsync(memoryCheck);
            Assert.Equal(testData, memoryCheck.ToArray());

            await storage.DeleteImageAsync(path);

            var streamAfterDelete = await storage.GetImageStreamAsync(path);
            Assert.Null(streamAfterDelete);
        }
        finally
        {
            if (Directory.Exists(tempWebRoot))
            {
                Directory.Delete(tempWebRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BabyLeafImageAnalyzer_ValidatesSharpImage_WithVegetationCoverage()
    {
        var bytes = GenerateSyntheticLettuceImage(320, 320, sharpEdges: true);
        using var stream = new MemoryStream(bytes);

        var result = await BabyLeafImageAnalyzer.AnalyzeAsync(stream, cmPerPixel: 0.045);

        Assert.NotNull(result);
        Assert.NotNull(result.Quality);
        Assert.True(result.Quality.IsReliable, $"La imagen debería ser confiable: {result.Quality.RejectionReason}");
        Assert.True(result.Quality.VegetationCoverageRatio > 0.05, "Debería detectar cobertura de dosel vegetal");
        Assert.True(result.Quality.LaplacianVariance > 65.0, "Debería superar el umbral de nitidez");

        Assert.NotNull(result.Morphology);
        Assert.True(result.Morphology.LeafAreaCm2 > 5.0, "El área foliar calculada debe ser mayor a 5 cm²");
        Assert.True(result.Morphology.MaxLeafLengthCm > 2.0, "La longitud máxima de hoja debe ser mayor a 2 cm");
        Assert.NotNull(result.Morphology.SizeRatio);
    }

    [Fact]
    public async Task BabyLeafPipelineService_TemporalWindowGate_RejectsGddOutsideWindow()
    {
        var webRoot = FindWebProjectRoot();
        var env = new TestWebHostEnvironment
        {
            WebRootPath = Path.Combine(webRoot, "wwwroot"),
            ContentRootPath = webRoot
        };

        using var pipeline = new BabyLeafPipelineService(env, NullLogger<BabyLeafPipelineService>.Instance);
        var bytes = GenerateSyntheticLettuceImage();
        using var stream = new MemoryStream(bytes);

        // GDD 100 < GddMin 250 -> Fuera de ventana (plántula inmadura)
        var result = await pipeline.EvaluatePlantAsync(
            imageStream: stream,
            accumulatedGdd: 100.0,
            gddMin: 250.0,
            gddMax: 450.0);

        Assert.Equal(BabyLeafStageStatus.NoBabyLeaf, result.Status);
        Assert.False(result.IsInTemporalWindow);
        Assert.Equal(0.0, result.BabyLeafScore);
    }

    [Fact]
    public void BabyLeafPipelineService_ComputeGrowthRate_CalculatesExpectedIncrements()
    {
        // Caso sin mediciones previas -> factor neutro 0.85
        var (rate0, factor0) = BabyLeafPipelineService.ComputeGrowthRate(50.0, null, 280.0, null);
        Assert.Equal(0.0, rate0);
        Assert.Equal(0.85, factor0);

        // Caso con crecimiento normal: Delta Area = 60 - 45 = 15 cm2 en Delta GDD = 290 - 260 = 30 GDD
        // rate = 15 / 30 = 0.50 cm2/GDD. Factor = clamp(0.50 / 0.45, 0.20, 1.0) = 1.0
        var (rate1, factor1) = BabyLeafPipelineService.ComputeGrowthRate(60.0, 45.0, 290.0, 260.0);
        Assert.Equal(0.5, rate1);
        Assert.Equal(1.0, factor1);
    }

    [Fact]
    public async Task BabyLeafPipelineService_MultiHeadInference_OutputsNPKAndHealthProbabilities()
    {
        var webRoot = FindWebProjectRoot();
        var env = new TestWebHostEnvironment
        {
            WebRootPath = Path.Combine(webRoot, "wwwroot"),
            ContentRootPath = webRoot
        };

        using var pipeline = new BabyLeafPipelineService(env, NullLogger<BabyLeafPipelineService>.Instance);
        Assert.True(pipeline.IsModelLoaded, "El modelo Multi-Head ONNX debe estar cargado");

        var bytes = GenerateSyntheticLettuceImage();
        using var stream = new MemoryStream(bytes);

        var result = await pipeline.EvaluatePlantAsync(
            imageStream: stream,
            accumulatedGdd: 280.0,
            gddMin: 250.0,
            gddMax: 450.0);

        Assert.True(result.IsInTemporalWindow);
        Assert.NotNull(result.VisualState);

        // Validar cabeza NPK
        Assert.Contains(result.VisualState.TopNpkClass, BabyLeafPipelineService.NpkClasses);
        Assert.Equal(4, result.VisualState.NpkProbabilities.Count);
        double npkSum = result.VisualState.NpkProbabilities.Values.Sum();
        Assert.InRange(npkSum, 0.98, 1.02);

        // Validar cabeza Sanidad / Plagas
        Assert.Contains(result.VisualState.TopHealthClass, BabyLeafPipelineService.HealthClasses);
        Assert.Equal(5, result.VisualState.HealthProbabilities.Count);
        double healthSum = result.VisualState.HealthProbabilities.Values.Sum();
        Assert.InRange(healthSum, 0.98, 1.02);

        // Validar Baby Leaf Score y veredicto
        Assert.InRange(result.BabyLeafScore, 0.0, 100.0);
        Assert.Contains(result.Status, new[] { BabyLeafStageStatus.Apta, BabyLeafStageStatus.Candidata, BabyLeafStageStatus.NoApta });
        Assert.False(string.IsNullOrWhiteSpace(result.Recommendation));
    }

    [Fact]
    public async Task PlantImageController_UploadPlantImage_RunsPipelineAndPersistsToDatabase()
    {
        await using var context = _fixture.Factory.CreateDbContext();

        var lot = await context.Lots.Include(l => l.Plants).FirstOrDefaultAsync(l => l.Plants.Any());
        Assert.NotNull(lot);
        var plant = lot.Plants.First();

        var webRoot = FindWebProjectRoot();
        var tempWebRoot = Path.Combine(Path.GetTempPath(), "hydropilot-test-vision-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWebRoot);

        try
        {
            var env = new TestWebHostEnvironment
            {
                WebRootPath = tempWebRoot,
                ContentRootPath = webRoot
            };

            var storage = new LocalFileSystemPlantImageStorageService(env, NullLogger<LocalFileSystemPlantImageStorageService>.Instance);
            using var pipeline = new BabyLeafPipelineService(env, NullLogger<BabyLeafPipelineService>.Instance);
            var settings = new SettingsService(_fixture.Factory);
            var weather = new WeatherService(
                new HttpClient(),
                _fixture.Factory,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<WeatherService>.Instance);
            var gddService = new GddService(_fixture.Factory, weather, settings);
            var notificationService = new NotificationService(_fixture.Factory, NullLogger<NotificationService>.Instance);

            var controller = new PlantImageController(
                _fixture.Factory,
                storage,
                pipeline,
                gddService,
                notificationService,
                NullLogger<PlantImageController>.Instance
            );

            var imageBytes = GenerateSyntheticLettuceImage();
            using var fileStream = new MemoryStream(imageBytes);
            var formFile = new FormFile(fileStream, 0, imageBytes.Length, "file", "test_babyleaf_pipeline.png")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/png"
            };

            var response = await controller.UploadPlantImage(
                plant.Id,
                formFile,
                DateTime.UtcNow,
                gddOverride: 280.0,
                CancellationToken.None);

            var objectResult = Assert.IsType<ObjectResult>(response);
            Assert.Equal(StatusCodes.Status201Created, objectResult.StatusCode);

            // Verificar persistencia en base de datos
            await using var verifyContext = _fixture.Factory.CreateDbContext();
            var savedImage = await verifyContext.PlantImages
                .Include(i => i.Analysis)
                .OrderByDescending(i => i.Id)
                .FirstOrDefaultAsync(i => i.PlantId == plant.Id && i.FileName == "test_babyleaf_pipeline.png");

            Assert.NotNull(savedImage);
            Assert.True(savedImage.Processed);
            Assert.NotNull(savedImage.Analysis);
            Assert.NotNull(savedImage.Analysis.PredominantClass);
            Assert.NotNull(savedImage.Analysis.HealthClass);
            Assert.NotNull(savedImage.Analysis.BabyLeafScore);
            Assert.NotNull(savedImage.Analysis.BabyLeafStatus);
        }
        finally
        {
            if (Directory.Exists(tempWebRoot))
            {
                Directory.Delete(tempWebRoot, recursive: true);
            }
        }
    }
}
