using System.Collections.Generic;
using System.Linq;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Client.Clothing;

internal static class ClothingSpeciesHelper
{
    public static IEnumerable<string> GetClothingSpecies(
        IPrototypeManager prototypeManager,
        string speciesId,
        string slot)
    {
        yield return speciesId;

        var normalizedSpeciesId = speciesId.ToLowerInvariant();
        if (normalizedSpeciesId != speciesId)
            yield return normalizedSpeciesId;

        var species = prototypeManager.EnumeratePrototypes<SpeciesPrototype>()
            .FirstOrDefault(prototype => string.Equals(prototype.ID, speciesId, StringComparison.OrdinalIgnoreCase));
        if (species == null)
            yield break;

        ProtoId<SpeciesPrototype>? fallback = null;
        if (species.ClothingSpeciesFallback.TryGetValue(slot, out var slotFallback))
        {
            fallback = slotFallback;
        }
        else
        {
            foreach (var (fallbackSlot, fallbackSpecies) in species.ClothingSpeciesFallback)
            {
                if (!string.Equals(fallbackSlot, slot, StringComparison.OrdinalIgnoreCase))
                    continue;

                fallback = fallbackSpecies;
                break;
            }
        }

        if (fallback is not { } fallbackSpeciesId
            || string.Equals(fallbackSpeciesId, speciesId, StringComparison.OrdinalIgnoreCase))
            yield break;

        var fallbackId = fallbackSpeciesId.ToString();
        yield return fallbackId;

        if (fallbackId.ToLowerInvariant() != fallbackId)
            yield return fallbackId.ToLowerInvariant();
    }
}
