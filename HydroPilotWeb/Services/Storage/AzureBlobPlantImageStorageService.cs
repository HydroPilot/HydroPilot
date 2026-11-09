using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HydroPilotWeb.Services.Storage;

/// <summary>
/// Almacenamiento en Azure Blob Storage para producción (Plan 19).
/// Guarda las imágenes en el contenedor 'plant-images'.
/// </summary>
public class AzureBlobPlantImageStorageService : IPlantImageStorageService
{
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<AzureBlobPlantImageStorageService> _logger;

    public AzureBlobPlantImageStorageService(
        IConfiguration configuration,
        ILogger<AzureBlobPlantImageStorageService> logger)
    {
        _logger = logger;
        var connectionString = configuration["Azure:Storage:ConnectionString"]
            ?? configuration.GetConnectionString("AzureBlobStorage")
            ?? throw new InvalidOperationException("No se encontró Azure:Storage:ConnectionString para Azure Blob Storage.");

        var containerName = configuration["Azure:Storage:ContainerName"] ?? "plant-images";
        var serviceClient = new BlobServiceClient(connectionString);
        _containerClient = serviceClient.GetBlobContainerClient(containerName);
        _containerClient.CreateIfNotExists(PublicAccessType.Blob);
    }

    public async Task<string> SaveImageAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var blobClient = _containerClient.GetBlobClient(safeFileName);

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var headers = new BlobHttpHeaders { ContentType = contentType };
        await blobClient.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, ct);

        _logger.LogInformation("Imagen subida a Azure Blob Storage: {Uri}", blobClient.Uri);
        return blobClient.Uri.ToString();
    }

    public async Task<Stream?> GetImageStreamAsync(string storagePath, CancellationToken ct = default)
    {
        var blobName = Path.GetFileName(new Uri(storagePath).AbsolutePath);
        var blobClient = _containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync(ct))
        {
            return null;
        }

        var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        return download.Value.Content;
    }

    public async Task DeleteImageAsync(string storagePath, CancellationToken ct = default)
    {
        var blobName = Path.GetFileName(new Uri(storagePath).AbsolutePath);
        var blobClient = _containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        _logger.LogInformation("Blob eliminado de Azure: {Name}", blobName);
    }

    public string GetPublicUrl(string storagePath)
    {
        return storagePath;
    }
}
