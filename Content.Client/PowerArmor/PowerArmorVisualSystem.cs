using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.PowerArmor;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.Client.PowerArmor;

/// <summary>Snapshot of an occupant's sprite, for restoring on the way out.</summary>
[RegisterComponent]
public sealed partial class PowerArmorPilotVisualsComponent : Component
{
    /// <summary>Visibility of every suppressed layer, keyed by index.</summary>
    public Dictionary<int, bool> HiddenLayers = new();

    /// <summary>Baseline scale of each head layer, keyed by layer.</summary>
    public Dictionary<HumanoidVisualLayers, Vector2> HeadScales = new();

    /// <summary>Baseline offset of each head layer, keyed by layer.</summary>
    public Dictionary<HumanoidVisualLayers, Vector2> HeadOffsets = new();

    /// <summary>Layer count the snapshot was taken at.</summary>
    public int CapturedLayers;
}

public sealed partial class PowerArmorVisualSystem : SharedPowerArmorSystem
{
    /// <summary>Head-only view; StencilMask is excluded, as ClientClothingSystem asserts it hidden.</summary>
    private static readonly HumanoidVisualLayers[] HeadLayers =
    [
        HumanoidVisualLayers.Head,
        HumanoidVisualLayers.HeadSide,
        HumanoidVisualLayers.HeadTop,
        HumanoidVisualLayers.Snout,
        HumanoidVisualLayers.Eyes,
        HumanoidVisualLayers.Face,
        HumanoidVisualLayers.Hair,
        HumanoidVisualLayers.FacialHair,
    ];

    [Dependency] private readonly SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PowerArmorFrameComponent, EntInsertedIntoContainerMessage>(OnFrameChanged);
        SubscribeLocalEvent<PowerArmorFrameComponent, EntRemovedFromContainerMessage>(OnFrameChanged);
        SubscribeLocalEvent<PowerArmorFrameComponent, AfterAutoHandleStateEvent>(OnFrameChanged);

        SubscribeLocalEvent<PowerArmorPilotComponent, ComponentStartup>(OnPilotStartup);
        SubscribeLocalEvent<PowerArmorPilotComponent, ComponentShutdown>(OnPilotShutdown);

    }

    protected override void OnStartup(EntityUid uid, PowerArmorFrameComponent component, ComponentStartup args)
    {
        base.OnStartup(uid, component, args);
        Refresh((uid, component));
    }

    /// <summary>Reapplies the head-only view, which the humanoid appearance system wipes on replicate.</summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var (pilotComp, state) in
                     EntityQuery<PowerArmorPilotComponent, PowerArmorPilotVisualsComponent>().ToList())
        {
            var pilot = pilotComp.Owner;

            if (!TryComp<PowerArmorFrameComponent>(pilotComp.Frame, out var frame)
                || !TryComp<SpriteComponent>(pilot, out var pilotSprite)
                || frame.PilotSlot?.ContainedEntity != pilot)
            {
                continue;
            }

            if (HeadStillApplied(pilot, pilotComp.Frame, pilotSprite, state, frame))
                continue;

            UpdatePilotVisuals((pilotComp.Frame, frame));
        }
    }

    /// <summary>True while the head-only view is still fully applied.</summary>
    private bool HeadStillApplied(
        EntityUid pilot,
        EntityUid frameUid,
        SpriteComponent pilotSprite,
        PowerArmorPilotVisualsComponent state,
        PowerArmorFrameComponent frame)
    {
        var head = HumanoidVisualLayers.Head;

        if (!state.HeadScales.TryGetValue(head, out var baseScale)
            || !_sprite.LayerMapTryGet((pilot, pilotSprite), head, out var headIndex, false))
        {
            return false;
        }

        var shouldShow = GetPiece(frameUid, PowerArmorPieceSlot.Helmet) == null;
        var headLayer = (SpriteComponent.Layer) pilotSprite[headIndex];

        if (headLayer.Scale != baseScale || headLayer.Visible != shouldShow)
            return false;

        // The rebuild un-hides the body too, so spot-check a body layer.
        return _sprite.LayerMapTryGet((pilot, pilotSprite), HumanoidVisualLayers.Chest, out var chestIndex, false)
            && !pilotSprite[chestIndex].Visible;
    }

    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref EntInsertedIntoContainerMessage args) => Refresh(ent);
    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref EntRemovedFromContainerMessage args) => Refresh(ent);
    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent);

    private void OnPilotStartup(EntityUid uid, PowerArmorPilotComponent component, ComponentStartup args)
    {
        if (TryComp<PowerArmorFrameComponent>(component.Frame, out var frame))
            UpdatePilotVisuals((component.Frame, frame));
    }

    private void OnPilotShutdown(EntityUid uid, PowerArmorPilotComponent component, ComponentShutdown args)
        => RestorePilotVisuals(uid);

    private void Refresh(Entity<PowerArmorFrameComponent> ent)
    {
        UpdateVisuals(ent);
        UpdatePilotVisuals(ent);
    }

    private void UpdateVisuals(Entity<PowerArmorFrameComponent> ent)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        foreach (var slot in PowerArmorSlotIds.All)
        {
            // Via the layer map; `map:` keys parse into the enum value.
            if (!_sprite.TryGetLayer((ent.Owner, sprite), LayerFor(slot), out var layer, false))
                continue;

            var piece = GetPiece(ent, slot);

            if (piece is not { } pieceUid
                || !TryComp<PowerArmorPieceComponent>(pieceUid, out var pieceComp)
                || !TryComp<SpriteComponent>(pieceUid, out var pieceSprite)
                || pieceSprite.BaseRSI == null)
            {
                _sprite.LayerSetVisible(layer, false);
                continue;
            }

            _sprite.LayerSetRsi(layer, pieceSprite.BaseRSI);
            _sprite.LayerSetRsiState(layer, pieceComp.WornStateName);
            _sprite.LayerSetVisible(layer, true);
        }
    }

    private void UpdatePilotVisuals(Entity<PowerArmorFrameComponent> ent)
    {
        var pilot = ent.Comp.PilotSlot?.ContainedEntity;

        if (pilot is not { } pilotUid || !TryComp<SpriteComponent>(pilotUid, out var sprite))
            return;

        var helmeted = GetPiece(ent, PowerArmorPieceSlot.Helmet) != null;
        var state = EnsureComp<PowerArmorPilotVisualsComponent>(pilotUid);

        // Re-snapshot if layers were added or removed while the occupant was inside.
        var layerCount = sprite.AllLayers.Count();
        if (state.CapturedLayers != layerCount)
        {
            state.HiddenLayers.Clear();
            state.CapturedLayers = layerCount;

            var i = 0;
            foreach (var layer in sprite.AllLayers)
                state.HiddenLayers[i++] = layer.Visible;
        }

        foreach (var layer in sprite.AllLayers)
            layer.Visible = false;

        foreach (var head in HeadLayers)
        {
            if (!_sprite.LayerMapTryGet((pilotUid, sprite), head, out var layerIndex, false))
                continue;

            var headLayer = (SpriteComponent.Layer) sprite[layerIndex];

            // Captured on first sight: the sprite can still be mid-build.
            state.HeadScales.TryAdd(head, headLayer.Scale);
            state.HeadOffsets.TryAdd(head, headLayer.Offset);

            // The head keeps the mob's scale: the collar notch is only ~2px against an ~8px head.
            _sprite.LayerSetScale((pilotUid, sprite), layerIndex, state.HeadScales[head]);
            _sprite.LayerSetOffset((pilotUid, sprite), layerIndex, state.HeadOffsets[head] + ent.Comp.PilotHeadOffset);

            sprite[layerIndex].Visible = !helmeted;
        }
    }

    private void RestorePilotVisuals(EntityUid uid)
    {
        if (!TryComp<PowerArmorPilotVisualsComponent>(uid, out var state))
            return;

        RemComp<PowerArmorPilotVisualsComponent>(uid);

        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var index = 0;
        foreach (var layer in sprite.AllLayers)
        {
            if (index < state.HiddenLayers.Count)
                layer.Visible = state.HiddenLayers[index];

            index++;
        }

        foreach (var (head, scale) in state.HeadScales)
        {
            if (!_sprite.LayerMapTryGet((uid, sprite), head, out var layerIndex, false))
                continue;

            _sprite.LayerSetScale((uid, sprite), layerIndex, scale);
            _sprite.LayerSetOffset((uid, sprite), layerIndex, state.HeadOffsets[head]);
        }
    }
}