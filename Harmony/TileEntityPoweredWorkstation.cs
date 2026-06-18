using Audio;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElectricityRealism
{
    // Extends TileEntityWorkstation (so OpenTileEntityUi recognises it and opens
    // the crafting UI correctly) while also implementing IPowered (so the wire
    // tool and power manager treat it like any other powered consumer).
    // The power tracking fields and methods are ported directly from TileEntityPowered.
    public class TileEntityPoweredWorkstation : TileEntityWorkstation, IPowered
    {
        private const string ModVersion = "v0.29";

        // ── Construction ─────────────────────────────────────────────────────────

        public TileEntityPoweredWorkstation(Chunk _chunk) : base(_chunk)
        {
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism TileEntityPoweredWorkstation " + ModVersion);
            }
        }

        // ── Power fields (ported from TileEntityPowered) ─────────────────────────

        public PowerItem.PowerItemTypes PowerItemType = PowerItem.PowerItemTypes.Consumer;
        public PowerItem PowerItem;
        public Vector3 WireOffset = Vector3.zero;
        public float CenteredPitch;
        public float CenteredYaw;
        private int requiredPower;
#pragma warning disable CS0169, CS0414, CS0649
        private bool isPowered;
        private bool needBlockData;
        private bool wiresDirty;
        private bool activateDirty;
#pragma warning restore CS0169, CS0414, CS0649

        private Vector3i parentPosition = new Vector3i(-9999, -9999, -9999);
        private List<IWireNode> currentWireNodes = new List<IWireNode>();
        private List<Vector3i> wireDataList = new List<Vector3i>();
        private Transform blockTransform;
        private bool hasLoggedTransformPosition = false;
        //private RecipeQueueItem[] pausedQueue = null;
        private bool wasReceivingPower = true;
        private bool wasBurningBeforePowerCut = false;

        // ── IPowered implementation ──────────────────────────────────────────────

        public bool IsPowered
        {
            get
            {
                // PowerItem.IsPowered is replicated to clients by the vanilla power
                // system (lights/generators rely on this), so it's safe to read
                // unconditionally here. The previous client-only branch returned the
                // private `isPowered` field, which was never assigned anywhere in this
                // class and was therefore always false on clients — causing the
                // "craft" transform (animation/sound) to be permanently disabled
                // client-side for any workstation whose visible "running" cue isn't
                // also masked by a server-meta-driven particle effect (i.e. the
                // cement mixer, which has no ParticleName).
                return this.PowerItem != null && this.PowerItem.IsPowered;
            }
        }

        // Convenience property used by BlockPoweredWorkstation and patches.
        public bool IsReceivingPower { get { return this.IsPowered; } }

        public int RequiredPower
        {
            get { return this.requiredPower; }
            set { this.requiredPower = value; }
        }


        // Change PowerUsed to reflect actual current draw:
        public int PowerUsed
        {
            get
            {
                return (this.PowerItem != null) ? (int)this.PowerItem.RequiredPower : this.requiredPower;
            }
        }

        public int ChildCount { get { return this.wireDataList.Count; } }

        public Vector3 GetWireOffset() { return this.WireOffset; }

        public int GetRequiredPower() { return this.RequiredPower; }

        public PowerItem GetPowerItem() { return this.PowerItem; }

        public Vector3i GetParent() { return this.parentPosition; }

        public bool HasParent()
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                return this.PowerItem != null && this.PowerItem.Parent != null;
            return this.parentPosition.y != -9999;
        }

        public bool CanHaveParent(IPowered powered) { return true; }

        public void MarkChanged() { this.setModified(); }

        public void MarkWireDirty() { this.wiresDirty = true; }

        public Transform BlockTransform
        {
            get { return this.blockTransform; }
            set
            {
                this.blockTransform = value;
                BlockValue block = GameManager.Instance.World.GetBlock(base.ToWorldPos());
                if (this.blockTransform != null)
                {
                    Transform t = this.blockTransform.Find("WireOffset");
                    if (t != null)
                    {
                        this.WireOffset = block.Block.shape.GetRotation(block) * t.localPosition;
                        return;
                    }
                }
                if (block.Block.Properties.Values.ContainsKey("WireOffset"))
                {
                    this.WireOffset = block.Block.shape.GetRotation(block) *
                        StringParsers.ParseVector3(block.Block.Properties.Values["WireOffset"], 0, -1);
                }
            }
        }

        // ── Power initialisation ─────────────────────────────────────────────────

        public void InitializePowerData()
        {
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism InitializePowerData called at " + base.ToWorldPos());
            }

            if (GameManager.Instance == null) { return; }

            ushort blockID = (ushort)GameManager.Instance.World.GetBlock(base.ToWorldPos()).type;

            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism InitializePowerData: blockID=" + blockID +
                    " pos=" + base.ToWorldPos());
            }

            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                this.PowerItem = PowerManager.Instance.GetPowerItemByWorldPos(base.ToWorldPos());
                if (NicElectricityRealism.DebugLog)
                {
                    Debug.Log("ElectricityRealism: GetPowerItemByWorldPos at " + base.ToWorldPos() +
                        " = " + (this.PowerItem == null ? "NULL" : "FOUND"));
                }
                if (this.PowerItem != null)
                {
                    // Use saved blockID — GetBlock may return 0 during OnReadComplete.
                    blockID = this.PowerItem.BlockID;
                }

                if (this.PowerItem == null)
                {
                    if (NicElectricityRealism.DebugLog)
                    {
                        Debug.Log("ElectricityRealism: PowerItem created new");
                    }
                    this.PowerItem = PowerItem.CreateItem(this.PowerItemType);
                    this.PowerItem.Position = base.ToWorldPos();
                    this.PowerItem.BlockID = blockID;
                    this.PowerItem.SetValuesFromBlock();
                    this.PowerItem.RequiredPower = 1;
                    PowerManager.Instance.AddPowerNode(this.PowerItem, null);

                    if (NicElectricityRealism.DebugLog)
                    {
                        Debug.Log("ElectricityRealism: AddPowerNode done, Circuits.Count=" +
                            PowerManager.Instance.Circuits.Count);
                        Debug.Log("PowerItemDictionary.Count=" +
                           PowerManager.Instance.PowerItemDictionary.Count +
                           " containsOurPos=" + PowerManager.Instance.PowerItemDictionary.ContainsKey(base.ToWorldPos()));
                    }

                    // Manually replicate what AddTileEntity does, since we can't pass 'this'
                    // as TileEntityPowered (we extend TileEntityWorkstation instead).
                    // AddTileEntity just stores the tile entity reference and calls these two methods.
                    if (this.PowerItem.TileEntity == null)
                    {
                        this.CreateWireDataFromPowerItem();
                    }
                    this.MarkWireDirty();
                }
                else
                {
                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism: PowerItem found existing, Children=" +
                        this.PowerItem.Children.Count + " Parent=" +
                        (this.PowerItem.Parent == null ? "NULL" : "OK"));
                    blockID = this.PowerItem.BlockID;

                    // Restore parentPosition from the saved PowerItem.
                    if (this.PowerItem.Parent != null)
                    {
                        this.parentPosition = this.PowerItem.Parent.Position;
                    }

                    // Rebuild wire data from the saved power graph.
                    this.CreateWireDataFromPowerItem();
                    this.MarkWireDirty();
                }
                this.setModified();
                this.activateDirty = true;
            }

            // Read RequiredPower from blocks.xml property.
            if (Block.list[(int)blockID].Properties.Values.ContainsKey("RequiredPower"))
            {
                this.RequiredPower = Convert.ToInt32(
                    Block.list[(int)blockID].Properties.Values["RequiredPower"]);
            }
            else
            {
                this.RequiredPower = 5;
            }

            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                this.DrawWires();
            }
        }

        public void CheckForNewWires()
        {
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism CheckForNewWires: wireDataList.Count=" +
                    this.wireDataList.Count + " PowerItem.Children=" +
                    (this.PowerItem?.Children.Count ?? -1));
            }
            if (GameManager.Instance == null) { return; }
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer) { return; }

            for (int i = 0; i < this.wireDataList.Count; i++)
            {
                Vector3 pos = this.wireDataList[i].ToVector3();
                if (this.PowerItem.GetChild(pos) == null)
                {
                    PowerItem item = PowerManager.Instance.GetPowerItemByWorldPos(this.wireDataList[i]);
                    PowerManager.Instance.SetParent(item, this.PowerItem);
                }
            }
        }

        public override void OnReadComplete()
        {
            base.OnReadComplete();
            this.InitializePowerData();
            this.CheckForNewWires();
        }

        // ── Wire management (ported from TileEntityPowered) ──────────────────────

        public void AddWireData(Vector3i child)
        {
            this.wireDataList.Add(child);
            this.SendWireData();
        }

        public void SendWireData()
        {
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism SendWireData: wireDataList.Count=" + this.wireDataList.Count);
            }

            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(
                    NetPackageManager.GetPackage<NetPackageWireActions>().Setup(
                        NetPackageWireActions.WireActions.SendWires,
                        base.ToWorldPos(), this.wireDataList, -1),
                    false, -1, -1, -1, null, 192, false);
            }
            else
            {
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                    NetPackageManager.GetPackage<NetPackageWireActions>().Setup(
                        NetPackageWireActions.WireActions.SendWires,
                        base.ToWorldPos(), this.wireDataList, -1), false);
            }
        }

        public void CreateWireDataFromPowerItem()
        {
            this.wireDataList.Clear();
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                for (int i = 0; i < this.PowerItem.Children.Count; i++)
                {
                    this.wireDataList.Add(this.PowerItem.Children[i].Position);
                }
            }
        }

        public void RemoveWires()
        {
            for (int i = 0; i < this.currentWireNodes.Count; i++)
            {
                WireManager.Instance.ReturnToPool(this.currentWireNodes[i]);
            }
            this.currentWireNodes.Clear();
        }

        public void DrawWires()
        {
            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism DrawWires called: blockTransform=" +
                    (this.blockTransform == null ? "NULL" : "OK") +
                    " wireDataList.Count=" + this.wireDataList.Count);
            }

            if (this.blockTransform == null)
            {
                this.wiresDirty = true;
                return;
            }

            WireManager instance = WireManager.Instance;
            bool showPulse = instance.ShowPulse;
            bool wiresShowing = instance.WiresShowing;

            if (this.wireDataList.Count > 0)
            {
                World world = GameManager.Instance.World;
                if (showPulse)
                {
                    showPulse = world.CanPlaceBlockAt(
                        base.ToWorldPos(),
                        world.gameManager.GetPersistentLocalPlayer(),
                        false);
                }
            }

            int num = 0;
            for (int i = 0; i < this.wireDataList.Count; i++)
            {
                Vector3i vector3i = this.wireDataList[i];
                Chunk chunk = GameManager.Instance.World.GetChunkFromWorldPos(vector3i) as Chunk;
                if (chunk != null)
                {
                    TileEntity te = GameManager.Instance.World.GetTileEntity(chunk.ClrIdx, vector3i);
                    bool hasValidTransform = false;
                    if (te is TileEntityPoweredWorkstation wsChild && wsChild.BlockTransform != null)
                        hasValidTransform = true;
                    else if (te is TileEntityPowered tep && tep.BlockTransform != null)
                        hasValidTransform = true;
                    if (!hasValidTransform) { this.wiresDirty = true; return; }
                }
            }

            for (int j = 0; j < this.wireDataList.Count; j++)
            {
                Vector3i vector3i2 = this.wireDataList[j];
                Chunk chunk2 = GameManager.Instance.World.GetChunkFromWorldPos(vector3i2) as Chunk;
                if (chunk2 != null)
                {
                    TileEntity te2 = GameManager.Instance.World.GetTileEntity(chunk2.ClrIdx, vector3i2);
                    Vector3 endOffset = new Vector3(0.5f, 0.5f, 0.5f);
                    if (te2 is TileEntityPoweredWorkstation wsChild2)
                        endOffset += wsChild2.WireOffset;
                    else if (te2 is TileEntityPowered tep2)
                        endOffset += tep2.WireOffset;

                    if (num >= this.currentWireNodes.Count)
                    {
                        IWireNode wireNodeFromPool = WireManager.Instance.GetWireNodeFromPool();
                        this.currentWireNodes.Add(wireNodeFromPool);
                    }

                    this.currentWireNodes[num].SetStartPosition(
                        base.ToWorldPos().ToVector3());
                    this.currentWireNodes[num].SetStartPositionOffset(
                        new Vector3(0.5f, 0.5f, 0.5f));
                    this.currentWireNodes[num].SetEndPosition(vector3i2.ToVector3());
                    this.currentWireNodes[num].SetEndPositionOffset(endOffset);
                    this.currentWireNodes[num].BuildMesh();
                    this.currentWireNodes[num].TogglePulse(showPulse);
                    this.currentWireNodes[num].SetVisible(wiresShowing);
                    num++;
                }
            }

            for (int k = num; k < this.currentWireNodes.Count; k++)
            {
                IWireNode wireNode = this.currentWireNodes[num];
                WireManager.Instance.ReturnToPool(wireNode);
                this.currentWireNodes.Remove(wireNode);
            }

            this.wiresDirty = false;
        }

        public void SetParentWithWireTool(IPowered newParentTE, int wiringEntityID)
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                PowerItem powerItem = newParentTE.GetPowerItem();
                if (NicElectricityRealism.DebugLog)
                {
                    UnityEngine.Debug.Log("ElectricityRealism SetParentWithWireTool:" +
                        " this.PowerItem=" + (this.PowerItem == null ? "NULL" : "OK") +
                        " newParent PowerItem=" + (powerItem == null ? "NULL" : "OK") +
                        " IsServer=" + SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer);
                }

                PowerItem oldParent = this.PowerItem.Parent;
                PowerManager.Instance.SetParent(this.PowerItem, powerItem);

                // Update parentPosition so HasParent() returns true correctly.
                if (this.PowerItem.Parent != null)
                {
                    this.parentPosition = this.PowerItem.Parent.Position;
                }

                if (NicElectricityRealism.DebugLog)
                {
                    UnityEngine.Debug.Log("ElectricityRealism after PowerManager.SetParent:" +
                        " this.PowerItem.Parent=" + (this.PowerItem.Parent == null ? "NULL" : "OK") +
                        " this.PowerItem.IsPowered=" + this.PowerItem.IsPowered);
                }
                if (oldParent != null && oldParent.TileEntity != null)
                {
                    oldParent.TileEntity.CreateWireDataFromPowerItem();
                    oldParent.TileEntity.SendWireData();
                    oldParent.TileEntity.RemoveWires();
                    oldParent.TileEntity.DrawWires();
                }
                newParentTE.CreateWireDataFromPowerItem();
                newParentTE.SendWireData();
                newParentTE.RemoveWires();
                newParentTE.DrawWires();
            }
            else
            {
                this.parentPosition = ((TileEntity)newParentTE).ToWorldPos();
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                    NetPackageManager.GetPackage<NetPackageWireActions>().Setup(
                        NetPackageWireActions.WireActions.SetParent,
                        base.ToWorldPos(),
                        new List<Vector3i> { this.parentPosition },
                        wiringEntityID), false);
            }
            this.setModified();
        }

        public void RemoveParentWithWiringTool(int wiringEntityID)
        {

            if (NicElectricityRealism.DebugLog)
            {
                Debug.Log("ElectricityRealism RemoveParentWithWiringTool called");
            }

            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                if (this.PowerItem.Parent != null)
                {
                    // Play the wire break sound.
                    Manager.BroadcastPlay(
                        this.ToWorldPos().ToVector3(),
                        this.IsPowered ? "wire_live_break" : "wire_dead_break",
                        0f);

                    PowerItem oldParent = this.PowerItem.Parent;
                    this.PowerItem.RemoveSelfFromParent();
                    if (oldParent.TileEntity != null)
                    {
                        oldParent.TileEntity.CreateWireDataFromPowerItem();
                        oldParent.TileEntity.SendWireData();
                        oldParent.TileEntity.RemoveWires();
                        oldParent.TileEntity.DrawWires();
                    }
                }
            }
            else
            {
                this.parentPosition = new Vector3i(-9999, -9999, -9999);
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                    NetPackageManager.GetPackage<NetPackageWireActions>().Setup(
                        NetPackageWireActions.WireActions.RemoveParent,
                        base.ToWorldPos(),
                        new List<Vector3i>(),
                        wiringEntityID), false);
            }
            this.setModified();
        }

        public void SetWireData(List<Vector3i> wireChildren)
        {
            this.wireDataList = wireChildren;
            this.RemoveWires();
            this.DrawWires();
        }

        // ── TileEntity overrides ─────────────────────────────────────────────────

        public override TileEntityType GetTileEntityType()
        {
            // Return Workstation so OpenTileEntityUi routes to workstationOpened.
            return TileEntityType.Workstation;
        }

        public override TileEntity Clone()
        {
            TileEntityPoweredWorkstation clone = new TileEntityPoweredWorkstation(this.chunk);
            clone.CopyFrom(this);
            return clone;
        }

        public override void OnUnload(World world)
        {
            base.OnUnload(world);
            this.RemoveWires();
        }

        // ── Save / Load ──────────────────────────────────────────────────────────
        public override void read(PooledBinaryReader _br, TileEntity.StreamModeRead _eStreamMode)
        {
            base.read(_br, _eStreamMode);
            // No extra data — wire connections restored via PowerManager in OnReadComplete.
        }

        public override void write(PooledBinaryWriter _bw, TileEntity.StreamModeWrite _eStreamMode)
        {
            base.write(_bw, _eStreamMode);
            // No extra data — wire connections saved by PowerManager separately.
        }

        // Updates the parent position after a wire connection is established.
        // Called from patches that cannot access the private parentPosition field directly.
        public void UpdateParentPosition(Vector3i position)
        {
            this.parentPosition = position;
        }

        public override void UpdateTick(World world)
        {
            if (NicElectricityRealism.DebugLog && !this.IsReceivingPower && this.wasReceivingPower)
                Debug.Log("ElectricityRealism UpdateTick: power just cut!" +
                    " IsCrafting=" + this.IsCrafting +
                    " isBurning=" + this.isBurning +
                    " hasRecipeInQueue=" + this.hasRecipeInQueue());

            if (this.IsReceivingPower)
            {
                if (!this.wasReceivingPower)
                {
                    this.wasReceivingPower = true;
                    if (this.wasBurningBeforePowerCut)
                    {
                        this.wasBurningBeforePowerCut = false;
                        this.isBurning = true;
                    }
                    this.visibleChanged = true;
                }
                base.UpdateTick(world);
                // Reconcile power draw every tick after base processes crafting state.
                // Cannot rely on UpdateVisible for this: vanilla only calls it on visual
                // state changes (e.g. forge fire going out). The chemistry station has no
                // such trigger, so UpdateVisible is never called when its queue empties.
                if (this.PowerItem != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                {
                    bool isWorking = this.isBurning || this.IsCrafting;
                    ushort targetPower = isWorking ? (ushort)this.RequiredPower : (ushort)1;
                    if (this.PowerItem.RequiredPower != targetPower)
                    {
                        this.PowerItem.RequiredPower = targetPower;
                        this.PowerItem.SendHasLocalChangesToRoot();
                    }
                }
            }
            else
            {
                this.wasReceivingPower = false;
                this.lastTickTime = GameTimer.Instance.ticks;
                if (this.isBurning)
                {
                    this.wasBurningBeforePowerCut = true;
                    this.isBurning = false;
                }
                this.setModified();
                this.visibleChanged = true;
                this.UpdateVisible();
            }

            // In UpdateTick, after the craft animation toggle:
            if (this.PowerItem != null && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                // Check if wireDataList is out of sync with PowerItem.Children.
                bool wiresDirtyFromChildren = false;
                if (this.wireDataList.Count != this.PowerItem.Children.Count)
                {
                    wiresDirtyFromChildren = true;
                }
                else
                {
                    for (int i = 0; i < this.wireDataList.Count; i++)
                    {
                        if (this.wireDataList[i] != this.PowerItem.Children[i].Position)
                        {
                            wiresDirtyFromChildren = true;
                            break;
                        }
                    }
                }

                if (wiresDirtyFromChildren)
                {
                    this.CreateWireDataFromPowerItem();
                    this.SendWireData();
                    this.RemoveWires();
                    this.wiresDirty = true; // will trigger DrawWires when transform is ready
                }
            }

            // If BlockTransform hasn't been set yet, try to get it now.
            if (this.blockTransform == null)
            {
                BlockEntityData blockEntity = this.chunk?.GetBlockEntity(base.ToWorldPos());
                if (blockEntity != null && blockEntity.transform != null)
                {
                    if (NicElectricityRealism.DebugLog)
                        Debug.Log("ElectricityRealism UpdateTick: setting BlockTransform");
                    this.BlockTransform = blockEntity.transform;
                }
            }

            // Draw wires if dirty and we have a transform.
            if (this.wiresDirty && this.blockTransform != null)
            {
                if (NicElectricityRealism.DebugLog)
                    Debug.Log("ElectricityRealism UpdateTick: DrawWires wireDataList.Count=" +
                        this.wireDataList.Count);
                this.DrawWires();
            }

            if (NicElectricityRealism.DebugLog && this.blockTransform != null && !this.hasLoggedTransformPosition)
            {
                this.hasLoggedTransformPosition = true;
                Debug.Log("ElectricityRealism blockTransform.position=" + this.blockTransform.position +
                    " ToWorldPos=" + base.ToWorldPos().ToVector3());
            }
        }
    }
}