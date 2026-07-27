namespace AgentHub.Api.Browser;

public sealed class BrowserOptions
{
    public bool Enabled { get; set; } = true;
    public string Image { get; set; } = "ghcr.io/open-agenthub/open-agenthub/browser:latest";
    public string PullPolicy { get; set; } = "IfNotPresent";
    public string ImagePullSecret { get; set; } = "";
    public string RuntimeClassName { get; set; } = "";
    public string CpuRequest { get; set; } = "250m";
    public string MemoryRequest { get; set; } = "512Mi";
    public string CpuLimit { get; set; } = "1";
    public string MemoryLimit { get; set; } = "2Gi";
    public int ScreenWidth { get; set; } = 2560;
    public int ScreenHeight { get; set; } = 1600;
    public int StartupTimeoutSeconds { get; set; } = 90;
    public int CookieCheckpointSeconds { get; set; } = 60;
    public int CookieStateMaxBytes { get; set; } = 1_048_576;
    public int[] ExtraEgressPorts { get; set; } = [];
}
