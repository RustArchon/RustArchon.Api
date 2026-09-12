// Copyright ©2026 Scott Blomfield

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
/// Platform-admin management of the reusable <c>{{Token}}</c> placeholders email templates draw from.
/// See <c>EmailTemplateRegistry</c> for where placeholders are declared and seeded, and
/// <c>EmailTemplatesController</c> for linking one to a template.
/// </summary>
/// <remarks>
/// Gated by the same <c>ManagePlatformSettings</c> policy as <c>EmailTemplatesController</c>.
/// Deliberately no <c>Create</c>/<c>Delete</c>/rename of <see cref="EmailPlaceholder.Name"/>: the Name
/// is the literal substitution key real sending code fills a dictionary with (see
/// <c>ICommunicationPublisher.QueueTemplatedAsync</c>'s remarks) - an admin renaming or inventing one
/// here would produce a placeholder no code path has ever heard of, silently never substituted. Only
/// <see cref="EmailPlaceholder.Description"/>/<see cref="EmailPlaceholder.Sample"/> are administered
/// here; which placeholders a *template* uses is <c>EmailTemplatesController.UpdatePlaceholders</c>.
/// </remarks>
[ApiController]
[Route("api/email-placeholders")]
[Authorize(Policy = "ManagePlatformSettings")]
public class EmailPlaceholdersController(IEmailPlaceholderRepository repository) : ControllerBase
{
    /// <summary>Every registered placeholder. Unpaginated - the same reasoning as
    /// <c>EmailTemplatesController.GetAll</c>.</summary>
    [HttpGet]
    public async Task<ActionResult<List<EmailPlaceholderDto>>> GetAll()
    {
        var placeholders = await repository.GetAllAsync();
        return Ok(placeholders.OrderBy(p => p.Name).Select(ToDto).ToList());
    }

    /// <summary>One placeholder, identified by its <see cref="EmailPlaceholder.Name"/>.</summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<EmailPlaceholderDto>> GetByName(string name)
    {
        var placeholder = await repository.GetByNameAsync(name);
        return placeholder is null ? NotFound() : Ok(ToDto(placeholder));
    }

    /// <summary>Updates one placeholder's Description/Sample.</summary>
    [HttpPut("{name}")]
    public async Task<ActionResult<EmailPlaceholderDto>> Update(
        string name, [FromBody] UpdateEmailPlaceholderDto updateDto)
    {
        var placeholder = await repository.GetByNameAsync(name);
        if (placeholder is null)
        {
            return NotFound();
        }

        placeholder.Description = updateDto.Description;
        placeholder.Sample = updateDto.Sample;
        var updated = await repository.UpdateAsync(placeholder);

        return Ok(ToDto(updated));
    }

    private static EmailPlaceholderDto ToDto(EmailPlaceholder p) => new()
    {
        Name = p.Name,
        Description = p.Description,
        Sample = p.Sample
    };
}
