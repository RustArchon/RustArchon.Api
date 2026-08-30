// Copyright ©2026 Scott Blomfield

using System;
using Microsoft.AspNetCore.DataProtection;

namespace RustArchon.Api.Infrastructure.Security;

/// <summary>
/// <see cref="IRconCredentialProtector"/> implementation backed by ASP.NET Core Data Protection.
/// </summary>
/// <remarks>
/// Uses a dedicated purpose string so this protector's keys can never be reused to decrypt data
/// protected for an unrelated purpose elsewhere in the application. In a multi-instance production
/// deployment, the Data Protection key ring must be persisted to a shared, durable location (a
/// database, blob storage, etc.) rather than the default per-machine profile - see
/// <c>Program.cs</c>'s <c>AddDataProtection</c> call and the README.
/// </remarks>
public class RconCredentialProtector : IRconCredentialProtector
{
    private const string Purpose = "RustArchon.RustServer.RconPassword.v1";

    private readonly IDataProtector _protector;

    public RconCredentialProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string Protect(string plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(plaintextPassword);
        return _protector.Protect(plaintextPassword);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedPassword)
    {
        ArgumentNullException.ThrowIfNull(protectedPassword);
        return _protector.Unprotect(protectedPassword);
    }
}
