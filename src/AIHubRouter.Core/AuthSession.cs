namespace AIHubRouter.Core;

public sealed record AuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    public bool IsUsable(DateTimeOffset now, TimeSpan renewalMargin)
    {
        return !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAt - now > renewalMargin;
    }
}

public sealed record LoginCredentials(string Email, string Password)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);
}

public sealed class InteractiveAuthenticationRequiredException : Exception
{
    public InteractiveAuthenticationRequiredException()
        : base("当前账号需要验证码或两步验证，请先在 AIHub 网页完成登录验证。")
    {
    }
}
