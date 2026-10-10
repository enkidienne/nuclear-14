using System.Collections.Generic;
using Content.Server.Repairable;
using Content.Server._Misfits.Special;
using Content.Server._Misfits.Weapons;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.DragDrop;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Movement.Events;
using Content.Shared.PowerArmor;
using Content.Shared.Projectiles;
using Content.Shared.Verbs;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Localization;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.PowerArmor;

    /// Entry, drag-drop, integrity and damage bleed-through.
public sealed partial class PowerArmorSystem : SharedPowerArmorSystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private INetManager _net = default!;

    /// Gear handed to the occupant, and their slots.
    public const string CoreProto = "PowerArmorFrameCore";
    public const string ArmorProto = "PowerArmorFrameArmor";
    private const string CoreSlot = "back";
    private const string ArmorSlot = "outerClothing";

    public override void Initialize()
    {
        base.Initialize();

        // Verbs live in the shared system; a divergent list flickers the icons.
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorEntryEvent>(OnEntryFinished);
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorExitEvent>(OnExitFinished);
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorPieceInstallEvent>(OnPieceInstalled);
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorPieceRemoveEvent>(OnPieceRemoved);
        SubscribeLocalEvent<PowerArmorFrameComponent, DragDropTargetEvent>(OnDragDrop);
        SubscribeLocalEvent<PowerArmorFrameComponent, CanDropTargetEvent>(OnCanDragDrop);
        SubscribeLocalEvent<PowerArmorFrameComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<PowerArmorPieceComponent, BeforeDamageChangedEvent>(OnPieceDamageBefore);
        SubscribeLocalEvent<PowerArmorFrameComponent, BeforeDamageChangedEvent>(OnFrameDamageBefore);
    SubscribeLocalEvent<PowerArmorFrameComponent, DamageModifyEvent>(OnFrameDamageModify);
       SubscribeLocalEvent<PowerArmorFrameComponent, EntInsertedIntoContainerMessage>(OnPieceFitted);
        SubscribeLocalEvent<PowerArmorFrameComponent, EntRemovedFromContainerMessage>(OnPieceRemoved);
        SubscribeLocalEvent<PowerArmorFrameComponent, DestructionEventArgs>(OnDestruction);
        SubscribeLocalEvent<PowerArmorFrameComponent, UpdateCanMoveEvent>(OnCanMove);
        // Not ComponentStartup: EnsureComp fires it before SetupPilot sets Frame.
        SubscribeLocalEvent<PowerArmorPilotComponent, EntGotInsertedIntoContainerMessage>(OnPilotInserted);
        SubscribeLocalEvent<PowerArmorPilotComponent, ComponentShutdown>(OnPilotShutdown);

        // Runs last so it sees the final, falloff/luck-adjusted damage.
        SubscribeLocalEvent<ProjectileHitEvent>(OnTrueDamageHit,
            after: new[] { typeof(BallisticDamageFalloffSystem), typeof(SpecialCombatSystem) });
    }

    /// Issues the core and harness, reusing the last occupant's.
    private void OnPilotInserted(EntityUid uid, PowerArmorPilotComponent component, EntGotInsertedIntoContainerMessage args)
    {
        if (!_net.IsServer || !TryComp<PowerArmorFrameComponent>(component.Frame, out var frame))
            return;

        // Return unused: the gear parks back on the frame in OnPilotShutdown.
        EnsureIssued(uid, frame, CoreProto, CoreSlot);
        EnsureIssued(uid, frame, ArmorProto, ArmorSlot);

        Dirty(uid, component);
    }

    /// Reuses the parked item if there is one.
    private EntityUid EnsureIssued(EntityUid uid, PowerArmorFrameComponent frame, string proto, string slot)
    {
        foreach (var item in frame.IssuedGear.ContainedEntities.ToArray())
        {
            if (CompOrNull<MetaDataComponent>(item)?.EntityPrototype?.ID != proto)
                continue;

            _container.Remove(item, frame.IssuedGear);

            if (_inventory.TryEquip(uid, item, slot, silent: true, force: true))
                return item;

            _container.Insert(item, frame.IssuedGear);
        }

        // force: skips CanEquip, refused by the frame's own slot lock.
        var fresh = Spawn(proto, Transform(uid).Coordinates);

        if (!_inventory.TryEquip(uid, fresh, slot, silent: true, force: true))
        {
            QueueDel(fresh);
            return default;
        }

        return fresh;
    }

    /// Parks the issued gear, contents and all.
    private void OnPilotShutdown(EntityUid uid, PowerArmorPilotComponent component, ComponentShutdown args)
    {
        if (!TryComp<PowerArmorFrameComponent>(component.Frame, out var frame))
            return;

        Stash(uid, frame, CoreSlot);
        Stash(uid, frame, ArmorSlot);
    }

    private void Stash(EntityUid uid, PowerArmorFrameComponent frame, string slot)
    {
        if (!_inventory.TryGetSlotEntity(uid, slot, out var item) || item == default)
            return;

        // force: bypasses the slot lock that otherwise keeps these on while piloting.
        _inventory.TryUnequip(uid, slot, silent: true, force: true);
        _container.Insert(item.Value, frame.IssuedGear);
    }

    /// True damage bypasses the pool; the wearer eats the full hit.
    /// Broadcast: SpecialCombatSystem owns the directed ProjectileComponent pair.
    private void OnTrueDamageHit(ref ProjectileHitEvent args)
    {
        if (args.Projectile == EntityUid.Invalid
            || !TryComp<ProjectileComponent>(args.Projectile, out var projectile)
            || !projectile.IgnoreResistances
            || !TryComp<PowerArmorFrameComponent>(args.Target, out var frame))
        {
            return;
        }

        var trueDamage = args.Damage;

        // An empty specifier bails TryChangeDamage before DamageChanged, so the pool is untouched.
        args.Damage = new DamageSpecifier();

        if (frame.PilotSlot?.ContainedEntity is not { } pilot)
            return;

        _damageable.TryChangeDamage(pilot, trueDamage, ignoreResistances: true, origin: projectile.Shooter);
    }

    private void OnEntryFinished(EntityUid uid, PowerArmorFrameComponent component, PowerArmorEntryEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (component.Broken)
        {
            _popup.PopupEntity(Loc.GetString("power-armor-frame-broken"), args.Args.User);
            return;
        }

        if (_whitelist.IsWhitelistFail(component.PilotWhitelist, args.Args.User))
        {
            _popup.PopupEntity(Loc.GetString("power-armor-frame-cannot-pilot"), args.Args.User);
            return;
        }

        if (IsTooBulky(args.Args.User))
        {
            _popup.PopupEntity(Loc.GetString("power-armor-frame-too-bulky"), args.Args.User);
            return;
        }

        TryInsert(uid, args.Args.User, component);
        args.Handled = true;
    }

    private void OnExitFinished(EntityUid uid, PowerArmorFrameComponent component, PowerArmorExitEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        // The verb hides it from non-pilots; never trust that alone.
        if (args.Args.User == component.PilotSlot?.ContainedEntity)
            TryEject(uid, component);

        args.Handled = true;
    }

    private void OnPieceInstalled(EntityUid uid, PowerArmorFrameComponent component, PowerArmorPieceInstallEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        var item = GetEntity(args.Item);
        if (args.Args.User is { } user && GetPiece(uid, args.Slot) == null
            && TryComp<PowerArmorPieceComponent>(item, out var piece)
            && piece.Slot == args.Slot)
        {
            TryInstallPiece(uid, item, user);
        }

        args.Handled = true;
    }

    private void OnPieceRemoved(EntityUid uid, PowerArmorFrameComponent component, PowerArmorPieceRemoveEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        // Ejects straight to the user's hands, or to the floor if they cannot take it.
        if (GetPiece(uid, args.Slot) != null && args.Args.User is { } user)
            TryRemovePiece(uid, args.Slot, user);

        args.Handled = true;
    }

    private void OnDragDrop(EntityUid uid, PowerArmorFrameComponent component, ref DragDropTargetEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        var doAfter = new DoAfterArgs(EntityManager, args.Dragged, component.EntryDelay, new PowerArmorEntryEvent(), uid, target: uid)
        {
            BreakOnMove = true,
        };
        _doAfter.TryStartDoAfter(doAfter);
    }

    private void OnCanDragDrop(EntityUid uid, PowerArmorFrameComponent component, ref CanDropTargetEvent args)
    {
        args.Handled = true;
        args.CanDrop |= !component.Broken && CanInsert(uid, args.Dragged, component);
    }

    /// Fitted armour has no pool of its own; the hit is billed to the frame.
    private void OnPieceDamageBefore(EntityUid uid, PowerArmorPieceComponent component, ref BeforeDamageChangedEvent args)
    {
        if (!TryGetHostFrame(uid, out var frame))
            return;

       args.Cancelled = true;
        _damageable.TryChangeDamage(frame, args.Damage);
    }

    // BeforeDamageChanged, not a bail-out in OnFrameDamageModify: that would still bill the hit.
    private void OnFrameDamageBefore(EntityUid uid, PowerArmorFrameComponent component, ref BeforeDamageChangedEvent args)
    {
        if (component.PilotSlot?.ContainedEntity is { } shooter && args.Origin == shooter)
            args.Cancelled = true;
    }

    /// Must be DamageModifyEvent: BeforeDamageChangedEvent.Damage is write-only.
    /// Blasts pass ignoreResistances and skip this entirely.
    private void OnFrameDamageModify(EntityUid uid, PowerArmorFrameComponent component, DamageModifyEvent args)
    {
        // A wreck protects nothing, and repair must heal in full.
        if (component.Broken || component.MaxArmor <= 0 || !args.Damage.AnyPositive())
            return;

        args.Damage *= PowerArmorFrameComponent.DamageMultiplier(component.MaxArmor);
    }

    private void OnDamageChanged(EntityUid uid, PowerArmorFrameComponent component, DamageChangedEvent args)
    {
        SetIntegrity(uid, component.MaxIntegrity - args.Damageable.TotalDamage, component);

               if (!args.DamageIncreased || args.DamageDelta == null || component.DamageBleedThrough <= 0)
            return;

        if (component.PilotSlot.ContainedEntity is not { } pilot)
            return;

        var through = component.Broken
            ? args.DamageDelta
            : DamageSpecifier.ApplyModifierSets(args.DamageDelta, InstalledModifiers(uid)) * component.DamageBleedThrough;

        _damageable.TryChangeDamage(pilot, through);
    }

    private IEnumerable<DamageModifierSet> InstalledModifiers(EntityUid uid)
        => uid == EntityUid.Invalid ? [] : CompOrNull<PowerArmorFrameComponent>(uid)?.FittedModifiers ?? [];

    /// Ceiling and resistance: base chassis plus whatever is bolted on.
    private void RecomputePools(EntityUid uid, PowerArmorFrameComponent component)
    {
        var max = component.BaseIntegrity;
        var armour = FixedPoint2.Zero;
        component.FittedModifiers.Clear();

        foreach (var slot in PowerArmorSlotIds.All)
        {
            if (GetPiece(uid, slot) is { } piece
                && TryComp<PowerArmorPieceComponent>(piece, out var pieceComp))
            {
                max += pieceComp.Integrity;
                armour += pieceComp.Armor;
                component.FittedModifiers.Add(pieceComp.Modifiers);
            }
        }

        if (armour != component.MaxArmor)
        {
            component.MaxArmor = armour;
            Dirty(uid, component);
        }

        if (max == component.MaxIntegrity)
            return;

        component.MaxIntegrity = max;

        // Holds Integrity == MaxIntegrity - TotalDamage so welding cannot drift.
        var totalDamage = CompOrNull<DamageableComponent>(uid)?.TotalDamage ?? FixedPoint2.Zero;
        SetIntegrity(uid, max - totalDamage, component);
    }

    private void OnPieceFitted(EntityUid uid, PowerArmorFrameComponent component, EntInsertedIntoContainerMessage args)
    {
        RecomputePools(uid, component);
        // Also fires for the pilot slot, which decides a wreck's collision layer.
        ApplyWreckCollision((uid, component));
    }

    private void OnPieceRemoved(EntityUid uid, PowerArmorFrameComponent component, EntRemovedFromContainerMessage args)
    {
        RecomputePools(uid, component);
        ApplyWreckCollision((uid, component));
    }

    /// Clamped: welding's negative damage overshoots the subtraction.
    private void SetIntegrity(EntityUid uid, FixedPoint2 integrity, PowerArmorFrameComponent component)
    {
        integrity = FixedPoint2.Clamp(integrity, 0, component.MaxIntegrity);

        if (component.Integrity == integrity)
            return;

        component.Integrity = integrity;
        Dirty(uid, component);

        // Welding a wreck back over the break threshold restores it to prime.
        if (component.Integrity > FixedPoint2.Zero && component.Broken)
        {
            component.Broken = false;
            Dirty(uid, component);
            ApplyWreckCollision((uid, component));
            _actionBlocker.UpdateCanMove(uid);
            return;
        }

        if (component.Integrity > FixedPoint2.Zero || component.Broken)
            return;

        Break(uid, component);
    }

    private void Break(EntityUid uid, PowerArmorFrameComponent component)
    {
        component.Broken = true;
        component.Integrity = FixedPoint2.Zero;
        Dirty(uid, component);

        // Empty wreck is transparent to fire; occupied still stops shots so the wearer takes them.
        ApplyWreckCollision((uid, component));

        // Re-evaluate movement, or it keeps rolling on stale state.
        _actionBlocker.UpdateCanMove(uid);

        // The occupant stays put: a dead frame still shields them, and they can climb out.
        if (component.PilotSlot.ContainedEntity is { } pilot)
            _popup.PopupEntity(Loc.GetString("power-armor-frame-broken-eject"), pilot);
    }

    private void OnDestruction(EntityUid uid, PowerArmorFrameComponent component, DestructionEventArgs args)
    {
        Break(uid, component);
    }

    private void OnCanMove(EntityUid uid, PowerArmorFrameComponent component, UpdateCanMoveEvent args)
    {
        if (component.Broken)
            args.Cancel();
    }
}
