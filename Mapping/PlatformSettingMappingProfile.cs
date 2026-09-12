// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="PlatformSetting"/> - a plain <see cref="Profile"/>, not
/// JumpStart's <c>EntityMappingProfile{TEntity,TDto,TCreateDto,TUpdateDto}</c> base, since there is no
/// create DTO for this entity (see <see cref="PlatformSettingDto"/>'s remarks) and its update path
/// only ever touches <see cref="PlatformSetting.Value"/> directly, not through AutoMapper.
/// </summary>
public class PlatformSettingMappingProfile : Profile
{
    public PlatformSettingMappingProfile()
    {
        // Api-side and DTO-side PlatformSettingValueType are separate enums with matching member
        // names (DTOs never reference API entity types directly) - AutoMapper maps enums by member
        // name by default, so this Just Works with no explicit member-by-member configuration.
        CreateMap<PlatformSetting, PlatformSettingDto>()
            // Computed by PlatformSettingsController.ToDto after this map runs, not from any entity
            // property - a Secret setting's real value never reaches this map in the first place.
            .ForMember(dto => dto.HasValue, opt => opt.Ignore());
    }
}
