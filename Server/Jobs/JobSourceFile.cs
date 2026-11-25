namespace Server.Jobs;

public record JobSourceFile(string RelativePath, long SizeBytes, DateTime LastModifiedUtc);
