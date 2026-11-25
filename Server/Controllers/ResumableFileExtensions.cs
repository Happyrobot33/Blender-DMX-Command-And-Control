using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Server.Controllers;

public static class ResumableFileExtensions
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".mkv"] = "video/x-matroska"; // not in ASP.NET Core's default table
        return provider;
    }

    public static PhysicalFileResult ResumableFile(this ControllerBase controller, string path, string downloadName, string? contentType = null)
    {
        var info = new FileInfo(path);
        contentType ??= ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        return controller.PhysicalFile(info.FullName, contentType, downloadName,
            lastModified: info.LastWriteTimeUtc, entityTag: etag, enableRangeProcessing: true);
    }
}
