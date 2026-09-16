using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace AtlasMail.Web.Controllers;

/// <summary>Páginas de shell: login, acceso denegado.</summary>
public class AccountController : Controller
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login() => View();

    [HttpGet]
    public IActionResult AccessDenied() => View();

    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
}

/// <summary>Página base y redirección según rol.</summary>
public class HomeController : Controller
{
    [Authorize]
    public IActionResult Index()
    {
        return User.IsInRole("SuperAdmin") || User.IsInRole("DomainAdmin") || User.IsInRole("SecurityAdmin")
            ? RedirectToAction("Index", "Admin")
            : RedirectToAction("Index", "Webmail");
    }

    [Authorize]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    public IActionResult Error() => View();
}

/// <summary>Webmail (shell SPA manejada con fetch).</summary>
[Authorize]
public class WebmailController : Controller
{
    public IActionResult Index() => View();
}

/// <summary>Admin center (shell).</summary>
[Authorize(Roles = "SuperAdmin,DomainAdmin,SecurityAdmin")]
public class AdminController : Controller
{
    public IActionResult Index() => View();
}