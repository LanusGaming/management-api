using Dashboard.Api.DTOs;

namespace Dashboard.Api.Services;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(LoginRequest request);
    Task<LoginResponse?> RefreshAsync(string refreshToken);
    Task RevokeAsync(string refreshToken);
}