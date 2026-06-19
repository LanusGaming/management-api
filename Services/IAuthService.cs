using Dashboard.Api.DTOs;

namespace Dashboard.Api.Services;

public interface IAuthService
{
    Task<bool> RegisterAsync(AuthRequest request);
    Task<LoginResponse?> LoginAsync(AuthRequest request);
    Task<LoginResponse?> RefreshAsync(string refreshToken);
    Task RevokeAsync(string refreshToken);
}