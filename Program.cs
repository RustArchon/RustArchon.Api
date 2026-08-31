// Copyright ©2026 Scott Blomfield

using AutoMapper;
using Correlate.AspNetCore;
using JumpStart.Services.Authentication;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Authentication;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using StackExchange.Redis;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// 1. DATABASE CONTEXT
// ============================================
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApiDbContext>(options =>
    options.UseNpgsql(connectionString));

// ============================================
// 2. JUMPSTART FRAMEWORK SERVICES
// ============================================
builder.Services.AddJumpStart(options =>
{
    options.RegisterUserContext<ApiUserContext>();
    options.RegisterTenantContext<JwtTenantContext>();
    options.AutoDiscoverRepositories = true; // required for EnsureDbContextResolution
    options.ScanAssembly(typeof(Program).Assembly);
    options.RegisterAuthorizationController = true; // Roles/UserPermissions CRUD - also registers
                                                      // IRoleRepository, used by AccountBootstrapController
    options.RegisterTokenController = true;          // POST /api/token/exchange
    options.RegisterTenantsController = true;        // Tenant CRUD + membership - also registers
                                                      // ITenantRepository/IUserTenantRepository
});

// ============================================
// 3. AUTOMAPPER
// ============================================
builder.Services.AddJumpStartAutoMapper(
    typeof(Program).Assembly,          // RustArchon.Api mapping profiles
    typeof(JumpStart.Data.Tenant).Assembly); // JumpStart framework mapping profiles (Tenant, Role, ...)

// ============================================
// 4. RCON CREDENTIAL PROTECTION
// ============================================
// The Data Protection key ring is persisted to disk rather than left at .NET's default per-machine
// profile - a container can be recreated at any time, and without a persisted key ring every
// previously-encrypted RconPassword becomes permanently undecryptable the moment that happens.
// DataProtection:KeyPath defaults to a local folder for non-Docker dev; the Docker Compose setup
// points it at a named volume (/keys) instead. This only covers a single API instance/volume - see
// the README for what changes once RustArchon.Api is ever scaled to more than one replica.
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dataprotection-keys");
Directory.CreateDirectory(dataProtectionKeyPath);

builder.Services.AddDataProtection()
    .SetApplicationName("RustArchon")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
builder.Services.AddScoped<IRconCredentialProtector, RconCredentialProtector>();

// ============================================
// 4b. PLATFORM SETTINGS (Valkey-cached, Postgres-backed)
// ============================================
// IConnectionMultiplexer is registered ONLY when a connection string is actually configured -
// PlatformSettingsCache resolves it lazily via IServiceProvider.GetService (not constructor
// injection), so an environment that never configures Valkey at all still runs fine, straight against
// Postgres for every settings read. See PlatformSettingsCache's remarks for the rest of its
// defense-in-depth against Valkey being unreachable.
var valkeyConnectionString = builder.Configuration["Valkey:ConnectionString"];
if (!string.IsNullOrWhiteSpace(valkeyConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var options = ConfigurationOptions.Parse(valkeyConnectionString);
        // Never let a temporarily-unreachable Valkey block or fail Api startup - see
        // PlatformSettingsCache's remarks (degradation level 2). The multiplexer keeps retrying the
        // connection in the background regardless.
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    });
}

// IPlatformSettingRepository itself is picked up by AutoDiscoverRepositories below, same as every
// other repository in this project - no explicit registration needed here.
//
// Scoped, not Singleton - it depends on IPlatformSettingRepository, itself scoped to ApiDbContext's
// own per-request lifetime. IConnectionMultiplexer (genuinely a singleton) is still resolved lazily
// via IServiceProvider inside PlatformSettingsCache rather than constructor-injected, purely so a
// deployment that never configures Valkey at all doesn't turn depending on this cache into a DI
// resolution failure - see its remarks.
builder.Services.AddScoped<IPlatformSettingsCache, PlatformSettingsCache>();

// ============================================
// 4c. MESSAGING (RABBITMQ)
// ============================================
// Username/Password come from RABBITMQ_DEFAULT_USER/PASS directly - the RabbitMQ container's own
// required env var names, not a RustArchon-specific rename. See RabbitMqOptions's remarks.
var rabbitMqOptions = new RabbitMqOptions
{
    Host = builder.Configuration["RabbitMq:Host"] ?? "localhost",
    VirtualHost = builder.Configuration["RabbitMq:VirtualHost"] ?? "/",
    Username = builder.Configuration["RABBITMQ_DEFAULT_USER"] ?? "guest",
    Password = builder.Configuration["RABBITMQ_DEFAULT_PASS"] ?? "guest"
};

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RconFrameIngestionConsumer>();
    x.AddConsumer<ConnectionStatusConsumer>();
    x.AddConsumer<ServerConnectionHeartbeatConsumer>();

    // 10s matches RustArchon.Worker's SendRconCommandConsumer fanout - see RustServersController's
    // SendCommand action for how a RequestTimeoutException (no instance responded - e.g. no worker
    // currently owns this server) maps to a 504.
    x.AddRequestClient<SendRconCommand>(RequestTimeout.After(s: 10));

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);
        });

        // The three AddConsumer<> registrations above are ordinary competing-consumer subscriptions
        // (the API runs as a single instance, so there's no fanout-vs-competing distinction to make
        // the way there is on the Worker side for ServerLifecycleChanged/SendRconCommand) -
        // ConfigureEndpoints' own default per-consumer-type queue naming is exactly right here, no
        // explicit ReceiveEndpoint needed. SendRconCommand's request client above needs no matching
        // consumer registration at all - MassTransit manages its temporary reply queue internally.
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHostedService<ServerClaimSweepService>();
builder.Services.AddSignalR();

// ============================================
// 5. JWT AUTHENTICATION CONFIGURATION
// ============================================
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>()
    ?? throw new InvalidOperationException("JwtSettings configuration is missing");

// SecretKey specifically comes from the flat RUSTARCHON_JWT_SECRET_KEY key, not the nested
// "JwtSettings" section above (Issuer/Audience/ExpirationMinutes still do) - see ADR-016 (JumpStart)
// for why JwtTokenService itself now supports this, and the PostConfigure<JwtTokenOptions> call below
// for the other half: RustArchon (Blazor) signs identity-assertion tokens with the same key this
// validates against, so both reads must agree.
jwtSettings.SecretKey = builder.Configuration["RUSTARCHON_JWT_SECRET_KEY"]
    ?? throw new InvalidOperationException("RUSTARCHON_JWT_SECRET_KEY configuration is missing.");

// Same flat-key approach used throughout this file - RUSTARCHON_INTERNAL_API_KEY reaches this
// property directly, and it's the exact same name RustArchon.Worker and the Blazor web app read too.
builder.Services.Configure<InternalApiKeyOptions>(options =>
    options.SharedSecret = builder.Configuration["RUSTARCHON_INTERNAL_API_KEY"] ?? string.Empty);

// AddJumpStart's RegisterTokenController=true (below) registers JwtTokenService with JwtTokenOptions
// bound from the "JwtSettings" section by default (see JumpStart's AddJwtTokenService/ADR-016) -
// override SecretKey the same way as jwtSettings.SecretKey above, so TokenController.Exchange signs
// with the exact key this process's own AddJwtBearer setup validates against.
builder.Services.PostConfigure<JwtTokenOptions>(options =>
    options.SecretKey = builder.Configuration["RUSTARCHON_JWT_SECRET_KEY"] ?? options.SecretKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
            ClockSkew = TimeSpan.Zero // No tolerance for expired tokens
        };

        // Browsers can't set an Authorization header on a WebSocket handshake - SignalR's documented
        // workaround is accepting the token from the query string instead, scoped to just the hub
        // path so this doesn't loosen how every other (ordinary HTTP) endpoint accepts a token.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            // Without this, every rejected token surfaces only as a bare 401 with no trace of *why* -
            // expired, bad signature, wrong issuer/audience, and "someone sent garbage" all look
            // identical from the client side. Logged at Warning (not Error) since a rejected token is
            // an expected, routine occurrence (a stale ITokenStore entry, a client clock issue), not a
            // service fault.
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                    .LogWarning(context.Exception, "JWT bearer authentication failed for {Path}", context.HttpContext.Request.Path);
                return Task.CompletedTask;
            }
        };
    })
    // Service-to-service calls (e.g. the Blazor web app's QueuedEmailSender) - a separate scheme from
    // the JWT bearer above, opted into per-endpoint via [Authorize(AuthenticationSchemes = "InternalApiKey")].
    // See InternalApiKeyAuthenticationHandler's remarks.
    .AddScheme<AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>("InternalApiKey", null);

// ============================================
// 6. AUTHORIZATION
// ============================================
// Read once here and reused below by AdminInvitationSeeder, rather than each reading the config key
// independently. RUSTARCHON_ADMIN_EMAIL itself is only ever consulted at account-bootstrap time now
// (see AccountBootstrapController) - PlatformAdmin below is a real Permission claim, resolved and
// granted the same way every other authorization check in this app is, not a live string comparison.
var adminEmail = builder.Configuration["RUSTARCHON_ADMIN_EMAIL"];

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PlatformAdmin", policy =>
        policy.RequireClaim("Permission", SiteAdminRoleSeeder.ManageInvitationsPermission));
    options.AddPolicy("ManagePlatformSettings", policy =>
        policy.RequireClaim("Permission", SiteAdminRoleSeeder.ManageSettingsPermission));
});

// ============================================
// 7. CORS CONFIGURATION
// ============================================
// Allow the RustArchon Blazor Server app to call this API.
var blazorServerUrl = builder.Configuration["CorsSettings:BlazorServerUrl"] ?? "https://localhost:7199";

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorServer", policy =>
    {
        policy.WithOrigins(blazorServerUrl)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// ============================================
// 8. HTTP CONTEXT ACCESSOR (for IUserContext/ITenantContext)
// ============================================
builder.Services.AddHttpContextAccessor();

// ============================================
// 9. API DOCUMENTATION
// ============================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "RustArchon API", Version = "v1" });
});

// ============================================
// 10. CONTROLLERS
// ============================================
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

var app = builder.Build();

// ============================================
// APPLY PENDING MIGRATIONS
// ============================================
// Convenience for local development so the app "just runs" against a fresh LocalDB instance with no
// manual `dotnet ef database update` step. Not appropriate for production services with multiple
// scaled-out instances (concurrent migration application) - gate this behind an environment check
// before deploying beyond a single dev/staging instance.
using (var migrationScope = app.Services.CreateScope())
{
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<ApiDbContext>();
    dbContext.Database.Migrate();

    // See AdminInvitationSeeder's remarks - this is what makes a first account possible at all on a
    // fresh, invitation-gated deployment.
    await AdminInvitationSeeder.SeedAsync(
        dbContext,
        adminEmail,
        builder.Configuration["RUSTARCHON_ADMIN_CODE"],
        migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>());

    // See SiteAdminRoleSeeder's remarks - this is the role AccountBootstrapController grants to a
    // newly-registered RUSTARCHON_ADMIN_EMAIL account, and what the PlatformAdmin policy above
    // actually checks for.
    await SiteAdminRoleSeeder.EnsureRoleAsync(
        dbContext, migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>());

    // See PlatformSettingsRegistry's remarks - seeds every known platform setting (currently just
    // InvitationCodesEnabled) with its default value if the row doesn't exist yet.
    await PlatformSettingsRegistry.EnsureDefaultsAsync(
        dbContext, builder.Configuration, migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>());
}

// ============================================
// MIDDLEWARE PIPELINE
// ============================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "RustArchon API v1");
    });
}
else
{
    // Without this, an unhandled exception outside Development previously fell all the way through
    // to Kestrel's own bare, contentless 500 - no logging, no diagnostic trail, nothing. Registered
    // first (before UseCorrelate, before everything) so it wraps the whole rest of the pipeline,
    // including middleware, not just controller actions. Development deliberately skips this in favor
    // of the framework's own built-in developer exception page (full stack trace), which this generic
    // handler would otherwise shadow.
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            if (exception is not null)
            {
                context.RequestServices.GetRequiredService<ILogger<Program>>()
                    .LogError(exception, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                title = "An unexpected error occurred.",
                status = StatusCodes.Status500InternalServerError
            });
        });
    });
}

app.UseCorrelate();

// The official .NET container base images set this to "true" - skip redirecting to HTTPS when the
// app only has an HTTP endpoint to begin with (ASPNETCORE_URLS=http://+:8080 in the Dockerfiles),
// which is the case for every container in the Docker Compose setup. A reverse proxy in front of the
// public-facing container is where TLS termination belongs in that topology - see the README.
if (!builder.Configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER"))
{
    app.UseHttpsRedirection();
}

// CORS must be before Authentication
app.UseCors("AllowBlazorServer");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<RconHub>("/hubs/rcon");

// Fail fast on startup if any AutoMapper profile is misconfigured.
var mapper = app.Services.GetRequiredService<IMapper>();
mapper.ConfigurationProvider.AssertConfigurationIsValid();

app.Run();
