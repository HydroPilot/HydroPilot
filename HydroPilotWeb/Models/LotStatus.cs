using System.ComponentModel.DataAnnotations;

namespace HydroPilotWeb.Models;

public class LotStatus
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public ICollection<Lot> Lots { get; set; } = [];
}
