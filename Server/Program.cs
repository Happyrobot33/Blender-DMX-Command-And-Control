using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs;
using Server.Jobs.Analysis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
    {
        // Every unsafe (POST/PUT/DELETE) MVC request must carry an antiforgery token. The bearer-token
        // API opts out explicitly ([IgnoreAntiforgeryToken]) since it isn't cookie-authenticated.
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    })
    // Accept/emit enum names ("Completed") in the JSON API.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Database: FluentMigrator owns the schema (versioned migrations), EF Core is the ORM.
var connectionString = builder.Configuration.GetConnectionString(DatabaseBootstrapper.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{DatabaseBootstrapper.ConnectionStringName}' is not configured.");
var serverVersion = ServerVersion.Parse(builder.Configuration["Database:ServerVersion"] ?? "11.4.0-mariadb");

builder.Services.AddDbContext<AppDbContext>(options => options.UseMySql(connectionString, serverVersion));

builder.Services.AddFluentMigratorCore()
    .ConfigureRunner(runner => runner
        .AddMySql5()
        .WithGlobalConnectionString(connectionString)
        .ScanIn(typeof(Program).Assembly).For.Migrations());
// (No AddFluentMigratorConsole(): it registers a second console logger for the whole app, which
// printed every log line twice. Migration output still goes through the normal logging pipeline.)

// Jobs
builder.Services.Configure<JobStorageOptions>(builder.Configuration.GetSection(JobStorageOptions.SectionName));
builder.Services.AddSingleton<JobPaths>();
builder.Services.AddScoped<JobUploadService>();

// Blender scanning (frame range of the main .blend), run inline as part of each upload.
builder.Services.Configure<BlenderOptions>(builder.Configuration.GetSection(BlenderOptions.SectionName));
builder.Services.AddSingleton<BlendFileScanner>();

// Sub task progress reporting (POST /api/tasks/{id}/progress).
builder.Services.AddScoped<Server.Jobs.Tasks.TaskProgressService>();
builder.Services.AddScoped<Server.Jobs.Tasks.TaskClaimService>();
builder.Services.AddScoped<Server.Jobs.Tasks.TaskResultService>();
builder.Services.AddSingleton<ArchiveCache>();
builder.Services.AddSingleton<SourceArchiveService>();
builder.Services.AddSingleton<FramesArchiveService>();

// Lossless video generation once all of a job's tasks are complete.
builder.Services.Configure<Server.Jobs.Video.VideoOptions>(builder.Configuration.GetSection(Server.Jobs.Video.VideoOptions.SectionName));
builder.Services.AddSingleton<Server.Jobs.Video.FfmpegEncoder>();
builder.Services.AddSingleton<Server.Jobs.Video.VideoEncodingQueue>();
builder.Services.AddHostedService<Server.Jobs.Video.VideoEncodingWorker>();

// Authentication: users sign in to the web UI with a cookie; render machines use bearer API tokens.
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<RenderMachineService>();

builder.Services.AddAuthentication(AuthConstants.UserScheme)
    .AddCookie(AuthConstants.UserScheme, options =>
    {
        options.Cookie.Name = "bdmx.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        options.Events.OnValidatePrincipal = UserSessionValidator.ValidateAsync;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(AuthConstants.ApiTokenScheme, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthConstants.UserPolicy, policy => policy
        .AddAuthenticationSchemes(AuthConstants.UserScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.PrincipalTypeClaim, AuthConstants.UserPrincipalType))
    .AddPolicy(AuthConstants.RenderMachinePolicy, policy => policy
        .AddAuthenticationSchemes(AuthConstants.ApiTokenScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.PrincipalTypeClaim, AuthConstants.MachinePrincipalType))
    // Every endpoint without its own [Authorize]/[AllowAnonymous] requires a signed-in user.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(AuthConstants.UserScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(AuthConstants.PrincipalTypeClaim, AuthConstants.UserPrincipalType)
        .Build());

// Brute-force protection for the login form: 10 attempts per minute per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthConstants.LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.InitializeDatabase();

// `Server create-user <name>`: create an account (e.g. the first one) and exit without serving.
if (CreateUserCommand.IsInvoked(args))
    return await CreateUserCommand.RunAsync(app, args);

Directory.CreateDirectory(app.Services.GetRequiredService<JobPaths>().Root);
await app.FailInterruptedJobsAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// The only anonymous endpoints: static CSS/JS/images (needed to render the login page) and the
// login page itself ([AllowAnonymous] on AccountController). Everything else requires sign-in.
app.MapStaticAssets().AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
return 0;
