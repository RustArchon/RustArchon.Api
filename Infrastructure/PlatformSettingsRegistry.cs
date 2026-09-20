// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Data;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// The single place every platform-wide setting RustArchon knows about is declared, and the seeder
/// that ensures each one exists in <see cref="ApiDbContext.PlatformSettings"/> with its default value.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <see cref="Data.PlatformSetting"/>'s generic key/value shape actually usable:
/// adding a new setting is adding one <c>EnsureSettingAsync</c> call below (a code change, but never a
/// migration) - the admin UI and <see cref="IPlatformSettingsCache"/> both work against whatever rows
/// exist without needing to know the full set of keys in advance.
/// </para>
/// <para>
/// <strong>Idempotent</strong>, safe to call on every Api startup (mirrors <see cref="AdminInvitationSeeder"/>/
/// <see cref="SiteAdminRoleSeeder"/>), but not write-once the way it first sounds: an existing row's
/// <see cref="Data.PlatformSetting.DisplayName"/>/<see cref="Data.PlatformSetting.Description"/>/
/// <see cref="Data.PlatformSetting.ValueType"/>/<see cref="Data.PlatformSetting.Category"/>/
/// <see cref="Data.PlatformSetting.Order"/>/<see cref="Data.PlatformSetting.Options"/>/
/// <see cref="Data.PlatformSetting.VisibleWhenKey"/>/<see cref="Data.PlatformSetting.VisibleWhenValue"/>/
/// <see cref="Data.PlatformSetting.VisibleWhenNegate"/> are synced from here on every startup, since
/// all of it is code-defined metadata, not the admin's own data - a wording fix, a re-grouped section
/// or a re-ordered field should actually take effect on existing deployments, not just new ones. Only
/// <see cref="Data.PlatformSetting.Value"/> is ever left alone once a row exists; an admin's edit
/// through <see cref="Controllers.PlatformSettingsController"/> is never overwritten by a later
/// restart re-running this.
/// </para>
/// </remarks>
public static class PlatformSettingsRegistry
{
    /// <summary>The admin settings page's section headers - see <see cref="Data.PlatformSetting.Category"/>.</summary>
    public static class Categories
    {
        public const string General = "General";
        public const string Registration = "Registration";
        public const string Billing = "Billing";
        public const string Payments = "Payments";
        public const string Email = "Email";
        public const string Ticketing = "Ticketing";
        public const string Plugin = "Plugin";
        public const string Reports = "Reports";
    }

    /// <summary>
    /// The values <see cref="EmailServiceProvider"/> can hold - see
    /// <c>RustArchon.Worker</c>'s <c>EmailDeliveryProviderFactory</c>, the only other place these
    /// exact strings matter.
    /// </summary>
    public static class EmailProviders
    {
        public const string Smtp = "Smtp";
        public const string Resend = "Resend";
        public const string SendGrid = "SendGrid";
    }

    /// <summary>
    /// The values <see cref="TicketingProvider"/> can hold - see <c>RustArchon.Worker</c>'s
    /// <c>TicketingIntegrationProviderFactory</c>, the only other place these exact strings matter.
    /// Only <see cref="Internal"/> and <see cref="Webhook"/> exist today; a named vendor integration
    /// (Zendesk, etc.) is a future addition to this set plus a new
    /// <c>ITicketingIntegrationProvider</c>, not a rework of the seam itself.
    /// </summary>
    public static class TicketingProviders
    {
        public const string Internal = "Internal";
        public const string Webhook = "Webhook";
    }

    /// <summary>
    /// The values <see cref="CaptchaProvider"/> can hold - see
    /// <c>RustArchon.Api.Infrastructure.Captcha.CaptchaVerifierFactory</c>, the only other place these
    /// exact strings matter. Gates only the anonymous public ticket-submission form today - the one
    /// unauthenticated, form-filling surface this platform has.
    /// </summary>
    public static class CaptchaProviders
    {
        public const string None = "None";
        public const string ReCaptcha = "ReCaptcha";
        public const string Turnstile = "Turnstile";
    }

    /// <summary>
    /// The platform's own display name - the wordmark in the Panel's nav bar, every page's title
    /// suffix, and the <c>{{SiteName}}</c> token <c>CommunicationPublisher</c> makes available to
    /// every email it sends, templated or not (see <see cref="EmailTemplateRegistry.Placeholders.SiteName"/>).
    /// Read anonymously through <see cref="Controllers.PublicBrandingController"/>, unlike the rest of
    /// this table - a nav bar has to show its own name before anyone is signed in.
    /// </summary>
    public const string SiteName = "SiteName";

    /// <summary>The <see cref="SiteName"/>/<see cref="Controllers.PublicBrandingController"/> fallback
    /// used when the setting is unset or unreachable - never expected to matter once seeded, since
    /// <see cref="EnsureDefaultsAsync"/> always creates the row, but a caller a few seconds into a
    /// fresh deployment (or riding out a database blip) still needs something to show.</summary>
    public const string DefaultSiteName = "RustArchon";

    /// <summary>
    /// The platform's own public site URL - what the Panel's nav brand link points at, and the
    /// <c>{{SiteUrl}}</c> token available to every email alongside <see cref="SiteName"/>. Distinct
    /// from the Api's own <c>CorsSettings:BlazorServerUrl</c> configuration value: that one is where
    /// the Api itself builds a working link back into the Panel (invitation accept links, the tracking
    /// pixel) and is necessarily per-environment infrastructure, not admin-editable branding, whereas
    /// this is the public-facing address worth putting in front of a reader - the same one regardless
    /// of which environment happened to send the email.
    /// </summary>
    public const string SiteUrl = "SiteUrl";

    /// <summary>See <see cref="DefaultSiteName"/>'s remarks - the same reasoning, for <see cref="SiteUrl"/>.</summary>
    public const string DefaultSiteUrl = "https://www.rustarchon.com";

    /// <summary>
    /// This deployment's own Panel base URL - where <c>StripeCheckoutService</c> sends the browser back
    /// to once a Checkout session completes or is abandoned. Distinct from <see cref="SiteUrl"/>: that
    /// one is the public marketing address worth putting in front of a reader (and available to every
    /// email as a placeholder); this is purely infrastructure - which running instance of the Panel a
    /// server-side redirect needs to build a working URL against. Also distinct from
    /// <c>CorsSettings:BlazorServerUrl</c>, the Api's own CORS allow-list entry - that one has to be
    /// synchronous, read-once ASP.NET Core middleware configuration, evaluated before the app (and so
    /// before this table) is even accepting requests, so it stays an environment value; this setting is
    /// everything downstream of startup that needs the same URL, seeded from that same env value the one
    /// time this row is created (see <see cref="InvitationCodesEnabled"/>'s own <c>legacyEnvDefault</c>
    /// for the identical one-time-seed-then-ignore-the-env-var pattern) so an existing deployment's
    /// current behavior doesn't change the moment this ships.
    /// </summary>
    public const string PanelBaseUrl = "PanelBaseUrl";

    /// <summary>
    /// <see cref="PanelBaseUrl"/>'s fallback if the row is ever missing or unreadable - the same
    /// development-mode address <c>CorsSettings:BlazorServerUrl</c> itself falls back to.
    /// </summary>
    public const string DefaultPanelBaseUrl = "https://localhost:7199";

    /// <summary>
    /// Which setting keys, when changed, need every already-open RustArchon.Panel circuit told that its
    /// next navigation must be a full reload - see <see cref="IAppGenerationCache"/>. A key belongs here
    /// only if it's rendered into the page chrome itself, visible on every screen regardless of which
    /// one a circuit happens to be sitting on right now - <see cref="SiteName"/>/<see cref="SiteUrl"/>
    /// (the nav bar's own brand name/link) are the founding members. A setting nothing currently
    /// on-screen reflects (an SMTP host, payment terms) does not belong here: bumping the generation for
    /// one of those would force a full reload for a change nobody would ever actually see mid-session.
    /// </summary>
    public static readonly IReadOnlySet<string> KeysAffectingRenderedChrome = new HashSet<string> { SiteName, SiteUrl };

    /// <summary>
    /// Whether registration requires a valid invitation code. Replaces the old
    /// <c>RUSTARCHON_INVITATION_CODES_ENABLED</c> environment-variable toggle - see
    /// <see cref="Controllers.InvitationsController"/>, the only place this is read.
    /// </summary>
    public const string InvitationCodesEnabled = "InvitationCodesEnabled";

    /// <summary>
    /// Which <see cref="Plan"/> a brand-new Organization is assigned on sign-up - see
    /// <c>AccountBootstrapController</c>, the only place this is read. Empty (the seeded default) means
    /// "no explicit choice made yet" - bootstrap falls back to the cheapest currently-active Plan in
    /// that case (see <c>IPlanRepository.GetCheapestActiveAsync</c>). Once an admin does pick one here,
    /// that exact Plan is used even if it's later deactivated - the picker in the admin UI keeps
    /// showing it in that case specifically so the admin can see (and change) what's actually
    /// configured rather than it silently reverting to the cheapest-active fallback.
    /// </summary>
    public const string DefaultPlanId = "DefaultPlanId";

    /// <summary>
    /// How many days after issue an invoice falls due - see <c>InvoiceService</c>, the only place this
    /// is read. A setting rather than a constant because it is a commercial decision that changes
    /// without a deployment, and because every "past due" figure on every receivables report is derived
    /// from it.
    /// </summary>
    public const string PaymentTermsDays = "PaymentTermsDays";

    /// <summary>The value <see cref="PaymentTermsDays"/> falls back to when unset or unparseable.</summary>
    public const int DefaultPaymentTermsDays = 14;

    /// <summary>
    /// How many days before an invoice's due date <c>DunningService</c> sends the "payment due soon"
    /// reminder - see <see cref="EmailTemplateRegistry.Codes.PaymentDueSoon"/>. A commercial decision,
    /// same reasoning as <see cref="PaymentTermsDays"/>.
    /// </summary>
    public const string PaymentDueSoonReminderDays = "PaymentDueSoonReminderDays";

    /// <summary>The value <see cref="PaymentDueSoonReminderDays"/> falls back to when unset or unparseable.</summary>
    public const int DefaultPaymentDueSoonReminderDays = 14;

    /// <summary>
    /// How many days after an invoice goes past due <c>DunningService</c> waits, with it still unpaid,
    /// before suspending the Organization (see <c>OrganizationLifecycleService.SetStatusAsync</c>).
    /// Measured from <c>Subscription.StatusChangedOn</c> - the moment the subscription was actually
    /// marked <see cref="Shared.DTOs.SubscriptionStatus.PastDue"/> - not from the invoice's own due
    /// date, so the "past due" notice's own promised countdown ("suspension in N days") is exactly this
    /// number regardless of how promptly the sweep caught the invoice going overdue.
    /// </summary>
    public const string SuspensionGraceDays = "SuspensionGraceDays";

    /// <summary>The value <see cref="SuspensionGraceDays"/> falls back to when unset or unparseable.</summary>
    public const int DefaultSuspensionGraceDays = 7;

    /// <summary>
    /// Where <c>NexusComplianceNotificationService</c> sends its daily digest of tenants whose invoices
    /// are blocked on a missing Stripe tax registration (see <c>Data.BlockedInvoiceIssuance</c>). Empty
    /// (the seeded default) means nobody has configured one yet - the digest is skipped entirely rather
    /// than sent nowhere. Deliberately a plain address, not "every Site Admin" - see that service's own
    /// remarks for why: nothing in this Api can resolve a Site Admin's user id to an email address at
    /// all (that data lives entirely in RustArchon.Panel's own Identity store), and a single
    /// distribution address Scott configures once is simpler anyway - it doesn't depend on who currently
    /// holds the role or whether they've ever logged in.
    /// </summary>
    public const string ComplianceNotificationEmail = "ComplianceNotificationEmail";

    /// <summary>
    /// The restricted Stripe API key this deployment collects payments through - see
    /// <c>StripeCredentialProvider</c>, the only place this is read (directly from Postgres and
    /// decrypted on the spot, never through <see cref="IPlatformSettingsCache"/> - see that class'
    /// remarks for why a Secret setting stays out of Valkey). Scoped to <c>Checkout Sessions: Write</c>
    /// and nothing else - see <c>StripeCheckoutService</c>'s remarks for why no broader scope is ever
    /// needed. Test-mode (<c>rk_test_...</c>) or live-mode (<c>rk_live_...</c>) depending on the
    /// deployment. Encrypted at rest - see <see cref="PlatformSettingValueType.Secret"/>.
    /// </summary>
    public const string StripeSecretKey = "StripeSecretKey";

    /// <summary>
    /// The signing secret Stripe issues for this deployment's webhook endpoint - what would verify an
    /// inbound payload genuinely came from Stripe before trusting anything in it. Not an API key; carries
    /// no ability to call Stripe's API at all. Encrypted at rest - see
    /// <see cref="PlatformSettingValueType.Secret"/>.
    /// </summary>
    public const string StripeWebhookSecret = "StripeWebhookSecret";

    /// <summary>
    /// Which email provider is in effect - a <see cref="PlatformSettingValueType.Choice"/> among
    /// <see cref="EmailProviders"/>. Replaces an earlier "infer it from which fields are filled in"
    /// design: that fell apart the moment a second API-based provider existed, since "is the API key
    /// set" no longer says *which* API it's for. This is also what
    /// <see cref="Data.PlatformSetting.VisibleWhenKey"/> on <see cref="EmailSmtpHost"/> and
    /// <see cref="EmailApiKey"/> both point at, to show only the fields the current choice uses.
    /// </summary>
    public const string EmailServiceProvider = "EmailServiceProvider";

    /// <summary>
    /// SMTP server hostname - only relevant, and only shown, while <see cref="EmailServiceProvider"/>
    /// is <see cref="EmailProviders.Smtp"/>.
    /// </summary>
    public const string EmailSmtpHost = "EmailSmtpHost";

    public const string EmailSmtpPort = "EmailSmtpPort";
    public const int DefaultEmailSmtpPort = 587;

    public const string EmailSmtpEnableSsl = "EmailSmtpEnableSsl";
    public const string EmailSmtpUsername = "EmailSmtpUsername";

    /// <summary>Encrypted at rest - see <see cref="Data.PlatformSettingValueType.Secret"/>.</summary>
    public const string EmailSmtpPassword = "EmailSmtpPassword";

    /// <summary>
    /// Which ticketing backend a submitted <see cref="Data.Ticket"/>'s events are mirrored to -
    /// site-owner-global, a <see cref="PlatformSettingValueType.Choice"/> among
    /// <see cref="TicketingProviders"/>. The internal ticket system (Admin/Tickets, My Tickets) is
    /// always the real place a ticket lives and gets answered regardless of this setting - see
    /// <see cref="TicketingWebhookUrl"/>'s remarks for what "Webhook" actually does and doesn't do.
    /// </summary>
    public const string TicketingProvider = "TicketingProvider";

    /// <summary>
    /// Where a signed ticket-event payload is POSTed when <see cref="TicketingProvider"/> is
    /// <see cref="TicketingProviders.Webhook"/>. Push-only, one-way notification - it lets the
    /// receiving system know a ticket was created or replied to, not a two-way sync; a reply typed on
    /// the far end never comes back into this system. Only shown while <see cref="TicketingProvider"/>
    /// is <see cref="TicketingProviders.Webhook"/>.
    /// </summary>
    public const string TicketingWebhookUrl = "TicketingWebhookUrl";

    /// <summary>
    /// The shared secret <c>TicketEventConsumer</c> HMAC-SHA256-signs each webhook payload with, so the
    /// receiving system can verify a delivery genuinely came from here. Encrypted at rest - see
    /// <see cref="Data.PlatformSettingValueType.Secret"/>. Only shown while <see cref="TicketingProvider"/>
    /// is <see cref="TicketingProviders.Webhook"/>.
    /// </summary>
    public const string TicketingWebhookSecret = "TicketingWebhookSecret";

    /// <summary>
    /// Which captcha vendor guards the anonymous public ticket-submission form - a
    /// <see cref="PlatformSettingValueType.Choice"/> among <see cref="CaptchaProviders"/>, default
    /// <see cref="CaptchaProviders.None"/> (no captcha - fine for local dev, not recommended once the
    /// form is public).
    /// </summary>
    public const string CaptchaProvider = "CaptchaProvider";

    /// <summary>The public site key the contact form's captcha widget renders with - safe to expose
    /// anonymously (see <see cref="Controllers.PublicTicketingConfigController"/>), unlike
    /// <see cref="CaptchaSecretKey"/>. Only shown while <see cref="CaptchaProvider"/> is not
    /// <see cref="CaptchaProviders.None"/>.</summary>
    public const string CaptchaSiteKey = "CaptchaSiteKey";

    /// <summary>The private key the Api calls the captcha vendor's verify endpoint with. Encrypted at
    /// rest - see <see cref="Data.PlatformSettingValueType.Secret"/>. Only shown while
    /// <see cref="CaptchaProvider"/> is not <see cref="CaptchaProviders.None"/>.</summary>
    public const string CaptchaSecretKey = "CaptchaSecretKey";

    /// <summary>
    /// The private half of this deployment's RustArchon-plugin signing key (RSA-2048, PKCS#8, base64), encrypted at
    /// rest - see <see cref="Data.PlatformSettingValueType.Secret"/>. <b>Generated automatically, once, the first
    /// time the plugin script is downloaded</b>; nothing else creates it. The public half is derived from it on demand
    /// rather than stored, so the two can never disagree. Replacing it strands every plugin already installed from
    /// this Panel: they trust the old key and will refuse anything signed by the new one.
    /// </summary>
    public const string PluginSigningKey = "PluginSigningKey";

    /// <summary>
    /// The site-wide switch for automatic plugin updates. On by default; each server also has to opt in itself. Turning this off stops every
    /// automatic update at once (a person can still press the buttons) - the emergency stop for a release that turns out to be bad.
    /// </summary>
    public const string PluginAutoUpdatesEnabled = "PluginAutoUpdatesEnabled";

    /// <summary>
    /// How many hours a new plugin or Updater version takes to become eligible for automatic installation on every server (a straight-line ramp: half
    /// the servers after half the time). Zero, the default, means everyone at once. A person pressing Update is never held back by it.
    /// </summary>
    public const string PluginRolloutHours = "PluginRolloutHours";

    /// <summary>The value <see cref="PluginRolloutHours"/> falls back to when unset or unparseable: no ramp.</summary>
    public const int DefaultPluginRolloutHours = 0;

    /// <summary>
    /// How many days the active plugin signing key may go without being rotated before the Panel reminds a site administrator to consider it.
    /// Zero turns the reminder off.
    /// </summary>
    public const string PluginKeyRotationReminderDays = "PluginKeyRotationReminderDays";

    /// <summary>The value <see cref="PluginKeyRotationReminderDays"/> falls back to when unset or unparseable.</summary>
    public const int DefaultPluginKeyRotationReminderDays = 365;

    /// <summary>
    /// How many in-game (F7) reports one server may file per minute. Applied only after the server's secret address has been checked, so
    /// nobody can spend a real server's allowance. Later this may become a per-server setting; for now it is one number for everyone.
    /// </summary>
    public const string ReportsPerServerPerMinute = "ReportsPerServerPerMinute";

    /// <summary>The value <see cref="ReportsPerServerPerMinute"/> falls back to when unset or unparseable.</summary>
    public const int DefaultReportsPerServerPerMinute = 60;

    /// <summary>
    /// How many report posts one network address may make to the Panel's public report address per minute, secret or no secret. A blunt
    /// first line of defence that keeps a flood from reaching the Api at all.
    /// </summary>
    public const string ReportsPerAddressPerMinute = "ReportsPerAddressPerMinute";

    /// <summary>The value <see cref="ReportsPerAddressPerMinute"/> falls back to when unset or unparseable.</summary>
    public const int DefaultReportsPerAddressPerMinute = 120;

    /// <summary>
    /// How many "Verify" checks of a third-party key (geolocation, VPN, Steam) one person may run per minute. Each one makes the Api call the
    /// provider on their behalf, so the limit keeps the button from being a free key-testing service.
    /// </summary>
    public const string IntegrationChecksPerUserPerMinute = "IntegrationChecksPerUserPerMinute";

    /// <summary>The value <see cref="IntegrationChecksPerUserPerMinute"/> falls back to when unset or unparseable.</summary>
    public const int DefaultIntegrationChecksPerUserPerMinute = 20;

    /// <summary>
    /// Encrypted at rest - see <see cref="Data.PlatformSettingValueType.Secret"/>. Shown only while
    /// <see cref="EmailServiceProvider"/> is anything other than <see cref="EmailProviders.Smtp"/>.
    /// </summary>
    public const string EmailApiKey = "EmailApiKey";

    /// <summary>The "From" address on every email RustArchon sends, test or otherwise.</summary>
    public const string EmailDefaultFromAddress = "EmailDefaultFromAddress";

    public const string EmailDefaultFromName = "EmailDefaultFromName";

    /// <summary>
    /// Which culture (e.g. <c>"en-US"</c>) an email falls back to when the recipient's own preferred
    /// culture has no <c>EmailTemplateTranslation</c> - see <c>CommunicationPublisher.ResolveTranslation</c>,
    /// the only place this is read. Deliberately a plain <see cref="PlatformSettingValueType.String"/>,
    /// not a <see cref="PlatformSettingValueType.Choice"/>: the set of valid cultures is whichever
    /// <c>Resources/SharedResource.*.json</c> files RustArchon.Panel happens to be compiled with, and
    /// this Api has no visibility into that list - RustArchon.Panel's own <c>PlatformSettings.razor</c>
    /// special-cases this key to render a dropdown built from its own
    /// <c>IOptions&lt;RequestLocalizationOptions&gt;.SupportedUICultures</c> instead of a free-text box.
    /// Empty (the seeded default) behaves the same as <see cref="EmailTemplateRegistry.SeedCulture"/>,
    /// since the fallback chain reaches that rung right after this one anyway.
    /// </summary>
    public const string DefaultCulture = "DefaultCulture";

    public static async Task EnsureDefaultsAsync(ApiDbContext dbContext, IConfiguration configuration, ILogger logger)
    {
        // A deployment that already had RUSTARCHON_INVITATION_CODES_ENABLED set keeps that exact
        // value as this setting's seeded starting point, the one time this row is created - the env
        // var is never consulted again after that. Defaults to true (fail closed) if neither this row
        // nor the legacy env var exist yet, matching InvitationCodeOptions's old default.
        var legacyEnvDefault = configuration.GetValue<bool?>("RUSTARCHON_INVITATION_CODES_ENABLED") ?? true;

        await EnsureSettingAsync(
            dbContext,
            key: SiteName,
            category: Categories.General,
            order: 10,
            displayName: "Site name",
            description: "Shown in the Panel's nav bar, every page title, and available to every " +
                "email as the {{SiteName}} placeholder.",
            valueType: PlatformSettingValueType.String,
            defaultValue: DefaultSiteName,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: SiteUrl,
            category: Categories.General,
            order: 20,
            displayName: "Site URL",
            description: "The public web address the Panel's nav brand links to, and available to " +
                "every email as the {{SiteUrl}} placeholder.",
            valueType: PlatformSettingValueType.String,
            defaultValue: DefaultSiteUrl,
            logger: logger);

        // See PanelBaseUrl's own remarks - this deployment's existing CorsSettings:BlazorServerUrl value
        // becomes this row's starting point the one time it's created, the same one-time-seed pattern
        // legacyEnvDefault below uses for RUSTARCHON_INVITATION_CODES_ENABLED.
        var panelBaseUrlEnvDefault =
            configuration["CorsSettings:BlazorServerUrl"] ?? DefaultPanelBaseUrl;

        await EnsureSettingAsync(
            dbContext,
            key: PanelBaseUrl,
            category: Categories.General,
            order: 30,
            displayName: "Panel base URL",
            description: "This deployment's own Panel address - where Stripe Checkout sends the " +
                "browser back to once payment completes or is abandoned.",
            valueType: PlatformSettingValueType.String,
            defaultValue: panelBaseUrlEnvDefault,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: InvitationCodesEnabled,
            category: Categories.Registration,
            order: 10,
            displayName: "Require invitation codes to register",
            description: "When enabled, a valid invitation code is required to create an account. " +
                "Disable once you're ready to open registration to everyone.",
            valueType: PlatformSettingValueType.Boolean,
            defaultValue: legacyEnvDefault ? "true" : "false",
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: DefaultPlanId,
            category: Categories.Registration,
            order: 20,
            displayName: "Default plan for new sign-ups",
            description: "Which plan a brand-new Organization starts on. Leave unset to always use " +
                "whichever active plan is currently cheapest.",
            valueType: PlatformSettingValueType.PlanReference,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PaymentTermsDays,
            category: Categories.Billing,
            order: 10,
            displayName: "Payment terms (days)",
            description: "How long after an invoice is issued it falls due. Everything a receivables " +
                "report calls overdue is measured from this.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultPaymentTermsDays.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PaymentDueSoonReminderDays,
            category: Categories.Billing,
            order: 20,
            displayName: "Payment due soon reminder (days before due)",
            description: "How many days before an invoice's due date to send a heads-up that payment " +
                "is coming due.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultPaymentDueSoonReminderDays.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: SuspensionGraceDays,
            category: Categories.Billing,
            order: 30,
            displayName: "Suspension grace period (days)",
            description: "How many days an Organization stays past due, still unpaid, before its " +
                "servers are suspended.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultSuspensionGraceDays.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: ComplianceNotificationEmail,
            category: Categories.Billing,
            order: 40,
            displayName: "Tax compliance notification address",
            description: "Where to send the daily digest of jurisdictions blocking an invoice for lack " +
                "of a Stripe tax registration - see the Tax Registrations dashboard at " +
                "dashboard.stripe.com/tax/locations. Leave blank to skip the digest entirely.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: StripeSecretKey,
            category: Categories.Payments,
            order: 10,
            displayName: "Stripe secret key",
            description: "The restricted API key (Checkout Sessions: Write) Stripe checkout runs " +
                "under - rk_test_... or rk_live_... depending on the deployment. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: StripeWebhookSecret,
            category: Categories.Payments,
            order: 20,
            displayName: "Stripe webhook signing secret",
            description: "The signing secret Stripe issues for this deployment's webhook endpoint, " +
                "used to verify an inbound payload genuinely came from Stripe. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailDefaultFromAddress,
            category: Categories.Email,
            order: 10,
            displayName: "From address",
            description: "The sender address on every email RustArchon sends, including test sends " +
                "from this page.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailDefaultFromName,
            category: Categories.Email,
            order: 20,
            displayName: "From name",
            description: "The sender display name alongside the From address, e.g. \"RustArchon\".",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: DefaultCulture,
            category: Categories.Email,
            order: 25,
            displayName: "Default language",
            description: "Which language an email falls back to when the recipient's own preferred " +
                "language has no translation for the template being sent.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailServiceProvider,
            category: Categories.Email,
            order: 30,
            displayName: "Email service provider",
            description: "Which of the settings below is actually used to send email.",
            valueType: PlatformSettingValueType.Choice,
            options: $"{EmailProviders.Smtp},{EmailProviders.Resend},{EmailProviders.SendGrid}",
            defaultValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailApiKey,
            category: Categories.Email,
            order: 40,
            displayName: "API key",
            description: "The API key for whichever provider is selected above. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            visibleWhenNegate: true,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailSmtpHost,
            category: Categories.Email,
            order: 50,
            displayName: "SMTP host",
            description: "The mail server to connect to.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailSmtpPort,
            category: Categories.Email,
            order: 60,
            displayName: "SMTP port",
            description: "587 for STARTTLS (the usual choice), 465 for implicit TLS, 25 for unencrypted.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultEmailSmtpPort.ToString(),
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailSmtpEnableSsl,
            category: Categories.Email,
            order: 70,
            displayName: "SMTP uses TLS",
            description: "Disable only for a host that genuinely has no TLS support - nearly every " +
                "real mail provider requires this on.",
            valueType: PlatformSettingValueType.Boolean,
            defaultValue: "true",
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailSmtpUsername,
            category: Categories.Email,
            order: 80,
            displayName: "SMTP username",
            description: "Usually the full mailbox address.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: EmailSmtpPassword,
            category: Categories.Email,
            order: 90,
            displayName: "SMTP password",
            description: "Encrypted at rest - never shown here again once set.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            visibleWhenKey: EmailServiceProvider,
            visibleWhenValue: EmailProviders.Smtp,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: TicketingProvider,
            category: Categories.Ticketing,
            order: 10,
            displayName: "Ticketing provider",
            description: "Where support ticket events are mirrored. The internal ticket system " +
                "always stays the real place a ticket is answered - Webhook only sends a one-way " +
                "notification elsewhere, it doesn't hand the conversation off.",
            valueType: PlatformSettingValueType.Choice,
            options: $"{TicketingProviders.Internal},{TicketingProviders.Webhook}",
            defaultValue: TicketingProviders.Internal,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: TicketingWebhookUrl,
            category: Categories.Ticketing,
            order: 20,
            displayName: "Webhook URL",
            description: "Where a signed ticket-event payload is POSTed.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            visibleWhenKey: TicketingProvider,
            visibleWhenValue: TicketingProviders.Webhook,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: TicketingWebhookSecret,
            category: Categories.Ticketing,
            order: 30,
            displayName: "Webhook signing secret",
            description: "Signs each payload (HMAC-SHA256) so the receiving system can verify it " +
                "genuinely came from here. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            visibleWhenKey: TicketingProvider,
            visibleWhenValue: TicketingProviders.Webhook,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: CaptchaProvider,
            category: Categories.Ticketing,
            order: 40,
            displayName: "Captcha provider",
            description: "Guards the public contact form's ticket submission against bots. None is " +
                "fine for local development; leaving it None on a publicly-reachable deployment " +
                "invites spam.",
            valueType: PlatformSettingValueType.Choice,
            options: $"{CaptchaProviders.None},{CaptchaProviders.ReCaptcha},{CaptchaProviders.Turnstile}",
            defaultValue: CaptchaProviders.None,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: CaptchaSiteKey,
            category: Categories.Ticketing,
            order: 50,
            displayName: "Captcha site key",
            description: "The public key the contact form's captcha widget renders with.",
            valueType: PlatformSettingValueType.String,
            defaultValue: string.Empty,
            visibleWhenKey: CaptchaProvider,
            visibleWhenValue: CaptchaProviders.None,
            visibleWhenNegate: true,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: CaptchaSecretKey,
            category: Categories.Ticketing,
            order: 60,
            displayName: "Captcha secret key",
            description: "The private key the Api verifies a submitted captcha token with. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            visibleWhenKey: CaptchaProvider,
            visibleWhenValue: CaptchaProviders.None,
            visibleWhenNegate: true,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PluginSigningKey,
            category: Categories.Plugin,
            order: 10,
            displayName: "Plugin signing key (private)",
            description: "Signs the RustArchon server plugin this Panel serves, and the updates it sends. Generated " +
                "automatically the first time the plugin is downloaded - leave it empty. DO NOT REPLACE OR CLEAR IT " +
                "once plugins are installed: every installed plugin trusts the key it was downloaded with and will " +
                "refuse anything signed by another. Encrypted at rest.",
            valueType: PlatformSettingValueType.Secret,
            defaultValue: string.Empty,
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PluginAutoUpdatesEnabled,
            category: Categories.Plugin,
            order: 20,
            displayName: "Automatic plugin updates",
            description: "Lets servers that have turned on \"Update automatically\" receive the plugin and Updater versions this Panel serves without " +
                "anyone pressing a button. Turn this off to stop every automatic update at once, for example if a release turns out to be bad " +
                "(you can also withdraw the release). Administrators can still update a server by hand.",
            valueType: PlatformSettingValueType.Boolean,
            defaultValue: "true",
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PluginRolloutHours,
            category: Categories.Plugin,
            order: 30,
            displayName: "Plugin roll-out time (hours)",
            description: "How long a newly published plugin or Updater version takes to reach every server that updates automatically. The servers " +
                "come in gradually over this time - half of them after half of it - so a version with a problem is noticed on a few servers before " +
                "it is on all of them (withdraw the release or turn off Automatic plugin updates to stop it). Zero, the default, means every " +
                "server at once. Someone pressing Update on a server is never held back.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultPluginRolloutHours.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: PluginKeyRotationReminderDays,
            category: Categories.Plugin,
            order: 40,
            displayName: "Signing key rotation reminder (days)",
            description: "After the active plugin signing key has gone this many days without being rotated, site administrators see a reminder to " +
                "consider rotating it. Rotating is safe (servers on the old key are moved to the new one in a single update) but is never done for you. " +
                "Zero turns the reminder off.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultPluginKeyRotationReminderDays.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: ReportsPerServerPerMinute,
            category: Categories.Reports,
            order: 10,
            displayName: "Reports per server per minute",
            description: "How many in-game (F7) reports one game server may file each minute. A real server files a handful at the very most; " +
                "anything past this is refused until the minute is up. Counted only for servers that present their own secret address.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultReportsPerServerPerMinute.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: ReportsPerAddressPerMinute,
            category: Categories.Reports,
            order: 20,
            displayName: "Report posts per network address per minute",
            description: "How many posts to the public report address one network address may make each minute, whether or not the secret is " +
                "right. Keeps a flood from reaching the Api. Several game servers behind one address share this, so keep it comfortably above " +
                "the per-server limit times the number of servers a host runs. The Panel picks up a change within a minute.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultReportsPerAddressPerMinute.ToString(),
            logger: logger);

        await EnsureSettingAsync(
            dbContext,
            key: IntegrationChecksPerUserPerMinute,
            category: Categories.Reports,
            order: 30,
            displayName: "Key checks per person per minute",
            description: "How many times one person may press \"Verify\" on a geolocation, VPN or Steam key each minute. Every check makes this " +
                "platform call the provider on their behalf. A person clicking a button never gets near the default; it is a guard against a script.",
            valueType: PlatformSettingValueType.Integer,
            defaultValue: DefaultIntegrationChecksPerUserPerMinute.ToString(),
            logger: logger);
    }

    private static async Task EnsureSettingAsync(
        ApiDbContext dbContext,
        string key,
        string category,
        int order,
        string displayName,
        string description,
        PlatformSettingValueType valueType,
        string defaultValue,
        ILogger logger,
        string? options = null,
        string? visibleWhenKey = null,
        string? visibleWhenValue = null,
        bool visibleWhenNegate = false)
    {
        var existing = await dbContext.Set<PlatformSetting>().FirstOrDefaultAsync(s => s.Key == key);

        if (existing is not null)
        {
            // Metadata only - Value is the admin's own and never touched here. See this class's own
            // remarks for why syncing the rest on every startup, not just the first, is deliberate.
            existing.Category = category;
            existing.Order = order;
            existing.DisplayName = displayName;
            existing.Description = description;
            existing.ValueType = valueType;
            existing.Options = options;
            existing.VisibleWhenKey = visibleWhenKey;
            existing.VisibleWhenValue = visibleWhenValue;
            existing.VisibleWhenNegate = visibleWhenNegate;
            await dbContext.SaveChangesAsync();
            return;
        }

        dbContext.Set<PlatformSetting>().Add(new PlatformSetting
        {
            Key = key,
            Category = category,
            Order = order,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            Options = options,
            VisibleWhenKey = visibleWhenKey,
            VisibleWhenValue = visibleWhenValue,
            VisibleWhenNegate = visibleWhenNegate,
            Value = defaultValue,
            CreatedById = Guid.Empty,
            CreatedOn = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Seeded platform setting '{Key}' = '{Value}'.", key, defaultValue);
    }
}
