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
        public const string Email = "Email";
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
