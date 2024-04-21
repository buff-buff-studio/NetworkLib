﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NetBuff.Interface;
using NetBuff.Misc;
using NetBuff.Packets;
using UnityEngine;

namespace NetBuff.Components
{
    [RequireComponent(typeof(NetworkIdentity))]
    [Icon("Assets/Editor/Icons/NetworkBehaviour.png")]
    [HelpURL("https://buff-buff-studio.github.io/NetBuff-Lib-Docs/components/#network-behaviour")]
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        #region Internal Fields
        private NetworkIdentity _identity;
        private NetworkValue[] _values;
        private readonly Queue<byte> _dirtyValues = new();
        private bool _serializerDirty;
        #endregion

        #region Helper Properties
        public byte BehaviourId => (byte) Array.IndexOf(Identity.Behaviours, this);
        public bool IsDirty => NetworkManager.Instance.DirtyBehaviours.Contains(this);
        public ReadOnlySpan<NetworkValue> Values => new(_values);
        public NetworkIdentity Identity => _identity ??= GetComponent<NetworkIdentity>();
        
        public NetworkId Id
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.Id; }
        }

        public int OwnerId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.OwnerId; }
        }

        public NetworkId PrefabId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.PrefabId; }
        }

        public bool HasAuthority
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.HasAuthority; }
        }

        public bool IsOwnedByClient
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.IsOwnedByClient; }
        }

        public int SceneId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.SceneId; }
        }

        public bool IsServer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.IsServer; }
        }

        public int LoadedSceneCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.LoadedSceneCount; }
        }

        public string SourceScene
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.SourceScene; }
        }

        public string LastLoadedScene
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Identity.LastLoadedScene; }
        }

        #endregion
        
         #region Listeners
        [ServerOnly]
        public virtual void OnServerReceivePacket(IOwnedPacket packet, int clientId) { }
        
        [ClientOnly]
        public virtual void OnClientReceivePacket(IOwnedPacket packet) { }

        public virtual void OnSpawned(bool isRetroactive){}
        
        public virtual void OnSceneChanged(int fromScene, int toScene) {}
        
        [ServerOnly]
        public virtual void OnClientConnected(int clientId){}
        
        [ServerOnly]
        public virtual void OnClientDisconnected(int clientId){}
        
        public virtual void OnDespawned(){}
        
        public virtual void OnActiveChanged(bool active){}
        
        public virtual void OnOwnershipChanged(int oldOwner, int newOwner){}
        
        public virtual void OnSceneLoaded(int sceneId){}
        
        public virtual void OnSceneUnloaded(int sceneId){}
        
        public virtual void OnAnyObjectSpawned(NetworkIdentity identity, bool isRetroactive){}
        #endregion
        
        #region Value Methods

        public void WithValues(params NetworkValue[] values)
        {
            foreach (var value in values)
                value.AttachedTo = this;

            _values = values;
        }

        public void MarkValueDirty(NetworkValue value)
        {
            if (_values == null)
                return;
            var index = Array.IndexOf(_values, value);
            
            if (index == -1)
                throw new InvalidOperationException("The value is not attached to this behaviour");
            
            MarkValueDirty((byte) index);
        }


        public void MarkValueDirty(byte index)
        {
            _dirtyValues.Enqueue(index);

            if(IsDirty)
                return;   
            
            NetworkManager.Instance.DirtyBehaviours.Add(this);
        }
        
        public void MarkSerializerDirty()
        {
            if(IsDirty)
                return;
            
            if(this is not INetworkBehaviourSerializer)
                throw new InvalidOperationException("The behaviour does not implement INetworkBehaviourSerializer");

            _serializerDirty = true;
            NetworkManager.Instance.DirtyBehaviours.Add(this);
        }
        
        public void UpdateDirtyValues()
        {
            var writer = new BinaryWriter(new MemoryStream());
            writer.Write((byte) _dirtyValues.Count);
            while (_dirtyValues.Count > 0)
            {
                var index = _dirtyValues.Dequeue();
                writer.Write(index);
                _values[index].Serialize(writer);
            }
            
            if (_serializerDirty)
            {
                if(this is INetworkBehaviourSerializer nbs)
                    nbs.OnSerialize(writer, false);
                _serializerDirty = false;
            }
            
            var packet = new NetworkValuesPacket
            {
                Id = Id,
                BehaviourId = BehaviourId,
                Payload = ((MemoryStream) writer.BaseStream).ToArray()
            };

            if (NetworkManager.Instance.IsServerRunning)
            {
                if(NetworkManager.Instance.IsClientRunning)
                    ServerBroadcastPacketExceptFor(packet, NetworkManager.Instance.LocalClientIds[0], true);
                else
                    ServerBroadcastPacket(packet, true);
            }
            else
                ClientSendPacket(packet, true);
        }

        [ServerOnly]
        public void SendNetworkValuesToClient(int clientId)
        {
            if(!IsServer)
                throw new Exception("This method can only be called on the server");
            
            var packet = GetPreExistingValuesPacket();
            if(packet != null)
                ServerSendPacket(packet, clientId, true);
        }
        
        [ServerOnly]
        public NetworkValuesPacket GetPreExistingValuesPacket()
        {
            if(!IsServer)
                throw new Exception("This method can only be called on the server");
            
            if(_values == null || _values.Length == 0)
            {
                var writer = new BinaryWriter(new MemoryStream());
                writer.Write((byte) 0);
                
                if (this is INetworkBehaviourSerializer nbs)
                {
                    nbs.OnSerialize(writer, true);
                    
                    return new NetworkValuesPacket
                    {
                        Id = Id,
                        BehaviourId = BehaviourId,
                        Payload = ((MemoryStream) writer.BaseStream).ToArray()
                    };
                }
                return null;
            }
            else
            {
                var writer = new BinaryWriter(new MemoryStream());
                writer.Write((byte) _values.Length);
    
                for(var i = 0; i < _values.Length; i++)
                {
                    writer.Write((byte) i);
                    _values[i].Serialize(writer);
                }
                
                if (this is INetworkBehaviourSerializer nbs)
                {
                    nbs.OnSerialize(writer, true);
                }
                
                return new NetworkValuesPacket
                {
                    Id = Id,
                    BehaviourId = BehaviourId,
                    Payload = ((MemoryStream) writer.BaseStream).ToArray()
                };
            }
        }

        public void ApplyDirtyValues(byte[] payload)
        {
            var reader = new BinaryReader(new MemoryStream(payload));
            var count = reader.ReadByte();
            for (var i = 0; i < count; i++)
            {
                var index = reader.ReadByte();
                _values[index].Deserialize(reader);
            }
            
            if(reader.BaseStream.Position != reader.BaseStream.Length && this is INetworkBehaviourSerializer nbs )
            {
                nbs.OnDeserialize(reader);
            }
        }
        #endregion

        #region Packet Methods
        [ServerOnly]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ServerBroadcastPacket(IPacket packet, bool reliable = false) => Identity.ServerBroadcastPacket(packet, reliable);
        
        [ServerOnly]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ServerBroadcastPacketExceptFor(IPacket packet, int except, bool reliable = false) => Identity.ServerBroadcastPacketExceptFor(packet, except, reliable);
        
        [ServerOnly]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ServerSendPacket(IPacket packet, int clientId, bool reliable = false) => Identity.ServerSendPacket(packet, clientId, reliable);
        
        [ClientOnly]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClientSendPacket(IPacket packet, bool reliable = false) => Identity.ClientSendPacket(packet, reliable);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SendPacket(IPacket packet, bool reliable = false) => Identity.SendPacket(packet, reliable);
 
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PacketListener<T> GetPacketListener<T>() where T : IPacket => Identity.GetPacketListener<T>();
        #endregion

        #region Object Methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Despawn() => Identity.Despawn();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetActive(bool active) => Identity.SetActive(active);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOwner(int clientId) => Identity.SetOwner(clientId);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkIdentity GetNetworkObject(NetworkId objectId) => NetworkIdentity.GetNetworkObject(objectId);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IEnumerable<NetworkIdentity> GetNetworkObjects() => NetworkIdentity.GetNetworkObjects();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetNetworkObjectCount() => NetworkIdentity.GetNetworkObjectCount();
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IEnumerable<NetworkIdentity> GetNetworkObjectsOwnedBy(int clientId) => NetworkIdentity.GetNetworkObjectsOwnedBy(clientId);
        #endregion

        #region Client Methods
        [ClientOnly]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLocalClientIndex(int clientId) => Identity.GetLocalClientIndex(clientId);
        #endregion

        #region Prefabs
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetPrefabById(NetworkId prefab) => Identity.GetPrefabById(prefab);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NetworkId GetIdForPrefab(GameObject prefab) => Identity.GetIdForPrefab(prefab);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPrefabValid(NetworkId prefab) => Identity.IsPrefabValid(prefab);
        #endregion

        #region Scene Moving
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveToScene(int sceneId) => Identity.MoveToScene(sceneId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveToScene(string sceneName) => Identity.MoveToScene(sceneName);
        #endregion

        #region Scene Utils
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IEnumerable<string> GetLoadedScenes() => Identity.GetLoadedScenes();

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public int GetSceneId(string sceneName) => Identity.GetSceneId(sceneName);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string GetSceneName(int sceneId) => Identity.GetSceneName(sceneId);
        #endregion

        #region Spawning
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(GameObject prefab) => NetworkIdentity.Spawn(prefab);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(GameObject prefab, Vector3 position, Quaternion rotation, bool active) => NetworkIdentity.Spawn(prefab, position, rotation, active);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(GameObject prefab, Vector3 position, Quaternion rotation, int owner) => NetworkIdentity.Spawn(prefab, position, rotation, owner);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(GameObject prefab, Vector3 position, Quaternion rotation) => NetworkIdentity.Spawn(prefab, position, rotation);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale, bool active, int owner = -1, int scene = -1) => NetworkIdentity.Spawn(prefab, position, rotation, scale, active, owner, scene);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NetworkId Spawn(NetworkId prefabId, Vector3 position, Quaternion rotation, Vector3 scale, bool active, int owner = -1, int scene = -1) => NetworkIdentity.Spawn(prefabId, position, rotation, scale, active, owner, scene);
        #endregion
    }
}