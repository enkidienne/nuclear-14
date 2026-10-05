using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.DragDrop;
using Content.Shared.FixedPoint;
using Content.Shared.Popups;
using Content.Shared.Movement.Events;
using Content.Shared.PowerArmor;
using Content.Shared.Verbs;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Localization;

namespace Content.Server.PowerArmor;

/// <summary>Enter/exit verbs, drag-drop entry, integrity and damage bleed-through.</summary>
public sealed class PowerArmorSystem : SharedPowerArmorSystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PowerArmorFrameComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorEntryEvent>(OnEntryFinished);
        SubscribeLocalEvent<PowerArmorFrameComponent, PowerArmorExitEvent>(OnExitFinished);
        SubscribeLocalEvent<PowerArmorFrameComponent, DragDropTargetEvent>(OnDragDrop);
        SubscribeLocalEvent<PowerArmorFrameComponent, CanDropTargetEvent>(OnCanDragDrop);
        SubscribeLocalEvent<PowerArmorFrameComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<PowerArmorFrameComponent, DestructionEventArgs>(OnDestruction);
        SubscribeLocalEvent<PowerArmorFrameComponent, UpdateCanMoveEvent>(OnCanMove);
    }

    private void OnGetVerbs(EntityUid uid, PowerArmorFrameComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess)
            return;

        if (IsEmpty(uid, component))
        {
            if (!CanInsert(uid, args.User, component))
            {
                if (component.Broken)
                    _popup.PopupEntity(Loc.GetString("power-armor-frame-broken"), args.User);
                return;
            }

            args.Verbs.Add(new AlternativeVerb
            {
                Text = "power-armor-frame-verb-enter",
                Act = () =>
                {
                    var doAfter = new DoAfterArgs(EntityManager, args.User, component.EntryDelay, new PowerArmorEntryEvent(), uid, target: uid)
                    {
                        BreakOnMove = true,
                    };
                    _doAfter.TryStartDoAfter(doAfter);
                },
            });
            return;
        }

        var self = args.User == uid || args.User == component.PilotSlot.ContainedEntity;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "power-armor-frame-verb-exit",
            Priority = 1,
            Act = () =>
            {
                // Climbing out of your own suit is instant; dragging somebody else out is not.
                if (self)
                {
                    TryEject(uid, component);
                    return;
                }

                var doAfter = new DoAfterArgs(EntityManager, args.User, component.ExitDelay, new PowerArmorExitEvent(), uid, target: uid);
                _doAfter.TryStartDoAfter(doAfter);
            },
        });
    }

    private void OnEntryFinished(EntityUid uid, PowerArmorFrameComponent component, PowerArmorEntryEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (_whitelist.IsWhitelistFail(component.PilotWhitelist, args.Args.User))
        {
            _popup.PopupEntity(Loc.GetString("power-armor-frame-cannot-pilot"), args.Args.User);
            return;
        }

        TryInsert(uid, args.Args.User, component);
        args.Handled = true;
    }

    private void OnExitFinished(EntityUid uid, PowerArmorFrameComponent component, PowerArmorExitEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        TryEject(uid, component);
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

    private void OnDamageChanged(EntityUid uid, PowerArmorFrameComponent component, DamageChangedEvent args)
    {
        var integrity = component.MaxIntegrity - args.Damageable.TotalDamage;
        SetIntegrity(uid, integrity, component);

        // Structural hits only partially carry through to the occupant.
        if (!args.DamageIncreased || args.DamageDelta == null || component.DamageBleedThrough <= 0)
            return;

        if (component.PilotSlot.ContainedEntity is not { } pilot)
            return;

        var through = args.DamageDelta * InstalledReduction(uid) * component.DamageBleedThrough;
        _damageable.TryChangeDamage(pilot, through);
    }

    /// <summary>Damage multiplier contributed by the currently installed armour.</summary>
    private float InstalledReduction(EntityUid uid)
    {
        var reduction = 1f;

        foreach (var slot in PowerArmorSlotIds.All)
        {
            if (GetPiece(uid, slot) is { } piece
                && TryComp<PowerArmorPieceComponent>(piece, out var pieceComp))
            {
                reduction *= pieceComp.DamageReduction;
            }
        }

        return reduction;
    }

    private void SetIntegrity(EntityUid uid, FixedPoint2 integrity, PowerArmorFrameComponent component)
    {
        if (integrity < FixedPoint2.Zero)
            integrity = FixedPoint2.Zero;

        component.Integrity = integrity;
        Dirty(uid, component);

        if (component.Integrity > FixedPoint2.Zero || component.Broken)
            return;

        Break(uid, component);
    }

    private void Break(EntityUid uid, PowerArmorFrameComponent component)
    {
        component.Broken = true;
        component.Integrity = FixedPoint2.Zero;
        Dirty(uid, component);

        // Whoever is inside gets thrown clear rather than being trapped.
        if (component.PilotSlot.ContainedEntity is { } pilot && TryEject(uid, component, pilot))
            _popup.PopupEntity(Loc.GetString("power-armor-frame-broken-eject"), pilot);
    }

    private void OnDestruction(EntityUid uid, PowerArmorFrameComponent component, DestructionEventArgs args)
    {
        Break(uid, component);
    }

    /// <summary>A wrecked frame has no power and refuses to budge.</summary>
    private void OnCanMove(EntityUid uid, PowerArmorFrameComponent component, UpdateCanMoveEvent args)
    {
        if (component.Broken)
            args.Cancel();
    }
}