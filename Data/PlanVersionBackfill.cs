// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Data;

/// <summary>
/// Reconstructs <see cref="Plan.SupersededByPlanId"/> for the plans that existed before it was recorded. Run once, by the
/// <c>AddPlanSupersededBy</c> migration; from then on <c>PlansController.Supersede</c> writes the link as it happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a reconstruction, not a record.</b> Before the link existed the only trace of a supersede was that two rows shared a
/// <see cref="Plan.Name"/>, so the rule is the one that trace supports: an <em>inactive</em> plan that has a later plan of the same name was replaced
/// by the very next one created after it. Active plans are never marked (nothing has replaced them), and neither is an inactive plan with no later
/// plan of its name - that one was only deactivated, and stays reactivatable.
/// </para>
/// <para>
/// Where it can be wrong: a plan an admin deactivated and then, separately, created a new one of the same name would read as replaced although no
/// supersede happened. There is no way to tell those apart after the fact, and the cost of guessing "replaced" is only that the older one is not offered
/// for reactivation - the newer plan of that name is the one in use anyway. Rows are compared by creation time, with the id (which sorts by time) as
/// the tie-break; soft-deleted rows are ignored on both sides.
/// </para>
/// </remarks>
public static class PlanVersionBackfill
{
    public const string Sql = """
        UPDATE "Plan" AS p
        SET "SupersededByPlanId" = (
            SELECT n."Id"
            FROM "Plan" AS n
            WHERE n."Name" = p."Name"
              AND n."DeletedOn" IS NULL
              AND n."Id" <> p."Id"
              AND (n."CreatedOn" > p."CreatedOn" OR (n."CreatedOn" = p."CreatedOn" AND n."Id" > p."Id"))
            ORDER BY n."CreatedOn", n."Id"
            LIMIT 1)
        WHERE p."Active" = FALSE
          AND p."DeletedOn" IS NULL
          AND p."SupersededByPlanId" IS NULL;
        """;
}
