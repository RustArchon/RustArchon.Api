// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="PlayerSession"/> - a plain <see cref="Profile"/>, not JumpStart's
/// <c>EntityMappingProfile{TEntity,TDto,TCreateDto,TUpdateDto}</c> base, since this entity has no
/// create/update DTO for that base to require (sessions are opened/closed by the system, not created
/// or edited by a caller).
/// </summary>
public class PlayerSessionMappingProfile : Profile
{
    public PlayerSessionMappingProfile()
    {
        CreateMap<PlayerSession, PlayerSessionDto>();
    }
}
