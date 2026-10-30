using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HydroPilotWeb.Services.Storage;

/// <summary>
/// Almacenamiento local en disco para desarrollo y pruebas (Plan 19).
/// Guarda las imágenes en wwwroot/uploads/plant-images/.
/// </summary>
public class LocalFileSystemPlantImageStorageService : IPlantImageStorageService
{
    private readonly string _uploadDirectory;
    private readonly ILogger<LocalFileSystemPlantImageStorageService> _logger;

    public LocalFileSystemPlantImageStorageService(
        IWebHostEnvironment env,
        ILogger<LocalFileSystemPlantImageStorageService> logger)
    {
        _logger = logger;
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _uploadDirectory = Path.Combine(webRoot, "uploads", "plant-images");

        if (!Directory.Exists(_uploadDirectory))
        {
            Directory.CreateDirectory(_uploadDirectory);
        }
    }

    public async Task<string> SaveImageAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var destinationPath = Path.Combine(_uploadDirectory, safeFileName);

        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await content.CopyToAsync(fileStream, ct);
        _logger.LogInformation("Imagen guardada localmente en {Path}", destinationPath);

        return $"/uploads/plant-images/{safeFileName}";
    }

    public Task<Stream?> GetImageStreamAsync(string storagePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(storagePath);
        var fullPath = Path.Combine(_uploadDirectory, fileName);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteImageAsync(string storagePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(storagePath);
        var fullPath = Path.Combine(_uploadDirectory, fileName);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Imagen eliminada de {Path}", fullPath);
        }

        return Task.CompletedTask;
    }

    public string GetPublicUrl(string storagePath)
    {
        if (storagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            storagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return storagePath;
        }

        if (storagePath.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return storagePath;
        }

        var fileName = Path.GetFileName(storagePath);
        return $"/uploads/plant-images/{fileName}";
    }
}
