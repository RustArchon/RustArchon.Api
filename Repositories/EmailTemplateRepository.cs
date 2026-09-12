// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository implementation for <see cref="EmailTemplate"/> entities.
/// </summary>
public class EmailTemplateRepository(ApiDbContext context, IUserContext? userContext = null)
    : Repository<EmailTemplate>(context, userContext), IEmailTemplateRepository
{
    /// <inheritdoc />
    public Task<EmailTemplate?> GetByCodeAsync(string code) =>
        _dbSet.Include(t => t.Placeholders).Include(t => t.Translations).FirstOrDefaultAsync(t => t.Code == code);

    /// <inheritdoc />
    public override async Task<IEnumerable<EmailTemplate>> GetAllAsync() =>
        await _dbSet.Include(t => t.Placeholders).Include(t => t.Translations).ToListAsync();

    /// <inheritdoc />
    public async Task<EmailTemplate> SetPlaceholdersAsync(
        EmailTemplate template, IReadOnlyList<EmailPlaceholder> placeholders)
    {
        template.Placeholders.Clear();

        foreach (var placeholder in placeholders)
        {
            template.Placeholders.Add(placeholder);
        }

        await context.SaveChangesAsync();
        return template;
    }

    /// <inheritdoc />
    public async Task<EmailTemplateTranslation> UpsertTranslationAsync(
        EmailTemplate template, string culture, string subject, string htmlBody)
    {
        var translation = template.Translations.FirstOrDefault(t => t.Culture == culture);

        // Stamped by hand rather than through AddAsync/UpdateAsync - this collection is saved directly
        // against the DbContext, the same reason SetPlaceholdersAsync above bypasses them too, so
        // nothing else populates these.
        var userId = userContext is null ? (Guid?)null : await userContext.GetCurrentUserIdAsync();

        if (translation is not null)
        {
            translation.Subject = subject;
            translation.HtmlBody = htmlBody;
            translation.ModifiedOn = DateTimeOffset.UtcNow;
            translation.ModifiedById = userId;
        }
        else
        {
            translation = new EmailTemplateTranslation
            {
                EmailTemplateId = template.Id,
                Culture = culture,
                Subject = subject,
                HtmlBody = htmlBody,
                CreatedOn = DateTimeOffset.UtcNow,
                CreatedById = userId ?? Guid.Empty
            };
            template.Translations.Add(translation);
        }

        await context.SaveChangesAsync();
        return translation;
    }
}
