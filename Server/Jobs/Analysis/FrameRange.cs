namespace Server.Jobs.Analysis;

public record FrameRange(string SceneName, int Start, int End, int FrameRate = 24)
{
    public int FrameCount => End < Start ? 0 : End - Start + 1;
}
