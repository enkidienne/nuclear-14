using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Client.PowerArmor;
using Content.Server.Repairable;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Server.Tools;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Tools.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Components;
using Content.Shared.PowerArmor;
using Content.Shared.Verbs;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests.PowerArmor
{
    [TestFixture]
    [TestOf(typeof(SharedPowerArmorSystem))]
    [TestOf(typeof(PowerArmorVisualSystem))]
    public sealed class PowerArmorFrameTest
    {
        private const string PilotProto = "PowerArmorFrameTestPilot";
        private const string FrameProto = "PowerArmorFrame";
        private const string JunkProto = "PowerArmorFrameTestJunk";
        private const string HelmetProto = "PowerArmorPieceHelmetT51";
        private const string ChestProto = "PowerArmorPieceChestT51";
        private const string LeftArmProto = "PowerArmorPieceLeftArmT51";
        private const string RightArmProto = "PowerArmorPieceRightArmT51";
        private const string LeftLegProto = "PowerArmorPieceLeftLegT51";
        private const string RightLegProto = "PowerArmorPieceRightLegT51";

        [TestPrototypes]
        private const string Prototypes = @"
# MobHumanDummy has no InputMover, so the frame would refuse to let it climb in.
- type: entity
  parent: BaseMobHuman
  id: PowerArmorFrameTestPilot

- type: entity
  parent: BaseItem
  id: PowerArmorFrameTestJunk
";

        private static readonly (string Proto, PowerArmorPieceSlot Slot)[] FullSet =
        [
            (HelmetProto, PowerArmorPieceSlot.Helmet),
            (ChestProto, PowerArmorPieceSlot.Chest),
            (LeftArmProto, PowerArmorPieceSlot.LeftArm),
            (RightArmProto, PowerArmorPieceSlot.RightArm),
            (LeftLegProto, PowerArmorPieceSlot.LeftLeg),
            (RightLegProto, PowerArmorPieceSlot.RightLeg),
        ];

        /// <summary>Every armour set, so the universal frame is checked against all of them.</summary>
        private static readonly string[] SetPrefixes = ["T51", "T45", "APA"];

        [Test]
        public async Task PilotEntersAndLeavesTheFrame()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid pilot = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                pilot = sEntManager.SpawnEntity(PilotProto, testMap.GridCoords);
            });

            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(sEntManager.HasComponent<PowerArmorFrameComponent>(frame));
                    Assert.That(powerArmor.IsEmpty(frame), Is.True);
                });
            });

            // Climb in.
            await server.WaitPost(() => Assert.That(powerArmor.TryInsert(frame, pilot), Is.True));
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var frameComp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                var pilotComp = sEntManager.GetComponent<PowerArmorPilotComponent>(pilot);

                Assert.Multiple(() =>
                {
                    Assert.That(frameComp.PilotSlot.ContainedEntity, Is.EqualTo(pilot));
                    Assert.That(pilotComp.Frame, Is.EqualTo(frame));

                    // Input must be relayed to the frame or the suit cannot be driven.
                    Assert.That(sEntManager.HasComponent<RelayInputMoverComponent>(pilot));

                    // A second pilot must not take the same frame.
                    Assert.That(powerArmor.TryInsert(frame, pilot), Is.False);
                });
            });

            // Climb out.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var frameComp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);

                Assert.Multiple(() =>
                {
                    Assert.That(frameComp.PilotSlot.ContainedEntity, Is.Null);
                    Assert.That(sEntManager.HasComponent<PowerArmorPilotComponent>(pilot), Is.False);
                    Assert.That(sEntManager.HasComponent<RelayInputMoverComponent>(pilot), Is.False);
                    Assert.That(powerArmor.IsEmpty(frame), Is.True);
                });
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task PiecesOnlyFitTheirOwnMountingPoint()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();
            var slots = sEntManager.EntitySysManager.GetEntitySystem<ItemSlotsSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;

            await server.WaitPost(() => frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords));
            await server.WaitRunTicks(5);

            // A helmet fits the helmet mounting point.
            await server.WaitPost(() =>
            {
                var helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
                Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.True);
            });

            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                Assert.That(powerArmor.GetPiece(frame, PowerArmorPieceSlot.Helmet), Is.Not.Null);
                Assert.That(powerArmor.GetPiece(frame, PowerArmorPieceSlot.Chest), Is.Null);
            });

            await server.WaitPost(() =>
            {
                var chest = sEntManager.SpawnEntity(ChestProto, testMap.GridCoords);
                var helmetSlot = sEntManager.GetComponent<ItemSlotsComponent>(frame).Slots[PowerArmorSlotIds.IdFor(PowerArmorPieceSlot.Helmet)];

                Assert.Multiple(() =>
                {
                    Assert.That(helmetSlot.Item, Is.Not.Null);
                    Assert.That(slots.CanInsert(frame, chest, null, helmetSlot), Is.False,
                        "a chest piece must not fit the helmet mounting point");
                });
            });

            // Non-power-armour items are refused by the whitelist.
            await server.WaitPost(() =>
            {
                var junk = sEntManager.SpawnEntity(JunkProto, testMap.GridCoords);
                var chestSlot = sEntManager.GetComponent<ItemSlotsComponent>(frame).Slots[PowerArmorSlotIds.IdFor(PowerArmorPieceSlot.Chest)];

                Assert.That(slots.CanInsert(frame, junk, null, chestSlot), Is.False);
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task PiecesCannotBeSwappedWhileOccupied()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();
            var slots = sEntManager.EntitySysManager.GetEntitySystem<ItemSlotsSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                var pilot = sEntManager.SpawnEntity(PilotProto, testMap.GridCoords);
                Assert.That(powerArmor.TryInsert(frame, pilot), Is.True);
            });

            await server.WaitRunTicks(5);

            await server.WaitPost(() =>
            {
                var helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
                var helmetSlot = sEntManager.GetComponent<ItemSlotsComponent>(frame).Slots[PowerArmorSlotIds.IdFor(PowerArmorPieceSlot.Helmet)];

                Assert.That(slots.CanInsert(frame, helmet, null, helmetSlot), Is.False,
                    "armour must not be installable while somebody is inside the frame");

                Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.False);
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task InstalledPiecesDriveTheFrameSprite()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var cEntManager = client.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;

            await server.WaitPost(() => frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords));
            await pair.RunTicksSync(5);

            var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));

            // A bare frame shows only its own chassis.
            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientFrame);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    // A bare frame shows only its own chassis.
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Frame, true, "frame");

                    foreach (var slot in PowerArmorSlotIds.All)
                        AssertLayerVisible(sprites, clientFrame, sprite, SharedPowerArmorSystem.LayerFor(slot), visible: false);
                });
            });

            // Bolt on the full set; every mounting point should light up.
            await server.WaitPost(() =>
            {
                foreach (var (proto, _) in FullSet)
                {
                    var piece = sEntManager.SpawnEntity(proto, testMap.GridCoords);
                    Assert.That(powerArmor.TryInstallPiece(frame, piece), Is.True, $"installing {proto}");
                }
            });

            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientFrame);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Helmet, true, "helmet");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Chest, true, "chest");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.LeftArm, true, "lefthand");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.RightArm, true, "righthand");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.LeftLeg, true, "leftleg");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.RightLeg, true, "rightleg");
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Frame, true, "frame");
                });
            });

            // Pull the helmet back off; only that layer should go away.
            await server.WaitPost(() =>
            {
                var helmet = powerArmor.GetPiece(frame, PowerArmorPieceSlot.Helmet);
                Assert.That(helmet, Is.Not.Null);
                Assert.That(sEntManager.EntitySysManager.GetEntitySystem<ItemSlotsSystem>()
                    .TryEject(frame, PowerArmorSlotIds.IdFor(PowerArmorPieceSlot.Helmet), null, out _), Is.True);
            });

            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientFrame);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Helmet, false);
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Chest, true);
                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Frame, true);
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The frame is one chassis for every armour set, so each set must bolt on and render
        /// through it. Also proves the layers swap RSI: all three sets are separate RSIs.
        /// </summary>
        [Test]
        public async Task EverySetFitsTheUniversalFrameAndDrivesItsOwnSprite()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var cEntManager = client.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            foreach (var set in SetPrefixes)
            {
                EntityUid frame = default;

                await server.WaitPost(() => frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords));
                await pair.RunTicksSync(5);

                await server.WaitPost(() =>
                {
                    foreach (var (suffix, slot) in FullSet)
                    {
                        var piece = sEntManager.SpawnEntity($"PowerArmorPiece{suffix}{set}", testMap.GridCoords);
                        Assert.That(powerArmor.TryInstallPiece(frame, piece), Is.True, $"{set} {suffix}");
                    }
                });

                await pair.RunTicksSync(10);

                var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));
                var seenRsis = new HashSet<string>();

                await client.WaitAssertion(() =>
                {
                    var sprite = cEntManager.GetComponent<SpriteComponent>(clientFrame);
                    var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                    foreach (var (_, slot) in FullSet)
                    {
                        var layer = SharedPowerArmorSystem.LayerFor(slot);
                        AssertLayerVisible(sprites, clientFrame, sprite, layer, true, $"{set} {slot}");
                        seenRsis.Add(sprites.LayerGetRSI((clientFrame, sprite), layer)?.Path.ToString() ?? "none");
                    }

                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Frame, true, $"{set} frame");
                });

                Assert.That(seenRsis, Has.Count.EqualTo(1), $"{set}: every piece shares one RSI");
                Assert.That(seenRsis.First(), Does.Contain($"PowerArmorPieces{set}"),
                    $"{set}: the frame must draw from that set's RSI");

                await server.WaitPost(() => sEntManager.DeleteEntity(frame));
                await pair.RunTicksSync(5);
            }

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task OccupantShowsOnlyTheirHeadAndOnlyWhenUnhelmeted()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var cEntManager = client.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid pilot = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                pilot = sEntManager.SpawnEntity("MobHuman", testMap.GridCoords);
            });

            await pair.RunTicksSync(10);

            var clientPilot = cEntManager.GetEntity(sEntManager.GetNetEntity(pilot));
            var clientFrameScale = cEntManager.GetComponent<SpriteComponent>(
                cEntManager.GetEntity(sEntManager.GetNetEntity(frame))).Scale;

            // Baseline read outside the frame, to check the restore against.
            var spriteSys = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();
            var baseSprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
            var baseHeadOffset = spriteSys.LayerMapTryGet((clientPilot, baseSprite), HumanoidVisualLayers.Head, out var bhi, false)
                ? ((SpriteComponent.Layer) baseSprite[bhi]).Offset
                : Vector2.Zero;
            var baseHeadScale = HeadScale(spriteSys, clientPilot, baseSprite);
            var baseHeadAnchor = HeadCentre(spriteSys, clientPilot, baseSprite);

            await server.WaitPost(() => Assert.That(powerArmor.TryInsert(frame, pilot), Is.True));
            await pair.RunTicksSync(10);

            // Appearance replication rebuilds every humanoid layer.
            await server.WaitPost(() =>
            {
                var appearance = sEntManager.GetComponent<HumanoidAppearanceComponent>(pilot);
                appearance.Width = 0.95f;
                sEntManager.Dirty(pilot, appearance);
            });
            await pair.RunTicksSync(10);

            // Unhelmeted: head layers up, body layers down.
            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    // The container must show contents or the head is occluded.
                    Assert.That(sprite.ContainerOccluded, Is.False, "pilot sprite must not be occluded");

                    // Render order hides the head: the frame paints over the pilot unless it is lower.
                    var pilotDepth = cEntManager.GetComponent<SpriteComponent>(clientPilot).DrawDepth;
                    var frameDepth = cEntManager.GetComponent<SpriteComponent>(
                        cEntManager.GetEntity(sEntManager.GetNetEntity(frame))).DrawDepth;
                    Assert.That(frameDepth, Is.LessThan(pilotDepth),
                        $"frame depth {frameDepth} must be below the pilot's {pilotDepth} or the chassis covers the head");

                                        var headOffset = cEntManager.GetComponent<PowerArmorFrameComponent>(
                    cEntManager.GetEntity(sEntManager.GetNetEntity(frame))).PilotHeadOffset;

                    // Rendered bounding boxes, not the layer offset.
                    var headCentre = HeadCentre(sprites, clientPilot, sprite);
                    Assert.That(float.IsFinite(headCentre.X) && float.IsFinite(headCentre.Y), Is.True,
                        $"head position must stay finite (was {headCentre})");
                    Assert.That(headCentre.Y, Is.EqualTo(baseHeadAnchor.Y + headOffset.Y).Within(0.01f),
                        $"head should sit at its anchor plus the offset (anchor {baseHeadAnchor.Y}, now {headCentre.Y})");
                    Assert.That(headCentre.X, Is.EqualTo(baseHeadAnchor.X + headOffset.X).Within(0.01f),
                        "head should not drift sideways");

                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.True, "head should show");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Face), Is.True, "face should show");

                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.False, "torso must be hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.LArm), Is.False, "arm must be hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.RLeg), Is.False, "leg must be hidden");
                });
            });

            // A helmet hides the head; step out to fit one.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.That(HeadScale(sprites, clientPilot, sprite), Is.EqualTo(baseHeadScale),
                    "head scale restored after first eject");
                Assert.That(HeadOffset(sprites, clientPilot, sprite), Is.EqualTo(baseHeadOffset),
                    "head offset restored after first eject");
            });

            await server.WaitPost(() =>
            {
                var helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
                Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.True);
                Assert.That(powerArmor.TryInsert(frame, pilot), Is.True);
            });

            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.False, "helmeted head must be hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.False, "torso must stay hidden");
                });
            });

            // Climbing out restores the mob's normal appearance.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.True, "head restored");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.True, "torso restored");

                    // Head scale and offset both go back on exit.
                    Assert.That(HeadScale(sprites, clientPilot, sprite), Is.EqualTo(baseHeadScale),
                        $"head scale restored (was {baseHeadScale})");
                    Assert.That(HeadOffset(sprites, clientPilot, sprite), Is.EqualTo(baseHeadOffset),
                        $"head offset restored (was {baseHeadOffset})");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>Verbs are collected on the client, so a server-only handler never fires.</summary>
        [Test]
        public async Task ConditionVerbIsProducedOnTheClient()
        {
await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var cEntManager = client.ResolveDependency<IEntityManager>();
            var damageable = sEntManager.EntitySysManager.GetEntitySystem<DamageableSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid piece = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                piece = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
            });

            await server.WaitRunTicks(5);
            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
                Assert.That(damageable.TryChangeDamage(piece, new DamageSpecifier { DamageDict = { ["Blunt"] = 40 } }),
                    Is.Not.Null));

            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));
                var clientPiece = cEntManager.GetEntity(sEntManager.GetNetEntity(piece));

                // Only the frame reports condition; durability is one pool.
                var ev = new GetVerbsEvent<ExamineVerb>(
                    clientFrame, clientFrame, null, null,
                    canInteract: true, canComplexInteract: true, canAccess: true,
                    new List<VerbCategory>());

                cEntManager.EventBus.RaiseLocalEvent(clientFrame, ev, true);

                var condition = ev.Verbs.OfType<ExamineVerb>().FirstOrDefault(v => v.Text == "Condition");
                Assert.That(condition, Is.Not.Null, "frame must offer a Condition verb on the client");
                Assert.That(condition!.ShowOnExamineTooltip, Is.True,
                    "Condition verb must render as a tooltip button");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>InteractUsingEvent is not container-relayed, so the occupant must forward it to the frame.</summary>
        [Test]
        public async Task WeldingTheOccupantRepairsTheFrame()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var damageable = sEntManager.EntitySysManager.GetEntitySystem<DamageableSystem>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid pilot = default;
            EntityUid welder = default;
            EntityUid engineer = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                pilot = sEntManager.SpawnEntity("MobHuman", testMap.GridCoords);
                welder = sEntManager.SpawnEntity("Welder", testMap.GridCoords);
                // The doafter hangs off the user's DoAfterComponent, so the user must be a mob.
                engineer = sEntManager.SpawnEntity("MobHuman", testMap.GridCoords);
                Assert.That(sEntManager.System<SharedHandsSystem>().TryPickup(engineer, welder), Is.True);

                // A welder must be lit before it will weld.
                sEntManager.System<ToolSystem>()
                    .TurnOn((welder, sEntManager.GetComponent<WelderComponent>(welder)), engineer);
                sEntManager.GetComponent<ItemToggleComponent>(welder).Activated = true;

                Assert.That(powerArmor.TryInsert(frame, pilot), Is.True);
                Assert.That(damageable.TryChangeDamage(frame, new DamageSpecifier { DamageDict = { ["Blunt"] = 40 } }),
                    Is.Not.Null);
            });

            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var comp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(60)), "40 off a bare 100 chassis");
                Assert.That(sEntManager.HasComponent<RepairableComponent>(frame), Is.True, "frame is weldable");
            });

            // Control: a break here means the weld mechanism, not the forwarding.
            await server.WaitPost(() =>
            {
                var interaction = sEntManager.EntitySysManager.GetEntitySystem<SharedInteractionSystem>();
                var coords = sEntManager.GetComponent<TransformComponent>(frame).Coordinates;

                interaction.InteractUsing(engineer, welder, frame, coords, false, false);
            });

            // doAfterDelay is 8 seconds, which is 240 ticks at 30/s.
            await server.WaitRunTicks(400);

            await server.WaitAssertion(() =>
            {
                var comp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(85)), "welding the chassis directly mends 25");
            });

            // Damage it again, then aim at the occupant instead.
            await server.WaitPost(() =>
            {
                Assert.That(sEntManager.EntitySysManager.GetEntitySystem<DamageableSystem>()
                    .TryChangeDamage(frame, new DamageSpecifier { DamageDict = { ["Blunt"] = 40 } }), Is.Not.Null);
            });
            await server.WaitRunTicks(10);

            await server.WaitAssertion(() =>
            {
                var comp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(45)), "back down to 45");
            });

            // Aim at the occupant, which is what a player would click.
            await server.WaitPost(() =>
            {
                var interaction = sEntManager.EntitySysManager.GetEntitySystem<SharedInteractionSystem>();
                var coords = sEntManager.GetComponent<TransformComponent>(pilot).Coordinates;

                interaction.InteractUsing(engineer, welder, pilot, coords, false, false);
            });

            await server.WaitRunTicks(400);

            await server.WaitAssertion(() =>
            {
                var comp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(70)),
                    "welding the occupant repairs the frame by 25");
            });

            // Let the doafter unwind before teardown, or the pair is dirty-disposed.
            await server.WaitRunTicks(30);

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task FrameIsTheOnlyDurabilityPool()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var damageable = sEntManager.EntitySysManager.GetEntitySystem<DamageableSystem>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid helmet = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
                Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.True);
            });

            await server.WaitRunTicks(5);

            static DamageSpecifier Blunt(int amount) => new() { DamageDict = { ["Blunt"] = amount } };

            // A fresh frame must not read as wrecked.
            await server.WaitAssertion(() =>
            {
                var comp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                Assert.Multiple(() =>
                {
                    Assert.That(comp.MaxIntegrity, Is.EqualTo(FixedPoint2.New(115)), "base 100 plus helmet");
                    Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(115)), "undamaged tracks the ceiling");
                    Assert.That(comp.Broken, Is.False);
                });
            });

            // Returns null by design: piece damage is cancelled and redirected.
            await server.WaitPost(() => damageable.TryChangeDamage(helmet, Blunt(40)));
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var frameComp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);
                var helmetDamage = sEntManager.GetComponent<DamageableComponent>(helmet).TotalDamage;

                Assert.Multiple(() =>
                {
                    Assert.That(frameComp.Integrity, Is.EqualTo(FixedPoint2.New(75)), "40 billed to the pool");
                    Assert.That(helmetDamage, Is.EqualTo(FixedPoint2.New(0)), "piece keeps no durability of its own");
                    Assert.That(sEntManager.HasComponent<RepairableComponent>(frame), Is.True, "frame is weldable");
                });
            });

            // Welding the frame mends the pool.
            await server.WaitPost(() => damageable.TryChangeDamage(frame, Blunt(-25)));
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var frameComp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);

                Assert.Multiple(() =>
                {
                    Assert.That(frameComp.Integrity, Is.EqualTo(FixedPoint2.New(100)));
                });
            });

            // Stripping armour lowers the ceiling back to the bare chassis.
            await server.WaitPost(() => Assert.That(powerArmor.TryRemovePiece(frame, PowerArmorPieceSlot.Helmet), Is.True));
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var frameComp = sEntManager.GetComponent<PowerArmorFrameComponent>(frame);

                Assert.Multiple(() =>
                {
                    Assert.That(frameComp.MaxIntegrity, Is.EqualTo(FixedPoint2.New(100)), "bare chassis ceiling");
                    Assert.That(frameComp.Integrity, Is.EqualTo(FixedPoint2.New(85)), "25 damage still outstanding");
                });
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task ConditionColoursComeFromPrototype()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            await server.WaitPost(() => frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords));
            await server.WaitRunTicks(5);

            // The bands are prototype data, so a new set can reskin them in YAML.
            await server.WaitAssertion(() =>
            {
                var condition = sEntManager.GetComponent<PowerArmorFrameComponent>(frame).Condition;

                Assert.Multiple(() =>
                {
                    Assert.That(condition.GoodColor, Is.EqualTo("green"));
                    Assert.That(condition.GoodAt, Is.EqualTo(0.66f));
                    Assert.That(condition.WornColor, Is.EqualTo("yellow"));
                    Assert.That(condition.WornAt, Is.EqualTo(0.33f));
                    Assert.That(condition.BadColor, Is.EqualTo("red"));
                });
            });

            await pair.CleanReturnAsync();
        }

        // Via the layer map, because that is how the prototype's `map:` key is registered.
        private static bool LayerVisible(SpriteSystem sprites, EntityUid uid, SpriteComponent sprite, Enum key)
        {
            return TryLayer(sprites, uid, sprite, key, out var layer) && layer.Visible;
        }

        private static void AssertLayerVisible(
            SpriteSystem sprites,
            EntityUid uid,
            SpriteComponent sprite,
            Enum key,
            bool visible,
            string? state = null)
        {
            Assert.That(TryLayer(sprites, uid, sprite, key, out var layer), Is.True, $"layer {key} must exist");
            Assert.That(layer.Visible, Is.EqualTo(visible), $"layer {key} visibility");

            if (state != null)
                Assert.That(layer.RsiState.ToString(), Is.EqualTo(state), $"layer {key} state");
        }

        private static Vector2 HeadOffset(SpriteSystem sprites, EntityUid uid, SpriteComponent sprite)
        {
            return sprites.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Head, out var index, false)
                ? ((SpriteComponent.Layer) sprite[index]).Offset
                : Vector2.Zero;
        }

        /// <summary>Where the head art actually lands, after scale and offset.</summary>
        private static Vector2 HeadCentre(SpriteSystem sprites, EntityUid uid, SpriteComponent sprite)
        {
            return sprites.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Head, out var index, false)
                ? ((SpriteComponent.Layer) sprite[index]).CalculateBoundingBox().Center
                : Vector2.Zero;
        }

        private static Vector2 HeadScale(SpriteSystem sprites, EntityUid uid, SpriteComponent sprite)
        {
            return sprites.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Head, out var index, false)
                ? sprite[index].Scale
                : Vector2.Zero;
        }

        private static bool TryLayer(            SpriteSystem sprites,
            EntityUid uid,
            SpriteComponent sprite,
            Enum key,
            [NotNullWhen(true)] out ISpriteLayer? layer)
        {
            var found = sprites.LayerMapTryGet((uid, sprite), key, out var index, false);
            layer = found ? sprite[index] : null;
            return found;
        }
    }
}
