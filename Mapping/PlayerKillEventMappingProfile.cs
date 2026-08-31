// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="PlayerKillEvent"/> - a plain <see cref="Profile"/>, not
/// JumpStart's <c>EntityMappingProfile{TEntity,TDto,TCreateDto,TUpdateDto}</c> base, since this entity
/// is append-only and has no create/update DTO for that base to require.
/// </summary>
public class PlayerKillEventMappingProfile : Profile
{
    public PlayerKillEventMappingProfile()
    {
        CreateMap<PlayerKillEvent, PlayerKillEventDto>();
    }
}
