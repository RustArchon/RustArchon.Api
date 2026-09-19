// Copyright ©2026 Scott Blomfield

using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Maps a <see cref="TicketStatus"/> to its <see cref="TicketStatusDto"/> - shared by every controller
/// that renders a ticket (<c>TicketsController</c>, <c>AdminTicketsController</c>,
/// <c>GuestTicketController</c>), since all three need the identical mapping.
/// </summary>
public static class TicketStatusMapper
{
    public static TicketStatusDto ToDto(TicketStatus status) => new()
    {
        Id = status.Id,
        Name = status.Name,
        Slug = status.Slug,
        IsClosed = status.IsClosed,
        IsProtected = status.IsProtected,
        IsActive = status.IsActive,
        DisplayOrder = status.DisplayOrder
    };
}
