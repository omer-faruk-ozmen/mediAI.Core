namespace Core.Security.JWT;

public class TokenOptions(
    string audience,
    string issuer,
    int accessTokenExpiration,
    string securityKey,
    int refreshTokenTtl)
{
    public string Audience { get; set; } = audience;
    public string Issuer { get; set; } = issuer;

    /// <summary>
    /// Access token expiration time in minutes.
    /// </summary>
    public int AccessTokenExpiration { get; set; } = accessTokenExpiration;

    public string SecurityKey { get; set; } = securityKey;

    /// <summary>
    /// Refresh token time in days.
    /// </summary>
    public int RefreshTokenTTL { get; set; } = refreshTokenTtl;

    public TokenOptions() : this(string.Empty, string.Empty, 0, string.Empty, 0)
    {
    }
}
