// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Services;

/// <summary>What is wrong with an announcement's words, in a form a caller can show. Empty when there is nothing wrong.</summary>
public sealed record AnnouncementProblem(string Code, string Message);

/// <summary>An announcement's words after they have been checked: per language, ready to be filled in for each recipient.</summary>
public sealed record PreparedAnnouncement(
    AnnouncementLanguageMode Mode, string? SingleCulture, string DefaultCulture, IReadOnlyDictionary<string, PreparedVersion> Versions);

/// <summary>One language's version: the subject as one line, and the body as finished email markup - both still carrying their <c>{{Token}}</c>s.</summary>
public sealed record PreparedVersion(string Culture, string Subject, string BodyHtml);

/// <summary>An organization an announcement would reach.</summary>
public sealed record AnnouncementRecipient(Guid TenantId, string OrganizationName, string Email, Guid PlanId, string PlanName, string? Culture);

/// <summary>Who an announcement would reach, and who was left out and why.</summary>
public sealed record AnnouncementAudience(
    IReadOnlyList<AnnouncementRecipient> Recipients, IReadOnlyDictionary<string, int> LeftOut);

public interface IPlanAnnouncementService
{
    /// <summary>The organizations on the plan (and its newer versions, if asked), narrowed as asked. <c>null</c> if there is no such plan.</summary>
    Task<AnnouncementAudience?> ResolveAudienceAsync(Guid planId, AnnouncementCriteriaDto criteria, CancellationToken cancellationToken = default);

    Task<AnnouncementPreviewDto?> PreviewAsync(Guid planId, AnnouncementCriteriaDto criteria, CancellationToken cancellationToken = default);

    /// <summary>Checks the words and prepares them, or says what is wrong: a missing default-language version, an empty subject or body, an unknown token.</summary>
    Task<(PreparedAnnouncement? Prepared, AnnouncementProblem? Problem)> PrepareAsync(AnnouncementContentDto content, CancellationToken cancellationToken = default);

    /// <summary>Queues one email to each organization the criteria reach. <c>Conflict</c> means the audience is not the size the admin confirmed.</summary>
    Task<(AnnouncementResultDto? Result, AnnouncementProblem? Problem)> SendAsync(
        Guid planId, SendPlanAnnouncementDto request, Guid? sentBy, CancellationToken cancellationToken = default);

    /// <summary>Sends each language's version, once, to one address, so the admin can see how it will look. Nothing is recorded against any organization.</summary>
    Task<(int Queued, AnnouncementProblem? Problem)> SendTestAsync(Guid planId, SendAnnouncementTestDto request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnnouncementBatchDto>> HistoryAsync(Guid planId, int limit, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Who:</b> the organizations with an open subscription on the plan - and, if asked, on every plan that superseded it, following the version link forward -
/// that are in good standing (Active, and Past Due only if asked, on an active organization) and have a contact email. The rest are counted and reported, not
/// silently dropped.
/// </para>
/// <para>
/// <b>In whose language:</b> an organization has no language of its own; the person who created it does, in their profile (see <see cref="UserProfile"/>). An
/// organization's language is that person's, or - when they have set none - the platform's default. A per-language send gives each organization its language's
/// version (exact language, then the language without its region, then the default language's); a single-language send ignores all of this.
/// </para>
/// <para>
/// <b>What is sent:</b> one email per organization through the ordinary communications pipeline, so each is recorded with exactly what that organization
/// received, can be cancelled while still queued, and shows its delivery status. The admin's own text is filled in per recipient (their organization's name,
/// the plan's name, the site's name) before it goes into the template, whose only content is the two slots for it.
/// </para>
/// </remarks>
public class PlanAnnouncementService(
    ApiDbContext context, ICommunicationPublisher publisher, IUserProfileStore profiles, IPlatformSettingsCache settings, TimeProvider clock,
    ILogger<PlanAnnouncementService> logger) : IPlanAnnouncementService
{
    public const string PastDueLeftOut = "past_due_left_out";
    public const string NotInGoodStanding = "not_in_good_standing";
    public const string NoContactEmail = "no_contact_email";

    /// <summary>The most a send may reach - not a limit anyone should meet, but a guard against a request that would queue an unbounded number of emails.</summary>
    public const int MaxRecipients = 5_000;

    private static readonly IReadOnlySet<string> RawTokens = new HashSet<string>(StringComparer.Ordinal) { EmailTemplateRegistry.Placeholders.AnnouncementBody };

    public async Task<AnnouncementAudience?> ResolveAudienceAsync(Guid planId, AnnouncementCriteriaDto criteria, CancellationToken cancellationToken = default)
    {
        var chain = await ChainAsync(planId, criteria.IncludeSupersedingVersions, cancellationToken);
        if (chain is null)
        {
            return null;
        }

        // Not tenant-filtered: this is a platform action across every organization on the plan.
        var subscriptions = await context.Set<Subscription>().IgnoreQueryFilters().AsNoTracking()
            .Include(s => s.Tenant)
            .Where(s => chain.Keys.Contains(s.PlanId) && s.EndDate == null)
            .ToListAsync(cancellationToken);

        var creators = subscriptions.Select(s => s.Tenant.CreatedById).Where(id => id != Guid.Empty).Distinct().ToList();
        var creatorProfiles = await profiles.FindManyAsync(creators, cancellationToken);

        var recipients = new List<AnnouncementRecipient>();
        var leftOut = new Dictionary<string, int>();
        void Skip(string reason) => leftOut[reason] = leftOut.GetValueOrDefault(reason) + 1;

        foreach (var subscription in subscriptions.OrderBy(s => s.Tenant.Name, StringComparer.OrdinalIgnoreCase))
        {
            var tenant = subscription.Tenant;
            if (subscription.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled || !tenant.IsActive)
            {
                Skip(NotInGoodStanding);
            }
            else if (subscription.Status == SubscriptionStatus.PastDue && !criteria.IncludePastDue)
            {
                Skip(PastDueLeftOut);
            }
            else if (!IsUsableAddress(tenant.ContactEmail))
            {
                Skip(NoContactEmail);
            }
            else
            {
                var culture = creatorProfiles.GetValueOrDefault(tenant.CreatedById)?.PreferredCulture;
                recipients.Add(new AnnouncementRecipient(tenant.Id, tenant.Name, tenant.ContactEmail!.Trim(), subscription.PlanId, chain[subscription.PlanId], culture));
            }
        }

        return new AnnouncementAudience(recipients, leftOut);
    }

    public async Task<AnnouncementPreviewDto?> PreviewAsync(Guid planId, AnnouncementCriteriaDto criteria, CancellationToken cancellationToken = default)
    {
        var audience = await ResolveAudienceAsync(planId, criteria, cancellationToken);
        if (audience is null)
        {
            return null;
        }

        return new AnnouncementPreviewDto
        {
            Recipients = audience.Recipients.Count,
            DefaultCulture = await DefaultCultureAsync(),
            ByPlanVersion = audience.Recipients.GroupBy(r => (r.PlanId, r.PlanName))
                .Select(g => new AnnouncementCountDto { Key = g.Key.PlanId.ToString(), Name = g.Key.PlanName, Count = g.Count() })
                .OrderByDescending(c => c.Count).ToList(),
            ByLanguage = audience.Recipients.GroupBy(r => r.Culture ?? string.Empty)
                .Select(g => new AnnouncementCountDto { Key = g.Key, Name = g.Key, Count = g.Count() })
                .OrderByDescending(c => c.Count).ToList(),
            LeftOut = Reasons(audience.LeftOut)
        };
    }

    // ---- the words ---------------------------------------------------------------------------------------------------------------

    public async Task<(PreparedAnnouncement? Prepared, AnnouncementProblem? Problem)> PrepareAsync(AnnouncementContentDto content, CancellationToken cancellationToken = default)
    {
        var defaultCulture = await DefaultCultureAsync();
        var versions = new Dictionary<string, PreparedVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in content.Versions)
        {
            string culture;
            try
            {
                culture = UserProfileStore.Normalize(version.Culture) ?? throw new ArgumentException("A language is needed.");
            }
            catch (ArgumentException)
            {
                return (null, new AnnouncementProblem("bad_culture", $"\"{version.Culture}\" is not a language name such as en-US."));
            }

            if (versions.ContainsKey(culture))
            {
                return (null, new AnnouncementProblem("duplicate_culture", $"There are two versions for {culture}."));
            }

            var subject = AnnouncementBody.CleanSubject(version.Subject);
            if (subject.Length == 0)
            {
                return (null, new AnnouncementProblem("empty_subject", $"The {culture} version needs a subject."));
            }

            if (subject.Length > AnnouncementBody.MaxSubjectLength)
            {
                return (null, new AnnouncementProblem("subject_too_long", $"The {culture} subject is longer than {AnnouncementBody.MaxSubjectLength} characters."));
            }

            string body;
            string plain;
            try
            {
                body = AnnouncementBody.ToHtml(version.BodyDelta);
                plain = AnnouncementBody.PlainText(version.BodyDelta);
            }
            catch (FormatException ex)
            {
                return (null, new AnnouncementProblem("bad_body", $"The {culture} message could not be read: {ex.Message}"));
            }

            if (!AnnouncementBody.HasContent(body))
            {
                return (null, new AnnouncementProblem("empty_body", $"The {culture} version needs a message."));
            }

            var unknown = AnnouncementBody.UnknownTokens(subject).Concat(AnnouncementBody.UnknownTokens(plain)).Distinct().ToList();
            if (unknown.Count > 0)
            {
                return (null, new AnnouncementProblem(
                    "unknown_token",
                    $"The {culture} version uses {string.Join(", ", unknown.Select(t => "{{" + t + "}}"))}, which has no value to put in. You can use "
                    + string.Join(", ", AnnouncementBody.AllowedTokens.Order().Select(t => "{{" + t + "}}")) + "."));
            }

            versions[culture] = new PreparedVersion(culture, subject, body);
        }

        if (versions.Count == 0)
        {
            return (null, new AnnouncementProblem("no_versions", "There is no message to send."));
        }

        if (content.Mode == AnnouncementLanguageMode.SingleLanguage)
        {
            var single = UserProfileStore.NormalizeOrNull(content.SingleCulture);
            if (single is null || !versions.ContainsKey(single))
            {
                return (null, new AnnouncementProblem("single_culture_missing", "Choose which language's message everyone gets."));
            }

            return (new PreparedAnnouncement(content.Mode, single, defaultCulture, versions.ToDictionary(v => v.Key, v => v.Value)), null);
        }

        // A per-language send has to have something for an organization whose language nobody wrote a version for: the default language's.
        if (Pick(versions, defaultCulture, defaultCulture) is null)
        {
            return (null, new AnnouncementProblem(
                "default_culture_missing", $"A version in the default language ({defaultCulture}) is required, for organizations whose language has no version."));
        }

        return (new PreparedAnnouncement(content.Mode, null, defaultCulture, versions.ToDictionary(v => v.Key, v => v.Value)), null);
    }

    /// <summary>
    /// The version an organization gets: its language exactly, then that language without its region (<c>en-GB</c> gets <c>en</c>), then the default language
    /// (and that without its region). <c>null</c> only if none of those has a version.
    /// </summary>
    public static PreparedVersion? Pick(IReadOnlyDictionary<string, PreparedVersion> versions, string? culture, string defaultCulture)
    {
        PreparedVersion? Exact(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null : versions.FirstOrDefault(v => string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        string? Parent(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var dash = name.IndexOf('-');
            return dash > 0 ? name[..dash] : null;
        }

        return Exact(culture) ?? Exact(Parent(culture)) ?? Exact(defaultCulture) ?? Exact(Parent(defaultCulture));
    }

    // ---- sending -----------------------------------------------------------------------------------------------------------------

    public async Task<(AnnouncementResultDto? Result, AnnouncementProblem? Problem)> SendAsync(
        Guid planId, SendPlanAnnouncementDto request, Guid? sentBy, CancellationToken cancellationToken = default)
    {
        var (prepared, problem) = await PrepareAsync(request.Content, cancellationToken);
        if (prepared is null)
        {
            return (null, problem);
        }

        var audience = await ResolveAudienceAsync(planId, request.Criteria, cancellationToken);
        if (audience is null)
        {
            return (null, new AnnouncementProblem("no_such_plan", "There is no such plan."));
        }

        if (audience.Recipients.Count == 0)
        {
            return (null, new AnnouncementProblem("no_recipients", "No organization matches, so there is nobody to send it to."));
        }

        if (audience.Recipients.Count > MaxRecipients)
        {
            return (null, new AnnouncementProblem("too_many_recipients", $"That is more than {MaxRecipients} organizations; narrow it down."));
        }

        if (audience.Recipients.Count != request.ExpectedRecipients)
        {
            return (null, new AnnouncementProblem(
                "audience_changed",
                $"You confirmed {request.ExpectedRecipients} organization(s), but {audience.Recipients.Count} match now. Nothing was sent; look again and confirm."));
        }

        var now = clock.GetUtcNow();
        var firstVersion = prepared.Mode == AnnouncementLanguageMode.SingleLanguage
            ? prepared.Versions[prepared.SingleCulture!]
            : Pick(prepared.Versions, prepared.DefaultCulture, prepared.DefaultCulture)!;

        var batch = new CommunicationBatch
        {
            Kind = CommunicationBatch.Kinds.PlanAnnouncement,
            PlanId = planId,
            IncludeSupersedingVersions = request.Criteria.IncludeSupersedingVersions,
            IncludePastDue = request.Criteria.IncludePastDue,
            LanguageMode = prepared.Mode.ToString(),
            SingleCulture = prepared.SingleCulture,
            Subject = Limit(firstVersion.Subject, 300),
            SentById = sentBy,
            SentOn = now,
            RecipientCount = 0,
            SkippedCount = audience.LeftOut.Values.Sum()
        };
        context.Set<CommunicationBatch>().Add(batch);
        await context.SaveChangesAsync(cancellationToken);

        var siteName = await SiteNameAsync();
        var queued = 0;
        foreach (var recipient in audience.Recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = prepared.Mode == AnnouncementLanguageMode.SingleLanguage
                ? prepared.Versions[prepared.SingleCulture!]
                : Pick(prepared.Versions, recipient.Culture, prepared.DefaultCulture)!;

            try
            {
                await QueueOneAsync(version, recipient.OrganizationName, recipient.PlanName, siteName, recipient.Email, recipient.TenantId, batch.Id, cancellationToken);
                queued++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One organization's email failing to queue is not a reason to abandon the rest, or to leave the admin thinking nothing went out.
                logger.LogError(ex, "Could not queue the announcement for organization {TenantId}.", recipient.TenantId);
            }
        }

        batch.RecipientCount = queued;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Announcement '{Subject}' queued for {Queued} of {Matched} organization(s) on plan {PlanId} (batch {BatchId}) by {UserId}.",
            batch.Subject, queued, audience.Recipients.Count, planId, batch.Id, sentBy);

        var result = new AnnouncementResultDto { BatchId = batch.Id, Queued = queued, LeftOut = Reasons(audience.LeftOut) };
        if (queued < audience.Recipients.Count)
        {
            result.LeftOut.Add(new PlanMoveReasonDto
            {
                Code = "could_not_queue", Message = "the email could not be queued for this organization", Count = audience.Recipients.Count - queued
            });
        }

        return (result, null);
    }

    public async Task<(int Queued, AnnouncementProblem? Problem)> SendTestAsync(Guid planId, SendAnnouncementTestDto request, CancellationToken cancellationToken = default)
    {
        var (prepared, problem) = await PrepareAsync(request.Content, cancellationToken);
        if (prepared is null)
        {
            return (0, problem);
        }

        var plan = await context.Set<Plan>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return (0, new AnnouncementProblem("no_such_plan", "There is no such plan."));
        }

        var siteName = await SiteNameAsync();
        var versions = prepared.Mode == AnnouncementLanguageMode.SingleLanguage ? [prepared.Versions[prepared.SingleCulture!]] : prepared.Versions.Values.ToList();
        foreach (var version in versions)
        {
            await QueueOneAsync(
                version with { Subject = "[Test] " + version.Subject }, "Example Organization", plan.Name, siteName, request.ToAddress.Trim(), tenantId: null, batchId: null,
                cancellationToken);
        }

        return (versions.Count, null);
    }

    public async Task<IReadOnlyList<AnnouncementBatchDto>> HistoryAsync(Guid planId, int limit, CancellationToken cancellationToken = default) =>
        await context.Set<CommunicationBatch>().AsNoTracking()
            .Where(b => b.PlanId == planId && b.Kind == CommunicationBatch.Kinds.PlanAnnouncement)
            .OrderByDescending(b => b.SentOn).Take(Math.Clamp(limit, 1, 100))
            .Select(b => new AnnouncementBatchDto
            {
                Id = b.Id, SentOn = b.SentOn, Subject = b.Subject, Recipients = b.RecipientCount, LeftOut = b.SkippedCount,
                IncludeSupersedingVersions = b.IncludeSupersedingVersions, IncludePastDue = b.IncludePastDue, LanguageMode = b.LanguageMode
            })
            .ToListAsync(cancellationToken);

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills in the admin's own tokens for this recipient, then hands the finished subject and body to the template as its two slots. The body's values are
    /// HTML-encoded as they go in (an organization's name is not trusted markup); the subject is a plain header.
    /// </summary>
    private Task QueueOneAsync(PreparedVersion version, string organizationName, string planName, string siteName, string to, Guid? tenantId, Guid? batchId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["OrganizationName"] = AnnouncementBody.CleanSubject(organizationName),
            ["PlanName"] = AnnouncementBody.CleanSubject(planName),
            ["SiteName"] = AnnouncementBody.CleanSubject(siteName)
        };
        var (subject, body) = EmailTemplateRenderer.Render(version.Subject, version.BodyHtml, values);

        return publisher.QueueTemplatedWithMarkupAsync(
            EmailTemplateRegistry.Codes.PlanAnnouncement,
            new Dictionary<string, string>
            {
                [EmailTemplateRegistry.Placeholders.AnnouncementSubject] = AnnouncementBody.CleanSubject(subject),
                [EmailTemplateRegistry.Placeholders.AnnouncementBody] = body
            },
            RawTokens, to, userId: null, tenantId, culture: version.Culture, batchId, cancellationToken);
    }

    /// <summary>The plan and, if asked, every plan after it in the chain of versions, with their names. <c>null</c> if the plan does not exist.</summary>
    private async Task<Dictionary<Guid, string>?> ChainAsync(Guid planId, bool includeSuperseding, CancellationToken cancellationToken)
    {
        var chain = new Dictionary<Guid, string>();
        var nextId = (Guid?)planId;
        while (nextId is { } id && !chain.ContainsKey(id))
        {
            var plan = await context.Set<Plan>().AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Name, p.SupersededByPlanId }).FirstOrDefaultAsync(cancellationToken);
            if (plan is null)
            {
                break;
            }

            chain[id] = plan.Name;
            nextId = includeSuperseding ? plan.SupersededByPlanId : null;
        }

        return chain.Count == 0 ? null : chain;
    }

    private async Task<string> DefaultCultureAsync()
    {
        var configured = await settings.GetStringAsync(PlatformSettingsRegistry.DefaultCulture);
        return UserProfileStore.NormalizeOrNull(configured) ?? EmailTemplateRegistry.SeedCulture;
    }

    private async Task<string> SiteNameAsync()
    {
        var name = await settings.GetStringAsync(PlatformSettingsRegistry.SiteName);
        return string.IsNullOrWhiteSpace(name) ? PlatformSettingsRegistry.DefaultSiteName : name;
    }

    // A plain address (not "Name <address>"), with a real domain: the parser accepts "a@b", which is a valid address but not one mail can be sent to.
    private static bool IsUsableAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.Length <= 320 && MailAddress.TryCreate(address.Trim(), out var parsed)
        && parsed.Address == address.Trim() && parsed.Host.Contains('.');

    private static List<PlanMoveReasonDto> Reasons(IReadOnlyDictionary<string, int> leftOut) =>
        leftOut.Where(l => l.Value > 0).OrderByDescending(l => l.Value).ThenBy(l => l.Key)
            .Select(l => new PlanMoveReasonDto { Code = l.Key, Message = Describe(l.Key), Count = l.Value }).ToList();

    private static string Describe(string code) => code switch
    {
        PastDueLeftOut => "they have an overdue invoice, and past-due organizations were not included",
        NotInGoodStanding => "their subscription is suspended or cancelled, or the organization is inactive",
        NoContactEmail => "the organization has no usable contact email",
        _ => code
    };

    private static string Limit(string text, int max) => text.Length <= max ? text : text[..max];
}
