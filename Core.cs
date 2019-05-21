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
        public const ushort ModId = 2662;
        public const string ModName = "Zeppelin Controller";

        private NetworkAPI Network => NetworkAPI.Instance;

        //private static Dictionary<long, ZeppelinController> Zeppelins = new Dictionary<long, ZeppelinController>();



        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            if (!NetworkAPI.IsInitialized)
            {
                NetworkAPI.Init(ModId, ModName);
            }

            if (Network.NetworkType == NetworkTypes.Client)
            {
                Network.RegisterNetworkCommand("sync");
            }
            else
            {
                Network.RegisterNetworkCommand("setup", ServerCallback_Setup);
                Network.RegisterNetworkCommand("restart", ServerCallback_Restart);
                Network.RegisterNetworkCommand("change", ServerCallback_Change);
                Network.RegisterNetworkCommand("toggle_active", ServerCallback_ToggleActive);
                Network.RegisterNetworkCommand("sync", ServerCallback_Sync);
            }
        }

        private void ServerCallback_Setup(ulong steamId, string commandString, byte[] data)
        {

            ZeppelinDefinition info = MyAPIGateway.Utilities.SerializeFromBinary<ZeppelinDefinition>(data);

            


            long blockId = long.Parse(cmd.DataType);

            if (Zeppelins.ContainsKey(blockId))
            {
                Zeppelins[blockId].ZeppSetup();
            }
        }

        private void ServerCallback_Restart(ulong steamId, string commandString, byte[] data)
        {

        }
        private void ServerCallback_Change(ulong steamId, string commandString, byte[] data)
        {

        }
        private void ServerCallback_ToggleActive(ulong steamId, string commandString, byte[] data)
        {

        }
        private void ServerCallback_Sync(ulong steamId, string commandString, byte[] data)
        {

        }

        protected override void UnloadData()
        {
            if (NetworkAPI.IsInitialized)
            {
                Network.Close();
            }
        }

        //public static void RegisterZeppelin(ZeppelinController zep)
        //{
        //    if (!Zeppelins.ContainsKey(zep.Entity.EntityId))
        //    {
        //        Zeppelins.Add(zep.Entity.EntityId, zep);
        //    }
        //}

        //public static void UnregisterZeppelin(ZeppelinController zep)
        //{
        //    Zeppelins.Remove(zep.Entity.EntityId);
        //}

        public static void SendDataChanged(ZeppelinDefinition data)
        {
            if (coms == null) return;

            coms.SendCommand(new Command()
            {
                XMLData = MyAPIGateway.Utilities.SerializeToXML(data)
            });
        }

        public static void SendRequest(Command cmd)
        {
            if (coms == null || MyAPIGateway.Multiplayer.IsServer) return;

            coms.SendCommand(cmd);
        }

        private void HandleFromClient(Command cmd)
        {
            if (cmd.Arguments == "setup")
            {

            }
            else if (cmd.Arguments == "restart")
            {
                long blockId = long.Parse(cmd.DataType);
                if (Zeppelins.ContainsKey(blockId))
                {
                    Zeppelins[blockId].ResetTargetElevation();
                }
            }
            else if (cmd.Arguments == "change")
            {
                long blockId = long.Parse(cmd.DataType);
                float amount = float.Parse(cmd.XMLData);

                if (Zeppelins.ContainsKey(blockId))
                {
                    Zeppelins[blockId].ChangeTargetElevation(amount);
                }
            }
            else if (cmd.Arguments == "toggle_active")
            {
                long blockId = long.Parse(cmd.DataType);
                if (Zeppelins.ContainsKey(blockId))
                {
                    Zeppelins[blockId].ToggleActive();
                }
            }
            else if (cmd.Arguments == "sync")
            {
                long blockId = long.Parse(cmd.DataType);
                if (Zeppelins.ContainsKey(blockId))
                {
                    coms.SendCommand(new Command() { XMLData = MyAPIGateway.Utilities.SerializeToXML(Zeppelins[blockId].Data) }, cmd.SteamId);
                }
            }
            else
            {
                ZeppelinDefinition data = MyAPIGateway.Utilities.SerializeFromXML<ZeppelinDefinition>(cmd.XMLData);

                if (data != null && Zeppelins.ContainsKey(data.BlockId))
                {
                    Zeppelins[data.BlockId].Data = data;

                    coms.SendCommand(new Command() { XMLData = cmd.XMLData });
                }
            }
        }

        private void HandleFromServer(Command cmd)
        {
            ZeppelinDefinition data = MyAPIGateway.Utilities.SerializeFromXML<ZeppelinDefinition>(cmd.XMLData);

            if (data != null && Zeppelins.ContainsKey(data.BlockId))
            {
                Zeppelins[data.BlockId].UpdateZeppelinData(data);
            }
        }
    }
}
