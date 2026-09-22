// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="ServerReport"/> and its notes - a plain <see cref="Profile"/>: reports are created only by
/// ingestion and changed only by a status or assignment action, so there is no create/update DTO for JumpStart's entity profile
/// base to require. The raw
/// payloads and the picture's storage key are deliberately not mapped out.
/// </summary>
public class ServerReportMappingProfile : Profile
{
    public ServerReportMappingProfile()
    {
        CreateMap<ServerReport, ServerReportDto>()
            .ForMember(d => d.HasScreenshot, o => o.MapFrom(s => s.ScreenshotObjectKey != null));

        // The author is whoever was signed in when the note was added - the audit column, not a second copy of it.
        CreateMap<ServerReportNote, ServerReportNoteDto>()
            .ForMember(d => d.AuthorUserId, o => o.MapFrom(s => s.CreatedById));
    }
}
