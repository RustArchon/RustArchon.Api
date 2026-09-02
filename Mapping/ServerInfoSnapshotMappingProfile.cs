// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="ServerInfoSnapshot"/> - a plain <see cref="Profile"/>, not
/// JumpStart's <c>EntityMappingProfile{TEntity,TDto,TCreateDto,TUpdateDto}</c> base, since this entity
/// has no create/update DTO for that base to require (snapshots are captured by the system, not
/// created or edited by a caller) - same reasoning as <see cref="PlayerSessionMappingProfile"/>.
/// </summary>
public class ServerInfoSnapshotMappingProfile : Profile
{
    public ServerInfoSnapshotMappingProfile()
    {
        CreateMap<ServerInfoSnapshot, ServerInfoSnapshotDto>();
    }
}
