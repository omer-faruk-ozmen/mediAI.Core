namespace Core.Security.JWT;

public class AccessToken(string token, DateTime expirationDate)
{
    public string Token { get; set; } = token;
    public DateTime ExpirationDate { get; set; } = expirationDate;

    public AccessToken() : this(string.Empty, new DateTime())
    {
    }
}
