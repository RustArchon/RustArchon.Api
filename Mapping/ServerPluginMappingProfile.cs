// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="ServerPlugin"/> - a plain <see cref="Profile"/> for the same reason
/// as <see cref="ServerInfoSnapshotMappingProfile"/>: rows are captured by the system, so there is no
/// create/update DTO for JumpStart's <c>EntityMappingProfile</c> base to require.
/// </summary>
public class ServerPluginMappingProfile : Profile
{
    public ServerPluginMappingProfile()
    {
        CreateMap<ServerPlugin, ServerPluginDto>();
    }
}
