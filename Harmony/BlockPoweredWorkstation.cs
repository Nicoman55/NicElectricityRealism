using UnityEngine;

namespace ElectricityRealism
{
    public class BlockPoweredWorkstation : BlockPowered
    {
        private const string Version = "v0.11";

        // Mirrors the vanilla BlockWorkstation command list exactly.
        // "open"  = short press E — open the crafting UI
        // "take"  = long press E  — pick up the workstation
        private new readonly BlockActivationCommand[] cmds = new BlockActivationCommand[]
        {
            new BlockActivationCommand("open", "campfire", true,  false, null),
            new BlockActivationCommand("take", "hand",     false, false, null)
        };

        // ── Tile entity ──────────────────────────────────────────────────────────

        public override void OnBlockAdded(
            WorldBase _world,
            Chunk _chunk,
            Vector3i _blockPos,
            BlockValue _blockValue,
            PlatformUserIdentifierAbs _addedByPlayer)
        {
            base.OnBlockAdded(_world, _chunk, _blockPos, _blockValue, _addedByPlayer);

            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism BlockPoweredWorkstation.OnBlockAdded called");
                Debug.Log(System.Environment.StackTrace);
            }

            // Check if already our type — nothing to do.
            if (_world.GetTileEntity(_chunk.ClrIdx, _blockPos) is TileEntityPoweredWorkstation)
            {
                return;
            }

            // Create our tile entity — replacing any plain TileEntityWorkstation from disk.
            TileEntityPoweredWorkstation tileEntity = new TileEntityPoweredWorkstation(_chunk);
            tileEntity.localChunkPos = World.toBlock(_blockPos);

            // If a plain TileEntityWorkstation exists (loaded from disk), copy its data.
            TileEntityWorkstation existing =
                _world.GetTileEntity(_chunk.ClrIdx, _blockPos) as TileEntityWorkstation;
            if (existing != null)
            {
                tileEntity.fuel = existing.fuel;
                tileEntity.tools = existing.tools;
                tileEntity.input = existing.input;
                tileEntity.output = existing.output;
                tileEntity.queue = existing.queue;
                tileEntity.lastTickTime = existing.lastTickTime;
                tileEntity.currentBurnTimeLeft = existing.currentBurnTimeLeft;
                tileEntity.currentMeltTimesLeft = existing.currentMeltTimesLeft;
                tileEntity.lastInput = existing.lastInput;
                tileEntity.isBurning = existing.isBurning;
                tileEntity.isPlayerPlaced = existing.isPlayerPlaced;
                tileEntity.isModuleUsed = existing.isModuleUsed;
                tileEntity.materialNames = existing.materialNames;
                tileEntity.CraftCompleteList = existing.CraftCompleteList;
                tileEntity.entityId = existing.entityId;
            }
            else
            {
                tileEntity.IsPlayerPlaced = true;
            }

            tileEntity.InitializePowerData();
            _chunk.AddTileEntity(tileEntity);

            // On fresh placement (not loaded from disk), ensure forge starts cold.
            if (existing == null)
            {
                tileEntity.isBurning = false;
                //tileEntity.visibleWorking = true; // ← force mismatch so UpdateVisible fires
                BlockValue bv = _world.GetBlock(_blockPos);
                if (bv.meta != 0)
                {
                    bv.meta = 0;
                    _world.SetBlockRPC(_blockPos, bv);
                }
            }

            // Also trigger UpdateVisible immediately to sync the model state.
            tileEntity.visibleChanged = true;
            tileEntity.UpdateVisible();
            GameManager.Instance.StartCoroutine(DelayedUpdateVisible(tileEntity));
        }

        public override void OnBlockRemoved(
            WorldBase world,
            Chunk _chunk,
            Vector3i _blockPos,
            BlockValue _blockValue)
        {
            if (_blockValue.ischild) { return; }

            TileEntityPoweredWorkstation workstation =
                _chunk.GetTileEntity(World.toBlock(_blockPos)) as TileEntityPoweredWorkstation;

            if (workstation != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                if (workstation.PowerItem != null)
                {
                    if (workstation.PowerItem.Parent != null &&
                        workstation.PowerItem.Parent.TileEntity != null)
                    {
                        TileEntityPowered parentTE = workstation.PowerItem.Parent.TileEntity;
                        PowerManager.Instance.RemovePowerNode(workstation.PowerItem);
                        parentTE.CreateWireDataFromPowerItem();
                        parentTE.SendWireData();
                        parentTE.RemoveWires();
                        parentTE.DrawWires();
                    }
                    else
                    {
                        PowerManager.Instance.RemovePowerNode(workstation.PowerItem);
                    }
                }
                workstation.RemoveWires();
            }


            // Remove our tile entity BEFORE calling base, so base's RemoveTileEntityAt<TileEntityPowered>
            // finds nothing and harmlessly skips, while our entity is already gone.
            _chunk.RemoveTileEntityAt<TileEntityPoweredWorkstation>(
                (World)world, World.toBlock(_blockPos));

            // Remove particle effect if present.
            if (this.particleName != null)
                world.GetGameManager().RemoveBlockParticleEffect(_blockPos);

            // Now call base — handles model/transform cleanup and wire tool check.
            // Its RemoveTileEntityAt<TileEntityPowered> will find nothing since we already removed ours.
            base.OnBlockRemoved(world, _chunk, _blockPos, _blockValue);
        }

        // ── Player interaction ───────────────────────────────────────────────────

        public override string GetActivationText(
            WorldBase _world,
            BlockValue _blockValue,
            int _clrIdx,
            Vector3i _blockPos,
            EntityAlive _entityFocusing)
        {
            TileEntityPoweredWorkstation poweredWorkstation =
                _world.GetTileEntity(_clrIdx, _blockPos) as TileEntityPoweredWorkstation;

            bool isReceivingPower = poweredWorkstation != null && poweredWorkstation.IsReceivingPower;

            if (!isReceivingPower)
            {
                return Localization.Get("electricityRealismNoPower");
            }

            // Use the same localization key as vanilla workstations.
            return Localization.Get("useWorkstation");
        }

        public override bool HasBlockActivationCommands(
            WorldBase _world,
            BlockValue _blockValue,
            int _clrIdx,
            Vector3i _blockPos,
            EntityAlive _entityFocusing)
        {
            return true;
        }

        public override BlockActivationCommand[] GetBlockActivationCommands(
            WorldBase _world,
            BlockValue _blockValue,
            int _clrIdx,
            Vector3i _blockPos,
            EntityAlive _entityFocusing)
        {
            TileEntityPoweredWorkstation poweredWorkstation =
                _world.GetTileEntity(_clrIdx, _blockPos) as TileEntityPoweredWorkstation;

            bool isReceivingPower = poweredWorkstation != null && poweredWorkstation.IsReceivingPower;

            // "open" only enabled when powered.
            // "take" only enabled when on own land claim and player-placed,
            // mirroring the vanilla logic exactly.
            bool isMyLand = _world.IsMyLandProtectedBlock(
                _blockPos,
                _world.GetGameManager().GetPersistentLocalPlayer(),
                false);

            bool isPlayerPlaced = poweredWorkstation != null && poweredWorkstation.IsPlayerPlaced;

            this.cmds[0].enabled = isReceivingPower;
            this.cmds[1].enabled = isMyLand && isPlayerPlaced && this.TakeDelay > 0f;

            return this.cmds;
        }

        public override bool OnBlockActivated(
            string _commandName,
            WorldBase _world,
            int _cIdx,
            Vector3i _blockPos,
            BlockValue _blockValue,
            EntityPlayerLocal _player)
        {
            if (_commandName == "take")
            {
                this.TakeItemWithTimer(_cIdx, _blockPos, _blockValue, _player);
                return true;
            }

            if (_commandName == "open")
            {
                TileEntityPoweredWorkstation poweredWorkstation =
                    _world.GetTileEntity(_blockPos) as TileEntityPoweredWorkstation;

                if (poweredWorkstation == null)
                {
                    return false;
                }

                bool isReceivingPower = poweredWorkstation.IsReceivingPower;

                if (!isReceivingPower)
                {
                    GameManager.ShowTooltip(
                        _player,
                        Localization.Get("electricityRealismNoPower"),
                        false, false, 0f);
                    return false;
                }

                // Open the workstation UI the same way vanilla does —
                // TELockServer triggers the server-side lock and opens the UI.
                _player.AimingGun = false;
                Vector3i worldPos = poweredWorkstation.ToWorldPos();
                _world.GetGameManager().TELockServer(
                    _cIdx, worldPos, poweredWorkstation.entityId, _player.entityId, null);
                return true;
            }

            return false;
        }

        // Override Init to register WorkstationData with CraftingManager,
        // exactly as BlockWorkstation.Init() does. Without this,
        // workstationOpened() cannot find the window to open.
        public override void Init()
        {
            base.Init();
            this.TakeDelay = 2f;
            base.Properties.ParseFloat("TakeDelay", ref this.TakeDelay);
            WorkstationData workstationData = new WorkstationData(base.GetBlockName(), base.Properties);
            CraftingManager.AddWorkstationData(workstationData);

            if (base.Properties.Values.ContainsKey("ParticleName"))
            {
                this.particleName = base.Properties.Values["ParticleName"];
                ParticleEffect.LoadAsset(this.particleName);
            }
            if (base.Properties.Values.ContainsKey("ParticleOffset"))
            {
                this.particleOffset = StringParsers.ParseVector3(
                    base.Properties.Values["ParticleOffset"], 0, -1);
            }
        }

        public override void OnBlockEntityTransformBeforeActivated(
            WorldBase _world,
            Vector3i _blockPos,
            BlockValue _blockValue,
            BlockEntityData _ebcd)
        {
            base.OnBlockEntityTransformBeforeActivated(_world, _blockPos, _blockValue, _ebcd);
            TileEntityWorkstation te =
                _world.GetTileEntity(_blockPos) as TileEntityWorkstation;
            if (te != null)
            {
                BlockWorkstation bws = Block.list[_world.GetBlock(_blockPos).type] as BlockWorkstation;
                if (bws != null)
                    bws.UpdateVisible(te);
            }
        }

        private string particleName;
        private Vector3 particleOffset;

        public override void OnBlockValueChanged(WorldBase world, Chunk _chunk, int _clrIdx,
            Vector3i _blockPos, BlockValue _oldBlockValue, BlockValue _newBlockValue)
        {
            base.OnBlockValueChanged(world, _chunk, _clrIdx, _blockPos, _oldBlockValue, _newBlockValue);
            if (this.particleName == null) { return; }
            if (_newBlockValue.meta != 0 && !world.GetGameManager().HasBlockParticleEffect(_blockPos))
            {
                Vector3 offset = this.shape.GetRotation(_newBlockValue) *
                    (this.particleOffset - new Vector3(0.5f, 0.5f, 0.5f)) + new Vector3(0.5f, 0.5f, 0.5f);
                world.GetGameManager().SpawnBlockParticleEffect(
                    _blockPos,
                    new ParticleEffect(this.particleName,
                        _blockPos.ToVector3() + offset,
                        this.shape.GetRotation(_newBlockValue),
                        1f, Color.white));
            }
            else if (_newBlockValue.meta == 0 && world.GetGameManager().HasBlockParticleEffect(_blockPos))
            {
                world.GetGameManager().RemoveBlockParticleEffect(_blockPos);
            }
        }

        public override void OnBlockLoaded(WorldBase _world, int _clrIdx,
            Vector3i _blockPos, BlockValue _blockValue)
        {
            base.OnBlockLoaded(_world, _clrIdx, _blockPos, _blockValue);
            if (_blockValue.ischild) { return; }
            if (this.particleName == null) { return; }
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism OnBlockLoaded meta=" + _blockValue.meta +
                    " particleName=" + (this.particleName ?? "null"));
            }
            if (_blockValue.meta != 0 && !_world.GetGameManager().HasBlockParticleEffect(_blockPos))
            {
                Vector3 offset = this.shape.GetRotation(_blockValue) *
                    (this.particleOffset - new Vector3(0.5f, 0.5f, 0.5f)) + new Vector3(0.5f, 0.5f, 0.5f);
                _world.GetGameManager().SpawnBlockParticleEffect(
                    _blockPos,
                    new ParticleEffect(this.particleName,
                        _blockPos.ToVector3() + offset,
                        this.shape.GetRotation(_blockValue),
                        1f, Color.white));
            }
        }

        public override void OnBlockUnloaded(WorldBase _world, int _clrIdx,
            Vector3i _blockPos, BlockValue _blockValue)
        {
            base.OnBlockUnloaded(_world, _clrIdx, _blockPos, _blockValue);
            if (this.particleName != null)
                _world.GetGameManager().RemoveBlockParticleEffect(_blockPos);
        }

        private static System.Collections.IEnumerator DelayedUpdateVisible(TileEntityPoweredWorkstation te)
        {
            //yield return null; // wait one frame
            yield return new WaitForSeconds(0.5f);
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism DelayedUpdateVisible firing");
            }
            te.visibleWorking = true;
            te.isBurning = false;
            te.visibleChanged = true;
            te.UpdateVisible();
        }
    }
}