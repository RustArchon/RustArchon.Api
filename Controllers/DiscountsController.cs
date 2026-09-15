// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of the discount catalog, and redeeming a code on a specific Organization's
/// behalf.
/// </summary>
/// <remarks>
/// Gated by <c>ManageDiscounts</c>, its own permission - see <see cref="Infrastructure.SiteAdminRoleSeeder.ManageDiscountsPermission"/>'s
/// own remarks for why this is separate from <c>ManageBilling</c>/<c>ManagePlans</c>.
/// </remarks>
[ApiController]
[Route("api/discounts")]
[Authorize(Policy = "ManageDiscounts")]
public class DiscountsController(IDiscountService discountService) : ControllerBase
{
    /// <summary>Every discount ever created, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<List<DiscountDto>>> GetAll(CancellationToken cancellationToken)
    {
        var discounts = await discountService.GetAllAsync(cancellationToken);
        return Ok(discounts.Select(ToDto).ToList());
    }

    /// <summary>Creates a new discount code.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateDiscountRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            await discountService.CreateAsync(
                request.Code, request.AmountType, request.AmountValue, request.Description,
                request.MaxRedemptions, request.OncePerOrganization, request.RestrictedToTenantId,
                request.ExpiresOn, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }

    /// <summary>Turns a discount on or off - see <see cref="Discount.IsActive"/>.</summary>
    [HttpPost("{id:guid}/set-active")]
    public async Task<IActionResult> SetActive(
        Guid id, [FromQuery] bool isActive, CancellationToken cancellationToken) =>
        await discountService.SetActiveAsync(id, isActive, cancellationToken) ? NoContent() : NotFound();

    /// <summary>Redeems a code on a specific Organization's behalf - the admin equivalent of a customer
    /// entering a code themselves (see <c>SubscriptionController.RedeemDiscount</c>), same validation.</summary>
    [HttpPost("redeem")]
    public async Task<ActionResult<DiscountRedemptionResultDto>> Redeem(
        [FromBody] AdminRedeemDiscountRequestDto request, CancellationToken cancellationToken)
    {
        var result = await discountService.RedeemAsync(request.TenantId, request.Code, cancellationToken);
        return Ok(new DiscountRedemptionResultDto { Success = result.Success, ErrorMessage = result.ErrorMessage });
    }

    private static DiscountDto ToDto(Discount discount) => new()
    {
        Id = discount.Id,
        Code = discount.Code,
        Description = discount.Description,
        AmountType = discount.AmountType,
        AmountValue = discount.AmountValue,
        Frequency = discount.Frequency,
        MaxRedemptions = discount.MaxRedemptions,
        TimesRedeemed = discount.TimesRedeemed,
        OncePerOrganization = discount.OncePerOrganization,
        RestrictedToTenantId = discount.RestrictedToTenantId,
        RestrictedToOrganizationName = discount.RestrictedToTenant?.Name,
        ExpiresOn = discount.ExpiresOn,
        IsActive = discount.IsActive,
        CreatedOn = discount.CreatedOn
    };
}
