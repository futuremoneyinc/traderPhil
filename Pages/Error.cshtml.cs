using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TraderPhil.V4.Web.Pages;

public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet() => RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}
