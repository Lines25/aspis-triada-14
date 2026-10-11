using Robust.Shared.Audio;

namespace Content.Shared._Aspis.Interaction;

[RegisterComponent]
public sealed partial class VulpkaninSniffComponent : Component
{
    [DataField]
    public SoundSpecifier Sound = new SoundCollectionSpecifier("AspisVulpkaninSniff");
}
