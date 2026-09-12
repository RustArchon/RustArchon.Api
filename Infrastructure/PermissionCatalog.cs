// Copyright ©2026 Scott Blomfield

using JumpStart.Authorization;

namespace RustArchon.Api.Infrastructure;

/// <summary>
/// Every permission RustArchon supports, declared once.
/// </summary>
/// <remarks>
/// <para>
/// The application half of JumpStart's ADR-019: the framework owns the mechanism - a closed set,
/// scope rules, "nobody grants what they do not hold" - and this is the content. A grant of anything
/// not named here is refused by the repository, whatever route it arrives by.
/// </para>
/// <para>
/// <strong>Two shapes, one kind of object.</strong> <c>RustServer.*</c> follows ADR-011's
/// entity/action convention because those permissions really are about one entity;
/// <c>Platform.*</c> and <c>Subscription.*</c> do not, because a report spans a dozen tables and
/// billing spans three. The framework never parses either - the convention is a naming habit, not a
/// model - which is what lets both live in one registry and be checked by one mechanism.
/// </para>
/// </remarks>
public static class PermissionCatalog
{
    // ---- Tenant-scoped: what an Organization's own members can do ----

    public const string ServerGet = "RustServer.Get";
    public const string ServerList = "RustServer.List";
    public const string ServerCreate = "RustServer.Create";
    public const string ServerUpdate = "RustServer.Update";
    public const string ServerDelete = "RustServer.Delete";

    /// <summary>
    /// Sending an arbitrary RCON command to one of the Organization's servers.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="ServerUpdate"/> deliberately. That one permission used to cover
    /// editing the panel entry, enabling, disabling <em>and</em> running any command on the box -
    /// which is effectively total control of the game server. "May rename this entry" and "may run
    /// <c>ban</c> on every player" are not the same trust, and a tenant building a Moderator role
    /// needs to be able to grant one without the other.
    /// </remarks>
    public const string ServerSendCommand = "RustServer.SendCommand";

    /// <summary>Reading the Organization's own plan, invoices and billing history.</summary>
    public const string SubscriptionView = "Subscription.View";

    /// <summary>
    /// Changing the Organization's plan, term or capacity.
    /// </summary>
    /// <remarks>
    /// Not delegable. This is the permission that spends money - a plan change or a slot purchase
    /// raises a real invoice with a legally sequential number - so it stays with the built-in Owner
    /// role rather than being something a tenant can fold into a role of its own. A customer who
    /// wants a second person able to spend makes them an Owner, which is a visible act.
    /// </remarks>
    public const string SubscriptionManage = "Subscription.Manage";

    /// <summary>
    /// Adding, suspending and removing the Organization's members, and assigning them roles.
    /// </summary>
    /// <remarks>
    /// Delegable, and safe to delegate because of JumpStart's fourth grant rule: somebody holding
    /// this can only ever assign a role whose permissions they hold themselves. An Admin cannot hand
    /// out the built-in Owner role, because Owner contains
    /// <see cref="SubscriptionManage"/> and they do not have it. The rule does the containment; this
    /// flag does not have to.
    /// </remarks>
    public const string OrganizationManageMembers = "Organization.ManageMembers";

    /// <summary>
    /// Defining the Organization's own roles - the capability the pricing page sells as "role
    /// separation".
    /// </summary>
    /// <remarks>
    /// Not delegable, and this one deliberately: defining roles is how new combinations of power come
    /// into existence in an Organization, and that stays with the built-in Owner. Delegating it would
    /// let somebody build a role, grant themselves nothing new by rule 4 - but reshape who else holds
    /// what, which is an owner-level decision. Separately gated on the plan tier by
    /// <c>PlanRoleManagementPolicy</c>.
    /// </remarks>
    public const string OrganizationManageRoles = "Organization.ManageRoles";

    /// <summary>
    /// Changing the Organization's own name and contact address.
    /// </summary>
    /// <remarks>
    /// Delegable, for the same reason as <see cref="OrganizationManageMembers"/>: there is nothing
    /// here that spends money or reshapes who holds what, so an Admin role can safely include it.
    /// <see cref="JumpStart.Data.Tenant.ContactEmail"/> is not cosmetic - it is where every lifecycle notice
    /// <c>OrganizationLifecycleService</c> queues actually goes, so this is also the one place an
    /// Organization can fix a wrong or missing address itself.
    /// </remarks>
    public const string OrganizationManageSettings = "Organization.ManageSettings";

    // ---- Platform-scoped: running the business, never held inside a tenant ----

    public const string PlatformManageInvitations = "Platform.ManageInvitations";
    public const string PlatformManageSettings = "Platform.ManageSettings";
    public const string PlatformManagePlans = "Platform.ManagePlans";
    public const string PlatformViewReports = "Platform.ViewReports";
    public const string PlatformManageBilling = "Platform.ManageBilling";
    public const string PlatformManageOrganizations = "Platform.ManageOrganizations";

    /// <summary>
    /// Everything an Organization's built-in Owner role holds - the full tenant-scoped set.
    /// </summary>
    public static readonly string[] OwnerPermissions =
    [
        ServerGet, ServerList, ServerCreate, ServerUpdate, ServerDelete, ServerSendCommand,
        SubscriptionView, SubscriptionManage,
        OrganizationManageMembers, OrganizationManageRoles, OrganizationManageSettings
    ];

    /// <summary>Every platform permission - what the "Site Admin" role holds.</summary>
    public static readonly string[] PlatformPermissions =
    [
        PlatformManageInvitations, PlatformManageSettings, PlatformManagePlans,
        PlatformViewReports, PlatformManageBilling, PlatformManageOrganizations
    ];

    /// <summary>
    /// The declarations handed to JumpStart at startup.
    /// </summary>
    /// <remarks>
    /// <c>DelegableByTenantAdmin</c> is the field that decides what a customer may put in a role of
    /// their own. Everything about running servers is delegable - that is the feature the pricing
    /// page sells. Spending money is not.
    /// </remarks>
    public static PermissionDescriptor[] All =>
    [
        new(ServerGet, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "View a server and its details."),
        new(ServerList, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "See the organization's list of servers."),
        new(ServerCreate, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "Add a server, consuming a purchased slot."),
        new(ServerUpdate, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "Edit a server's details, and enable or disable it."),
        new(ServerDelete, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "Remove a server and its history."),
        new(ServerSendCommand, PermissionScope.Tenant, "Servers", DelegableByTenantAdmin: true,
            "Run any RCON command on a server."),

        new(SubscriptionView, PermissionScope.Tenant, "Billing", DelegableByTenantAdmin: true,
            "See the organization's plan, invoices and billing history."),
        new(SubscriptionManage, PermissionScope.Tenant, "Billing", DelegableByTenantAdmin: false,
            "Change the plan, term or capacity. Raises invoices - Owner only."),

        new(OrganizationManageMembers, PermissionScope.Tenant, "Organization", DelegableByTenantAdmin: true,
            "Add, suspend and remove members, and assign them roles."),
        new(OrganizationManageRoles, PermissionScope.Tenant, "Organization", DelegableByTenantAdmin: false,
            "Define the organization's own roles - Owner only, and only on a plan that includes it."),
        new(OrganizationManageSettings, PermissionScope.Tenant, "Organization", DelegableByTenantAdmin: true,
            "Change the organization's name and contact address."),

        new(PlatformManageInvitations, PermissionScope.Platform, "Platform"),
        new(PlatformManageSettings, PermissionScope.Platform, "Platform"),
        new(PlatformManagePlans, PermissionScope.Platform, "Platform"),
        new(PlatformViewReports, PermissionScope.Platform, "Platform"),
        new(PlatformManageBilling, PermissionScope.Platform, "Platform"),
        new(PlatformManageOrganizations, PermissionScope.Platform, "Platform")
    ];
}
