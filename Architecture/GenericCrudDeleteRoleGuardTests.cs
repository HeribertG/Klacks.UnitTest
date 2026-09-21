// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Holds the generic CRUD DELETE verb at Admin-only. InputBaseController.Delete used to carry
/// [Authorize(Roles = Admin,Authorised)] — a role check from before the granular rights model, inherited
/// unchanged by every derived controller, so a supervisor could delete contracts, shifts, absences,
/// countries and group subtrees although Permissions.GetPermissionsForRole grants the Authorised role no
/// CanDelete* right at all.
///
/// Why this is a per-type guard rather than a single assertion on the base method: an [Authorize] on an
/// override is AND-combined with the base one (AuthorizeAttributeInheritanceTests), so the roles an
/// endpoint really demands are the INTERSECTION of every role-bearing attribute on the action and on its
/// type. A controller could therefore add a restriction the base assertion would never see, and — the
/// direction that matters here — a controller cannot widen the base one, which is why the documented
/// exceptions derive from SupervisorDeletableController instead of overriding anything.
///
/// Scope note — what this guard does NOT cover: DELETE actions declared by a controller itself
/// (ClientsController.Delete, GroupsController.DeleteSubtree, ContainersController.DeleteTemplates, …).
/// Only the generic Delete of the two CRUD base classes is checked; a hand-written delete action carries
/// its own decision and is pinned by its own controller test.
/// </summary>

using System.Reflection;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GenericCrudDeleteRoleGuardTests
{
    private const string DeleteMethodName = "Delete";
    private const int MinimumAdminOnlyControllers = 15;

    /// <summary>
    /// The controllers whose generic DELETE stays open to the supervisor role, each with the flow that was
    /// found in the code rather than an intention. Adding a name here widens who may delete that resource,
    /// so the reason has to be checkable.
    /// </summary>
    private static readonly Dictionary<string, string> SupervisorDeletableWithReason =
        new(StringComparer.Ordinal)
        {
            ["GroupItemsController"] =
                "Membership removal is a supervisor action (spec 2.3 pins the sibling action " +
                "RemoveByClientAndGroup that way), and the skills remove_client_from_group " +
                "(CanEditClients) and remove_shift_from_group (CanEditShifts) delete by id through the " +
                "self API under the caller's own token, so an Admin-only DELETE would answer them 403.",
            ["ScheduleNotesController"] =
                "The schedule context menu offers the delete with no permission gate of its own; it is a " +
                "supervisor's daily schedule work, not an administrative removal of master data.",
            ["ScheduleCommandsController"] =
                "Same schedule context menu as ScheduleNotes; the keyword entries are day-to-day planning " +
                "data.",
            ["ExpensesController"] =
                "Same schedule context menu as ScheduleNotes; expenses and reimbursements are recorded and " +
                "corrected while planning."
        };

    [Test]
    public void EveryGenericCrudDelete_IsAdminOnly_UnlessItIsADocumentedSupervisorAction()
    {
        var controllers = GenericCrudControllers();

        controllers.Count.ShouldBeGreaterThan(
            MinimumAdminOnlyControllers,
            $"Only {controllers.Count} generic CRUD controllers were found. The scan cannot have read the " +
            "real presentation layer, so an empty violation list would prove nothing.");

        var violations = new List<string>();

        foreach (var controller in controllers)
        {
            var allowed = RolesAllowedOnDelete(controller);
            var expected = SupervisorDeletableWithReason.ContainsKey(controller.Name)
                ? new[] { Roles.Admin, Roles.Authorised }
                : new[] { Roles.Admin };

            if (!allowed.SetEquals(expected))
            {
                violations.Add(
                    $"{controller.FullName}: DELETE admits [{string.Join(", ", allowed.OrderBy(r => r, StringComparer.Ordinal))}], " +
                    $"expected [{string.Join(", ", expected)}]");
            }
        }

        var report = new StringBuilder();
        foreach (var violation in violations.OrderBy(v => v, StringComparer.Ordinal))
        {
            report.AppendLine($"  {violation}");
        }

        violations.ShouldBeEmpty(
            "Deleting a record is an administrative act under the granular rights model: the Authorised " +
            "role holds CanCreate*/CanEdit* but no CanDelete* right. A controller whose DELETE really is a " +
            "supervisor action derives from SupervisorDeletableController and is named in " +
            $"{nameof(SupervisorDeletableWithReason)} together with the flow that makes it one." +
            $"{Environment.NewLine}{report}");
    }

    [Test]
    public void TheBaseClasses_StillCarryTheTwoDifferentRoleGates()
    {
        RolesOfDeclaredDelete(typeof(InputBaseController<>)).ShouldBe(
            Roles.Admin,
            "InputBaseController.Delete is the Admin-only default every generic CRUD controller inherits.");

        RolesOfDeclaredDelete(typeof(SupervisorDeletableController<>)).ShouldBe(
            $"{Roles.Admin},{Roles.Authorised}",
            "SupervisorDeletableController exists only to keep the documented supervisor deletes open; " +
            "without its wider gate the exception list above would be silently equivalent to the default.");
    }

    [Test]
    public void EveryDocumentedException_IsAnExistingControllerOnTheSupervisorBase()
    {
        var names = GenericCrudControllers()
            .Where(DeleteComesFromTheSupervisorBase)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        var stale = SupervisorDeletableWithReason.Keys
            .Where(name => !names.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These justifications name a controller that no longer derives from " +
            "SupervisorDeletableController — the text reads as a reviewed decision but guards nothing: "
            + string.Join(", ", stale));

        var undocumented = names
            .Where(name => !SupervisorDeletableWithReason.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        undocumented.ShouldBeEmpty(
            "A controller was moved onto SupervisorDeletableController without writing down the supervisor " +
            "flow that justifies it: " + string.Join(", ", undocumented));
    }

    /// <summary>
    /// The roles a caller can hold and still reach the generic DELETE. AuthorizationMiddleware combines
    /// every IAuthorizeData of an endpoint into one policy whose requirements are AND-combined, so a
    /// caller has to satisfy each role-bearing attribute on its own — the admitted set is the
    /// intersection, not the union.
    /// </summary>
    /// <param name="controller">The concrete controller whose DELETE endpoint is inspected</param>
    private static HashSet<string> RolesAllowedOnDelete(Type controller)
    {
        var attributes = new List<AuthorizeAttribute>();

        var delete = CrudDeleteMethodOf(controller)!;
        attributes.AddRange(delete.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        for (var current = controller; current is not null && current != typeof(object); current = current.BaseType)
        {
            attributes.AddRange(current.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        }

        HashSet<string>? allowed = null;
        foreach (var attribute in attributes.Where(a => !string.IsNullOrWhiteSpace(a.Roles)))
        {
            var roles = attribute.Roles!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            if (allowed is null)
            {
                allowed = roles;
                continue;
            }

            allowed.IntersectWith(roles);
        }

        return allowed ?? [];
    }

    private static string? RolesOfDeclaredDelete(Type baseDefinition)
    {
        var delete = baseDefinition.GetMethod(DeleteMethodName, BindingFlags.Public | BindingFlags.Instance);
        delete.ShouldNotBeNull($"{baseDefinition.Name} no longer declares a {DeleteMethodName} action.");

        var attribute = delete!.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .SingleOrDefault(a => !string.IsNullOrWhiteSpace(a.Roles));

        attribute.ShouldNotBeNull($"{baseDefinition.Name}.{DeleteMethodName} carries no role restriction.");
        return attribute!.Roles;
    }

    /// <summary>
    /// The action a controller inherited from one of the two generic CRUD base classes, or null when it
    /// has none. Resolved by scanning the methods rather than by GetMethod(name), because a controller is
    /// free to declare an overloaded Delete of its own and GetMethod would throw on the ambiguity.
    /// </summary>
    /// <param name="controller">The concrete controller being inspected</param>
    private static MethodInfo? CrudDeleteMethodOf(Type controller)
    {
        return controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == DeleteMethodName)
            .FirstOrDefault(method => IsGenericCrudBase(method.GetBaseDefinition().DeclaringType));
    }

    private static bool IsGenericCrudBase(Type? declaring)
    {
        if (declaring is null || !declaring.IsGenericType)
        {
            return false;
        }

        var definition = declaring.GetGenericTypeDefinition();
        return definition == typeof(InputBaseController<>)
               || definition == typeof(SupervisorDeletableController<>);
    }

    private static bool DeleteComesFromTheSupervisorBase(Type controller)
    {
        var declaring = CrudDeleteMethodOf(controller)?.GetBaseDefinition().DeclaringType;

        return declaring is { IsGenericType: true }
               && declaring.GetGenericTypeDefinition() == typeof(SupervisorDeletableController<>);
    }

    /// <summary>
    /// Every routed controller whose DELETE comes from one of the two generic CRUD base classes. Read from
    /// the base definition of the method rather than from the type hierarchy, so a controller that stops
    /// inheriting the action drops out of the scan instead of being asserted against an action it no
    /// longer has.
    /// </summary>
    private static List<Type> GenericCrudControllers()
    {
        return typeof(BaseController).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => CrudDeleteMethodOf(t) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }
}
