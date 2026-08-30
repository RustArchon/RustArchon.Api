// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="InvitationCode"/> entities.
/// </summary>
public class InvitationCodeRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<InvitationCode>(context, userContext), IInvitationCodeRepository
{
    /// <inheritdoc />
    public async Task<bool> TryRedeemAsync(string code, string? email)
    {
        // The caller (InvitationsController) already strips whitespace/dashes and upper-cases; do the
        // same to the stored, dash-formatted Code (e.g. "ABCD-1234-WXYZ") so the comparison matches
        // regardless of how the code was originally generated/displayed.
        var rows = await _dbSet
            .Where(c => c.Code.Replace("-", "") == code
                && c.IsActive
                && c.RedeemedAtUtc == null
                && (c.BoundEmail == null || c.BoundEmail == email))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.RedeemedAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(c => c.RedeemedByEmail, email));

        return rows == 1;
    }
}
