// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Endpoints for RustArchon's own other processes to call, authenticated by shared secret (see
/// <see cref="Infrastructure.Authentication.InternalApiKeyAuthenticationHandler"/>) rather than a
/// user/tenant JWT - there's no end user involved in any of these calls.
/// </summary>
/// <remarks>
/// Not published to the Docker host (see docker-compose.yml's comment on <c>rustarchon-api</c>) -
/// reachable only from other containers on the compose network.
/// </remarks>
[ApiController]
[Route("internal")]
[Authorize(AuthenticationSchemes = "InternalApiKey")]
public class InternalController : ControllerBase
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IRustServerRepository _rustServerRepository;
    private readonly IRconCredentialProtector _rconCredentialProtector;
    private readonly IApiKeyProtector _apiKeyProtector;
    private readonly IPlatformSettingRepository _platformSettingRepository;
    private readonly ICommunicationPublisher _communicationPublisher;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IOrganizationProvisioningService _provisioning;
    private readonly ApiDbContext _dbContext;
    private readonly Infrastructure.ObjectStorage.IObjectStorage _objectStorage;
    private readonly IPaymentService _paymentService;

    public InternalController(
        IPublishEndpoint publishEndpoint,
        IRustServerRepository rustServerRepository,
        IRconCredentialProtector rconCredentialProtector,
        IApiKeyProtector apiKeyProtector,
        IPlatformSettingRepository platformSettingRepository,
        ICommunicationPublisher communicationPublisher,
        ICommunicationRepository communicationRepository,
        IOrganizationProvisioningService provisioning,
        ApiDbContext dbContext,
        Infrastructure.ObjectStorage.IObjectStorage objectStorage,
        IPaymentService paymentService)
    {
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _rustServerRepository = rustServerRepository ?? throw new ArgumentNullException(nameof(rustServerRepository));
        _rconCredentialProtector = rconCredentialProtector ?? throw new ArgumentNullException(nameof(rconCredentialProtector));
        _apiKeyProtector = apiKeyProtector ?? throw new ArgumentNullException(nameof(apiKeyProtector));
        _platformSettingRepository = platformSettingRepository ?? throw new ArgumentNullException(nameof(platformSettingRepository));
        _communicationPublisher = communicationPublisher ?? throw new ArgumentNullException(nameof(communicationPublisher));
        _communicationRepository = communicationRepository ?? throw new ArgumentNullException(nameof(communicationRepository));
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _objectStorage = objectStorage ?? throw new ArgumentNullException(nameof(objectStorage));
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
    }

    /// <summary>
    /// Queues an email for delivery. Called by the Blazor web app's <c>QueuedEmailSender</c> - it has
    /// no direct broker access itself (see <see cref="RustArchon.Messaging"/>'s own remarks: messaging
    /// infrastructure is Api/Worker only), so this is the seam it goes through instead. Returns as
    /// soon as the message is durably published, not once the email is actually sent - see
    /// <see cref="EmailRequested"/>'s remarks and <c>EmailRequestedConsumer</c> in
    /// <c>RustArchon.Worker</c> for where the actual send happens.
    /// </summary>
    [HttpPost("email")]
    public async Task<IActionResult> SendEmail([FromBody] SendEmailRequestDto request)
    {
        await _communicationPublisher.QueueAsync(
            request.To, request.UserId, request.TenantId, request.Subject, request.HtmlBody);
        return Accepted();
    }

    /// <summary>
    /// Queues an email built from an admin-editable <see cref="Data.EmailTemplate"/> rather than raw
    /// HTML - the templated counterpart to <see cref="SendEmail"/>, used for every account-level
    /// (never organization-level - see <see cref="SendTemplatedEmailRequestDto.UserId"/>'s remarks)
    /// email the Blazor web app's own Identity pages trigger.
    /// </summary>
    [HttpPost("email/templated")]
    public async Task<IActionResult> SendTemplatedEmail([FromBody] SendTemplatedEmailRequestDto request)
    {
        await _communicationPublisher.QueueTemplatedAsync(
            request.TemplateCode, request.Tokens, request.To, request.UserId, tenantId: null, culture: request.Culture);
        return Accepted();
    }

    /// <summary>
    /// Records that a communication's tracking pixel was loaded - called by RustArchon.Panel's
    /// <c>/track/email/{id}.gif</c> route, the one public place a recipient's mail client can actually
    /// reach. A no-op (still 204, never an error) for an id that doesn't exist, is still Queued, or is
    /// already past Sent - see <see cref="IInternalCommunicationApiClient"/>'s Panel-side remarks for
    /// why this can never fail loudly: a broken response here must never become a broken image in
    /// somebody's inbox.
    /// </summary>
    [HttpPost("communications/{id:guid}/viewed")]
    public async Task<IActionResult> MarkCommunicationViewed(Guid id, CancellationToken cancellationToken)
    {
        var communication = await _communicationRepository.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (communication is { Status: Data.CommunicationStatus.Sent })
        {
            communication.Status = Data.CommunicationStatus.Viewed;
            communication.ViewedOn = DateTimeOffset.UtcNow;
            await _communicationRepository.SaveAsync(communication, cancellationToken);
        }

        return NoContent();
    }

    /// <summary>
    /// Discards the empty Organization a half-finished registration left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration provisions the Organization before redeeming the invitation code, so that a
    /// provisioning failure costs nobody a code. The price of that order is the reverse case: the
    /// Organization exists and then redemption loses a race. This clears that up, rather than
    /// leaving an empty Organization for a site admin to find.
    /// </para>
    /// <para>
    /// Internal rather than tenant-scoped because there is no one to authorize it as - the account
    /// that founded the Organization is being deleted in the same breath, and it holds its
    /// permissions inside the very tenant being discarded. Guarded instead by what it will act on:
    /// only an Organization this user founded, with no servers, no invoices, and nobody but them in
    /// it. See <c>IOrganizationProvisioningService.TryDiscardAsync</c>.
    /// </para>
    /// </remarks>
    /// <returns>Whether an Organization was discarded. <c>false</c> is an ordinary answer - there
    /// may be nothing to discard, or it may no longer be empty.</returns>
    [HttpPost("registrations/{userId:guid}/discard-organization")]
    public async Task<ActionResult<bool>> DiscardOrganization(Guid userId, CancellationToken cancellationToken)
    {
        // Resolved from the founder rather than taken as a parameter: the caller is rolling back a
        // registration and knows the account, and letting it name any tenant would make this a
        // delete-any-organization endpoint that happens to have guards.
        var founded = await _dbContext.Set<Tenant>()
            .AcrossAllTenants()
            .Where(t => t.CreatedById == userId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (founded.Count != 1)
        {
            // None to discard, or more than one - in which case this is not the fresh registration
            // it claims to be and guessing which is meant would be worse than doing nothing.
            return Ok(false);
        }

        return Ok(await _provisioning.TryDiscardAsync(founded[0], userId, cancellationToken));
    }


    /// <summary>
    /// Returns a server's connection details, including its decrypted RCON password, for whichever
    /// <c>RustArchon.Worker</c> instance is trying to claim or refresh its connection. Called from
    /// <c>ConnectToServerConsumer</c> - see <see cref="InternalRustServerInfoDto"/>'s remarks for the
    /// exact shape it expects back.
    /// </summary>
    /// <returns>404 if the server has been deleted or disabled since the caller last knew about it -
    /// the consumer treats that the same as "stop trying to connect."</returns>
    [HttpGet("rust-servers/{id:guid}")]
    public async Task<ActionResult<InternalRustServerInfoDto>> GetServer(Guid id)
    {
        var server = await _rustServerRepository.GetByIdAcrossTenantsAsync(id);
        if (server is null)
        {
            return NotFound();
        }

        return new InternalRustServerInfoDto(
            server.Id,
            server.TenantId,
            server.Host,
            server.Port,
            _rconCredentialProtector.Unprotect(server.RconPassword),
            server.AssignedWorkerId,
            server.LastHeartbeatUtc);
    }

    /// <summary>
    /// The platform's current email delivery configuration, secrets decrypted - called by
    /// <c>RustArchon.Worker</c>'s email consumers on every send (real or test) rather than cached
    /// anywhere, since sending an email is rare enough that there's no hot path here to protect. See
    /// <see cref="InternalEmailSettingsDto"/>'s remarks.
    /// </summary>
    [HttpGet("email-settings")]
    public async Task<ActionResult<InternalEmailSettingsDto>> GetEmailSettings()
    {
        var settings = (await _platformSettingRepository.GetAllAsync())
            .ToDictionary(s => s.Key, s => s);

        string Value(string key) => settings.TryGetValue(key, out var s) ? s.Value : string.Empty;

        string Decrypt(string key, string purpose)
        {
            var stored = Value(key);
            return string.IsNullOrEmpty(stored) ? string.Empty : _apiKeyProtector.Unprotect(purpose, stored);
        }

        return new InternalEmailSettingsDto(
            ServiceProvider: Value(PlatformSettingsRegistry.EmailServiceProvider),
            SmtpHost: Value(PlatformSettingsRegistry.EmailSmtpHost),
            SmtpPort: int.TryParse(Value(PlatformSettingsRegistry.EmailSmtpPort), out var port)
                ? port : PlatformSettingsRegistry.DefaultEmailSmtpPort,
            SmtpEnableSsl: !bool.TryParse(Value(PlatformSettingsRegistry.EmailSmtpEnableSsl), out var ssl) || ssl,
            SmtpUsername: Value(PlatformSettingsRegistry.EmailSmtpUsername),
            SmtpPassword: Decrypt(PlatformSettingsRegistry.EmailSmtpPassword, ApiKeyProtectorPurposes.EmailSmtpPassword),
            ApiKey: Decrypt(PlatformSettingsRegistry.EmailApiKey, ApiKeyProtectorPurposes.EmailApiKey),
            DefaultFromAddress: Value(PlatformSettingsRegistry.EmailDefaultFromAddress),
            DefaultFromName: Value(PlatformSettingsRegistry.EmailDefaultFromName));
    }

    /// <summary>
    /// Records a Stripe-confirmed payment against an invoice - called by RustArchon.Panel's own public
    /// Stripe webhook route once it has verified the event's signature and confirmed the Checkout
    /// Session actually completed as paid. This Api never talks to Stripe's inbound webhook directly
    /// (see <see cref="RecordStripePaymentRequestDto"/>'s remarks) - by the time this is called, Stripe
    /// itself is out of the picture and this is exactly a payment being recorded, same as
    /// <c>BillingController.RecordPayment</c>'s manual-entry path, just with a
    /// <see cref="RecordStripePaymentRequestDto.ProviderPaymentId"/> attached for idempotency against
    /// Stripe's at-least-once delivery.
    /// </summary>
    [HttpPost("stripe/payments")]
    public async Task<IActionResult> RecordStripePayment(
        [FromBody] RecordStripePaymentRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            await _paymentService.RecordPaymentAsync(
                request.InvoiceId, request.Amount, PaymentMethod.Card, receivedOn: null,
                reference: request.ProviderPaymentId, providerPaymentId: request.ProviderPaymentId,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The invoice named in a webhook payload no longer exists - can't happen through this
            // codebase's own checkout-session creation, but the Panel's webhook route is handling
            // external input regardless of how the payload was produced. 404, not 500: there is nothing
            // wrong with this Api, and Panel's own logging is where the real diagnosis happens.
            return NotFound();
        }

        return Accepted();
    }

    /// <summary>
    /// Records a Stripe payment-attempt failure - called by RustArchon.Panel's own public Stripe webhook
    /// route on a verified <c>payment_intent.payment_failed</c> event. Never allocates anything and
    /// never touches the invoice named; it exists purely so the decline is visible on the Payment Ledger
    /// report - see <see cref="IPaymentService.RecordFailedPaymentAsync"/>'s own remarks.
    /// </summary>
    [HttpPost("stripe/payments/failed")]
    public async Task<IActionResult> RecordFailedStripePayment(
        [FromBody] RecordFailedStripePaymentRequestDto request, CancellationToken cancellationToken)
    {
        await _paymentService.RecordFailedPaymentAsync(
            request.InvoiceId, request.Amount, PaymentMethod.Card, request.ProviderPaymentId,
            request.ProviderEventId, request.FailureCode, request.FailureMessage, cancellationToken);

        // Unlike RecordStripePayment, a missing invoice and an already-recorded event both come back as
        // null rather than an exception - neither is this Api being asked to do something invalid, both
        // are "there's nothing new to record", so both get the same 202 rather than one of them being a
        // 404 someone has to explain.
        return Accepted();
    }

    /// <summary>
    /// Records a Stripe chargeback - called by RustArchon.Panel's own public Stripe webhook route on a
    /// verified <c>charge.dispute.created</c> event. See
    /// <see cref="IPaymentService.RecordDisputeAsync"/>'s own remarks for why this never touches Stripe's
    /// API itself.
    /// </summary>
    [HttpPost("stripe/disputes")]
    public async Task<IActionResult> RecordStripeDispute(
        [FromBody] RecordStripeDisputeRequestDto request, CancellationToken cancellationToken)
    {
        await _paymentService.RecordDisputeAsync(
            request.ProviderPaymentId, request.DisputeId, request.Reason, request.DueBy, cancellationToken);

        return Accepted();
    }

    /// <summary>
    /// One file from a theme's package, by the same relative path it was uploaded under (e.g.
    /// <c>theme.css</c>, <c>images/hero.png</c>) - called by RustArchon.Panel's own public
    /// <c>/theme-assets/...</c> route, the seam a browser's anonymous, same-origin
    /// <c>&lt;link&gt;</c>/<c>@font-face</c> load actually reaches. Garage's real endpoint/credentials
    /// never leave this Api - same trust boundary every other internal-only capability in this
    /// controller already draws.
    /// </summary>
    /// <returns>404 if the theme or that exact path doesn't exist - indistinguishable from each other,
    /// since neither case is anything a caller can act on differently.</returns>
    [HttpGet("theme-assets/{themeId:guid}/{*path}")]
    public async Task<IActionResult> GetThemeAsset(Guid themeId, string path, CancellationToken cancellationToken)
    {
        var content = await _objectStorage.GetAsync($"themes/{themeId:D}/{path}", cancellationToken);
        return content is null ? NotFound() : File(content.Content, content.ContentType);
    }
}
