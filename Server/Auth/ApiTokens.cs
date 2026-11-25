using System.Security.Cryptography;
using System.Text;

namespace Server.Auth;

public static class ApiTokens
{
    public const string Prefix = "bdmx_";
    private const int DisplayPrefixLength = 12;

    public static (string Token, string Hash, string DisplayPrefix) Generate()
    {
        var token = Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token), token[..DisplayPrefixLength]);
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
