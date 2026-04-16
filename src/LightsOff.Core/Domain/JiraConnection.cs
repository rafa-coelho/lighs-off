namespace LightsOff.Core.Domain;

public class JiraConnection
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiTokenSecretKey { get; set; } = "jira.apiToken";
}
