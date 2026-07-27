namespace AgentHub.Api.Browser;

public sealed record BrowserViewport(int Width, int Height)
{
    public const int MinWidth = 480;
    public const int MinHeight = 320;
    public const int MaxWidth = 2560;
    public const int MaxHeight = 1600;

    public static bool TryCreate(int width, int height, out BrowserViewport? viewport)
    {
        if (width < MinWidth || height < MinHeight ||
            width > MaxWidth || height > MaxHeight)
        {
            viewport = null;
            return false;
        }
        viewport = new BrowserViewport(width, height);
        return true;
    }
}
