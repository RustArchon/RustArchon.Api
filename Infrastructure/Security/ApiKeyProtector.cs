// Copyright ©2026 Scott Blomfield

using System;
using Microsoft.AspNetCore.DataProtection;

namespace RustArchon.Api.Infrastructure.Security;

/// <summary>
/// <see cref="IApiKeyProtector"/> implementation backed by ASP.NET Core Data Protection - same
/// approach as <see cref="RconCredentialProtector"/>, parameterized by purpose instead of fixed to one.
/// </summary>
public class ApiKeyProtector : IApiKeyProtector
{
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public ApiKeyProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider));
    }

    /// <inheritdoc />
    public string Protect(string purpose, string plaintextKey)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(plaintextKey);
        return _dataProtectionProvider.CreateProtector(purpose).Protect(plaintextKey);
    }

    /// <inheritdoc />
    public string Unprotect(string purpose, string protectedKey)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(protectedKey);
        return _dataProtectionProvider.CreateProtector(purpose).Unprotect(protectedKey);
    }
}
