using Audio;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ElectricityRealism
{
    public class NicElectricityRealism : IModApi
    {
        private const string Version = "v0.53";
        
        // Global variable for debugging; true: Debugging on, false: Debugging off
        public static bool DebugLog = true;

        public void InitMod(Mod mod)
        {
            Debug.Log("Loading Electricity Realism Patch: " + base.GetType().ToString());
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism NicElectricityRealism " + Version);
            }
            Harmony harmony = new Harmony(base.GetType().ToString());
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        public NicElectricityRealism()
        {
        }

        [HarmonyPatch(typeof(GameManager))]
        [HarmonyPatch("OpenTileEntityUi")]
        public class GameManager_OpenTileEntityUi
        {
            public static bool Prefix(
                GameManager __instance,
                int _entityIdThatOpenedIt,
                TileEntity _te,
                string _customUi,
                World ___m_World)
            {
                TileEntityPoweredWorkstation poweredWorkstation =
                    _te as TileEntityPoweredWorkstation;

                bool isOurWorkstation = poweredWorkstation != null;

                if (!isOurWorkstation)
                {
                    return true;
                }

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism OpenTileEntityUi:" +
                        " IsReceivingPower=" + poweredWorkstation.IsReceivingPower +
                        " PowerItem=" + (poweredWorkstation.PowerItem == null ? "NULL" : "OK") +
                        " PowerItem.IsPowered=" + (poweredWorkstation.PowerItem?.IsPowered ?? false));
                }

                bool isReceivingPower = poweredWorkstation.IsReceivingPower;

                if (isReceivingPower)
                {
                    return true;
                }

                EntityPlayerLocal playerEntity =
                    ___m_World.GetEntity(_entityIdThatOpenedIt) as EntityPlayerLocal;

                bool playerFound = playerEntity != null;

                if (playerFound)
                {
                    GameManager.ShowTooltip(
                        playerEntity,
                        Localization.Get("electricityRealismNoPower"),
                        false,
                        false,
                        0f);
                }

                return false;
            }

            public GameManager_OpenTileEntityUi()
            {
            }
        }
    
        // ── NetPackageWireToolActions.ProcessPackage patch ───────────────────────────
        //
        // The wire tool's network package casts the tile entity to TileEntityPowered.
        // Since TileEntityPoweredWorkstation extends TileEntityWorkstation instead,
        // that cast returns null and wire connection fails.
        // This Prefix detects our tile entity and sets up the wire node manually.
        [HarmonyPatch(typeof(NetPackageWireToolActions))]
        [HarmonyPatch("ProcessPackage")]
        public class NetPackageWireToolActions_ProcessPackage
        {
            public static bool Prefix(
                NetPackageWireToolActions __instance,
                World _world,
                GameManager _callbacks,
                Vector3i ___tileEntityPosition,
                NetPackageWireToolActions.WireActions ___currentOperation,
                int ___entityID)
            {
                if (_world == null) { return true; }
                if (___currentOperation != NetPackageWireToolActions.WireActions.AddWire) { return true; }

                Chunk chunk = _world.GetChunkFromWorldPos(
                    ___tileEntityPosition.x,
                    ___tileEntityPosition.y,
                    ___tileEntityPosition.z) as Chunk;

                if (chunk == null) { return true; }

                // Check if the tile entity at this position is ours.
                TileEntityPoweredWorkstation poweredWorkstation =
                    _world.GetTileEntity(chunk.ClrIdx, ___tileEntityPosition)
                    as TileEntityPoweredWorkstation;

                if (poweredWorkstation == null)
                {
                    // Not our block — let vanilla handle it.
                    return true;
                }

                // It is our block. Handle the wire node setup ourselves,
                // mirroring exactly what the vanilla code does for TileEntityPowered.
                EntityPlayer entityPlayer =
                    _world.GetEntity(___entityID) as EntityPlayer;

                if (entityPlayer != null)
                {
                    Transform transform = entityPlayer.RootTransform.FindInChilds(
                        entityPlayer.GetRightHandTransformName(), false);

                    if (transform != null)
                    {
                        ItemActionConnectPower.ConnectPowerData connectPowerData =
                            (ItemActionConnectPower.ConnectPowerData)
                            entityPlayer.inventory.holdingItemData.actionData[1];

                        WireNode component = ((GameObject)UnityEngine.Object.Instantiate(
                            Resources.Load("Prefabs/WireNode"))).GetComponent<WireNode>();

                        component.LocalPosition =
                            poweredWorkstation.ToWorldPos().ToVector3() - Origin.position;
                        component.localOffset = poweredWorkstation.GetWireOffset();
                        component.localOffset.x += 0.5f;
                        component.localOffset.y += 0.5f;
                        component.localOffset.z += 0.5f;
                        component.Source = transform.gameObject;
                        component.TogglePulse(false);
                        component.SetPulseSpeed(360f);
                        connectPowerData.wireNode = component;
                    }
                }

                if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            ___currentOperation, ___tileEntityPosition, ___entityID),
                        false, -1, ___entityID, -1, null, 192, false);
                }

                // Skip the original method since we handled it.
                return false;
            }
        }

        // Patches ItemActionConnectPower.OnHoldingUpdate to handle our workstation
        // when GetPoweredBlock returns null for it (because it's not a TileEntityPowered).
        [HarmonyPatch(typeof(ItemActionConnectPower))]
        [HarmonyPatch("OnHoldingUpdate")]
        [HarmonyPriority(Priority.High)]
        public class ItemActionConnectPower_OnHoldingUpdate
        {

            public static bool Prefix(ItemActionData _actionData)
            {
                ItemActionConnectPower.ConnectPowerData connectPowerData =
                    _actionData as ItemActionConnectPower.ConnectPowerData;

                if (connectPowerData == null) { return true; }

                if (NicElectricityRealism.DebugLog)
                {
                    if (connectPowerData.StartLink || connectPowerData.HasStartPoint)
                    {
                        Debug.Log("ElectricityRealism OnHoldingUpdate Prefix fired" +
                            " StartLink=" + connectPowerData.StartLink +
                            " HasStartPoint=" + connectPowerData.HasStartPoint);
                    }
                }

                ItemInventoryData invData = _actionData.invData;

                // ── SECOND CLICK ──────────────────────────────────────────────────────────
                if (connectPowerData.HasStartPoint && connectPowerData.StartLink)
                {
                    World world = invData.world;
                    Vector3i destPos = invData.hitInfo.hit.blockPos;
                    if (destPos == connectPowerData.startPoint) { return true; }

                    // Case A: destination is our workstation, source is vanilla.
                    Block destBlock = world.GetBlock(destPos).Block;
                    if (destBlock is BlockPowered)
                    {
                        Chunk destChunk = world.GetChunkFromWorldPos(destPos.x, destPos.y, destPos.z) as Chunk;
                        if (destChunk != null)
                        {
                            TileEntityPoweredWorkstation workstationDest =
                                destChunk.GetTileEntity(World.toBlock(destPos)) as TileEntityPoweredWorkstation;
                            if (workstationDest != null)
                            {
                                if (NicElectricityRealism.DebugLog)
                                    Debug.Log("ElectricityRealism: second click — dest is our workstation");

                                Chunk startChunk = world.GetChunkFromWorldPos(
                                    connectPowerData.startPoint.x,
                                    connectPowerData.startPoint.y,
                                    connectPowerData.startPoint.z) as Chunk;
                                if (startChunk == null) { return true; }

                                TileEntityPowered sourceTE =
                                    world.GetTileEntity(startChunk.ClrIdx, connectPowerData.startPoint)
                                    as TileEntityPowered;

                                // Source might also be our workstation — handle both cases.
                                if (sourceTE == null)
                                {
                                    TileEntityPoweredWorkstation sourceWorkstation =
                                        world.GetTileEntity(startChunk.ClrIdx, connectPowerData.startPoint)
                                        as TileEntityPoweredWorkstation;
                                    if (sourceWorkstation == null) { return true; }

                                    // Source is our workstation, dest is also our workstation — chain connection.
                                    if (!workstationDest.CanHaveParent(sourceWorkstation)) { return false; }
                                    if (sourceWorkstation.ChildCount > 8) { return false; }

                                    workstationDest.SetParentWithWireTool(sourceWorkstation, invData.holdingEntity.entityId);
                                    Manager.BroadcastPlay(
                                        workstationDest.ToWorldPos().ToVector3(),
                                        sourceWorkstation.IsPowered ? "wire_live_connect" : "wire_dead_connect", 0f);

                                    invData.holdingEntity.RightArmAnimationUse = true;
                                    connectPowerData.StartLink = false;
                                    connectPowerData.HasStartPoint = false;
                                    CleanupWireNode(connectPowerData);
                                    SendRemoveWire(invData);
                                    return false;
                                }

                                if (!workstationDest.CanHaveParent(sourceTE)) { return true; }
                                if (sourceTE.ChildCount > 8) { return true; }

                                workstationDest.SetParentWithWireTool(sourceTE, invData.holdingEntity.entityId);
                                Manager.BroadcastPlay(
                                    workstationDest.ToWorldPos().ToVector3(),
                                    sourceTE.IsPowered ? "wire_live_connect" : "wire_dead_connect", 0f);

                                invData.holdingEntity.RightArmAnimationUse = true;
                                connectPowerData.StartLink = false;
                                connectPowerData.HasStartPoint = false;
                                CleanupWireNode(connectPowerData);
                                SendRemoveWire(invData);
                                return false;
                            }
                        }
                    }

                    // Case B: start point is our workstation, destination is vanilla.
                    Chunk startChunkB = world.GetChunkFromWorldPos(
                        connectPowerData.startPoint.x,
                        connectPowerData.startPoint.y,
                        connectPowerData.startPoint.z) as Chunk;
                    if (startChunkB == null) { return true; }

                    TileEntityPoweredWorkstation workstationSource =
                        world.GetTileEntity(startChunkB.ClrIdx, connectPowerData.startPoint)
                        as TileEntityPoweredWorkstation;
                    if (workstationSource == null) { return true; }

                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism: Case B — source is our workstation");

                    Chunk destChunkB = world.GetChunkFromWorldPos(destPos.x, destPos.y, destPos.z) as Chunk;
                    if (destChunkB == null) { return true; }

                    TileEntityPowered destTE =
                        world.GetTileEntity(destChunkB.ClrIdx, destPos) as TileEntityPowered;

                    if (destTE == null)
                    {
                        // Tile entity not ready yet — let vanilla run to create it, then we'll catch it next click.
                        if (NicElectricityRealism.DebugLog)
                            Debug.Log("ElectricityRealism: Case B destTE null, letting vanilla create it");
                        return true;
                    }

                    // Tile entity exists — handle the connection ourselves.
                    if (!workstationSource.CanHaveParent(destTE)) { return true; }
                    if (destTE.ChildCount > 8) { return true; }

                    destTE.SetParentWithWireTool(workstationSource, invData.holdingEntity.entityId);

                    if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    {
                        workstationSource.CreateWireDataFromPowerItem();
                        workstationSource.SendWireData();
                        workstationSource.RemoveWires();
                        workstationSource.DrawWires();
                    }

                    Manager.BroadcastPlay(
                        destTE.ToWorldPos().ToVector3(),
                        workstationSource.IsPowered ? "wire_live_connect" : "wire_dead_connect", 0f);

                    invData.holdingEntity.RightArmAnimationUse = true;

                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism: Case B connected successfully");

                    connectPowerData.StartLink = false;
                    connectPowerData.HasStartPoint = false;
                    CleanupWireNode(connectPowerData);
                    SendRemoveWire(invData);
                    return false;
                }

                // ── FIRST CLICK: set the start point on our workstation ───────────────
                if (!connectPowerData.StartLink) { return true; }
                if (connectPowerData.HasStartPoint) { return true; }

                Block block = invData.world.GetBlock(invData.hitInfo.hit.blockPos).Block;
                if (!(block is BlockPowered)) { return true; }

                Vector3i blockPos = invData.hitInfo.hit.blockPos;
                ChunkCluster chunkCluster = invData.world.ChunkClusters[invData.hitInfo.hit.clrIdx];
                if (chunkCluster == null) { return true; }

                Chunk chunk = (Chunk)chunkCluster.GetChunkSync(
                    World.toChunkXZ(blockPos.x), blockPos.y, World.toChunkXZ(blockPos.z));
                if (chunk == null) { return true; }

                TileEntityPoweredWorkstation workstation =
                    chunk.GetTileEntity(World.toBlock(blockPos)) as TileEntityPoweredWorkstation;
                if (workstation == null)
                {
                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism OnHoldingUpdate: not our workstation, block=" + block.GetType().Name);
                    return true;
                }

                if (NicElectricityRealism.DebugLog)
                    Debug.Log("ElectricityRealism OnHoldingUpdate: found our workstation, setting start point");

                connectPowerData.StartLink = false;
                connectPowerData.startPoint = blockPos;
                connectPowerData.HasStartPoint = true;
                invData.holdingEntity.RightArmAnimationUse = true;

                if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.AddWire,
                            blockPos,
                            invData.holdingEntity.entityId),
                        false, -1, -1, -1, null, 192, false);
                }
                else
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.AddWire,
                            blockPos,
                            invData.holdingEntity.entityId), false);
                }

                Manager.BroadcastPlay(
                    workstation.ToWorldPos().ToVector3(),
                    workstation.IsPowered ? "wire_live_connect" : "wire_dead_connect",
                    0f);

                // Robust hand transform lookup — mirrors GetHandTransform exactly.
                EntityAlive holdingEntity = invData.holdingEntity;
                Transform handTransform = null;

                Transform graphics = holdingEntity.RootTransform.Find("Graphics");
                if (graphics != null)
                {
                    Transform t = graphics.FindInChilds(holdingEntity.GetRightHandTransformName(), true);
                    if (t != null && t.childCount > 0)
                        handTransform = t;
                }
                if (handTransform == null)
                {
                    Transform camera = holdingEntity.RootTransform.Find("Camera");
                    if (camera != null)
                    {
                        Transform t = camera.FindInChilds(holdingEntity.GetRightHandTransformName(), true);
                        if (t != null && t.childCount > 0)
                            handTransform = t;
                    }
                }
                if (handTransform == null)
                {
                    handTransform = holdingEntity.emodel.GetRightHandTransform();
                }

                if (NicElectricityRealism.DebugLog)
                {
                    Transform dbgWireMesh = handTransform?.FindInChilds("wire_mesh", false);
                    Debug.Log("ElectricityRealism first click: handTransform=" +
                        (handTransform == null ? "NULL" : "OK") +
                        " wireMesh=" + (dbgWireMesh == null ? "NULL" : "OK"));
                }

                if (handTransform != null)
                {
                    Transform wireMesh = handTransform.FindInChilds("wire_mesh", false);
                    if (wireMesh != null)
                    {
                        if (connectPowerData.wireNode != null)
                        {
                            WireManager.Instance.RemoveActiveWire(connectPowerData.wireNode);
                            UnityEngine.Object.Destroy(connectPowerData.wireNode.gameObject);
                            connectPowerData.wireNode = null;
                        }

                        WireNode component = ((GameObject)UnityEngine.Object.Instantiate(
                            Resources.Load("Prefabs/WireNode"))).GetComponent<WireNode>();

                        component.LocalPosition = blockPos.ToVector3() - Origin.position;
                        component.localOffset = workstation.GetWireOffset();
                        component.localOffset.x += 0.5f;
                        component.localOffset.y += 0.5f;
                        component.localOffset.z += 0.5f;
                        component.Source = wireMesh.gameObject;
                        component.TogglePulse(false);
                        component.SetPulseSpeed(360f);
                        connectPowerData.wireNode = component;
                        WireManager.Instance.AddActiveWire(component);
                        return false; // skip vanilla for first click too
                    }
                }
                return true; // fallthrough — let vanilla handle
            }

            private static void CleanupWireNode(ItemActionConnectPower.ConnectPowerData connectPowerData)
            {
                if (connectPowerData.wireNode != null)
                {
                    WireManager.Instance.RemoveActiveWire(connectPowerData.wireNode);
                    UnityEngine.Object.Destroy(connectPowerData.wireNode.gameObject);
                    connectPowerData.wireNode = null;
                }
            }

            private static void SendRemoveWire(ItemInventoryData invData)
            {
                if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.RemoveWire,
                            Vector3i.zero,
                            invData.holdingEntity.entityId),
                        false, -1, -1, -1, null, 192, false);
                }
                else
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.RemoveWire,
                            Vector3i.zero,
                            invData.holdingEntity.entityId), false);
                }
            }
        }

        // Patches PowerItem.HandlePowerReceived to notify our TileEntityPoweredWorkstation
        // when its power state changes. Normally this is done via PowerItem.TileEntity.SetModified()
        // but we can't set TileEntity since it expects TileEntityPowered.
        [HarmonyPatch(typeof(PowerItem))]
        [HarmonyPatch("HandlePowerReceived")]
        public class PowerItem_HandlePowerReceived
        {
            public static void Postfix(PowerItem __instance)
            {
                // Only act if this PowerItem has no TileEntity assigned
                // (which is our case since we can't call AddTileEntity).
                if (__instance.TileEntity != null) { return; }
                if (__instance.Position == Vector3i.zero) { return; }

                // Check if there is a TileEntityPoweredWorkstation at this PowerItem's position.
                World world = GameManager.Instance?.World;
                if (world == null) { return; }

                Chunk chunk = world.GetChunkFromWorldPos(
                    __instance.Position.x,
                    __instance.Position.y,
                    __instance.Position.z) as Chunk;
                if (chunk == null) { return; }

                TileEntityPoweredWorkstation workstation =
                    world.GetTileEntity(chunk.ClrIdx, __instance.Position)
                    as TileEntityPoweredWorkstation;
                if (workstation == null) { return; }

                // Mirror what TileEntity.SetModified() does — notify the game
                // that this tile entity's state has changed and needs saving/syncing.
                workstation.SetModified();

                // When power is lost, stop the workstation.
                if (!__instance.IsPowered && workstation.isBurning)
                {
                    workstation.isBurning = false;
                    workstation.setModified();
                }

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism PowerItem_HandlePowerReceived:" +
                        " IsPowered=" + __instance.IsPowered +
                        " IsServer=" + SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer);
                }
            }
        }

        [HarmonyPatch(typeof(NetPackageWireActions))]
        [HarmonyPatch("ProcessPackage")]
        public class NetPackageWireActions_ProcessPackage
        {
            public static void Prefix(
                NetPackageWireActions __instance,
                World _world,
                Vector3i ___tileEntityPosition,
                NetPackageWireActions.WireActions ___currentOperation,
                List<Vector3i> ___wireChildren)
            {
                if (_world == null) { return; }
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }
                if (___currentOperation != NetPackageWireActions.WireActions.SetParent) { return; }

                // Check if the tile entity at tileEntityPosition is our workstation.
                Chunk chunk = _world.GetChunkFromWorldPos(
                    ___tileEntityPosition.x,
                    ___tileEntityPosition.y,
                    ___tileEntityPosition.z) as Chunk;
                if (chunk == null) { return; }

                TileEntityPoweredWorkstation workstation =
                    _world.GetTileEntity(chunk.ClrIdx, ___tileEntityPosition)
                    as TileEntityPoweredWorkstation;
                if (workstation == null) { return; }

                // Our workstation is at tileEntityPosition — handle SetParent ourselves
                // before GetPoweredTileEntity casts it to TileEntityPowered and fails.
                if (___wireChildren == null || ___wireChildren.Count == 0) { return; }

                Vector3i parentPos = ___wireChildren[0];
                Chunk parentChunk = _world.GetChunkFromWorldPos(
                    parentPos.x, parentPos.y, parentPos.z) as Chunk;
                if (parentChunk == null) { return; }

                TileEntityPowered parentTE =
                    _world.GetTileEntity(parentChunk.ClrIdx, parentPos) as TileEntityPowered;
                if (parentTE == null) { return; }

                PowerItem parentPowerItem = parentTE.GetPowerItem();
                if (parentPowerItem == null) { return; }

                PowerItem oldParent = workstation.PowerItem.Parent;
                PowerManager.Instance.SetParent(workstation.PowerItem, parentPowerItem);

                if (workstation.PowerItem.Parent != null)
                {
                    workstation.UpdateParentPosition(workstation.PowerItem.Parent.Position);
                }

                if (oldParent != null && oldParent.TileEntity != null)
                {
                    oldParent.TileEntity.CreateWireDataFromPowerItem();
                    oldParent.TileEntity.SendWireData();
                    oldParent.TileEntity.RemoveWires();
                    oldParent.TileEntity.DrawWires();
                }

                parentTE.CreateWireDataFromPowerItem();
                parentTE.SendWireData();
                parentTE.RemoveWires();
                parentTE.DrawWires();

                workstation.setModified();
            }
        }

        // Shows correct wattage color and value when hovering over our powered workstation
        // with the wire tool. Vanilla code casts to TileEntityPowered which fails for us.
        [HarmonyPatch(typeof(ItemActionConnectPower))]
        [HarmonyPatch("OnHoldingUpdate")]
        public class ItemActionConnectPower_OnHoldingUpdate_Postfix
        {
            public static void Postfix(ItemActionData _actionData)
            {
                ItemActionConnectPower.ConnectPowerData connectPowerData =
                    _actionData as ItemActionConnectPower.ConnectPowerData;
                if (connectPowerData == null) { return; }
                if (connectPowerData.playerUI == null) { return; }
                if (!_actionData.invData.hitInfo.bHitValid) { return; }

                Vector3i blockPos = _actionData.invData.hitInfo.hit.blockPos;
                int num = (int)(Constants.cDigAndBuildDistance * Constants.cDigAndBuildDistance);
                if (_actionData.invData.hitInfo.hit.distanceSq > (float)num) { return; }

                ChunkCluster chunkCluster =
                    _actionData.invData.world.ChunkClusters[_actionData.invData.hitInfo.hit.clrIdx];
                if (chunkCluster == null) { return; }

                Chunk chunk = (Chunk)chunkCluster.GetChunkSync(
                    World.toChunkXZ(blockPos.x), blockPos.y, World.toChunkXZ(blockPos.z));
                if (chunk == null) { return; }

                TileEntityPoweredWorkstation workstation =
                    chunk.GetTileEntity(World.toBlock(blockPos)) as TileEntityPoweredWorkstation;
                if (workstation == null) { return; }

                // Override the wattage display with our workstation's correct values.
                Color color = workstation.IsPowered ? Color.yellow : Color.grey;
                int powerUsed = workstation.PowerUsed;
                connectPowerData.playerUI.nguiWindowManager.SetLabel(
                    EnumNGUIWindow.PowerInfo,
                    string.Format("{0}W", powerUsed),
                    new Color?(color),
                    true);
            }
        }

        [HarmonyPatch(typeof(TileEntityPowered))]
        [HarmonyPatch("DrawWires")]
        public class TileEntityPowered_DrawWires
        {
            public static bool Prefix(TileEntityPowered __instance)
            {
                // Only intercept if this powered entity has our workstation as a child.
                bool hasOurWorkstation = false;
                World world = GameManager.Instance?.World;
                if (world == null) { return true; }

                for (int i = 0; i < __instance.wireDataList.Count; i++)
                {
                    Vector3i pos = __instance.wireDataList[i];
                    Chunk chunk = world.GetChunkFromWorldPos(pos.x, pos.y, pos.z) as Chunk;
                    if (chunk == null) { continue; }

                    if (world.GetTileEntity(chunk.ClrIdx, pos) is TileEntityPoweredWorkstation)
                    {
                        hasOurWorkstation = true;
                        break;
                    }
                }

                // If no workstation children, let vanilla handle it normally.
                if (!hasOurWorkstation) { return true; }

                // Has our workstation — run our own DrawWires that handles both types.
                if (__instance.BlockTransform == null)
                {
                    __instance.wiresDirty = true;
                    return false;
                }

                WireManager wm = WireManager.Instance;
                bool showPulse = wm.ShowPulse;
                bool wiresShowing = wm.WiresShowing;

                if (__instance.wireDataList.Count > 0 && showPulse)
                {
                    showPulse = world.CanPlaceBlockAt(
                        __instance.ToWorldPos(),
                        world.gameManager.GetPersistentLocalPlayer(),
                        false);
                }

                // First pass — verify all children have transforms.
                for (int i = 0; i < __instance.wireDataList.Count; i++)
                {
                    Vector3i pos = __instance.wireDataList[i];
                    Chunk chunk = world.GetChunkFromWorldPos(pos.x, pos.y, pos.z) as Chunk;
                    if (chunk == null) { __instance.wiresDirty = true; return false; }

                    TileEntity te = world.GetTileEntity(chunk.ClrIdx, pos);
                    bool hasTransform = false;

                    if (te is TileEntityPoweredWorkstation ws && ws.BlockTransform != null)
                        hasTransform = true;
                    else if (te is TileEntityPowered tp && tp.BlockTransform != null)
                        hasTransform = true;

                    if (!hasTransform) { __instance.wiresDirty = true; return false; }
                }

                // Second pass — draw wires.
                int num = 0;
                for (int j = 0; j < __instance.wireDataList.Count; j++)
                {
                    Vector3i pos2 = __instance.wireDataList[j];
                    Chunk chunk2 = world.GetChunkFromWorldPos(pos2.x, pos2.y, pos2.z) as Chunk;
                    if (chunk2 == null) { continue; }

                    TileEntity te2 = world.GetTileEntity(chunk2.ClrIdx, pos2);
                    Vector3 endOffset = new Vector3(0.5f, 0.5f, 0.5f);

                    if (te2 is TileEntityPoweredWorkstation ws2)
                        endOffset += ws2.WireOffset;
                    else if (te2 is TileEntityPowered tp2)
                        endOffset += tp2.WireOffset;

                    if (num >= __instance.currentWireNodes.Count)
                        __instance.currentWireNodes.Add(WireManager.Instance.GetWireNodeFromPool());

                    __instance.currentWireNodes[num].SetStartPosition(
                        __instance.BlockTransform.position + Origin.position);
                    __instance.currentWireNodes[num].SetStartPositionOffset(__instance.WireOffset);
                    __instance.currentWireNodes[num].SetEndPosition(pos2.ToVector3());
                    __instance.currentWireNodes[num].SetEndPositionOffset(endOffset);
                    __instance.currentWireNodes[num].BuildMesh();
                    __instance.currentWireNodes[num].TogglePulse(showPulse);
                    __instance.currentWireNodes[num].SetVisible(wiresShowing);
                    num++;
                }

                // Clean up excess wire nodes.
                for (int k = num; k < __instance.currentWireNodes.Count; k++)
                {
                    WireManager.Instance.ReturnToPool(__instance.currentWireNodes[num]);
                    __instance.currentWireNodes.Remove(__instance.currentWireNodes[num]);
                }

                __instance.wiresDirty = false;
                return false; // skip original
            }
        }

        // Patches ItemActionConnectPower.DisconnectWire to play the wire cutoff sound
        // when disconnecting from our workstation. Vanilla casts to TileEntityPowered
        // which fails for us, so the sound is never played.
        [HarmonyPatch(typeof(ItemActionConnectPower))]
        [HarmonyPatch("DisconnectWire")]
        public class ItemActionConnectPower_DisconnectWire
        {
            // Store workstation reference between Prefix and Postfix.
            private static TileEntityPoweredWorkstation pendingRedraw = null;

            public static void Prefix(
                ItemActionConnectPower.ConnectPowerData _actionData)
            {
                pendingRedraw = null;

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism DisconnectWire Prefix fired: HasStartPoint=" +
                        _actionData.HasStartPoint + " startPoint=" + _actionData.startPoint);
                }

                // Check startPoint validity instead of HasStartPoint,
                // since HasStartPoint may already be cleared by the time we run.
                if (_actionData.startPoint == Vector3i.zero) { return; }

                World world = _actionData.invData.world;
                if (world == null) { return; }

                Chunk chunk = world.GetChunkFromWorldPos(
                    _actionData.startPoint.x,
                    _actionData.startPoint.y,
                    _actionData.startPoint.z) as Chunk;
                if (chunk == null) { return; }

                if (NicElectricityRealism.DebugLog)
                {
                    TileEntity te = chunk.GetTileEntity(World.toBlock(_actionData.startPoint));
                    Debug.Log("ElectricityRealism DisconnectWire: te type=" +
                        (te == null ? "NULL" : te.GetType().Name));
                }

                TileEntityPoweredWorkstation workstation =
                    chunk.GetTileEntity(World.toBlock(_actionData.startPoint))
                    as TileEntityPoweredWorkstation;

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism DisconnectWire: workstation cast result=" +
                        (workstation == null ? "NULL" : "OK"));
                }

                if (workstation == null) { return; }

                // Play the wire break sound.
                Manager.BroadcastPlay(
                    workstation.ToWorldPos().ToVector3(),
                    workstation.IsPowered ? "wire_live_break" : "wire_dead_break",
                    0f);

                // Store for Postfix to redraw after power graph is updated.
                pendingRedraw = workstation;

                // Send RemoveWire packet.
                if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism DisconnectWire: PowerItem.Parent=" +
                            (workstation.PowerItem?.Parent == null ? "NULL" : "OK"));

                    if (workstation.PowerItem != null && workstation.PowerItem.Parent == null)
                    {
                        workstation.RemoveParentWithWiringTool(_actionData.invData.holdingEntity.entityId);
                    }

                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.RemoveWire,
                            Vector3i.zero,
                            _actionData.invData.holdingEntity.entityId),
                        false, -1, -1, -1, null, 192, false);
                }
                else
                {
                    SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                        NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(
                            NetPackageWireToolActions.WireActions.RemoveWire,
                            Vector3i.zero,
                            _actionData.invData.holdingEntity.entityId), false);
                }
            }

            public static void Postfix(
                ItemActionConnectPower.ConnectPowerData _actionData)
            {
                // After vanilla has updated the power graph, redraw our workstation's wires.
                if (pendingRedraw == null) { return; }
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism DisconnectWire Postfix: redrawing workstation wires");
                }

                pendingRedraw.CreateWireDataFromPowerItem();
                pendingRedraw.SendWireData();
                pendingRedraw.RemoveWires();
                pendingRedraw.DrawWires();
                pendingRedraw = null;
            }
        }

        // Patches PowerManager.RemoveChild to update our workstation's wire data
        // when a child block is disconnected from it. Vanilla updates TileEntity.DrawWires
        // via TileEntity reference, but ours is null so it's skipped.
        [HarmonyPatch(typeof(PowerManager))]
        [HarmonyPatch("RemoveChild")]
        public class PowerManager_RemoveChild
        {
            private static TileEntityPoweredWorkstation pendingWireUpdate = null;

            public static void Prefix(PowerItem child)
            {
                pendingWireUpdate = null;
                if (child.Parent == null) { return; }
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }

                World world = GameManager.Instance?.World;
                if (world == null) { return; }

                Chunk chunk = world.GetChunkFromWorldPos(
                    child.Parent.Position.x,
                    child.Parent.Position.y,
                    child.Parent.Position.z) as Chunk;
                if (chunk == null) { return; }

                TileEntityPoweredWorkstation workstation =
                    world.GetTileEntity(chunk.ClrIdx, child.Parent.Position)
                    as TileEntityPoweredWorkstation;
                if (workstation == null) { return; }

                pendingWireUpdate = workstation;
            }

            public static void Postfix()
            {
                if (pendingWireUpdate == null) { return; }
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }

                if (NicElectricityRealism.DebugLog)
                    Debug.Log("ElectricityRealism PowerManager_RemoveChild Postfix: redrawing workstation");

                pendingWireUpdate.CreateWireDataFromPowerItem();
                pendingWireUpdate.SendWireData();
                pendingWireUpdate.RemoveWires();
                pendingWireUpdate.DrawWires();
                pendingWireUpdate = null;
            }
        }

        [HarmonyPatch(typeof(GameManager))]
        [HarmonyPatch("SaveAndCleanupWorld")]
        public class GameManager_SaveAndCleanupWorld
        {
            public static void Prefix()
            {
                if (PowerManager.HasInstance &&
                    SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism: SaveAndCleanupWorld — forcing PowerManager save");
                    PowerManager.Instance.SavePowerManager();
                }
            }
        }

        [HarmonyPatch(typeof(PowerManager))]
        [HarmonyPatch("LoadPowerManager")]
        public class PowerManager_LoadPowerManager
        {
            public static void Postfix()
            {
                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism PowerManager_LoadPowerManager: " +
                        "PowerItemDictionary.Count=" + PowerManager.Instance.PowerItemDictionary.Count);
                    foreach (var kvp in PowerManager.Instance.PowerItemDictionary)
                    {
                        Debug.Log("ElectricityRealism PowerManager entry: pos=" + kvp.Key +
                            " type=" + kvp.Value.PowerItemType);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Chunk), "read", new Type[] {
            typeof(PooledBinaryReader), typeof(uint), typeof(bool) })]
        public class Chunk_read
        {
            public static void Postfix(Chunk __instance, bool _bNetworkRead)
            {
                // Only process persistence reads, not network reads.
                if (_bNetworkRead) { return; }
                if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }

                List<Vector3i> toReplace = new List<Vector3i>();
                foreach (var kvp in __instance.tileEntities.dict)
                {
                    if (kvp.Value is TileEntityPoweredWorkstation) { continue; }
                    if (!(kvp.Value is TileEntityWorkstation)) { continue; }
                    BlockValue blockVal = __instance.GetBlock(kvp.Key);
                    if (blockVal.Block is BlockPoweredWorkstation)
                        toReplace.Add(kvp.Key);
                }

                foreach (Vector3i pos in toReplace)
                {
                    TileEntityWorkstation ws =
                        __instance.tileEntities.dict[pos] as TileEntityWorkstation;

                    TileEntityPoweredWorkstation powered =
                        new TileEntityPoweredWorkstation(__instance);
                    powered.localChunkPos = pos;
                    powered.fuel = ws.fuel;
                    powered.tools = ws.tools;
                    powered.input = ws.input;
                    powered.output = ws.output;
                    powered.queue = ws.queue;
                    powered.lastTickTime = ws.lastTickTime;
                    powered.currentBurnTimeLeft = ws.currentBurnTimeLeft;
                    powered.currentMeltTimesLeft = ws.currentMeltTimesLeft;
                    powered.lastInput = ws.lastInput;
                    powered.isBurning = ws.isBurning;
                    powered.isPlayerPlaced = ws.isPlayerPlaced;
                    powered.isModuleUsed = ws.isModuleUsed;
                    powered.materialNames = ws.materialNames;
                    powered.CraftCompleteList = ws.CraftCompleteList;
                    powered.entityId = ws.entityId;

                    // Directly replace in the dictionary — bypasses the Set overwrite in Chunk.read.
                    __instance.tileEntities.dict[pos] = powered;
                    // Also update the list.
                    int idx = __instance.tileEntities.list.IndexOf(ws);
                    if (idx >= 0) __instance.tileEntities.list[idx] = powered;

                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism Chunk_read: replaced TileEntityWorkstation " +
                            "with TileEntityPoweredWorkstation at " + powered.ToWorldPos());

                    powered.InitializePowerData();
                    powered.CheckForNewWires();
                }
            }
        }

        [HarmonyPatch(typeof(ItemActionConnectPower))]
        [HarmonyPatch("ExecuteAction")]
        public class ItemActionConnectPower_ExecuteAction
        {
            public static void Postfix(ItemActionData _actionData, bool _bReleased)
            {
                if (!_bReleased) { return; }

                ItemActionConnectPower.ConnectPowerData connectPowerData =
                    _actionData as ItemActionConnectPower.ConnectPowerData;
                if (connectPowerData == null) { return; }
                if (!connectPowerData.HasStartPoint) { return; }

                // If we already have a start point, skip the animation delay
                // so OnHoldingUpdate processes the second click immediately.
                _actionData.lastUseTime = 0f;
            }
        }

        [HarmonyPatch(typeof(Block))]
        [HarmonyPatch("OnBlockActivated", new Type[] {
            typeof(string), typeof(WorldBase), typeof(int),
            typeof(Vector3i), typeof(BlockValue), typeof(EntityPlayerLocal) })]
        public class Block_OnBlockActivated
        {
            public static bool Prefix(BlockValue _blockValue, EntityPlayerLocal _player)
            {
                if (_player == null) { return true; }

                ItemActionConnectPower.ConnectPowerData connectPowerData =
                    _player.inventory.holdingItemData?.actionData[1]
                    as ItemActionConnectPower.ConnectPowerData;

                if (connectPowerData == null) { return true; }
                if (!connectPowerData.HasStartPoint) { return true; }

                // Suppress activation for any powered block while wire tool has a start point.
                // This prevents lights/switches toggling and briefly destroying their tile entity
                // on the same frame the wire connection is being completed.
                if (!(_blockValue.Block is BlockPowered)) { return true; }

                return false;
            }
        }

        [HarmonyPatch(typeof(TileEntityWorkstation))]
        [HarmonyPatch("SetDataFromNet")]
        public class TileEntityWorkstation_SetDataFromNet
        {
            public static void Postfix(TileEntityWorkstation __instance)
            {
                TileEntityPoweredWorkstation powered = __instance as TileEntityPoweredWorkstation;
                if (powered == null) { return; }

                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism SetDataFromNet:" +
                        " IsCrafting=" + powered.IsCrafting +
                        " IsPowered=" + powered.IsPowered);
                }

                BlockWorkstation bws =
                    GameManager.Instance?.World?.GetBlock(powered.ToWorldPos()).Block as BlockWorkstation;
                if (bws == null) { return; }

                bws.UpdateVisible(powered);
            }
        }

        [HarmonyPatch(typeof(TileEntityWorkstation))]
        [HarmonyPatch("UpdateVisible")]
        public class TileEntityWorkstation_UpdateVisible
        {
            private static Dictionary<int, Color> originalEmissionColors = new Dictionary<int, Color>();

            public static bool Prefix(TileEntityWorkstation __instance)
            {
                if (!(__instance is TileEntityPoweredWorkstation powered)) { return true; }

                bool isCrafting = powered.IsCrafting;
                if (isCrafting != powered.visibleCrafting)
                {
                    powered.visibleCrafting = isCrafting;
                    powered.visibleChanged = true;
                }
                //bool isWorking = powered.isBurning || powered.hasRecipeInQueue();
                bool isWorking = (powered.isBurning || powered.hasRecipeInQueue()) && powered.IsReceivingPower;
                if (isWorking != powered.visibleWorking)
                {
                    powered.visibleWorking = isWorking;
                    powered.visibleChanged = true;
                }

                if (powered.visibleChanged)
                {
                    powered.visibleChanged = false;

                    // Update power draw based on crafting state.
                    if (powered.PowerItem != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    {
                        ushort targetPower = isWorking ? (ushort)powered.RequiredPower : (ushort)1;
                        if (powered.PowerItem.RequiredPower != targetPower)
                        {
                            powered.PowerItem.RequiredPower = targetPower;
                            powered.PowerItem.SendHasLocalChangesToRoot();
                        }
                    }

                    // Drive the block meta change that triggers particles, glow and sound.
                    World world = GameManager.Instance?.World;
                    if (world != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    {
                        BlockValue blockValue = world.GetBlock(powered.ToWorldPos());
                        if (NicElectricityRealism.DebugLog)
                        {
                            Debug.Log("ElectricityRealism: UpdateLightState before meta=" + blockValue.meta +
                                " isBurning=" + powered.isBurning +
                                " CanOperate=" + powered.CanOperate(GameTimer.Instance.ticks));
                        }
                        powered.UpdateLightState(world, blockValue);
                        if (NicElectricityRealism.DebugLog)
                        {
                            bool hasParticle = GameManager.Instance?.World != null &&
                                GameManager.Instance.HasBlockParticleEffect(powered.ToWorldPos());
                            Debug.Log("ElectricityRealism: HasBlockParticleEffect=" + hasParticle +
                                " meta=" + (GameManager.Instance?.World?.GetBlock(powered.ToWorldPos()).meta ?? -1));
                        }
                        blockValue = world.GetBlock(powered.ToWorldPos());
                        if (NicElectricityRealism.DebugLog)
                        {
                            Debug.Log("ElectricityRealism: UpdateLightState after meta=" + blockValue.meta);
                        }
                    }
                    
                    BlockEntityData blockEntity = powered.GetChunk()?.GetBlockEntity(powered.ToWorldPos());
                    if (blockEntity == null) { return false; }
                    Transform transform = blockEntity.transform;
                    if (transform == null) { return false; }

                    if (NicElectricityRealism.DebugLog && blockEntity != null && transform != null)
                    {
                        string children = "";
                        for (int c = 0; c < transform.childCount; c++)
                            children += transform.GetChild(c).name + ", ";
                        Debug.Log("ElectricityRealism forge transforms: " + children);
                    }

                    if (NicElectricityRealism.DebugLog)
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        DumpTransforms(transform, sb, 0);
                        Debug.Log("ElectricityRealism forge all transforms:\n" + sb.ToString());
                    }

                    // Update tool transforms.
                    string toolNames = "1,2,3";
                    Block.list[GameManager.Instance.World.GetBlock(powered.ToWorldPos()).type]
                        .Properties.ParseString("Workstation.ToolNames", ref toolNames);
                    string[] toolTransformNames = toolNames.Split(',');
                    ItemStack[] tools = powered.Tools;
                    int num = Utils.FastMin(tools.Length, toolTransformNames.Length);
                    for (int i = 0; i < num; i++)
                    {
                        Transform t = transform.Find(toolTransformNames[i]);
                        if (t != null)
                            t.gameObject.SetActive(!tools[i].IsEmpty());
                    }

                    // Toggle craft animation.
                    Transform craftTransform = transform.Find("craft");

                    if (NicElectricityRealism.DebugLog)
                    {
                        Debug.Log("ElectricityRealism: setting craft active=" +
                            (powered.IsCrafting && powered.IsPowered) +
                            " craftTransform=" + (craftTransform == null ? "NULL" : "OK"));
                    }
                    
                    if (craftTransform != null)
                        craftTransform.gameObject.SetActive(powered.IsCrafting && powered.IsPowered);

                    // Toggle emissive glow on forge mesh — controls the coal/ember appearance.
                    Transform lodTransform = transform.Find("forgeWorkstation_LOD0");

                    if (NicElectricityRealism.DebugLog)
                    {
                        Debug.Log("ElectricityRealism emission toggle: isWorking=" + isWorking +
                            " isBurning=" + powered.isBurning +
                            " IsCrafting=" + powered.IsCrafting +
                            " lodTransform=" + (lodTransform == null ? "NULL" : "OK"));
                    }

                    if (lodTransform != null)
                    {
                        Renderer renderer = lodTransform.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            if (NicElectricityRealism.DebugLog)
                            {
                                Debug.Log("ElectricityRealism: renderer materials count=" + renderer.materials.Length);
                            }

                            // Cache original emission color before first modification.
                            if (!originalEmissionColors.ContainsKey(renderer.GetInstanceID()))
                            {
                                Material mat = renderer.material;
                                if (mat.HasProperty("_EmissionColor"))
                                    originalEmissionColors[renderer.GetInstanceID()] = mat.GetColor("_EmissionColor");
                            }
                            
                            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                            renderer.GetPropertyBlock(mpb);
                            if (isWorking)
                                mpb.SetColor("_EmissionColor", originalEmissionColors.ContainsKey(renderer.GetInstanceID())
                                    ? originalEmissionColors[renderer.GetInstanceID()]
                                    : new Color(0.3f, 0.15f, 0.05f));
                            else
                                mpb.SetColor("_EmissionColor", Color.black);
                            renderer.SetPropertyBlock(mpb);
                        }
                    }

                    if (NicElectricityRealism.DebugLog)
                    {
                        Debug.Log("ElectricityRealism TileEntityWorkstation_UpdateVisible:" +
                            " IsCrafting=" + powered.IsCrafting +
                            " IsPowered=" + powered.IsPowered +
                            " isBurning=" + powered.isBurning +
                            " pos=" + powered.ToWorldPos());
                    }
                }
                return false; // skip vanilla since we handled it
            }
            private static void DumpTransforms(Transform t, System.Text.StringBuilder sb, int depth)
            {
                sb.AppendLine(new string('-', depth) + t.name);
                for (int i = 0; i < t.childCount; i++)
                    DumpTransforms(t.GetChild(i), sb, depth + 1);
            }
        }
    }
}