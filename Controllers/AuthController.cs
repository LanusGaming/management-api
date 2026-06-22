using Dashboard.Api.DTOs;
using Dashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAuthService _authService;

    public AuthController(IConfiguration configuration, IWebHostEnvironment environment, IAuthService authService)
    {
        _configuration = configuration;
        _environment = environment;
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(AuthRequest request)
    {
        var success = await _authService.RegisterAsync(request);

        if (!success)
            return Conflict(new { message = "Username already taken" });

        return Created("", new { message = "User created successfully" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(AuthRequest request)
    {
        var result = await _authService.LoginAsync(request);

        if (result == null)
            return Unauthorized(new { message = "Invalid username or password" });

        Response.Cookies.Append("refreshToken", result.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = _environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddDays(
                _configuration.GetValue<int>("Jwt:RefreshTokenExpiryDays"))
        });

        return Ok(new
        {
            accessToken = result.AccessToken,
            expiresAt = result.ExpiresAt
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var refreshToken = Request.Cookies["refreshToken"];

        if (refreshToken == null)
            return Unauthorized(new { message = "No refresh token" });

        var result = await _authService.RefreshAsync(refreshToken);

        if (result == null)
            return Unauthorized(new { message = "Invalid or expired refresh token" });

        return Ok(new
        {
            accessToken = result.AccessToken,
            expiresAt = result.ExpiresAt
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = Request.Cookies["refreshToken"];

        if (refreshToken != null)
        {
            await _authService.RevokeAsync(refreshToken);
            Response.Cookies.Delete("refreshToken");
        }
        
        return NoContent();
    }
}