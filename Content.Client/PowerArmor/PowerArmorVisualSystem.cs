using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Humanoid;
using Content.Shared.PowerArmor;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.Client.PowerArmor;

/// <summary>Snapshots an occupant's sprite so it can be restored when they climb out.</summary>
[RegisterComponent]
public sealed partial class PowerArmorPilotVisualsComponent : Component
{
    /// <summary>Visibility of every suppressed layer, keyed by index.</summary>
    /// <remarks>Indexed because <c>SpriteComponent.LayerMap</c> is internal.</remarks>
    public Dictionary<int, bool> HiddenLayers = new();

    /// <summary>Layer count the snapshot was taken at, so a stale snapshot can be detected.</summary>
    public int CapturedLayers;

    public Vector2 Offset = Vector2.Zero;
}

/// <summary>Composes a frame's sprite from its installed pieces and hides the occupant's body.</summary>
/// <remarks>The head is hidden explicitly when helmeted: render order is global, not parent-then-child.</remarks>
public sealed class PowerArmorVisualSystem : SharedPowerArmorSystem
{
    /// <summary>Head-only view: everything else is suppressed so it cannot poke through the armour.</summary>
    /// <remarks>Excludes StencilMask, which ClientClothingSystem asserts is hidden with no jumpsuit.</remarks>
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

    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref EntInsertedIntoContainerMessage args) => Refresh(ent);
    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref EntRemovedFromContainerMessage args) => Refresh(ent);
    private void OnFrameChanged(Entity<PowerArmorFrameComponent> ent, ref AfterAutoHandleStateEvent args) => Refresh(ent);

    private void OnPilotStartup(EntityUid uid, PowerArmorPilotComponent component, ComponentStartup args)
    {
        if (TryComp<PowerArmorFrameComponent>(component.Frame, out var frame))
            UpdatePilotVisuals((frame.Owner, frame));
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
            // Via the layer map, not by index: `map:` keys parse into the enum value.
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

            // The RSI comes from the piece, so new armour sets need no frame changes.
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

        var layerCount = sprite.AllLayers.Count();
        if (state.CapturedLayers != layerCount)
        {
            state.HiddenLayers.Clear();
            state.CapturedLayers = layerCount;
            state.Offset = sprite.Offset;

            var i = 0;
            foreach (var layer in sprite.AllLayers)
                state.HiddenLayers[i++] = layer.Visible;
        }

        foreach (var layer in sprite.AllLayers)
            layer.Visible = false;

        if (!helmeted)
        {
            foreach (var head in HeadLayers)
            {
                if (sprite.LayerMapTryGet(head, out var layerIndex))
                    sprite[layerIndex].Visible = true;
            }
        }

        _sprite.SetOffset((pilotUid, sprite), state.Offset + ent.Comp.PilotHeadOffset);
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

        _sprite.SetOffset((uid, sprite), state.Offset);
    }
}