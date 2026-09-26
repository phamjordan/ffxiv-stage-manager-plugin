using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using StageManager.Core;

namespace StageManager;

// Only this adapter owns native actors. Call exclusively on Framework.Update.
internal sealed unsafe class NativeGhosts(IObjectTable objects, IDataManager data, IPluginLog log)
{
    private sealed class Owned(ushort index, nint address, double created)
    {
        public readonly ushort Index = index;
        public readonly nint Address = address;
        public readonly double Created = created;
        public bool Ready;
        public string ActionKey = "";
    }
    private readonly Dictionary<string, Owned> actors = [];
    private readonly Dictionary<string, double> retryAfter = [];
    private sealed record EmoteDefinition(uint Id, ActionTimeline? Timeline);
    private readonly Dictionary<string, EmoteDefinition?> emotes = new(StringComparer.OrdinalIgnoreCase);
    public int Count => actors.Count;
    public string Status { get; private set; } = "";

    public void Tick(RenderTarget[] targets, string ownCastId, double now, float alpha)
    {
        var manager = ClientObjectManager.Instance();
        if (manager == null) return;
        var wanted = targets.Select(t => t.Cast.Id).ToHashSet();
        foreach (var id in actors.Keys.Where(id => !wanted.Contains(id)).ToArray()) Delete(manager, id);
        var spawnBudget = 2; // Spread the director's model loads over several frames.
        foreach (var target in targets.Take(32))
        {
            if (!actors.TryGetValue(target.Cast.Id, out var owned))
            {
                if (retryAfter.GetValueOrDefault(target.Cast.Id) > now) continue;
                if (spawnBudget <= 0) continue;
                retryAfter[target.Cast.Id] = now + 2;
                var source = target.Cast.Id == ownCastId ? objects.LocalPlayer : FindPlayer(target.Cast);
                if (source is null) { Status = $"{target.Cast.Name}: not nearby; showing waypoint only."; continue; }
                spawnBudget--;
                try { owned = Spawn(manager, source, target, now, alpha); }
                catch (Exception ex) { log.Error(ex, "Ghost creation failed"); Status = "Ghost creation failed; see /xllog."; continue; }
                if (owned is null) continue;
                actors[target.Cast.Id] = owned;
            }
            var actor = Resolve(manager, owned);
            if (actor == null) { actors.Remove(target.Cast.Id); continue; }
            // Native setters notify the draw object. Raw field writes leave an
            // already-loaded model at its previous transform after a slide change.
            // Keep this before the action-key check: a position edit need not change the emote.
            actor->SetPosition(target.Position.X, target.Position.Y, target.Position.Z);
            actor->SetRotation(target.Position.Yaw);
            actor->Alpha = Math.Clamp(alpha, .1f, .8f);
            if (!owned.Ready)
            {
                if (!actor->IsReadyToDraw())
                {
                    if (now - owned.Created > 5) { Delete(manager, target.Cast.Id); Status = "Ghost model did not load; showing waypoint."; }
                    continue;
                }
                actor->EnableDraw(); owned.Ready = true;
            }
            if (owned.ActionKey == target.AnimationKey) continue;
            owned.ActionKey = target.AnimationKey;
            actor->Timeline.BaseOverride = 0;
            actor->Timeline.LipsOverride = 0;
            actor->SetMode(CharacterModes.Normal, 0);
            // Clear every slot on our own actor, including upper-body/facial
            // gestures. A base-only reset can leave a one-shot emote running.
            for (uint slot = 0; slot < actor->Timeline.TimelineSequencer.TimelineIds.Length; slot++)
                actor->Timeline.TimelineSequencer.SetSlotTimeline(slot, 0);
            if (!target.Animate || target.Emote.Length == 0) continue;
            var timeline = FindEmote(target.Emote)?.Timeline;
            if (timeline is null) { Status = $"/{target.Emote}: no supported emote timeline; text cue remains visible."; continue; }
            actor->SetMode(CharacterModes.AnimLock, 0);
            if (timeline.Value.ActionTimelineIDMode == 0)
                actor->Timeline.TimelineSequencer.PlayTimeline((ushort)timeline.Value.RowId);
            else actor->Timeline.BaseOverride = (ushort)timeline.Value.RowId;
        }
    }

    private IPlayerCharacter? FindPlayer(CastMember cast) => objects.OfType<IPlayerCharacter>().FirstOrDefault(p =>
        p.Name.TextValue.Equals(cast.Name, StringComparison.OrdinalIgnoreCase) &&
        (cast.World.Length == 0 || p.HomeWorld.Value.Name.ToString().Equals(cast.World, StringComparison.OrdinalIgnoreCase)));

    public ActorObservation? Observe(RenderTarget target, string ownCastId)
    {
        var performer = target.Cast.Id == ownCastId ? objects.LocalPlayer : FindPlayer(target.Cast);
        if (performer is null || performer.Address == 0) return null;
        var character = (Character*)performer.Address;
        var position = character->Position;
        var expected = target.RequiredEmote.Length == 0 ? null : FindEmote(target.RequiredEmote);
        return new(new(position.X, position.Y, position.Z),
            expected is not null && character->EmoteController.EmoteId == expected.Id);
    }

    private Owned? Spawn(ClientObjectManager* manager, IPlayerCharacter source, RenderTarget target, double now, float alpha)
    {
        var character = (Character*)source.Address;
        if (character == null || !character->IsReadyToDraw()) return null;
        // Copy values, never a whole draw-data container (which owns native pointers).
        var customize = character->DrawData.CustomizeData;
        var equipment = character->DrawData.EquipmentModelIds.ToArray();
        var glasses = character->DrawData.GlassesIds[0];
        var hatHidden = character->DrawData.IsHatHidden;
        var visor = character->DrawData.IsVisorToggled;
        var weaponHidden = character->DrawData.IsWeaponHidden;
        var weapons = character->DrawData.WeaponData.ToArray().Select(w => (w.ModelId, w.Flags1, w.Flags2, w.State)).ToArray();
        var index = manager->CreateBattleCharacter();
        if (index == uint.MaxValue || index > ushort.MaxValue) { Status = "No free local actor slots; showing waypoint."; return null; }
        var actor = (BattleChara*)manager->GetObjectByIndex((ushort)index);
        if (actor == null) { manager->DeleteObjectByIndex((ushort)index, 0); return null; }
        try
        {
            actor->ObjectKind = ObjectKind.BattleNpc;
            actor->TargetableStatus = 0;
            actor->SetPosition(target.Position.X, target.Position.Y, target.Position.Z);
            actor->SetRotation(target.Position.Yaw);
            actor->Alpha = Math.Clamp(alpha, .1f, .8f);
            if (!customize.Normalize(&customize)) throw new ArgumentException("Invalid character customization.");
            actor->DrawData.CustomizeData = customize;
            for (var i = 0; i < Math.Min(equipment.Length, actor->DrawData.EquipmentModelIds.Length); i++)
                actor->DrawData.Equipment((DrawDataContainer.EquipmentSlot)i) = equipment[i];
            for (var i = 0; i < Math.Min(weapons.Length, actor->DrawData.WeaponData.Length); i++)
            {
                ref var weapon = ref actor->DrawData.Weapon((DrawDataContainer.WeaponSlot)i);
                weapon.ModelId = weapons[i].ModelId;
                weapon.Flags1 = weapons[i].Flags1; weapon.Flags2 = weapons[i].Flags2; weapon.State = weapons[i].State;
            }
            actor->DrawData.IsHatHidden = hatHidden;
            actor->DrawData.IsVisorToggled = visor;
            actor->DrawData.IsWeaponHidden = weaponHidden;
            actor->DrawData.SetGlasses(0, glasses);
            return new((ushort)index, (nint)actor, now);
        }
        catch { manager->DeleteObjectByIndex((ushort)index, 0); throw; }
    }

    private EmoteDefinition? FindEmote(string command)
    {
        if (emotes.TryGetValue(command, out var cached)) return cached;
        EmoteDefinition? result = null;
        foreach (var emote in data.GetExcelSheet<Emote>())
        {
            if (emote.TextCommand.RowId == 0) continue;
            var text = emote.TextCommand.Value;
            if (!new[] { text.Command.ToString(), text.ShortCommand.ToString(), text.Alias.ToString(), text.ShortAlias.ToString() }
                    .Any(s => s.TrimStart('/').Equals(command, StringComparison.OrdinalIgnoreCase))) continue;
            result = new(emote.RowId, emote.ActionTimeline[0].RowId is > 0 and <= ushort.MaxValue ? emote.ActionTimeline[0].Value : null);
            break;
        }
        emotes[command] = result;
        return result;
    }

    private static BattleChara* Resolve(ClientObjectManager* manager, Owned owned)
    {
        var current = manager->GetObjectByIndex(owned.Index);
        return (nint)current == owned.Address ? (BattleChara*)current : null;
    }

    private void Delete(ClientObjectManager* manager, string id)
    {
        if (!actors.Remove(id, out var owned)) return;
        var actor = Resolve(manager, owned);
        if (actor == null) return;
        actor->DisableDraw(); manager->DeleteObjectByIndex(owned.Index, 0);
    }

    public void Clear()
    {
        var manager = ClientObjectManager.Instance();
        if (manager != null) foreach (var id in actors.Keys.ToArray()) Delete(manager, id);
        actors.Clear(); retryAfter.Clear(); Status = "";
    }

    // TerritoryChanged fires after the engine has rebuilt its object pools.
    // Never dereference or delete handles from the previous pool.
    public void ForgetOnZoneChange() { actors.Clear(); retryAfter.Clear(); Status = ""; }
}
