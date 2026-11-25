using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs.Tasks;

namespace Server.Auth;

public class RenderMachineValidationException(string message) : Exception(message);

public class RenderMachineService(AppDbContext db, TaskClaimService claims)
{
    public const int MaxNameLength = 100;

    public async Task<(RenderMachine Machine, string Token)> CreateAsync(string name, CancellationToken cancellationToken)
    {
        name = await ValidateNameAsync(name, exceptId: null, cancellationToken);

        var (token, hash, prefix) = ApiTokens.Generate();
        var now = DateTime.UtcNow;
        var machine = new RenderMachine
        {
            Name = name,
            TokenHash = hash,
            TokenPrefix = prefix,
            IsEnabled = true,
            CreatedAtUtc = now,
            TokenIssuedAtUtc = now,
        };
        db.RenderMachines.Add(machine);
        await db.SaveChangesAsync(cancellationToken);
        return (machine, token);
    }

    public async Task<RenderMachine> RenameAsync(int id, string name, CancellationToken cancellationToken)
    {
        var machine = await FindAsync(id, cancellationToken);
        machine.Name = await ValidateNameAsync(name, exceptId: id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return machine;
    }

    public async Task<(RenderMachine Machine, string Token)> RegenerateTokenAsync(int id, CancellationToken cancellationToken)
    {
        var machine = await FindAsync(id, cancellationToken);
        var (token, hash, prefix) = ApiTokens.Generate();
        machine.TokenHash = hash;
        machine.TokenPrefix = prefix;
        machine.TokenIssuedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (machine, token);
    }

    public async Task<RenderMachine> SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        var machine = await FindAsync(id, cancellationToken);
        machine.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        return machine;
    }

    public async Task<RenderMachine> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var machine = await FindAsync(id, cancellationToken);

        // Disable first so the machine can't claim new tasks between the release and the delete
        // (a task claimed in that window would be left Assigned with no machine).
        machine.IsEnabled = false;
        await db.SaveChangesAsync(cancellationToken);
        await claims.ReleaseMachineTasksAsync(id, cancellationToken);

        db.RenderMachines.Remove(machine);
        await db.SaveChangesAsync(cancellationToken);
        return machine;
    }

    private async Task<string> ValidateNameAsync(string name, int? exceptId, CancellationToken cancellationToken)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > MaxNameLength)
            throw new RenderMachineValidationException($"Name must be 1–{MaxNameLength} characters.");
        if (await db.RenderMachines.AnyAsync(m => m.Name == name && m.Id != exceptId, cancellationToken))
            throw new RenderMachineValidationException($"A render machine named '{name}' already exists.");
        return name;
    }

    private async Task<RenderMachine> FindAsync(int id, CancellationToken cancellationToken) =>
        await db.RenderMachines.SingleOrDefaultAsync(m => m.Id == id, cancellationToken)
        ?? throw new RenderMachineValidationException("Render machine not found.");
}
