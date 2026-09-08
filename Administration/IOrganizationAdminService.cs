// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Administration;

/// <summary>
/// Reading and acting on <em>other people's</em> Organizations - the site-admin view of one customer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every read here deliberately crosses the tenant boundary</strong>, via JumpStart's
/// <c>AcrossAllTenants()</c>. That is the whole purpose of this service, and it is why it is a separate
/// service behind its own permission rather than a relaxation of the existing tenant-scoped ones: an
/// Organization member must never be able to reach another Organization's data, and the way to keep that
/// true is for the code that can to live in one auditable place, named for what it does.
/// </para>
/// <para>
/// <strong>It does not impersonate.</strong> An earlier shape considered letting a site admin switch
/// their current tenant to any Organization and reuse every existing screen. That is less code and much
/// worse: every action would then be recorded as if the customer had taken it, and the one question
/// support most needs to answer afterwards - did we do this, or did they? - would have no answer. Here
/// the admin stays themselves and names the Organization they are acting on.
/// </para>
/// <para>
/// The reports answer questions about the population; this answers questions about one account. They
/// read the same tables and are not substitutes: no report could ever say "this customer emailed, what
/// is going on with them", because none of them is about an individual.
/// </para>
/// </remarks>
public interface IOrganizationAdminService
{
    /// <summary>Organizations matching the filter, newest sign-up first.</summary>
    Task<IReadOnlyList<OrganizationSummaryDto>> ListAsync(
        OrganizationQueryDto query, CancellationToken cancellationToken = default);

    /// <summary>Everything behind one Organization, or <c>null</c> if there is no such tenant.</summary>
    Task<OrganizationDetailDto?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The plans a site admin can move this Organization onto.</summary>
    Task<IReadOnlyList<ReportFilterOptionDto>> GetPlanOptionsAsync(CancellationToken cancellationToken = default);
}
