using System.Security.Claims;
using HydroPilotWeb.Models;
using HydroPilotWeb.Services.Lotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HydroPilotWeb.Tests.Integration;

/// <summary>Comentarios de un lote sobre SQL Server: alta, orden, borrado y qué pasa al borrar el lote o el usuario.</summary>
[Collection("forecast-sql")]
public class LotCommentSqlServerTests
{
    private readonly ForecastSqlFixture _fixture;

    public LotCommentSqlServerTests(ForecastSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private LotCommentService Service() => new(_fixture.Factory, NullLogger<LotCommentService>.Instance);

    private static ClaimsPrincipal Principal(User user, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, role), new Claim("user_id", user.Id.ToString())], "test"));

    private async Task<(Lot Lot, User Author, User Other)> SeedAsync()
    {
        await using var context = _fixture.Factory.CreateDbContext();
        var lot = await ForecastTestData.CreateActiveLotAsync(context, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5));
        User NewUser(string given) => new()
        {
            GoogleSub = $"test-{Guid.NewGuid():N}",
            Email = $"{given.ToLowerInvariant()}@example.com",
            GivenName = given,
            Surname = "Prueba",
            Role = "Operador"
        };
        var author = NewUser("Ana");
        var other = NewUser("Beto");
        context.Users.AddRange(author, other);
        await context.SaveChangesAsync();
        return (lot, author, other);
    }

    [Fact]
    public async Task Agrega_Y_Lista_Los_Mas_Nuevos_Arriba()
    {
        var (lot, author, other) = await SeedAsync();
        var service = Service();

        Assert.True((await service.AddAsync(lot.Id, "  Primer comentario  ", Principal(author, "Operador"))).Succeeded);
        Assert.True((await service.AddAsync(lot.Id, "Segundo\r\ncon salto", Principal(other, "Productor"))).Succeeded);

        var list = await service.GetAsync(lot.Id, Principal(author, "Operador"));
        Assert.Equal(2, await service.CountAsync(lot.Id));
        Assert.Equal(["Segundo\ncon salto", "Primer comentario"], list.Items.Select(c => c.Text));
        Assert.Equal(["Beto Prueba", "Ana Prueba"], list.Items.Select(c => c.AuthorName));
        Assert.Equal([false, true], list.Items.Select(c => c.CanDelete)); // Ana solo borra el suyo
    }

    [Fact]
    public async Task No_Guarda_Vacios_Ni_En_Lotes_Inexistentes()
    {
        var (lot, author, _) = await SeedAsync();
        var service = Service();

        Assert.False((await service.AddAsync(lot.Id, "   ", Principal(author, "Operador"))).Succeeded);
        Assert.False((await service.AddAsync(-1, "Hola", Principal(author, "Operador"))).Succeeded);
        Assert.False((await service.AddAsync(lot.Id, "Hola", Principal(author, "no-asignado"))).Succeeded);
        Assert.Equal(0, await service.CountAsync(lot.Id));
    }

    [Fact]
    public async Task Solo_El_Autor_O_Un_Administrador_Borran()
    {
        var (lot, author, other) = await SeedAsync();
        var service = Service();
        await service.AddAsync(lot.Id, "Para borrar", Principal(author, "Operador"));
        var id = (await service.GetAsync(lot.Id, Principal(author, "Operador"))).Items.Single().Id;

        Assert.False((await service.DeleteAsync(id, Principal(other, "Productor"))).Succeeded);
        Assert.Equal(1, await service.CountAsync(lot.Id));

        Assert.True((await service.DeleteAsync(id, Principal(other, "Administrador"))).Succeeded);
        Assert.Equal(0, await service.CountAsync(lot.Id));
    }

    [Fact]
    public async Task Borrar_El_Usuario_Deja_El_Comentario_Y_Borrar_El_Lote_Lo_Borra()
    {
        var (lot, author, _) = await SeedAsync();
        var service = Service();
        await service.AddAsync(lot.Id, "Queda con la firma", Principal(author, "Operador"));

        await using (var context = _fixture.Factory.CreateDbContext())
        {
            context.Users.Remove(await context.Users.SingleAsync(u => u.Id == author.Id));
            await context.SaveChangesAsync();
        }
        var comment = (await service.GetAsync(lot.Id, Principal(author, "Operador"))).Items.Single();
        Assert.Equal("Ana Prueba", comment.AuthorName);
        Assert.False(comment.CanDelete);

        await using (var context = _fixture.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM Lots WHERE Id = {0}", lot.Id);
            Assert.False(await context.LotComments.AnyAsync(c => c.LotId == lot.Id));
        }
    }
}
