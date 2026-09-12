// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Configuration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Administration;

/// <inheritdoc cref="ICommunicationPublisher" />
public class CommunicationPublisher(
    ICommunicationRepository communications, IEmailTemplateRepository emailTemplates,
    IPublishEndpoint publishEndpoint, IConfiguration configuration, IPlatformSettingsCache settingsCache)
    : ICommunicationPublisher
{
    /// <inheritdoc />
    public async Task<Guid> QueueAsync(
        string toAddress, Guid? userId, Guid? tenantId, string subject, string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var (siteName, siteUrl) = await ResolveSiteBrandingAsync();
        return await QueueInternalAsync(
            toAddress, userId, tenantId, subject, htmlBody, siteName, siteUrl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid> QueueTemplatedAsync(
        string templateCode, IReadOnlyDictionary<string, string> tokens, string toAddress, Guid? userId,
        Guid? tenantId, string? culture = null, CancellationToken cancellationToken = default)
    {
        var template = await emailTemplates.GetByCodeAsync(templateCode)
            ?? throw new InvalidOperationException($"No email template registered for code '{templateCode}'.");

        var defaultCulture = await settingsCache.GetStringAsync(PlatformSettingsRegistry.DefaultCulture);
        var translation = ResolveTranslation(template, culture, defaultCulture);

        var (siteName, siteUrl) = await ResolveSiteBrandingAsync();

        // Available to every templated email automatically - a caller never has to remember to pass
        // these the way it does OrganizationName or InvoiceNumber. A caller-supplied value of the same
        // key would be overwritten here, but neither name is one any real caller has ever had reason
        // to pass - both are reserved, platform-wide tokens, not per-send data.
        var mergedTokens = new Dictionary<string, string>(tokens)
        {
            [EmailTemplateRegistry.Placeholders.SiteName] = siteName,
            [EmailTemplateRegistry.Placeholders.SiteUrl] = siteUrl
        };

        var (subject, htmlBody) = EmailTemplateRenderer.Render(translation.Subject, translation.HtmlBody, mergedTokens);

        return await QueueInternalAsync(
            toAddress, userId, tenantId, subject, htmlBody, siteName, siteUrl, cancellationToken);
    }

    /// <summary>
    /// Picks which of <paramref name="template"/>'s <see cref="EmailTemplateTranslation"/>s to actually
    /// send, in order: the exact requested culture, that culture's parent (so <c>"en-GB"</c> falls back
    /// to a plain <c>"en"</c> translation if one exists, without needing every regional variant seeded),
    /// the platform's <see cref="PlatformSettingsRegistry.DefaultCulture"/> setting, the hardcoded
    /// <c>"en-US"</c> every template is seeded with, and finally whatever translation exists at all.
    /// </summary>
    /// <remarks>
    /// Api has no notion of "the supported cultures" to validate <paramref name="requestedCulture"/>
    /// against - see <see cref="Data.EmailTemplateTranslation"/>'s own remarks. An unrecognized or
    /// misspelled culture string simply falls through every step here rather than erroring, the same as
    /// a recognized one with no translation yet.
    /// </remarks>
    private static EmailTemplateTranslation ResolveTranslation(
        EmailTemplate template, string? requestedCulture, string? defaultCulture)
    {
        EmailTemplateTranslation? ByExactCulture(string? culture) =>
            string.IsNullOrWhiteSpace(culture)
                ? null
                : template.Translations.FirstOrDefault(t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));

        string? ParentCultureName(string? culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
            {
                return null;
            }

            try
            {
                var parent = CultureInfo.GetCultureInfo(culture).Parent;
                return parent == CultureInfo.InvariantCulture ? null : parent.Name;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }

        return ByExactCulture(requestedCulture)
            ?? ByExactCulture(ParentCultureName(requestedCulture))
            ?? ByExactCulture(defaultCulture)
            ?? ByExactCulture(EmailTemplateRegistry.SeedCulture)
            ?? template.Translations.FirstOrDefault()
            ?? throw new InvalidOperationException($"Email template '{template.Code}' has no translations at all.");
    }

    /// <summary>
    /// The actual queueing logic shared by both public methods - factored out so
    /// <see cref="QueueTemplatedAsync"/> can resolve the platform's branding once and reuse it for both
    /// the token substitution and the <see cref="EmailLayout.Wrap"/> shell, rather than
    /// <see cref="QueueAsync"/> resolving it again from inside a second call.
    /// </summary>
    private async Task<Guid> QueueInternalAsync(
        string toAddress, Guid? userId, Guid? tenantId, string subject, string htmlBody,
        string siteName, string siteUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlBody);

        // Assigned ourselves rather than left to the database's default generator: this id has to be
        // known before the row is ever saved, since it's what the tracking pixel embedded in the
        // stored HtmlBody itself points at. Guid.CreateVersion7() matches what the default generator
        // would have produced anyway (time-ordered, same as every other entity's id here) - this just
        // has to happen earlier than usual.
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var communication = new Communication
        {
            Id = id,
            UserId = userId,
            TenantId = tenantId,
            ToAddress = toAddress,
            Subject = subject,
            HtmlBody = WithTrackingPixel(id, EmailLayout.Wrap(htmlBody, siteName, siteUrl)),
            Status = CommunicationStatus.Queued,
            QueuedOn = now
        };

        await communications.AddAsync(communication);

        await publishEndpoint.Publish(
            new EmailRequested(id, toAddress, subject, communication.HtmlBody), cancellationToken);

        return id;
    }

    /// <summary>
    /// The platform's current name and public URL, falling back to
    /// <see cref="PlatformSettingsRegistry.DefaultSiteName"/>/<see cref="PlatformSettingsRegistry.DefaultSiteUrl"/>
    /// if either setting is somehow unset - see those constants' own remarks for why that should never
    /// actually happen once <see cref="PlatformSettingsRegistry.EnsureDefaultsAsync"/> has run.
    /// </summary>
    private async Task<(string SiteName, string SiteUrl)> ResolveSiteBrandingAsync()
    {
        var siteName = await settingsCache.GetStringAsync(PlatformSettingsRegistry.SiteName);
        var siteUrl = await settingsCache.GetStringAsync(PlatformSettingsRegistry.SiteUrl);

        return (
            string.IsNullOrWhiteSpace(siteName) ? PlatformSettingsRegistry.DefaultSiteName : siteName,
            string.IsNullOrWhiteSpace(siteUrl) ? PlatformSettingsRegistry.DefaultSiteUrl : siteUrl);
    }

    /// <summary>
    /// Appends an invisible 1x1 image request pointed at the Panel's own public tracking-pixel route -
    /// see <c>IInternalCommunicationApiClient</c>'s remarks in RustArchon.Panel for why the pixel lives
    /// there and not on this Api.
    /// </summary>
    private string WithTrackingPixel(Guid communicationId, string htmlBody)
    {
        var panelUrl = (configuration["CorsSettings:BlazorServerUrl"] ?? "https://localhost:7199").TrimEnd('/');
        var pixelUrl = $"{panelUrl}/track/email/{communicationId:D}.gif";

        return htmlBody +
            $"""<img src="{WebUtility.HtmlEncode(pixelUrl)}" width="1" height="1" alt="" style="display:none" />""";
    }
}
