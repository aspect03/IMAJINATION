using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ImajinationAPI.Services;

var dotenvPath = FindDotEnvPath();
var dotenvValues = dotenvPath is null
    ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    : LoadDotEnv(dotenvPath);
var builder = WebApplication.CreateBuilder(args);

var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

if (dotenvValues.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(dotenvValues);
}

var allowedOrigins = builder.Configuration
    .GetSection("AppSecurity:AllowedOrigins")
    .Get<string[]>()?
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray()
    ?? Array.Empty<string>();
var jwtBootstrapService = new JwtTokenService(builder.Configuration);

// Add Controllers
builder.Services.AddControllers();
builder.Services.AddDataProtection();
builder.Services.AddSingleton(jwtBootstrapService);
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<UploadScanningService>();
builder.Services.AddSingleton<MessageProtectionService>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "HybridAuth";
        options.DefaultAuthenticateScheme = "HybridAuth";
        options.DefaultChallengeScheme = "HybridAuth";
    })
    .AddPolicyScheme("HybridAuth", "JWT or session token", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            if (!string.IsNullOrWhiteSpace(context.Request.Headers["X-Session-Token"]))
            {
                return SessionTokenAuthenticationHandler.SchemeName;
            }

            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = jwtBootstrapService.CreateValidationParameters();
    })
    .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(
        SessionTokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "IMAJINATION-XSRF";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddMemoryCache(); // <-- ADD THIS LINE
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"message\":\"Too many requests. Please wait a moment and try again.\"}",
            cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var remoteKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"{remoteKey}:{path}";
        var isAuthRoute = path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = isAuthRoute ? 10 : 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = isAuthRoute ? 0 : 20
            });
    });
});

// Add CORS - This is the "Key" that lets your HTML talk to C#
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowImajination",
        policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
            else
            {
                // Safe local-development fallback
                policy.WithOrigins(
                          "http://localhost:5248",
                          "https://localhost:5248",
                          "http://127.0.0.1:5248",
                          "https://127.0.0.1:5248")
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            }
        });
});

var app = builder.Build();

var legacyStaticPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/"] = "/pages/home/LandingPage.html",
    ["/LandingPage.html"] = "/pages/home/LandingPage.html",
    ["/login.html"] = "/pages/auth/login.html",
    ["/forgotpassword.html"] = "/pages/auth/forgotpassword.html",
    ["/signuproles.html"] = "/pages/auth/signuproles.html",
    ["/signup customer.html"] = "/pages/auth/signup customer.html",
    ["/signup organizer.html"] = "/pages/auth/signup organizer.html",
    ["/artist.html"] = "/pages/auth/artist.html",
    ["/sessionist.html"] = "/pages/auth/sessionist.html",
    ["/Events.html"] = "/pages/browse/Events.html",
    ["/Artists.html"] = "/pages/browse/Artists.html",
    ["/Sessionists.html"] = "/pages/browse/Sessionists.html",
    ["/Community.html"] = "/pages/browse/Community.html",
    ["/Checkout.html"] = "/pages/bookings/Checkout.html",
    ["/messages.html"] = "/pages/bookings/messages.html",
    ["/CustomerDashboard.html"] = "/pages/dashboards/CustomerDashboard.html",
    ["/OrganizerDashboard.html"] = "/pages/dashboards/OrganizerDashboard.html",
    ["/ArtistDashboard.html"] = "/pages/dashboards/ArtistDashboard.html",
    ["/SessionistDashboard.html"] = "/pages/dashboards/SessionistDashboard.html",
    ["/ProfileAdmin.html"] = "/pages/dashboards/ProfileAdmin.html",
    ["/ArtistDetails.html"] = "/pages/details/ArtistDetails.html",
    ["/artistdetails.html"] = "/pages/details/ArtistDetails.html",
    ["/EventDetailPage.html"] = "/pages/details/EventDetailPage.html",
    ["/SessionistDetailsPage.html"] = "/pages/details/SessionistDetailsPage.html",
    ["/dashboardscanner.html"] = "/pages/tools/dashboardscanner.html",
    ["/style.css"] = "/assets/css/style.css"
};

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self' data: blob: https:; " +
        "img-src 'self' data: blob: https:; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com; " +
        "font-src 'self' data: https://fonts.gstatic.com; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.tailwindcss.com https://unpkg.com https://accounts.google.com https://cdn.jsdelivr.net; " +
        "frame-src 'self' https://accounts.google.com; " +
        "connect-src 'self' https:;";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(self), microphone=(), geolocation=(), payment=(), usb=()";

    await next();
});

var contentRootProvider = new PhysicalFileProvider(app.Environment.ContentRootPath);

app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path.Value;

    if (!string.IsNullOrWhiteSpace(requestPath))
    {
        if (legacyStaticPaths.TryGetValue(requestPath, out var rewrittenPath))
        {
            context.Request.Path = rewrittenPath;
        }
        else if (requestPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/assets" + requestPath;
        }
    }

    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = contentRootProvider
});

// Enable CORS
app.UseCors("AllowImajination");
app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

var antiforgery = app.Services.GetRequiredService<IAntiforgery>();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") ||
        HttpMethods.IsGet(context.Request.Method) ||
        HttpMethods.IsHead(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method) ||
        HttpMethods.IsTrace(context.Request.Method) ||
        context.Request.Path.StartsWithSegments("/api/security/csrf-token"))
    {
        await next();
        return;
    }

    try
    {
        await antiforgery.ValidateRequestAsync(context);
        await next();
    }
    catch (AntiforgeryValidationException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"message\":\"Security validation failed. Refresh the page and try again.\"}");
    }
});

app.UseAuthentication();
app.UseAuthorization();

// Map the routes
app.MapControllers();

app.Run();

static Dictionary<string, string?> LoadDotEnv(string filePath)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(filePath))
    {
        return values;
    }

    foreach (var rawLine in File.ReadAllLines(filePath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim().Replace("__", ":");
        var value = line[(separatorIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            continue;
        }

        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        values[key] = value;
    }

    return values;
}

static string? FindDotEnvPath()
{
    var startingPoints = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    }
    .Where(path => !string.IsNullOrWhiteSpace(path))
    .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var start in startingPoints)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }
    }

    return null;
}
