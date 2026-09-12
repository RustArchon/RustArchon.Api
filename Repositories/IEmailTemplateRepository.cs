// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Threading.Tasks;
using JumpStart.Repositories;
using RustArchon.Api.Data;

namespace RustArchon.Api.Repositories;

/// <summary>
/// Repository interface for <see cref="EmailTemplate"/> entities.
/// </summary>
public interface IEmailTemplateRepository : IRepository<EmailTemplate>
{
    /// <summary>
    /// Gets a template by its unique <see cref="EmailTemplate.Code"/> rather than its <c>Id</c> - the
    /// lookup every reader (the registry's idempotency check, <c>CommunicationPublisher</c>,
    /// <c>EmailTemplatesController</c>) actually needs, since callers know the well-known code, never
    /// the row's Guid.
    /// </summary>
    Task<EmailTemplate?> GetByCodeAsync(string code);

    /// <summary>
    /// Replaces <paramref name="template"/>'s linked placeholders with exactly <paramref name="placeholders"/>
    /// and saves. Bypasses the inherited <c>UpdateAsync</c> - that method's <c>CurrentValues.SetValues</c>
    /// only copies scalar properties, never a navigation collection.
    /// </summary>
    Task<EmailTemplate> SetPlaceholdersAsync(EmailTemplate template, IReadOnlyList<EmailPlaceholder> placeholders);

    /// <summary>
    /// Creates or overwrites <paramref name="template"/>'s translation for <paramref name="culture"/> -
    /// design B's whole point: a new language is a new row here, never a schema change or a new
    /// <see cref="EmailTemplate"/> row.
    /// </summary>
    Task<EmailTemplateTranslation> UpsertTranslationAsync(
        EmailTemplate template, string culture, string subject, string htmlBody);
}
