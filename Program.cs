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
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Authentication;
using RustArchon.Api.Infrastructure.Security;
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
// 4b. INVITATION-GATED SIGN-UP
// ============================================
// Read directly from the flat RUSTARCHON_INVITATION_CODES_ENABLED key - the same name used in .env
// and docker-compose.yml, no Section:Key rename in between. See InvitationCodeOptions's remarks.
builder.Services.Configure<InvitationCodeOptions>(options =>
    options.Enabled = builder.Configuration.GetValue("RUSTARCHON_INVITATION_CODES_ENABLED", true));

// ============================================
// 4c. MESSAGING (RABBITMQ)
// ============================================
// Publish-only for now (InternalController's email endpoint is the only publisher) - no consumers
// registered here yet. The RCON pipeline's own Api-side consumers (RconFrameIngestionConsumer,
// ServerConnectionHeartbeatConsumer, ...) land in this same AddMassTransit call once that work
// resumes; ConfigureEndpoints below is a no-op until then.
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
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);
        });

        cfg.ConfigureEndpoints(context);
    });
});

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

// Same flat-key approach as InvitationCodeOptions above - RUSTARCHON_INTERNAL_API_KEY reaches this
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
    })
    // Service-to-service calls (e.g. the Blazor web app's QueuedEmailSender) - a separate scheme from
    // the JWT bearer above, opted into per-endpoint via [Authorize(AuthenticationSchemes = "InternalApiKey")].
    // See InternalApiKeyAuthenticationHandler's remarks.
    .AddScheme<AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>("InternalApiKey", null);

// ============================================
// 6. AUTHORIZATION
// ============================================
// PlatformAdmin is a single admin email read directly from RUSTARCHON_ADMIN_EMAIL, not a JumpStart
// Permission claim - granting this through a tenant's own Role would mean the normal sign-up
// bootstrap flow could accidentally hand every new user admin rights over sign-up itself, which is
// why this is a separate, config-only check instead. Read once here and reused below by
// AdminInvitationSeeder, rather than each reading the config key independently.
var adminEmail = builder.Configuration["RUSTARCHON_ADMIN_EMAIL"];

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("PlatformAdmin", policy =>
        policy.RequireAssertion(context =>
        {
            var email = context.User.Identity?.Name;
            return !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(adminEmail)
                && string.Equals(email, adminEmail, StringComparison.OrdinalIgnoreCase);
        }));
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

// Fail fast on startup if any AutoMapper profile is misconfigured.
var mapper = app.Services.GetRequiredService<IMapper>();
mapper.ConfigurationProvider.AssertConfigurationIsValid();

app.Run();
