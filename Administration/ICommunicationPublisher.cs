// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.Administration;

/// <summary>
/// The one place an outbound email becomes both a durably-queued <c>EmailRequested</c> message and a
/// permanent <c>Communication</c> record - every caller that wants an email sent goes through this
/// rather than publishing <c>EmailRequested</c> directly, so "every communication sent" (see
/// <c>Communication</c>'s remarks) is actually true rather than true-if-every-caller-remembers-to.
/// </summary>
public interface ICommunicationPublisher
{
    /// <summary>
    /// Records, queues and returns the id of a new communication.
    /// </summary>
    /// <param name="toAddress">Who it's actually going to - always required.</param>
    /// <param name="userId">The member it's to, when the recipient has an account - see
    /// <c>Communication.UserId</c>'s remarks.</param>
    /// <param name="tenantId">Set only for an organization-level communication - see
    /// <c>Communication.TenantId</c>'s remarks.</param>
    /// <param name="subject">The email subject.</param>
    /// <param name="htmlBody">The email body, before the tracking pixel is appended - the stored
    /// <c>Communication.HtmlBody</c> and the message actually sent both include the pixel; this
    /// parameter doesn't need to.</param>
    Task<Guid> QueueAsync(
        string toAddress, Guid? userId, Guid? tenantId, string subject, string htmlBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="QueueAsync"/>, except the Subject/HtmlBody come from the admin-editable
    /// <c>EmailTemplate</c> named <paramref name="templateCode"/> (see
    /// <c>Infrastructure.EmailTemplateRegistry.Codes</c>) rather than being built by the caller - the
    /// preferred way to send anything whose wording an admin might reasonably want to change without a
    /// deployment.
    /// </summary>
    /// <param name="templateCode">One of <c>Infrastructure.EmailTemplateRegistry.Codes</c>.</param>
    /// <param name="tokens">Values for the template's <c>{{Token}}</c> placeholders - see
    /// <c>EmailTemplateRenderer.Render</c>'s remarks for how they're substituted.</param>
    /// <param name="culture">
    /// The recipient's preferred culture (e.g. <c>"en-US"</c>), when known - see
    /// <c>RustArchon.Panel.Data.ApplicationUser.PreferredCulture</c>, the only real source of one today.
    /// Never required: a template with no matching <c>EmailTemplateTranslation</c> for this culture
    /// falls back through the platform's <c>DefaultCulture</c> setting to <c>"en-US"</c>, and finally to
    /// whichever translation exists at all - a missing translation is never a reason to fail a send.
    /// Organization-level sends (tenantId set) have no single recipient culture to pass and simply omit
    /// this, the same as before culture existed at all.
    /// </param>
    Task<Guid> QueueTemplatedAsync(
        string templateCode, IReadOnlyDictionary<string, string> tokens, string toAddress, Guid? userId,
        Guid? tenantId, string? culture = null, CancellationToken cancellationToken = default);
}
