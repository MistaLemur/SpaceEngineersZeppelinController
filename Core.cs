using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using ModNetworkAPI;

namespace ZepController
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class Core : MySessionComponentBase
    {
        private NetworkAPI Network => NetworkAPI.Instance;

        private static Dictionary<long, ZeppelinController> Zeppelins = new Dictionary<long, ZeppelinController>();

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            NetworkAPI.Init(2662, "zep");

            if (Network.NetworkType == NetworkTypes.Client)
            {
                Network.RegisterChatCommand("sync", ClientCommand);
                Network.RegisterNetworkCommand(null, ClientCallback);
            }
            else
            {
                Network.RegisterNetworkCommand("setup", ServerCallback_Setup);
                Network.RegisterNetworkCommand("reset", ServerCallback_Reset);
                Network.RegisterNetworkCommand("change", ServerCallback_Change);
                Network.RegisterNetworkCommand("toggle_active", ServerCallback_ToggleActive);
                Network.RegisterNetworkCommand("sync", ServerCallback_Sync);
            }
        }

        protected override void UnloadData()
        {
            NetworkAPI.Dispose();
        }

        private void ServerCallback_Setup(ulong steamid, string command, byte[] data)
        {
            long blockId = MyAPIGateway.Utilities.SerializeFromBinary<long>(data);

            if (Zeppelins.ContainsKey(blockId))
            {
                Zeppelins[blockId].ZeppSetup();
            }
        }

        private void ServerCallback_Reset(ulong steamid, string command, byte[] data)
        {
            long blockId = MyAPIGateway.Utilities.SerializeFromBinary<long>(data);

            if (Zeppelins.ContainsKey(blockId))
            {
                Zeppelins[blockId].ResetTargetElevation();
            }
        }

        private void ServerCallback_Change(ulong steamid, string command, byte[] data)
        {
            AltitudeAdjust altitude = MyAPIGateway.Utilities.SerializeFromBinary<AltitudeAdjust>(data);

            if (altitude != null && Zeppelins.ContainsKey(altitude.BlockId))
            {
                Zeppelins[altitude.BlockId].ChangeTargetElevation(altitude.AdjustmentAmount);
            }
        }

        private void ServerCallback_ToggleActive(ulong steamid, string command, byte[] data)
        {
            long blockId = MyAPIGateway.Utilities.SerializeFromBinary<long>(data);

            if (Zeppelins.ContainsKey(blockId))
            {
                Zeppelins[blockId].ToggleActive();
            }
        }

        private void ServerCallback_Sync(ulong steamid, string command, byte[] data)
        {
            long blockId = MyAPIGateway.Utilities.SerializeFromBinary<long>(data);

            if (Zeppelins.ContainsKey(blockId))
            {
                Network.SendCommand(null, null, MyAPIGateway.Utilities.SerializeToBinary(Zeppelins[blockId].Data), steamid);
            }
        }

        private void ClientCallback(ulong steamid, string command, byte[] data)
        {
            ZeppelinData zdata = MyAPIGateway.Utilities.SerializeFromBinary<ZeppelinData>(data);

            if (zdata != null && Zeppelins.ContainsKey(zdata.BlockId))
            {
                Zeppelins[zdata.BlockId].UpdateZeppelinData(zdata);
            }
        }

        private void ClientCommand(string args)
        {
            Network.SendCommand("sync");
        }

        public static void RegisterZeppelin(ZeppelinController zep)
        {
            if (!Zeppelins.ContainsKey(zep.Entity.EntityId))
            {
                Zeppelins.Add(zep.Entity.EntityId, zep);
            }
        }

        public static void UnregisterZeppelin(ZeppelinController zep)
        {
            Zeppelins.Remove(zep.Entity.EntityId);
        }
    }
}
