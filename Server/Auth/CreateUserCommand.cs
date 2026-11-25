using Microsoft.EntityFrameworkCore;

namespace Server.Auth;

public static class CreateUserCommand
{
    public const string Name = "create-user";

    public static bool IsInvoked(string[] args) => args.Length > 0 && args[0] == Name;

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var makeAdmin = args.Contains("--admin");
        args = args.Where(a => a != "--admin").ToArray();
        if (args.Length != 2)
        {
            Console.Error.WriteLine($"Usage: Server {Name} <username> [--admin]   (the first user is always an admin)");
            return 2;
        }

        var password = Environment.GetEnvironmentVariable("BDMX_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            password = ReadPassword("Password: ");
            if (ReadPassword("Confirm password: ") != password)
            {
                Console.Error.WriteLine("Passwords do not match.");
                return 1;
            }
        }

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var role = makeAdmin || !await db.Users.AnyAsync() ? Data.Entities.UserRole.Admin : Data.Entities.UserRole.User;
            var user = await users.CreateAsync(args[1], password, CancellationToken.None, role);
            Console.WriteLine($"Created {role.ToString().ToLowerInvariant()} '{user.Username}'.");
            return 0;
        }
        catch (UserValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }
}
