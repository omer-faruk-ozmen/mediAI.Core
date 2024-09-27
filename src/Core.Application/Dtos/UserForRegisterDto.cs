using System.Text.Json.Serialization;

namespace Core.Application.Dtos;

public class UserForRegisterDto(string email, string password) : IDto
{
    public required string Email { get; set; } = email;

    [JsonIgnore]
    public string Password { get; set; } = password;

    public UserForRegisterDto() : this(string.Empty, string.Empty)
    {
    }
}

