using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Controlled test creation until planetary quotas provide their own route disks.
/// Does not register public destinations or issue unrestricted market rewards.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class AspisPlanetaryRouteCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IAdminManager _admins = default!;

    public string Command => "planetroute";
    public string Description => "Створити тестову атмосферну та планетарну мапи й видати штатний FTL-диск.";
    public string Help => "planetroute — команда ігрового адміністратора для першого тесту.";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } session ||
            !_admins.HasAdminFlag(session, AdminFlags.Admin) ||
            session.AttachedEntity is not { } actor)
        {
            shell.WriteError("Команду може виконати тільки адміністратор у грі з персонажем.");
            return;
        }
        if (args.Length > 0)
        {
            shell.WriteError("Використання: planetroute");
            return;
        }

        var system = _entities.EntitySysManager.GetEntitySystem<AspisPlanetaryAirspaceSystem>();
        if (system.TryIssueTestRoute(actor, out var message))
            shell.WriteLine(message);
        else
            shell.WriteError(message);
    }
}
