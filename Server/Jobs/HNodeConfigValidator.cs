using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Server.Jobs;

public static class HNodeConfigValidator
{
    public const string Extension = ".shwcfg";

    public static string ValidateFileName(string? fileName)
    {
        // Browsers send just the name, but never trust a client-supplied path.
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (name.Length is 0 or > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') || name.StartsWith('.'))
            throw new JobUploadException("The HNode config needs a valid file name.");
        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || name.Length == Extension.Length)
            throw new JobUploadException($"The HNode config must be a {Extension} file, not '{name}'.");
        return name;
    }

    public static void ValidateContent(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new JobUploadException("The HNode config is not valid UTF-8 text.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new JobUploadException($"The HNode config is not valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}");
        }

        if (yaml.Documents.Count == 0 || yaml.Documents.All(d => d.RootNode is YamlScalarNode { Value: null or "" }))
            throw new JobUploadException("The HNode config is empty.");
    }
}
