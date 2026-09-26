namespace AuthCenter.Infrastructure.Settings;

public class EmailSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "AuthCenter";

    /// <summary>
    /// Development and automated tests only: write each message as a JSON file in this directory
    /// instead of sending it (end-to-end tests read codes and links from it). Refused elsewhere.
    /// </summary>
    public string? DevelopmentPickupDirectory { get; init; }
}
