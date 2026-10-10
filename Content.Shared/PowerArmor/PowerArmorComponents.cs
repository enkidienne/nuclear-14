using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.PowerArmor;

/// Mounting points a frame exposes.
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

    /// Draw order comes from the prototype's layer list, not these values.
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

    /// ItemSlots key per slot, as spelled in YAML.
public static class PowerArmorSlotIds
{
    /// ItemSlots keys indexed by slot.
    public static readonly string[] Ids = ["helmet", "chest", "leftArm", "rightArm", "leftLeg", "rightLeg"];

        // Enum.GetValues clones per call; this is read per verb and per damage tick.
    public static readonly PowerArmorPieceSlot[] All = Enum.GetValues<PowerArmorPieceSlot>();

    public static string IdFor(PowerArmorPieceSlot slot) => Ids[(int) slot];

    public static bool TrySlotForId(string? id, out PowerArmorPieceSlot slot)
    {
        var index = Array.IndexOf(Ids, id);
        slot = (PowerArmorPieceSlot) index;
        return index >= 0;
    }
}

    /// A chassis a player climbs inside; their input is relayed to it.
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PowerArmorFrameComponent : Component
{
    public const string DefaultPilotSlotId = "power-armor-pilot-slot";

    /// Issued gear parked between occupants.
    public const string IssuedGearContainerId = "power-armor-issued-gear";

    [ViewVariables]
    public ContainerSlot PilotSlot = default!;

    /// Issued core and harness.
    [ViewVariables]
    public Container IssuedGear = default!;

    [ViewVariables]
    public readonly string PilotSlotId = DefaultPilotSlotId;

    /// Seconds to climb in.
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float EntryDelay = 3f;

    /// Seconds to climb out.
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ExitDelay = 3f;

    /// Seconds to bolt a piece on.
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float InstallDelay = 2f;

    /// Seconds to unbolt a piece.
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float RemoveDelay = 2f;

    /// Entities that may occupy the frame.
    [DataField]
    public EntityWhitelist? PilotWhitelist;

    /// Fraction of frame damage that reaches the occupant.
    [DataField]
    public float DamageBleedThrough = 0.15f;

    /// The single durability pool; armour pieces have none of their own.
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 Integrity;

    /// Chassis integrity before any armour is fitted.
    [DataField]
    public FixedPoint2 BaseIntegrity = 100;

    /// BaseIntegrity plus every fitted piece's integrity.
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 MaxIntegrity;

    /// Sum of every fitted piece's resistance.
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 MaxArmor;

    /// Pooled resistance at which incoming damage is halved.
    public const int HalfDamageArmour = 100;

    public static float DamageMultiplier(FixedPoint2 armour)
        => HalfDamageArmour / (HalfDamageArmour + (float) armour);

    /// Condition readout colours by fraction remaining.
    [DataField]
    public PowerArmorConditionScale Condition = new();

    /// Wrecked: cannot be entered or accept pieces.
    [ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public bool Broken;

    /// Whether a wreck can still be exited.
    [DataField]
    public bool AllowExitWhenBroken = true;

    /// Fallout 4 says no.
    [DataField]
    public bool AllowModificationWhileOccupied = false;

    /// Head lift in world units; the frame scales 1.25 but the head does not.
    [DataField]
    public Vector2 PilotHeadOffset = Vector2.Zero;

    /// Mirrored layer keys; tracked so a hat removed mid-exit still gets cleaned up.
    [ViewVariables(VVAccess.ReadWrite)]
    public HashSet<string> MirroredKeys = new();

    /// <summary>Modifier sets of the fitted pieces, rebuilt by RecomputePools. Server-side only.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public List<DamageModifierSet> FittedModifiers = new();
}

[DataDefinition]
public sealed partial class PowerArmorConditionScale
{
    [DataField] public string GoodColor = "green";
    [DataField] public float GoodAt = 0.66f;
    [DataField] public string WornColor = "yellow";
    [DataField] public float WornAt = 0.33f;
    [DataField] public string BadColor = "red";
}

    /// A piece that bolts onto a frame.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class PowerArmorPieceComponent : Component
{
    /// Mounting point this piece belongs in.
    [DataField, AutoNetworkedField]
    public PowerArmorPieceSlot Slot = PowerArmorPieceSlot.Chest;

    /// RSI state drawn on the frame while fitted.
    [DataField]
    public string WornStateName = "chest";

    /// Resistance this piece contributes while fitted.
    [DataField(required: true)]
    public DamageModifierSet Modifiers = default!;

    /// Raises the host frame's ceiling; not a pool of its own.
    [DataField, AutoNetworkedField]
    public FixedPoint2 Integrity = 15;

    /// Raises the host frame's pooled resistance; independent of Integrity.
    [DataField, AutoNetworkedField]
    public FixedPoint2 Armor = 15;
}

    /// Present while the entity occupies a frame.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PowerArmorPilotComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public EntityUid Frame;
}

[Serializable, NetSerializable]
/// The held piece. DoAfterArgs.Target is the frame, so the item has to be carried here
/// or it is lost by the time the bar finishes.
public sealed partial class PowerArmorPieceInstallEvent : SimpleDoAfterEvent
{
    public PowerArmorPieceSlot Slot;
    public NetEntity Item;
}

[Serializable, NetSerializable]
public sealed partial class PowerArmorPieceRemoveEvent : SimpleDoAfterEvent
{
    public PowerArmorPieceSlot Slot;
}
