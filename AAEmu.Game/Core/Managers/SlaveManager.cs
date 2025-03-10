﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Tasks.Slave;
using AAEmu.Game.Utils;
using AAEmu.Game.Utils.DB;

#pragma warning disable CA2000 // Dispose objects before losing scope

using NLog;

namespace AAEmu.Game.Core.Managers;

public class SlaveManager : Singleton<SlaveManager>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private Dictionary<uint, SlaveTemplate> _slaveTemplates;
    private Dictionary<uint, Slave> _activeSlaves;
    private List<Slave> _testSlaves;
    private Dictionary<uint, Slave> _tlSlaves;
    public Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>> _attachPoints;
    public Dictionary<uint, List<SlaveInitialItems>> _slaveInitialItems; // PackId and List<Slot/ItemData>
    public Dictionary<uint, SlaveMountSkills> _slaveMountSkills;
    public Dictionary<uint, uint> _repairableSlaves; // SlaveId, RepairEffectId

    private object _slaveListLock;

    public bool Exist(uint templateId)
    {
        return _slaveTemplates.ContainsKey(templateId);
    }

    public SlaveTemplate GetSlaveTemplate(uint id)
    {
        return _slaveTemplates.TryGetValue(id, out var template) ? template : null;
    }

    public Slave GetActiveSlaveByOwnerObjId(uint objId)
    {
        lock (_slaveListLock)
            return _activeSlaves.TryGetValue(objId, out var slave) ? slave : null;
    }

    /// <summary>
    /// Returns a list of all Slaves of specific SlaveKind
    /// </summary>
    /// <param name="kind"></param>
    /// <param name="worldId">When set, only return from specific world</param>
    /// <returns></returns>
    public IEnumerable<Slave> GetActiveSlavesByKind(SlaveKind kind, uint worldId = uint.MaxValue)
    {
        lock (_slaveListLock)
        {
            if (worldId >= uint.MaxValue)
                return _activeSlaves.Select(i => i.Value).Where(s => s.Template.SlaveKind == kind);

            return _activeSlaves.Select(i => i.Value).Where(s => (s.Template.SlaveKind == kind) && (s.Transform.WorldId == worldId));
        }
    }

    /// <summary>
    /// Returns a list of all Slaves of specific SlaveKind
    /// </summary>
    /// <param name="kinds"></param>
    /// <param name="worldId">When set, only return from specific world</param>
    /// <returns></returns>
    public IEnumerable<Slave> GetActiveSlavesByKinds(SlaveKind[] kinds, uint worldId = uint.MaxValue)
    {
        lock (_slaveListLock)
        {
            if (worldId >= uint.MaxValue)
                return _activeSlaves.Where(s => kinds.Contains(s.Value.Template.SlaveKind))
                    .Select(s => s.Value);

            return _activeSlaves.Where(s => kinds.Contains(s.Value.Template.SlaveKind))
                .Where(s => s.Value?.Transform.WorldId == worldId)
                .Select(s => s.Value);
        }
    }

    public Slave GetActiveSlaveByObjId(uint objId)
    {
        lock (_slaveListLock)
        {
            foreach (var slave in _activeSlaves.Values.Where(slave => slave.ObjId == objId))
            {
                return slave;
            }
        }
        return null;
    }
    public Slave GetTestSlaveByObjId(uint objId)
    {
        lock (_slaveListLock)
        {
            foreach (var slave in _testSlaves.Where(slave => slave.ObjId == objId))
            {
                return slave;
            }
        }
        return null;
    }
 
    public Slave GetSlaveByObjId(uint objId)
    {
        lock (_slaveListLock)
        {
            foreach (var slave in _activeSlaves.Values.Where(slave => slave.ObjId == objId))
            {
                return slave;
            }

            foreach (var slave in _testSlaves.Where(slave => slave.ObjId == objId))
            {
                return slave;
            }
        }
        return null;
    }

    /* Unused
    private Slave GetActiveSlaveByTlId(uint tlId)
    {
        lock (_slaveListLock)
        {
            foreach (var slave in _activeSlaves.Values)
            {
                if (slave.TlId == tlId) return slave;
            }
        }

        return null;
    }*/

    /// <summary>
    /// Get mount skill associated with slaveMountSkillId
    /// </summary>
    /// <param name="slaveMountSkillId"></param>
    /// <returns></returns>
    public uint GetSlaveMountSkillFromId(uint slaveMountSkillId)
    {
        return _slaveMountSkills.TryGetValue(slaveMountSkillId, out var res) ? res.MountSkillId : 0;
    }

    /// <summary>
    /// Gets a list of all mount skills for a given slave type
    /// </summary>
    /// <param name="slaveTemplateId"></param>
    /// <returns></returns>
    public List<uint> GetSlaveMountSkillList(uint slaveTemplateId)
    {
        var res = new List<uint>();
        foreach (var q in _slaveMountSkills.Values.Where(q => q.SlaveId == slaveTemplateId))
            res.Add(q.MountSkillId);
        return res;
    }

    public void UnbindSlave(Character character, uint tlId, AttachUnitReason reason)
    {
        Slave slave;
        lock (_slaveListLock)
            slave = _tlSlaves[tlId];
        var attachPoint = slave.AttachedCharacters.FirstOrDefault(x => x.Value == character).Key;
        if (attachPoint != default)
        {
            slave.AttachedCharacters.Remove(attachPoint);
            character.Transform.Parent = null;
            character.Transform.StickyParent = null;
        }

        character.Buffs.TriggerRemoveOn(BuffRemoveOn.Unmount);
        character.AttachedPoint = AttachPointKind.None;

        character.BroadcastPacket(new SCUnitDetachedPacket(character.ObjId, reason), true);
    }

    public void BindSlave(Character character, uint objId, AttachPointKind attachPoint, AttachUnitReason bondKind)
    {
        // Check if the target spot is already taken
        Slave slave;
        lock (_slaveListLock)
            slave = _tlSlaves.FirstOrDefault(x => x.Value.ObjId == objId).Value;
        //var slave = GetActiveSlaveByObjId(objId);
        if ((slave == null) || (slave.AttachedCharacters.ContainsKey(attachPoint)))
            return;

        character.BroadcastPacket(new SCUnitAttachedPacket(character.ObjId, attachPoint, bondKind, objId), true);
        character.AttachedPoint = attachPoint;
        switch (attachPoint)
        {
            case AttachPointKind.Driver:
                character.BroadcastPacket(new SCSlaveBoundPacket(character.Id, objId), true);
                break;
        }

        slave.AttachedCharacters.Add(attachPoint, character);
        character.Transform.Parent = slave.Transform;
        // TODO: move to attach point's position
        character.Transform.Local.SetPosition(0, 0, 0, 0, 0, 0);
    }

    public void BindSlave(GameConnection connection, uint tlId)
    {
        var unit = connection.ActiveChar;
        Slave slave;
        lock (_slaveListLock)
            slave = _tlSlaves[tlId];

        BindSlave(unit, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);
    }

    // TODO - GameConnection connection
    /// <summary>
    /// Removes a slave from the world
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="objId"></param>
    public void Delete(Character owner, uint objId)
    {
        var activeSlaveInfo = GetActiveSlaveByObjId(objId);
        var testSlaveInfo = GetTestSlaveByObjId(objId);
        // replace Slave with test ones from Mirage
        activeSlaveInfo ??= testSlaveInfo;
        if (activeSlaveInfo == null) return;
        activeSlaveInfo.Save();
        // Remove passengers
        foreach (var character in activeSlaveInfo.AttachedCharacters.Values.ToList())
            UnbindSlave(character, activeSlaveInfo.TlId, AttachUnitReason.SlaveBinding);

        // Check if one of the slave doodads is holding a item
        foreach (var doodad in activeSlaveInfo.AttachedDoodads)
        {
            if ((doodad.ItemId != 0) || (doodad.ItemTemplateId != 0))
            {
                owner?.SendErrorMessage(ErrorMessageType.SlaveEquipmentLoadedItem); // TODO: Do we need this error? Client already mentions it.
                return; // don't allow un-summon if some it's holding a item (should be a trade-pack)
            }
        }

        var despawnDelayedTime = DateTime.UtcNow.AddSeconds(activeSlaveInfo.Template.PortalTime - 0.5f);

        activeSlaveInfo.Transform.DetachAll();

        foreach (var doodad in activeSlaveInfo.AttachedDoodads)
        {
            // Note, we un-check the persistent flag here, or else the doodad will delete itself from DB as well
            // This is not desired for player owned slaves
            if (owner != null)
                doodad.IsPersistent = false;
            doodad.Despawn = despawnDelayedTime;
            SpawnManager.Instance.AddDespawn(doodad);
            // doodad.Delete();
        }

        foreach (var attachedSlave in activeSlaveInfo.AttachedSlaves)
        {
            lock (_slaveListLock)
                _tlSlaves.Remove(attachedSlave.TlId);
            attachedSlave.Despawn = despawnDelayedTime;
            SpawnManager.Instance.AddDespawn(attachedSlave);
            //attachedSlave.Delete();
        }

        var world = WorldManager.Instance.GetWorld(activeSlaveInfo.Transform.WorldId);
        world.Physics.RemoveShip(activeSlaveInfo);
        owner?.BroadcastPacket(new SCSlaveDespawnPacket(objId), true);
        owner?.BroadcastPacket(new SCSlaveRemovedPacket(owner.ObjId, activeSlaveInfo.TlId), true);
        lock (_slaveListLock)
        {
            if (testSlaveInfo == null)
                _activeSlaves.Remove(owner.ObjId); // remove only the ones that we spawn from items
            else
                _testSlaves.Remove(activeSlaveInfo); // remove only the ones that we spawn from Mirage.
        }

        activeSlaveInfo.Despawn = DateTime.UtcNow.AddSeconds(activeSlaveInfo.Template.PortalTime + 0.5f);
        SpawnManager.Instance.AddDespawn(activeSlaveInfo);
    }

    /// <summary>
    /// Slave created from spawn effect since this is a test vehicle from Mirage
    /// </summary>
    /// <param name="SubType">TemplateId</param>
    /// <param name="hideSpawnEffect"></param>
    /// <param name="positionOverride"></param>
    public Slave Create(uint SubType, bool hideSpawnEffect = false, Transform positionOverride = null)
    {
        var slave = Create(null, null, SubType, null, hideSpawnEffect, positionOverride);
        if (slave == null) return null;
        _testSlaves.Add(slave);

        return slave;
    }

    /// <summary>
    /// Slave created from spawn effect
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="skillData"></param>
    /// <param name="hideSpawnEffect"></param>
    /// <param name="positionOverride"></param>
    public void Create(Character owner, SkillItem skillData, bool hideSpawnEffect = false, Transform positionOverride = null)
    {
        var activeSlaveInfo = GetActiveSlaveByOwnerObjId(owner.ObjId);
        if (activeSlaveInfo != null)
        {
            activeSlaveInfo.Save();
            // TODO: If too far away, don't delete
            Delete(owner, activeSlaveInfo.ObjId);
            // return;
        }

        var item = owner.Inventory.GetItemById(skillData.ItemId);
        if (item == null) return;

        var itemTemplate = (SummonSlaveTemplate)ItemManager.Instance.GetTemplate(item.TemplateId);
        if (itemTemplate == null) return;

        Create(owner, null, itemTemplate.SlaveId, item, hideSpawnEffect, positionOverride); // TODO replace the underlying code with this call
    }

    // added "/slave spawn <templateId>" to be called from the script command
    /// <summary>
    /// Slave created by player or spawn effect, use either useSpawner or templateId
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="useSpawner"></param>
    /// <param name="templateId"></param>
    /// <param name="item"></param>
    /// <param name="hideSpawnEffect"></param>
    /// <param name="positionOverride"></param>
    /// <returns>Newly created Slave</returns>
    public Slave Create(Character owner, SlaveSpawner useSpawner, uint templateId, Item item = null, bool hideSpawnEffect = false, Transform positionOverride = null)
    {
        var slaveTemplate = GetSlaveTemplate(useSpawner?.UnitId ?? templateId);
        if (slaveTemplate == null) return null;

        var tlId = (ushort)TlIdManager.Instance.GetNextId();
        var objId = ObjectIdManager.Instance.GetNextId();

        using var spawnPos = positionOverride ?? new Transform(null);
        var spawnOffsetPos = new Vector3();

        var dbId = 0u;
        var slaveName = string.Empty;
        var slaveHp = 1;
        var slaveMp = 1;
        var isLoadedPlayerSlave = false;
        if ((owner?.Id > 0) && (item?.Id > 0))
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            // Sorting required to make make sure parenting doesn't produce invalid parents (normally)

            command.CommandText = "SELECT * FROM slaves  WHERE (owner = @playerId) AND (item_id = @itemId) LIMIT 1";
            command.Parameters.AddWithValue("playerId", owner.Id);
            command.Parameters.AddWithValue("itemId", item.Id);
            command.Prepare();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                dbId = reader.GetUInt32("id");
                // var slaveItemId = reader.GetUInt32("item_id");
                // var slaveOwnerId = reader.GetUInt32("owner");
                slaveName = reader.GetString("name");
                // var slaveCreatedAt = reader.GetDateTime("created_at");
                // var slaveUpdatedAt = reader.GetDateTime("updated_at");
                slaveHp = reader.GetInt32("hp");
                slaveMp = reader.GetInt32("mp");
                // Coords are saved, but not really used when summoning and are only required to show vehicle
                // location after a server restart (if it was still summoned)
                // var slaveX = reader.GetFloat("x");
                // var slaveY = reader.GetFloat("y");
                // var slaveZ = reader.GetFloat("z");
                isLoadedPlayerSlave = true;
                break;
            }
        }

        // TODO: Attach Slave's DbId to the Item Details
        // We currently fake the DbId using TlId instead

        if (spawnPos.Local.IsOrigin())
        {
            if (owner == null && useSpawner == null)
            {
                Logger.Warn($"Tried creating a slave without a defined position, either use a Owner, Spawner or PositionOverride");
                return null;
            }

            if (useSpawner != null)
            {
                spawnPos.ApplyWorldSpawnPosition(useSpawner.Position, WorldManager.DefaultInstanceId);
            }
            else
            {
                spawnPos.ApplyWorldTransformToLocalPosition(owner.Transform, owner.InstanceId);
            }

            // If no spawn position override has been provided, then handle normal spawning algorithm

            // owner.SendMessage("SlaveSpawnOffset: x:{0} y:{1}", slaveTemplate.SpawnXOffset, slaveTemplate.SpawnYOffset);
            if (owner != null)
            {
                spawnPos.Local.AddDistanceToFront(Math.Clamp(slaveTemplate.SpawnYOffset, 5f, 50f));
            }
            // INFO: Seems like X offset is defined as the size of the vehicle summoned, but visually it's nicer if we just ignore this 
            // spawnPos.Local.AddDistanceToRight(slaveTemplate.SpawnXOffset);
            if (slaveTemplate.IsABoat())
            {
                // If we're spawning a boat, put it at the water level regardless of our own height
                // TODO: if not at ocean level, get actual target location water body height (for example rivers)
                var world = WorldManager.Instance.GetWorld(spawnPos.WorldId);
                if (world == null)
                {
                    Logger.Fatal($"Unable to find world to spawn in {spawnPos.WorldId}");
                    return null;
                }

                var worldWaterLevel = world.Water.GetWaterSurface(spawnPos.World.Position);
                spawnPos.Local.SetHeight(worldWaterLevel);

                // temporary grab ship information so that we can use it to find a suitable spot in front to summon it
                var tempShipModel = ModelManager.Instance.GetShipModel(slaveTemplate.ModelId);
                var minDepth = tempShipModel.MassBoxSizeZ - tempShipModel.MassCenterZ + 1f;

                // Somehow take into account where the ship will end up related to it's mass center (also check boat physics)
                spawnOffsetPos.Z += (tempShipModel.MassCenterZ < 0f ? (tempShipModel.MassCenterZ / 2f) : 0f) -
                                    tempShipModel.KeelHeight;

                for (var inFront = 0f; inFront < (50f + tempShipModel.MassBoxSizeX); inFront += 1f)
                {
                    using var depthCheckPos = spawnPos.CloneDetached();
                    depthCheckPos.Local.AddDistanceToFront(inFront);
                    var floorHeight = WorldManager.Instance.GetHeight(depthCheckPos);
                    if (floorHeight > 0f)
                    {
                        var surfaceHeight = world.Water.GetWaterSurface(depthCheckPos.World.Position);
                        var delta = surfaceHeight - floorHeight;
                        if (delta > minDepth)
                        {
                            //owner.SendMessage("Extra inFront = {0}, required Depth = {1}", inFront, minDepth);
                            spawnPos.Dispose();

                            spawnPos.ApplyWorldTransformToLocalPosition(depthCheckPos);
                            break;
                        }
                    }
                }

                spawnPos.Local.Position += spawnOffsetPos;
            }
            else
            {
                // If a land vehicle, put it a the ground level of it's target spawn location
                // TODO: check for maximum height difference for summoning
                var h = WorldManager.Instance.GetHeight(spawnPos);
                if (h > 0f)
                    spawnPos.Local.SetHeight(h);
            }

            // Always spawn horizontal(level) and 90° CCW of the player
            spawnPos.Local.SetRotation(0f, 0f, owner?.Transform.World.Rotation.Z + MathF.PI / 2 ?? useSpawner.Position.Yaw);
        }

        if (item is SummonSlave slaveSummonItem)
        {
            slaveSummonItem.SlaveType = 0x02;
            slaveSummonItem.SlaveDbId = dbId;
            if ((slaveSummonItem.IsDestroyed > 0) || (slaveSummonItem.RepairStartTime > DateTime.MinValue))
            {
                var secondsLeft = (slaveSummonItem.RepairStartTime.AddMinutes(10) - DateTime.UtcNow).TotalSeconds;
                if (secondsLeft > 0.0)
                {
                    // Slave was destroyed and is on cooldown
                    owner?.SendErrorMessage(ErrorMessageType.SlaveSpawnErrorNeedRepairTime, (uint)Math.Round(secondsLeft));
                    return null;
                }
            }
            slaveSummonItem.SummonLocation = spawnPos.World.Position;
            slaveSummonItem.RepairStartTime = DateTime.MinValue; // reset timer here
            slaveSummonItem.IsDirty = true;
            owner?.SendPacket(new SCItemTaskSuccessPacket(ItemTaskType.UpdateSummonMateItem, new ItemUpdate(item), new List<ulong>()));
        }

        owner?.BroadcastPacket(new SCSlaveCreatedPacket(owner.ObjId, tlId, objId, hideSpawnEffect, 0, owner.Name), true);

        // Get new Id to save if it has a player as owner
        if ((owner?.Id > 0) && (dbId <= 0))
            dbId = CharacterIdManager.Instance.GetNextId(); // dbId = SlaveIdManager.Instance.GetNextId();

        var summonedSlave = new Slave
        {
            TlId = tlId,
            ObjId = objId,
            TemplateId = slaveTemplate.Id,
            Name = string.IsNullOrWhiteSpace(slaveName) ? slaveTemplate.Name : slaveName,
            Level = (byte)slaveTemplate.Level,
            ModelId = slaveTemplate.ModelId,
            Template = slaveTemplate,
            Hp = slaveHp,
            Mp = slaveMp,
            ModelParams = new UnitCustomModelParams(),
            Faction = owner?.Faction ?? FactionManager.Instance.GetFaction(slaveTemplate.FactionId),
            Id = dbId,
            Summoner = owner,
            SummoningItem = item,
            SpawnTime = DateTime.UtcNow,
            Spawner = useSpawner,
        };

        ApplySlaveBonuses(summonedSlave);

        if (!isLoadedPlayerSlave)
        {
            summonedSlave.Hp = summonedSlave.MaxHp;
            summonedSlave.Mp = summonedSlave.MaxMp;
        }

        if (_slaveInitialItems.TryGetValue(summonedSlave.Template.SlaveInitialItemPackId, out var itemPack))
        {
            foreach (var initialItem in itemPack)
            {
                // var newItem = new Item(WorldManager.DefaultWorldId,ItemManager.Instance.GetTemplate(initialItem.itemId),1);
                var newItem = ItemManager.Instance.Create(initialItem.itemId, 1, 0, false);
                summonedSlave.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, newItem, initialItem.equipSlotId);
            }
        }

        summonedSlave.Hp = Math.Min(summonedSlave.Hp, summonedSlave.MaxHp);
        summonedSlave.Mp = Math.Min(summonedSlave.Mp, summonedSlave.MaxMp);

        // Reset HP on "dead" vehicles
        if (summonedSlave.Hp <= 0)
            summonedSlave.Hp = summonedSlave.MaxHp;

        summonedSlave.Transform = spawnPos.CloneDetached(summonedSlave);
        summonedSlave.Spawn();

        spawnPos.Dispose();

        // If this was a previously saved slave, load doodads from DB and spawn them
        var doodadSpawnCount = SpawnManager.Instance.SpawnPersistentDoodads(DoodadOwnerType.Slave, (int)summonedSlave.Id, summonedSlave, true);
        Logger.Debug($"Loaded {doodadSpawnCount} doodads from DB for Slave {summonedSlave.ObjId} (Db: {summonedSlave.Id}");

        // Create all remaining doodads that where not previously loaded
        foreach (var doodadBinding in summonedSlave.Template.DoodadBindings)
        {
            // If this AttachPoint has already been spawned, skip it's creation
            if (summonedSlave.AttachedDoodads.Any(d => d.AttachPoint == doodadBinding.AttachPointId))
                continue;

            var doodad = new Doodad
            {
                ObjId = ObjectIdManager.Instance.GetNextId(),
                TemplateId = doodadBinding.DoodadId,
                OwnerObjId = owner?.ObjId ?? 0,
                ParentObjId = summonedSlave.ObjId,
                AttachPoint = doodadBinding.AttachPointId,
                OwnerId = owner?.Id ?? 0,
                PlantTime = summonedSlave.SpawnTime,
                OwnerType = DoodadOwnerType.Slave,
                OwnerDbId = summonedSlave.Id,
                Template = DoodadManager.Instance.GetTemplate(doodadBinding.DoodadId),
                Data = (byte)doodadBinding.AttachPointId, // copy of AttachPointId
                ParentObj = summonedSlave,
                Faction = summonedSlave.Faction,
                Type2 = 1u, // Flag: No idea why it's 1 for slave's doodads, seems to be 0 for everything else
                Spawner = null,
            };

            doodad.SetScale(doodadBinding.Scale);

            doodad.FuncGroupId = doodad.GetFuncGroupId();
            doodad.Transform = summonedSlave.Transform.CloneAttached(doodad);
            doodad.Transform.Parent = summonedSlave.Transform;

            // NOTE: In 1.2 we can't replace slave parts like sail, so just apply it to all of the doodads on spawn)
            // Should probably have a check somewhere if a doodad can have the UCC applied or not
            if (item != null && item.HasFlag(ItemFlag.HasUCC) && (item.UccId > 0))
                doodad.UccId = item.UccId;

            ApplyAttachPointLocation(summonedSlave, doodad, doodadBinding.AttachPointId);

            summonedSlave.AttachedDoodads.Add(doodad);
            doodad.Spawn();

            // Only set IsPersistent if the binding is defined as such
            if ((owner?.Id > 0) && (item?.Id > 0) && (doodadBinding.Persist))
            {
                doodad.IsPersistent = true;
                doodad.Save();
            }
        }

        foreach (var slaveBinding in summonedSlave.Template.SlaveBindings)
        {
            var childSlaveTemplate = GetSlaveTemplate(slaveBinding.SlaveId);
            var childTlId = (ushort)TlIdManager.Instance.GetNextId();
            var childObjId = ObjectIdManager.Instance.GetNextId();
            var childSlave = new Slave()
            {
                TlId = childTlId,
                ObjId = childObjId,
                ParentObj = summonedSlave,
                TemplateId = childSlaveTemplate.Id,
                Name = childSlaveTemplate.Name,
                Level = (byte)childSlaveTemplate.Level,
                ModelId = childSlaveTemplate.ModelId,
                Template = childSlaveTemplate,
                Hp = 1,
                Mp = 1,
                ModelParams = new UnitCustomModelParams(),
                Faction = owner?.Faction ?? summonedSlave.Faction,
                Id = 11, // TODO
                Summoner = owner,
                SpawnTime = DateTime.UtcNow,
                AttachPointId = (sbyte)slaveBinding.AttachPointId,
                OwnerObjId = summonedSlave.ObjId
            };

            ApplySlaveBonuses(childSlave);

            childSlave.Hp = childSlave.MaxHp;
            childSlave.Mp = childSlave.MaxMp;
            childSlave.Transform = summonedSlave.Transform.CloneDetached(childSlave);
            childSlave.Transform.Parent = summonedSlave.Transform;

            ApplyAttachPointLocation(summonedSlave, childSlave, slaveBinding.AttachPointId);

            summonedSlave.AttachedSlaves.Add(childSlave);
            lock (_slaveListLock)
                _tlSlaves.Add(childSlave.TlId, childSlave);
            childSlave.Spawn();
            childSlave.PostUpdateCurrentHp(childSlave, 0, childSlave.Hp, KillReason.Unknown);
        }

        lock (_slaveListLock)
        {
            _tlSlaves.Add(summonedSlave.TlId, summonedSlave);
            if (owner != null)
                _activeSlaves.TryAdd(owner.ObjId, summonedSlave);
        }

        if (summonedSlave.Template.IsABoat())
        {
            var world = WorldManager.Instance.GetWorld(owner.Transform.WorldId);
            world.Physics.AddShip(summonedSlave);
        }

        owner?.SendPacket(new SCMySlavePacket(summonedSlave.ObjId, summonedSlave.TlId, summonedSlave.Name,
            summonedSlave.TemplateId,
            summonedSlave.Hp, summonedSlave.MaxHp,
            summonedSlave.Transform.World.Position.X,
            summonedSlave.Transform.World.Position.Y,
            summonedSlave.Transform.World.Position.Z
        ));

        // Save to DB
        summonedSlave.Save();

        summonedSlave.PostUpdateCurrentHp(summonedSlave, 0, summonedSlave.Hp, KillReason.Unknown);
        UpdateSlaveRepairPoints(summonedSlave);

        return summonedSlave;
    }

    /// <summary>
    /// Use loaded attachPoint location and apply them depending on the slave and point
    /// </summary>
    /// <param name="slave">Owner</param>
    /// <param name="baseUnit">GameObject to apply to</param>
    /// <param name="attachPoint">Location to apply</param>
    private void ApplyAttachPointLocation(Slave slave, GameObject baseUnit, AttachPointKind attachPoint)
    {
        if (_attachPoints.ContainsKey(slave.ModelId))
        {
            if (_attachPoints[slave.ModelId].ContainsKey(attachPoint))
            {
                baseUnit.Transform = slave.Transform.CloneAttached(baseUnit);
                baseUnit.Transform.Parent = slave.Transform;
                baseUnit.Transform.Local.Translate(_attachPoints[slave.ModelId][attachPoint].AsPositionVector());
                baseUnit.Transform.Local.SetRotation(
                    _attachPoints[slave.ModelId][attachPoint].Roll,
                    _attachPoints[slave.ModelId][attachPoint].Pitch,
                    _attachPoints[slave.ModelId][attachPoint].Yaw);
                Logger.Debug($"Model id: {slave.ModelId} attachment {attachPoint} => pos {_attachPoints[slave.ModelId][attachPoint]} = {baseUnit.Transform}");
                return;
            }
            else
            {
                Logger.Warn($"Model id: {slave.ModelId} incomplete attach point information");
            }
        }
        else
        {
            Logger.Warn($"Model id: {slave.ModelId} has no attach point information");
        }
    }

    /// <summary>
    /// Applies buff ans bonuses to Slave
    /// </summary>
    /// <param name="summonedSlave"></param>
    private static void ApplySlaveBonuses(Slave summonedSlave)
    {
        // Add Passive buffs
        foreach (var buff in summonedSlave.Template.PassiveBuffs)
        {
            var passive = SkillManager.Instance.GetPassiveBuffTemplate(buff.PassiveBuffId);
            summonedSlave.Buffs.AddBuff(passive.BuffId, summonedSlave);
        }

        // Add Normal initial buffs
        foreach (var buff in summonedSlave.Template.InitialBuffs)
            summonedSlave.Buffs.AddBuff(buff.BuffId, summonedSlave);

        // Apply bonuses
        foreach (var bonusTemplate in summonedSlave.Template.Bonuses)
        {
            var bonus = new Bonus();
            bonus.Template = bonusTemplate;
            bonus.Value = bonusTemplate.Value; // TODO using LinearLevelBonus
            summonedSlave.AddBonus(0, bonus);
        }
    }

    public void LoadSlaveAttachmentPointLocations()
    {
        Logger.Info("Loading Slave Model Attach Points...");

        var filePath = Path.Combine(FileManager.AppPath, "Data", "slave_attach_points.json");
        var contents = FileManager.GetFileContents(filePath);
        if (string.IsNullOrWhiteSpace(contents))
            throw new IOException(
                $"File {filePath} doesn't exists or is empty.");

        if (JsonHelper.TryDeserializeObject(contents, out List<SlaveModelAttachPoint> attachPoints, out _))
            Logger.Info("Slave model attach points loaded...");
        else
            Logger.Warn("Slave model attach points not loaded...");

        // Convert degrees from json to radian
        foreach (var vehicle in attachPoints)
        {
            foreach (var pos in vehicle.AttachPoints)
            {
                pos.Value.Roll = pos.Value.Roll.DegToRad();
                pos.Value.Pitch = pos.Value.Pitch.DegToRad();
                pos.Value.Yaw = pos.Value.Yaw.DegToRad();
            }
        }

        _attachPoints = new Dictionary<uint, Dictionary<AttachPointKind, WorldSpawnPosition>>();
        foreach (var set in attachPoints)
        {
            _attachPoints[set.ModelId] = set.AttachPoints;
        }
    }

    public void Load()
    {
        _slaveListLock = new object();
        _slaveTemplates = new Dictionary<uint, SlaveTemplate>();
        lock (_slaveListLock)
        {
            _activeSlaves = new Dictionary<uint, Slave>();
            _testSlaves = new List<Slave>();
            _tlSlaves = new Dictionary<uint, Slave>();
        }
        _slaveInitialItems = new Dictionary<uint, List<SlaveInitialItems>>();
        _slaveMountSkills = new Dictionary<uint, SlaveMountSkills>();
        _repairableSlaves = new Dictionary<uint, uint>();

        #region SQLLite

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slaves";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveTemplate
                        {
                            Id = reader.GetUInt32("id"),
                            Name =
                                LocalizationManager.Instance.Get("slaves", "name", reader.GetUInt32("id"),
                                    reader.GetString("name")),
                            ModelId = reader.GetUInt32("model_id"),
                            Mountable = reader.GetBoolean("mountable"),
                            SpawnXOffset = reader.GetFloat("spawn_x_offset"),
                            SpawnYOffset = reader.GetFloat("spawn_y_offset"),
                            FactionId = reader.GetUInt32("faction_id", 0),
                            Level = reader.GetUInt32("level"),
                            Cost = reader.GetInt32("cost"),
                            SlaveKind = (SlaveKind)reader.GetUInt32("slave_kind_id"),
                            SpawnValidAreaRance = reader.GetUInt32("spawn_valid_area_range", 0),
                            SlaveInitialItemPackId = reader.GetUInt32("slave_initial_item_pack_id", 0),
                            SlaveCustomizingId = reader.GetUInt32("slave_customizing_id", 0),
                            Customizable = reader.GetBoolean("customizable", false),
                            PortalTime = reader.GetFloat("portal_time"),
                            Hp25DoodadCount = reader.GetInt32("hp25_doodad_count"),
                            Hp50DoodadCount = reader.GetInt32("hp50_doodad_count"),
                            Hp75DoodadCount = reader.GetInt32("hp75_doodad_count"),
                        };
                        _slaveTemplates.Add(template.Id, template);
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM unit_modifiers WHERE owner_type='Slave'";
                command.Prepare();
                using (var sqliteDataReader = command.ExecuteReader())
                using (var reader = new SQLiteWrapperReader(sqliteDataReader))
                {
                    while (reader.Read())
                    {
                        var slaveId = reader.GetUInt32("owner_id");
                        if (!_slaveTemplates.TryGetValue(slaveId, out var slaveTemplate))
                            continue;
                        var template = new BonusTemplate();
                        template.Attribute = (UnitAttribute)reader.GetByte("unit_attribute_id");
                        template.ModifierType = (UnitModifierType)reader.GetByte("unit_modifier_type_id");
                        template.Value = reader.GetInt32("value");
                        template.LinearLevelBonus = reader.GetInt32("linear_level_bonus");
                        slaveTemplate.Bonuses.Add(template);
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_initial_items";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var ItemPackId = reader.GetUInt32("slave_initial_item_pack_id");
                        var SlotId = reader.GetByte("equip_slot_id");
                        var item = reader.GetUInt32("item_id");

                        if (_slaveInitialItems.TryGetValue(ItemPackId, out var key))
                        {
                            key.Add(new SlaveInitialItems() { slaveInitialItemPackId = ItemPackId, equipSlotId = SlotId, itemId = item });
                        }
                        else
                        {
                            var newPack = new List<SlaveInitialItems>();
                            var newKey = new SlaveInitialItems
                            {
                                slaveInitialItemPackId = ItemPackId,
                                equipSlotId = SlotId,
                                itemId = item
                            };
                            newPack.Add(newKey);

                            _slaveInitialItems.Add(ItemPackId, newPack);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_initial_buffs";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveInitialBuffs
                        {
                            Id = reader.GetUInt32("id"),
                            SlaveId = reader.GetUInt32("slave_id"),
                            BuffId = reader.GetUInt32("buff_id")
                        };
                        if (_slaveTemplates.ContainsKey(template.SlaveId))
                        {
                            _slaveTemplates[template.SlaveId].InitialBuffs.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_passive_buffs";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlavePassiveBuffs
                        {
                            Id = reader.GetUInt32("id"),
                            OwnerId = reader.GetUInt32("owner_id"),
                            OwnerType = reader.GetString("owner_type"),
                            PassiveBuffId = reader.GetUInt32("passive_buff_id")
                        };
                        if (_slaveTemplates.ContainsKey(template.OwnerId))
                        {
                            _slaveTemplates[template.OwnerId].PassiveBuffs.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_doodad_bindings";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveDoodadBindings
                        {
                            Id = reader.GetUInt32("id"),
                            OwnerId = reader.GetUInt32("owner_id"),
                            OwnerType = reader.GetString("owner_type"),
                            AttachPointId = (AttachPointKind)reader.GetInt32("attach_point_id"),
                            DoodadId = reader.GetUInt32("doodad_id"),
                            Persist = reader.GetBoolean("persist", true),
                            Scale = reader.GetFloat("scale")
                        };
                        if (_slaveTemplates.ContainsKey(template.OwnerId))
                        {
                            _slaveTemplates[template.OwnerId].DoodadBindings.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_healing_point_doodads";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveDoodadBindings
                        {
                            Id = reader.GetUInt32("id"),
                            OwnerId = reader.GetUInt32("owner_id"),
                            OwnerType = reader.GetString("owner_type"),
                            AttachPointId = (AttachPointKind)reader.GetInt32("attach_point_id"),
                            DoodadId = reader.GetUInt32("doodad_id"),
                            Persist = false,
                            Scale = 1f
                        };
                        if (_slaveTemplates.ContainsKey(template.OwnerId))
                        {
                            _slaveTemplates[template.OwnerId].HealingPointDoodads.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_bindings";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveBindings()
                        {
                            Id = reader.GetUInt32("id"),
                            OwnerId = reader.GetUInt32("owner_id"),
                            OwnerType = reader.GetString("owner_type"),
                            AttachPointId = (AttachPointKind)reader.GetUInt32("attach_point_id"),
                            SlaveId = reader.GetUInt32("slave_id")
                        };

                        if (_slaveTemplates.ContainsKey(template.OwnerId))
                        {
                            _slaveTemplates[template.OwnerId].SlaveBindings.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_drop_doodads";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveDropDoodad()
                        {
                            Id = reader.GetUInt32("id"),
                            OwnerId = reader.GetUInt32("owner_id"),
                            OwnerType = reader.GetString("owner_type"),
                            DoodadId = reader.GetUInt32("doodad_id"),
                            Count = reader.GetUInt32("count"),
                            Radius = reader.GetFloat("radius"),
                            OnWater = reader.GetBoolean("on_water", true),
                        };

                        if (template.OwnerType != "Slave")
                        {
                            Logger.Warn($"Non slave-owned drops defined in slave_drop_doodads table");
                            continue;
                        }
                        if (_slaveTemplates.ContainsKey(template.OwnerId))
                        {
                            _slaveTemplates[template.OwnerId].SlaveDropDoodads.Add(template);
                        }
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM slave_mount_skills";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new SlaveMountSkills()
                        {
                            Id = reader.GetUInt32("id"),
                            SlaveId = reader.GetUInt32("slave_id"),
                            MountSkillId = reader.GetUInt32("mount_skill_id")
                        };

                        if (!_slaveMountSkills.TryAdd(template.Id, template))
                            Logger.Warn($"Duplicate entry for slave_mount_skills");
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM repairable_slaves";
                command.Prepare();

                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        if (!_repairableSlaves.TryAdd(reader.GetUInt32("slave_id"),
                                reader.GetUInt32("repair_slave_effect_id")))
                            Logger.Warn($"Duplicate entry for repairable_slaves");
                    }
                }
            }

        }
        #endregion

        LoadSlaveAttachmentPointLocations();
    }

    public static void Initialize()
    {
        var sendMySlaveTask = new SendMySlaveTask();
        TaskManager.Instance.Schedule(sendMySlaveTask, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void SendMySlavePacketToAllOwners()
    {
        Dictionary<uint, Slave> slaveList = null;
        lock (_slaveListLock)
            slaveList = _activeSlaves;

        foreach (var (ownerObjId, slave) in slaveList)
        {
            var owner = WorldManager.Instance.GetCharacterByObjId(ownerObjId);
            owner?.SendPacket(new SCMySlavePacket(slave.ObjId, slave.TlId, slave.Name, slave.TemplateId,
                slave.Hp, slave.MaxHp,
                slave.Transform.World.Position.X,
                slave.Transform.World.Position.Y,
                slave.Transform.World.Position.Z));
        }
    }

    public Slave GetIsMounted(uint objId, out AttachPointKind attachPoint)
    {
        attachPoint = AttachPointKind.None;
        lock (_slaveListLock)
        {
            foreach (var slave in _activeSlaves.Values)
                foreach (var unit in slave.AttachedCharacters)
                {
                    if (unit.Value.ObjId == objId)
                    {
                        attachPoint = unit.Key;
                        return slave;
                    }
                }
        }

        return null;
    }

    public void RemoveActiveSlave(Character character, ushort slaveTlId)
    {
        if (_tlSlaves.TryGetValue(slaveTlId, out var slave))
        {
            if (slave.Summoner?.ObjId != character.ObjId)
            {
                Logger.Warn($"Non-owner is trying to desummon a slave {character.Name} => {slave.Name} (ObjId: {slave.ObjId})");
                return;
            }
        }
        else
        {
            return;
        }

        Delete(character, slave.ObjId);
        // slave.Delete();
    }

    public void RidersEscape(Character player, SkillCastPositionTarget skillCastPositionTarget)
    {
        var mySlave = GetActiveSlaveByOwnerObjId(player.ObjId);
        if (mySlave == null)
        {
            Logger.Warn($"{player.Name} using Rider's Escape with no slave active!");
            return;
        }

        // NOTE: ObjId and TlId gets retained during Rider's Escape

        // Despawn effect
        mySlave.BroadcastPacket(new SCSlaveDespawnPacket(mySlave.ObjId), true);
        mySlave.BroadcastPacket(new SCSlaveRemovedPacket(mySlave.ObjId, mySlave.TlId), true);
        mySlave.SendPacket(new SCUnitsRemovedPacket(new[] { mySlave.ObjId }));

        // Move location
        mySlave.SetPosition(skillCastPositionTarget.PosX, skillCastPositionTarget.PosY, skillCastPositionTarget.PosZ, 0f, 0f, skillCastPositionTarget.PosRot);
        // Without this offset, it just doesn't feel right
        mySlave.Transform.Local.AddDistanceToFront(mySlave.Template.SpawnXOffset / 2f);
        mySlave.Transform.Local.AddDistanceToRight(mySlave.Template.SpawnYOffset / 2f);

        // Respawn effect
        mySlave.Hide(); // Hide is needed for it's internals
        mySlave.Spawn();
        //mySlave.SendPacket(new SCUnitStatePacket(mySlave));
        //mySlave.SendPacket(new SCUnitPointsPacket(mySlave.ObjId, mySlave.Hp, mySlave.Mp));
        //mySlave.SendPacket(new SCSlaveStatePacket(mySlave.ObjId, mySlave.TlId, mySlave.Summoner.Name, mySlave.Summoner.ObjId, mySlave.Id));
    }

    public void UpdateSlaveRepairPoints(Slave slave)
    {
        var hpPercent = slave.Hp * 100 / slave.MaxHp;

        var repairPoints = 0;
        if (hpPercent is < 100 and >= 75)
            repairPoints = slave.Template.Hp75DoodadCount;
        else if (hpPercent is < 75 and >= 50)
            repairPoints = slave.Template.Hp50DoodadCount;
        else if (hpPercent is < 50 and >= 25)
            repairPoints = slave.Template.Hp25DoodadCount;
        else if (hpPercent < 25)
            repairPoints = slave.Template.HealingPointDoodads.Count; // Use max points or Hp 25% ?

        // Get Current Count
        var currentHealPoints = new List<Doodad>();
        var unUsedHealPoints = new List<AttachPointKind>();
        foreach (var healBinding in slave.Template.HealingPointDoodads)
            unUsedHealPoints.Add(healBinding.AttachPointId);

        foreach (var doodad in slave.AttachedDoodads)
        {
            if ((doodad.AttachPoint < AttachPointKind.HealPoint0) || (doodad.AttachPoint > AttachPointKind.HealPoint9))
                continue;
            currentHealPoints.Add(doodad);
            unUsedHealPoints.Remove(doodad.AttachPoint);
        }

        var pointsToAdd = repairPoints - currentHealPoints.Count;
        if (pointsToAdd < 0)
        {
            // We have too many points, remove some
            for (var iRemove = pointsToAdd; iRemove < 0; iRemove++)
            {
                var i = Random.Shared.Next(currentHealPoints.Count);
                var doodad = currentHealPoints[i];
                if (doodad == null)
                    continue;

                doodad.Hide();
                doodad.Despawn = DateTime.UtcNow;
                SpawnManager.Instance.AddDespawn(doodad);
                slave.AttachedDoodads.Remove(doodad);
                currentHealPoints.Remove(doodad);
                unUsedHealPoints.Add(doodad.AttachPoint);
                doodad.Delete();
            }
        }

        if ((pointsToAdd > 0) && (unUsedHealPoints.Count > 0))
        {
            // We don't have enough points, add some
            for (var iAdd = 0; (iAdd < pointsToAdd) && (unUsedHealPoints.Count > 0); iAdd++)
            {
                // pick a random spot
                var wreckPointLocation = unUsedHealPoints[Random.Shared.Next(unUsedHealPoints.Count)];
                unUsedHealPoints.Remove(wreckPointLocation);
                var healBinding = slave.Template.HealingPointDoodads.FirstOrDefault(p => p.AttachPointId == wreckPointLocation);
                if (healBinding == null)
                {
                    Logger.Error($"Somehow failed to grab a healing point {wreckPointLocation} for {slave.TemplateId}");
                    return;
                }

                var wreckArea = new Doodad
                {
                    ObjId = ObjectIdManager.Instance.GetNextId(),
                    TemplateId = healBinding.DoodadId,
                    OwnerObjId = slave.OwnerObjId,
                    ParentObjId = slave.ObjId,
                    AttachPoint = wreckPointLocation,
                    OwnerId = slave.Summoner?.Id ?? 0,
                    PlantTime = DateTime.UtcNow,
                    OwnerType = DoodadOwnerType.Slave,
                    OwnerDbId = slave.Id,
                    Template = DoodadManager.Instance.GetTemplate(healBinding.DoodadId),
                    Data = (byte)wreckPointLocation, // copy of AttachPointId
                    ParentObj = slave,
                    Faction = slave.Faction, // FactionManager.Instance.GetFaction(FactionsEnum.Friendly),
                    Type2 = 1u, // Flag: No idea why it's 1 for slave's doodads, seems to be 0 for everything else
                    Spawner = null,
                    IsPersistent = false,
                };

                wreckArea.SetScale(1f);
                ApplyAttachPointLocation(slave, wreckArea, wreckPointLocation);

                wreckArea.FuncGroupId = wreckArea.GetFuncGroupId();

                slave.AttachedDoodads.Add(wreckArea);
                currentHealPoints.Add(wreckArea);
                wreckArea.Spawn();
            }
        }
    }

    public void RemoveAndDespawnAllActiveOwnedSlaves(Character owner)
    {
        var activeSlaveInfo = GetActiveSlaveByOwnerObjId(owner.ObjId);
        if (activeSlaveInfo != null)
        {
            activeSlaveInfo.Save();
            Delete(owner, activeSlaveInfo.ObjId);
        }
    }

    /// <summary>
    /// RemoveAndDespawnTestSlave - deleting Mirage's test transport
    /// </summary>
    /// <param name="ObjId"></param>
    /// <returns></returns>
    public void RemoveAndDespawnTestSlave(Character owner, uint slaveObjId)
    {
        Delete(owner, slaveObjId);
    }
}
