using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using TraderPhil.V4.Web.Auth;
using TraderPhil.V4.Web.Data;
using TraderPhil.V4.Web.Services;

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
        options.LoginPath         = "/Account/SignIn";
        options.LogoutPath        = "/Account/SignOut";
        options.AccessDeniedPath  = "/Account/Forbidden";

        // 30-day window with sliding renewal: every page hit within the window
        // resets the 30-day clock. After 30 days of inactivity, sign-in is required.
        options.ExpireTimeSpan    = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        // Persist the cookie to disk so it survives browser restarts, mobile tab
        // eviction, OS sleep, etc. Without this, the cookie is session-only and
        // mobile browsers kill it aggressively when the tab goes background.
        options.Cookie.MaxAge       = TimeSpan.FromDays(30);
        options.Cookie.SameSite     = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly     = true;

        // On every validated request, refresh the cookie window and reaffirm
        // IsPersistent. This is what makes sliding expiration actually slide
        // on browsers that respect MaxAge.
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = ctx =>
            {
                if (ctx.Properties is not null)
                {
                    ctx.Properties.IsPersistent = true;
                    ctx.Properties.ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(30);
                }
                ctx.ShouldRenew = true;
                return Task.CompletedTask;
            }
        };
    })
    .AddGoogle(options =>
    {
        options.ClientId     = builder.Configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException("Authentication:Google:ClientId missing.");
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Authentication:Google:ClientSecret missing.");

        // We keep the Google `sub` claim - it's what WebUsers.GoogleSubjectID maps to.
        options.SaveTokens = false;

        options.Events = new OAuthEvents
        {
            // CRITICAL: do the WebUser lookup HERE, while the OAuth ticket is being
            // formed, and append our custom claims (WebUserID, IsAdmin) to the
            // principal that's about to be persisted as a cookie. This produces a
            // single Set-Cookie header in the response - no double-write race.
            //
            // The previous design had the Google handler write the cookie first
            // (Google claims only), then Callback.OnGetAsync would call SignInAsync
            // a second time to overwrite it with WebUserID. Mobile browsers
            // occasionally dropped the second Set-Cookie on the redirect chain,
            // leaving the user authenticated but with no WebUserID claim and
            // causing the "can't find my account" loop.
            OnTicketReceived = async ctx =>
            {
                // Persistent cookie so it survives browser restarts / mobile tab eviction.
                if (ctx.Properties is not null)
                {
                    ctx.Properties.IsPersistent = true;
                    ctx.Properties.ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(30);
                }

                if (ctx.Principal?.Identity is not ClaimsIdentity identity)
                    return;

                var sub   = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var email = identity.FindFirst(ClaimTypes.Email)?.Value;
                var name  = identity.FindFirst(ClaimTypes.Name)?.Value;

                if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email))
                {
                    // No usable identity from Google - fail the ticket so the cookie
                    // is never written. User will land on /Account/Forbidden via the
                    // OnRemoteFailure path.
                    ctx.Fail("Google ticket missing sub or email");
                    return;
                }

                var users = ctx.HttpContext.RequestServices.GetRequiredService<IWebUserRepository>();
                var webUser = await users.SignInAsync(sub, email, name);
                if (webUser is null)
                {
                    // User exists but is marked inactive, or some other lookup failure.
                    ctx.Fail("WebUser lookup failed or account inactive");
                    return;
                }

                // Append our app-specific claims to the principal that's about to be
                // converted to a cookie. Single Set-Cookie, no race.
                identity.AddClaim(new Claim("WebUserID", webUser.WebUserID.ToString()));
                identity.AddClaim(new Claim("IsAdmin",   webUser.IsAdmin.ToString()));
            },

            // If OnTicketReceived calls ctx.Fail, OR Google itself sends back an error,
            // route the user to /Account/Forbidden with a clean state (no half-cookie).
            OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/Account/Forbidden");
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
        };

        // OAuth correlation cookie needs Secure + Lax so mobile browsers
        // don't drop it during the Google redirect handshake.
        options.CorrelationCookie.SameSite     = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();

// -- Data layer
builder.Services.AddSingleton<IWebUserRepository, WebUserRepository>();
builder.Services.AddSingleton<IPerformanceRepository, PerformanceRepository>();
builder.Services.AddSingleton<ITreasuryRepository, TreasuryRepository>();
builder.Services.AddSingleton<IStrategyRepository, StrategyRepository>();
builder.Services.AddSingleton<IUserSettingsRepository, UserSettingsRepository>();
builder.Services.AddSingleton<IProfitTargetsRepository, ProfitTargetsRepository>();
builder.Services.AddSingleton<IFundingRepository, FundingRepository>();
builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
builder.Services.AddSingleton<ISubscriptionRepository, StubSubscriptionRepository>();
builder.Services.AddSingleton<IOnboardingRepository, OnboardingRepository>();

// -- Funding cache (deposit methods/addresses). See _Wiring_Notes.txt.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IFundingCache, FundingCache>();

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
