using System;
using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q2.2B.2: administration-only access to round-local construction yard probes.
/// Standalone IConsoleCommand implementations are discovered during server
/// console initialization, before AdminManager snapshots command permissions.
/// This makes the commands available in authorized players' client consoles.
/// </summary>
public sealed partial class OrganizationSystem
{
    // Compatibility with Q2.2B.1's existing call in OrganizationSystem.Initialize().
    // IMPORTANT: do not dynamically RegisterCommand here: AdminManager already
    // cached its permission table before EntitySystem.Initialize(), so players
    // would not receive the command in their allowed command list.
    private void InitConstructionYardCommands()
    {
    }

    /// <summary>
    /// Called only from the Admin-flagged server console command below.
    /// This is a test probe, never a real company quota or a payment trigger.
    /// </summary>
    public bool TrySpawnAdminConstructionYard(int level, out string message)
    {
        if (level is < 1 or > 4)
        {
            message = "Використання: yardspawn [1-4]";
            return false;
        }

        var company = _organizations.Values.FirstOrDefault(org =>
            org.Kind == OrganizationKind.Company &&
            org.Industry == OrganizationIndustry.Construction &&
            org.InsolvencyState == OrganizationInsolvencyState.Active);
        if (company == null)
        {
            message = "Спочатку зареєструйте активну будівельну компанію.";
            return false;
        }

        if (!TryDeployConstructionYardProbe(company.Id, level - 1,
                out var position, out var failure))
        {
            message = failure;
            return false;
        }

        message = $"Верф {level} рівня для «{company.Name}» створена: " +
                  $"X={position.X:0.0}, Y={position.Y:0.0} (звичайний космос). " +
                  "FTL-маршрутів, власності на корабель і виплат не створено.";
        return true;
    }

    public string[] GetAdminConstructionYardStatus()
    {
        if (_constructionYards.Count == 0)
            return ["У цьому раунді тестових верфей ще немає."];

        return _constructionYards.Values.Select(yard =>
        {
            var complete = TryInspectConstructionHull(yard.ContractHull,
                out var present, out var required, out var report);
            return $"{yard.QuotaCode}: {yard.DesignName}, рівень {yard.DifficultySlot + 1}, " +
                   $"X={yard.WorldPosition.X:0.0}, Y={yard.WorldPosition.Y:0.0}, " +
                   $"{present}/{required}, завершено={complete}; {report}";
        }).ToArray();
    }
}

/// <summary>
/// Available from the in-game client console only to admins with Admin flag.
/// Server console is inherently privileged and can also invoke it.
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class AspisYardSpawnConsoleCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IAdminManager _admins = default!;

    public string Command => "yardspawn";
    public string Description => "Створити тестову космічну верф для будівельної компанії.";
    public string Help => "yardspawn [1-4] — рівень складності (за замовчуванням 1).";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        // Defense in depth, in addition to the AdminManager's command gate.
        if (shell.Player is { } player && !_admins.HasAdminFlag(player, AdminFlags.Admin))
        {
            shell.WriteError("Ця команда доступна лише адміністраторам із правом Admin.");
            return;
        }

        if (args.Length > 1 || (args.Length == 1 && !int.TryParse(args[0], out _)))
        {
            shell.WriteError("Використання: yardspawn [1-4]");
            return;
        }

        var level = args.Length == 0 ? 1 : int.Parse(args[0]);
        var organization = _entities.EntitySysManager.GetEntitySystem<OrganizationSystem>();
        if (!organization.TrySpawnAdminConstructionYard(level, out var message))
            shell.WriteError(message);
        else
            shell.WriteLine(message);
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class AspisYardStatusConsoleCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IAdminManager _admins = default!;

    public string Command => "yardstatus";
    public string Description => "Показати всі тестові верфі та прогрес ремонту корпусів.";
    public string Help => "yardstatus";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is { } player && !_admins.HasAdminFlag(player, AdminFlags.Admin))
        {
            shell.WriteError("Ця команда доступна лише адміністраторам із правом Admin.");
            return;
        }

        if (args.Length != 0)
        {
            shell.WriteError("Використання: yardstatus");
            return;
        }

        var organization = _entities.EntitySysManager.GetEntitySystem<OrganizationSystem>();
        foreach (var line in organization.GetAdminConstructionYardStatus())
            shell.WriteLine(line);
    }
}
