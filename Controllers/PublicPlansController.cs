// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Controllers;

/// <summary>
/// Anonymous endpoint RustArchon.Web's pricing page calls, since a marketing-site visitor is never
/// authenticated. See <see cref="PlansController"/> for the platform-admin side. Same
/// <c>[AllowAnonymous]</c>-on-its-own-controller pattern as <see cref="InvitationsController"/>, for
/// the same reason: mixing an authenticated admin surface and a public one on one controller class
/// isn't how this codebase does it.
/// </summary>
[ApiController]
[Route("api/public/plans")]
[AllowAnonymous]
public class PublicPlansController : ControllerBase
{
    private readonly IPlanRepository _repository;
    private readonly IMapper _mapper;

    public PublicPlansController(IPlanRepository repository, IMapper mapper)
    {
        _repository = repository;
        _mapper = mapper;
    }

    /// <summary>
    /// Every currently-active Plan (at most one per Name - see <see cref="Data.Plan"/>'s remarks),
    /// cheapest first, so the pricing page can render them left-to-right cheapest-to-priciest without
    /// hardcoding any particular tier order here.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<PublicPlanDto>>> GetActive()
    {
        var plans = await _repository.GetAllOrderedAsync();
        var active = plans.Where(p => p.Active).OrderBy(p => p.MonthlyPrice).ToList();
        return Ok(_mapper.Map<List<PublicPlanDto>>(active));
    }
}
