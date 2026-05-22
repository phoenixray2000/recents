namespace Recents.App.Models;

public sealed class ClipboardWebDavSyncSettings
{
    public bool Enabled { get; set; } = false;
    public string RemoteDirectoryUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public int PollIntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 60;
    public int RetryTimes { get; set; } = 2;
    public bool IgnoreCertificateErrors { get; set; } = false;
    public bool DeletePreviousFilesOnPush { get; set; } = true;
    public bool SaveRemoteItemsToHistory { get; set; } = true;
    public long MaxPayloadBytes { get; set; } = 20L * 1024 * 1024;
}
