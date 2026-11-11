namespace HydroPilotWeb.Components.Common;

/// <summary>Una opción de <see cref="SearchSelect"/>: su clave, el texto que se muestra y busca, y el grupo (opcional).</summary>
public sealed record SearchSelectItem(string Key, string Label, string? Group = null);
