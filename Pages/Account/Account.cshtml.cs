using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    // The WebUser lookup and claim injection now happen in Program.cs's
    // OnTicketReceived event, BEFORE the cookie is written. By the time we land
    // here, User is fully populated with WebUserID/IsAdmin claims and the cookie
    // is already in flight. We just need to honor returnUrl.
    //
    // We intentionally do NOT call HttpContext.SignInAsync here. Doing so would
    // produce a second Set-Cookie header on the response chain - the same bug
    // that caused mobile browsers to drop the WebUserID claim and forced users
    // into the "can't find my account" sign-out-and-back-in dance.

    public IActionResult OnGet(string? returnUrl = null)
    {
        // If something went sideways and we don't have a WebUserID claim, kick to
        // Forbidden. This shouldn't happen with the new OnTicketReceived flow
        // (it calls ctx.Fail on lookup failure, which routes to Forbidden via
        // OnRemoteFailure), but it's belt-and-suspenders for robustness.
        if (string.IsNullOrEmpty(User.FindFirst("WebUserID")?.Value))
            return RedirectToPage("/Account/Forbidden");

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
