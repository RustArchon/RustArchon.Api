// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="IOrganizationServerAdminService" />
public class OrganizationServerAdminService(
    ApiDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IRequestClient<SendRconCommand> sendCommandClient,
    IRconCredentialProtector rconCredentialProtector,
    ILogger<OrganizationServerAdminService> logger) : IOrganizationServerAdminService
{
    /// <inheritdoc />
    public async Task<bool> EnableAsync(
        Guid tenantId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await FindAsync(tenantId, serverId, cancellationToken);
        if (server is null)
        {
            return false;
        }

        if (!server.IsEnabled)
        {
            server.IsEnabled = true;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Published unconditionally, exactly as the tenant-facing path does: a server that is already
        // enabled but has lost its worker still needs a claim, and re-publishing one is idempotent.
        await publishEndpoint.Publish(new ConnectToServer(server.Id, server.TenantId), cancellationToken);

        logger.LogInformation(
            "Site admin enabled server {ServerId} for tenant {TenantId}.", serverId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DisableAsync(
        Guid tenantId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await FindAsync(tenantId, serverId, cancellationToken);
        if (server is null)
        {
            return false;
        }

        if (server.IsEnabled)
        {
            server.IsEnabled = false;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await publishEndpoint.Publish(
            new ServerLifecycleChanged(server.Id, server.TenantId, ServerLifecycleChangeType.Disabled),
            cancellationToken);

        logger.LogInformation(
            "Site admin disabled server {ServerId} for tenant {TenantId}.", serverId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateConnectionAsync(
        Guid tenantId, Guid serverId, string host, int port, string? rconPassword,
        CancellationToken cancellationToken = default)
    {
        var server = await FindAsync(tenantId, serverId, cancellationToken);
        if (server is null)
        {
            return false;
        }

        server.Host = host;
        server.Port = port;

        if (!string.IsNullOrWhiteSpace(rconPassword))
        {
            server.RconPassword = rconCredentialProtector.Protect(rconPassword);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Only an enabled server has a connection worth rebuilding - the same condition the tenant-facing
        // update applies, so a disabled server is not woken up by being edited.
        if (server.IsEnabled)
        {
            await publishEndpoint.Publish(
                new ServerLifecycleChanged(server.Id, server.TenantId, ServerLifecycleChangeType.Updated),
                cancellationToken);
        }

        logger.LogInformation(
            "Site admin updated connection details for server {ServerId} (tenant {TenantId}); "
            + "RCON password {PasswordState}.",
            serverId, tenantId, string.IsNullOrWhiteSpace(rconPassword) ? "unchanged" : "replaced");

        return true;
    }

    /// <inheritdoc />
    public async Task<RconCommandResult?> SendCommandAsync(
        Guid tenantId, Guid serverId, string command, CancellationToken cancellationToken = default)
    {
        var server = await FindAsync(tenantId, serverId, cancellationToken);
        if (server is null)
        {
            return null;
        }

        logger.LogInformation(
            "Site admin sent RCON command to server {ServerId} (tenant {TenantId}).", serverId, tenantId);

        try
        {
            var response = await sendCommandClient.GetResponse<RconCommandResult>(
                new SendRconCommand(serverId, command),
                cancellationToken,
                timeout: RequestTimeout.After(s: 10));

            return response.Message;
        }
        catch (RequestTimeoutException)
        {
            // No worker answered. Reported as a failed command rather than thrown, so the caller gets the
            // same shape back whether the connection was missing or nobody was listening at all.
            return new RconCommandResult(false, null, null, null, "Timeout");
        }
    }

    /// <summary>
    /// The server, but only if it really belongs to the named Organization.
    /// </summary>
    /// <remarks>
    /// The tenant id comes from the route and the server id from the row that was clicked, so they agree
    /// in every honest request. Checking anyway is what stops a hand-built one from acting on a server in
    /// a different Organization than the page said - the id pair is the only thing carrying the intent,
    /// and half of it would otherwise go unverified.
    /// </remarks>
    private Task<RustServer?> FindAsync(Guid tenantId, Guid serverId, CancellationToken cancellationToken) =>
        dbContext.Set<RustServer>()
            .AcrossAllTenants()
            .FirstOrDefaultAsync(s => s.Id == serverId && s.TenantId == tenantId, cancellationToken);
}
