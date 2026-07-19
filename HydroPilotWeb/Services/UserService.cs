using System.Security.Claims;
using HydroPilotWeb.Data;
using HydroPilotWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace HydroPilotWeb.Services;

public class UserService
{
    private readonly IDbContextFactory<HydroPilotDbContext> _dbFactory;

    public UserService(IDbContextFactory<HydroPilotDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<User> FindOrCreateAsync(ClaimsPrincipal principal)
    {
        var googleSub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? throw new InvalidOperationException("Google sub claim not found");

        var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var givenName = principal.FindFirst(ClaimTypes.GivenName)?.Value ?? "";
        var surname = principal.FindFirst(ClaimTypes.Surname)?.Value ?? "";

        await using var context = _dbFactory.CreateDbContext();

        var existingUser = await context.Users
            .FirstOrDefaultAsync(u => u.GoogleSub == googleSub);

        if (existingUser != null)
        {
            existingUser.Email = email;
            existingUser.GivenName = givenName;
            existingUser.Surname = surname;
            existingUser.LastLoginAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            return existingUser;
        }

        var role = await context.Users.AnyAsync() ? "Operador" : "Administrador";

        var newUser = new User
        {
            GoogleSub = googleSub,
            Email = email,
            GivenName = givenName,
            Surname = surname,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        context.Users.Add(newUser);
        await context.SaveChangesAsync();
        return newUser;
    }

    public async Task<User?> FindAdminByPasswordAsync(string password)
    {
        await using var context = _dbFactory.CreateDbContext();

        var admin = await context.Users
            .FirstOrDefaultAsync(u => u.PasswordHash != null);

        if (admin == null)
            return null;

        return BCrypt.Net.BCrypt.Verify(password, admin.PasswordHash) ? admin : null;
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        await using var context = _dbFactory.CreateDbContext();
        return await context.Users.OrderBy(u => u.CreatedAt).ToListAsync();
    }

    public async Task UpdateRoleAsync(int userId, string role)
    {
        await using var context = _dbFactory.CreateDbContext();
        var user = await context.Users.FindAsync(userId);
        if (user != null)
        {
            user.Role = role;
            await context.SaveChangesAsync();
        }
    }
}
