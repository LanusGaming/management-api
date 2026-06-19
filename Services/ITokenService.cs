using Dashboard.Api.Models;

namespace Dashboard.Api.Services;

public interface ITokenService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
}