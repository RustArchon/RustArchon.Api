// Copyright ©2026 Scott Blomfield

using AutoMapper;
using JumpStart.Api.Mapping;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="RustServer"/> mappings.
/// </summary>
/// <remarks>
/// <see cref="RustServer.RconPassword"/> is deliberately excluded from every mapping direction:
/// it never appears on <see cref="RustServerDto"/> (read), and both create/update DTOs carry their
/// plaintext password under the same property name only transiently - see
/// <see cref="Controllers.RustServersController"/>, which encrypts it via
/// <see cref="Infrastructure.Security.IRconCredentialProtector"/> before anything is persisted.
///
/// <see cref="RustServer.TenantId"/>/<see cref="RustServer.Tenant"/> are likewise excluded from the
/// create/update mappings: unlike audit fields (which <see cref="EntityMappingProfile{TEntity,TDto,
/// TCreateDto,TUpdateDto}"/> ignores automatically for any <c>IAuditable</c> entity), tenant fields
/// get no such automatic treatment, so every <c>ITenantScoped</c> entity's own profile must ignore
/// them itself. <c>TenantId</c> is populated by <c>Repository{TEntity}.AddAsync</c> from the ambient
/// tenant context (see JumpStart ADR-010) - never from client input.
/// </remarks>
public class RustServerMappingProfile
    : EntityMappingProfile<RustServer, RustServerDto, CreateRustServerDto, UpdateRustServerDto>
{
    protected override void ConfigureAdditionalMappings(
        IMappingExpression<RustServer, RustServerDto> entityMap,
        IMappingExpression<CreateRustServerDto, RustServer> createMap,
        IMappingExpression<UpdateRustServerDto, RustServer> updateMap)
    {
        // Read side: never map the encrypted key values themselves out - only whether one is set.
        entityMap.ForMember(dest => dest.HasSteamApiKey, opt => opt.MapFrom(src => !string.IsNullOrEmpty(src.SteamApiKey)));
        entityMap.ForMember(dest => dest.HasGeolocationApiKey, opt => opt.MapFrom(src => !string.IsNullOrEmpty(src.GeolocationApiKey)));

        // Update DTO's RconPassword is optional (null/empty means "keep the existing password") and
        // must never overwrite the encrypted value in place - the controller handles it explicitly.
        updateMap.ForMember(dest => dest.RconPassword, opt => opt.Ignore());

        // Same reasoning as RconPassword above, for both new secrets - update's is optional (null/
        // empty means "keep the existing key") and must never overwrite the encrypted value in place.
        // Create's is NOT ignored (matching RconPassword's own create-side treatment): AutoMapper
        // copies the plaintext straight across by name, and the controller then encrypts whatever
        // landed on the entity in place, same shape as OnBeforeCreate already does for RconPassword.
        updateMap.ForMember(dest => dest.SteamApiKey, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.GeolocationApiKey, opt => opt.Ignore());

        // TenantId/Tenant are set by the repository from ambient tenant context, never by the client.
        createMap.ForMember(dest => dest.TenantId, opt => opt.Ignore());
        createMap.ForMember(dest => dest.Tenant, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.TenantId, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.Tenant, opt => opt.Ignore());

        // Connection lifecycle/ownership fields don't exist on either create/update DTO - a client
        // never dictates these directly. They're driven entirely by RustServersController's own
        // Enable/Disable actions and by the MassTransit consumers reacting to what a Worker instance
        // reports (see ConnectionStatusConsumer/ServerConnectionHeartbeatConsumer). Same reasoning as
        // TenantId above: JumpStart's EntityMappingProfile only auto-ignores audit fields, not these,
        // so every one needs an explicit Ignore() here or AssertConfigurationIsValid() fails at
        // startup exactly the way the original TenantId omission did.
        createMap.ForMember(dest => dest.IsEnabled, opt => opt.Ignore());
        createMap.ForMember(dest => dest.ConnectionStatus, opt => opt.Ignore());
        createMap.ForMember(dest => dest.ConnectionStatusDetail, opt => opt.Ignore());
        createMap.ForMember(dest => dest.ConnectionStatusChangedAtUtc, opt => opt.Ignore());
        createMap.ForMember(dest => dest.AssignedWorkerId, opt => opt.Ignore());
        createMap.ForMember(dest => dest.LastHeartbeatUtc, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.IsEnabled, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.ConnectionStatus, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.ConnectionStatusDetail, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.ConnectionStatusChangedAtUtc, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.AssignedWorkerId, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.LastHeartbeatUtc, opt => opt.Ignore());

        // The RustArchon-plugin switches are NOT on the create/update DTOs, on purpose: Update is a full-record
        // PUT, and carrying them would let an ordinary edit from a client that does not send them silently reset a
        // switch someone turned off. They are changed only through PUT plugin-settings (see UpdatePluginSettings),
        // and a new server takes the entity's own default of on.
        createMap.ForMember(dest => dest.PluginRecordingEnabled, opt => opt.Ignore());
        createMap.ForMember(dest => dest.PluginCombatLogEnabled, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.PluginRecordingEnabled, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.PluginCombatLogEnabled, opt => opt.Ignore());
        // Same reasoning, and stronger: updates are OFF by default and must only ever be turned on deliberately.
        createMap.ForMember(dest => dest.PluginUpdatesEnabled, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.PluginUpdatesEnabled, opt => opt.Ignore());

        // The report-forwarding secret and its verified-at stamp are never on a create/update DTO and never mapped back out
        // (ADR-0001): the secret is minted, rotated and read only by ReportForwardingService, behind its own permission, so an
        // ordinary edit - which a lesser role can make - can neither read nor overwrite it.
        createMap.ForMember(dest => dest.ReportsSecret, opt => opt.Ignore());
        createMap.ForMember(dest => dest.ReportForwardingVerifiedAtUtc, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.ReportsSecret, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.ReportForwardingVerifiedAtUtc, opt => opt.Ignore());
    }
}
