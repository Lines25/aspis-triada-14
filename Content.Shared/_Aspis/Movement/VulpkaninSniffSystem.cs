using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;

namespace Content.Shared._Aspis.Movement;

public sealed class VulpkaninSniffSystem : EntitySystem
{
    private const float SniffSoundMaxDistance = 5f;

    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<VulpkaninSniffComponent, GetVerbsEvent<InnateVerb>>(AddSniffVerb);
    }

    private void AddSniffVerb(EntityUid uid, VulpkaninSniffComponent component, GetVerbsEvent<InnateVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || args.User == args.Target)
            return;

        args.Verbs.Add(new InnateVerb
        {
            Text = Loc.GetString("vulpkanin-sniff-verb"),
            Act = () => Sniff(uid, component, args.Target),
            Priority = 2,
        });
    }

    private void Sniff(EntityUid uid, VulpkaninSniffComponent component, EntityUid target)
    {
        if (!Exists(target))
            return;

        _popup.PopupPredicted(Loc.GetString("vulpkanin-sniff-self-popup", ("target", MetaData(target).EntityName)), uid, uid);
        _popup.PopupEntity(Loc.GetString("vulpkanin-sniff-target-popup", ("user", MetaData(uid).EntityName)), target, target);
        var soundParams = component.Sound.Params
            .WithMaxDistance(SniffSoundMaxDistance)
            .WithRolloffFactor(3f);
        _audio.PlayPredicted(component.Sound, uid, uid, soundParams);
    }
}
