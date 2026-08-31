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
        // Update DTO's RconPassword is optional (null/empty means "keep the existing password") and
        // must never overwrite the encrypted value in place - the controller handles it explicitly.
        updateMap.ForMember(dest => dest.RconPassword, opt => opt.Ignore());

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
    }
}
