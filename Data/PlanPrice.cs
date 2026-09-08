// Copyright ©2026 Scott Blomfield

using System.ComponentModel.DataAnnotations.Schema;
using JumpStart.Data;

namespace RustArchon.Api.Data;

/// <summary>
/// What one <see cref="Plan"/> costs for one billing term. A plan offers exactly the terms it has rows
/// here for.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the three fixed <c>MonthlyPrice</c>/<c>QuarterlyPrice</c>/<c>AnnualPrice</c> columns that
/// used to sit on <see cref="Plan"/>. Those columns forced every plan to offer all three terms and made
/// "not sold on this term" inexpressible - a price of zero already means <em>free</em>, so it couldn't
/// also mean <em>unavailable</em>. A provider selling annual-only now has one row, and one selling
/// monthly per-server alongside a flat annual rate can do that too.
/// </para>
/// <para>
/// <strong>One formula covers both pricing models:</strong>
/// </para>
/// <code>
/// amount = BaseAmount + max(0, quantity - IncludedUnits) * UnitAmount
/// </code>
/// <para>
/// A flat tier sets <see cref="UnitAmount"/> to zero, so quantity doesn't enter into it and the price is
/// just <see cref="BaseAmount"/>. A per-server plan sets a non-zero <see cref="UnitAmount"/>, and
/// <see cref="IncludedUnits"/> covers whatever the base price already buys - "$2 for the first server,
/// $2 for each after" is base 2.00, included 1, unit 2.00. <see cref="Plan.PricingModel"/> says which
/// story to tell a buyer; it doesn't change this arithmetic.
/// </para>
/// <para>
/// At most one row per (<see cref="PlanId"/>, <see cref="TermMonths"/>) - enforced by a unique index in
/// <c>ApiDbContext.OnModelCreating</c>. A plan being superseded (see <see cref="Plan"/>'s remarks)
/// carries its prices with it: the new Plan row gets its own PlanPrice rows, and the old ones stay put
/// so a subscriber on the old plan keeps being priced at what they signed up for.
/// </para>
/// </remarks>
[Table("PlanPrice")]
public class PlanPrice : Entity
{
    public Guid PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    /// <summary>
    /// How many months one billing period on this price lasts. Any positive count is valid - see
    /// <see cref="Shared.DTOs.BillingTerms"/> for why this isn't an enum.
    /// </summary>
    public int TermMonths { get; set; }

    /// <summary>
    /// The price for one period before any per-unit charge. For a flat tier this is the whole price; for
    /// a per-server plan it's whatever <see cref="IncludedUnits"/> costs.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal BaseAmount { get; set; }

    /// <summary>
    /// How much capacity <see cref="BaseAmount"/> already covers. Units beyond this are charged at
    /// <see cref="UnitAmount"/> each.
    /// </summary>
    public int IncludedUnits { get; set; }

    /// <summary>
    /// The price of one unit of capacity beyond <see cref="IncludedUnits"/>. Zero for a flat tier, which
    /// is what makes quantity irrelevant to that plan's price.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal UnitAmount { get; set; }

    /// <summary>
    /// ISO 4217 code for the amounts on this row. Stored per price rather than per plan so a plan could
    /// eventually be sold in more than one currency; today every row is the deployment's single currency.
    /// </summary>
    [Column(TypeName = "char(3)")]
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// What this price charges for <paramref name="quantity"/> units of capacity over one period.
    /// </summary>
    public decimal AmountFor(int quantity) =>
        BaseAmount + (Math.Max(0, quantity - IncludedUnits) * UnitAmount);

    /// <summary>
    /// This price expressed per month at <paramref name="quantity"/> units - the common footing on which
    /// two plans or two terms can be compared, and what decides whether a change is an upgrade.
    /// </summary>
    public decimal MonthlyEquivalentFor(int quantity) =>
        TermMonths <= 0 ? 0m : AmountFor(quantity) / TermMonths;
}
