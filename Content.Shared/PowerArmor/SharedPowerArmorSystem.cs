using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Content.Shared.ActionBlocker;
using Content.Shared.Armor;
using Content.Shared.CombatMode;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.MouseRotator;
using Content.Shared.DoAfter;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Verbs;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Localization;
using Robust.Shared.Serialization;

namespace Content.Shared.PowerArmor;

/// Power armour entry, exit, and piece matching.
public abstract partial class SharedPowerArmorSystem : EntitySystem
{
    [Dependency] protected ActionBlockerSystem _actionBlocker = default!;
    [Dependency] protected EntityWhitelistSystem _whitelist = default!;
    [Dependency] protected ItemSlotsSystem _itemSlots = default!;
    [Dependency] protected SharedContainerSystem _container = default!;
    [Dependency] protected SharedInteractionSystem _interaction = default!;
    [Dependency] protected ExamineSystemShared _examine = default!;
[Dependency] protected SharedMoverController _mover = default!;
    [Dependency] protected SharedPhysicsSystem _physics = default!;
    [Dependency] protected InventorySystem _inventory = default!;
    [Dependency] protected SharedDoAfterSystem _doAfter = default!;
    [Dependency] protected SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PowerArmorFrameComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<PowerArmorFrameComponent, ItemSlotInsertAttemptEvent>(OnSlotInsertAttempt);
        SubscribeLocalEvent<PowerArmorFrameComponent, ItemSlotEjectAttemptEvent>(OnSlotEjectAttempt);
        // Shared: the examine verb list is built client-side.
        SubscribeLocalEvent<PowerArmorFrameComponent, GetVerbsEvent<ExamineVerb>>(OnExamineFrame);

        // Shared: a client/server verb mismatch flickers the icons.
        SubscribeLocalEvent<PowerArmorFrameComponent, GetVerbsEvent<AlternativeVerb>>(AddPieceVerbs,
            after: new[] { typeof(ItemSlotsSystem) });

        SubscribeLocalEvent<PowerArmorPilotComponent, CanAttackFromContainerEvent>(OnCanAttackFromContainer);
        SubscribeLocalEvent<PowerArmorPilotComponent, AccessibleOverrideEvent>(OnAccessibleOverride);
        SubscribeLocalEvent<PowerArmorPilotComponent, InRangeOverrideEvent>(OnInRangeOverride);
        SubscribeLocalEvent<PowerArmorPilotComponent, InteractUsingEvent>(OnPilotInteractUsing,
            before: new[] { typeof(SharedArmorSystem) });
        SubscribeLocalEvent<PowerArmorPilotComponent, EntGotRemovedFromContainerMessage>(OnPilotRemoved);
        SubscribeLocalEvent<PowerArmorPilotComponent, IsEquippingAttemptEvent>(OnPilotEquipAttempt);
        SubscribeLocalEvent<PowerArmorPilotComponent, IsUnequippingAttemptEvent>(OnPilotUnequipAttempt);

        // Removed with the pilot component; nothing else tore it down.
        SubscribeLocalEvent<PowerArmorPilotComponent, TryChangePartDamageEvent>(OnPilotPartDamage);
    }

    /// Clears CanSever only; cancelling would zero damage the wearer should still take.
    private void OnPilotPartDamage(EntityUid uid, PowerArmorPilotComponent component, ref TryChangePartDamageEvent args)
        => args.CanSever = false;

    /// Empty wrecks drop Opaque/BulletImpassable so rounds pass through; occupied ones keep them.
    public void ApplyWreckCollision(Entity<PowerArmorFrameComponent> ent)
    {
        if (!TryComp<FixturesComponent>(ent, out var fixtures))
            return;

        var layer = ent.Comp.Broken && ent.Comp.PilotSlot?.ContainedEntity == null
            ? (int) CollisionGroup.MidImpassable
            : (int) CollisionGroup.MobLayer;

        // Copy: SetCollisionLayer re-derives the body layers.
        foreach (var (id, fixture) in fixtures.Fixtures.ToArray())
        {
            _physics.SetCollisionLayer(ent, id, fixture, layer, fixtures);
        }
    }

    protected virtual void OnStartup(EntityUid uid, PowerArmorFrameComponent component, ComponentStartup args)
    {
        component.PilotSlot = _container.EnsureContainer<ContainerSlot>(uid, component.PilotSlotId);
        component.IssuedGear = _container.EnsureContainer<Container>(uid, PowerArmorFrameComponent.IssuedGearContainerId);

        // The Damageable starts empty, so the pool would read 0.
        if (component.Integrity <= 0)
        {
            component.MaxIntegrity = component.BaseIntegrity;
            component.Integrity = component.MaxIntegrity;
            Dirty(uid, component);
        }
    }

    public static PowerArmorVisualLayers LayerFor(PowerArmorPieceSlot slot) => (PowerArmorVisualLayers) (int) slot;

    public bool IsEmpty(EntityUid uid, PowerArmorFrameComponent? component = null)
    {
        return Resolve(uid, ref component) && component.PilotSlot.ContainedEntity == null;
    }

    public bool CanInsert(EntityUid uid, EntityUid toInsert, PowerArmorFrameComponent? component = null)
    {
        if (!Resolve(uid, ref component) || component.Broken)
            return false;

        if (component.PilotSlot.ContainedEntity != null)
            return false;

        // The frame issues its own outer layer and core.
        if (IsTooBulky(toInsert))
            return false;

        if (_whitelist.IsWhitelistFail(component.PilotWhitelist, toInsert))
            return false;

        return _actionBlocker.CanMove(toInsert);
    }

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
        return true;
    }

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
        return true;
    }

    public bool TryInstallPiece(EntityUid frame, EntityUid piece, EntityUid? user = null)
    {
        if (!TryComp<PowerArmorPieceComponent>(piece, out var pieceComp))
            return false;

        return _itemSlots.TryInsert(frame, PowerArmorSlotIds.IdFor(pieceComp.Slot), piece, user);
    }

    /// Ejects to <paramref name="user"/>'s hands if given, otherwise to the floor.
    public bool TryRemovePiece(EntityUid frame, PowerArmorPieceSlot slot, EntityUid? user = null, ItemSlotsComponent? slots = null)
    {
        if (GetPiece(frame, slot, slots) is not { } piece)
            return false;

        if (!_itemSlots.TryGetSlot(frame, PowerArmorSlotIds.IdFor(slot), out var itemSlot, slots))
            return false;

        return _itemSlots.TryEjectToHands(frame, itemSlot, user, excludeUserAudio: true);
    }

    public EntityUid? GetPiece(EntityUid frame, PowerArmorPieceSlot slot, ItemSlotsComponent? slots = null)
    {
        return _itemSlots.GetItemOrNull(frame, PowerArmorSlotIds.IdFor(slot), slots);
    }

    public bool TryGetHostFrame(EntityUid piece, out EntityUid frame)
    {
        frame = default;

        if (_container.TryGetContainingContainer(piece, out var container)
            && container.Owner is { } owner
            && HasComp<PowerArmorFrameComponent>(owner))
        {
            frame = owner;
            return true;
        }

        return false;
    }

    private void SetupPilot(EntityUid frame, EntityUid pilot)
    {
        var pilotComp = EnsureComp<PowerArmorPilotComponent>(pilot);
        pilotComp.Frame = frame;
        Dirty(pilot, pilotComp);

        // No InteractionRelay: it re-issued every interaction as the chassis, blocking their hands.
        _mover.SetRelay(pilot, frame);

        // Combat mode may already have been on, putting the rotator on their body.
        if (TryComp<CombatModeComponent>(pilot, out var combat) && combat.IsInCombatMode)
            GiveRotatorToFrame(frame);
    }

    private void GiveRotatorToFrame(EntityUid frame)
    {
        EnsureComp<MouseRotatorComponent>(frame);
        EnsureComp<NoRotateOnMoveComponent>(frame);
    }

    private void RemovePilot(EntityUid pilot)
    {
        // Otherwise the empty frame keeps tracking the cursor.
        if (TryComp<PowerArmorPilotComponent>(pilot, out var pilotComp))
        {
            RemComp<MouseRotatorComponent>(pilotComp.Frame);
            RemComp<NoRotateOnMoveComponent>(pilotComp.Frame);
        }

        RemComp<PowerArmorPilotComponent>(pilot);
        RemComp<RelayInputMoverComponent>(pilot);
    }

    /// Helmet exempt: the occupant may open their own visor.
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

        if (expected == PowerArmorPieceSlot.Helmet)
            return;

        if (frame.PilotSlot.ContainedEntity != null && !frame.AllowModificationWhileOccupied)
            args.Cancelled = true;
    }

    /// Helmet exempt, matching OnSlotInsertAttempt.
    private void OnSlotEjectAttempt(EntityUid uid, PowerArmorFrameComponent frame, ref ItemSlotEjectAttemptEvent args)
    {
        if (PowerArmorSlotIds.TrySlotForId(args.Slot.ID, out var slot) && slot == PowerArmorPieceSlot.Helmet)
            return;

        if (frame.PilotSlot.ContainedEntity != null && !frame.AllowModificationWhileOccupied)
            args.Cancelled = true;
    }

    private void OnCanAttackFromContainer(EntityUid uid, PowerArmorPilotComponent component, CanAttackFromContainerEvent args)
    {
        args.CanAttack = true;
    }

    private void OnAccessibleOverride(EntityUid uid, PowerArmorPilotComponent component, ref AccessibleOverrideEvent args)
    {
        if (args.User != uid)
            return;

        // Only world targets borrow the chassis' reach.
        if (args.Target != uid && _container.IsInSameOrParentContainer(uid, args.Target, out _, out _))
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

    /// InteractUsingEvent is not container-relayed and the occupant is what players click.
    private void OnPilotInteractUsing(EntityUid uid, PowerArmorPilotComponent component, ref InteractUsingEvent args)
    {
        if (args.Handled || !Exists(component.Frame))
            return;

       RaiseLocalEvent(component.Frame, args);
    }

    /// Left by some route other than eject.
    private void OnPilotRemoved(EntityUid uid, PowerArmorPilotComponent component, EntGotRemovedFromContainerMessage args)
    {
        // RemComp fires ComponentShutdown, which is where the client puts the sprite back.
        RemovePilot(uid);
    }

    /// Back slot is reserved for the core.
    private void OnPilotEquipAttempt(EntityUid uid, PowerArmorPilotComponent component, ref IsEquippingAttemptEvent args)
    {
        if (args.Equipee != uid || args.EquipTarget != uid)
            return;

        if (IsFrameSlot(args.SlotFlags))
            args.Cancel();
    }

    private void OnPilotUnequipAttempt(EntityUid uid, PowerArmorPilotComponent component, ref IsUnequippingAttemptEvent args)
    {
        if (args.Unequipee != uid || args.UnEquipTarget != uid)
            return;

        if (IsFrameSlot(args.SlotFlags))
            args.Cancel();
    }

    private static bool IsFrameSlot(SlotFlags flags)
        => (flags & FrameSlots) != 0;

    /// Backpack or outer clothing.
    public bool IsTooBulky(EntityUid uid, InventoryComponent? inventory = null)
    {
        if (!Resolve(uid, ref inventory, false))
            return false;

        // By flag rather than by name, so a renamed slot cannot silently stop counting.
        var enumerator = _inventory.GetSlotEnumerator((uid, inventory), FrameSlots);
        while (enumerator.NextItem(out var item) && item != default)
            return true;

        return false;
    }

    private const SlotFlags FrameSlots = SlotFlags.OUTERCLOTHING | SlotFlags.BACK;

    /// The pool only; the piece stats live on the pieces.
    private void OnExamineFrame(EntityUid uid, PowerArmorFrameComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        var msg = DurabilityMessage(component.Integrity, component.MaxIntegrity, component.Condition);

        if (component.Broken)
        {
            msg.PushNewline();
            msg.AddMarkupOrThrow(Loc.GetString("power-armor-durability-broken"));
        }

        AddDurabilityVerb(args, component, msg);
        AddFittedPartsVerb(uid, component, args);
    }

    private void AddFittedPartsVerb(EntityUid uid, PowerArmorFrameComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        var msg = new FormattedMessage();
        var fitted = new List<(EntityUid Piece, FixedPoint2 Integrity, FixedPoint2 Armor)>();

        foreach (var slot in PowerArmorSlotIds.All)
        {
            if (GetPiece(uid, slot) is { } piece
                && TryComp<PowerArmorPieceComponent>(piece, out var pieceComp))
            {
                fitted.Add((piece, pieceComp.Integrity, pieceComp.Armor));
            }
        }

        if (fitted.Count == 0)
        {
            msg.AddMarkupOrThrow(Loc.GetString("power-armor-fitted-none"));
        }
        else
        {
            var armour = FixedPoint2.Zero;
            var integrity = FixedPoint2.Zero;

            foreach (var (_, pieceIntegrity, pieceArmor) in fitted)
            {
                integrity += pieceIntegrity;
                armour += pieceArmor;
            }

        // What the pool buys, split by each piece's share.
            var reduction = 1f - PowerArmorFrameComponent.DamageMultiplier(armour);
            var first = true;

            foreach (var (piece, pieceIntegrity, pieceArmor) in fitted)
            {
                if (!first)
                    msg.PushNewline();

                first = false;

                msg.AddMarkupOrThrow(Loc.GetString("power-armor-fitted-part",
                    ("name", Identity.Name(piece, EntityManager)),
                    ("integrity", (int) pieceIntegrity),
                    ("armor", Percent(reduction * (float) pieceArmor / (float) armour))));
            }

            msg.PushNewline();
            msg.AddMarkupOrThrow(Loc.GetString("power-armor-fitted-total",
                ("integrity", (int) integrity),
                ("armor", Percent(reduction))));
        }

        _examine.AddDetailedExamineVerb(args, component, msg,
            Loc.GetString("power-armor-fitted-verb-text"),
            "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("power-armor-fitted-verb-message"));
    }

    private static string Percent(float fraction) => (fraction * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string SlotLocId(PowerArmorPieceSlot slot) => slot switch
    {
        PowerArmorPieceSlot.Helmet => "power-armor-slot-helmet",
        PowerArmorPieceSlot.Chest => "power-armor-slot-chest",
        PowerArmorPieceSlot.LeftArm => "power-armor-slot-left-arm",
        PowerArmorPieceSlot.RightArm => "power-armor-slot-right-arm",
        PowerArmorPieceSlot.LeftLeg => "power-armor-slot-left-leg",
        PowerArmorPieceSlot.RightLeg => "power-armor-slot-right-leg",
        _ => "power-armor-slot-helmet",
    };

    private void AddPieceVerbs(EntityUid uid, PowerArmorFrameComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.Hands == null || !args.CanInteract || !args.CanAccess)
            return;

        // Drop ItemSlots' instant verbs before adding the slow ones.
        args.Verbs.RemoveWhere(v => (v.Category == VerbCategory.Insert || v.Category == VerbCategory.Eject)
            && IsSlotVerbText(v.Text));

        if (IsEmpty(uid, component))
        {
        // Always offered, so a blocked pilot still gets told why on click.
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "power-armor-frame-verb-enter",
                Act = () =>
                {
                    if (component.Broken)
                    {
                        _popup.PopupClient(Loc.GetString("power-armor-frame-broken"), args.User);
                        return;
                    }

                    if (IsTooBulky(args.User))
                    {
                        _popup.PopupClient(Loc.GetString("power-armor-frame-too-bulky"), args.User);
                        return;
                    }

                    if (_whitelist.IsWhitelistFail(component.PilotWhitelist, args.User))
                    {
                        _popup.PopupClient(Loc.GetString("power-armor-frame-cannot-pilot"), args.User);
                        return;
                    }

                    _doAfter.TryStartDoAfter(new DoAfterArgs(
                        EntityManager, args.User, component.EntryDelay, new PowerArmorEntryEvent(), uid, target: uid)
                    {
                        BreakOnMove = true,
                    });
                },
            });
        }
        // Only the occupant climbs out; nobody else gets to unseat them.
        else if (component.PilotSlot?.ContainedEntity == args.User)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "power-armor-frame-verb-exit",
                Priority = 1,
                Act = () => _doAfter.TryStartDoAfter(new DoAfterArgs(
                    EntityManager, args.User, component.ExitDelay, new PowerArmorExitEvent(), uid, target: uid)
                {
                    BreakOnMove = true,
                }),
            });
        }

        // Fitting and stripping happen from outside; the loop blocks them while occupied.
        foreach (var slot in PowerArmorSlotIds.All)
        {
            if (component.PilotSlot?.ContainedEntity != null
                && !component.AllowModificationWhileOccupied
                && slot != PowerArmorPieceSlot.Helmet)
            {
                continue;
            }

            var text = Loc.GetString(SlotLocId(slot));

            if (args.Using is { } usingUid)
            {
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = text,
                    Category = VerbCategory.Insert,
                    IconEntity = GetNetEntity(usingUid),
                    Act = () => _doAfter.TryStartDoAfter(new DoAfterArgs(
                        EntityManager, args.User, component.InstallDelay,
                        new PowerArmorPieceInstallEvent
                        {
                            Slot = slot,
                            Item = GetNetEntity(usingUid),
                        }, uid, target: uid)
                    {
                        BreakOnMove = true,
                    }),
                });
            }

            if (GetPiece(uid, slot) is { } piece)
            {
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = text,
                    Category = VerbCategory.Eject,
                    IconEntity = GetNetEntity(piece),
                    Act = () => _doAfter.TryStartDoAfter(new DoAfterArgs(
                        EntityManager, args.User, component.RemoveDelay,
                        new PowerArmorPieceRemoveEvent { Slot = slot }, uid, target: uid)
                    {
                        BreakOnMove = true,
                    }),
                });
            }
        }
    }


    private static HashSet<string>? _slotVerbTexts;

    private bool IsSlotVerbText(string text)
    {
        // Built once; was six Loc lookups per verb.
        _slotVerbTexts ??= BuildSlotVerbTexts();
        return _slotVerbTexts.Contains(text);
    }

    private HashSet<string> BuildSlotVerbTexts()
    {
        var texts = new HashSet<string>();
        foreach (var slot in PowerArmorSlotIds.All)
            texts.Add(Loc.GetString(SlotLocId(slot)));

        return texts;
    }

    private FormattedMessage DurabilityMessage(
        FixedPoint2 integrity,
        FixedPoint2 max,
        PowerArmorConditionScale condition)
    {
        var fraction = max <= 0 ? 0f : (float) integrity / (float) max;
        var color = fraction >= condition.GoodAt
            ? condition.GoodColor
            : fraction >= condition.WornAt
                ? condition.WornColor
                : condition.BadColor;

        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(Loc.GetString("power-armor-durability-examine",
            ("current", (int) integrity),
            ("max", (int) max),
            ("color", color)));
        return msg;
    }

    private void AddDurabilityVerb<T>(GetVerbsEvent<ExamineVerb> args, T component, FormattedMessage msg)
        where T : Component
    {
        _examine.AddDetailedExamineVerb(args, component, msg,
            Loc.GetString("power-armor-durability-verb-text"),
            "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("power-armor-durability-verb-message"));
    }
}



[Serializable, NetSerializable]
public sealed partial class PowerArmorEntryEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class PowerArmorExitEvent : SimpleDoAfterEvent;
