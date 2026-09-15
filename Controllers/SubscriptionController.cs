// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RustArchon.Api.Billing;
using JumpStart.Authorization;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// A tenant's view of, and control over, its own subscription: what plan and term it's on, what a
/// change would do, making that change, and calling off one that hasn't happened yet.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to the caller's own Organization throughout - the tenant id comes from
/// <see cref="ITenantContext"/>, never from the request, so there is no way to quote or change another
/// Organization's subscription. Distinct from <see cref="PlansController"/>, which is the platform
/// admin's catalog management and is gated by <c>ManagePlans</c>.
/// </para>
/// <para>
/// <strong>Preview before commit.</strong> <see cref="Quote"/> and <see cref="Change"/> take the same
/// request body and run the same code path; the only difference is that one persists. Clients are
/// expected to show the quote and have the user accept it before calling <see cref="Change"/>, because
/// a change can include a part that only takes effect months later (see
/// <see cref="PlanChangeQuoteDto"/>).
/// </para>
/// <para>
/// <strong>Known gap:</strong> gated by plain <c>[Authorize]</c>, so any authenticated member of an
/// Organization can change its plan - there's no "billing" permission to require, since JumpStart's
/// role system grants permissions per entity type and a subscription isn't one of the entities it
/// covers. Worth tightening to an Owner-only policy before this handles real money.
/// </para>
/// </remarks>
[ApiController]
[Route("api/subscription")]
[Authorize]
public class SubscriptionController(
    ISubscriptionService subscriptionService,
    IStripeCheckoutService checkoutService,
    IOptions<StripeOptions> stripeOptions,
    IDiscountService discountService,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>The caller's current subscription, including any change already scheduled.</summary>
    [RequirePermission(PermissionCatalog.SubscriptionView)]
    [HttpGet]
    public async Task<ActionResult<SubscriptionDto>> Get(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var subscription = await subscriptionService.GetAsync(tenantId, cancellationToken);
        return subscription is null ? NotFound() : Ok(subscription);
    }

    /// <summary>
    /// Every billing period the caller has been charged for, newest first - including the prorated
    /// partial periods a mid-period change creates.
    /// </summary>
    [RequirePermission(PermissionCatalog.SubscriptionView)]
    [HttpGet("billing-history")]
    public async Task<ActionResult<IReadOnlyList<BillingHistoryEntryDto>>> GetBillingHistory(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await subscriptionService.GetBillingHistoryAsync(tenantId, cancellationToken));
    }

    /// <summary>
    /// Starts a Stripe-hosted checkout for one of the caller's own open invoices - see
    /// <see cref="IStripeCheckoutService"/>'s remarks. <see cref="PermissionCatalog.SubscriptionManage"/>,
    /// not <c>SubscriptionView</c> - paying money is an action, not a read.
    /// </summary>
    /// <returns>The Stripe-hosted URL to redirect the browser to.</returns>
    [RequirePermission(PermissionCatalog.SubscriptionManage)]
    [HttpPost("invoices/{invoiceId:guid}/checkout-session")]
    public async Task<ActionResult<string>> CreateCheckoutSession(Guid invoiceId, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var panelBaseUrl = stripeOptions.Value.PanelBaseUrl.TrimEnd('/');
        var url = await checkoutService.CreateCheckoutSessionAsync(
            tenantId, invoiceId,
            successUrl: $"{panelBaseUrl}/Account/BillingHistory?paid=1",
            cancelUrl: $"{panelBaseUrl}/Account/BillingHistory",
            cancellationToken);

        return url is null ? BadRequest("That invoice can't be paid right now.") : Ok(url);
    }

    /// <summary>
    /// The plans the caller may move to, cheapest first, each flagged with whether it's their current
    /// one and whether their server count would fit inside it.
    /// </summary>
    [RequirePermission(PermissionCatalog.SubscriptionView)]
    [HttpGet("plan-options")]
    public async Task<ActionResult<IReadOnlyList<PlanOptionDto>>> GetPlanOptions(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await subscriptionService.GetPlanOptionsAsync(tenantId, cancellationToken));
    }

    /// <summary>
    /// What moving to the requested plan/term would do - what changes today, what changes later, what
    /// it costs, and whether it's permitted. Changes nothing.
    /// </summary>
    [RequirePermission(PermissionCatalog.SubscriptionView)]
    [HttpPost("quote")]
    public async Task<ActionResult<PlanChangeQuoteDto>> Quote(
        [FromBody] ChangePlanRequestDto request, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return Ok(await subscriptionService.QuoteAsync(
            tenantId, request.PlanId, request.TermMonths, request.Quantity, cancellationToken));
    }

    /// <summary>
    /// Applies the change and returns the same quote describing exactly what was done.
    /// </summary>
    /// <remarks>
    /// A change the rules don't permit comes back as a 400 carrying only
    /// <see cref="PlanChangeQuoteDto.BlockedReason"/> - already a plain-English sentence written for
    /// the user, so the Panel can surface it directly rather than inventing its own wording (see
    /// <c>ApiErrorMessage</c>).
    /// </remarks>
    [RequirePermission(PermissionCatalog.SubscriptionManage)]
    [HttpPost("change")]
    public async Task<ActionResult<PlanChangeQuoteDto>> Change(
        [FromBody] ChangePlanRequestDto request, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var quote = await subscriptionService.ApplyAsync(
            tenantId, request.PlanId, request.TermMonths, request.Quantity, cancellationToken);

        return quote.Allowed ? Ok(quote) : BadRequest(quote.BlockedReason);
    }

    /// <summary>
    /// Redeems a discount code against the caller's own Organization - applied to whichever invoice is
    /// issued next (renewal or plan change), see <see cref="IDiscountService.RedeemAsync"/>. Always a
    /// 200 carrying <see cref="DiscountRedemptionResultDto.Success"/>, never a 4xx for a code that
    /// simply doesn't work - that's an ordinary outcome the Panel shows directly, not an error.
    /// </summary>
    [RequirePermission(PermissionCatalog.SubscriptionManage)]
    [HttpPost("discounts/redeem")]
    public async Task<ActionResult<DiscountRedemptionResultDto>> RedeemDiscount(
        [FromBody] RedeemDiscountRequestDto request, CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        var result = await discountService.RedeemAsync(tenantId, request.Code, cancellationToken);
        return Ok(new DiscountRedemptionResultDto { Success = result.Success, ErrorMessage = result.ErrorMessage });
    }

    /// <summary>
    /// Calls off the pending scheduled change, leaving the current subscription to run on unchanged.
    /// </summary>
    [RequirePermission(PermissionCatalog.SubscriptionManage)]
    [HttpDelete("scheduled-change")]
    public async Task<IActionResult> CancelScheduledChange(CancellationToken cancellationToken)
    {
        if (await tenantContext.GetCurrentTenantIdAsync() is not { } tenantId)
        {
            return Forbid();
        }

        return await subscriptionService.CancelScheduledChangeAsync(tenantId, cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
