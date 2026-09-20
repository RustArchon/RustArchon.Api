// Copyright ©2026 Scott Blomfield

using AutoMapper;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="ServerPluginStatus"/> - a plain <see cref="Profile"/> for the same reason as
/// <see cref="ServerPluginMappingProfile"/>: rows are captured by the system, so there is no create/update DTO.
/// </summary>
public class ServerPluginStatusMappingProfile : Profile
{
    public ServerPluginStatusMappingProfile()
    {
        // The entity names these Reported*; the DTO drops the prefix because it only ever carries reported values.
        CreateMap<ServerPluginStatus, ServerPluginStatusDto>()
            .ForMember(dest => dest.RecordingEnabled, opt => opt.MapFrom(src => src.ReportedRecordingEnabled))
            .ForMember(dest => dest.CombatLogEnabled, opt => opt.MapFrom(src => src.ReportedCombatLogEnabled))
            // Not on the entity: it compares the plugin's key with THIS Panel's, which only the controller can look up.
            .ForMember(dest => dest.SigningKeyMatchesThisPanel, opt => opt.Ignore())
            .ForMember(dest => dest.SigningKeyState, opt => opt.Ignore())
            // Likewise filled in by the controller: what this Panel would install, and whether the Updater is there.
            .ForMember(dest => dest.LatestPluginVersion, opt => opt.Ignore())
            .ForMember(dest => dest.UpdateAvailable, opt => opt.Ignore())
            .ForMember(dest => dest.UpdaterInstalled, opt => opt.Ignore())
            .ForMember(dest => dest.UpdaterVersion, opt => opt.Ignore())
            .ForMember(dest => dest.LatestUpdaterVersion, opt => opt.Ignore())
            .ForMember(dest => dest.UpdaterUpdateAvailable, opt => opt.Ignore());
    }
}
