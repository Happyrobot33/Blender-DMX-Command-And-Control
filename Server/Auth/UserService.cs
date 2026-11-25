using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;

namespace Server.Auth;

public class UserValidationException(string message) : Exception(message);

public partial class UserService(AppDbContext db, IPasswordHasher<User> hasher)
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 256;

    [GeneratedRegex("^[A-Za-z0-9._-]{3,64}$")]
    private static partial Regex UsernamePattern();

    public async Task<User> CreateAsync(string username, string password, CancellationToken cancellationToken)
    {
        username = (username ?? string.Empty).Trim();
        if (!UsernamePattern().IsMatch(username))
            throw new UserValidationException("Username must be 3–64 characters: letters, digits, '.', '_' or '-'.");
        ValidatePassword(password);

        if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken))
            throw new UserValidationException($"A user named '{username}' already exists.");

        var user = new User
        {
            Username = username,
            SecurityStamp = NewSecurityStamp(),
            CreatedAtUtc = DateTime.UtcNow,
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlConnector.MySqlException
                                           { ErrorCode: MySqlConnector.MySqlErrorCode.DuplicateKeyEntry })
        {
            // Lost a race with a concurrent create of the same username.
            throw new UserValidationException($"A user named '{username}' already exists.");
        }
        return user;
    }

    public async Task<User?> VerifyCredentialsAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            return null;

        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username.Trim(), cancellationToken);
        if (user is null)
        {
            // Spend comparable time on unknown usernames so timing doesn't reveal which exist.
            hasher.VerifyHashedPassword(new User(), DummyHash.Value, password);
            return null;
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, password);
        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task ChangePasswordAsync(int userId, string newPassword, CancellationToken cancellationToken)
    {
        ValidatePassword(newPassword);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UserValidationException("User not found.");

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        user.SecurityStamp = NewSecurityStamp(); // signs out every existing session for this user
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int userId, int currentUserId, CancellationToken cancellationToken)
    {
        if (userId == currentUserId)
            throw new UserValidationException("You can't delete your own account.");
        if (await db.Users.CountAsync(cancellationToken) <= 1)
            throw new UserValidationException("You can't delete the last user.");

        var deleted = await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            throw new UserValidationException("User not found.");
    }

    public static ClaimsPrincipal CreatePrincipal(User user) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AuthConstants.PrincipalTypeClaim, AuthConstants.UserPrincipalType),
            new Claim(AuthConstants.SecurityStampClaim, user.SecurityStamp),
        ], AuthConstants.UserScheme));

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new UserValidationException($"Password must be at least {MinPasswordLength} characters.");
        if (password.Length > MaxPasswordLength)
            throw new UserValidationException($"Password must be at most {MaxPasswordLength} characters.");
    }

    private static string NewSecurityStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<User>().HashPassword(new User(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
}
