using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private sealed class PendingBlueprintUpload(
        Guid organization, string name, Guid uploadId, OrganizationBlueprintUploadKind kind,
        int totalChunks, int originalLength, TimeSpan started)
    {
        public readonly Guid Organization = organization;
        public readonly string Name = name;
        public readonly Guid UploadId = uploadId;
        public readonly OrganizationBlueprintUploadKind Kind = kind;
        public readonly int TotalChunks = totalChunks;
        public readonly int OriginalLength = originalLength;
        public readonly TimeSpan Started = started;
        public readonly StringBuilder Contents = new();
        public int NextIndex;
    }

    // Never persist these buffers across rounds. Identity belongs to the BUI actor.
    private readonly Dictionary<EntityUid, PendingBlueprintUpload> _pendingBlueprintUploads = new();

    private void OnBlueprintUploadChunk(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationBlueprintUploadChunkMessage args)
    {
        var actor = args.Actor;
        // Refuse untrusted uploads before allocating any large buffers.
        if (!TryGetOutpostOwner(uid, actor, args.Organization, out _))
            return;

        foreach (var (key, upload) in _pendingBlueprintUploads.ToArray())
        {
            if (_gameTiming.CurTime - upload.Started > TimeSpan.FromMinutes(4))
                _pendingBlueprintUploads.Remove(key);
        }

        if (args.Kind is not (OrganizationBlueprintUploadKind.Fleet or OrganizationBlueprintUploadKind.Outpost) ||
            args.UploadId == Guid.Empty || args.Name is null ||
            args.Name.Length is < 1 or > 64 ||
            args.Name.AsSpan().IndexOfAny('\n', '\r') >= 0 ||
            args.OriginalLength is < 100 or > OrganizationBlueprintUploadRules.MaxEnvelopeCharacters ||
            args.TotalChunks is < 1 or > OrganizationBlueprintUploadRules.MaxChunks ||
            // Permit one additional fragment when avoiding UTF-16 surrogate splits.
            args.TotalChunks < (args.OriginalLength + OrganizationBlueprintUploadRules.ChunkCharacters - 1) / OrganizationBlueprintUploadRules.ChunkCharacters ||
            args.TotalChunks > (args.OriginalLength + OrganizationBlueprintUploadRules.ChunkCharacters - 1) / OrganizationBlueprintUploadRules.ChunkCharacters + 1 ||
            args.Index < 0 || args.Index >= args.TotalChunks ||
            args.Data is null ||
            args.Data.Length is < 1 or > OrganizationBlueprintUploadRules.ChunkCharacters ||
            (args.Index < args.TotalChunks - 1 &&
             args.Data.Length < OrganizationBlueprintUploadRules.ChunkCharacters - 1))
        {
            _pendingBlueprintUploads.Remove(actor);
            Refresh(uid, actor, "Некоректні дані передачі креслення.");
            return;
        }

        if (args.Index == 0)
        {
            if (!_pendingBlueprintUploads.ContainsKey(actor) && _pendingBlueprintUploads.Count >= 8)
            {
                Refresh(uid, actor, "Забагато одночасних завантажень креслень.");
                return;
            }
            _pendingBlueprintUploads[actor] = new PendingBlueprintUpload(args.Organization,
                args.Name, args.UploadId, args.Kind, args.TotalChunks, args.OriginalLength,
                _gameTiming.CurTime);
        }

        if (!_pendingBlueprintUploads.TryGetValue(actor, out var pending) ||
            pending.Organization != args.Organization || pending.UploadId != args.UploadId ||
            pending.Kind != args.Kind || pending.Name != args.Name ||
            pending.TotalChunks != args.TotalChunks || pending.OriginalLength != args.OriginalLength ||
            pending.NextIndex != args.Index ||
            pending.Contents.Length + args.Data.Length > OrganizationBlueprintUploadRules.MaxTransferCharacters)
        {
            _pendingBlueprintUploads.Remove(actor);
            Refresh(uid, actor, "Послідовність частин креслення порушено. Повторіть завантаження.");
            return;
        }

        pending.Contents.Append(args.Data);
        pending.NextIndex++;
        if (pending.NextIndex != pending.TotalChunks)
            return;

        _pendingBlueprintUploads.Remove(actor);
        try
        {
            // Bounded text fragments, not a compressed stream. Keep the signed
            // ship envelope byte-for-byte equivalent at the string level.
            var envelope = pending.Contents.ToString();
            if (envelope.Length != pending.OriginalLength ||
                envelope.Length > OrganizationBlueprintUploadRules.MaxEnvelopeCharacters)
                throw new InvalidOperationException("Неповна або змінена передача креслення.");

            // Both paths verify the original signed envelope, ownership and
            // content hash; receiving chunks grants no privileges by itself.
            if (pending.Kind == OrganizationBlueprintUploadKind.Outpost)
                StageOutpostBlueprint(uid, actor, pending.Organization, pending.Name, envelope);
            else
                StageFleetBlueprint(uid, actor, pending.Organization, pending.Name, envelope);
        }
        catch (Exception ex)
        {
            _log.GetSawmill("organizations.blueprints").Warning(
                $"Rejected blueprint transfer {pending.UploadId}: {ex.GetType().Name}");
            Refresh(uid, actor, "Не вдалося перевірити креслення. Перевірте файл і повторіть спробу.");
        }
    }
}
