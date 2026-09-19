// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Admin management of <see cref="TicketStatus"/> - the states a ticket can be in, letting a
/// self-hoster shape the lifecycle to match their own workflow rather than being stuck with a fixed
/// enum. Reuses <see cref="PermissionCatalog.PlatformManageOrganizations"/> rather than declaring its
/// own permission, the same reasoning <see cref="AdminTicketsController"/> already gives: one more
/// capability on the same admin surface, not a separate one.
/// </summary>
/// <remarks>
/// A protected status (<see cref="TicketStatus.IsProtected"/>) is one of the seven <c>TicketStatusSeeder</c>
/// creates - most looked up by <see cref="TicketStatus.Slug"/> from <see cref="TicketsController"/>,
/// <see cref="AdminTicketsController"/>, or <see cref="TicketSubmissionController"/> to drive the ticket
/// lifecycle, plus <c>Cancelled</c>, which nothing looks up automatically but is permanent by design -
/// see <c>TicketStatusSeeder</c>'s own remarks. A protected status can still be renamed or have
/// <see cref="TicketStatus.IsClosed"/> flipped - that flexibility is the entire point of this being an
/// entity instead of an enum - but <see cref="Delete"/> and deactivating via <see cref="Update"/> both
/// refuse it outright.
/// </remarks>
[ApiController]
[Route("api/admin/ticket-statuses")]
[Authorize]
[RequirePermission(PermissionCatalog.PlatformManageOrganizations)]
public class AdminTicketStatusesController(ITicketStatusRepository statuses) : ControllerBase
{
    /// <summary>Every status, active or not, for the management page.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TicketStatusDto>>> List(CancellationToken cancellationToken)
    {
        var found = await statuses.GetAllOrderedAsync(cancellationToken);
        return Ok(found.Select(TicketStatusMapper.ToDto).ToList());
    }

    /// <summary>One status, by id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketStatusDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var status = await statuses.GetByIdAsync(id, includes: null);
        return status is null ? NotFound() : Ok(TicketStatusMapper.ToDto(status));
    }

    /// <summary>Adds a new, non-protected status.</summary>
    [HttpPost]
    public async Task<ActionResult<TicketStatusDto>> Create(
        [FromBody] CreateTicketStatusRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            return BadRequest("Enter a name.");
        }

        var status = new TicketStatus
        {
            Slug = await GenerateUniqueSlugAsync(name, cancellationToken),
            Name = name,
            IsClosed = request.IsClosed,
            IsProtected = false,
            IsActive = true,
            DisplayOrder = request.DisplayOrder,
            CreatedById = Guid.Empty
        };

        await statuses.AddAsync(status);

        return Ok(TicketStatusMapper.ToDto(status));
    }

    /// <summary>Renames a status, or changes whether it's closed, its order, or whether it's active.
    /// Refuses to deactivate a protected status - see this controller's remarks.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TicketStatusDto>> Update(
        Guid id, [FromBody] UpdateTicketStatusRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var status = await statuses.GetByIdAsync(id, includes: null);
        if (status is null)
        {
            return NotFound();
        }

        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            return BadRequest("Enter a name.");
        }

        if (status.IsProtected && !request.IsActive)
        {
            return BadRequest("This status is required by the ticket lifecycle and can't be deactivated.");
        }

        status.Name = name;
        status.IsClosed = request.IsClosed;
        status.DisplayOrder = request.DisplayOrder;
        status.IsActive = request.IsActive;

        await statuses.UpdateAsync(status);

        return Ok(TicketStatusMapper.ToDto(status));
    }

    /// <summary>Removes a status. Refuses a protected one, and refuses one any ticket still points at -
    /// see this controller's remarks.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var status = await statuses.GetByIdAsync(id, includes: null);
        if (status is null)
        {
            return NotFound();
        }

        if (status.IsProtected)
        {
            return BadRequest("This status is required by the ticket lifecycle and can't be deleted.");
        }

        if (await statuses.IsInUseAsync(id, cancellationToken))
        {
            return BadRequest("This status is still assigned to at least one ticket.");
        }

        await statuses.DeleteAsync(id);

        return NoContent();
    }

    /// <summary>Slugifies <paramref name="name"/> and appends a numeric suffix if that slug is already
    /// taken - the same "stable key derived from a display name, disambiguated on collision" shape as
    /// any other admin-created slug in this codebase.</summary>
    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(name);
        var candidate = baseSlug;
        var suffix = 2;

        while (await statuses.GetBySlugAsync(candidate, cancellationToken) is not null)
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasDash = false;

        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        if (lastWasDash && builder.Length > 0)
        {
            builder.Length--;
        }

        var slug = builder.ToString();
        return slug.Length == 0 ? Guid.NewGuid().ToString("N") : slug;
    }
}
