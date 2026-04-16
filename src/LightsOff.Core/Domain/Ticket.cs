namespace LightsOff.Core.Domain;

public sealed record Ticket(
    string Key,
    string Summary,
    string Status,
    string? Assignee,
    string Type,
    string Url);
