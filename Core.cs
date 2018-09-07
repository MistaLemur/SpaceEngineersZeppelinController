using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Components;
using ZepController.Coms;

namespace ZepController
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class Core : MySessionComponentBase
    {
        private ushort ModId = 2662;

        private static ICommunicate coms = null;

        private static Dictionary<long, ZeppelinController> Zeppelins = new Dictionary<long, ZeppelinController>();

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            MyAPIGateway.Session.OnSessionReady += OnSessionReady;
        }

        private void OnSessionReady()
        {
            MyAPIGateway.Session.OnSessionReady -= OnSessionReady;
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                coms = new Server(ModId);
                coms.OnCommandRecived += HandleFromClient;
            }
            else
            {
                coms = new Client(ModId);
                coms.OnCommandRecived += HandleFromServer;
            }
        }

        protected override void UnloadData()
        {
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                coms.OnCommandRecived -= HandleFromClient;
            }
            else
            {
                coms.OnCommandRecived -= HandleFromServer;
            }
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

        public static void SendDataChanged(ZeppelinData data)
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
                long blockId = long.Parse(cmd.DataType);

                if (Zeppelins.ContainsKey(blockId))
                {
                    Zeppelins[blockId].ZeppSetup();
                }
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
            else
            {
                ZeppelinData data = MyAPIGateway.Utilities.SerializeFromXML<ZeppelinData>(cmd.XMLData);

                if (data != null && Zeppelins.ContainsKey(data.BlockId))
                {
                    Zeppelins[data.BlockId].Data = data;

                    coms.SendCommand(new Command() { XMLData = cmd.XMLData });
                }
            }
        }

        private void HandleFromServer(Command cmd)
        {
            ZeppelinData data = MyAPIGateway.Utilities.SerializeFromXML<ZeppelinData>(cmd.XMLData);

            if (data != null && Zeppelins.ContainsKey(data.BlockId))
            {
                Zeppelins[data.BlockId].ServerUpdate(data);
            }
        }
    }
}
