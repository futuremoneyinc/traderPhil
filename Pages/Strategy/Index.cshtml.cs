using Microsoft.AspNetCore.Authorization;
using TraderPhil.V4.Web.Auth;

namespace TraderPhil.V4.Web.Pages.Strategy;

[Authorize]
public class IndexModel : TraderPhilPageModel
{
    public IndexModel(IWebUserRepository users) : base(users) { }
    public void OnGet() { }
}
