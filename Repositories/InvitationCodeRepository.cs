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
        // Every condition below is load-bearing, and the update is the test: matching and stamping in
        // one statement is what makes redemption atomic, so two people racing the same code produce
        // one winner rather than two reads that both saw it unredeemed. A second, unguarded update
        // over the same rows would undo all of that - it would stamp the code whatever the guards
        // said, burning it on failed attempts and on attempts by the wrong person.
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

    /// <inheritdoc />
    public Task<bool> IsRedeemableAsync(string code, string? email) =>
        // Exactly the WHERE clause TryRedeemAsync uses, asked as a question instead of an update.
        // Written out rather than shared with it deliberately: the value of the redemption being a
        // single statement is that nothing can come between matching and stamping, and factoring the
        // predicate into something both call is the first step towards someone reusing it as a
        // read-then-write.
        _dbSet.AnyAsync(c => c.Code.Replace("-", "") == code
            && c.IsActive
            && c.RedeemedAtUtc == null
            && (c.BoundEmail == null || c.BoundEmail == email));
}
