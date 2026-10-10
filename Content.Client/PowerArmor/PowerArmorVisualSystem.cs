using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client.Inventory;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Item;
using Content.Shared.PowerArmor;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Containers;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client.PowerArmor;

/// Composes a frame's sprite and mirrors the occupant's head onto it.
public sealed partial class PowerArmorVisualSystem : SharedPowerArmorSystem
{
    /// Layers mirrored from the pilot.
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

    /// Head/face slots that get mirrored.
    private static readonly string[] AccessorySlots =
    [
        "head", "eyes", "mask", "ears", "neck",
    ];

    private static readonly MarkingCategories[] HairCategories =
    [
        MarkingCategories.Hair, MarkingCategories.FacialHair,
    ];

    private static readonly MarkingCategories[] UndergarmentCategories =
    [
        MarkingCategories.UndergarmentTop, MarkingCategories.UndergarmentBottom,
    ];

    /// Hidden inside the chassis; cuffs belong to CuffableSystem and stay hidden on exit.
    private static readonly HumanoidVisualLayers[] BodyLayers =
    [
        HumanoidVisualLayers.Chest,
        HumanoidVisualLayers.RArm,
        HumanoidVisualLayers.LArm,
        HumanoidVisualLayers.RLeg,
        HumanoidVisualLayers.LLeg,
        HumanoidVisualLayers.RHand,
        HumanoidVisualLayers.LHand,
        HumanoidVisualLayers.RFoot,
        HumanoidVisualLayers.LFoot,
        HumanoidVisualLayers.Tail,
        HumanoidVisualLayers.Wings,
        HumanoidVisualLayers.Handcuffs,
        HumanoidVisualLayers.UndergarmentTop,
        HumanoidVisualLayers.UndergarmentBottom,
        HumanoidVisualLayers.Arms,
        HumanoidVisualLayers.Legs,
        HumanoidVisualLayers.Special,
    ];

    /// Restored on exit; cuffs stay hidden.
    private static readonly HumanoidVisualLayers[] BodyLayersToRestore =
    [
        HumanoidVisualLayers.Chest,
        HumanoidVisualLayers.RArm,
        HumanoidVisualLayers.LArm,
        HumanoidVisualLayers.RLeg,
        HumanoidVisualLayers.LLeg,
        HumanoidVisualLayers.RHand,
        HumanoidVisualLayers.LHand,
        HumanoidVisualLayers.RFoot,
        HumanoidVisualLayers.LFoot,
        HumanoidVisualLayers.Tail,
        HumanoidVisualLayers.Wings,
        HumanoidVisualLayers.UndergarmentTop,
        HumanoidVisualLayers.UndergarmentBottom,
        HumanoidVisualLayers.Arms,
        HumanoidVisualLayers.Legs,
        HumanoidVisualLayers.Special,
    ];

    /// Key prefixes for mirrored layers.
    private static string FrameLayerKey(HumanoidVisualLayers layer) => $"PowerArmor_{layer}";
    private static string FrameExtraKey(string pilotKey) => $"PowerArmor_Extra_{pilotKey}";

    /// Wrecked chassis tint; the mirrored head keeps its own colour.
    private static readonly Color WreckedColor = new(0.72f, 0.70f, 0.68f);

    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private SharedHumanoidAppearanceSystem _humanoidAppearance = default!;
    [Dependency] private MarkingManager _markingManager = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PowerArmorFrameComponent, EntInsertedIntoContainerMessage>(OnPieceFitted);
        SubscribeLocalEvent<PowerArmorFrameComponent, EntRemovedFromContainerMessage>(OnPieceRemoved);
        SubscribeLocalEvent<PowerArmorFrameComponent, AfterAutoHandleStateEvent>(OnFrameStateChanged);
        SubscribeLocalEvent<PowerArmorFrameComponent, ComponentShutdown>(OnFrameShutdown);

        SubscribeLocalEvent<PowerArmorPilotComponent, ComponentStartup>(OnPilotStartup);
        SubscribeLocalEvent<PowerArmorPilotComponent, ComponentShutdown>(OnPilotShutdown);
        // Re-mirror when the occupant's kit changes.
        SubscribeLocalEvent<PowerArmorPilotComponent, AppearanceChangeEvent>(OnPilotAppearanceChanged);
        SubscribeLocalEvent<PowerArmorPilotComponent, VisualsChangedEvent>(OnPilotAppearanceChanged);
    }

    /// <summary>Last known wreck state per frame, so damage ticks do not re-mirror the head.</summary>
    private readonly Dictionary<EntityUid, bool> _lastBroken = new();

    private void OnFrameStateChanged(Entity<PowerArmorFrameComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        // Integrity is dirtied on every hit but no visual reads it, so a full re-mirror per damage
        // tick would rebuild every layer for nothing. Broken is the only state UpdateVisuals uses.
        if (_lastBroken.TryGetValue(ent.Owner, out var was) && was == ent.Comp.Broken)
            return;

        _lastBroken[ent.Owner] = ent.Comp.Broken;
        ApplyWreckCollision(ent);
        Refresh(ent);
    }

    private void OnFrameShutdown(Entity<PowerArmorFrameComponent> ent, ref ComponentShutdown args)
        => _lastBroken.Remove(ent.Owner);

    private void OnPieceFitted(Entity<PowerArmorFrameComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        // The gear stash churning must not rebuild the sprite.
        if (args.Container.ID == PowerArmorFrameComponent.IssuedGearContainerId)
            return;

        Refresh(ent);
    }

    private void OnPieceRemoved(Entity<PowerArmorFrameComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == PowerArmorFrameComponent.IssuedGearContainerId)
            return;

        Refresh(ent);
    }

    /// <summary>
    /// Walks the marking layers in the given categories. Markings are not in the LayerMap by an
    /// enum, so they are found by the "<c>{marking}-{state}</c>" key ApplyMarking gives them.
    /// </summary>
    private void ForEachMarkingLayer(EntityUid uid, SpriteComponent sprite, HumanoidAppearanceComponent? humanoid,
        MarkingCategories[] categories, Action<string> act)
    {
        if (humanoid?.MarkingSet?.Markings == null)
            return;

        foreach (var category in categories)
        {
            if (!humanoid.MarkingSet.Markings.TryGetValue(category, out var markings))
                continue;

            foreach (var marking in markings)
            {
                if (!_markingManager.TryGetMarking(marking, out var proto))
                    continue;

                foreach (var spec in proto.Sprites)
                {
                    if (spec is SpriteSpecifier.Rsi rsi)
                        act($"{proto.ID}-{rsi.RsiState}");
                }
            }
        }
    }

    private void SetMarkingLayers(EntityUid uid, SpriteComponent sprite, HumanoidAppearanceComponent? humanoid,
        MarkingCategories[] categories, bool visible)
        => ForEachMarkingLayer(uid, sprite, humanoid, categories, layerId => SetLayerVisible(uid, sprite, layerId, visible));

    /// <summary>Sets every layer in a slot's visual keys, optionally sparing head accessories.</summary>
    private void SetClothingLayers(EntityUid uid, SpriteComponent sprite, bool visible, bool spareHeadAccessories)
    {
        if (!TryComp<InventorySlotsComponent>(uid, out var slots))
            return;

        foreach (var (slotName, layerKeys) in slots.VisualLayerKeys)
        {
            if (spareHeadAccessories && IsHeadAccessorySlot(slotName))
                continue;

            foreach (var key in layerKeys)
                SetLayerVisible(uid, sprite, key, visible);
        }
    }

    private void SetLayerVisible(EntityUid uid, SpriteComponent sprite, string key, bool visible)
    {
        if (_sprite.LayerMapTryGet((uid, sprite), key, out var index, false))
            _sprite.LayerSetVisible((Layer)sprite[index], visible);
    }

    private void HidePilotBodyLayers(EntityUid pilotUid, bool helmeted = false)
    {
        if (!TryComp<SpriteComponent>(pilotUid, out var pilotSprite))
            return;

        TryComp<HumanoidAppearanceComponent>(pilotUid, out var humanoid);

        // Via the appearance system: tracks HiddenLayers, so exit cannot undo a severed limb.
        _humanoidAppearance.SetLayersVisibility(pilotUid, BodyLayers, false, humanoid: humanoid);

        // Set both ways, so taking the helmet off inside gives the head back.
        _humanoidAppearance.SetLayersVisibility(pilotUid, HeadLayers, !helmeted, humanoid: humanoid);

        SetMarkingLayers(pilotUid, pilotSprite, humanoid, UndergarmentCategories, visible: false);

        // Clothing is hidden; head/face accessories are mirrored instead.
        SetClothingLayers(pilotUid, pilotSprite, visible: false, spareHeadAccessories: true);
    }

    protected override void OnStartup(EntityUid uid, PowerArmorFrameComponent component, ComponentStartup args)
    {
        base.OnStartup(uid, component, args);

        // Apply wreck collision even if it loaded wrecked.
        ApplyWreckCollision((uid, component));
        Refresh((uid, component));
    }

    private void OnPilotStartup(EntityUid uid, PowerArmorPilotComponent component, ComponentStartup args)
    {
        if (TryComp<PowerArmorFrameComponent>(component.Frame, out var frame))
            UpdatePilotVisuals((component.Frame, frame));
    }

    private void OnPilotShutdown(EntityUid uid, PowerArmorPilotComponent component, ComponentShutdown args)
        => RestorePilotVisuals(component.Frame, uid);

    /// So a hat put on inside shows up straight away.
    private void OnPilotAppearanceChanged(EntityUid uid, PowerArmorPilotComponent component, ref AppearanceChangeEvent args)
        => RefreshPilot(component.Frame);

    private void OnPilotAppearanceChanged(EntityUid uid, PowerArmorPilotComponent component, ref VisualsChangedEvent args)
        => RefreshPilot(component.Frame);

    private void RefreshPilot(EntityUid frame)
    {
        if (TryComp<PowerArmorFrameComponent>(frame, out var frameComp))
            UpdatePilotVisuals((frame, frameComp));
    }

    private void Refresh(Entity<PowerArmorFrameComponent> ent)
    {
        UpdateVisuals(ent);
        UpdatePilotVisuals(ent);
    }

    private void UpdateVisuals(Entity<PowerArmorFrameComponent> ent)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        var tint = ent.Comp.Broken ? WreckedColor : Color.White;

        foreach (var slot in PowerArmorSlotIds.All)
        {
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

        // Any armour set fits, since the RSI comes from the piece.
            _sprite.LayerSetRsi(layer, pieceSprite.BaseRSI);
            _sprite.LayerSetRsiState(layer, pieceComp.WornStateName);
            _sprite.LayerSetVisible(layer, true);
            _sprite.LayerSetColor(layer, tint);
        }

        // The chassis wears the tint too.
        if (_sprite.TryGetLayer((ent.Owner, sprite), PowerArmorVisualLayers.Frame, out var chassis, false))
            _sprite.LayerSetColor(chassis, tint);
    }

    private void UpdatePilotVisuals(Entity<PowerArmorFrameComponent> ent)
    {
        var frameUid = ent.Owner;

        if (ent.Comp.PilotSlot?.ContainedEntity is not { } pilotUid
            || !TryComp<SpriteComponent>(pilotUid, out var pilotSprite)
            || !TryComp<SpriteComponent>(ent, out var frameSprite))
        {
            return;
        }

        // Reads as still-fitted during a removal otherwise, leaving the head hidden.
        var helmeted = GetPiece(ent, PowerArmorPieceSlot.Helmet) != null;
        // OverMobs occludes the body outright.
        _sprite.SetVisible((pilotUid, pilotSprite), false);

        HidePilotBodyLayers(pilotUid, helmeted);

        // Undergarments are marking layers that HumanoidAppearanceSystem adds a beat after insert.
        // No timer retry: their appearance event re-runs this whole refresh, which hides them.
        // Mirror head/accessories to the frame sprite.
        EnsureMirroredLayers(frameUid, frameSprite, pilotSprite, pilotUid, helmeted);
    }

    private static bool IsHeadAccessorySlot(string slotName)
    {
        return Array.IndexOf(AccessorySlots, slotName) != -1;
    }

    private void EnsureMirroredLayers(EntityUid frameUid, SpriteComponent frameSprite, SpriteComponent pilotSprite, EntityUid pilotUid, bool helmeted)
    {
        // Clear first: recomputing from current kit stranded hats removed mid-exit.
        ClearMirroredLayers(frameUid, frameSprite);

        // Anchored past the helmet so armour covers the face; the chassis anchor drew masks over it.
        if (!_sprite.LayerMapTryGet((frameUid, frameSprite), PowerArmorVisualLayers.Helmet, out var frameLayerIndex, false)
            && !_sprite.LayerMapTryGet((frameUid, frameSprite), PowerArmorVisualLayers.Frame, out frameLayerIndex, false))
        {
            return;
        }

        // Align the head with the collar notch.
        var headLift = Vector2.Zero;
        if (TryComp<PowerArmorFrameComponent>(frameUid, out var frameComp))
            headLift = frameComp.PilotHeadOffset;

        // Local, not the shared set: a re-entrant refresh would clear it and blow the indices.
        var mirrored = new HashSet<string>();

        // 1. Copy base humanoid head layers (includes Hair, FacialHair base layers).
        foreach (var headLayer in HeadLayers)
        {
            var key = FrameLayerKey(headLayer);
            CopyLayerToFrame(frameUid, frameSprite, pilotSprite, pilotUid, headLayer, key, frameLayerIndex, mirrored, helmeted, headLift);
        }

        // 2. Copy hair/facial hair MARKING layers (the actual hair sprites).
        // 2. Hair and facial hair marking layers, keyed "<marking>-<state>".
        CopyHairMarkingLayers(frameUid, frameSprite, pilotSprite, pilotUid, frameLayerIndex, mirrored, helmeted, headLift);

        // 3. Clothing layers for head/face slots.
        if (TryComp<InventorySlotsComponent>(pilotUid, out var inventorySlots))
        {
            foreach (var slotName in AccessorySlots)
            {
                if (!inventorySlots.VisualLayerKeys.TryGetValue(slotName, out var layerKeys))
                    continue;

                foreach (var pilotKey in layerKeys)
                {
                    CopyAccessoryLayerToFrameDynamic(frameUid, frameSprite, pilotSprite, pilotUid, pilotKey, frameLayerIndex, mirrored, helmeted, headLift);
                }
            }
        }

        // Committed last, so a re-entrant refresh cannot strand a half-built set.
        if (frameComp != null)
            frameComp.MirroredKeys = mirrored;
    }

    private void CopyHairMarkingLayers(EntityUid frameUid, SpriteComponent frameSprite, SpriteComponent pilotSprite, EntityUid pilotUid,
        int frameLayerIndex, HashSet<string> mirrored, bool helmeted, Vector2 headLift)
    {
        TryComp<HumanoidAppearanceComponent>(pilotUid, out var humanoid);

        ForEachMarkingLayer(pilotUid, pilotSprite, humanoid, HairCategories, layerId =>
        {
            if (_sprite.LayerMapTryGet((pilotUid, pilotSprite), layerId, out _, false))
                CopyAccessoryLayerToFrameDynamic(frameUid, frameSprite, pilotSprite, pilotUid, layerId, frameLayerIndex, mirrored, helmeted, headLift);
        });
    }

    private void CopyLayerToFrame(EntityUid frameUid, SpriteComponent frameSprite, SpriteComponent pilotSprite, EntityUid pilotUid,
        HumanoidVisualLayers headLayer, string frameKey, int frameLayerIndex, HashSet<string> mirrored,
        bool helmeted, Vector2 headLift)
    {
        if (_sprite.LayerMapTryGet((frameUid, frameSprite), frameKey, out _, false))
            _sprite.RemoveLayer((frameUid, frameSprite), frameKey, false);

        if (!_sprite.LayerMapTryGet((pilotUid, pilotSprite), headLayer, out var pilotLayerIndex, false))
            return;

        var pilotLayer = (Layer)pilotSprite[pilotLayerIndex];

        var newIndex = frameLayerIndex + 1 + mirrored.Count;
        var newLayer = _sprite.AddBlankLayer((frameUid, frameSprite), newIndex);

        _sprite.LayerMapSet((frameUid, frameSprite), frameKey, newIndex);

        mirrored.Add(frameKey);

        CopyLayerData(pilotLayer, newLayer, helmeted, headLift);
    }

    private void CopyAccessoryLayerToFrameDynamic(EntityUid frameUid, SpriteComponent frameSprite, SpriteComponent pilotSprite, EntityUid pilotUid,
        string pilotKey, int frameLayerIndex, HashSet<string> mirrored, bool helmeted, Vector2 headLift)
    {
        var frameKey = FrameExtraKey(pilotKey);

        if (_sprite.LayerMapTryGet((frameUid, frameSprite), frameKey, out _, false))
            _sprite.RemoveLayer((frameUid, frameSprite), frameKey, false);

        if (!_sprite.LayerMapTryGet((pilotUid, pilotSprite), pilotKey, out var pilotLayerIndex, false))
            return;

        var pilotLayer = (Layer)pilotSprite[pilotLayerIndex];
        if (!pilotLayer.Visible)
            return;

        var newIndex = frameLayerIndex + 1 + mirrored.Count;
        var newLayer = _sprite.AddBlankLayer((frameUid, frameSprite), newIndex);

        _sprite.LayerMapSet((frameUid, frameSprite), frameKey, newIndex);

        mirrored.Add(frameKey);

        CopyLayerData(pilotLayer, newLayer, helmeted, headLift);
    }

    private void CopyLayerData(Layer src, Layer dst, bool helmeted, Vector2 headLift)
    {
        _sprite.LayerSetRsi(dst, src.RSI);
        _sprite.LayerSetRsiState(dst, src.State);
        dst.Color = src.Color;
        dst.Shader = src.Shader;
        dst.RenderingStrategy = src.RenderingStrategy;
        // Scale and rotation too: clothing applies a species displacement scale, and skipping it
        // mirrors accessories at the wrong size.
        _sprite.LayerSetScale(dst, src.Scale);
        _sprite.LayerSetRotation(dst, src.Rotation);
        _sprite.LayerSetDirOffset(dst, src.DirOffset);
        _sprite.LayerSetVisible(dst, !helmeted);
        _sprite.LayerSetOffset(dst, src.Offset + headLift);
    }

    /// Removes mirrored layers and forgets their keys.
    private void ClearMirroredLayers(EntityUid frameUid, SpriteComponent frameSprite)
    {
        if (!TryComp<PowerArmorFrameComponent>(frameUid, out var frame) || frame.MirroredKeys.Count == 0)
            return;

        foreach (var key in frame.MirroredKeys)
        {
            if (_sprite.LayerMapTryGet((frameUid, frameSprite), key, out _, false))
                _sprite.RemoveLayer((frameUid, frameSprite), key, false);
        }

        frame.MirroredKeys.Clear();
    }

    private void RestorePilotVisuals(EntityUid frameUid, EntityUid pilot)
    {
        if (!TryComp<SpriteComponent>(frameUid, out var frameSprite))
            return;

        ClearMirroredLayers(frameUid, frameSprite);

                if (pilot != default && TryComp<SpriteComponent>(pilot, out var pilotSprite))
        {
            // The sprite was hidden for the whole ride.
            _sprite.SetVisible((pilot, pilotSprite), true);

            // Clears HiddenLayers only, so severed limbs and other systems' layers survive.
            TryComp<HumanoidAppearanceComponent>(pilot, out var humanoid);
            _humanoidAppearance.SetLayersVisibility(pilot, BodyLayersToRestore, true, humanoid: humanoid);
            _humanoidAppearance.SetLayersVisibility(pilot, HeadLayers, true, humanoid: humanoid);

            SetClothingLayers(pilot, pilotSprite, visible: true, spareHeadAccessories: false);
            SetMarkingLayers(pilot, pilotSprite, humanoid, HairCategories, visible: true);
            SetMarkingLayers(pilot, pilotSprite, humanoid, UndergarmentCategories, visible: true);
        }
    }
}
