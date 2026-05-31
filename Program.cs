using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// -- MVC / Razor Pages
builder.Services.AddRazorPages();

// -- Authentication: Cookie + Google OAuth
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath        = "/Account/SignIn";
        options.LogoutPath       = "/Account/SignOut";
        options.AccessDeniedPath = "/Account/Forbidden";
        options.ExpireTimeSpan   = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    })
    .AddGoogle(options =>
    {
        options.ClientId     = builder.Configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException("Authentication:Google:ClientId missing.");
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Authentication:Google:ClientSecret missing.");
        // We keep the Google `sub` claim — it's what WebUsers.GoogleSubjectID maps to.
        options.SaveTokens = false;
    });

builder.Services.AddAuthorization();

// -- Data layer
builder.Services.AddSingleton<IWebUserRepository, WebUserRepository>();
builder.Services.AddSingleton<IPerformanceRepository, PerformanceRepository>();
builder.Services.AddSingleton<ITreasuryRepository, TreasuryRepository>();

// -- HTTP context accessor needed by the page model base
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Convenience: hitting "/" should land on the Performance dashboard.
app.MapGet("/", context =>
{
    context.Response.Redirect("/Performance");
    return Task.CompletedTask;
});

app.Run();
