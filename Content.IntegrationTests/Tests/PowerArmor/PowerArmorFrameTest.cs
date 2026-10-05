using Content.Client.PowerArmor;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Humanoid;
using Content.Shared.Movement.Components;
using Content.Shared.PowerArmor;
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
        private const string FrameProto = "PowerArmorFrameT45";
        private const string JunkProto = "PowerArmorFrameTestJunk";
        private const string HelmetProto = "PowerArmorPieceHelmetT45";
        private const string ChestProto = "PowerArmorPieceChestT45";
        private const string LeftArmProto = "PowerArmorPieceLeftArmT45";
        private const string RightArmProto = "PowerArmorPieceRightArmT45";
        private const string LeftLegProto = "PowerArmorPieceLeftLegT45";
        private const string RightLegProto = "PowerArmorPieceRightLegT45";

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
                    Assert.That(frameComp.Pilot, Is.EqualTo(pilot));
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
                    Assert.That(frameComp.Pilot, Is.Null);
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

                Assert.Multiple(() =>
                {
                    // A bare frame shows only its own chassis.
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Frame, true, "frame");

                    foreach (var slot in PowerArmorSlotIds.All)
                        AssertLayerVisible(sprite, SharedPowerArmorSystem.LayerFor(slot), visible: false);
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

                Assert.Multiple(() =>
                {
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Helmet, true, "helmet");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Chest, true, "chest");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.LeftArm, true, "lefthand");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.RightArm, true, "righthand");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.LeftLeg, true, "leftleg");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.RightLeg, true, "rightleg");
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Frame, true, "frame");
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

                Assert.Multiple(() =>
                {
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Helmet, false);
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Chest, true);
                    AssertLayerVisible(sprite, PowerArmorVisualLayers.Frame, true);
                });
            });

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
                Assert.That(powerArmor.TryInsert(frame, pilot), Is.True);
            });

            await pair.RunTicksSync(10);

            var clientPilot = cEntManager.GetEntity(sEntManager.GetNetEntity(pilot));

            // Unhelmeted: head layers up, body layers down.
            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);

                Assert.Multiple(() =>
                {
                    // The container must show contents or the head could never be seen.
                    Assert.That(sprite.ContainerOccluded, Is.False, "pilot sprite must not be occluded");

                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Head), Is.True, "head should show");
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Face), Is.True, "face should show");

                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Chest), Is.False, "torso must be hidden");
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.LArm), Is.False, "arm must be hidden");
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.RLeg), Is.False, "leg must be hidden");
                });
            });

            // A helmet must hide the head: render order is global, so it cannot cover it.
            // Armour cannot be fitted while occupied, so step out, fit it, climb back in.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(5);

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

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Head), Is.False, "helmeted head must be hidden");
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Chest), Is.False, "torso must stay hidden");
                });
            });

            // Climbing out restores the mob's normal appearance.
            await server.WaitPost(() => Assert.That(powerArmor.TryEject(frame), Is.True));
            await pair.RunTicksSync(10);

            await client.WaitAssertion(() =>
            {
                var sprite = cEntManager.GetComponent<SpriteComponent>(clientPilot);

                Assert.Multiple(() =>
                {
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Head), Is.True, "head restored");
                    Assert.That(LayerVisible(sprite, HumanoidVisualLayers.Chest), Is.True, "torso restored");
                });
            });

            await pair.CleanReturnAsync();
        }

        // Via the layer map, because that is how the prototype's `map:` key is registered.
        private static bool LayerVisible(SpriteComponent sprite, HumanoidVisualLayers layer)
        {
            Assert.That(sprite.LayerMapTryGet(layer, out _), Is.True, $"layer {layer} must exist");
            return LayerVisible(sprite, (object) layer);
        }

        private static bool LayerVisible(SpriteComponent sprite, PowerArmorVisualLayers layer)
        {
            Assert.That(sprite.LayerMapTryGet(layer, out _), Is.True, $"layer {layer} must exist");
            return LayerVisible(sprite, (object) layer);
        }

        private static bool LayerVisible(SpriteComponent sprite, object key)
        {
            Assert.That(sprite.LayerMapTryGet(key, out var index), Is.True, "layer must exist");
            return sprite[index].Visible;
        }

        private static string? LayerState(SpriteComponent sprite, PowerArmorVisualLayers layer)
        {
            Assert.That(sprite.LayerMapTryGet(layer, out var index), Is.True, $"layer {layer} must exist");
            return sprite[index].RsiState.ToString();
        }

        private static void AssertLayerVisible(
            SpriteComponent sprite,
            PowerArmorVisualLayers layer,
            bool visible,
            string? state = null)
        {
            Assert.That(LayerVisible(sprite, layer), Is.EqualTo(visible), $"layer {layer} visibility");

            if (state != null)
                Assert.That(LayerState(sprite, layer), Is.EqualTo(state), $"layer {layer} state");
        }
    }
}