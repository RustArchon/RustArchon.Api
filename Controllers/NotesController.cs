// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Security.Claims;
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
/// A site admin's own annotations on Organizations and people.
/// </summary>
/// <remarks>
/// <para>
/// Platform-wide and cross-tenant on purpose, gated the same way <c>OrganizationsController</c> and
/// <c>PlatformUsersController</c> already are - this is one more capability on the same admin
/// surface, not a tenant-facing feature, so it reuses their permission rather than declaring one of
/// its own.
/// </para>
/// <para>
/// <see cref="Note.IsPrivate"/> is enforced here, not left to the query filter: a private note is
/// excluded from every listing that isn't the author's own, and an attempt to edit or delete somebody
/// else's private note is refused outright rather than answered with a listing that quietly omits it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/notes")]
[Authorize]
[RequirePermission(PermissionCatalog.PlatformManageOrganizations)]
public class NotesController(INoteRepository notes) : ControllerBase
{
    /// <summary>Notes about an Organization, a person, or both - at least one is required.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NoteDto>>> List(
        Guid? tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        if (tenantId is null && userId is null)
        {
            return BadRequest("Specify an organization, a person, or both.");
        }

        var found = await notes.GetVisibleAsync(tenantId, userId, CurrentUserId, cancellationToken);
        var currentUserId = CurrentUserId;

        return Ok(found.Select(note => ToDto(note, currentUserId)).ToList());
    }

    /// <summary>Creates a note. Refused if it names neither an Organization nor a person.</summary>
    [HttpPost]
    public async Task<ActionResult<NoteDto>> Create(
        [FromBody] SaveNoteRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TenantId is null && request.UserId is null)
        {
            return BadRequest("A note needs to be about an organization, a person, or both.");
        }

        if (CurrentUserId is not { } userId)
        {
            return Forbid();
        }

        var note = new Note
        {
            Title = request.Title?.Trim(),
            Content = request.Content.Trim(),
            TenantId = request.TenantId,
            UserId = request.UserId,
            IsPrivate = request.IsPrivate
        };

        await notes.AddAsync(note);

        return Ok(ToDto(note, userId));
    }

    /// <summary>Updates a note. Refused for somebody else's private note.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<NoteDto>> Update(
        Guid id, [FromBody] SaveNoteRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TenantId is null && request.UserId is null)
        {
            return BadRequest("A note needs to be about an organization, a person, or both.");
        }

        var note = await notes.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (note is null)
        {
            return NotFound();
        }

        if (note.IsPrivate && note.CreatedById != CurrentUserId)
        {
            return Forbid();
        }

        note.Title = request.Title?.Trim();
        note.Content = request.Content.Trim();
        note.TenantId = request.TenantId;
        note.UserId = request.UserId;
        note.IsPrivate = request.IsPrivate;

        await notes.SaveAsync(note, cancellationToken);

        return Ok(ToDto(note, CurrentUserId));
    }

    /// <summary>Deletes a note. Refused for somebody else's private note.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var note = await notes.GetByIdAcrossTenantsAsync(id, cancellationToken);

        if (note is null)
        {
            return NotFound();
        }

        if (note.IsPrivate && note.CreatedById != CurrentUserId)
        {
            return Forbid();
        }

        await notes.DeleteAcrossTenantsAsync(id, cancellationToken);

        return NoContent();
    }

    private static NoteDto ToDto(Note note, Guid? currentUserId) => new()
    {
        Id = note.Id,
        Title = note.Title,
        Content = note.Content,
        TenantId = note.TenantId,
        UserId = note.UserId,
        IsPrivate = note.IsPrivate,
        CreatedById = note.CreatedById,
        CreatedOn = note.CreatedOn,
        ModifiedOn = note.ModifiedOn,
        CanManage = !note.IsPrivate || note.CreatedById == currentUserId
    };

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
