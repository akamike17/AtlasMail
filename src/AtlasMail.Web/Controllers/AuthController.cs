using System.Security.Claims;
using AtlasMail.Application;
using AtlasMail.Application.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AtlasMail.Web.Controllers;

/// <summary>
/// Endpoints JSON para session (login/logout/me) consumidos por fetch().
/// Login y logout son [AllowAnonymous] para servicio; el resto requiere sesión.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) { _auth = auth; }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (_auth.IsBlocked(HttpContext.Connection.RemoteIpAddress?.ToString(), req.Username))
            return Unauthorized(new { error = "Demasiados intentos. Intenta en unos minutos." });

        var result = await _auth.LoginAsync(req, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());
        if (!result.Success) return Unauthorized(new { error = result.Error });
        if (result.Username == null) return Unauthorized(new { error = "error" });
        if (result.Role == null) return Unauthorized(new { error = "error" });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.Username),
            new(ClaimTypes.Name, result.Username),
            new(ClaimTypes.Role, result.Role.Value.ToString()),
            new("domainId", result.DomainId?.ToString() ?? string.Empty)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });

        return Ok(new { username = result.Username, role = result.Role.ToString() });
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { ok = true });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        var username = User.Identity?.Name ?? string.Empty;
        var role = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value ?? "User";
        var domainId = User.Claims.FirstOrDefault(c => c.Type == "domainId")?.Value;
        return Ok(new { username, role, domainId });
    }
}