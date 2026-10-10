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
using Content.Shared.Hands.Components;
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
using Robust.Client.Graphics;
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

        private static IEnumerable<string> SetProtos(string set) =>
            FullSet.Select(p => p.Proto.Replace("T51", set));

        /// <summary>RSI state a piece draws on the frame, e.g. LeftLegT51 -> leftleg.</summary>
        private static string WornState(string proto) => proto["PowerArmorPiece".Length..]
            .Replace("T51", "", StringComparison.Ordinal)
            .Replace("Helmet", "helmet")
            .Replace("Chest", "chest")
            .Replace("LeftArm", "lefthand")
            .Replace("RightArm", "righthand")
            .Replace("LeftLeg", "leftleg")
            .Replace("RightLeg", "rightleg");

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
                // The chest, not the helmet: the occupant may unbolt their own helmet while inside.
                var chest = sEntManager.SpawnEntity(ChestProto, testMap.GridCoords);
                var chestSlot = sEntManager.GetComponent<ItemSlotsComponent>(frame).Slots[PowerArmorSlotIds.IdFor(PowerArmorPieceSlot.Chest)];

                Assert.That(slots.CanInsert(frame, chest, null, chestSlot), Is.False,
                    "armour must not be installable while somebody is inside the frame");

                Assert.That(powerArmor.TryInstallPiece(frame, chest), Is.False);
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
                foreach (var proto in SetProtos("T51"))
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

            // One RSI per set, and no two sets may share: that is what proves the frame swaps art.
            var allSetRsis = new HashSet<RSI>();

            foreach (var set in SetPrefixes)
            {
                EntityUid frame = default;

                await server.WaitPost(() => frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords));
                await pair.RunTicksSync(5);

                await server.WaitPost(() =>
                {
                    foreach (var proto in SetProtos(set))
                    {
                        var piece = sEntManager.SpawnEntity(proto, testMap.GridCoords);
                        Assert.That(powerArmor.TryInstallPiece(frame, piece), Is.True, $"installing {proto}");
                    }
                });

                await pair.RunTicksSync(10);

                var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));
                var seenRsis = new HashSet<RSI>();

                await client.WaitAssertion(() =>
                {
                    var sprite = cEntManager.GetComponent<SpriteComponent>(clientFrame);
                    var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                    foreach (var (proto, slot) in FullSet)
                    {
                        var layer = SharedPowerArmorSystem.LayerFor(slot);
                        // The worn state name comes from the piece, so each set drives the layer.
                        Assert.That(sprites.LayerMapTryGet((clientFrame, sprite), layer, out var idx, false), Is.True,
                            $"{set} {slot} layer must exist");
                        var pieceLayer = (SpriteComponent.Layer) sprite[idx];
                        Assert.Multiple(() =>
                        {
                            Assert.That(pieceLayer.Visible, Is.True, $"{set} {slot} visible");
                            Assert.That(pieceLayer.State.ToString(), Is.EqualTo(WornState(proto)),
                                $"{set} {slot} state");
                        });
                        seenRsis.Add(((SpriteComponent.Layer) sprite[layer]).RSI);
                    }

                    AssertLayerVisible(sprites, clientFrame, sprite, PowerArmorVisualLayers.Frame, true);
                });

                Assert.That(seenRsis, Has.Count.EqualTo(1), $"{set}: every piece shares one RSI");
                Assert.That(allSetRsis.Add(seenRsis.First()), Is.True, $"{set} drew from a unique RSI");

                await server.WaitPost(() => sEntManager.DeleteEntity(frame));
                await pair.RunTicksSync(5);
            }

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The occupant is reduced to a head, and gets it back on the way out.
        /// </summary>
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
            var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));

            await server.WaitPost(() => Assert.That(powerArmor.TryInsert(frame, pilot), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    Assert.That(sprite.ContainerOccluded, Is.False, "pilot sprite must not be occluded");

                    // The chassis draws above the occupant and covers the body on its own.
                    var frameDepth = cEntManager.GetComponent<SpriteComponent>(clientFrame).DrawDepth;
                    var pilotDepth = sprite.DrawDepth;
                    Assert.That(frameDepth, Is.GreaterThan(pilotDepth),
                        $"frame depth {frameDepth} must be above the pilot's {pilotDepth} or the chassis cannot occlude the body");

                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.True, "head shows");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.False, "torso hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.LArm), Is.False, "arm hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.RLeg), Is.False, "leg hidden");
                });
            });

            // Armour cannot be fitted while occupied, so step out, fit a helmet, climb back in.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(5);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();
                Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.True,
                    "torso restored on the way out");
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
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.False,
                        "helmeted head hidden");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.False,
                        "torso still hidden");
                });
            });

            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.True, "head back");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.True, "torso back");
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

        /// <summary>
        /// An unoccupied frame must still offer its fit/strip verbs. Fitting and stripping happen
        /// from outside, so the enter/exit branch must not swallow the slot loop.
        /// </summary>
        [Test]
        public async Task AnEmptyFrameStillOffersItsSlotVerbs()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var client = pair.Client;

            var sEntManager = server.ResolveDependency<IEntityManager>();
            var cEntManager = client.ResolveDependency<IEntityManager>();
            var powerArmor = sEntManager.EntitySysManager.GetEntitySystem<SharedPowerArmorSystem>();

            var testMap = await pair.CreateTestMap();

            EntityUid frame = default;
            EntityUid helmet = default;
            EntityUid inspector = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
                inspector = sEntManager.SpawnEntity("MobHuman", testMap.GridCoords);
            });

            await pair.RunTicksSync(5);

            await server.WaitPost(() =>
            {
                Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.True);
                Assert.That(powerArmor.IsEmpty(frame), Is.True, "nobody is inside");
            });

            await pair.RunTicksSync(5);

            var clientFrame = cEntManager.GetEntity(sEntManager.GetNetEntity(frame));
            var clientInspector = cEntManager.GetEntity(sEntManager.GetNetEntity(inspector));

            await client.WaitAssertion(() =>
            {
                // The handler ignores anyone without hands, so this has to be a real mob.
                var hands = cEntManager.GetComponent<HandsComponent>(clientInspector);

                var ev = new GetVerbsEvent<AlternativeVerb>(
                    clientInspector, clientFrame, null, hands,
                    canInteract: true, canComplexInteract: true, canAccess: true,
                    new List<VerbCategory>());

                cEntManager.EventBus.RaiseLocalEvent(clientFrame, ev, true);

                var verbs = ev.Verbs.OfType<AlternativeVerb>().ToList();

                Assert.Multiple(() =>
                {
                    Assert.That(ev.Verbs.Count, Is.GreaterThan(0), "the frame must offer verbs at all");

                    // One fitted piece, so exactly one strip verb; no held item, so no fit verb.
                    Assert.That(verbs.Count(v => v.Category == VerbCategory.Eject), Is.EqualTo(1),
                        "an empty frame must still offer the helmet strip verb");
                    Assert.That(verbs.Count(v => v.Category == VerbCategory.Insert), Is.EqualTo(0),
                        "nothing is held, so there is nothing to fit");

                    Assert.That(verbs.Count(v => v.Text == "power-armor-frame-verb-enter"), Is.EqualTo(1),
                        "an empty frame must offer the enter verb");
                    Assert.That(verbs.Count(v => v.Text == "power-armor-frame-verb-exit"), Is.EqualTo(0),
                        "nobody is inside, so there is nobody to exit");
                });
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Taking the helmet off inside the suit must hand the head back. The head layers are
        /// tracked as hidden, so this is a state change and not a one-way hide.
        /// </summary>
        [Test]
        public async Task TakingTheHelmetOffInsideRestoresTheHead()
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
            EntityUid helmet = default;

            await server.WaitPost(() =>
            {
                frame = sEntManager.SpawnEntity(FrameProto, testMap.GridCoords);
                pilot = sEntManager.SpawnEntity("MobHuman", testMap.GridCoords);
                helmet = sEntManager.SpawnEntity(HelmetProto, testMap.GridCoords);
            });

            await pair.RunTicksSync(10);

            var clientPilot = cEntManager.GetEntity(sEntManager.GetNetEntity(pilot));

            await server.WaitPost(() => Assert.That(powerArmor.TryInsert(frame, pilot), Is.True));
            await pair.RunTicksSync(10);

            // The helmet is exempt from the occupied-frame lock, so it goes on from inside.
            await server.WaitPost(() => Assert.That(powerArmor.TryInstallPiece(frame, helmet), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();
                Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.False,
                    "helmeted head is hidden");
            });

            await server.WaitPost(() =>
                Assert.That(powerArmor.TryRemovePiece(frame, PowerArmorPieceSlot.Helmet), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);
                var sprites = cEntManager.EntitySysManager.GetEntitySystem<SpriteSystem>();

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Head), Is.True,
                        "head comes back when the helmet comes off from inside");
                    Assert.That(LayerVisible(sprites, clientPilot, sprite, HumanoidVisualLayers.Chest), Is.False,
                        "the torso stays under the chassis");
                });
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
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(100)), "welding the chassis directly fully repairs");
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
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(60)), "back down to 60 after 40 damage");
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
                Assert.That(comp.Integrity, Is.EqualTo(FixedPoint2.New(100)),
                    "welding the occupant fully repairs the frame");
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
                    // Fitted armour soaks part of the hit before the pool bills it: 40 * (100 / 115).
                       var expected = 115f - 40f * PowerArmorFrameComponent.DamageMultiplier(frameComp.MaxArmor);
                       Assert.That((float) frameComp.Integrity, Is.EqualTo(expected).Within(0.5f),
                           "40 partly soaked by the fitted helmet");
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
                    // Welding is ignoreResistances, so it heals the full 25 on top of the soaked hit.
                    var soak = PowerArmorFrameComponent.DamageMultiplier(frameComp.MaxArmor);
                    Assert.That((float) frameComp.Integrity, Is.EqualTo(115f - 40f * soak + 25f).Within(0.5f));
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
                    // Ceiling drops to 100, and the soaked hit minus the weld stays outstanding.
                    var helmetArmour = sEntManager.GetComponent<PowerArmorPieceComponent>(helmet).Armor;
                    var outstanding = 40f * PowerArmorFrameComponent.DamageMultiplier(helmetArmour) - 25f;
                    Assert.That((float) frameComp.Integrity, Is.EqualTo(100f - outstanding).Within(0.5f),
                        "soaked damage less the weld still outstanding");
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
