using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared.FixedPoint;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.PowerArmor;

/// <summary>The mounting points a frame exposes for <see cref="PowerArmorPieceComponent"/> items.</summary>
[Serializable, NetSerializable]
public enum PowerArmorPieceSlot : byte
{
    Helmet,
    Chest,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
}

/// <summary>Sprite layers a frame declares, mirroring <see cref="PowerArmorPieceSlot"/> plus the chassis.</summary>
/// <remarks>Draw order comes from the order the prototype lists its layers, not from these values.</remarks>
[Serializable, NetSerializable]
public enum PowerArmorVisualLayers : byte
{
    Helmet = (byte) PowerArmorPieceSlot.Helmet,
    Chest = (byte) PowerArmorPieceSlot.Chest,
    LeftArm = (byte) PowerArmorPieceSlot.LeftArm,
    RightArm = (byte) PowerArmorPieceSlot.RightArm,
    LeftLeg = (byte) PowerArmorPieceSlot.LeftLeg,
    RightLeg = (byte) PowerArmorPieceSlot.RightLeg,
    Frame,
}

/// <summary>Ties <see cref="PowerArmorPieceSlot"/> to the matching <c>ItemSlots</c> key used in YAML.</summary>
public static class PowerArmorSlotIds
{
    /// <summary>ItemSlots keys, indexed by <see cref="PowerArmorPieceSlot"/>.</summary>
    public static readonly string[] Ids = ["helmet", "chest", "leftArm", "rightArm", "leftLeg", "rightLeg"];

    public static IEnumerable<PowerArmorPieceSlot> All => Enum.GetValues<PowerArmorPieceSlot>();

    public static string IdFor(PowerArmorPieceSlot slot) => Ids[(int) slot];

    public static bool TrySlotForId(string? id, out PowerArmorPieceSlot slot)
    {
        var index = Array.IndexOf(Ids, id);
        slot = (PowerArmorPieceSlot) index;
        return index >= 0;
    }
}

/// <summary>A pilotable power armor chassis; occupants climb inside and their input is relayed to it.</summary>
/// <remarks>Armour lives in <c>ItemSlots</c> keyed by <see cref="PowerArmorSlotIds"/>, so it swaps between frames.</remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PowerArmorFrameComponent : Component
{
    public const string DefaultPilotSlotId = "power-armor-pilot-slot";

    /// <summary>Holds the occupant, whose sprite the client system reduces to a bare head.</summary>
    [ViewVariables]
    public ContainerSlot PilotSlot = default!;

    [ViewVariables]
    public readonly string PilotSlotId = DefaultPilotSlotId;

    /// <summary>The entity currently inside the frame.</summary>
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public EntityUid? Pilot;

    /// <summary>Seconds taken to climb in.</summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float EntryDelay = 3f;

    /// <summary>Seconds taken to climb out.</summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ExitDelay = 3f;

    /// <summary>Only entities passing this may occupy the frame.</summary>
    [DataField]
    public EntityWhitelist? PilotWhitelist;

    /// <summary>Fraction of frame damage that carries through to the occupant.</summary>
    [DataField]
    public float DamageBleedThrough = 0.15f;

    /// <summary>Remaining structural integrity.</summary>
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 Integrity;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 MaxIntegrity = 300;

    /// <summary>A wrecked frame cannot be entered or accept new pieces.</summary>
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public bool Broken;

    /// <summary>Whether a wrecked frame can still be climbed out of.</summary>
    [DataField]
    public bool AllowExitWhenBroken = true;

    /// <summary>Whether pieces may be swapped while occupied. Fallout 4 says no.</summary>
    [DataField]
    public bool AllowModificationWhileOccupied = false;

    /// <summary>Nudges the occupant's head into the collar; needs tuning per frame.</summary>
    [DataField]
    public Vector2 PilotHeadOffset = Vector2.Zero;
}

/// <summary>A helmet, chest, arm or leg piece that bolts onto a <see cref="PowerArmorFrameComponent"/>.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PowerArmorPieceComponent : Component
{
    /// <summary>Which mounting point this piece belongs in; the frame refuses it anywhere else.</summary>
    [DataField, AutoNetworkedField]
    public PowerArmorPieceSlot Slot = PowerArmorPieceSlot.Chest;

    /// <summary>RSI state drawn on the frame while installed; the RSI path comes from the piece's own sprite.</summary>
    [DataField]
    public string WornStateName = "chest";

    /// <summary>Incoming damage multiplier while installed; heavier plating absorbs more.</summary>
    [DataField]
    public float DamageReduction = 0.9f;
}

/// <summary>Placed on an entity while it occupies a <see cref="PowerArmorFrameComponent"/>.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PowerArmorPilotComponent : Component
{
    /// <summary>The frame this entity is inside of.</summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public EntityUid Frame;
}