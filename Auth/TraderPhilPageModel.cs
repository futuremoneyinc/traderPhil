using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TraderPhil.V4.Web.Auth;

/// <summary>
/// Base class for every page that needs to know who the user is and what UEIs
/// they're allowed to look at. Inherit from this instead of PageModel directly.
///
/// Usage in a page:
///     public class IndexModel : TraderPhilPageModel
///     {
///         public IndexModel(IWebUserRepository users) : base(users) { }
///
///         public async Task<IActionResult> OnGetAsync(int uei)
///         {
///             var deny = await AssertUeiAccessAsync(uei);
///             if (deny is not null) return deny;
///             // ... load data scoped to uei ...
///             return Page();
///         }
///     }
/// </summary>
public abstract class TraderPhilPageModel : PageModel
{
    protected readonly IWebUserRepository UserRepo;

    protected TraderPhilPageModel(IWebUserRepository users)
    {
        UserRepo = users;
    }

    /// <summary>Returns the current logged-in WebUserID, or null if not signed in.</summary>
    public int? CurrentWebUserId
    {
        get
        {
            var s = User.FindFirstValue("WebUserID");
            return int.TryParse(s, out var id) ? id : null;
        }
    }

    /// <summary>Returns true if the user is logged in and the WebUsers.IsAdmin flag is set.</summary>
    public bool CurrentUserIsAdmin =>
        User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "True");

    /// <summary>The user's display name from the cookie, for the nav greeting.</summary>
    public string? CurrentDisplayName =>
        User.Identity?.IsAuthenticated == true
            ? (User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.Email))
            : null;

    /// <summary>
    /// Enforces that the current user is allowed to access the given UEI.
    /// Returns null when access is granted; returns a redirect/forbid result otherwise.
    /// Always await the result and short-circuit if non-null.
    ///
    /// Admins bypass the WebUserUEI check entirely.
    /// </summary>
    protected async Task<IActionResult?> AssertUeiAccessAsync(int uei)
    {
        if (CurrentWebUserId is not int webUserId)
            return RedirectToPage("/Account/SignIn");

        if (CurrentUserIsAdmin) return null;

        var allowed = await UserRepo.HasUeiAccessAsync(webUserId, uei);
        return allowed ? null : Forbid();
    }

    /// <summary>
    /// Convenience: returns the UEIs this user can see. Empty list = brand-new user with no grants.
    /// </summary>
    protected async Task<IReadOnlyList<int>> GetMyUeisAsync()
    {
        if (CurrentWebUserId is not int id) return Array.Empty<int>();
        return await UserRepo.GetGrantedUeisAsync(id);
    }
}
