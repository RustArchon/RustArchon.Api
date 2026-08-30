// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository contract for <see cref="InvitationCode"/> entities.
/// </summary>
public interface IInvitationCodeRepository : IRepository<InvitationCode>
{
    /// <summary>
    /// Atomically redeems a code, if and only if it is currently active, unredeemed, and (when bound
    /// to an email) matches <paramref name="email"/>.
    /// </summary>
    /// <param name="code">The code as submitted by the caller (matched exactly - the caller is
    /// responsible for trimming whitespace before calling).</param>
    /// <param name="email">The redeeming user's email, lower-cased/trimmed. Recorded on success
    /// regardless of whether the code was bound to an email or open to anyone.</param>
    /// <returns>
    /// <c>true</c> if this call redeemed the code; <c>false</c> if it doesn't exist, is inactive,
    /// already redeemed, or bound to a different email.
    /// </returns>
    /// <remarks>
    /// Implemented as a single <c>UPDATE ... WHERE RedeemedAtUtc IS NULL</c> statement (see
    /// <c>InvitationCodeRepository</c>) rather than a read-then-write - that's what makes this safe
    /// against two concurrent redemption attempts for the same code without any explicit locking:
    /// only one UPDATE can ever affect a row, because the second one's WHERE clause no longer matches
    /// once the first commits.
    /// </remarks>
    Task<bool> TryRedeemAsync(string code, string? email);
}
