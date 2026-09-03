// Copyright ©2026 Scott Blomfield

using AutoMapper;
using JumpStart.Api.Mapping;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="Plan"/> mappings.
/// </summary>
public class PlanMappingProfile : EntityMappingProfile<Plan, PlanDto, CreatePlanDto, UpdatePlanDto>
{
    public PlanMappingProfile()
    {
        // Used by PublicPlansController for RustArchon.Web's pricing page - not part of the base
        // class's Create/Update/Dto set, since it's neither an admin read shape nor a write DTO.
        CreateMap<Plan, PublicPlanDto>();
    }

    protected override void ConfigureAdditionalMappings(
        IMappingExpression<Plan, PlanDto> entityMap,
        IMappingExpression<CreatePlanDto, Plan> createMap,
        IMappingExpression<UpdatePlanDto, Plan> updateMap)
    {
        // Computed per-request from a live TenantPlan count (see IPlanRepository.GetSubscriberCountAsync)
        // - PlansController sets it explicitly on the mapped DTO, not AutoMapper.
        entityMap.ForMember(dest => dest.SubscriberCount, opt => opt.Ignore());

        // Name is fixed at creation and never edited in place - see UpdatePlanDto's remarks. ColorCode
        // maps by convention (UpdatePlanDto has its own ColorCode property) - it's the one field
        // that's freely editable regardless of subscriber count.
        updateMap.ForMember(dest => dest.Name, opt => opt.Ignore());
    }
}
