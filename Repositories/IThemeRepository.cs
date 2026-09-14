// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository for <see cref="Theme"/> - the platform-wide theme catalog. Not tenant-scoped, so unlike
/// <see cref="ICommunicationRepository"/>/<see cref="INoteRepository"/> there's no "across tenants"
/// variant needed - every caller already sees every row.
/// </summary>
public interface IThemeRepository : IRepository<Theme>
{
    /// <summary>Every theme, newest first - the admin list's full contents (expected to stay small,
    /// same reasoning as <c>PlatformSettingsController.GetAll</c>'s own unpaginated list).</summary>
    Task<IReadOnlyList<Theme>> GetAllOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>The currently-active theme, or <c>null</c> if none is (the platform default look
    /// applies).</summary>
    Task<Theme?> GetActiveAsync(CancellationToken cancellationToken = default);
}
