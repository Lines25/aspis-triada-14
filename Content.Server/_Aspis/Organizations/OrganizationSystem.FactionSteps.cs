using System.Collections.Generic;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// One source of truth for the objective list shown in the console and PDA.
/// Clients receive only descriptive, read-only progress; a click NEVER advances it.
/// </summary>
public sealed partial class OrganizationSystem
{
    private OrganizationFactionStepEntry[] BuildFactionTaskSteps(
        Organization faction, SectorQuotaClaim? claim, int slot, int category)
    {
        var steps = new List<OrganizationFactionStepEntry>();
        void Add(string title, string detail, bool completed) => steps.Add(new()
        {
            Title = title,
            Details = detail,
            Completed = completed
        });

        var hasTravel = claim != null && (claim.FactionWreckDisk is { } disk &&
            !Deleted(disk) || FactionTravelVisited(claim));
        Add("Отримати диск координат",
            "Після прийняття завдання КПК видає персональний диск координат. " +
            "Якщо він загубився до вильоту, перевидайте його в програмі «Організація» " +
            "після затримки. Без диска FTL-маршрут недоступний.", hasTravel);

        if (slot > 0)
        {
            Add("Вирушити до експедиційного об'єкта",
                "Разом із диском видається експедиційний локатор. Увімкніть його на гріді: " +
                "сигнали лунатимуть частіше біля контрольного обладнання. " +
                "Вставте диск у навігаційну консоль шатла. Дочекайтеся підготовки маршруту " +
                "й вирушіть через FTL із району Trade Mall. Після прибуття будьте готові " +
                "до ворожих дронів, небезпечних відсіків та далекого переходу через комплекс.",
                claim?.FactionWreckVisited == true);
            var kind = claim?.FactionWreckActivity ??
                FactionWreckActivityFor((OrganizationFactionFocus)category);
            Add(kind switch
                {
                    FactionWreckActivityKind.Patrol => "Перевірити системи безпеки",
                    FactionWreckActivityKind.Scientific => "Відновити й відкалібрувати сенсор",
                    FactionWreckActivityKind.SectorAid => "Відновити аварійне забезпечення",
                    _ => "Оглянути контрольний маяк"
                },
                FactionWreckTerminalAction(kind) + ". " +
                "Знайдіть потрібний пристрій у комплексі. Прогрес зараховується тільки " +
                "після реальної взаємодії на експедиційному гріді.",
                claim?.FactionWreckInspectionComplete == true);
            Add("Знайти та зчитати аварійний реєстратор",
                "Після огляду контрольного обладнання знайдіть реєстратор в іншій частині " +
                "об'єкта та зчитайте його журнал. Дані потраплять до архіву фракції.",
                claim?.Completed == true);
        }
        else
        {
            Add("Оформити доставку польового комплекту",
                "У КПК натисніть «Надрукувати накладну на комплект». " +
                "Вставте накладну в Cargo Requests на Trade Mall та дочекайтеся схвалення " +
                "і доставки ящика. Польовий комплект має вигляд захищеного кейса.",
                claim?.FactionKitOrdered == true || claim?.FactionStage > 0);
            Add("Прибути на призначену планету",
                "Перевезіть комплект на шатлі. Вставте диск координат у FTL-консоль, " +
                "вирушіть із району Trade Mall та висадіться саме на планеті, " +
                "яку сервер створив для цього завдання.", claim?.FactionPlanetVisited == true);
            Add("Позначити ділянку й встановити орієнтир",
                "На поверхні використайте кейс із польовим обладнанням на вільній ділянці. " +
                "З'явиться орієнтир. Інші ділянки фракцій мають бути щонайменше за 20 м.",
                claim is { FactionSiteBound: true } site && (site.FactionStage >= 2 ||
                    site.FactionSurveyBeacon is { } beacon && !Deleted(beacon)));
            Add("Підключити ретранслятор",
                "Використайте той самий комплект безпосередньо на орієнтирі. " +
                "Сервер замінить його фізичним ретранслятором та прив'яже до ділянки.",
                claim?.FactionStage >= 2);
            Add("Відремонтувати ретранслятор",
                "Застосуйте до обладнання 5 листів сталі для базового ремонту. " +
                "Матеріали витрачаються лише після перевірки сервером.",
                claim?.FactionStage >= 3);
            if (FactionMilitaryObjective(faction))
                Add("Зміцнити оборонний контур",
                    "Додайте до відремонтованого ретранслятора 3 листи пласталі. " +
                    "Цей пункт з'являється для фракцій з оборонним або мілітаристським спрямуванням.",
                    claim?.FactionMilitaryPrepared == true || claim?.Completed == true);
            if (FactionScienceObjective(faction))
                Add("Відкалібрувати науковий контур",
                    "Додайте 3 листи скла для налаштування наукового модуля. " +
                    "Після обох необхідних процедур можна запустити передавання.",
                    claim?.FactionSciencePrepared == true || claim?.Completed == true);
            Add("Запустити передавання сигналу",
                "Взаємодійте з антеною порожньою рукою після ремонту та додаткових процедур. " +
                "Під час стабілізації обладнання повинне залишатися на закріпленій ділянці.",
                claim?.FactionStage >= 4);
            Add("Підтвердити сигнал і зареєструвати вузол",
                "Дочекайтеся стабілізації, ще раз взаємодійте з антеною й отримайте " +
                "підтвердження серверного реєстру. Тільки тоді завдання вважається виконаним.",
                claim?.Completed == true);
        }
        return steps.ToArray();
    }
}
