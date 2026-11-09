namespace HydroPilotWeb.Services.Storage;

/// <summary>
/// Contrato de almacenamiento de imágenes de plantas (Plan 19).
/// Desacopla el destino físico (Azure Blob Storage en producción vs filesystem local en desarrollo).
/// </summary>
public interface IPlantImageStorageService
{
    /// <summary>
    /// Guarda el contenido binario de una imagen y devuelve su ruta o URI accesible.
    /// </summary>
    Task<string> SaveImageAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Obtiene el stream de lectura de una imagen previamente almacenada.
    /// </summary>
    Task<Stream?> GetImageStreamAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Elimina una imagen almacenada.
    /// </summary>
    Task DeleteImageAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// Resuelve la URL pública o relativa para servir la imagen en el frontend Blazor.
    /// </summary>
    string GetPublicUrl(string storagePath);
}
