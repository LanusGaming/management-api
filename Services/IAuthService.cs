using Management.Api.DTOs;
using Management.Api.Models;

namespace Management.Api.Services;

public interface IAuthService
{
    Task<bool> RegisterAsync(AuthRequest request);
    Task<LoginResponse?> LoginAsync(AuthRequest request);
    Task<RefreshToken?> ValidateAsync(string refreshToken);
    Task<LoginResponse?> RefreshAsync(string refreshToken);
    Task RevokeAsync(string refreshToken);
}