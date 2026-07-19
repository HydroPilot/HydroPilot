using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class User
{
    public int Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string GoogleSub { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(128)]
    public string GivenName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Surname { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Role { get; set; } = "Operador";

    [MaxLength(256)]
    public string? PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
}
