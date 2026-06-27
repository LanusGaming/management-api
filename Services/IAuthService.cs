using Dashboard.Api.DTOs;
using Dashboard.Api.Models;

namespace Dashboard.Api.Services;

public interface IAuthService
{
    Task<bool> RegisterAsync(AuthRequest request);
    Task<LoginResponse?> LoginAsync(AuthRequest request);
    Task<RefreshToken?> ValidateAsync(string refreshToken);
    Task<LoginResponse?> RefreshAsync(string refreshToken);
    Task RevokeAsync(string refreshToken);
}