// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Platform-admin management of email templates: listing and editing the Subject/HtmlBody actually
/// sent for each kind of email RustArchon sends. See <c>EmailTemplateRegistry</c> for where templates
/// are declared and seeded.
/// </summary>
/// <remarks>
/// Gated by the same <c>ManagePlatformSettings</c> policy as <c>PlatformSettingsController</c> - editing
/// what an email says is the same kind of platform-wide, not-tenant-specific decision as everything
/// else that policy already covers.
/// </remarks>
/// <remarks>
/// Deliberately no <c>Create</c>/<c>Delete</c> actions, for the same reason as
/// <c>PlatformSettingsController</c>: templates are seeded by <c>EmailTemplateRegistry</c>, never
/// invented ad hoc through this UI - the registry is the only place a new template Code is ever
/// introduced, in code, next to whatever actually sends it.
/// </remarks>
[ApiController]
[Route("api/email-templates")]
[Authorize(Policy = "ManagePlatformSettings")]
public class EmailTemplatesController(
    IEmailTemplateRepository repository, IEmailPlaceholderRepository placeholderRepository) : ControllerBase
{
    /// <summary>
    /// Lists every email template. Unpaginated - an admin-only list expected to stay small, the same
    /// reasoning as <c>PlatformSettingsController.GetAll</c>.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<EmailTemplateDto>>> GetAll()
    {
        var templates = await repository.GetAllAsync();
        return Ok(templates.OrderBy(t => t.Name).Select(ToDto).ToList());
    }

    /// <summary>
    /// One template, identified by its <see cref="EmailTemplate.Code"/> rather than its <c>Id</c> - the
    /// admin page already knows the well-known code from the list, never the row's Guid.
    /// </summary>
    [HttpGet("{code}")]
    public async Task<ActionResult<EmailTemplateDto>> GetByCode(string code)
    {
        var template = await repository.GetByCodeAsync(code);
        return template is null ? NotFound() : Ok(ToDto(template));
    }

    /// <summary>
    /// Creates or overwrites one language's Subject/HtmlBody for this template - the same call whether
    /// <see cref="UpdateEmailTemplateTranslationDto.Culture"/> already has a row or not, per Scott's
    /// choice not to give each language its own editor page; the Panel's translation dropdown just picks
    /// which culture this call names.
    /// </summary>
    [HttpPut("{code}/translations")]
    public async Task<ActionResult<EmailTemplateDto>> UpsertTranslation(
        string code, [FromBody] UpdateEmailTemplateTranslationDto updateDto)
    {
        var template = await repository.GetByCodeAsync(code);
        if (template is null)
        {
            return NotFound();
        }

        await repository.UpsertTranslationAsync(template, updateDto.Culture, updateDto.Subject, updateDto.HtmlBody);

        return Ok(ToDto(template));
    }

    /// <summary>
    /// Sets which placeholders this template uses - the whole set, not a delta. Lets an admin add or
    /// remove one without a code change, per <c>EmailTemplateRegistry</c>'s remarks on why the linkage
    /// is seeded once and then left alone.
    /// </summary>
    [HttpPut("{code}/placeholders")]
    public async Task<ActionResult<EmailTemplateDto>> UpdatePlaceholders(
        string code, [FromBody] UpdateEmailTemplatePlaceholdersDto updateDto)
    {
        var template = await repository.GetByCodeAsync(code);
        if (template is null)
        {
            return NotFound();
        }

        var placeholders = new List<EmailPlaceholder>();

        foreach (var name in updateDto.PlaceholderNames.Distinct(StringComparer.Ordinal))
        {
            var placeholder = await placeholderRepository.GetByNameAsync(name);
            if (placeholder is null)
            {
                return BadRequest($"No such placeholder: '{name}'.");
            }

            placeholders.Add(placeholder);
        }

        var updated = await repository.SetPlaceholdersAsync(template, placeholders);
        return Ok(ToDto(updated));
    }

    private static EmailTemplateDto ToDto(EmailTemplate t) => new()
    {
        Id = t.Id,
        Code = t.Code,
        Name = t.Name,
        Description = t.Description,
        Placeholders = [.. t.Placeholders
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new EmailPlaceholderDto { Name = p.Name, Description = p.Description, Sample = p.Sample })],
        Translations = [.. t.Translations
            .OrderBy(tr => tr.Culture, StringComparer.Ordinal)
            .Select(tr => new EmailTemplateTranslationDto { Culture = tr.Culture, Subject = tr.Subject, HtmlBody = tr.HtmlBody })]
    };
}
