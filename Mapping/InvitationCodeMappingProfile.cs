// Copyright ©2026 Scott Blomfield

using AutoMapper;
using JumpStart.Api.Mapping;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Mapping;

/// <summary>
/// AutoMapper profile for <see cref="InvitationCode"/> mappings.
/// </summary>
public class InvitationCodeMappingProfile
    : EntityMappingProfile<InvitationCode, InvitationCodeDto, CreateInvitationCodeDto, UpdateInvitationCodeDto>
{
    protected override void ConfigureAdditionalMappings(
        IMappingExpression<InvitationCode, InvitationCodeDto> entityMap,
        IMappingExpression<CreateInvitationCodeDto, InvitationCode> createMap,
        IMappingExpression<UpdateInvitationCodeDto, InvitationCode> updateMap)
    {
        // Code is generated server-side (InvitationCodesController.Create), never client-supplied.
        // IsActive/RedeemedAtUtc/RedeemedByEmail all start at their entity defaults for a new code.
        createMap.ForMember(dest => dest.Code, opt => opt.Ignore());
        createMap.ForMember(dest => dest.IsActive, opt => opt.Ignore());
        createMap.ForMember(dest => dest.RedeemedAtUtc, opt => opt.Ignore());
        createMap.ForMember(dest => dest.RedeemedByEmail, opt => opt.Ignore());

        // Update only ever edits Note/IsActive - Code/BoundEmail are fixed at creation, and
        // redemption state is only ever written by IInvitationCodeRepository.TryRedeemAsync's atomic
        // update, never through this mapping.
        updateMap.ForMember(dest => dest.Code, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.BoundEmail, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.RedeemedAtUtc, opt => opt.Ignore());
        updateMap.ForMember(dest => dest.RedeemedByEmail, opt => opt.Ignore());
    }
}
