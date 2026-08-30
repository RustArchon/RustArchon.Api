// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Anonymous endpoints used by the Register page, called before any account/tenant exists - there is
/// no user to authenticate this call with yet, unlike everything protected by <c>[EntityAuthorize]</c>
/// or the <c>PlatformAdmin</c> policy. See <see cref="InvitationCodesController"/> for the
/// platform-admin side (minting/deactivating codes).
/// </summary>
/// <remarks>
/// Redemption is deliberately the last gate in <c>Register.razor</c>'s flow, after the Identity user
/// already exists - see its remarks for why, and for the rollback (<c>UserManager.DeleteAsync</c>) it
/// performs if <see cref="Redeem"/> reports failure.
/// </remarks>
[ApiController]
[Route("api/invitations")]
[AllowAnonymous]
public class InvitationsController : ControllerBase
{
    private readonly IInvitationCodeRepository _repository;
    private readonly IOptions<InvitationCodeOptions> _options;

    public InvitationsController(IInvitationCodeRepository repository, IOptions<InvitationCodeOptions> options)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Reports whether registration currently requires an invitation code, so the Register page can
    /// decide whether to show/require the field at all - see <see cref="InvitationCodeOptions"/>.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<InvitationStatusDto> GetStatus()
    {
        return Ok(new InvitationStatusDto { Enabled = _options.Value.Enabled });
    }

    /// <summary>
    /// Attempts to redeem an invitation code. Always returns 200 - a rejected code is a normal,
    /// expected outcome here, not a server error - with <see cref="RedeemInvitationCodeResult.Success"/>
    /// telling the caller whether it actually worked.
    /// </summary>
    [HttpPost("redeem")]
    public async Task<ActionResult<RedeemInvitationCodeResult>> Redeem([FromBody] RedeemInvitationCodeRequest request)
    {
        if (!_options.Value.Enabled)
        {
            return Ok(new RedeemInvitationCodeResult { Success = true });
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Ok(new RedeemInvitationCodeResult { Success = false, Error = "An invitation code is required." });
        }

        var email = request.Email.Trim().ToLowerInvariant();

        // Strip whitespace/dashes so "ABCD-1234-WXYZ", "abcd1234wxyz", and "ABCD 1234 WXYZ" (however
        // someone retypes a code by hand) all match the same stored, dash-formatted code.
        var code = new string(request.Code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        var redeemed = await _repository.TryRedeemAsync(code, email);

        return Ok(new RedeemInvitationCodeResult
        {
            Success = redeemed,
            Error = redeemed
                ? null
                : "That invitation code is invalid, already used, or not valid for this email address."
        });
    }
}
