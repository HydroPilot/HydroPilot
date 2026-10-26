using System.Security.Claims;
using HydroPilotWeb.Components.Lotes;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using Xunit;

namespace HydroPilotWeb.Tests.Unit;

public class LotCommentRulesTests
{
    private static ClaimsPrincipal Principal(string role, int? userId = 7, string? name = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (userId is { } id)
            claims.Add(new Claim("user_id", id.ToString()));
        if (name is not null)
            claims.Add(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("Productor", true)]
    [InlineData("Operador", true)]
    [InlineData("Soporte Técnico", true)]
    [InlineData("no-asignado", false)]
    public void Comentan_Todos_Los_Roles_Asignados(string role, bool expected)
    {
        Assert.Equal(expected, LotCommentRules.CanComment(Principal(role)));
    }

    [Fact]
    public void Sin_Usuario_En_La_Sesion_No_Se_Comenta()
    {
        Assert.False(LotCommentRules.CanComment(Principal("Productor", userId: null)));
    }

    [Fact]
    public void Cada_Uno_Borra_Los_Suyos_Y_El_Administrador_Cualquiera()
    {
        Assert.True(LotCommentRules.CanDelete(7, Principal("Operador", userId: 7)));
        Assert.False(LotCommentRules.CanDelete(8, Principal("Operador", userId: 7)));
        Assert.False(LotCommentRules.CanDelete(null, Principal("Productor", userId: 7))); // autor borrado
        Assert.True(LotCommentRules.CanDelete(8, Principal("Administrador", userId: 1)));
        Assert.True(LotCommentRules.CanDelete(null, Principal("Administrador", userId: 1)));
    }

    [Fact]
    public void El_Texto_Se_Limpia_Y_Se_Valida()
    {
        Assert.Equal("Línea 1\nLínea 2", LotCommentRules.Normalize("  Línea 1\r\nLínea 2 \n "));
        Assert.Equal(string.Empty, LotCommentRules.Normalize(null));
        Assert.NotNull(LotCommentRules.Validate(LotCommentRules.Normalize("   \n ")));
        Assert.Null(LotCommentRules.Validate(new string('a', LotComment.TextMaxLength)));
        Assert.NotNull(LotCommentRules.Validate(new string('a', LotComment.TextMaxLength + 1)));
    }

    [Fact]
    public void La_Firma_Usa_Nombre_Y_Apellido_Y_Si_No_El_De_La_Sesion()
    {
        var google = new User { GivenName = "María", Surname = "García", Email = "maria@example.com" };
        Assert.Equal("María García", LotCommentRules.AuthorName(google, Principal("Productor")));

        var admin = new User { Email = "admin@hydropilot.local" };
        Assert.Equal("Administrador", LotCommentRules.AuthorName(admin, Principal("Administrador", name: "Administrador")));
        Assert.Equal("admin@hydropilot.local", LotCommentRules.AuthorName(admin, Principal("Administrador")));
        Assert.Equal("Usuario", LotCommentRules.AuthorName(null, Principal("Operador")));
    }

    [Theory]
    [InlineData("María García", "MG")]
    [InlineData("María José García", "MJ")]
    [InlineData("Administrador", "A")]
    [InlineData("admin@hydropilot.local", "A")]
    public void Iniciales_Del_Avatar(string name, string expected)
    {
        Assert.Equal(expected, LotCommentRules.Initials(name));
    }

    [Fact]
    public void Cuando_Relativo_A_Ahora()
    {
        var now = new DateTime(2026, 10, 25, 15, 30, 0);
        Assert.Equal("recién", LotFormat.When(now.AddSeconds(-20), now));
        Assert.Equal("recién", LotFormat.When(now.AddMinutes(2), now)); // reloj adelantado
        Assert.Equal("hace 5 min", LotFormat.When(now.AddMinutes(-5), now));
        Assert.Equal("hoy 09:05", LotFormat.When(new DateTime(2026, 10, 25, 9, 5, 0), now));
        Assert.Equal("ayer 23:59", LotFormat.When(new DateTime(2026, 10, 24, 23, 59, 0), now));
        Assert.Equal("12 oct 14:05", LotFormat.When(new DateTime(2026, 10, 12, 14, 5, 0), now));
        Assert.Equal("3 dic 2025", LotFormat.When(new DateTime(2025, 12, 3, 8, 0, 0), now));
    }
}
