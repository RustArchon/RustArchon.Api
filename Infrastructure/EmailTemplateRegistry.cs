// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// The single place every email template RustArchon sends is declared, and the seeder that ensures
/// each one exists in <see cref="ApiDbContext.EmailTemplates"/> with its default wording.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="PlatformSettingsRegistry"/>'s own split: <strong>idempotent</strong>, safe to call
/// on every Api startup, but not write-once - an existing template's <see cref="Data.EmailTemplate.Name"/>/
/// <see cref="Data.EmailTemplate.Description"/> are synced from here on every startup, since that's
/// code-defined metadata, not the admin's own words.
/// </para>
/// <para>
/// The seeded <see cref="Data.EmailTemplateTranslation"/> (see <see cref="SeedCulture"/>)/
/// <see cref="Data.EmailTemplate.Placeholders"/> and <see cref="Data.EmailPlaceholder.Description"/>/
/// <see cref="Data.EmailPlaceholder.Sample"/>, though, are seeded once and then left alone - this call
/// only fills them in the first time a row is created. From then on they're the admin's own, editable
/// through <see cref="Controllers.EmailTemplatesController"/> and
/// <see cref="Controllers.EmailPlaceholdersController"/> respectively, and never overwritten by a later
/// restart re-running this. The list passed to <c>EnsureTemplateAsync</c>'s <c>placeholders</c>
/// parameter is therefore only ever a *starting point* for a new template, not a standing declaration
/// enforced on every startup - an admin who removes or adds a placeholder through the UI stays removed
/// or added.
/// </para>
/// <para>
/// Adding a new template a feature needs is adding one <see cref="Codes"/> constant plus one
/// <c>EnsureTemplateAsync</c> call below (a code change, but never a migration) - callers then queue
/// through it via <c>ICommunicationPublisher.QueueTemplatedAsync</c> rather than building HTML inline.
/// A new placeholder is the same idea: one <see cref="Placeholders"/> constant plus one
/// <c>EnsurePlaceholderAsync</c> call, then list it among whichever templates' <c>EnsureTemplateAsync</c>
/// calls should start with it.
/// </para>
/// </remarks>
public static class EmailTemplateRegistry
{
    /// <summary>The stable <see cref="Data.EmailTemplate.Code"/> values calling code asks for.</summary>
    public static class Codes
    {
        /// <summary>Sent by <c>OrganizationInvitationService</c> when someone is invited to join an
        /// Organization.</summary>
        public const string OrganizationInvitation = "OrganizationInvitation";

        /// <summary>Sent by <c>QueuedEmailSender</c> (RustArchon.Panel) to confirm a new account, a
        /// resent confirmation, or a changed email address - ASP.NET Core Identity's own
        /// <c>IEmailSender{TUser}.SendConfirmationLinkAsync</c> is one method for all three, so this
        /// one template covers all three too.</summary>
        public const string EmailConfirmation = "EmailConfirmation";

        /// <summary>Sent by <c>QueuedEmailSender</c> when someone requests a password reset link.</summary>
        public const string PasswordResetLink = "PasswordResetLink";

        /// <summary>Sent by <c>QueuedEmailSender</c> when someone requests a password reset code
        /// (Identity's code-based flow, alongside the link-based one above).</summary>
        public const string PasswordResetCode = "PasswordResetCode";

        /// <summary>Sent by <c>OrganizationLifecycleService.SetStatusAsync</c> when an Organization is
        /// marked past due.</summary>
        public const string SubscriptionPastDue = "SubscriptionPastDue";

        /// <summary>Sent by <c>OrganizationLifecycleService.SetStatusAsync</c> when an Organization is
        /// suspended - the "why your servers just stopped" notice.</summary>
        public const string SubscriptionSuspended = "SubscriptionSuspended";

        /// <summary>Sent by <c>OrganizationLifecycleService.SetStatusAsync</c> when an Organization
        /// moves out of Suspended or PastDue back to Active.</summary>
        public const string SubscriptionReactivated = "SubscriptionReactivated";

        /// <summary>Sent by <c>OrganizationLifecycleService.CancelAsync</c> when an Organization's
        /// subscription ends.</summary>
        public const string SubscriptionCancelled = "SubscriptionCancelled";

        /// <summary>Sent by <c>OrganizationLifecycleService.ReopenAsync</c> when a cancelled
        /// Organization comes back.</summary>
        public const string SubscriptionReopened = "SubscriptionReopened";

        /// <summary>Sent by <c>InvoiceService.IssueForPeriodAsync</c> when a new invoice is raised.</summary>
        public const string InvoiceIssued = "InvoiceIssued";

        /// <summary>Sent by <c>PaymentService.RecordPaymentAsync</c> when a payment is recorded - a
        /// receipt, not an invoice.</summary>
        public const string PaymentReceived = "PaymentReceived";

        /// <summary>Sent instead of <see cref="SubscriptionCancelled"/> when a site admin cancels an
        /// Organization with <c>CancellationReasonCategory.TosViolation</c> - see
        /// <c>OrganizationLifecycleService.CancelAsync</c>. The account is already closed by the time
        /// this goes out; it is not a prior warning.</summary>
        public const string TosViolationNotice = "TosViolationNotice";
    }

    /// <summary>The stable <see cref="Data.EmailPlaceholder.Name"/> values calling code asks for -
    /// also what the tokens dictionary passed to <c>ICommunicationPublisher.QueueTemplatedAsync</c>
    /// must key its values by.</summary>
    public static class Placeholders
    {
        /// <summary>The platform's own display name - see <see cref="PlatformSettingsRegistry.SiteName"/>.
        /// Available in every email automatically; <c>CommunicationPublisher.QueueTemplatedAsync</c>
        /// supplies it whether or not a template lists it here.</summary>
        public const string SiteName = "SiteName";

        /// <summary>The platform's own public site URL - see <see cref="PlatformSettingsRegistry.SiteUrl"/>.
        /// Same automatic availability as <see cref="SiteName"/>.</summary>
        public const string SiteUrl = "SiteUrl";

        public const string OrganizationName = "OrganizationName";
        public const string InviteLink = "InviteLink";
        public const string ConfirmationLink = "ConfirmationLink";
        public const string ResetLink = "ResetLink";
        public const string ResetCode = "ResetCode";

        /// <summary>Why something happened, when a site admin gave one - shared across every
        /// status-change and violation-notice template rather than one per template, since it means
        /// the same thing everywhere it appears.</summary>
        public const string Reason = "Reason";

        public const string PlanName = "PlanName";
        public const string InvoiceNumber = "InvoiceNumber";
        public const string AmountDue = "AmountDue";
        public const string DueDate = "DueDate";
        public const string AmountPaid = "AmountPaid";
        public const string ReceivedDate = "ReceivedDate";
    }

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, ILogger logger)
    {
        var siteName = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.SiteName,
            description: "The platform's own display name - available in every email automatically.",
            sample: PlatformSettingsRegistry.DefaultSiteName,
            logger: logger);

        var siteUrl = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.SiteUrl,
            description: "The platform's own public site URL - available in every email automatically.",
            sample: PlatformSettingsRegistry.DefaultSiteUrl,
            logger: logger);

        var organizationName = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.OrganizationName,
            description: "The Organization's display name.",
            sample: "Acme Corporation",
            logger: logger);

        var inviteLink = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.InviteLink,
            description: "The link the recipient clicks to accept the invitation.",
            sample: "https://panel.example.com/Organization/Invitations/Accept?token=sample-token",
            logger: logger);

        var confirmationLink = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.ConfirmationLink,
            description: "The link that confirms an account or a changed email address.",
            sample: "https://panel.example.com/Account/ConfirmEmail?userId=sample&code=sample-code",
            logger: logger);

        var resetLink = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.ResetLink,
            description: "The link that lets someone choose a new password.",
            sample: "https://panel.example.com/Account/ResetPassword?code=sample-code",
            logger: logger);

        var resetCode = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.ResetCode,
            description: "A short code, typed in instead of following a link, that lets someone reset their password.",
            sample: "482913",
            logger: logger);

        var reason = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.Reason,
            description: "Why this happened, in the site admin's own words - blank if none was given.",
            sample: "Payment failed after several attempts.",
            logger: logger);

        var planName = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.PlanName,
            description: "The plan name.",
            sample: "Stone",
            logger: logger);

        var invoiceNumber = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.InvoiceNumber,
            description: "The invoice's own number.",
            sample: "INV-000482",
            logger: logger);

        var amountDue = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.AmountDue,
            description: "The invoice total, formatted as currency.",
            sample: "$15.00",
            logger: logger);

        var dueDate = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.DueDate,
            description: "The invoice's due date.",
            sample: "24 Sep 2026",
            logger: logger);

        var amountPaid = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.AmountPaid,
            description: "The payment amount, formatted as currency.",
            sample: "$15.00",
            logger: logger);

        var receivedDate = await EnsurePlaceholderAsync(
            dbContext,
            name: Placeholders.ReceivedDate,
            description: "The date the payment was received.",
            sample: "10 Sep 2026",
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.OrganizationInvitation,
            name: "Organization invitation",
            description: "Sent when someone is invited to join an Organization.",
            defaultSubject: "You've been invited to join {{OrganizationName}}",
            defaultHtmlBody:
                """
                <p>You've been invited to join <strong>{{OrganizationName}}</strong> on {{SiteName}}.</p>
                <p><a href="{{InviteLink}}">Accept the invitation</a></p>
                <p>If you don't have a {{SiteName}} account yet, you'll be asked to create one first -
                use this email address, since the invitation is addressed to it.</p>
                <p>If you weren't expecting this, you can ignore it. The link stops working on its own.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, inviteLink],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.EmailConfirmation,
            name: "Confirm email address",
            description: "Sent to confirm a new account, a resent confirmation, or a changed email address.",
            defaultSubject: "Confirm your email address",
            defaultHtmlBody:
                """
                <p>Please confirm your account by <a href="{{ConfirmationLink}}">clicking here</a>.</p>
                <p>If you didn't request this, you can ignore it.</p>
                """,
            placeholders: [siteName, siteUrl, confirmationLink],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.PasswordResetLink,
            name: "Password reset link",
            description: "Sent when someone requests a password reset link.",
            defaultSubject: "Reset your password",
            defaultHtmlBody:
                """
                <p>Please reset your password by <a href="{{ResetLink}}">clicking here</a>.</p>
                <p>If you didn't request this, you can ignore it.</p>
                """,
            placeholders: [siteName, siteUrl, resetLink],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.PasswordResetCode,
            name: "Password reset code",
            description: "Sent when someone requests a password reset code instead of a link.",
            defaultSubject: "Reset your password",
            defaultHtmlBody:
                """
                <p>Your password reset code is <strong>{{ResetCode}}</strong>.</p>
                <p>If you didn't request this, you can ignore it.</p>
                """,
            placeholders: [siteName, siteUrl, resetCode],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.SubscriptionPastDue,
            name: "Subscription past due",
            description: "Sent when an Organization's subscription is marked past due.",
            defaultSubject: "{{OrganizationName}}'s {{SiteName}} subscription is past due",
            defaultHtmlBody:
                """
                <p><strong>{{OrganizationName}}</strong>'s subscription has been marked past due.</p>
                <p>{{Reason}}</p>
                <p>Servers are still running for now - settle the balance to avoid a suspension.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, reason],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.SubscriptionSuspended,
            name: "Subscription suspended",
            description: "Sent when an Organization's servers are stopped for non-payment or another reason.",
            defaultSubject: "{{OrganizationName}}'s {{SiteName}} servers have been suspended",
            defaultHtmlBody:
                """
                <p><strong>{{OrganizationName}}</strong>'s servers have been suspended and are no longer
                running.</p>
                <p>{{Reason}}</p>
                <p>Contact support to have them restored.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, reason],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.SubscriptionReactivated,
            name: "Subscription reactivated",
            description: "Sent when an Organization moves out of past due or suspended back to active.",
            defaultSubject: "{{OrganizationName}} is active again",
            defaultHtmlBody:
                """
                <p>Good news - <strong>{{OrganizationName}}</strong>'s subscription is active again and any
                suspended servers have been asked to reconnect.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.SubscriptionCancelled,
            name: "Subscription cancelled",
            description: "Sent when an Organization's subscription is cancelled and its servers stopped.",
            defaultSubject: "{{OrganizationName}}'s {{SiteName}} subscription has been cancelled",
            defaultHtmlBody:
                """
                <p><strong>{{OrganizationName}}</strong>'s subscription has been cancelled and its servers
                have been stopped.</p>
                <p>{{Reason}}</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, reason],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.SubscriptionReopened,
            name: "Subscription reopened",
            description: "Sent when a cancelled Organization is reopened.",
            defaultSubject: "Welcome back to {{SiteName}}, {{OrganizationName}}",
            defaultHtmlBody:
                """
                <p><strong>{{OrganizationName}}</strong> has been reopened on the <strong>{{PlanName}}</strong>
                plan.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, planName],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.InvoiceIssued,
            name: "Invoice issued",
            description: "Sent when a new invoice is raised.",
            defaultSubject: "New invoice {{InvoiceNumber}} for {{OrganizationName}}",
            defaultHtmlBody:
                """
                <p>A new invoice has been issued for <strong>{{OrganizationName}}</strong>.</p>
                <p>Invoice <strong>{{InvoiceNumber}}</strong> - <strong>{{AmountDue}}</strong> due
                <strong>{{DueDate}}</strong>.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, invoiceNumber, amountDue, dueDate],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.PaymentReceived,
            name: "Payment received",
            description: "Sent when a payment is recorded against an Organization's account.",
            defaultSubject: "Payment received - {{OrganizationName}}",
            defaultHtmlBody:
                """
                <p>Thanks - a payment of <strong>{{AmountPaid}}</strong> was received from
                <strong>{{OrganizationName}}</strong> on {{ReceivedDate}}.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, amountPaid, receivedDate],
            logger: logger);

        await EnsureTemplateAsync(
            dbContext,
            code: Codes.TosViolationNotice,
            name: "TOS violation notice",
            description: "Sent when a site admin cancels an Organization for a Terms of Service violation, "
                + "in place of the ordinary cancellation notice.",
            defaultSubject: "Your {{SiteName}} account has been cancelled - {{OrganizationName}}",
            defaultHtmlBody:
                """
                <p>Your {{SiteName}} account for <strong>{{OrganizationName}}</strong> has been cancelled due to a
                violation of our Terms of Service.</p>
                <p>{{Reason}}</p>
                <p>If you believe this was a mistake, please contact us to discuss it.</p>
                """,
            placeholders: [siteName, siteUrl, organizationName, reason],
            logger: logger);
    }

    /// <summary>
    /// Seeds a placeholder the first time it's asked for; an existing one is returned untouched -
    /// <see cref="Data.EmailPlaceholder.Description"/>/<see cref="Data.EmailPlaceholder.Sample"/> are
    /// the admin's own from then on, editable through <see cref="Controllers.EmailPlaceholdersController"/>.
    /// </summary>
    private static async Task<EmailPlaceholder> EnsurePlaceholderAsync(
        ApiDbContext dbContext, string name, string description, string sample, ILogger logger)
    {
        var existing = await dbContext.Set<EmailPlaceholder>().FirstOrDefaultAsync(p => p.Name == name);

        if (existing is not null)
        {
            return existing;
        }

        var placeholder = new EmailPlaceholder
        {
            Name = name,
            Description = description,
            Sample = sample,
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        };

        dbContext.Set<EmailPlaceholder>().Add(placeholder);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded email placeholder '{Name}'.", name);
        return placeholder;
    }

    /// <summary>The culture every template's seeded default wording is written in - see
    /// <see cref="EmailTemplateTranslation.Culture"/>. Not "the platform's default culture" (that's
    /// <see cref="PlatformSettingsRegistry.DefaultCulture"/>, an admin-editable setting with no seeded
    /// value of its own) - this one is fixed, since the strings a few lines below it are English. Also
    /// the last-resort rung of <c>CommunicationPublisher.ResolveTranslation</c>'s fallback chain, since
    /// it's the one culture every template is guaranteed to actually have a row for.</summary>
    public const string SeedCulture = "en-US";

    private static async Task EnsureTemplateAsync(
        ApiDbContext dbContext, string code, string name, string description, string defaultSubject,
        string defaultHtmlBody, IReadOnlyList<EmailPlaceholder> placeholders, ILogger logger)
    {
        var existing = await dbContext.Set<EmailTemplate>().FirstOrDefaultAsync(t => t.Code == code);

        if (existing is not null)
        {
            // Name/Description only - the SeedCulture translation and Placeholders are the admin's own
            // once the row exists. See this class's own remarks for why the rest syncs on every startup
            // but these don't.
            existing.Name = name;
            existing.Description = description;
            await dbContext.SaveChangesAsync();
            return;
        }

        var template = new EmailTemplate
        {
            Code = code,
            Name = name,
            Description = description,
            Placeholders = [.. placeholders],
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        };

        template.Translations.Add(new EmailTemplateTranslation
        {
            Culture = SeedCulture,
            Subject = defaultSubject,
            HtmlBody = defaultHtmlBody,
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        });

        dbContext.Set<EmailTemplate>().Add(template);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded email template '{Code}'.", code);
    }
}
