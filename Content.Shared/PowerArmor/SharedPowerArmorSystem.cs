using System;
using Content.Shared.ActionBlocker;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Serialization;

namespace Content.Shared.PowerArmor;

/// <summary>Entering/leaving power armour, and matching pieces to mounting points.</summary>
public abstract class SharedPowerArmorSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PowerArmorFrameComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<PowerArmorFrameComponent, ItemSlotInsertAttemptEvent>(OnSlotInsertAttempt);
        SubscribeLocalEvent<PowerArmorFrameComponent, ItemSlotEjectAttemptEvent>(OnSlotEjectAttempt);

        SubscribeLocalEvent<PowerArmorPilotComponent, CanAttackFromContainerEvent>(OnCanAttackFromContainer);
        SubscribeLocalEvent<PowerArmorPilotComponent, AccessibleOverrideEvent>(OnAccessibleOverride);
        SubscribeLocalEvent<PowerArmorPilotComponent, InRangeOverrideEvent>(OnInRangeOverride);
        SubscribeLocalEvent<PowerArmorPilotComponent, EntGotRemovedFromContainerMessage>(OnPilotRemoved);
    }

    protected virtual void OnStartup(EntityUid uid, PowerArmorFrameComponent component, ComponentStartup args)
    {
        component.PilotSlot = _container.EnsureContainer<ContainerSlot>(uid, component.PilotSlotId);
    }

    /// <summary>Maps a mounting point to the sprite layer it draws into.</summary>
    public static PowerArmorVisualLayers LayerFor(PowerArmorPieceSlot slot) => (PowerArmorVisualLayers) (int) slot;

    /// <summary>True when nobody is occupying the frame.</summary>
    public bool IsEmpty(EntityUid uid, PowerArmorFrameComponent? component = null)
    {
        return Resolve(uid, ref component) && component.PilotSlot.ContainedEntity == null;
    }

    /// <summary>Whether <paramref name="toInsert"/> is allowed to occupy the frame.</summary>
    public bool CanInsert(EntityUid uid, EntityUid toInsert, PowerArmorFrameComponent? component = null)
    {
        if (!Resolve(uid, ref component) || component.Broken)
            return false;

        if (component.PilotSlot.ContainedEntity != null)
            return false;

        if (_whitelist.IsWhitelistFail(component.PilotWhitelist, toInsert))
            return false;

        return _actionBlocker.CanMove(toInsert);
    }

    /// <summary>Moves <paramref name="toInsert"/> into the frame and relays its input to it.</summary>
    public bool TryInsert(EntityUid uid, EntityUid toInsert, PowerArmorFrameComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (toInsert == EntityUid.Invalid || component.PilotSlot.ContainedEntity == toInsert)
            return false;

        if (!CanInsert(uid, toInsert, component))
            return false;

        SetupPilot(uid, toInsert);
        _container.Insert(toInsert, component.PilotSlot);

        component.Pilot = toInsert;
        Dirty(uid, component);
        return true;
    }

    /// <summary>Moves the occupant out, leaving them standing on the frame.</summary>
    public bool TryEject(EntityUid uid, PowerArmorFrameComponent? component = null, EntityUid? pilot = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        pilot ??= component.PilotSlot.ContainedEntity;

        if (pilot == null)
            return false;

        if (component.Broken && !component.AllowExitWhenBroken)
            return false;

        RemovePilot(pilot.Value);
        _container.RemoveEntity(uid, pilot.Value);

        component.Pilot = null;
        Dirty(uid, component);
        return true;
    }

    /// <summary>Installs a piece into the mounting point its <see cref="PowerArmorPieceComponent"/> declares.</summary>
    public bool TryInstallPiece(EntityUid frame, EntityUid piece, EntityUid? user = null)
    {
        if (!TryComp<PowerArmorPieceComponent>(piece, out var pieceComp))
            return false;

        return _itemSlots.TryInsert(frame, PowerArmorSlotIds.IdFor(pieceComp.Slot), piece, user);
    }

    /// <summary>Returns the piece installed in a mounting point, if any.</summary>
    public EntityUid? GetPiece(EntityUid frame, PowerArmorPieceSlot slot, ItemSlotsComponent? slots = null)
    {
        return _itemSlots.GetItemOrNull(frame, PowerArmorSlotIds.IdFor(slot), slots);
    }

    private void SetupPilot(EntityUid frame, EntityUid pilot)
    {
        var pilotComp = EnsureComp<PowerArmorPilotComponent>(pilot);
        pilotComp.Frame = frame;
        Dirty(pilot, pilotComp);

        // Mirrors the mech system, bypassing most interaction gating on the occupant.
        EnsureComp<InteractionRelayComponent>(pilot);
        _mover.SetRelay(pilot, frame);
        _interaction.SetRelay(pilot, frame);
    }

    private void RemovePilot(EntityUid pilot)
    {
        RemComp<PowerArmorPilotComponent>(pilot);
        RemComp<RelayInputMoverComponent>(pilot);
        RemComp<InteractionRelayComponent>(pilot);
    }

    /// <summary>A piece only enters its own mounting point, and only while the frame is empty.</summary>
    private void OnSlotInsertAttempt(EntityUid uid, PowerArmorFrameComponent frame, ref ItemSlotInsertAttemptEvent args)
    {
        if (!PowerArmorSlotIds.TrySlotForId(args.Slot.ID, out var expected))
            return;

        if (!TryComp<PowerArmorPieceComponent>(args.Item, out var piece)
            || piece.Slot != expected)
        {
            args.Cancelled = true;
            return;
        }

        if (frame.PilotSlot.ContainedEntity != null && !frame.AllowModificationWhileOccupied)
            args.Cancelled = true;
    }

    private void OnSlotEjectAttempt(EntityUid uid, PowerArmorFrameComponent frame, ref ItemSlotEjectAttemptEvent args)
    {
        if (frame.PilotSlot.ContainedEntity != null && !frame.AllowModificationWhileOccupied)
            args.Cancelled = true;
    }

    /// <summary>Occupants act as the frame, so attacking out of the container is allowed.</summary>
    private void OnCanAttackFromContainer(EntityUid uid, PowerArmorPilotComponent component, CanAttackFromContainerEvent args)
    {
        args.CanAttack = true;
    }

    private void OnAccessibleOverride(EntityUid uid, PowerArmorPilotComponent component, ref AccessibleOverrideEvent args)
    {
        if (args.User != uid)
            return;

        args.Handled = true;
        args.Accessible = _interaction.IsAccessible(component.Frame, args.Target);
    }

    private void OnInRangeOverride(EntityUid uid, PowerArmorPilotComponent component, ref InRangeOverrideEvent args)
    {
        if (args.User != uid)
            return;

        args.Handled = true;
        args.InRange = _interaction.InRangeUnobstructed(component.Frame, args.Target);
    }

    /// <summary>The occupant left by some route other than eject; tear the relay down.</summary>
    private void OnPilotRemoved(EntityUid uid, PowerArmorPilotComponent component, EntGotRemovedFromContainerMessage args)
    {
        var frame = component.Frame;

        RemComp<RelayInputMoverComponent>(uid);
        RemComp<InteractionRelayComponent>(uid);
        RemComp<PowerArmorPilotComponent>(uid);

        if (TryComp<PowerArmorFrameComponent>(frame, out var frameComp) && frameComp.Pilot == uid)
        {
            frameComp.Pilot = null;
            Dirty(frame, frameComp);
        }
    }
}

[Serializable, NetSerializable]
public sealed partial class PowerArmorEntryEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class PowerArmorExitEvent : SimpleDoAfterEvent;