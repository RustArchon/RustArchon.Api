// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>Repository for <see cref="Queue"/> - the buckets a <see cref="Ticket"/> is routed into.</summary>
public interface IQueueRepository : IRepository<Queue>
{
    /// <summary>Every active queue, in display order - what every queue picker (staff, tenant, and the
    /// anonymous contact form) shows.</summary>
    Task<IReadOnlyList<Queue>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>A queue by its stable <see cref="Queue.Slug"/>, or <c>null</c> if none matches.</summary>
    Task<Queue?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
}
