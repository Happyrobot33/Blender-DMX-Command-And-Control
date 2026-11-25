namespace Server.Auth;

public static class AuthConstants
{
    public const string UserScheme = "UserCookie";

    public const string ApiTokenScheme = "ApiToken";

    public const string UserPolicy = "User";

    public const string RenderMachinePolicy = "RenderMachine";

    public const string PrincipalTypeClaim = "bdmx:principal_type";

    public const string SecurityStampClaim = "bdmx:security_stamp";

    public const string UserPrincipalType = "user";
    public const string MachinePrincipalType = "machine";

    public const string LoginRateLimit = "login";
}
