using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Domain;
using TrueCentralVms.Server.Auth;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;
using TrueCentralVms.Server.Hubs;

namespace TrueCentralVms.Server.Api;

/// <summary>Mantenedor de usuarios (solo administradores).</summary>
public static class UsersApi
{
    private static UserDto ToDto(User u) => new(u.Id, u.Username, u.Role, u.Enabled, u.CreatedAt, u.PasswordChangedAt);

    private static IResult Error(string message, int statusCode = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public static void MapUsersApi(this WebApplication app)
    {
        app.MapGet("/api/users", async (HttpContext ctx, VmsDbContext db) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;
            var users = await db.Users.OrderBy(u => u.Username).ToListAsync();
            return Results.Ok(users.Select(ToDto));
        });

        app.MapPost("/api/users", async (HttpContext ctx, UserWriteDto request, VmsDbContext db,
            PasswordGovernance passwords, IHubContext<VmsHub> hub) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out _) is { } failure) return failure;

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            if (!Roles.IsValid(request.Role))
                return Error("Rol inválido.");
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);
            if (string.IsNullOrEmpty(request.Password))
                return Error("La contraseña es obligatoria.");
            if (await passwords.ValidateNewPasswordAsync(db, null, request.Password) is { } error)
                return Error(error);

            var user = new User { Username = username, Role = request.Role, Enabled = request.Enabled };
            passwords.SetPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user));
        });

        app.MapPut("/api/users/{id:int}", async (HttpContext ctx, int id, UserWriteDto request, VmsDbContext db,
            PasswordGovernance passwords, TokenService tokens, IHubContext<VmsHub> hub) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;

            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();

            if (!Roles.IsValid(request.Role))
                return Error("Rol inválido.");

            string username = request.Username?.Trim() ?? "";
            if (username.Length is < 3 or > 64)
                return Error("El nombre de usuario debe tener entre 3 y 64 caracteres.");
            if (await db.Users.AnyAsync(u => u.Id != id && u.Username == username))
                return Error("Ya existe un usuario con ese nombre.", StatusCodes.Status409Conflict);

            // Siempre debe quedar al menos un administrador habilitado.
            bool losesAdmin = user.Role == Roles.Admin && user.Enabled && (request.Role != Roles.Admin || !request.Enabled);
            if (losesAdmin && !await db.Users.AnyAsync(u => u.Id != id && u.Role == Roles.Admin && u.Enabled))
                return Error("Debe existir al menos un administrador habilitado.");

            if (!string.IsNullOrEmpty(request.Password))
            {
                if (await passwords.ValidateNewPasswordAsync(db, id, request.Password) is { } error)
                    return Error(error);
                passwords.SetPassword(user, request.Password);
                tokens.RevokeUser(id);
            }

            user.Username = username;
            user.Role = request.Role;
            user.Enabled = request.Enabled;
            if (!user.Enabled)
                tokens.RevokeUser(id);
            await db.SaveChangesAsync();

            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok(ToDto(user));
        });

        app.MapDelete("/api/users/{id:int}", async (HttpContext ctx, int id, VmsDbContext db,
            TokenService tokens, IHubContext<VmsHub> hub) =>
        {
            if (ApiSecurity.RequireAdmin(ctx, out var session) is { } failure) return failure;

            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            if (session.UserId == id)
                return Error("No puede eliminar su propio usuario.");
            if (user.Role == Roles.Admin && user.Enabled &&
                !await db.Users.AnyAsync(u => u.Id != id && u.Role == Roles.Admin && u.Enabled))
                return Error("Debe existir al menos un administrador habilitado.");

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            tokens.RevokeUser(id);

            await hub.Clients.All.SendAsync(VmsHubContract.ConfigChanged, "users");
            return Results.Ok();
        });
    }
}
