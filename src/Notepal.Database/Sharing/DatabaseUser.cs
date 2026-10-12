namespace Notepal.Database;

/// <summary>Caller identity supplied by the API after authentication, used to scope database queries.</summary>
public sealed record DatabaseUser(string UserId, string? Email);
