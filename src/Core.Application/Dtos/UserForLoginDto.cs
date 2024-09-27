using System.Text.Json.Serialization;

namespace Core.Application.Dtos;

public class UserForLoginDto(string email, string password) : IDto
{
    public required string Email { get; set; } = email;

    [JsonIgnore]
    public string Password { get; set; } = password;

    [JsonIgnore]
    public string? AuthenticatorCode { get; set; }

    public UserForLoginDto() : this(string.Empty, string.Empty)
    {
    }
}