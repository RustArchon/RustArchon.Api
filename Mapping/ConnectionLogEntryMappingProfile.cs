// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="ConnectionLogEntry"/> - a plain <see cref="Profile"/>, not
/// JumpStart's <c>EntityMappingProfile{TEntity,TDto,TCreateDto,TUpdateDto}</c> base, for the same
/// reason as <see cref="ServerInfoSnapshotMappingProfile"/>: entries are captured by the system, not
/// created or edited by a caller, so there's no create/update DTO for that base to require.
/// </summary>
public class ConnectionLogEntryMappingProfile : Profile
{
    public ConnectionLogEntryMappingProfile()
    {
        CreateMap<ConnectionLogEntry, ConnectionLogEntryDto>();
    }
}
