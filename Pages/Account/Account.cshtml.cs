using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using TraderPhil.V4.Web.Auth;

namespace TraderPhil.V4.Web.Pages.Account;

public class SignInModel : PageModel
{
    public IActionResult OnGet(string? returnUrl = null)
    {
        var redirect = string.IsNullOrEmpty(returnUrl) ? "/Performance" : returnUrl;
        return Challenge(
            new AuthenticationProperties { RedirectUri = "/Account/Callback?returnUrl=" + Uri.EscapeDataString(redirect) },
            GoogleDefaults.AuthenticationScheme);
    }
}

public class CallbackModel : PageModel
{
    private readonly IWebUserRepository _users;

    public CallbackModel(IWebUserRepository users) { _users = users; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        // The Google handler has already populated User with claims at this point.
        var sub   = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = User.FindFirstValue(ClaimTypes.Email);
        var name  = User.FindFirstValue(ClaimTypes.Name);

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email))
            return RedirectToPage("/Account/Forbidden");

        var webUser = await _users.SignInAsync(sub, email, name);
        if (webUser is null)
            return RedirectToPage("/Account/Forbidden");

        // Build our own cookie identity carrying the WebUserID + admin flag.
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, sub),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name ?? email),
            new Claim("WebUserID", webUser.WebUserID.ToString()),
            new Claim("IsAdmin",   webUser.IsAdmin.ToString())
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/Performance" : returnUrl);
    }
}

public class SignOutModel : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Account/SignedOut");
    }
}

public class SignedOutModel : PageModel { public void OnGet() { } }

public class ForbiddenModel : PageModel { public void OnGet() { } }
