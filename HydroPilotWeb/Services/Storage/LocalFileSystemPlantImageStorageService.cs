using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HydroPilotWeb.Services.Storage;

/// <summary>
/// Almacenamiento local en disco para desarrollo y pruebas (Plan 19).
/// Guarda las imágenes en wwwroot/uploads/plant-images/.
/// </summary>
public class LocalFileSystemPlantImageStorageService : IPlantImageStorageService
{
    private readonly string _webRoot;
    private readonly string _uploadDirectory;
    private readonly ILogger<LocalFileSystemPlantImageStorageService> _logger;

    public LocalFileSystemPlantImageStorageService(
        IWebHostEnvironment env,
        ILogger<LocalFileSystemPlantImageStorageService> logger)
    {
        _logger = logger;
        _webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _uploadDirectory = Path.Combine(_webRoot, "uploads", "plant-images");

        try
        {
            if (!Directory.Exists(_uploadDirectory))
            {
                Directory.CreateDirectory(_uploadDirectory);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo crear el directorio de subidas {Path}", _uploadDirectory);
        }
    }

    public async Task<string> SaveImageAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var simpleFileName = Path.GetFileName(fileName);
        var pruebaDir = Path.Combine(_webRoot, "img", "prueba");
        if (Directory.Exists(pruebaDir))
        {
            var match = Directory.EnumerateFiles(pruebaDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), simpleFileName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                var relPath = Path.GetRelativePath(_webRoot, match).Replace('\\', '/');
                if (!relPath.StartsWith('/'))
                {
                    relPath = "/" + relPath;
                }
                _logger.LogInformation("Imagen estática encontrada en {RelPath}, reutilizando sin duplicar en disco.", relPath);
                return relPath;
            }
        }

        var safeFileName = $"{Guid.NewGuid():N}_{simpleFileName}";
        var destinationPath = Path.Combine(_uploadDirectory, safeFileName);

        try
        {
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            await content.CopyToAsync(fileStream, ct);
            _logger.LogInformation("Imagen guardada localmente en {Path}", destinationPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo escribir en disco para {Path}, continuando en memoria/solo lectura.", destinationPath);
        }

        return $"/uploads/plant-images/{safeFileName}";
    }

    public Task<Stream?> GetImageStreamAsync(string storagePath, CancellationToken ct = default)
    {
        if (storagePath.StartsWith("/img/", StringComparison.OrdinalIgnoreCase))
        {
            var staticPath = Path.Combine(_webRoot, storagePath.TrimStart('/'));
            if (File.Exists(staticPath))
            {
                Stream staticStream = new FileStream(staticPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                return Task.FromResult<Stream?>(staticStream);
            }
        }

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
        if (storagePath.StartsWith("/img/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var fileName = Path.GetFileName(storagePath);
        var fullPath = Path.Combine(_uploadDirectory, fileName);

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
                _logger.LogInformation("Imagen eliminada de {Path}", fullPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "No se pudo eliminar la imagen de {Path}", fullPath);
            }
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

        if (storagePath.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase) ||
            storagePath.StartsWith("/img/", StringComparison.OrdinalIgnoreCase))
        {
            return storagePath;
        }

        var fileName = Path.GetFileName(storagePath);
        return $"/uploads/plant-images/{fileName}";
    }
}
