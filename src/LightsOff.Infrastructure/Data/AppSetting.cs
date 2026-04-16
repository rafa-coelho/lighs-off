namespace LightsOff.Infrastructure.Data;

public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public static class AppSettingKeys
{
    public const string JiraBaseUrl = "jira.baseUrl";
    public const string JiraEmail = "jira.email";
    public const string JiraApiTokenSecretKey = "jira.apiTokenSecretKey";
    public const string ActiveBoardId = "jira.activeBoardId";
}
